using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Gym.Web.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<WorkoutGroup> Groups => Set<WorkoutGroup>();
    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();
    public DbSet<Exercise> Exercises => Set<Exercise>();
    public DbSet<ExerciseVideo> ExerciseVideos => Set<ExerciseVideo>();
    public DbSet<Schedule> Schedules => Set<Schedule>();
    public DbSet<ScheduleDay> ScheduleDays => Set<ScheduleDay>();
    public DbSet<ScheduleDayExercise> ScheduleDayExercises => Set<ScheduleDayExercise>();
    public DbSet<UserExerciseSetting> UserExerciseSettings => Set<UserExerciseSetting>();
    public DbSet<WorkoutSession> Sessions => Set<WorkoutSession>();
    public DbSet<SessionParticipant> SessionParticipants => Set<SessionParticipant>();
    public DbSet<SessionExercise> SessionExercises => Set<SessionExercise>();
    public DbSet<SetLog> SetLogs => Set<SetLog>();
    public DbSet<ProgressionSettings> ProgressionSettings => Set<ProgressionSettings>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<GroupMember>(e =>
        {
            e.HasKey(m => new { m.GroupId, m.UserId });
            e.HasOne(m => m.Group).WithMany(g => g.Members).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(m => m.User).WithMany().OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Exercise>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.WeightIncrement).HasPrecision(7, 2);
        });

        builder.Entity<ExerciseVideo>(e =>
        {
            e.HasOne(v => v.Exercise).WithMany(x => x.Videos).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(v => new { v.ExerciseId, v.YouTubeId }).IsUnique();
        });

        builder.Entity<Schedule>(e =>
        {
            e.HasOne(s => s.Group).WithMany().OnDelete(DeleteBehavior.Restrict);
            e.HasOne(s => s.OwnerUser).WithMany().OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<ScheduleDay>(e =>
        {
            e.HasOne(d => d.Schedule).WithMany(s => s.Days).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(d => new { d.ScheduleId, d.Order });
        });

        builder.Entity<ScheduleDayExercise>(e =>
        {
            e.HasOne(x => x.ScheduleDay).WithMany(d => d.Exercises).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Exercise).WithMany().OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<UserExerciseSetting>(e =>
        {
            e.HasKey(x => new { x.UserId, x.ExerciseId });
            e.Property(x => x.WorkingWeight).HasPrecision(7, 2);
            e.HasOne(x => x.User).WithMany().OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Exercise).WithMany().OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<WorkoutSession>(e =>
        {
            e.HasOne(s => s.Schedule).WithMany().OnDelete(DeleteBehavior.Restrict);
            e.HasOne(s => s.Group).WithMany().OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(s => new { s.Status, s.GroupId });
        });

        builder.Entity<SessionParticipant>(e =>
        {
            e.HasKey(p => new { p.SessionId, p.UserId });
            e.HasOne(p => p.Session).WithMany(s => s.Participants).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(p => p.User).WithMany().OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<SessionExercise>(e =>
        {
            e.HasOne(x => x.Session).WithMany(s => s.Exercises).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Exercise).WithMany().OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<SetLog>(e =>
        {
            e.Property(x => x.Weight).HasPrecision(7, 2);
            e.HasOne(x => x.Session).WithMany(s => s.Sets).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.SessionExercise).WithMany().OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.User).WithMany().OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.UserId, x.ExerciseId, x.CompletedUtc });
        });

        builder.Entity<ProgressionSettings>(e =>
        {
            e.Property(x => x.UpperIncrement).HasPrecision(7, 2);
            e.Property(x => x.LowerIncrement).HasPrecision(7, 2);
            e.Property(x => x.CoreIncrement).HasPrecision(7, 2);
            e.Property(x => x.RoundTo).HasPrecision(7, 2);
        });
    }
}
