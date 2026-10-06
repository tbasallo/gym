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

    public void Dispose() => _db.Dispose();

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("offline");
    }
}
