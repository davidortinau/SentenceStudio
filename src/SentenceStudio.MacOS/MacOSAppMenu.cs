using AppKit;
using Foundation;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using SentenceStudio.Abstractions;
using SentenceStudio.Services;
using SentenceStudio.Services.Progress;

namespace SentenceStudio.MacOS;

/// <summary>
/// Adds a native macOS "Account" menu with an adaptive Log In / Log Out item.
/// The AppKit head hosts a single Blazor WebView; when the user is stuck pre-nav
/// (e.g. onboarding) or behind an overlay there is no in-window affordance to sign out
/// and switch accounts, so this provides an always-available native escape hatch (⇧⌘L).
/// </summary>
public static class MacOSAppMenu
{
    private static NSMenuItem? _authItem;
    private static NSObject? _activationObserver;
    private static bool _authSubscribed;

    private static IServiceProvider? Services => IPlatformApplication.Current?.Services;

    // The AppKit head does not provide MAUI Essentials' MainThread implementation
    // (calling it throws NotImplementedInReferenceAssemblyException), so marshal to the
    // UI thread via AppKit/Foundation directly.
    private static void OnMainThread(Action action)
    {
        var app = NSApplication.SharedApplication;
        if (app is not null)
            app.BeginInvokeOnMainThread(action);
        else
            action();
    }

    /// <summary>
    /// Installs the Account menu and re-asserts it on every app activation. The default main
    /// menu is rebuilt by MAUI/AppKit at different points in Debug vs Release, so a one-time
    /// install can be wiped; re-asserting on activation is cheap and idempotent.
    /// </summary>
    public static void RegisterForActivation()
    {
        _activationObserver ??= NSApplication.Notifications.ObserveDidBecomeActive((_, _) => EnsureInstalled());
        EnsureInstalled();
    }

    private static void EnsureInstalled()
    {
        var mainMenu = NSApplication.SharedApplication.MainMenu;
        if (mainMenu is null)
            return;

        foreach (var existing in mainMenu.Items)
        {
            if (existing.Title == "Account")
                return;
        }

        var accountMenu = new NSMenu("Account");
        _authItem = new NSMenuItem("Log Out")
        {
            KeyEquivalent = "l",
            KeyEquivalentModifierMask = NSEventModifierMask.CommandKeyMask | NSEventModifierMask.ShiftKeyMask
        };
        _authItem.Activated += OnAuthItemActivated;
        accountMenu.AddItem(_authItem);

        mainMenu.AddItem(new NSMenuItem("Account") { Submenu = accountMenu });

        if (!_authSubscribed && Services?.GetService<AuthenticationStateProvider>() is AuthenticationStateProvider provider)
        {
            _authSubscribed = true;
            provider.AuthenticationStateChanged += OnAuthStateChanged;
            _ = RefreshAuthStateAsync(provider);
        }
    }

    private static async Task RefreshAuthStateAsync(AuthenticationStateProvider provider)
    {
        try
        {
            var state = await provider.GetAuthenticationStateAsync();
            SetAuthenticated(state.User?.Identity?.IsAuthenticated ?? false);
        }
        catch
        {
            // Best-effort title sync; default ("Log Out") stays if state can't be read.
        }
    }

    private static void OnAuthStateChanged(Task<AuthenticationState> task)
    {
        _ = task.ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully)
                SetAuthenticated(t.Result.User?.Identity?.IsAuthenticated ?? false);
        }, TaskScheduler.Default);
    }

    private static void SetAuthenticated(bool authenticated)
    {
        OnMainThread(() =>
        {
            if (_authItem is not null)
                _authItem.Title = authenticated ? "Log Out" : "Log In";
        });
    }

    private static void OnAuthItemActivated(object? sender, EventArgs e)
    {
        // Uniform reset: always clear auth + return to the login screen. Robust regardless of
        // the tracked title, and lands on login even from pages that aren't [Authorize]-gated
        // (e.g. onboarding) or behind a sync overlay.
        OnMainThread(async () =>
        {
            try
            {
                var services = Services;
                if (services is null)
                    return;

                // Mirror the in-app NavMenu logout: clear progress caches, sign out
                // (clears JWT/refresh tokens + active profile), then drop the auth flag.
                services.GetService<ProgressCacheService>()?.InvalidateAll();

                if (services.GetService<AuthenticationStateProvider>() is MauiAuthenticationStateProvider auth)
                    await auth.LogOutAsync();

                var prefs = services.GetService<IPreferencesService>();
                prefs?.Set("app_is_authenticated", false);
                prefs?.Remove("active_profile_id");

                // Mirror NavMenu.Logout / Onboarding.Logout: clear the singleton's cached
                // profile so the previous user's profile does not linger across the reload.
                var appState = services.GetService<IAppState>();
                if (appState is not null)
                    appState.CurrentUserProfile = null;

                // Explicitly send the Blazor app to the login route (full reload via the WebView),
                // matching NavMenu.Logout's forceLoad navigation to /auth/login.
                MacOSBlazorHostPage.NavigateToLogin();
            }
            catch (Exception ex)
            {
                // Never let the native escape hatch crash the app (async-void context).
                System.Diagnostics.Debug.WriteLine($"[MacOSAppMenu] Logout failed: {ex}");
            }
        });
    }
}
