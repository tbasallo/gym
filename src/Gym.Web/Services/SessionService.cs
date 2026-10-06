using Gym.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gym.Web.Services;

public sealed class SessionService(IDbContextFactory<ApplicationDbContext> dbFactory, LiveUpdates live, TimeProvider clock)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<List<WorkoutSession>> GetActiveAsync(string userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.VisibleSessions(userId).AsNoTracking()
            .Where(s => s.Status == SessionStatus.Active)
            .Include(s => s.Participants).ThenInclude(p => p.User)
            .OrderByDescending(s => s.StartedUtc)
            .ToListAsync();
    }

    public async Task<List<WorkoutSession>> GetRecentAsync(string userId, int count)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.VisibleSessions(userId).AsNoTracking()
            .Where(s => s.Status == SessionStatus.Completed)
            .Include(s => s.Participants).ThenInclude(p => p.User)
            .OrderByDescending(s => s.StartedUtc)
            .Take(count)
            .ToListAsync();
    }

    public async Task<WorkoutSession?> GetAsync(int sessionId, string userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var session = await db.VisibleSessions(userId).AsNoTracking()
            .Include(s => s.Participants).ThenInclude(p => p.User)
            .Include(s => s.Exercises).ThenInclude(e => e.Exercise).ThenInclude(e => e.Videos)
            .Include(s => s.Exercises).ThenInclude(e => e.Exercise).ThenInclude(e => e.Links)
            .Include(s => s.Sets)
            .AsSplitQuery()
            .SingleOrDefaultAsync(s => s.Id == sessionId);
        if (session is not null)
        {
            session.Exercises = session.Exercises.OrderBy(e => e.Order).ToList();
            session.Sets = session.Sets.OrderBy(s => s.SetNumber).ToList();
        }

        return session;
    }

    /// <summary>
    /// Starts the given day (or the schedule's next day). If the schedule already has an
    /// active session, that session is returned so other devices simply join it.
    /// </summary>
    public async Task<int> StartAsync(string userId, int scheduleId, int? dayId, IReadOnlyCollection<string> participantIds)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var schedule = await db.VisibleSchedules(userId)
            .Include(s => s.Days).ThenInclude(d => d.Exercises)
            .Include(s => s.Group).ThenInclude(g => g!.Members)
            .AsSplitQuery()
            .SingleOrDefaultAsync(s => s.Id == scheduleId)
            ?? throw new UnauthorizedAccessException("Schedule not found.");

        var active = await db.Sessions
            .Where(s => s.ScheduleId == scheduleId && s.Status == SessionStatus.Active)
            .Select(s => (int?)s.Id)
            .FirstOrDefaultAsync();
        if (active is not null)
        {
            return active.Value;
        }

        var day = (dayId is null ? ScheduleService.NextDay(schedule) : schedule.Days.SingleOrDefault(d => d.Id == dayId))
            ?? throw new InvalidOperationException("This schedule has no days yet.");

        var allowed = schedule.Group?.Members.Select(m => m.UserId).ToHashSet() ?? [userId];
        var people = participantIds.Where(allowed.Contains).Distinct().ToList();
        if (people.Count == 0)
        {
            people.Add(userId);
        }

        var session = new WorkoutSession
        {
            ScheduleId = schedule.Id,
            ScheduleDayId = day.Id,
            GroupId = schedule.GroupId,
            StartedByUserId = userId,
            Title = day.Name,
            Status = SessionStatus.Active,
            StartedUtc = Now,
            Participants = people.Select(p => new SessionParticipant { UserId = p }).ToList(),
            Exercises = day.Exercises.OrderBy(e => e.Order).Select((e, i) => new SessionExercise
            {
                ExerciseId = e.ExerciseId,
                Order = i + 1,
                TargetSets = e.TargetSets,
                TargetReps = e.TargetReps,
                RestSeconds = e.RestSeconds,
            }).ToList(),
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();
        live.NotifySessions();
        return session.Id;
    }

    /// <summary>Starts the set timer for a participant. Any other timed set they have running is closed first.</summary>
    public Task StartSetAsync(int sessionId, string actingUserId, int sessionExerciseId, string participantId, decimal weight) =>
        MutateAsync(sessionId, actingUserId, async (db, session) =>
        {
            var exercise = RequireParticipantExercise(session, sessionExerciseId, participantId);
            await CloseRunningSetsAsync(db, session.Id, participantId);
            var targets = await TargetsAsync(db, participantId, exercise);
            db.SetLogs.Add(new SetLog
            {
                SessionId = session.Id,
                SessionExerciseId = exercise.Id,
                ExerciseId = exercise.ExerciseId,
                UserId = participantId,
                SetNumber = NextSetNumber(db, session, exercise.Id, participantId),
                Weight = Math.Max(0, weight),
                TargetSets = targets.Sets,
                TargetReps = targets.Reps,
                StartedUtc = Now,
            });
        });

    /// <summary>Finishes a timed set with what was actually done.</summary>
    public Task CompleteSetAsync(int sessionId, string actingUserId, int setId, int reps, decimal weight) =>
        MutateAsync(sessionId, actingUserId, (db, session) =>
        {
            var set = session.Sets.SingleOrDefault(s => s.Id == setId) ?? throw new InvalidOperationException("Set not found.");
            set.Reps = Math.Max(0, reps);
            // A device that never saw the starting weight sends 0; keep the weight the set started with.
            if (weight > 0)
            {
                set.Weight = weight;
            }

            set.CompletedUtc ??= Now;
            return Task.CompletedTask;
        });

    /// <summary>Records a set without using the timer.</summary>
    public Task LogSetAsync(int sessionId, string actingUserId, int sessionExerciseId, string participantId, int reps, decimal weight) =>
        MutateAsync(sessionId, actingUserId, async (db, session) =>
        {
            var exercise = RequireParticipantExercise(session, sessionExerciseId, participantId);
            var targets = await TargetsAsync(db, participantId, exercise);
            db.SetLogs.Add(new SetLog
            {
                SessionId = session.Id,
                SessionExerciseId = exercise.Id,
                ExerciseId = exercise.ExerciseId,
                UserId = participantId,
                SetNumber = NextSetNumber(db, session, exercise.Id, participantId),
                Reps = Math.Max(0, reps),
                Weight = Math.Max(0, weight),
                TargetSets = targets.Sets,
                TargetReps = targets.Reps,
                CompletedUtc = Now,
            });
        });

    public Task UpdateSetAsync(int sessionId, string actingUserId, int setId, int reps, decimal weight) =>
        MutateAsync(sessionId, actingUserId, (_, session) =>
        {
            var set = session.Sets.SingleOrDefault(s => s.Id == setId) ?? throw new InvalidOperationException("Set not found.");
            set.Reps = Math.Max(0, reps);
            set.Weight = Math.Max(0, weight);
            return Task.CompletedTask;
        });

    public Task DeleteSetAsync(int sessionId, string actingUserId, int setId) =>
        MutateAsync(sessionId, actingUserId, (db, session) =>
        {
            var set = session.Sets.SingleOrDefault(s => s.Id == setId);
            if (set is null)
            {
                return Task.CompletedTask;
            }

            db.SetLogs.Remove(set);
            var number = 1;
            foreach (var other in session.Sets
                         .Where(s => s.Id != setId && s.UserId == set.UserId && s.SessionExerciseId == set.SessionExerciseId)
                         .OrderBy(s => s.SetNumber))
            {
                other.SetNumber = number++;
            }

            return Task.CompletedTask;
        });

    public Task AddParticipantAsync(int sessionId, string actingUserId, string participantId) =>
        MutateAsync(sessionId, actingUserId, async (db, session) =>
        {
            if (session.Participants.Any(p => p.UserId == participantId))
            {
                return;
            }

            var allowed = session.GroupId is null
                ? participantId == session.StartedByUserId
                : await db.GroupMembers.AnyAsync(m => m.GroupId == session.GroupId && m.UserId == participantId);
            if (!allowed)
            {
                throw new UnauthorizedAccessException("That person is not in this group.");
            }

            session.Participants.Add(new SessionParticipant { UserId = participantId });
        });

    public Task RemoveParticipantAsync(int sessionId, string actingUserId, string participantId) =>
        MutateAsync(sessionId, actingUserId, (_, session) =>
        {
            if (session.Sets.Any(s => s.UserId == participantId))
            {
                throw new InvalidOperationException("They already logged sets in this session.");
            }

            session.Participants.RemoveAll(p => p.UserId == participantId);
            return Task.CompletedTask;
        });

    /// <summary>
    /// Adds an exercise to a running session. With <paramref name="supersetWithId"/> it joins that
    /// exercise's superset (placed right after it) and copies its targets.
    /// </summary>
    public Task AddExerciseAsync(int sessionId, string actingUserId, int exerciseId, int sets, int reps, int? supersetWithId = null) =>
        MutateAsync(sessionId, actingUserId, (_, session) =>
        {
            var added = new SessionExercise
            {
                ExerciseId = exerciseId,
                Order = session.Exercises.Count == 0 ? 1 : session.Exercises.Max(e => e.Order) + 1,
                TargetSets = Math.Max(1, sets),
                TargetReps = Math.Max(1, reps),
            };

            if (supersetWithId is { } partnerId)
            {
                var partner = session.Exercises.SingleOrDefault(e => e.Id == partnerId)
                    ?? throw new InvalidOperationException("Exercise not found.");
                added.TargetSets = partner.TargetSets;
                added.TargetReps = partner.TargetReps;
                added.RestSeconds = partner.RestSeconds;
                partner.SupersetGroup ??= NextSupersetGroup(session);
                added.SupersetGroup = partner.SupersetGroup;
                PlaceAfterGroup(session, added, partner.SupersetGroup.Value);
            }

            session.Exercises.Add(added);
            Renumber(session);
            return Task.CompletedTask;
        });

    /// <summary>
    /// Pairs two exercises already in the session into a superset (merging any supersets they are in).
    /// The first exercise stays where it is and the partner moves right after it.
    /// </summary>
    public Task CreateSupersetAsync(int sessionId, string actingUserId, int sessionExerciseId, int partnerSessionExerciseId) =>
        MutateAsync(sessionId, actingUserId, (_, session) =>
        {
            var first = session.Exercises.SingleOrDefault(e => e.Id == sessionExerciseId);
            var partner = session.Exercises.SingleOrDefault(e => e.Id == partnerSessionExerciseId);
            if (first is null || partner is null || first.Id == partner.Id)
            {
                throw new InvalidOperationException("Pick another exercise from this session.");
            }

            var firstBlock = Block(session, first);
            var partnerBlock = Block(session, partner).Where(e => !firstBlock.Contains(e)).ToList();
            var group = first.SupersetGroup ?? partner.SupersetGroup ?? NextSupersetGroup(session);
            foreach (var member in firstBlock.Concat(partnerBlock))
            {
                member.SupersetGroup = group;
            }

            var others = session.Exercises.Except(firstBlock).Except(partnerBlock).OrderBy(e => e.Order).ToList();
            var insertAt = others.Count(e => e.Order < firstBlock.Min(m => m.Order));
            var ordered = others.Take(insertAt).Concat(firstBlock).Concat(partnerBlock).Concat(others.Skip(insertAt)).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                ordered[i].Order = i + 1;
            }

            return Task.CompletedTask;
        });

    /// <summary>The exercise plus its superset partners, in order.</summary>
    private static List<SessionExercise> Block(WorkoutSession session, SessionExercise exercise) =>
        exercise.SupersetGroup is { } group
            ? session.Exercises.Where(e => e.SupersetGroup == group).OrderBy(e => e.Order).ToList()
            : [exercise];

    /// <summary>Takes an exercise out of its superset; a superset left with one exercise is dissolved.</summary>
    public Task LeaveSupersetAsync(int sessionId, string actingUserId, int sessionExerciseId) =>
        MutateAsync(sessionId, actingUserId, (_, session) =>
        {
            var exercise = session.Exercises.SingleOrDefault(e => e.Id == sessionExerciseId);
            if (exercise?.SupersetGroup is not { } group)
            {
                return Task.CompletedTask;
            }

            exercise.SupersetGroup = null;
            var rest = session.Exercises.Where(e => e.SupersetGroup == group).ToList();
            if (rest.Count == 1)
            {
                rest[0].SupersetGroup = null;
            }

            return Task.CompletedTask;
        });

    private static int NextSupersetGroup(WorkoutSession session) =>
        (session.Exercises.Max(e => e.SupersetGroup) ?? 0) + 1;

    /// <summary>Gives <paramref name="added"/> an order just after the last member of the superset.</summary>
    private static void PlaceAfterGroup(WorkoutSession session, SessionExercise added, int group)
    {
        var last = session.Exercises.Where(e => e.SupersetGroup == group).Max(e => e.Order);
        foreach (var later in session.Exercises.Where(e => e.Order > last))
        {
            later.Order++;
        }

        added.Order = last + 1;
    }

    private static void Renumber(WorkoutSession session)
    {
        var i = 1;
        foreach (var e in session.Exercises.OrderBy(e => e.Order))
        {
            e.Order = i++;
        }
    }

    /// <summary>
    /// Closes the session: stops running timers, records working weights and moves the
    /// schedule to the next day.
    /// </summary>
    public async Task FinishAsync(int sessionId, string actingUserId)
    {
        await MutateAsync(sessionId, actingUserId, async (db, session) =>
        {
            foreach (var participant in session.Participants)
            {
                await CloseRunningSetsAsync(db, session.Id, participant.UserId);
            }

            session.Status = SessionStatus.Completed;
            session.EndedUtc = Now;

            var completed = session.Sets.Where(s => s.CompletedUtc is not null && s.Reps > 0 && db.Entry(s).State != EntityState.Deleted);
            foreach (var group in completed.GroupBy(s => (s.UserId, s.ExerciseId)))
            {
                var setting = await db.UserExerciseSettings.FindAsync(group.Key.UserId, group.Key.ExerciseId);
                if (setting is null)
                {
                    setting = new UserExerciseSetting { UserId = group.Key.UserId, ExerciseId = group.Key.ExerciseId };
                    db.UserExerciseSettings.Add(setting);
                }

                setting.WorkingWeight = group.Max(s => s.Weight);
            }

            if (session.ScheduleId is { } scheduleId && session.ScheduleDayId is { } dayId)
            {
                var schedule = await db.Schedules.Include(s => s.Days).SingleOrDefaultAsync(s => s.Id == scheduleId);
                if (schedule is not null)
                {
                    schedule.NextDayId = ScheduleService.DayAfter(schedule, dayId);
                }
            }
        }, requireActive: true);
        live.NotifySessions();
    }

    /// <summary>Abandons the session without advancing the schedule. Empty sessions are deleted.</summary>
    public async Task CancelAsync(int sessionId, string actingUserId)
    {
        await MutateAsync(sessionId, actingUserId, (db, session) =>
        {
            if (session.Sets.Count == 0)
            {
                db.Sessions.Remove(session);
            }
            else
            {
                session.Status = SessionStatus.Cancelled;
                session.EndedUtc = Now;
            }

            return Task.CompletedTask;
        }, requireActive: true);
        live.NotifySessions();
    }

    private async Task MutateAsync(int sessionId, string actingUserId, Func<ApplicationDbContext, WorkoutSession, Task> mutate, bool requireActive = false)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var session = await db.VisibleSessions(actingUserId)
            .Include(s => s.Participants)
            .Include(s => s.Exercises)
            .Include(s => s.Sets)
            .AsSplitQuery()
            .SingleOrDefaultAsync(s => s.Id == sessionId)
            ?? throw new UnauthorizedAccessException("Session not found.");
        if (requireActive && session.Status != SessionStatus.Active)
        {
            throw new InvalidOperationException("This session is already closed.");
        }

        await mutate(db, session);
        await db.SaveChangesAsync();
        live.NotifySession(sessionId);
    }

    private static SessionExercise RequireParticipantExercise(WorkoutSession session, int sessionExerciseId, string participantId)
    {
        if (session.Participants.All(p => p.UserId != participantId))
        {
            throw new InvalidOperationException("That person is not in this session.");
        }

        return session.Exercises.SingleOrDefault(e => e.Id == sessionExerciseId)
            ?? throw new InvalidOperationException("Exercise not found.");
    }

    private static int NextSetNumber(ApplicationDbContext db, WorkoutSession session, int sessionExerciseId, string participantId) =>
        session.Sets.Count(s => s.SessionExerciseId == sessionExerciseId && s.UserId == participantId
            && db.Entry(s).State != EntityState.Deleted) + 1;

    private static async Task<(int Sets, int Reps)> TargetsAsync(ApplicationDbContext db, string userId, SessionExercise exercise)
    {
        var setting = await db.UserExerciseSettings.AsNoTracking()
            .SingleOrDefaultAsync(s => s.UserId == userId && s.ExerciseId == exercise.ExerciseId);
        return (setting?.TargetSets ?? exercise.TargetSets, setting?.TargetReps ?? exercise.TargetReps);
    }

    /// <summary>Completes a participant's running timed sets; ones with no reps entered are dropped.</summary>
    private async Task CloseRunningSetsAsync(ApplicationDbContext db, int sessionId, string participantId)
    {
        var running = await db.SetLogs
            .Where(s => s.SessionId == sessionId && s.UserId == participantId && s.CompletedUtc == null)
            .ToListAsync();
        foreach (var set in running)
        {
            if (set.Reps > 0)
            {
                set.CompletedUtc = Now;
            }
            else
            {
                db.SetLogs.Remove(set);
            }
        }
    }
}
