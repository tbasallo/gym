using Gym.Web.Data;
using Gym.Web.Services;

namespace Gym.Tests;

public class ProgressionTests
{
    private static readonly DateTime Today = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

    private static SessionPerformance Session(int daysAgo, decimal weight, params int[] reps) =>
        new(Today.AddDays(-daysAgo), 3, 8, reps.Select(r => new SetResult(r, weight)).ToList());

    private static ProgressionSuggestion Suggest(decimal increment, params SessionPerformance[] newestFirst) =>
        ProgressionEngine.Default.Suggest(new ProgressionContext
        {
            History = newestFirst,
            Settings = new ProgressionSettings(),
            Increment = increment,
            NowUtc = Today,
        });

    [Fact]
    public void No_history_asks_to_establish_a_weight()
    {
        var s = Suggest(5);
        Assert.Equal(SuggestionKind.Establish, s.Kind);
    }

    [Fact]
    public void Two_sessions_hitting_targets_at_same_weight_moves_up()
    {
        var s = Suggest(5, Session(2, 135, 8, 8, 8), Session(5, 135, 8, 8, 9));
        Assert.Equal(SuggestionKind.Increase, s.Kind);
        Assert.Equal(140m, s.Weight);
    }

    [Fact]
    public void One_good_session_holds()
    {
        var s = Suggest(5, Session(2, 135, 8, 8, 8), Session(5, 130, 8, 8, 8));
        Assert.Equal(SuggestionKind.Hold, s.Kind);
        Assert.Equal(135m, s.Weight);
        Assert.Contains("1 of 2", s.Reason);
    }

    [Fact]
    public void Missed_reps_hold_the_weight()
    {
        var s = Suggest(5, Session(2, 135, 8, 8, 6), Session(5, 135, 8, 8, 8));
        Assert.Equal(SuggestionKind.Hold, s.Kind);
        Assert.Equal(135m, s.Weight);
    }

    [Fact]
    public void Three_misses_in_a_row_deloads_ten_percent_rounded_down()
    {
        var s = Suggest(5, Session(2, 135, 8, 7, 6), Session(5, 135, 8, 6, 6), Session(9, 135, 7, 7, 6));
        Assert.Equal(SuggestionKind.Deload, s.Kind);
        Assert.Equal(120m, s.Weight); // 121.5 rounded down to 2.5 steps
    }

    [Fact]
    public void Long_layoff_eases_back_in()
    {
        var s = Suggest(5, Session(30, 100, 8, 8, 8), Session(33, 100, 8, 8, 8));
        Assert.Equal(SuggestionKind.Deload, s.Kind);
        Assert.Equal(90m, s.Weight);
        Assert.Equal("Layoff", s.Rule);
    }

    [Fact]
    public void Fewer_sets_than_target_is_not_a_hit()
    {
        Assert.False(Session(1, 100, 8, 8).HitTargets);
        Assert.True(Session(1, 100, 8, 8, 8).HitTargets);
    }

    [Fact]
    public void Lighter_back_off_sets_do_not_count_toward_targets()
    {
        var perf = new SessionPerformance(Today, 3, 8, [new(8, 135), new(8, 100), new(8, 100)]);
        Assert.Equal(135m, perf.TopWeight);
        Assert.False(perf.HitTargets);
    }

    [Theory]
    [InlineData(BodyRegion.Upper, 5)]
    [InlineData(BodyRegion.Lower, 10)]
    [InlineData(BodyRegion.FullBody, 10)]
    [InlineData(BodyRegion.Core, 5)]
    public void Increment_depends_on_body_region(BodyRegion region, decimal expected)
    {
        Assert.Equal(expected, Progression.IncrementFor(new Exercise { BodyRegion = region }, new ProgressionSettings()));
    }

