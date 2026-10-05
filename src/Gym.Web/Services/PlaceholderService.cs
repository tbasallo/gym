using System.Security.Cryptography;
using System.Text;
using Gym.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace Gym.Web.Services;

/// <summary>
/// People in a group who don't have an account yet (kids, guests). They are ordinary user rows
/// with a GUID id, no email and no password, so sets and history attach to them normally.
/// An invite link lets them claim that same row later, keeping everything.
/// </summary>
public sealed class PlaceholderService(IDbContextFactory<ApplicationDbContext> dbFactory, TimeProvider clock)
{
    public static readonly TimeSpan InviteLifetime = TimeSpan.FromDays(14);

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<ApplicationUser> AddAsync(int groupId, string actingUserId, string name)
    {
        var displayName = name.Trim();
        if (displayName.Length == 0)
        {
            throw new InvalidOperationException("Enter a name.");
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        await RequireMemberAsync(db, groupId, actingUserId);
        var actor = await db.Users.AsNoTracking().SingleAsync(u => u.Id == actingUserId);

        var person = new ApplicationUser
        {
            DisplayName = displayName.Length > 50 ? displayName[..50] : displayName,
            IsPlaceholder = true,
            WeightUnit = actor.WeightUnit,
            TimeZoneId = actor.TimeZoneId,
        };
        db.Users.Add(person);
        db.GroupMembers.Add(new GroupMember { GroupId = groupId, UserId = person.Id });
        await db.SaveChangesAsync();
        return person;
    }

    public async Task RenameAsync(int groupId, string actingUserId, string placeholderId, string name)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var person = await RequirePlaceholderAsync(db, groupId, actingUserId, placeholderId);
        var displayName = name.Trim();
        if (displayName.Length > 0)
        {
            person.DisplayName = displayName.Length > 50 ? displayName[..50] : displayName;
            await db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Creates a fresh one-time invite code (replacing any earlier one) and returns the
    /// relative registration link to share with the person.
    /// </summary>
    public async Task<string> CreateInviteLinkAsync(int groupId, string actingUserId, string placeholderId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var person = await RequirePlaceholderAsync(db, groupId, actingUserId, placeholderId);
        var code = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(24));
        person.ClaimCodeHash = Hash(code);
        person.ClaimCodeExpiresUtc = Now.Add(InviteLifetime);
        await db.SaveChangesAsync();
        return $"Account/Register?claim={Uri.EscapeDataString(person.Id)}&code={Uri.EscapeDataString(code)}";
    }

    /// <summary>The placeholder an invite link points at, or null if the link is invalid, used or expired.</summary>
    public async Task<ApplicationUser?> FindClaimableAsync(string? userId, string? code)
    {
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(code))
        {
            return null;
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        var person = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId);
        return IsValid(person, code) ? person : null;
    }

    /// <summary>
    /// Turns the placeholder into a real account with the given email and password. The user id
    /// (and with it every set, session and setting) stays the same.
    /// </summary>
    public async Task<(IdentityResult Result, ApplicationUser? User)> ClaimAsync(
        UserManager<ApplicationUser> userManager,
        IUserStore<ApplicationUser> userStore,
        string userId,
        string code,
        string displayName,
        string email,
        string password)
    {
        var user = await userManager.FindByIdAsync(userId);
        if (!IsValid(user, code))
        {
            return (IdentityResult.Failed(new IdentityError { Description = "This invite link is invalid or has expired." }), null);
        }

        if (!string.IsNullOrWhiteSpace(displayName))
        {
            user!.DisplayName = displayName.Trim();
        }

        await userStore.SetUserNameAsync(user!, email, CancellationToken.None);
        await ((IUserEmailStore<ApplicationUser>)userStore).SetEmailAsync(user!, email, CancellationToken.None);
        user!.IsPlaceholder = false;
        user.ClaimCodeHash = null;
        user.ClaimCodeExpiresUtc = null;

        // Validates the password and the (now set) user name/email, then saves everything at once.
        var result = await userManager.AddPasswordAsync(user, password);
        return (result, result.Succeeded ? user : null);
    }

    private bool IsValid(ApplicationUser? person, string code) =>
        person is { IsPlaceholder: true, ClaimCodeHash: { } hash, ClaimCodeExpiresUtc: { } expires }
        && expires > Now
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(hash), Encoding.ASCII.GetBytes(Hash(code)));

    private static string Hash(string code) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(code)));

    private static async Task RequireMemberAsync(ApplicationDbContext db, int groupId, string userId)
    {
        if (!await db.GroupMembers.AnyAsync(m => m.GroupId == groupId && m.UserId == userId))
        {
            throw new UnauthorizedAccessException("Not a member of this group.");
        }
    }

    private static async Task<ApplicationUser> RequirePlaceholderAsync(ApplicationDbContext db, int groupId, string actingUserId, string placeholderId)
    {
        await RequireMemberAsync(db, groupId, actingUserId);
        return await db.Users.SingleOrDefaultAsync(u => u.Id == placeholderId && u.IsPlaceholder
                && db.GroupMembers.Any(m => m.GroupId == groupId && m.UserId == u.Id))
            ?? throw new InvalidOperationException("That person already has an account or isn't in this group.");
    }
}
