using System.Security.Claims;
using Gym.Web.Data;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Gym.Web.Services;

/// <summary>The signed-in user for the current circuit, loaded once.</summary>
public sealed class UserContext(AuthenticationStateProvider auth, IDbContextFactory<ApplicationDbContext> dbFactory)
{
    private ApplicationUser? _user;
    private TimeZoneInfo? _zone;

    public async Task<ApplicationUser> GetUserAsync()
    {
        if (_user is not null)
        {
            return _user;
        }

        var state = await auth.GetAuthenticationStateAsync();
        var id = state.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("Not signed in.");
        await using var db = await dbFactory.CreateDbContextAsync();
        _user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == id);
        return _user;
    }

    public async Task<string> GetUserIdAsync() => (await GetUserAsync()).Id;

    public void Invalidate()
    {
        _user = null;
        _zone = null;
    }

    public TimeZoneInfo Zone
    {
        get
        {
            if (_zone is not null)
            {
                return _zone;
            }

            _zone = TimeZoneInfo.Utc;
            if (_user?.TimeZoneId is { Length: > 0 } tz && TimeZoneInfo.TryFindSystemTimeZoneById(tz, out var found))
            {
                _zone = found;
            }

            return _zone;
        }
    }

    public DateTime ToLocal(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);

    public string Unit => _user?.WeightUnit ?? "lb";
}
