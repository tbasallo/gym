namespace Gym.Web.Services;

public sealed record TrendPoint(DateTime DateUtc, decimal TopWeight, decimal Estimated1RM, bool HitTargets);

public sealed record ExerciseStats(
    DateTime? FirstUtc,
    DateTime? LastUtc,
    int SessionCount,
    int TotalSets,
    decimal? MinWeight,
    decimal? MaxWeight,
    decimal? LastTopWeight,
    decimal? BestEstimated1RM,
    int TargetStreak,
    int SessionsAtCurrentWeight,
    IReadOnlyList<TrendPoint> Trend)
{
    public static ExerciseStats Empty { get; } = new(null, null, 0, 0, null, null, null, null, 0, 0, []);

    /// <summary>Builds stats from completed sessions in any order.</summary>
    public static ExerciseStats From(IEnumerable<SessionPerformance> sessions)
    {
        var history = sessions.Where(s => s.Sets.Count > 0).OrderBy(s => s.DateUtc).ToList();
        if (history.Count == 0)
        {
            return Empty;
        }

        var newestFirst = Enumerable.Reverse(history).ToList();
        var last = newestFirst[0];
        var allSets = history.SelectMany(h => h.Sets).ToList();

        return new ExerciseStats(
            FirstUtc: history[0].DateUtc,
            LastUtc: last.DateUtc,
            SessionCount: history.Count,
            TotalSets: allSets.Count,
            MinWeight: allSets.Min(s => s.Weight),
            MaxWeight: allSets.Max(s => s.Weight),
            LastTopWeight: last.TopWeight,
            BestEstimated1RM: history.Max(h => h.Estimated1RM),
            TargetStreak: newestFirst.TakeWhile(h => h.HitTargets).Count(),
            SessionsAtCurrentWeight: newestFirst.TakeWhile(h => h.TopWeight == last.TopWeight).Count(),
            Trend: history.Select(h => new TrendPoint(h.DateUtc, h.TopWeight, h.Estimated1RM, h.HitTargets)).ToList());
    }
}
