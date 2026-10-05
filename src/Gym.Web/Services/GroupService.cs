using Gym.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gym.Web.Services;

public sealed class GroupService(IDbContextFactory<ApplicationDbContext> dbFactory)
{
    public async Task<List<WorkoutGroup>> GetGroupsAsync(string userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Groups.AsNoTracking()
            .Where(g => g.Members.Any(m => m.UserId == userId))
            .Include(g => g.Members).ThenInclude(m => m.User)
            .OrderBy(g => g.Name)
            .ToListAsync();
    }

    public async Task<WorkoutGroup> CreateAsync(string userId, string name)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var group = new WorkoutGroup
        {
            Name = name.Trim(),
            Members = [new GroupMember { UserId = userId, IsOwner = true }],
        };
        db.Groups.Add(group);
        await db.SaveChangesAsync();
        return group;
    }

    public async Task RenameAsync(int groupId, string userId, string name)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var group = await RequireMemberAsync(db, groupId, userId);
        group.Name = name.Trim();
        await db.SaveChangesAsync();
    }

    /// <summary>Adds an existing account to the group. Returns an error message, or null on success.</summary>
    public async Task<string?> AddMemberAsync(int groupId, string actingUserId, string email)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await RequireMemberAsync(db, groupId, actingUserId);

        var normalized = email.Trim().ToUpperInvariant();
        var user = await db.Users.SingleOrDefaultAsync(u => u.NormalizedEmail == normalized);
        if (user is null)
        {
            return "No account with that email. They need to register first.";
        }

        if (await db.GroupMembers.AnyAsync(m => m.GroupId == groupId && m.UserId == user.Id))
        {
            return "Already in the group.";
        }

        db.GroupMembers.Add(new GroupMember { GroupId = groupId, UserId = user.Id });
        await db.SaveChangesAsync();
        return null;
    }

    public async Task RemoveMemberAsync(int groupId, string actingUserId, string userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await RequireMemberAsync(db, groupId, actingUserId);
        var member = await db.GroupMembers.SingleOrDefaultAsync(m => m.GroupId == groupId && m.UserId == userId);
        if (member is not null)
        {
            db.GroupMembers.Remove(member);
            await db.SaveChangesAsync();
        }
    }

    /// <summary>The user plus everyone who shares a group with them.</summary>
    public async Task<List<ApplicationUser>> GetTeammatesAsync(string userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var groupIds = db.GroupMembers.Where(m => m.UserId == userId).Select(m => m.GroupId);
        return await db.Users.AsNoTracking()
            .Where(u => u.Id == userId || db.GroupMembers.Any(m => m.UserId == u.Id && groupIds.Contains(m.GroupId)))
            .OrderBy(u => u.DisplayName)
            .ToListAsync();
    }

    private static async Task<WorkoutGroup> RequireMemberAsync(ApplicationDbContext db, int groupId, string userId) =>
        await db.Groups.SingleOrDefaultAsync(g => g.Id == groupId && g.Members.Any(m => m.UserId == userId))
            ?? throw new UnauthorizedAccessException("Not a member of this group.");
}
