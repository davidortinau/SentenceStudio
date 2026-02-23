using SentenceStudio.Shared.Models;

namespace SentenceStudio;

/// <summary>
/// App-wide state interface (extracted from old AppShell.cs for use by Blazor pages).
/// </summary>
public interface IAppState
{
    Microsoft.Maui.ApplicationModel.AppTheme CurrentAppTheme { get; set; }
    UserProfile CurrentUserProfile { get; set; }
}

public class AppState : IAppState
{
    public Microsoft.Maui.ApplicationModel.AppTheme CurrentAppTheme { get; set; }
    public UserProfile CurrentUserProfile { get; set; }
}
