using Gym.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gym.Web.Services;

/// <summary>
/// Generates realistic demo history for a group so the charts have something to show, and
/// removes it again. Sample sessions are flagged <see cref="WorkoutSession.IsSample"/>; real
/// workouts, schedules and working weights are never touched.
/// </summary>
public sealed class SampleDataService(IDbContextFactory<ApplicationDbContext> dbFactory, ScheduleService schedules, TimeProvider clock)
{
    /// <summary>Training days per week, as offsets from Monday (Mon, Tue, Thu, Sat).</summary>
    private static readonly int[] TrainingDays = [0, 1, 3, 5];

    public async Task<int> CountAsync(int groupId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Sessions.CountAsync(s => s.GroupId == groupId && s.IsSample);
    }

    /// <summary>Adds <paramref name="weeks"/> weeks of completed group sessions ending yesterday. Returns the number of sessions created.</summary>
    public async Task<int> GenerateAsync(int groupId, string actingUserId, int weeks = 4)
    {
        weeks = Math.Clamp(weeks, 1, 26);
        await using var db = await dbFactory.CreateDbContextAsync();
        var group = await db.Groups.AsNoTracking()
            .Include(g => g.Members).ThenInclude(m => m.User)
            .SingleOrDefaultAsync(g => g.Id == groupId && g.Members.Any(m => m.UserId == actingUserId))
            ?? throw new UnauthorizedAccessException("Not a member of this group.");
        if (await db.Sessions.AnyAsync(s => s.GroupId == groupId && s.IsSample))
        {
            throw new InvalidOperationException("Sample workouts already exist. Remove them first.");
        }

        var schedule = await GroupScheduleAsync(db, groupId, actingUserId);
        var days = schedule.Days.Where(d => d.Exercises.Count > 0).OrderBy(d => d.Order).ToList();
        if (days.Count == 0)
        {
            throw new InvalidOperationException("The group's schedule has no exercises yet.");
        }

        var members = GroupService.Ordered(group.Members.Select(m => m.User)).ToList();
        var settings = await db.ProgressionSettings.AsNoTracking()
            .Where(s => members.Select(m => m.Id).Contains(s.UserId))
            .ToDictionaryAsync(s => s.UserId);
        var random = new Random(groupId * 7919 + 17);
        var states = new Dictionary<(string UserId, int ExerciseId), LiftState>();

        var today = clock.GetUtcNow().UtcDateTime.Date;
        var dates = Enumerable.Range(1, 7 * weeks)
            .Select(back => today.AddDays(-back))
            .Where(d => TrainingDays.Contains(((int)d.DayOfWeek + 6) % 7))
            .Order()
            .ToList();

        var dayIndex = 0;
        foreach (var date in dates)
        {
            var day = days[dayIndex++ % days.Count];
            var start = date.AddHours(22).AddMinutes(random.Next(0, 40)); // early evening in the Americas
            var session = new WorkoutSession
            {
                ScheduleId = schedule.Id,
                ScheduleDayId = day.Id,
                GroupId = groupId,
                StartedByUserId = actingUserId,
                Title = day.Name,
                Status = SessionStatus.Completed,
                IsSample = true,
                StartedUtc = start,
                Participants = members.Select(m => new SessionParticipant { UserId = m.Id }).ToList(),
            };

            var clockTime = start.AddMinutes(4);
            foreach (var planned in day.Exercises.OrderBy(e => e.Order))
            {
                var exercise = planned.Exercise;
                var sessionExercise = new SessionExercise
                {
                    ExerciseId = exercise.Id,
                    Order = planned.Order,
                    TargetSets = planned.TargetSets,
                    TargetReps = planned.TargetReps,
                    RestSeconds = planned.RestSeconds,
                };
                session.Exercises.Add(sessionExercise);

                for (var p = 0; p < members.Count; p++)
                {
                    var member = members[p];
                    var memberSettings = settings.GetValueOrDefault(member.Id) ?? new ProgressionSettings { UserId = member.Id };
                    var key = (member.Id, exercise.Id);
                    if (!states.TryGetValue(key, out var state))
                    {
                        state = new LiftState(StartingWeight(exercise, member, memberSettings, random));
                        states[key] = state;
                    }

                    var reps = state.Perform(planned.TargetSets, planned.TargetReps, random);
                    var setClock = clockTime.AddSeconds(p * 20);
                    for (var n = 0; n < reps.Count; n++)
                    {
                        var duration = TimeSpan.FromSeconds(25 + reps[n] * 3 + random.Next(0, 10));
                        session.Sets.Add(new SetLog
                        {
                            SessionExercise = sessionExercise,
                            ExerciseId = exercise.Id,
                            UserId = member.Id,
                            SetNumber = n + 1,
                            Reps = reps[n],
                            Weight = state.Weight,
                            TargetSets = planned.TargetSets,
                            TargetReps = planned.TargetReps,
                            StartedUtc = setClock,
                            CompletedUtc = setClock + duration,
                        });
                        setClock += duration + TimeSpan.FromSeconds(75 + random.Next(0, 45));
                    }

                    state.Advance(reps, planned.TargetSets, planned.TargetReps, Increment(exercise, memberSettings), memberSettings);
                }

                clockTime = clockTime.AddMinutes(planned.TargetSets * 2.5 + 2);
            }

            session.EndedUtc = clockTime;
            db.Sessions.Add(session);
        }

        await db.SaveChangesAsync();
        return dates.Count;
    }

