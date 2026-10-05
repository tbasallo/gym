using Gym.Web.Data;
using Gym.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Gym.Tests;

public sealed class PlaceholderTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeTime _time = new(new DateTimeOffset(2026, 10, 6, 18, 0, 0, TimeSpan.Zero));
    private readonly ServiceProvider _identity;
    private readonly GroupService _groups;
    private readonly PlaceholderService _placeholders;
    private readonly SessionService _sessions;
    private readonly ScheduleService _schedules;
    private readonly ApplicationUser _tony;

    public PlaceholderTests()
    {
        _groups = new GroupService(_db);
        _placeholders = new PlaceholderService(_db, _time);
        _sessions = new SessionService(_db, new LiveUpdates(), _time);
        _schedules = new ScheduleService(_db);
        _tony = _db.AddUser("Tony");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => _db.CreateDbContext());
        services.AddIdentityCore<ApplicationUser>(o => o.User.RequireUniqueEmail = true)
            .AddEntityFrameworkStores<ApplicationDbContext>();
        _identity = services.BuildServiceProvider();
    }

    private async Task<(int GroupId, ApplicationUser Kid)> GroupWithKidAsync()
    {
        var group = await _groups.CreateAsync(_tony.Id, "Family");
        var kid = await _placeholders.AddAsync(group.Id, _tony.Id, "  Sofia ");
        return (group.Id, kid);
    }

    private static string CodeFrom(string link) =>
        Uri.UnescapeDataString(link[(link.IndexOf("code=", StringComparison.Ordinal) + 5)..]);

    private async Task<(IdentityResult Result, ApplicationUser? User)> ClaimAsync(string userId, string code, string email)
    {
        using var scope = _identity.CreateScope();
        return await _placeholders.ClaimAsync(
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(),
            scope.ServiceProvider.GetRequiredService<IUserStore<ApplicationUser>>(),
            userId, code, "Sofia B", email, "Passw0rd!x");
    }

    [Fact]
    public async Task Placeholder_gets_a_guid_and_joins_the_group()
    {
        var (groupId, kid) = await GroupWithKidAsync();

        Assert.True(Guid.TryParse(kid.Id, out _));
        Assert.True(kid.IsPlaceholder);
        Assert.Equal("Sofia", kid.DisplayName);
        Assert.Null(kid.Email);

        var group = Assert.Single(await _groups.GetGroupsAsync(_tony.Id));
        Assert.Equal(groupId, group.Id);
        Assert.Contains(group.Members, m => m.UserId == kid.Id);
        Assert.Contains(await _groups.GetTeammatesAsync(_tony.Id), u => u.Id == kid.Id);
    }

    [Fact]
    public async Task Claiming_keeps_the_same_id_and_history()
    {
        var (groupId, kid) = await GroupWithKidAsync();
        var schedule = await _schedules.CreateStarterAsync(_tony.Id, groupId);
        var sessionId = await _sessions.StartAsync(_tony.Id, schedule.Id, null, [_tony.Id, kid.Id]);
        var bench = (await _sessions.GetAsync(sessionId, _tony.Id))!.Exercises[0];
        await _sessions.LogSetAsync(sessionId, _tony.Id, bench.Id, kid.Id, 10, 30);
        await _sessions.FinishAsync(sessionId, _tony.Id);

        var link = await _placeholders.CreateInviteLinkAsync(groupId, _tony.Id, kid.Id);
        Assert.StartsWith($"Account/Register?claim={kid.Id}&code=", link);
        Assert.NotNull(await _placeholders.FindClaimableAsync(kid.Id, CodeFrom(link)));

        var (result, user) = await ClaimAsync(kid.Id, CodeFrom(link), "sofia@example.com");

        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(e => e.Description)));
        Assert.Equal(kid.Id, user!.Id);
        await using var db = _db.CreateDbContext();
        var saved = await db.Users.SingleAsync(u => u.Id == kid.Id);
        Assert.False(saved.IsPlaceholder);
        Assert.Equal("sofia@example.com", saved.Email);
        Assert.Equal("SOFIA@EXAMPLE.COM", saved.NormalizedEmail);
        Assert.Equal("Sofia B", saved.DisplayName);
        Assert.NotNull(saved.PasswordHash);
        Assert.Null(saved.ClaimCodeHash);
        Assert.Equal(1, await db.SetLogs.CountAsync(s => s.UserId == kid.Id));

        // The link is single use.
        Assert.Null(await _placeholders.FindClaimableAsync(kid.Id, CodeFrom(link)));
    }

    [Fact]
    public async Task Wrong_or_expired_codes_are_rejected()
    {
        var (groupId, kid) = await GroupWithKidAsync();
        var link = await _placeholders.CreateInviteLinkAsync(groupId, _tony.Id, kid.Id);

        Assert.Null(await _placeholders.FindClaimableAsync(kid.Id, "not-the-code"));
        Assert.Null(await _placeholders.FindClaimableAsync(_tony.Id, CodeFrom(link)));
        Assert.False((await ClaimAsync(kid.Id, "not-the-code", "sofia@example.com")).Result.Succeeded);

        _time.Advance(PlaceholderService.InviteLifetime + TimeSpan.FromMinutes(1));
        Assert.Null(await _placeholders.FindClaimableAsync(kid.Id, CodeFrom(link)));
    }

    [Fact]
    public async Task New_invite_replaces_the_old_one()
    {
        var (groupId, kid) = await GroupWithKidAsync();
        var first = await _placeholders.CreateInviteLinkAsync(groupId, _tony.Id, kid.Id);
        var second = await _placeholders.CreateInviteLinkAsync(groupId, _tony.Id, kid.Id);

        Assert.Null(await _placeholders.FindClaimableAsync(kid.Id, CodeFrom(first)));
        Assert.NotNull(await _placeholders.FindClaimableAsync(kid.Id, CodeFrom(second)));
    }

    [Fact]
    public async Task Claim_with_an_email_already_in_use_fails_and_keeps_the_placeholder()
    {
        var (groupId, kid) = await GroupWithKidAsync();
        var link = await _placeholders.CreateInviteLinkAsync(groupId, _tony.Id, kid.Id);

        var (result, _) = await ClaimAsync(kid.Id, CodeFrom(link), "tony@example.com");

        Assert.False(result.Succeeded);
        Assert.NotNull(await _placeholders.FindClaimableAsync(kid.Id, CodeFrom(link)));
    }

    [Fact]
    public async Task Only_group_members_can_add_or_invite()
    {
        var (groupId, kid) = await GroupWithKidAsync();
        var stranger = _db.AddUser("Stranger");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _placeholders.AddAsync(groupId, stranger.Id, "Eve"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _placeholders.CreateInviteLinkAsync(groupId, stranger.Id, kid.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _placeholders.CreateInviteLinkAsync(groupId, _tony.Id, _tony.Id));
    }

    public void Dispose()
    {
        _identity.Dispose();
        _db.Dispose();
    }
}
