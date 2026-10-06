using System.Net;
using System.Text;
using Gym.Web.Data;
using Gym.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gym.Tests;

public class WgerClientTests
{
    // Shape of GET /api/v2/exerciseinfo/?name__search=...&language__code=en (ExerciseInfoSerializer).
    private const string InfoList = """
        {"count": 2, "next": null, "previous": null, "results": [
          {"id": 984, "uuid": "x", "category": {"id": 9, "name": "Legs"},
           "muscles": [{"id": 10, "name": "Quadriceps femoris", "name_en": "Quads"}],
           "muscles_secondary": [{"id": 8, "name": "Gluteus maximus", "name_en": "Glutes"}],
           "equipment": [{"id": 3, "name": "Dumbbell"}],
           "images": [{"image": "https://wger.de/media/exercise-images/984/a.png", "is_main": false},
                      {"image": "/media/exercise-images/984/main.png", "is_main": true}],
           "translations": [
             {"id": 1, "name": "Bulgarische Kniebeuge", "description": "<p>Deutsch</p>", "language": 1},
             {"id": 2, "name": "Bulgarian Split Squat", "description": "<p>Rear foot on a bench.</p><p>Lower down.</p>", "language": 2}]},
          {"id": 985, "category": {"id": 8, "name": "Arms"}, "muscles": [], "muscles_secondary": [], "equipment": [], "images": [],
           "translations": [{"id": 3, "name": "Curl", "description": "", "language": 2}]}
        ]}
        """;

    private static (WgerClient Client, List<string> Urls) Client(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var urls = new List<string>();
        var handler = new StubHandler(r => { urls.Add(r.RequestUri!.AbsoluteUri); return respond(r); });
        return (new WgerClient(new HttpClient(handler), NullLogger<WgerClient>.Instance), urls);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Searches_exerciseinfo_by_name_in_english_and_returns_full_details()
    {
        var (client, urls) = Client(_ => Json(InfoList));

        var lookup = await client.SearchAsync(" split squat ");

        Assert.Null(lookup.Error);
        Assert.Equal("https://wger.de/api/v2/exerciseinfo/?name__search=split%20squat&language__code=en&limit=20", Assert.Single(urls));
        Assert.Equal(2, lookup.Results.Count);
        var hit = lookup.Results[0];
        Assert.Equal("984", hit.ExternalId);
        Assert.Equal("Bulgarian Split Squat", hit.Name);
        Assert.Equal("Legs", hit.Category);
        Assert.Equal("https://wger.de/media/exercise-images/984/main.png", hit.ImageUrl);
        var d = hit.Details!;
        Assert.Equal(BodyRegion.Lower, d.BodyRegion);
        Assert.Equal("Quads, Glutes", d.Muscles);
        Assert.Equal("Dumbbell", d.Equipment);
        Assert.Equal("Rear foot on a bench.\nLower down.", d.Description);
    }

    [Fact]
    public async Task Falls_back_to_the_legacy_search_endpoint()
    {
        var (client, urls) = Client(r => r.RequestUri!.AbsolutePath.Contains("exerciseinfo")
            ? Json("{}", HttpStatusCode.NotFound)
            : Json("""{"suggestions": [{"value": "Squats", "data": {"id": 5, "base_id": 111, "name": "Squats", "category": "Legs", "image": null}}]}"""));

        var lookup = await client.SearchAsync("squat");

        Assert.Null(lookup.Error);
        Assert.Equal(2, urls.Count);
        var hit = Assert.Single(lookup.Results);
        Assert.Equal("111", hit.ExternalId);
        Assert.Null(hit.Details);
    }

    [Fact]
    public async Task Reports_why_the_service_could_not_be_used()
    {
        var (down, _) = Client(_ => throw new HttpRequestException("no route"));
        Assert.Equal("Couldn't reach the exercise database.", (await down.SearchAsync("squat")).Error);

        var (broken, _) = Client(_ => Json("oops", HttpStatusCode.InternalServerError));
        Assert.Contains("500", (await broken.SearchAsync("squat")).Error);
    }

    [Fact]
    public async Task Empty_term_does_not_call_the_service()
    {
        var (client, urls) = Client(_ => Json(InfoList));
        Assert.Empty((await client.SearchAsync("  ")).Results);
        Assert.Empty(urls);
    }

    [Fact]
    public void Web_search_links_use_the_exercise_name()
    {
        Assert.Equal("https://www.bing.com/search?q=Nordic%20Curl%20exercise%20how%20to%20proper%20form%20muscles", WebSearch.Bing(" Nordic Curl "));
        Assert.StartsWith("https://www.youtube.com/results?search_query=", WebSearch.YouTube("Nordic Curl"));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
