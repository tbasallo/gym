using Gym.Web.Data;
using Gym.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Gym.Tests;

public sealed class SampleDataTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeTime _time = new(new DateTimeOffset(2026, 10, 6, 15, 0, 0, TimeSpan.Zero));
    private readonly GroupService _groups;
    private readonly ScheduleService _schedules;
    private readonly SessionService _sessions;
    private readonly StatsService _stats;
    private readonly SampleDataService _sample;
    private readonly ApplicationUser _tony;
    private readonly ApplicationUser _maria;

    public SampleDataTests()
    {
        _groups = new GroupService(_db);
        _schedules = new ScheduleService(_db);
        _sessions = new SessionService(_db, new LiveUpdates(), _time);
        _stats = new StatsService(_db, _time);
        _sample = new SampleDataService(_db, _schedules, _time);
        _tony = _db.AddUser("Tony");
        _maria = _db.AddUser("Maria");
    }

    private async Task<int> GroupAsync()
    {
        var group = await _groups.CreateAsync(_tony.Id, "Family");
        await _groups.AddMemberAsync(group.Id, _tony.Id, "maria@example.com");
        return group.Id;
    }

    [Fact]
    public async Task Generates_four_weeks_for_everyone_and_every_rotation_exercise()
    {
        var groupId = await GroupAsync();

        var created = await _sample.GenerateAsync(groupId, _tony.Id);

        Assert.Equal(16, created); // 4 training days a week
        Assert.Equal(16, await _sample.CountAsync(groupId));

        await using var db = _db.CreateDbContext();
        var sessions = await db.Sessions.Where(s => s.IsSample).ToListAsync();
        Assert.All(sessions, s => Assert.Equal(SessionStatus.Completed, s.Status));
        Assert.All(sessions, s => Assert.True(s.StartedUtc < _time.GetUtcNow().UtcDateTime.Date));
        Assert.True(sessions.Min(s => s.StartedUtc) >= _time.GetUtcNow().UtcDateTime.Date.AddDays(-28));

        // The starter rotation was created and every exercise in it has history for both people.
        var schedule = Assert.Single(await _schedules.GetSchedulesAsync(_tony.Id));
        var exerciseIds = schedule.Days.SelectMany(d => d.Exercises).Select(e => e.ExerciseId).Distinct().ToList();
        foreach (var user in new[] { _tony, _maria })
        {
            var logged = await db.SetLogs.Where(l => l.UserId == user.Id).Select(l => l.ExerciseId).Distinct().ToListAsync();
            Assert.Equal(exerciseIds.Order(), logged.Order());
        }

        // Progress shows up in the stats: weights go up over the month for someone.
        var progress = (await _stats.GetGroupProgressAsync(groupId, _tony.Id))!;
        Assert.Equal(16, progress.Sessions.Count);
        Assert.Contains(progress.Summaries.Values, s => s.Stats.MaxWeight > s.Stats.MinWeight);
        Assert.All(progress.Summaries.Values, s => Assert.Equal(4, s.Stats.SessionCount));
    }

    [Fact]
    public async Task Removing_samples_keeps_real_workouts_and_the_rotation()
    {
        var groupId = await GroupAsync();
        var schedule = await _schedules.CreateStarterAsync(_tony.Id, groupId);
        var real = await _sessions.StartAsync(_tony.Id, schedule.Id, null, [_tony.Id]);
        var bench = (await _sessions.GetAsync(real, _tony.Id))!.Exercises[0];
        await _sessions.LogSetAsync(real, _tony.Id, bench.Id, _tony.Id, 8, 135);
        await _sessions.FinishAsync(real, _tony.Id);
        var nextBefore = ScheduleService.NextDay((await _schedules.GetAsync(schedule.Id, _tony.Id))!)!.Id;

        await _sample.GenerateAsync(groupId, _tony.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _sample.GenerateAsync(groupId, _tony.Id));

        Assert.Equal(16, await _sample.RemoveAsync(groupId, _tony.Id));
        Assert.Equal(0, await _sample.CountAsync(groupId));

        await using var db = _db.CreateDbContext();
        Assert.Equal(1, await db.Sessions.CountAsync());
        Assert.Equal(1, await db.SetLogs.CountAsync());
        Assert.Equal(nextBefore, ScheduleService.NextDay((await _schedules.GetAsync(schedule.Id, _tony.Id))!)!.Id);
    }

    [Fact]
    public async Task Longer_ranges_give_more_sessions_per_exercise()
    {
        var groupId = await GroupAsync();
        Assert.Equal(48, await _sample.GenerateAsync(groupId, _tony.Id, weeks: 12));
        var progress = (await _stats.GetGroupProgressAsync(groupId, _tony.Id))!;
        Assert.All(progress.Summaries.Values, s => Assert.Equal(12, s.Stats.SessionCount));
    }

    [Fact]
    public async Task Only_group_members_can_add_or_remove_samples()
    {
        var groupId = await GroupAsync();
        var stranger = _db.AddUser("Stranger");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _sample.GenerateAsync(groupId, stranger.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _sample.RemoveAsync(groupId, stranger.Id));
    }

    public void Dispose() => _db.Dispose();
}
