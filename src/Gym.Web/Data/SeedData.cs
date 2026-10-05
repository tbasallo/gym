using Microsoft.EntityFrameworkCore;

namespace Gym.Web.Data;

public static class SeedData
{
    private static readonly Exercise[] Exercises =
    [
        // Lower body
        New("Barbell Back Squat", BodyRegion.Lower, "Legs", "Quadriceps, Glutes, Hamstrings", "Barbell, Squat rack",
            "Bar on upper back, feet shoulder width. Brace, sit hips back and down until thighs are at least parallel, then drive up through the whole foot."),
        New("Goblet Squat", BodyRegion.Lower, "Legs", "Quadriceps, Glutes", "Dumbbell, Kettlebell",
            "Hold a dumbbell at your chest, elbows inside the knees. Squat down with an upright torso and stand back up."),
        New("Leg Press", BodyRegion.Lower, "Legs", "Quadriceps, Glutes", "Leg press machine",
            "Feet hip width on the platform. Lower until knees reach about 90 degrees without the lower back lifting, then press back up without locking out."),
        New("Romanian Deadlift", BodyRegion.Lower, "Legs", "Hamstrings, Glutes, Lower back", "Barbell, Dumbbells",
            "Soft knees, push hips back and slide the weight down the thighs until you feel a hamstring stretch. Keep a flat back and drive hips forward to stand."),
        New("Deadlift", BodyRegion.FullBody, "Legs", "Hamstrings, Glutes, Back", "Barbell",
            "Bar over mid-foot. Hinge to grip, flatten the back, push the floor away and stand tall. Lower under control."),
        New("Walking Lunge", BodyRegion.Lower, "Legs", "Quadriceps, Glutes", "Dumbbells",
            "Step forward and lower the back knee toward the floor, front knee over the ankle. Push through the front heel into the next step."),
        New("Leg Extension", BodyRegion.Lower, "Legs", "Quadriceps", "Leg extension machine",
            "Pad above the ankles. Extend the knees to straighten the legs, squeeze, then lower slowly."),
        New("Lying Leg Curl", BodyRegion.Lower, "Legs", "Hamstrings", "Leg curl machine",
            "Pad just above the heels. Curl heels toward glutes keeping hips down, then lower slowly."),
        New("Standing Calf Raise", BodyRegion.Lower, "Calves", "Calves", "Machine, Dumbbells",
            "Balls of the feet on an edge. Rise as high as possible, pause, then lower into a full stretch."),
        New("Hip Thrust", BodyRegion.Lower, "Legs", "Glutes, Hamstrings", "Barbell, Bench",
            "Upper back on a bench, bar over the hips. Drive hips up until the body is flat from knees to shoulders, squeeze glutes, lower."),

        // Upper body: push
        New("Barbell Bench Press", BodyRegion.Upper, "Chest", "Chest, Triceps, Front delts", "Barbell, Bench",
            "Shoulder blades pinched, feet planted. Lower the bar to mid-chest with elbows about 45 degrees, press back up."),
        New("Incline Dumbbell Press", BodyRegion.Upper, "Chest", "Upper chest, Front delts, Triceps", "Dumbbells, Incline bench",
            "Bench at 30-45 degrees. Lower dumbbells to the sides of the upper chest, press up and slightly together."),
        New("Dumbbell Chest Fly", BodyRegion.Upper, "Chest", "Chest", "Dumbbells, Bench",
            "Slight bend in the elbows. Open the arms wide until you feel a chest stretch, then hug back to the top."),
        New("Overhead Press", BodyRegion.Upper, "Shoulders", "Shoulders, Triceps", "Barbell",
            "Bar at the collarbone, glutes tight. Press straight overhead, moving the head back then through, lock out over mid-foot."),
        New("Dumbbell Lateral Raise", BodyRegion.Upper, "Shoulders", "Side delts", "Dumbbells",
            "Slight lean forward, lead with the elbows and raise to shoulder height. Lower slowly."),
        New("Triceps Pushdown", BodyRegion.Upper, "Arms", "Triceps", "Cable machine",
            "Elbows pinned at your sides. Push the handle down until arms are straight, squeeze, return under control."),
        New("Overhead Triceps Extension", BodyRegion.Upper, "Arms", "Triceps", "Dumbbell, Cable",
            "Hold the weight overhead, lower it behind the head by bending the elbows, then extend back up."),

        // Upper body: pull
        New("Lat Pulldown", BodyRegion.Upper, "Back", "Lats, Biceps", "Cable machine",
            "Grip slightly wider than shoulders. Pull the bar to the upper chest driving elbows down, control it back up."),
        New("Seated Cable Row", BodyRegion.Upper, "Back", "Mid back, Lats, Biceps", "Cable machine",
            "Sit tall, pull the handle to the stomach squeezing shoulder blades together, then reach forward under control."),
        New("Bent-Over Barbell Row", BodyRegion.Upper, "Back", "Back, Biceps", "Barbell",
            "Hinge to about 45 degrees with a flat back. Row the bar to the lower ribs, lower with control."),
        New("One-Arm Dumbbell Row", BodyRegion.Upper, "Back", "Lats, Mid back", "Dumbbell, Bench",
            "Hand and knee on a bench. Row the dumbbell to the hip, keeping the torso still."),
        New("Face Pull", BodyRegion.Upper, "Shoulders", "Rear delts, Upper back", "Cable machine, Rope",
            "Rope at face height. Pull toward the forehead, elbows high, rotating hands back."),
        New("Dumbbell Biceps Curl", BodyRegion.Upper, "Arms", "Biceps", "Dumbbells",
            "Elbows at your sides. Curl up while turning the palms up, squeeze, lower slowly."),
        New("Hammer Curl", BodyRegion.Upper, "Arms", "Biceps, Forearms", "Dumbbells",
            "Palms facing each other. Curl up without swinging, lower slowly."),

        // Core
        New("Cable Crunch", BodyRegion.Core, "Abs", "Abs", "Cable machine, Rope",
            "Kneel facing the cable, rope by your head. Crunch down by curling the spine, hips still."),
        New("Hanging Knee Raise", BodyRegion.Core, "Abs", "Abs, Hip flexors", "Pull-up bar",
            "Hang from a bar, raise knees toward the chest by curling the pelvis, lower without swinging."),
        New("Weighted Russian Twist", BodyRegion.Core, "Abs", "Obliques", "Dumbbell, Plate",
            "Sit leaning back with feet up or down, rotate the weight side to side under control."),
    ];

