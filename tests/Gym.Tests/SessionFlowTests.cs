using Gym.Web.Data;
using Gym.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Gym.Tests;

public sealed class SessionFlowTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeTime _time = new(new DateTimeOffset(2026, 10, 5, 22, 0, 0, TimeSpan.Zero));
    private readonly LiveUpdates _live = new();
    private readonly GroupService _groups;
    private readonly ScheduleService _schedules;
    private readonly SessionService _sessions;
    private readonly StatsService _stats;
    private readonly ApplicationUser _tony;
    private readonly ApplicationUser _maria;
    private readonly ApplicationUser _stranger;

    public SessionFlowTests()
    {
        _groups = new GroupService(_db);
        _schedules = new ScheduleService(_db);
        _sessions = new SessionService(_db, _live, _time);
        _stats = new StatsService(_db, _time);
        _tony = _db.AddUser("Tony");
        _maria = _db.AddUser("Maria");
        _stranger = _db.AddUser("Stranger");
    }

    private async Task<(Schedule Schedule, int SessionId)> StartGroupSessionAsync()
    {
        var group = await _groups.CreateAsync(_tony.Id, "Tony & Maria");
        Assert.Null(await _groups.AddMemberAsync(group.Id, _tony.Id, "maria@example.com"));
        var schedule = await _schedules.CreateStarterAsync(_tony.Id, group.Id);
        var id = await _sessions.StartAsync(_tony.Id, schedule.Id, null, [_tony.Id, _maria.Id]);
        return ((await _schedules.GetAsync(schedule.Id, _tony.Id))!, id);
    }

    [Fact]
    public async Task Group_session_logs_sets_for_everyone_and_advances_rotation()
    {
        var (schedule, id) = await StartGroupSessionAsync();
        var session = (await _sessions.GetAsync(id, _maria.Id))!;
        Assert.Equal("Chest & Triceps", session.Title);
        Assert.Equal(2, session.Participants.Count);
        var bench = session.Exercises[0];

        // Tony's phone logs both people.
        await _sessions.LogSetAsync(id, _tony.Id, bench.Id, _tony.Id, 8, 135);
        await _sessions.LogSetAsync(id, _tony.Id, bench.Id, _maria.Id, 8, 65);

        // Maria's phone times her second set.
        await _sessions.StartSetAsync(id, _maria.Id, bench.Id, _maria.Id, 65);
        _time.Advance(TimeSpan.FromSeconds(42));
        session = (await _sessions.GetAsync(id, _tony.Id))!;
        var running = Assert.Single(session.Sets, s => s.IsInProgress);
        await _sessions.CompleteSetAsync(id, _tony.Id, running.Id, 8, 0); // other device never saw the weight

        session = (await _sessions.GetAsync(id, _tony.Id))!;
        var timed = session.Sets.Single(s => s.UserId == _maria.Id && s.SetNumber == 2);
        Assert.Equal(65m, timed.Weight);
        Assert.Equal(TimeSpan.FromSeconds(42), timed.Duration);

        await _sessions.FinishAsync(id, _maria.Id);

        session = (await _sessions.GetAsync(id, _tony.Id))!;
        Assert.Equal(SessionStatus.Completed, session.Status);
        var after = (await _schedules.GetAsync(schedule.Id, _tony.Id))!;
        Assert.Equal(schedule.Days[1].Id, ScheduleService.NextDay(after)!.Id);

        var tonyBench = await _stats.GetUserExerciseAsync(_tony.Id, bench.ExerciseId);
        Assert.Equal(135m, tonyBench.WorkingWeight);
        var history = await _stats.GetHistoryAsync(_maria.Id, bench.ExerciseId);
        Assert.Equal(2, Assert.Single(history).Performance.Sets.Count);
    }

    [Fact]
    public async Task Second_device_joins_the_running_session()
    {
        var (schedule, id) = await StartGroupSessionAsync();
        var again = await _sessions.StartAsync(_maria.Id, schedule.Id, null, [_maria.Id]);
        Assert.Equal(id, again);
    }

    [Fact]
    public async Task Changes_are_broadcast_to_other_devices()
    {
        var (_, id) = await StartGroupSessionAsync();
        var session = (await _sessions.GetAsync(id, _tony.Id))!;
        var notified = new List<int>();
        _live.SessionChanged += notified.Add;
        await _sessions.LogSetAsync(id, _tony.Id, session.Exercises[0].Id, _maria.Id, 10, 50);
        Assert.Equal([id], notified);
    }

    [Fact]
    public async Task Outsiders_cannot_see_or_change_a_group_session()
    {
        var (_, id) = await StartGroupSessionAsync();
        Assert.Null(await _sessions.GetAsync(id, _stranger.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _sessions.FinishAsync(id, _stranger.Id));
        var session = (await _sessions.GetAsync(id, _tony.Id))!;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sessions.LogSetAsync(id, _tony.Id, session.Exercises[0].Id, _stranger.Id, 8, 100));
    }

    [Fact]
    public async Task Cancelling_an_empty_session_removes_it_and_keeps_the_day()
    {
        var (schedule, id) = await StartGroupSessionAsync();
        await _sessions.CancelAsync(id, _tony.Id);
        Assert.Null(await _sessions.GetAsync(id, _tony.Id));
        var after = (await _schedules.GetAsync(schedule.Id, _tony.Id))!;
        Assert.Equal(schedule.Days[0].Id, ScheduleService.NextDay(after)!.Id);
    }

    [Fact]
    public async Task Finishing_drops_timed_sets_with_no_reps_and_renumbering_works()
    {
        var (_, id) = await StartGroupSessionAsync();
        var bench = (await _sessions.GetAsync(id, _tony.Id))!.Exercises[0];
        await _sessions.LogSetAsync(id, _tony.Id, bench.Id, _tony.Id, 8, 100);
        await _sessions.LogSetAsync(id, _tony.Id, bench.Id, _tony.Id, 8, 105);
        await _sessions.LogSetAsync(id, _tony.Id, bench.Id, _tony.Id, 8, 110);
        var first = (await _sessions.GetAsync(id, _tony.Id))!.Sets.Single(s => s.SetNumber == 1);
        await _sessions.DeleteSetAsync(id, _tony.Id, first.Id);
        await _sessions.StartSetAsync(id, _tony.Id, bench.Id, _tony.Id, 110);

        var numbers = (await _sessions.GetAsync(id, _tony.Id))!.Sets.Where(s => s.UserId == _tony.Id).Select(s => s.SetNumber).Order();
        Assert.Equal([1, 2, 3], numbers);

        await _sessions.FinishAsync(id, _tony.Id);
        var sets = (await _sessions.GetAsync(id, _tony.Id))!.Sets;
        Assert.Equal(2, sets.Count);
        Assert.All(sets, s => Assert.False(s.IsInProgress));
    }

    [Fact]
    public async Task Schedule_days_can_be_reordered_and_deleted()
    {
        var schedule = await _schedules.CreateStarterAsync(_tony.Id, null);
        var loaded = (await _schedules.GetAsync(schedule.Id, _tony.Id))!;
        var leg = loaded.Days.Single(d => d.Name == "Leg Day");
        Assert.Equal(3, leg.Order);

        await _schedules.MoveDayAsync(schedule.Id, _tony.Id, leg.Id, -1);
        loaded = (await _schedules.GetAsync(schedule.Id, _tony.Id))!;
        Assert.Equal(["Chest & Triceps", "Leg Day", "Back & Biceps", "Shoulders & Core"], loaded.Days.Select(d => d.Name));

        await _schedules.DeleteDayAsync(schedule.Id, _tony.Id, loaded.Days[0].Id);
        loaded = (await _schedules.GetAsync(schedule.Id, _tony.Id))!;
        Assert.Equal([1, 2, 3], loaded.Days.Select(d => d.Order));
        Assert.Null(await _schedules.GetAsync(schedule.Id, _maria.Id));
    }

    [Fact]
    public async Task Suggestions_use_previous_sessions_only()
    {
        var (schedule, id) = await StartGroupSessionAsync();
        var bench = (await _sessions.GetAsync(id, _tony.Id))!.Exercises[0];
        for (var i = 0; i < 3; i++)
        {
            await _sessions.LogSetAsync(id, _tony.Id, bench.Id, _tony.Id, 8, 135);
        }

        await _sessions.FinishAsync(id, _tony.Id);
        await _schedules.SetNextDayAsync(schedule.Id, _tony.Id, schedule.Days[0].Id);
        _time.Advance(TimeSpan.FromDays(3));
        var second = await _sessions.StartAsync(_tony.Id, schedule.Id, null, [_tony.Id]);
        for (var i = 0; i < 3; i++)
        {
            await _sessions.LogSetAsync(second, _tony.Id, (await _sessions.GetAsync(second, _tony.Id))!.Exercises[0].Id, _tony.Id, 8, 135);
        }

        await _sessions.FinishAsync(second, _tony.Id);
        _time.Advance(TimeSpan.FromDays(3));

        var summary = await _stats.GetSummaryAsync(_tony.Id, bench.ExerciseId);
        Assert.Equal(SuggestionKind.Increase, summary.Suggestion.Kind);
        Assert.Equal(140m, summary.Suggestion.Weight);
        Assert.Equal(2, summary.Stats.TargetStreak);

        var all = await _stats.GetSummariesAsync(_tony.Id);
        Assert.Contains(all, s => s.Exercise.Id == bench.ExerciseId);
    }

    public void Dispose() => _db.Dispose();
}
