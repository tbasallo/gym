namespace Gym.Web.Services;

/// <summary>Plain web search links for an exercise; no API keys involved.</summary>
public static class WebSearch
{
    public static string YouTube(string exerciseName) => YouTubeClient.SearchUrl(exerciseName);

    public static string Bing(string exerciseName) =>
        "https://www.bing.com/search?q=" + Uri.EscapeDataString($"{exerciseName.Trim()} exercise how to proper form muscles");
}
