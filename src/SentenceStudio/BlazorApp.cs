using Microsoft.Maui.Controls;
using SentenceStudio.Services;

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
