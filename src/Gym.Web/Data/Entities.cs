using System.ComponentModel.DataAnnotations;

namespace Gym.Web.Data;

/// <summary>People who train together and share a rotation (e.g. a couple).</summary>
public class WorkoutGroup
{
    public int Id { get; set; }

    [MaxLength(100)]
    public string Name { get; set; } = "";

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public List<GroupMember> Members { get; set; } = [];
}

public class GroupMember
{
    public int GroupId { get; set; }
    public WorkoutGroup Group { get; set; } = null!;

    public string UserId { get; set; } = "";
    public ApplicationUser User { get; set; } = null!;

    public bool IsOwner { get; set; }
    public DateTime JoinedUtc { get; set; } = DateTime.UtcNow;
}

public enum BodyRegion
{
    Upper = 0,
    Lower = 1,
    Core = 2,
    FullBody = 3,
}

public class Exercise
{
    public int Id { get; set; }

    [MaxLength(120)]
    public string Name { get; set; } = "";

    public BodyRegion BodyRegion { get; set; }

    [MaxLength(100)]
    public string? Category { get; set; }

    [MaxLength(300)]
    public string? Muscles { get; set; }

    [MaxLength(200)]
    public string? Equipment { get; set; }

    public string? Description { get; set; }

    [MaxLength(500)]
    public string? ImageUrl { get; set; }

    /// <summary>Where details were pulled from (e.g. "wger"), and that source's id.</summary>
    [MaxLength(30)]
    public string? ExternalSource { get; set; }

    [MaxLength(50)]
    public string? ExternalId { get; set; }

    /// <summary>Overrides the progression engine's increment for this exercise.</summary>
    public decimal? WeightIncrement { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public List<ExerciseVideo> Videos { get; set; } = [];
}

public class ExerciseVideo
{
    public int Id { get; set; }

    public int ExerciseId { get; set; }
    public Exercise Exercise { get; set; } = null!;

    [MaxLength(20)]
    public string YouTubeId { get; set; } = "";

    [MaxLength(200)]
    public string Title { get; set; } = "";

    [MaxLength(100)]
    public string? Channel { get; set; }

    public bool IsPrimary { get; set; }

    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>An ordered rotation of days. Owned by a group or by a single user.</summary>
public class Schedule
{
    public int Id { get; set; }

    [MaxLength(100)]
    public string Name { get; set; } = "";

    public int? GroupId { get; set; }
    public WorkoutGroup? Group { get; set; }

    public string? OwnerUserId { get; set; }
    public ApplicationUser? OwnerUser { get; set; }

    /// <summary>The day that is up next. Null means the first day.</summary>
    public int? NextDayId { get; set; }

    public bool IsArchived { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public List<ScheduleDay> Days { get; set; } = [];
}

public class ScheduleDay
{
    public int Id { get; set; }

    public int ScheduleId { get; set; }
    public Schedule Schedule { get; set; } = null!;

    /// <summary>1-based position in the rotation.</summary>
    public int Order { get; set; }

    [MaxLength(100)]
    public string Name { get; set; } = "";

    public List<ScheduleDayExercise> Exercises { get; set; } = [];
}

public class ScheduleDayExercise
{
    public int Id { get; set; }

    public int ScheduleDayId { get; set; }
    public ScheduleDay ScheduleDay { get; set; } = null!;

    public int ExerciseId { get; set; }
    public Exercise Exercise { get; set; } = null!;

    public int Order { get; set; }

    public int TargetSets { get; set; } = 3;
    public int TargetReps { get; set; } = 10;

    /// <summary>Rest goal between sets; falls back to the user's default.</summary>
    public int? RestSeconds { get; set; }

    [MaxLength(300)]
    public string? Notes { get; set; }
}

/// <summary>A user's own numbers for an exercise: working weight and optional target overrides.</summary>
public class UserExerciseSetting
{
    public string UserId { get; set; } = "";
    public ApplicationUser User { get; set; } = null!;

