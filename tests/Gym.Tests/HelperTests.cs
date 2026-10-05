using Gym.Web.Data;
using Gym.Web.Services;

namespace Gym.Tests;

public class HelperTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://youtube.com/watch?feature=share&v=dQw4w9WgXcQ&t=10", "dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?si=abc", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://m.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://example.com/watch?v=dQw4w9WgXcQ", null)]
    [InlineData("not a link", null)]
    [InlineData("", null)]
    public void Parses_youtube_ids(string input, string? expected)
    {
        Assert.Equal(expected, YouTubeClient.ParseVideoId(input));
    }

    [Fact]
    public void Search_link_uses_exercise_keywords()
    {
        Assert.Equal("https://www.youtube.com/results?search_query=how%20to%20Leg%20Press%20proper%20form", YouTubeClient.SearchUrl("Leg Press"));
    }

    [Fact]
    public void Strips_html_from_wger_descriptions()
    {
        Assert.Equal("Stand tall.\nSquat down & up.", WgerClient.StripHtml("<p>Stand tall.</p><p>Squat down &amp; up.</p>"));
        Assert.Null(WgerClient.StripHtml("  "));
    }

    [Theory]
    [InlineData("Legs", BodyRegion.Lower)]
    [InlineData("Calves", BodyRegion.Lower)]
    [InlineData("Abs", BodyRegion.Core)]
    [InlineData("Chest", BodyRegion.Upper)]
    [InlineData(null, BodyRegion.Upper)]
    public void Maps_wger_categories(string? category, BodyRegion expected)
    {
        Assert.Equal(expected, WgerClient.RegionFor(category));
    }

    [Fact]
    public void Draft_weight_drops_trailing_zeros()
    {
        var draft = new SetDraft { Weight = 65.00m };
        Assert.Equal("65", draft.Weight.ToString(System.Globalization.CultureInfo.InvariantCulture));
        draft.Weight = 67.50m;
        Assert.Equal("67.5", draft.Weight.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Rotation_wraps_to_first_day()
    {
        var schedule = new Schedule
        {
            Days = [new() { Id = 10, Order = 1 }, new() { Id = 20, Order = 2 }, new() { Id = 30, Order = 3 }],
        };
        Assert.Equal(10, ScheduleService.NextDay(schedule)!.Id);
        Assert.Equal(20, ScheduleService.DayAfter(schedule, 10));
        Assert.Equal(10, ScheduleService.DayAfter(schedule, 30));
        schedule.NextDayId = 30;
        Assert.Equal(30, ScheduleService.NextDay(schedule)!.Id);
        schedule.NextDayId = 999;
        Assert.Equal(10, ScheduleService.NextDay(schedule)!.Id);
    }
}
