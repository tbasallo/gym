using Gym.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gym.Web.Services;

public sealed class ExerciseService(IDbContextFactory<ApplicationDbContext> dbFactory, WgerClient wger, YouTubeClient youTube)
{
    public async Task<List<Exercise>> SearchAsync(string? term = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var query = db.Exercises.AsNoTracking().Include(e => e.Videos).AsQueryable();
        if (!string.IsNullOrWhiteSpace(term))
        {
            var t = term.Trim();
            query = query.Where(e => e.Name.Contains(t) || (e.Muscles != null && e.Muscles.Contains(t))
                || (e.Category != null && e.Category.Contains(t)));
        }

        return await query.OrderBy(e => e.Name).ToListAsync();
    }

    public async Task<Exercise?> GetAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var exercise = await db.Exercises.AsNoTracking().Include(e => e.Videos).SingleOrDefaultAsync(e => e.Id == id);
        if (exercise is not null)
        {
            exercise.Videos = exercise.Videos.OrderByDescending(v => v.IsPrimary).ThenBy(v => v.AddedUtc).ToList();
        }

        return exercise;
    }

    /// <summary>Creates or updates an exercise. Returns an error message, or null on success.</summary>
    public async Task<string?> SaveAsync(Exercise input)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var name = input.Name.Trim();
        if (name.Length == 0)
        {
            return "Name is required.";
        }

        if (await db.Exercises.AnyAsync(e => e.Name == name && e.Id != input.Id))
        {
            return "An exercise with that name already exists.";
        }

        var exercise = input.Id == 0 ? new Exercise() : await db.Exercises.SingleAsync(e => e.Id == input.Id);
        exercise.Name = name;
        exercise.BodyRegion = input.BodyRegion;
        exercise.Category = Clean(input.Category);
        exercise.Muscles = Clean(input.Muscles);
        exercise.Equipment = Clean(input.Equipment);
        exercise.Description = Clean(input.Description);
        exercise.ImageUrl = Clean(input.ImageUrl);
        exercise.ExternalSource = Clean(input.ExternalSource);
        exercise.ExternalId = Clean(input.ExternalId);
        exercise.WeightIncrement = input.WeightIncrement is > 0 ? input.WeightIncrement : null;
        if (input.Id == 0)
        {
            db.Exercises.Add(exercise);
        }

        await db.SaveChangesAsync();
        input.Id = exercise.Id;
        return null;
    }

    public async Task<string?> DeleteAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.ScheduleDayExercises.AnyAsync(e => e.ExerciseId == id) || await db.SessionExercises.AnyAsync(e => e.ExerciseId == id))
        {
            return "It is used in a schedule or workout history, so it can't be deleted.";
        }

        await db.Exercises.Where(e => e.Id == id).ExecuteDeleteAsync();
        return null;
    }

    public Task<IReadOnlyList<ExerciseLookupResult>> LookupAsync(string term) => wger.SearchAsync(term);

    /// <summary>Copies details from the online source onto the exercise, keeping anything it doesn't provide.</summary>
    public async Task<bool> ApplyLookupAsync(Exercise exercise, string externalId)
    {
        var details = await wger.GetAsync(externalId);
        if (details is null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(exercise.Name))
        {
            exercise.Name = details.Name;
        }

        exercise.BodyRegion = details.BodyRegion;
        exercise.Category = details.Category ?? exercise.Category;
        exercise.Muscles = details.Muscles ?? exercise.Muscles;
        exercise.Equipment = details.Equipment ?? exercise.Equipment;
        exercise.Description = details.Description ?? exercise.Description;
        exercise.ImageUrl = details.ImageUrl ?? exercise.ImageUrl;
        exercise.ExternalSource = WgerClient.Source;
        exercise.ExternalId = details.ExternalId;
        return true;
    }

    public async Task<string?> AddVideoAsync(int exerciseId, string urlOrId, string? title, string? channel)
    {
        var videoId = YouTubeClient.ParseVideoId(urlOrId);
        if (videoId is null)
        {
            return "That doesn't look like a YouTube link.";
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.ExerciseVideos.AnyAsync(v => v.ExerciseId == exerciseId && v.YouTubeId == videoId))
        {
            return "Already saved.";
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            (title, channel) = await youTube.GetInfoAsync(videoId);
        }

        var first = !await db.ExerciseVideos.AnyAsync(v => v.ExerciseId == exerciseId);
        db.ExerciseVideos.Add(new ExerciseVideo
        {
            ExerciseId = exerciseId,
            YouTubeId = videoId,
            Title = Truncate(string.IsNullOrWhiteSpace(title) ? "YouTube video" : title.Trim(), 200),
            Channel = channel is null ? null : Truncate(channel.Trim(), 100),
            IsPrimary = first,
        });
        await db.SaveChangesAsync();
        return null;
    }

    public async Task SetPrimaryVideoAsync(int exerciseId, int videoId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var videos = await db.ExerciseVideos.Where(v => v.ExerciseId == exerciseId).ToListAsync();
        videos.ForEach(v => v.IsPrimary = v.Id == videoId);
        await db.SaveChangesAsync();
    }

    public async Task RemoveVideoAsync(int exerciseId, int videoId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var videos = await db.ExerciseVideos.Where(v => v.ExerciseId == exerciseId).ToListAsync();
        var video = videos.SingleOrDefault(v => v.Id == videoId);
        if (video is null)
        {
            return;
        }

        db.ExerciseVideos.Remove(video);
        if (video.IsPrimary && videos.FirstOrDefault(v => v.Id != videoId) is { } next)
        {
            next.IsPrimary = true;
        }

        await db.SaveChangesAsync();
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
