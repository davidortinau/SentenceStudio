using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace Plugin.Maui.HelpKit.Ui;

/// <summary>
/// Minimal color palette used by the native chat page. Values are exposed
/// via <c>DynamicResource</c> so hosts can override them in their app
/// resources without forking the page. Hosts that want the defaults can
/// call <see cref="ApplyDefaults(ResourceDictionary)"/> once at startup.
/// </summary>
public static class HelpKitThemeResources
{
    public const string PrimaryColor      = "HelpKitPrimaryColor";
    public const string SurfaceColor      = "HelpKitSurfaceColor";
    public const string OnSurface         = "HelpKitOnSurface";
    public const string BubbleUser        = "HelpKitBubbleUser";
    public const string BubbleAssistant   = "HelpKitBubbleAssistant";
    public const string BubbleUserText    = "HelpKitBubbleUserText";
    public const string BubbleAssistantText = "HelpKitBubbleAssistantText";
    public const string ErrorColor        = "HelpKitError";
    public const string MutedText         = "HelpKitMutedText";

    /// <summary>
    /// Adds default light/dark-aware colors to the provided dictionary if
    /// they are not already present. Safe to call multiple times. Colors
    /// are resolved against <see cref="Application.RequestedTheme"/> at the
    /// time this method runs. Hosts that need runtime theme switching
    /// should provide their own resource entries.
    /// </summary>
    public static void ApplyDefaults(ResourceDictionary resources)
    {
        ArgumentNullException.ThrowIfNull(resources);

        var dark = Application.Current?.RequestedTheme == AppTheme.Dark;

        TryAdd(resources, PrimaryColor,          Color.FromArgb("#2563EB"));
        TryAdd(resources, SurfaceColor,          dark ? Color.FromArgb("#1A1A1A") : Color.FromArgb("#FFFFFF"));
        TryAdd(resources, OnSurface,             dark ? Color.FromArgb("#F3F4F6") : Color.FromArgb("#111827"));
        TryAdd(resources, BubbleUser,            Color.FromArgb("#2563EB"));
        TryAdd(resources, BubbleUserText,        Color.FromArgb("#FFFFFF"));
        TryAdd(resources, BubbleAssistant,       dark ? Color.FromArgb("#2A2A2A") : Color.FromArgb("#F3F4F6"));
        TryAdd(resources, BubbleAssistantText,   dark ? Color.FromArgb("#F3F4F6") : Color.FromArgb("#111827"));
        TryAdd(resources, ErrorColor,            Color.FromArgb("#B91C1C"));
        TryAdd(resources, MutedText,             dark ? Color.FromArgb("#9CA3AF") : Color.FromArgb("#6B7280"));
    }

    private static void TryAdd(ResourceDictionary rd, string key, object value)
    {
        if (!rd.ContainsKey(key))
            rd[key] = value;
    }
}
