using Microsoft.Maui.Controls;

namespace SentenceStudio;

/// <summary>
/// Standard MAUI Application class for Blazor Hybrid.
/// Replaces the MauiReactor SentenceStudioApp component.
/// </summary>
public class BlazorApp : Application
{
    public BlazorApp()
    {
        MainPage = new BlazorHostPage();
    }
}
