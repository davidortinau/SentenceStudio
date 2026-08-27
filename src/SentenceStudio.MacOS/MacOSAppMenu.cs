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

            // Off the main thread on purpose. Reading the auth state goes through
            // ISecureStorageService, which on this head is KeychainSecureStorageService: automatic
            // reads run with the SecurityAgent prompt suppressed and fail fast, so this no longer
            // risks an unbounded block. It is still kept off the AppKit main thread so a slow
            // keychain call cannot stall the run loop before it first turns (which would also
            // stall the MAUI DevFlow agent). SetAuthenticated marshals back via OnMainThread.
            _ = Task.Run(() => RefreshAuthStateAsync(provider));
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

    /// <summary>Runs one sign-out step, never letting its failure stop the rest.</summary>
    private static void Step(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MacOSAppMenu] Logout step '{what}' failed: {ex.GetType().Name}");
        }
    }

    private static async Task StepAsync(string what, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MacOSAppMenu] Logout step '{what}' failed: {ex.GetType().Name}");
        }
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
            // Each step is isolated. This is the native escape hatch: it exists for the case where
            // the in-app UI is already misbehaving, so one failing step must not skip the ones
            // after it — and above all must not skip the navigation, which is what actually gets
            // the learner off a signed-in screen. A single try/catch around the whole block meant
            // an exception anywhere left the app sitting on the previous account's dashboard.
            var services = Services;

            if (services is not null)
            {
                Step("invalidate caches", () => services.GetService<ProgressCacheService>()?.InvalidateAll());

                await StepAsync("sign out", async () =>
                {
                    if (services.GetService<AuthenticationStateProvider>() is MauiAuthenticationStateProvider auth)
                        await auth.LogOutAsync();
                });

                Step("clear auth preferences", () =>
                {
                    var prefs = services.GetService<IPreferencesService>();
                    prefs?.Set("app_is_authenticated", false);
                    prefs?.Remove("active_profile_id");
                });

                Step("clear cached profile", () =>
                {
                    var appState = services.GetService<IAppState>();
                    if (appState is not null)
                        appState.CurrentUserProfile = null;
                });
            }

            // Always runs, whatever happened above.
            Step("navigate to login", MacOSBlazorHostPage.NavigateToLogin);
        });
    }
}