    [Fact]
    public void Exercise_increment_overrides_settings()
    {
        Assert.Equal(2.5m, Progression.IncrementFor(new Exercise { BodyRegion = BodyRegion.Lower, WeightIncrement = 2.5m }, new ProgressionSettings()));
    }

    [Fact]
    public void Epley_estimates_one_rep_max()
    {
        Assert.Equal(100m, Progression.Epley(100, 1));
        Assert.Equal(126.7m, Progression.Epley(100, 8));
        Assert.Equal(0m, Progression.Epley(100, 0));
    }

    [Fact]
    public void Stats_summarize_history()
    {
        var stats = ExerciseStats.From([Session(9, 100, 8, 8, 8), Session(5, 110, 8, 8, 8), Session(1, 110, 8, 8, 8)]);
        Assert.Equal(Today.AddDays(-9), stats.FirstUtc);
        Assert.Equal(Today.AddDays(-1), stats.LastUtc);
        Assert.Equal(3, stats.SessionCount);
        Assert.Equal(9, stats.TotalSets);
        Assert.Equal(100m, stats.MinWeight);
        Assert.Equal(110m, stats.MaxWeight);
        Assert.Equal(3, stats.TargetStreak);
        Assert.Equal(2, stats.SessionsAtCurrentWeight);
        Assert.Equal(3, stats.Trend.Count);
        Assert.True(stats.Trend[0].DateUtc < stats.Trend[^1].DateUtc);
    }

    [Fact]
    public void Reps_climbing_at_the_same_weight_trend_up()
    {
        var trend = ExerciseStats.From([Session(9, 100, 6, 6, 6), Session(5, 100, 7, 7, 6), Session(1, 100, 8, 8, 7)]).Reps;
        Assert.Equal(RepDirection.Up, trend.Direction);
        Assert.Equal(100m, trend.Weight);
        Assert.Equal(6m, trend.FromAvg);
        Assert.Equal(7.7m, trend.ToAvg);
        Assert.Equal(3, trend.Sessions);
    }

    [Fact]
    public void Reps_dropping_trend_down_and_small_changes_are_steady()
    {
        Assert.Equal(RepDirection.Down, ExerciseStats.From([Session(5, 100, 8, 8, 8), Session(1, 100, 8, 6, 6)]).Reps.Direction);
        Assert.Equal(RepDirection.Steady, ExerciseStats.From([Session(5, 100, 8, 8, 8), Session(1, 100, 8, 8, 7)]).Reps.Direction);
    }

    [Fact]
    public void Rep_trend_only_compares_sessions_at_the_current_weight()
    {
        // Reps fell because the weight went up; that is not a decline.
        var trend = ExerciseStats.From([Session(9, 100, 10, 10, 10), Session(1, 110, 6, 6, 6)]).Reps;
        Assert.Equal(RepDirection.Unknown, trend.Direction);
        Assert.Equal(110m, trend.Weight);
        Assert.Equal(6m, trend.ToAvg);
    }

    [Fact]
    public void Trend_points_keep_every_set_and_average_reps()
    {
        var point = ExerciseStats.From([Session(1, 100, 8, 7, 6)]).Trend.Single();
        Assert.Equal([8, 7, 6], point.Sets.Select(s => s.Reps));
        Assert.Equal(8, point.TargetReps);
        Assert.Equal(7m, point.AvgReps);
    }

    [Fact]
    public void Lighter_back_off_sets_are_left_out_of_rep_averages()
    {
        var withBackOff = new SessionPerformance(Today, 3, 8, [new(6, 135), new(12, 95), new(12, 95)]);
        var stats = ExerciseStats.From([Session(5, 135, 6, 6, 6), withBackOff]);
        Assert.Equal(6m, stats.Trend[^1].AvgReps);
        Assert.Equal(RepDirection.Steady, stats.Reps.Direction);
    }

    [Fact]
    public void Stats_of_nothing_are_empty()
    {
        Assert.Same(ExerciseStats.Empty, ExerciseStats.From([]));
    }
}
