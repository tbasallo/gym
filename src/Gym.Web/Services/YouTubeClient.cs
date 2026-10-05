using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Gym.Web.Services;

public sealed record VideoResult(string YouTubeId, string Title, string Channel, string? ThumbnailUrl);

/// <summary>YouTube Data API v3 search. Works without a key too: callers fall back to <see cref="SearchUrl"/>.</summary>
public sealed partial class YouTubeClient(HttpClient http, IConfiguration config, ILogger<YouTubeClient> logger)
{
    private string? ApiKey => config["YouTube:ApiKey"];

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    public async Task<IReadOnlyList<VideoResult>> SearchAsync(string exerciseName, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return [];
        }

        var url = "https://www.googleapis.com/youtube/v3/search?part=snippet&type=video&maxResults=8&safeSearch=strict"
            + $"&q={Uri.EscapeDataString(SearchTerms(exerciseName))}&key={Uri.EscapeDataString(ApiKey!)}";
        try
        {
            using var doc = JsonDocument.Parse(await http.GetStringAsync(url, cancellationToken));
            return doc.RootElement.GetProperty("items").EnumerateArray()
                .Select(item =>
                {
                    var snippet = item.GetProperty("snippet");
                    var thumbs = snippet.TryGetProperty("thumbnails", out var t) ? t : default;
                    string? thumb = null;
                    if (thumbs.ValueKind == JsonValueKind.Object && thumbs.TryGetProperty("medium", out var medium))
                    {
                        thumb = medium.GetProperty("url").GetString();
                    }

                    return new VideoResult(
                        item.GetProperty("id").GetProperty("videoId").GetString() ?? "",
                        WebUtility.HtmlDecode(snippet.GetProperty("title").GetString() ?? ""),
                        WebUtility.HtmlDecode(snippet.GetProperty("channelTitle").GetString() ?? ""),
                        thumb);
                })
                .Where(v => v.YouTubeId.Length > 0)
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or TaskCanceledException)
        {
            logger.LogWarning(ex, "YouTube search failed for {Exercise}", exerciseName);
            return [];
        }
    }

    /// <summary>Title and channel for a video via oEmbed (no API key needed).</summary>
    public async Task<(string? Title, string? Channel)> GetInfoAsync(string videoId, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = "https://www.youtube.com/oembed?format=json&url=" + Uri.EscapeDataString(WatchUrl(videoId));
            using var doc = JsonDocument.Parse(await http.GetStringAsync(url, cancellationToken));
            var root = doc.RootElement;
            return (root.TryGetProperty("title", out var t) ? t.GetString() : null,
                root.TryGetProperty("author_name", out var a) ? a.GetString() : null);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogInformation(ex, "oEmbed lookup failed for {VideoId}", videoId);
            return (null, null);
        }
    }

    public static string SearchTerms(string exerciseName) => $"how to {exerciseName} proper form";

    /// <summary>A plain YouTube search link with sensible keywords; needs no API key.</summary>
    public static string SearchUrl(string exerciseName) =>
        "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(SearchTerms(exerciseName));

    public static string EmbedUrl(string videoId) => $"https://www.youtube-nocookie.com/embed/{videoId}?rel=0";

    public static string WatchUrl(string videoId) => $"https://www.youtube.com/watch?v={videoId}";

    public static string ThumbnailUrl(string videoId) => $"https://i.ytimg.com/vi/{videoId}/mqdefault.jpg";

    /// <summary>Extracts the 11-character id from a watch, share, shorts or embed link, or a bare id.</summary>
    public static string? ParseVideoId(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var match = VideoIdPattern().Match(input.Trim());
        return match.Success ? match.Groups["id"].Value : null;
    }

    [GeneratedRegex(@"^(?:https?://)?(?:(?:www\.|m\.|music\.)?youtube(?:-nocookie)?\.com/(?:watch\?(?:.*&)?v=|shorts/|embed/|live/|v/)|youtu\.be/)?(?<id>[A-Za-z0-9_-]{11})(?:[?&#/].*)?$")]
    private static partial Regex VideoIdPattern();
}
