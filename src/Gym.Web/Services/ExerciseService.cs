using Gym.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gym.Web.Services;

/// <summary>Outcome of saving a pasted link: an error message (or null), and whether it became a video.</summary>
public sealed record LinkResult(string? Error, bool IsVideo);

public sealed class ExerciseService(IDbContextFactory<ApplicationDbContext> dbFactory, WgerClient wger, YouTubeClient youTube)
{
    public async Task<List<Exercise>> SearchAsync(string? term = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var query = db.Exercises.AsNoTracking().Include(e => e.Videos).Include(e => e.Links).AsSplitQuery().AsQueryable();
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
        var exercise = await db.Exercises.AsNoTracking().Include(e => e.Videos).Include(e => e.Links).AsSplitQuery()
            .SingleOrDefaultAsync(e => e.Id == id);
        if (exercise is not null)
        {
            exercise.Videos = exercise.Videos.OrderByDescending(v => v.IsPrimary).ThenBy(v => v.AddedUtc).ToList();
            exercise.Links = exercise.Links.OrderBy(l => l.AddedUtc).ToList();
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

    public Task<ExerciseLookup> LookupAsync(string term) => wger.SearchAsync(term);

    /// <summary>Copies details from the online source onto the exercise, keeping anything it doesn't provide.</summary>
    public async Task<bool> ApplyLookupAsync(Exercise exercise, ExerciseLookupResult result)
    {
        var details = result.Details ?? await wger.GetAsync(result.ExternalId);
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

    /// <summary>
    /// Saves any pasted link. YouTube links become embedded videos (as before); anything else is
    /// stored as a web link. Returns an error message, or null on success.
    /// </summary>
    public async Task<LinkResult> AddLinkAsync(int exerciseId, string input, string? title)
    {
        var text = input.Trim();
        if (text.Length == 0)
        {
            return new LinkResult("Paste a link first.", false);
        }

        if (IsYouTube(text))
        {
            return new LinkResult(await AddVideoAsync(exerciseId, text, title, null), true);
        }

        if (NormalizeWebUrl(text) is not { } url)
        {
            return new LinkResult("That doesn't look like a web link (it should start with https://).", false);
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.ExerciseLinks.AnyAsync(l => l.ExerciseId == exerciseId && l.Url == url))
        {
            return new LinkResult("Already saved.", false);
        }

        var link = new ExerciseLink { ExerciseId = exerciseId, Url = url };
        link.Title = Truncate(string.IsNullOrWhiteSpace(title) ? link.Site : title.Trim(), 200);
        db.ExerciseLinks.Add(link);
        await db.SaveChangesAsync();
        return new LinkResult(null, false);
    }

    public async Task RemoveLinkAsync(int exerciseId, int linkId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.ExerciseLinks.Where(l => l.ExerciseId == exerciseId && l.Id == linkId).ExecuteDeleteAsync();
    }

    /// <summary>A YouTube page link, or a bare 11-character video id.</summary>
    internal static bool IsYouTube(string text) =>
        YouTubeClient.ParseVideoId(text) is not null
        && (text.Length == 11 || text.Contains("youtube", StringComparison.OrdinalIgnoreCase) || text.Contains("youtu.be", StringComparison.OrdinalIgnoreCase));

    /// <summary>An absolute http(s) URL, adding https:// when only a host/path was typed. Null if it isn't a web address.</summary>
    internal static string? NormalizeWebUrl(string text)
    {
        var candidate = text.Contains("://", StringComparison.Ordinal) ? text : "https://" + text;
        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
               && uri.Host.Contains('.')
               && candidate.Length <= 2000
            ? uri.AbsoluteUri
            : null;
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

    /// <summary>Copies the saved videos and links of one exercise onto another (skipping any it already has).</summary>
    public async Task CopyVideosAsync(int fromExerciseId, int toExerciseId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var existingLinks = await db.ExerciseLinks.Where(l => l.ExerciseId == toExerciseId).Select(l => l.Url).ToListAsync();
        var sourceLinks = await db.ExerciseLinks.AsNoTracking().Where(l => l.ExerciseId == fromExerciseId).OrderBy(l => l.AddedUtc).ToListAsync();
        foreach (var link in sourceLinks.Where(l => !existingLinks.Contains(l.Url)))
        {
            db.ExerciseLinks.Add(new ExerciseLink { ExerciseId = toExerciseId, Url = link.Url, Title = link.Title });
        }

        var existing = await db.ExerciseVideos.Where(v => v.ExerciseId == toExerciseId).Select(v => v.YouTubeId).ToListAsync();
        var source = await db.ExerciseVideos.AsNoTracking().Where(v => v.ExerciseId == fromExerciseId)
            .OrderByDescending(v => v.IsPrimary).ThenBy(v => v.AddedUtc).ToListAsync();
        var first = existing.Count == 0;
        foreach (var video in source.Where(v => !existing.Contains(v.YouTubeId)))
        {
            db.ExerciseVideos.Add(new ExerciseVideo
            {
                ExerciseId = toExerciseId,
                YouTubeId = video.YouTubeId,
                Title = video.Title,
                Channel = video.Channel,
                IsPrimary = first && video.IsPrimary,
            });
        }

        await db.SaveChangesAsync();
    }

    /// <summary>A name like "Squat (copy)" that isn't taken yet.</summary>
    public async Task<string> CopyNameAsync(string name)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var names = await db.Exercises.Where(e => e.Name.StartsWith(name)).Select(e => e.Name).ToListAsync();
        var candidate = $"{name} (copy)";
        for (var n = 2; names.Contains(candidate, StringComparer.OrdinalIgnoreCase); n++)
        {
            candidate = $"{name} (copy {n})";
        }

        return candidate.Length > 120 ? candidate[..120] : candidate;
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
