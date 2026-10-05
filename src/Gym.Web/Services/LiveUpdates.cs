namespace Gym.Web.Services;

/// <summary>
/// In-process change notifications so every device viewing a session stays in sync.
/// Each Blazor Server circuit subscribes; a single App Service instance is assumed
/// (scaling out would need Azure SignalR Service or a backplane).
/// </summary>
public sealed class LiveUpdates
{
    /// <summary>Raised with the session id whenever sets, participants or status change.</summary>
    public event Action<int>? SessionChanged;

    /// <summary>Raised when a session starts or ends, so home screens can refresh.</summary>
    public event Action? SessionsChanged;

    public void NotifySession(int sessionId) => SessionChanged?.Invoke(sessionId);

    public void NotifySessions() => SessionsChanged?.Invoke();
}
