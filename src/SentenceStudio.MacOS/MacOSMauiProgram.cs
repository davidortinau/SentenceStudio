using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Storage;
#if DEBUG
using Microsoft.Maui.DevFlow.Agent;
using Microsoft.Maui.DevFlow.Blazor;
#endif
using Microsoft.AspNetCore.Components.WebView.Maui;
using Microsoft.Maui.Platforms.MacOS.Essentials;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Platforms.MacOS.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plugin.Maui.Audio;
using SentenceStudio;
using SentenceStudio.Abstractions;
using SentenceStudio.Abstractions.Keychain;
using SentenceStudio.MacOS.Platform;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.MacOS;

public static class MacOSMauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiAppMacOS<MacOSBlazorApp>()
            .AddMacOSEssentials()
            .UseMauiCommunityToolkit()
            .UseSentenceStudioApp();

        builder.AddMauiServiceDefaults("MacOS");

        builder.Configuration.AddEmbeddedAppSettings();

        builder.AddAudio();

        builder.Services.AddMauiBlazorWebView();
        builder.AddMacOSBlazorWebView();
        builder.Services.AddBlazorUIServices();

        // macOS AppKit ONLY. UseSentenceStudioApp() registers MauiSecureStorageService, which on
        // this head calls MAUI's macOS SecureStorage -> SecItemCopyMatching against the legacy
        // file-based keychain. Legacy items are ACL-gated on the creating binary's code signature
        // and Debug builds here are ad-hoc signed, so every rebuild changes the cdhash, macOS puts
        // up a modal SecurityAgent prompt, and the read blocks forever instead of returning or
        // throwing — startup then hangs on "Checking authentication...".
        //
        // MacOSKeychainGate suppresses that prompt for automatic reads via Apple's
        // SecKeychainSetUserInteractionAllowed, so a read that would prompt fails fast with a
        // typed InteractionRequired status and the app routes to signed-out UI instead.
        // Registered here, not in AppLib, so iOS/Android/Windows/Mac Catalyst are untouched.
        builder.Services.AddSingleton<IKeychainGate, MacOSKeychainGate>();
        builder.Services.Replace(
            ServiceDescriptor.Singleton<ISecureStorageService, KeychainSecureStorageService>());

        // The bare (un-namespaced) accounts this app used before namespacing live in the SAME
        // machine-global service as every other MAUI app's secrets, so "there is something at
        // auth_refresh" is not evidence that it is ours. LegacyCredentialAdoption is the only code
        // allowed near those names: it corroborates ownership from the payload before copying
        // anything, never deletes, and records a durable decision so it runs at most once per
        // install. Registered here only — no other head shares its keychain namespace.
        builder.Services.AddSingleton<ILegacyAdoptionJournal>(sp =>
            new PreferencesLegacyAdoptionJournal(
                sp.GetRequiredService<IPreferencesService>(),
                sp.GetService<ILogger<PreferencesLegacyAdoptionJournal>>()));
        builder.Services.AddSingleton<LegacyCredentialAdoption>();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging
            .AddDebug()
            .AddConsole()
            .SetMinimumLevel(LogLevel.Debug);
        builder.AddMauiDevFlowAgent(options => { options.Port = 9225; });
        builder.AddMauiBlazorDevFlowTools();
#endif

        var app = builder.Build();

        // One probe, before any auth-state resolution, so a corroborated pre-namespacing session
        // is available to the first restore and an uncorroborated one is permanently refused.
        // Fire-and-forget with its own guard: adoption is opportunistic and must never delay or
        // fail startup.
        _ = Task.Run(async () =>
        {
            try
            {
                await app.Services.GetRequiredService<LegacyCredentialAdoption>()
                    .TryAdoptAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                app.Services.GetService<ILogger<MacOSBlazorApp>>()?
                    .LogWarning(ex, "Legacy keychain adoption probe failed; continuing without it.");
            }
        });

        return SentenceStudioAppBuilder.InitializeApp(app);
    }

}