    /// <summary>Deletes every sample session for the group. Returns how many were removed.</summary>
    public async Task<int> RemoveAsync(int groupId, string actingUserId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (!await db.GroupMembers.AnyAsync(m => m.GroupId == groupId && m.UserId == actingUserId))
        {
            throw new UnauthorizedAccessException("Not a member of this group.");
        }

        var sample = db.Sessions.Where(s => s.GroupId == groupId && s.IsSample);
        await db.SetLogs.Where(l => sample.Any(s => s.Id == l.SessionId)).ExecuteDeleteAsync();
        await db.SessionParticipants.Where(p => sample.Any(s => s.Id == p.SessionId)).ExecuteDeleteAsync();
        await db.SessionExercises.Where(e => sample.Any(s => s.Id == e.SessionId)).ExecuteDeleteAsync();
        return await sample.ExecuteDeleteAsync();
    }

    private async Task<Schedule> GroupScheduleAsync(ApplicationDbContext db, int groupId, string actingUserId)
    {
        var existing = await db.Schedules.AsNoTracking()
            .Where(s => s.GroupId == groupId && !s.IsArchived && s.Days.Any(d => d.Exercises.Any()))
            .Include(s => s.Days).ThenInclude(d => d.Exercises).ThenInclude(e => e.Exercise)
            .AsSplitQuery()
            .OrderBy(s => s.Id)
            .FirstOrDefaultAsync();
        if (existing is not null)
        {
            return existing;
        }

        var created = await schedules.CreateStarterAsync(actingUserId, groupId);
        return await db.Schedules.AsNoTracking()
            .Include(s => s.Days).ThenInclude(d => d.Exercises).ThenInclude(e => e.Exercise)
            .AsSplitQuery()
            .SingleAsync(s => s.Id == created.Id);
    }

    private static decimal Increment(Exercise exercise, ProgressionSettings settings) =>
        Progression.IncrementFor(exercise, settings);

    /// <summary>A plausible starting weight for this lift and person (people differ in strength).</summary>
    private static decimal StartingWeight(Exercise exercise, ApplicationUser person, ProgressionSettings settings, Random random)
    {
        var baseWeight = exercise.Name switch
        {
            var n when n.Contains("Squat") && n.Contains("Barbell") => 115m,
            var n when n.Contains("Deadlift") => 135m,
            var n when n.Contains("Leg Press") => 160m,
            var n when n.Contains("Bench") || n.Contains("Row") && n.Contains("Barbell") => 95m,
            var n when n.Contains("Overhead Press") => 65m,
            var n when n.Contains("Pulldown") || n.Contains("Cable Row") => 90m,
            var n when n.Contains("Calf") || n.Contains("Hip Thrust") => 100m,
            var n when n.Contains("Lateral") || n.Contains("Fly") || n.Contains("Curl") || n.Contains("Extension") && !n.Contains("Leg") => 20m,
            _ => exercise.BodyRegion switch
            {
                BodyRegion.Lower or BodyRegion.FullBody => 90m,
                BodyRegion.Core => 40m,
                _ => 35m,
            },
        };

        // Each person gets their own made-up strength level (0.65x to 1.25x), plus a little noise per lift.
        var strength = 0.65m + (decimal)new Random(StableHash(person.Id)).NextDouble() * 0.6m;
        var weight = baseWeight * strength * (0.9m + (decimal)random.NextDouble() * 0.2m);
        var step = settings.RoundTo <= 0 ? 2.5m : settings.RoundTo;
        return Math.Max(step, Math.Round(weight / step) * step);
    }

    private static int StableHash(string value)
    {
        unchecked
        {
            var hash = 17;
            foreach (var c in value)
            {
                hash = hash * 31 + c;
            }

            return hash;
        }
    }

    /// <summary>Simulated lifter for one exercise: reps hover around target and improve until they move up.</summary>
    private sealed class LiftState(decimal weight)
    {
        public decimal Weight { get; private set; } = weight;

        private int _hitStreak;
        private int _missStreak;

        /// <summary>Skill at the current weight, in reps relative to target (starts slightly short after a jump).</summary>
        private double _form = 0.2;

        public List<int> Perform(int sets, int targetReps, Random random)
        {
            var reps = new List<int>(sets);
            for (var s = 0; s < sets; s++)
            {
                var fatigue = s * 0.45;
                var noise = random.NextDouble() * 1.6 - 0.8;
                var value = targetReps + _form - fatigue + noise + 0.6;
                reps.Add(Math.Clamp((int)Math.Round(value), Math.Max(1, targetReps - 4), targetReps + 3));
            }

            return reps;
        }

        public void Advance(List<int> reps, int targetSets, int targetReps, decimal increment, ProgressionSettings settings)
        {
            var hit = reps.Count(r => r >= targetReps) >= targetSets;
            _hitStreak = hit ? _hitStreak + 1 : 0;
            _missStreak = hit ? 0 : _missStreak + 1;

            if (_hitStreak >= Math.Max(1, settings.SessionsToProgress))
            {
                Weight += increment;
                _hitStreak = 0;
                _form = -1.4; // heavier: reps dip, then recover
            }
            else if (settings.MissesBeforeDeload > 0 && _missStreak >= settings.MissesBeforeDeload)
            {
                Weight = Math.Max(increment, Math.Floor(Weight * (100 - settings.DeloadPercent) / 100m / increment) * increment);
                _missStreak = 0;
                _form = 0.5;
            }
            else
            {
                _form = Math.Min(1.2, _form + 0.7); // practice at the same weight
            }
        }
    }
}