    public int ExerciseId { get; set; }
    public Exercise Exercise { get; set; } = null!;

    public decimal? WorkingWeight { get; set; }
    public int? TargetSets { get; set; }
    public int? TargetReps { get; set; }
}

public enum SessionStatus
{
    Active = 0,
    Completed = 1,
    Cancelled = 2,
}

public class WorkoutSession
{
    public int Id { get; set; }

    public int? ScheduleId { get; set; }
    public Schedule? Schedule { get; set; }

    public int? ScheduleDayId { get; set; }

    public int? GroupId { get; set; }
    public WorkoutGroup? Group { get; set; }

    public string StartedByUserId { get; set; } = "";

    /// <summary>Snapshot of the day name ("Leg Day") so history survives schedule edits.</summary>
    [MaxLength(100)]
    public string Title { get; set; } = "";

    public SessionStatus Status { get; set; }

    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? EndedUtc { get; set; }

    public List<SessionParticipant> Participants { get; set; } = [];
    public List<SessionExercise> Exercises { get; set; } = [];
    public List<SetLog> Sets { get; set; } = [];
}

public class SessionParticipant
{
    public int SessionId { get; set; }
    public WorkoutSession Session { get; set; } = null!;

    public string UserId { get; set; } = "";
    public ApplicationUser User { get; set; } = null!;
}

/// <summary>An exercise planned for a session, copied from the schedule when the session starts.</summary>
public class SessionExercise
{
    public int Id { get; set; }

    public int SessionId { get; set; }
    public WorkoutSession Session { get; set; } = null!;

    public int ExerciseId { get; set; }
    public Exercise Exercise { get; set; } = null!;

    public int Order { get; set; }
    public int TargetSets { get; set; }
    public int TargetReps { get; set; }
    public int? RestSeconds { get; set; }
}

public class SetLog
{
    public int Id { get; set; }

    public int SessionId { get; set; }
    public WorkoutSession Session { get; set; } = null!;

    public int SessionExerciseId { get; set; }
    public SessionExercise SessionExercise { get; set; } = null!;

    public string UserId { get; set; } = "";
    public ApplicationUser User { get; set; } = null!;

    /// <summary>Denormalized for history queries.</summary>
    public int ExerciseId { get; set; }

    public int SetNumber { get; set; }

    public int Reps { get; set; }
    public decimal Weight { get; set; }

    /// <summary>The user's targets when the set was logged (snapshot for progression history).</summary>
    public int TargetSets { get; set; }
    public int TargetReps { get; set; }

    /// <summary>Set only when the set timer was used.</summary>
    public DateTime? StartedUtc { get; set; }

    /// <summary>Null while a timed set is in progress.</summary>
    public DateTime? CompletedUtc { get; set; }

    public bool IsInProgress => CompletedUtc is null;

    public TimeSpan? Duration => StartedUtc is { } s && CompletedUtc is { } c ? c - s : null;
}

/// <summary>Per-user knobs for the progression rules engine.</summary>
public class ProgressionSettings
{
    [Key]
    public string UserId { get; set; } = "";

    /// <summary>Sessions in a row hitting every target before suggesting more weight.</summary>
    public int SessionsToProgress { get; set; } = 2;

    public decimal UpperIncrement { get; set; } = 5;
    public decimal LowerIncrement { get; set; } = 10;
    public decimal CoreIncrement { get; set; } = 5;

    /// <summary>Sessions in a row missing targets at the same weight before suggesting a deload.</summary>
    public int MissesBeforeDeload { get; set; } = 3;

    public int DeloadPercent { get; set; } = 10;

    /// <summary>Smallest weight step available (plates/dumbbells); suggestions are rounded to it.</summary>
    public decimal RoundTo { get; set; } = 2.5m;

    /// <summary>Days without training an exercise before suggesting a lighter restart.</summary>
    public int LayoffDays { get; set; } = 21;
}
