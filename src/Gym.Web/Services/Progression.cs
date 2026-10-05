using Gym.Web.Data;

namespace Gym.Web.Services;

public sealed record SetResult(int Reps, decimal Weight);

/// <summary>One session's worth of sets for a single user and exercise.</summary>
public sealed record SessionPerformance(DateTime DateUtc, int TargetSets, int TargetReps, IReadOnlyList<SetResult> Sets)
{
    public decimal TopWeight => Sets.Count == 0 ? 0 : Sets.Max(s => s.Weight);

    /// <summary>Every target set was done for the target reps at the session's top weight.</summary>
    public bool HitTargets =>
        Sets.Count(s => s.Weight >= TopWeight && s.Reps >= TargetReps) >= Math.Max(1, TargetSets);

    public decimal Estimated1RM => Sets.Count == 0 ? 0 : Sets.Max(s => Progression.Epley(s.Weight, s.Reps));
}

public enum SuggestionKind
{
    Establish,
    Hold,
    Increase,
    Deload,
}

public sealed record ProgressionSuggestion(SuggestionKind Kind, decimal? Weight, string Reason, string Rule);

public sealed class ProgressionContext
{
    /// <summary>Completed sessions, most recent first.</summary>
    public required IReadOnlyList<SessionPerformance> History { get; init; }

    public required ProgressionSettings Settings { get; init; }

    public required decimal Increment { get; init; }

    public decimal? WorkingWeight { get; init; }

    public DateTime NowUtc { get; init; } = DateTime.UtcNow;

    public SessionPerformance? Last => History.Count > 0 ? History[0] : null;

    public decimal Round(decimal weight, bool down = false)
    {
        var step = Settings.RoundTo <= 0 ? 1 : Settings.RoundTo;
        var steps = weight / step;
        steps = down ? Math.Floor(steps) : Math.Round(steps, MidpointRounding.AwayFromZero);
        return Math.Max(0, steps * step);
    }

    /// <summary>Most recent sessions, newest first, that share the latest session's top weight.</summary>
    public IEnumerable<SessionPerformance> AtCurrentWeight() =>
        Last is null ? [] : History.TakeWhile(h => h.TopWeight == Last.TopWeight);
}

/// <summary>A single progression rule. Rules run in order; the first one that returns a suggestion wins.</summary>
public interface IProgressionRule
{
    string Name { get; }

    ProgressionSuggestion? Evaluate(ProgressionContext context);
}

public sealed class NoHistoryRule : IProgressionRule
{
    public string Name => "First time";

    public ProgressionSuggestion? Evaluate(ProgressionContext c) =>
        c.Last is not null
            ? null
            : new(SuggestionKind.Establish, c.WorkingWeight,
                "First time: pick a weight you can move with good form for every rep.", Name);
}

public sealed class LayoffRule : IProgressionRule
{
    public string Name => "Layoff";

    public ProgressionSuggestion? Evaluate(ProgressionContext c)
    {
        if (c.Last is null || c.Settings.LayoffDays <= 0)
        {
            return null;
        }

        var days = (int)(c.NowUtc - c.Last.DateUtc).TotalDays;
        if (days < c.Settings.LayoffDays)
        {
            return null;
        }

        var weight = c.Round(c.Last.TopWeight * (100 - c.Settings.DeloadPercent) / 100m, down: true);
        return new(SuggestionKind.Deload, weight,
            $"{days} days since last time. Ease back in about {c.Settings.DeloadPercent}% lighter.", Name);
    }
}

public sealed class ProgressRule : IProgressionRule
{
    public string Name => "Move up";

    public ProgressionSuggestion? Evaluate(ProgressionContext c)
    {
        var needed = Math.Max(1, c.Settings.SessionsToProgress);
        var streak = c.AtCurrentWeight().TakeWhile(h => h.HitTargets).Count();
        if (c.Last is null || streak < needed)
        {
            return null;
        }

        var weight = c.Round(c.Last.TopWeight + c.Increment);
        return new(SuggestionKind.Increase, weight,
            $"Hit every target {streak} session{(streak == 1 ? "" : "s")} in a row at {c.Last.TopWeight:0.##}. Time to move up.", Name);
    }
}

public sealed class StallRule : IProgressionRule
{
    public string Name => "Deload";

    public ProgressionSuggestion? Evaluate(ProgressionContext c)
    {
        var needed = c.Settings.MissesBeforeDeload;
        if (c.Last is null || needed <= 0)
        {
            return null;
        }

        var misses = c.AtCurrentWeight().TakeWhile(h => !h.HitTargets).Count();
        if (misses < needed)
        {
            return null;
        }

        var weight = c.Round(c.Last.TopWeight * (100 - c.Settings.DeloadPercent) / 100m, down: true);
        return new(SuggestionKind.Deload, weight,
            $"Missed targets {misses} sessions in a row at {c.Last.TopWeight:0.##}. Drop {c.Settings.DeloadPercent}% and build back up.", Name);
    }
}

public sealed class HoldRule : IProgressionRule
{
    public string Name => "Hold";

    public ProgressionSuggestion? Evaluate(ProgressionContext c)
    {
        if (c.Last is null)
        {
            return null;
        }

        var needed = Math.Max(1, c.Settings.SessionsToProgress);
        var streak = c.AtCurrentWeight().TakeWhile(h => h.HitTargets).Count();
        var reason = c.Last.HitTargets
            ? $"Hit targets {streak} of {needed} sessions needed to move up."
            : "Missed some reps last time. Stay here and own it.";
        return new(SuggestionKind.Hold, c.Last.TopWeight, reason, Name);
    }
}

public sealed class ProgressionEngine(IEnumerable<IProgressionRule> rules)
{
    public static ProgressionEngine Default { get; } = new([
        new NoHistoryRule(),
        new LayoffRule(),
        new ProgressRule(),
        new StallRule(),
        new HoldRule(),
    ]);

    public IReadOnlyList<IProgressionRule> Rules { get; } = rules.ToList();

    public ProgressionSuggestion Suggest(ProgressionContext context)
    {
        foreach (var rule in Rules)
        {
            if (rule.Evaluate(context) is { } suggestion)
            {
                return suggestion;
            }
        }

        return new(SuggestionKind.Hold, context.Last?.TopWeight ?? context.WorkingWeight, "", "None");
    }
}

public static class Progression
{
    /// <summary>Estimated one-rep max (Epley).</summary>
    public static decimal Epley(decimal weight, int reps) =>
        reps <= 0 ? 0 : reps == 1 ? weight : Math.Round(weight * (1 + reps / 30m), 1);

    public static decimal IncrementFor(Exercise exercise, ProgressionSettings settings) =>
        exercise.WeightIncrement ?? exercise.BodyRegion switch
        {
            BodyRegion.Lower or BodyRegion.FullBody => settings.LowerIncrement,
            BodyRegion.Core => settings.CoreIncrement,
            _ => settings.UpperIncrement,
        };
}