    /// <summary>Day name, then (exercise, sets, reps).</summary>
    public static readonly (string Day, (string Exercise, int Sets, int Reps)[] Exercises)[] StarterRotation =
    [
        ("Chest & Triceps", [("Barbell Bench Press", 3, 8), ("Incline Dumbbell Press", 3, 10), ("Dumbbell Chest Fly", 3, 12), ("Triceps Pushdown", 3, 12)]),
        ("Back & Biceps", [("Lat Pulldown", 3, 10), ("Seated Cable Row", 3, 10), ("One-Arm Dumbbell Row", 3, 10), ("Dumbbell Biceps Curl", 3, 12)]),
        ("Leg Day", [("Barbell Back Squat", 3, 8), ("Romanian Deadlift", 3, 10), ("Leg Press", 3, 12), ("Standing Calf Raise", 3, 15)]),
        ("Shoulders & Core", [("Overhead Press", 3, 8), ("Dumbbell Lateral Raise", 3, 12), ("Face Pull", 3, 15), ("Cable Crunch", 3, 15)]),
    ];

    /// <summary>Adds the starter exercise library to an empty database.</summary>
    public static async Task SeedAsync(ApplicationDbContext db, CancellationToken cancellationToken = default)
    {
        if (await db.Exercises.AnyAsync(cancellationToken))
        {
            return;
        }

        db.Exercises.AddRange(Exercises.Select(Clone));
        await db.SaveChangesAsync(cancellationToken);
    }

    private static Exercise New(string name, BodyRegion region, string category, string muscles, string equipment, string description) =>
        new()
        {
            Name = name,
            BodyRegion = region,
            Category = category,
            Muscles = muscles,
            Equipment = equipment,
            Description = description,
        };

    private static Exercise Clone(Exercise e) =>
        New(e.Name, e.BodyRegion, e.Category!, e.Muscles!, e.Equipment!, e.Description!);
}
