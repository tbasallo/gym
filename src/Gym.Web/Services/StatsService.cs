using Gym.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gym.Web.Services;

public sealed record HistoryEntry(int SessionId, string Title, DateTime DateUtc, SessionPerformance Performance, TimeSpan? TimeUnderLoad);

public sealed record ExerciseSummary(Exercise Exercise, ExerciseStats Stats, ProgressionSuggestion Suggestion);

public sealed record PlannedExercise(SessionPerformance? Last, ProgressionSuggestion Suggestion, decimal? WorkingWeight, int? TargetSets, int? TargetReps);

public sealed class StatsService(IDbContextFactory<ApplicationDbContext> dbFactory, TimeProvider clock)
{
    public async Task<ProgressionSettings> GetSettingsAsync(string userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.ProgressionSettings.AsNoTracking().SingleOrDefaultAsync(s => s.UserId == userId)
            ?? new ProgressionSettings { UserId = userId };
    }

    public async Task SaveSettingsAsync(ProgressionSettings settings)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var exists = await db.ProgressionSettings.AnyAsync(s => s.UserId == settings.UserId);
        db.Entry(settings).State = exists ? EntityState.Modified : EntityState.Added;
        await db.SaveChangesAsync();
    }

    public async Task<UserExerciseSetting> GetUserExerciseAsync(string userId, int exerciseId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.UserExerciseSettings.AsNoTracking().SingleOrDefaultAsync(s => s.UserId == userId && s.ExerciseId == exerciseId)
            ?? new UserExerciseSetting { UserId = userId, ExerciseId = exerciseId };
    }

    /// <summary>Saves a person's working weight and personal target overrides for an exercise.</summary>
    public async Task SaveUserExerciseAsync(string actingUserId, UserExerciseSetting input)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var sharesGroup = input.UserId == actingUserId || await db.GroupMembers.AnyAsync(m => m.UserId == input.UserId
            && db.GroupMembers.Any(me => me.UserId == actingUserId && me.GroupId == m.GroupId));
        if (!sharesGroup)
        {
            throw new UnauthorizedAccessException("You can only change numbers for people in your group.");
        }

        var setting = await db.UserExerciseSettings.FindAsync(input.UserId, input.ExerciseId);
        if (setting is null)
        {
            setting = new UserExerciseSetting { UserId = input.UserId, ExerciseId = input.ExerciseId };
            db.UserExerciseSettings.Add(setting);
        }

        setting.WorkingWeight = input.WorkingWeight is >= 0 ? input.WorkingWeight : null;
        setting.TargetSets = input.TargetSets is > 0 ? input.TargetSets : null;
        setting.TargetReps = input.TargetReps is > 0 ? input.TargetReps : null;
        await db.SaveChangesAsync();
    }

    /// <summary>How often someone has been training.</summary>
    public async Task<(int Total, int Last30Days, int Last7Days, DateTime? LastUtc)> GetActivityAsync(string userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var now = clock.GetUtcNow().UtcDateTime;
        var dates = await db.Sessions.AsNoTracking()
            .Where(s => s.Status == SessionStatus.Completed && s.Sets.Any(x => x.UserId == userId))
            .Select(s => s.StartedUtc)
            .ToListAsync();
        return (dates.Count, dates.Count(d => d >= now.AddDays(-30)), dates.Count(d => d >= now.AddDays(-7)),
            dates.Count == 0 ? null : dates.Max());
    }

    /// <summary>Completed sessions for one exercise, newest first.</summary>
    public async Task<List<HistoryEntry>> GetHistoryAsync(string userId, int exerciseId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return (await LoadAsync(db, userId, [exerciseId])).GetValueOrDefault(exerciseId) ?? [];
    }

    public async Task<ExerciseSummary> GetSummaryAsync(string userId, int exerciseId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var exercise = await db.Exercises.AsNoTracking().SingleAsync(e => e.Id == exerciseId);
        var history = (await LoadAsync(db, userId, [exerciseId])).GetValueOrDefault(exerciseId) ?? [];
        var settings = await GetSettingsAsync(userId);
        var working = await db.UserExerciseSettings.AsNoTracking()
            .Where(s => s.UserId == userId && s.ExerciseId == exerciseId)
            .Select(s => s.WorkingWeight).SingleOrDefaultAsync();
        return Summarize(exercise, history, settings, working);
    }

    /// <summary>Every exercise the user has done, with stats and the next suggestion.</summary>
    public async Task<List<ExerciseSummary>> GetSummariesAsync(string userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var history = await LoadAsync(db, userId, null);
        var exercises = await db.Exercises.AsNoTracking().Where(e => history.Keys.Contains(e.Id)).ToListAsync();
        var settings = await GetSettingsAsync(userId);
        var working = await db.UserExerciseSettings.AsNoTracking()
            .Where(s => s.UserId == userId)
            .ToDictionaryAsync(s => s.ExerciseId, s => s.WorkingWeight);
        return exercises
            .Select(e => Summarize(e, history[e.Id], settings, working.GetValueOrDefault(e.Id)))
            .OrderByDescending(s => s.Stats.LastUtc)
            .ToList();
    }

    /// <summary>Last performance and suggestion for each participant and exercise in a session.</summary>
    public async Task<Dictionary<(string UserId, int ExerciseId), PlannedExercise>> GetPlanAsync(WorkoutSession session)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var exerciseIds = session.Exercises.Select(e => e.ExerciseId).Distinct().ToList();
        var result = new Dictionary<(string, int), PlannedExercise>();

        foreach (var participant in session.Participants)
        {
            var userId = participant.UserId;
            var history = await LoadAsync(db, userId, exerciseIds, excludeSessionId: session.Id);
            var settings = await GetSettingsAsync(userId);
            var mine = await db.UserExerciseSettings.AsNoTracking()
                .Where(s => s.UserId == userId && exerciseIds.Contains(s.ExerciseId))
                .ToDictionaryAsync(s => s.ExerciseId);

            foreach (var se in session.Exercises)
            {
                var entries = history.GetValueOrDefault(se.ExerciseId) ?? [];
                var own = mine.GetValueOrDefault(se.ExerciseId);
                var summary = Summarize(se.Exercise, entries, settings, own?.WorkingWeight);
                result[(userId, se.ExerciseId)] = new PlannedExercise(
                    entries.FirstOrDefault()?.Performance, summary.Suggestion, own?.WorkingWeight, own?.TargetSets, own?.TargetReps);
            }
        }

        return result;
    }

    private ExerciseSummary Summarize(Exercise exercise, List<HistoryEntry> history, ProgressionSettings settings, decimal? workingWeight)
    {
        var performances = history.Select(h => h.Performance).ToList();
        var context = new ProgressionContext
        {
            History = performances,
            Settings = settings,
            Increment = Progression.IncrementFor(exercise, settings),
            WorkingWeight = workingWeight,
            NowUtc = clock.GetUtcNow().UtcDateTime,
        };
        return new ExerciseSummary(exercise, ExerciseStats.From(performances), ProgressionEngine.Default.Suggest(context));
    }

    private static async Task<Dictionary<int, List<HistoryEntry>>> LoadAsync(
        ApplicationDbContext db, string userId, IReadOnlyCollection<int>? exerciseIds, int? excludeSessionId = null)
    {
        var query = db.SetLogs.AsNoTracking()
            .Where(s => s.UserId == userId && s.CompletedUtc != null && s.Reps > 0
                && s.Session.Status == SessionStatus.Completed);
        if (exerciseIds is not null)
        {
            query = query.Where(s => exerciseIds.Contains(s.ExerciseId));
        }

        if (excludeSessionId is not null)
        {
            query = query.Where(s => s.SessionId != excludeSessionId);
        }

        var rows = await query
            .Select(s => new
            {
                s.SessionId,
                s.Session.Title,
                SessionStartedUtc = s.Session.StartedUtc,
                s.ExerciseId,
                s.SetNumber,
                s.Reps,
                s.Weight,
                s.TargetSets,
                s.TargetReps,
                s.StartedUtc,
                s.CompletedUtc,
            })
            .ToListAsync();

        return rows
            .GroupBy(r => r.ExerciseId)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(r => r.SessionId)
                    .Select(sg =>
                    {
                        var sets = sg.OrderBy(r => r.SetNumber).ToList();
                        var first = sets[0];
                        var timed = sets.Where(r => r.StartedUtc is not null).Select(r => r.CompletedUtc!.Value - r.StartedUtc!.Value).ToList();
                        return new HistoryEntry(
                            sg.Key,
                            first.Title,
                            first.SessionStartedUtc,
                            new SessionPerformance(first.SessionStartedUtc, first.TargetSets, first.TargetReps,
                                sets.Select(r => new SetResult(r.Reps, r.Weight)).ToList()),
                            timed.Count == 0 ? null : TimeSpan.FromTicks(timed.Sum(t => t.Ticks)));
                    })
                    .OrderByDescending(h => h.DateUtc)
                    .ToList());
    }
}
