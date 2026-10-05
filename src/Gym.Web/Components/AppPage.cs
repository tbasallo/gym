using Gym.Web.Data;
using Gym.Web.Services;
using Microsoft.AspNetCore.Components;

namespace Gym.Web.Components;

/// <summary>Base for interactive pages: current user plus safe action handling.</summary>
public abstract class AppPage : ComponentBase
{
    [Inject] protected UserContext Me { get; set; } = null!;
    [Inject] protected ILogger<AppPage> Log { get; set; } = null!;

    protected ApplicationUser CurrentUser { get; private set; } = null!;
    protected string UserId => CurrentUser.Id;
    protected string? Error { get; set; }
    protected string? Notice { get; set; }
    protected bool Busy { get; private set; }

    protected override async Task OnInitializedAsync()
    {
        CurrentUser = await Me.GetUserAsync();
        await LoadAsync();
    }

    protected virtual Task LoadAsync() => Task.CompletedTask;

    protected async Task RefreshUserAsync()
    {
        Me.Invalidate();
        CurrentUser = await Me.GetUserAsync();
    }

    /// <summary>Runs a user action, showing a friendly message instead of breaking the circuit.</summary>
    protected async Task Run(Func<Task> action, bool reload = true)
    {
        if (Busy)
        {
            return;
        }

        Busy = true;
        Error = null;
        Notice = null;
        try
        {
            await action();
            if (reload)
            {
                await LoadAsync();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            Error = ex.Message;
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Action failed");
            Error = "Something went wrong. Please try again.";
        }
        finally
        {
            Busy = false;
        }
    }

    protected string Local(DateTime utc, string format = "ddd MMM d, h:mm tt") => Me.ToLocal(utc).ToString(format);

    protected string W(decimal? weight) => weight is null ? "—" : $"{weight:0.##}";

    protected string Unit => CurrentUser?.WeightUnit ?? "lb";

    protected static string Clock(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}" : $"{(int)span.TotalMinutes}:{span.Seconds:00}";
}
