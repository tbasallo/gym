using Gym.Web.Data;

namespace Gym.Web.Services;

/// <summary>Row-level visibility: a user sees their own data and their groups' data.</summary>
internal static class Access
{
    public static IQueryable<Schedule> VisibleSchedules(this ApplicationDbContext db, string userId) =>
        db.Schedules.Where(s => s.OwnerUserId == userId
            || (s.GroupId != null && db.GroupMembers.Any(m => m.GroupId == s.GroupId && m.UserId == userId)));

    public static IQueryable<WorkoutSession> VisibleSessions(this ApplicationDbContext db, string userId) =>
        db.Sessions.Where(s => s.StartedByUserId == userId
            || s.Participants.Any(p => p.UserId == userId)
            || (s.GroupId != null && db.GroupMembers.Any(m => m.GroupId == s.GroupId && m.UserId == userId)));
}
