using Gym.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gym.Web.Services;

/// <summary>
/// Keeps the public site from accepting strangers. If Registration:AllowedEmails is set,
/// only those addresses may register; otherwise only the very first account may.
/// </summary>
public sealed class RegistrationPolicy(IConfiguration config, IDbContextFactory<ApplicationDbContext> dbFactory)
{
    public IReadOnlyList<string> AllowedEmails =>
        (config["Registration:AllowedEmails"] ?? "")
            .Split([',', ';', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public async Task<bool> IsOpenAsync()
    {
        if (AllowedEmails.Count > 0)
        {
            return true;
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        return !await db.Users.AnyAsync();
    }

    public async Task<bool> CanRegisterAsync(string email)
    {
        var allowed = AllowedEmails;
        if (allowed.Count > 0)
        {
            return allowed.Contains(email.Trim(), StringComparer.OrdinalIgnoreCase);
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        return !await db.Users.AnyAsync();
    }
}
