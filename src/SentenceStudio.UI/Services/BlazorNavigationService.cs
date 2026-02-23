using Microsoft.AspNetCore.Components;

namespace SentenceStudio.WebUI.Services;

/// <summary>
/// Navigation service wrapper for Blazor.
/// Allows C# services to trigger navigation without direct Blazor dependency.
/// </summary>
public class BlazorNavigationService
{
    private NavigationManager _navigationManager;

    public void Initialize(NavigationManager navigationManager)
    {
        _navigationManager = navigationManager;
    }

    public void NavigateTo(string uri, bool forceLoad = false)
    {
        _navigationManager?.NavigateTo(uri, forceLoad);
    }

    public void NavigateBack()
    {
        _navigationManager?.NavigateTo("javascript:history.back()");
    }

    public string CurrentUri => _navigationManager?.Uri;
}
