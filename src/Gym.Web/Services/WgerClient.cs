using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gym.Web.Data;

namespace Gym.Web.Services;

/// <summary>A search hit; <see cref="Details"/> is filled when the search response already carried them.</summary>
public sealed record ExerciseLookupResult(string ExternalId, string Name, string? Category, string? ImageUrl, ExerciseDetails? Details = null);

/// <summary>Search outcome: hits, or why the service couldn't be used.</summary>
public sealed record ExerciseLookup(IReadOnlyList<ExerciseLookupResult> Results, string? Error = null)
{
    public static ExerciseLookup Empty { get; } = new([]);
}

public sealed record ExerciseDetails(
    string ExternalId,
    string Name,
    string? Category,
    BodyRegion BodyRegion,
    string? Muscles,
    string? Equipment,
    string? Description,
    string? ImageUrl);

/// <summary>
/// Pulls exercise details from the free, open wger.de exercise database (no key needed).
/// Uses <c>/api/v2/exerciseinfo/?name__search=</c>; the older <c>/exercise/search/</c> endpoint was
/// removed from current wger versions and is only tried as a fallback.
/// </summary>
public sealed partial class WgerClient(HttpClient http, ILogger<WgerClient> logger)
{
    public const string Source = "wger";
    private const string BaseUrl = "https://wger.de";
    private const int English = 2;

    public async Task<ExerciseLookup> SearchAsync(string term, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return ExerciseLookup.Empty;
        }

        var query = Uri.EscapeDataString(term.Trim());
        try
        {
            using var response = await http.GetAsync(
                $"{BaseUrl}/api/v2/exerciseinfo/?name__search={query}&language__code=en&limit=20", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                return new ExerciseLookup(ParseInfoList(doc.RootElement));
            }

            logger.LogWarning("wger exerciseinfo search returned {Status} for {Term}", (int)response.StatusCode, term);
            return await LegacySearchAsync(query, cancellationToken)
                ?? new ExerciseLookup([], $"The exercise database answered with an error ({(int)response.StatusCode}).");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "wger search failed for {Term}", term);
            return new ExerciseLookup([], ex is TaskCanceledException
                ? "The exercise database took too long to answer."
                : "Couldn't reach the exercise database.");
        }
    }

    public async Task<ExerciseDetails?> GetAsync(string externalId, CancellationToken cancellationToken = default)
    {
        try
        {
            var json = await http.GetStringAsync($"{BaseUrl}/api/v2/exerciseinfo/{Uri.EscapeDataString(externalId)}/", cancellationToken);
            using var doc = JsonDocument.Parse(json);
            return ParseInfo(doc.RootElement, externalId);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or TaskCanceledException)
        {
            logger.LogWarning(ex, "wger lookup failed for {Id}", externalId);
            return null;
        }
    }

    /// <summary>Parses a paginated <c>exerciseinfo</c> list (<c>{"results": [...]}</c>) into search hits.</summary>
    internal static IReadOnlyList<ExerciseLookupResult> ParseInfoList(JsonElement root)
    {
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return results.EnumerateArray()
            .Select(item => Int(item, "id") is { } id ? ParseInfo(item, id.ToString()) : null)
            .OfType<ExerciseDetails>()
            .Where(d => d.Name.Length > 0)
            .DistinctBy(d => d.ExternalId)
            .Select(d => new ExerciseLookupResult(d.ExternalId, d.Name, d.Category, d.ImageUrl, d))
            .ToList();
    }

    /// <summary>Parses one <c>exerciseinfo</c> object.</summary>
    internal static ExerciseDetails ParseInfo(JsonElement root, string externalId)
    {
        // Current versions call localized entries "translations"; older ones "exercises".
        var translations = root.TryGetProperty("translations", out var t) ? t
            : root.TryGetProperty("exercises", out var e) ? e : default;
        JsonElement? english = null;
        if (translations.ValueKind == JsonValueKind.Array)
        {
            english = translations.EnumerateArray().Cast<JsonElement?>()
                .FirstOrDefault(x => Int(x!.Value, "language") == English)
                ?? translations.EnumerateArray().Cast<JsonElement?>().FirstOrDefault();
        }

        var category = root.TryGetProperty("category", out var c) && c.ValueKind == JsonValueKind.Object ? Str(c, "name") : null;
        var muscles = Names(root, "muscles").Concat(Names(root, "muscles_secondary")).Distinct().ToList();
        var equipment = Names(root, "equipment").ToList();
        string? image = null;
        if (root.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array)
        {
            var all = images.EnumerateArray().ToList();
            var main = all.FirstOrDefault(i => i.TryGetProperty("is_main", out var m) && m.ValueKind == JsonValueKind.True);
            image = Absolute(Str(main.ValueKind == JsonValueKind.Object ? main : all.FirstOrDefault(), "image"));
        }

        return new ExerciseDetails(
            externalId,
            english is { } en ? Str(en, "name") ?? "" : "",
            category,
            RegionFor(category),
            muscles.Count == 0 ? null : string.Join(", ", muscles),
            equipment.Count == 0 ? null : string.Join(", ", equipment),
            english is { } d ? StripHtml(Str(d, "description")) : null,
            image);
    }

    /// <summary>Older wger versions only: <c>/api/v2/exercise/search/</c>. Null when unavailable.</summary>
    private async Task<ExerciseLookup?> LegacySearchAsync(string escapedTerm, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync($"{BaseUrl}/api/v2/exercise/search/?language=en&term={escapedTerm}", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (!doc.RootElement.TryGetProperty("suggestions", out var suggestions))
            {
                return null;
            }

            return new ExerciseLookup(suggestions.EnumerateArray()
                .Select(s =>
                {
                    var data = s.TryGetProperty("data", out var d) ? d : default;
                    var id = Int(data, "base_id") ?? Int(data, "id");
                    return id is null
                        ? null
                        : new ExerciseLookupResult(
                            id.Value.ToString(),
                            Str(data, "name") ?? Str(s, "value") ?? "",
                            Str(data, "category"),
                            Absolute(Str(data, "image_thumbnail") ?? Str(data, "image")));
                })
                .OfType<ExerciseLookupResult>()
                .DistinctBy(r => r.ExternalId)
                .ToList());
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            return null;
        }
    }

    public static BodyRegion RegionFor(string? category) => category?.ToLowerInvariant() switch
    {
        "legs" or "calves" or "glutes" => BodyRegion.Lower,
        "abs" or "core" => BodyRegion.Core,
        "cardio" => BodyRegion.FullBody,
        _ => BodyRegion.Upper,
    };

    public static string? StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var text = BlockTags().Replace(html, "\n");
        text = AnyTag().Replace(text, "");
        text = WebUtility.HtmlDecode(text);
        text = BlankLines().Replace(text, "\n\n");
        return text.Trim();
    }

    private static IEnumerable<string> Names(JsonElement root, string property) =>
        root.TryGetProperty(property, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray()
                .Select(x => Str(x, "name_en") is { Length: > 0 } en ? en : Str(x, "name"))
                .OfType<string>()
                .Where(n => n.Length > 0)
            : [];

    private static string? Str(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static int? Int(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : null;

    private static string? Absolute(string? url) =>
        string.IsNullOrWhiteSpace(url) ? null : url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : BaseUrl + url;

    [GeneratedRegex(@"<\s*(br|/p|/li|/h\d)\s*/?\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockTags();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"\n\s*\n+")]
    private static partial Regex BlankLines();
}
