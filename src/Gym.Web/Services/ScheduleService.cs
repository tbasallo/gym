using Gym.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gym.Web.Services;

public sealed class ScheduleService(IDbContextFactory<ApplicationDbContext> dbFactory)
{
    public async Task<List<Schedule>> GetSchedulesAsync(string userId, bool includeArchived = false)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var schedules = await db.VisibleSchedules(userId).AsNoTracking()
            .Where(s => includeArchived || !s.IsArchived)
            .Include(s => s.Group).ThenInclude(g => g!.Members).ThenInclude(m => m.User)
            .Include(s => s.Days).ThenInclude(d => d.Exercises).ThenInclude(e => e.Exercise)
            .AsSplitQuery()
            .OrderBy(s => s.GroupId == null).ThenBy(s => s.Name)
            .ToListAsync();
        schedules.ForEach(Sort);
        return schedules;
    }

    public async Task<Schedule?> GetAsync(int scheduleId, string userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var schedule = await db.VisibleSchedules(userId).AsNoTracking()
            .Include(s => s.Group).ThenInclude(g => g!.Members).ThenInclude(m => m.User)
            .Include(s => s.Days).ThenInclude(d => d.Exercises).ThenInclude(e => e.Exercise)
            .AsSplitQuery()
            .SingleOrDefaultAsync(s => s.Id == scheduleId);
        if (schedule is not null)
        {
            Sort(schedule);
        }

        return schedule;
    }

    public async Task<Schedule> CreateAsync(string userId, string name, int? groupId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (groupId is not null && !await db.GroupMembers.AnyAsync(m => m.GroupId == groupId && m.UserId == userId))
        {
            throw new UnauthorizedAccessException("Not a member of this group.");
        }

        var schedule = new Schedule
        {
            Name = name.Trim(),
            GroupId = groupId,
            OwnerUserId = groupId is null ? userId : null,
        };
        db.Schedules.Add(schedule);
        await db.SaveChangesAsync();
        return schedule;
    }

    public Task RenameAsync(int scheduleId, string userId, string name) =>
        EditScheduleAsync(scheduleId, userId, (_, s) => s.Name = name.Trim());

    public Task SetArchivedAsync(int scheduleId, string userId, bool archived) =>
        EditScheduleAsync(scheduleId, userId, (_, s) => s.IsArchived = archived);

    public Task SetNextDayAsync(int scheduleId, string userId, int dayId) =>
        EditScheduleAsync(scheduleId, userId, (_, s) =>
        {
            if (s.Days.Any(d => d.Id == dayId))
            {
                s.NextDayId = dayId;
            }
        });

    public Task AddDayAsync(int scheduleId, string userId, string name) =>
        EditScheduleAsync(scheduleId, userId, (_, s) =>
            s.Days.Add(new ScheduleDay { Name = name.Trim(), Order = s.Days.Count + 1 }));

    public Task RenameDayAsync(int scheduleId, string userId, int dayId, string name) =>
        EditScheduleAsync(scheduleId, userId, (_, s) => Day(s, dayId).Name = name.Trim());

    public Task DeleteDayAsync(int scheduleId, string userId, int dayId) =>
        EditScheduleAsync(scheduleId, userId, (db, s) =>
        {
            var day = Day(s, dayId);
            if (s.NextDayId == dayId)
            {
                s.NextDayId = DayAfter(s, dayId);
                if (s.NextDayId == dayId)
                {
                    s.NextDayId = null;
                }
            }

            s.Days.Remove(day);
            db.ScheduleDays.Remove(day);
            Renumber(s.Days);
        });

    public Task MoveDayAsync(int scheduleId, string userId, int dayId, int delta) =>
        EditScheduleAsync(scheduleId, userId, (_, s) => Move(s.Days, Day(s, dayId), delta));

    public Task AddExerciseAsync(int scheduleId, string userId, int dayId, int exerciseId, int sets, int reps) =>
        EditScheduleAsync(scheduleId, userId, (_, s) =>
        {
            var day = Day(s, dayId);
            day.Exercises.Add(new ScheduleDayExercise
            {
                ExerciseId = exerciseId,
                TargetSets = Math.Max(1, sets),
                TargetReps = Math.Max(1, reps),
                Order = day.Exercises.Count + 1,
            });
        });

    public Task UpdateExerciseAsync(int scheduleId, string userId, int dayId, int dayExerciseId, int sets, int reps, int? restSeconds, string? notes) =>
        EditScheduleAsync(scheduleId, userId, (_, s) =>
        {
            var item = Day(s, dayId).Exercises.Single(e => e.Id == dayExerciseId);
            item.TargetSets = Math.Max(1, sets);
            item.TargetReps = Math.Max(1, reps);
            item.RestSeconds = restSeconds is > 0 ? restSeconds : null;
            item.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        });

    public Task RemoveExerciseAsync(int scheduleId, string userId, int dayId, int dayExerciseId) =>
        EditScheduleAsync(scheduleId, userId, (db, s) =>
        {
            var day = Day(s, dayId);
            var item = day.Exercises.Single(e => e.Id == dayExerciseId);
            day.Exercises.Remove(item);
            db.ScheduleDayExercises.Remove(item);
            Renumber(day.Exercises);
        });

    public Task MoveExerciseAsync(int scheduleId, string userId, int dayId, int dayExerciseId, int delta) =>
        EditScheduleAsync(scheduleId, userId, (_, s) =>
        {
            var day = Day(s, dayId);
            Move(day.Exercises, day.Exercises.Single(e => e.Id == dayExerciseId), delta);
        });

    /// <summary>Creates a 4-day starter rotation using the seeded exercises.</summary>
    public async Task<Schedule> CreateStarterAsync(string userId, int? groupId)
    {
        var schedule = await CreateAsync(userId, "Starter rotation", groupId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var byName = await db.Exercises.ToDictionaryAsync(e => e.Name, e => e.Id);
        var tracked = await db.Schedules.Include(s => s.Days).SingleAsync(s => s.Id == schedule.Id);

        foreach (var (dayName, exercises) in SeedData.StarterRotation)
        {
            var day = new ScheduleDay { Name = dayName, Order = tracked.Days.Count + 1 };
            foreach (var (name, sets, reps) in exercises)
            {
                if (byName.TryGetValue(name, out var id))
                {
                    day.Exercises.Add(new ScheduleDayExercise
                    {
                        ExerciseId = id,
                        TargetSets = sets,
                        TargetReps = reps,
                        Order = day.Exercises.Count + 1,
                    });
                }
            }

            tracked.Days.Add(day);
        }

        await db.SaveChangesAsync();
        return tracked;
    }

    /// <summary>The day that is up next in the rotation.</summary>
    public static ScheduleDay? NextDay(Schedule schedule)
    {
        var days = schedule.Days.OrderBy(d => d.Order).ToList();
        return days.FirstOrDefault(d => d.Id == schedule.NextDayId) ?? days.FirstOrDefault();
    }

    /// <summary>The day following <paramref name="dayId"/>, wrapping to the first day.</summary>
    public static int? DayAfter(Schedule schedule, int dayId)
    {
        var days = schedule.Days.OrderBy(d => d.Order).ToList();
        if (days.Count == 0)
        {
            return null;
        }

        var index = days.FindIndex(d => d.Id == dayId);
        return days[(index + 1) % days.Count].Id;
    }

    private async Task EditScheduleAsync(int scheduleId, string userId, Action<ApplicationDbContext, Schedule> edit)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var schedule = await db.VisibleSchedules(userId)
            .Include(s => s.Days).ThenInclude(d => d.Exercises)
            .SingleOrDefaultAsync(s => s.Id == scheduleId)
            ?? throw new UnauthorizedAccessException("Schedule not found.");
        edit(db, schedule);
        await db.SaveChangesAsync();
    }

    private static ScheduleDay Day(Schedule schedule, int dayId) =>
        schedule.Days.SingleOrDefault(d => d.Id == dayId) ?? throw new InvalidOperationException("Day not found.");

    private static void Sort(Schedule schedule)
    {
        schedule.Days = schedule.Days.OrderBy(d => d.Order).ToList();
        foreach (var day in schedule.Days)
        {
            day.Exercises = day.Exercises.OrderBy(e => e.Order).ToList();
        }
    }

    private static void Renumber(IEnumerable<ScheduleDay> days)
    {
        var i = 1;
        foreach (var day in days.OrderBy(d => d.Order))
        {
            day.Order = i++;
        }
    }

    private static void Renumber(IEnumerable<ScheduleDayExercise> items)
    {
        var i = 1;
        foreach (var item in items.OrderBy(d => d.Order))
        {
            item.Order = i++;
        }
    }

    private static void Move(List<ScheduleDay> days, ScheduleDay day, int delta) =>
        Swap(days.OrderBy(d => d.Order).ToList(), day, delta, d => d.Order, (d, o) => d.Order = o);

    private static void Move(List<ScheduleDayExercise> items, ScheduleDayExercise item, int delta) =>
        Swap(items.OrderBy(d => d.Order).ToList(), item, delta, d => d.Order, (d, o) => d.Order = o);

    private static void Swap<T>(List<T> ordered, T item, int delta, Func<T, int> get, Action<T, int> set)
    {
        var index = ordered.IndexOf(item);
        var target = index + Math.Sign(delta);
        if (index < 0 || target < 0 || target >= ordered.Count)
        {
            return;
        }

        var other = ordered[target];
        var order = get(item);
        set(item, get(other));
        set(other, order);
    }
}
