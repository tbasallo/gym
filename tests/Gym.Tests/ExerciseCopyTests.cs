using Gym.Web.Data;
using Gym.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Gym.Tests;

public sealed class ExerciseCopyTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly ExerciseService _exercises;

    public ExerciseCopyTests()
    {
        var http = new HttpClient(new FailingHandler());
        _exercises = new ExerciseService(_db,
            new WgerClient(http, Microsoft.Extensions.Logging.Abstractions.NullLogger<WgerClient>.Instance),
            new YouTubeClient(http, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<YouTubeClient>.Instance));
    }

    [Fact]
    public async Task Copy_name_is_unique()
    {
        Assert.Equal("Leg Press (copy)", await _exercises.CopyNameAsync("Leg Press"));
        Assert.Null(await _exercises.SaveAsync(new Exercise { Name = "Leg Press (copy)" }));
        Assert.Equal("Leg Press (copy 2)", await _exercises.CopyNameAsync("Leg Press"));
    }

    [Fact]
    public async Task Copying_videos_keeps_the_main_one_and_skips_duplicates()
    {
        var source = (await _exercises.SearchAsync("Leg Press")).Single();
        Assert.Null(await _exercises.AddVideoAsync(source.Id, "https://youtu.be/aaaaaaaaaaa", "First", "Chan"));
        Assert.Null(await _exercises.AddVideoAsync(source.Id, "https://youtu.be/bbbbbbbbbbb", "Second", "Chan"));
        var copy = new Exercise { Name = "Single-Leg Press" };
        Assert.Null(await _exercises.SaveAsync(copy));

        await _exercises.CopyVideosAsync(source.Id, copy.Id);
        await _exercises.CopyVideosAsync(source.Id, copy.Id);

        await using var db = _db.CreateDbContext();
        var videos = await db.ExerciseVideos.Where(v => v.ExerciseId == copy.Id).OrderBy(v => v.YouTubeId).ToListAsync();
        Assert.Equal(["aaaaaaaaaaa", "bbbbbbbbbbb"], videos.Select(v => v.YouTubeId));
        Assert.True(videos[0].IsPrimary);
        Assert.False(videos[1].IsPrimary);
        Assert.Equal(2, await db.ExerciseVideos.CountAsync(v => v.ExerciseId == source.Id));
    }

    [Fact]
    public async Task Any_link_can_be_saved_and_youtube_links_become_videos()
    {
        var ex = (await _exercises.SearchAsync("Leg Press")).Single();

        var video = await _exercises.AddLinkAsync(ex.Id, "https://www.youtube.com/watch?v=aaaaaaaaaaa", null);
        var page = await _exercises.AddLinkAsync(ex.Id, "exrx.net/WeightExercises/Quadriceps/SLLegPress", "Form guide");
        var untitled = await _exercises.AddLinkAsync(ex.Id, "https://www.muscleandstrength.com/exercises/leg-press.html", " ");

        Assert.Equal(new LinkResult(null, true), video);
        Assert.Equal(new LinkResult(null, false), page);
        Assert.Equal(new LinkResult(null, false), untitled);

        var saved = (await _exercises.GetAsync(ex.Id))!;
        Assert.Single(saved.Videos);
        Assert.Equal(2, saved.Links.Count);
        Assert.Equal("https://exrx.net/WeightExercises/Quadriceps/SLLegPress", saved.Links[0].Url);
        Assert.Equal("Form guide", saved.Links[0].Title);
        Assert.Equal("muscleandstrength.com", saved.Links[1].Title);
        Assert.Equal("muscleandstrength.com", saved.Links[1].Site);

        Assert.Equal("Already saved.", (await _exercises.AddLinkAsync(ex.Id, "https://exrx.net/WeightExercises/Quadriceps/SLLegPress", null)).Error);

        await _exercises.RemoveLinkAsync(ex.Id, saved.Links[0].Id);
        Assert.Single((await _exercises.GetAsync(ex.Id))!.Links);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("just some words")]
    [InlineData("ftp://example.com/file")]
    [InlineData("localhost/admin")]
    public async Task Non_web_links_are_rejected(string input)
    {
        var ex = (await _exercises.SearchAsync("Leg Press")).Single();
        var result = await _exercises.AddLinkAsync(ex.Id, input, null);
        Assert.NotNull(result.Error);
        Assert.Empty((await _exercises.GetAsync(ex.Id))!.Links);
    }

    [Theory]
    [InlineData("https://youtu.be/aaaaaaaaaaa", true)]
    [InlineData("https://m.youtube.com/watch?v=aaaaaaaaaaa", true)]
    [InlineData("aaaaaaaaaaa", true)]
    [InlineData("https://exrx.net/aaaaaaaaaaa", false)]
    [InlineData("https://vimeo.com/123456", false)]
    public void Recognises_youtube_links(string input, bool expected)
    {
        Assert.Equal(expected, ExerciseService.IsYouTube(input));
    }

    [Fact]
    public async Task Copying_also_copies_links()
    {
        var source = (await _exercises.SearchAsync("Leg Press")).Single();
        await _exercises.AddLinkAsync(source.Id, "https://exrx.net/leg-press", "Guide");
        var copy = new Exercise { Name = "Leg Press Variation" };
        Assert.Null(await _exercises.SaveAsync(copy));

        await _exercises.CopyVideosAsync(source.Id, copy.Id);
        await _exercises.CopyVideosAsync(source.Id, copy.Id);

        var link = Assert.Single((await _exercises.GetAsync(copy.Id))!.Links);
        Assert.Equal("Guide", link.Title);
    }

    public void Dispose() => _db.Dispose();

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("offline");
    }
}
