namespace Gym.Web.Services;

public sealed record TrendPoint(
    DateTime DateUtc,
    decimal TopWeight,
    decimal Estimated1RM,
    bool HitTargets,
    int TargetReps,
    IReadOnlyList<SetResult> Sets)
{
    /// <summary>Average reps per working set (sets at the session's top weight; lighter back-off sets are left out).</summary>
    public decimal AvgReps => RepTrend.WorkingAvg(Sets, TopWeight);
}

public enum RepDirection
{
    Unknown,
    Up,
    Steady,
    Down,
}

/// <summary>
/// Whether reps are climbing at the current weight. Only sessions at the same top weight are
/// compared, since reps naturally drop right after moving up.
/// </summary>
public sealed record RepTrend(RepDirection Direction, decimal Weight, decimal? FromAvg, decimal? ToAvg, int Sessions)
{
    /// <summary>Change in average reps per set that counts as movement rather than noise.</summary>
    public const decimal Threshold = 0.5m;

    public static RepTrend Unknown { get; } = new(RepDirection.Unknown, 0, null, null, 0);

    public static RepTrend From(IReadOnlyList<SessionPerformance> newestFirst)
    {
        if (newestFirst.Count == 0)
        {
            return Unknown;
        }

        var weight = newestFirst[0].TopWeight;
        var atWeight = newestFirst.TakeWhile(h => h.TopWeight == weight).ToList();
        if (atWeight.Count < 2)
        {
            return new RepTrend(RepDirection.Unknown, weight, null, AvgReps(atWeight[0]), atWeight.Count);
        }

        var from = AvgReps(atWeight[^1]);
        var to = AvgReps(atWeight[0]);
        var direction = to - from >= Threshold ? RepDirection.Up
            : from - to >= Threshold ? RepDirection.Down
            : RepDirection.Steady;
        return new RepTrend(direction, weight, from, to, atWeight.Count);
    }

    private static decimal AvgReps(SessionPerformance p) => WorkingAvg(p.Sets, p.TopWeight);

    internal static decimal WorkingAvg(IReadOnlyList<SetResult> sets, decimal topWeight)
    {
        var working = sets.Where(s => s.Weight >= topWeight).ToList();
        return working.Count == 0 ? 0 : Math.Round(working.Average(s => (decimal)s.Reps), 1);
    }
}

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
    RepTrend Reps,
    IReadOnlyList<TrendPoint> Trend)
{
    public static ExerciseStats Empty { get; } = new(null, null, 0, 0, null, null, null, null, 0, 0, RepTrend.Unknown, []);

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
            Reps: RepTrend.From(newestFirst),
            Trend: history.Select(h => new TrendPoint(h.DateUtc, h.TopWeight, h.Estimated1RM, h.HitTargets, h.TargetReps, h.Sets)).ToList());
    }
}
