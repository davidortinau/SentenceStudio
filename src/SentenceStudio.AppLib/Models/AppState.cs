using SentenceStudio.Shared.Models;

namespace SentenceStudio;

/// <summary>
/// App-wide state interface (extracted from old AppShell.cs for use by Blazor pages).
/// </summary>
public interface IAppState
{
    UserProfile CurrentUserProfile { get; set; }
}

public class AppState : IAppState
{
    public UserProfile CurrentUserProfile { get; set; }
}
