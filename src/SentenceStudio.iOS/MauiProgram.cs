using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Storage;
#if DEBUG
using Microsoft.Maui.DevFlow.Agent;
using Microsoft.Maui.DevFlow.Blazor;
#endif
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.LifecycleEvents;
using Plugin.Maui.Audio;
using SentenceStudio;
using SentenceStudio.Services;
using SentenceStudio.Sharing;
using SentenceStudio.WebUI.Services;
using Shiny;
#if IOS
using AVFoundation;
using Foundation;
#endif

namespace SentenceStudio.iOS;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.Configuration.AddEmbeddedAppSettings();
        builder.AddMauiServiceDefaults("iOS");

        builder
            .UseMauiApp<BlazorApp>()
            .UseMauiCommunityToolkit()
            .UseShiny()
            .UseSentenceStudioApp();

        // Wire HelpKit (Plugin.Maui.HelpKit) — multi-targeted net10/net11 during incubation.
        builder.UseHelpKit();

        builder.AddAudio(
            playbackOptions =>
            {
#if IOS
                playbackOptions.Category = AVAudioSessionCategory.Playback;
#endif
            },
            recordingOptions =>
            {
#if IOS
                recordingOptions.Category = AVAudioSessionCategory.Record;
                recordingOptions.Mode = AVAudioSessionMode.Default;
                recordingOptions.CategoryOptions = AVAudioSessionCategoryOptions.MixWithOthers;
#endif
            });


        builder.ConfigureMauiHandlers(handlers =>
        {
            ModifyEntry();
            ModifyPicker();
            ConfigureWebView();
        });

        builder.Services.AddMauiBlazorWebView();
        builder.Services.AddBlazorUIServices();

        // Enable Safari Web Inspector for Release builds on personal/dogfooding devices.
        // Sets WKWebView.Inspectable = true on iOS 16.4+. NOT gated to #if DEBUG because
        // we sideload Release builds to DX24 and still need to diagnose Blazor WebView
        // issues that only reproduce in Release (e.g. JS interop, click handlers).
        // Safe to leave on outside the App Store path (this repo is not App-Store-published).
        builder.Services.AddBlazorWebViewDeveloperTools();

#if DEBUG
        builder.Logging
            .AddDebug()
            .AddConsole()
            .SetMinimumLevel(LogLevel.Debug);
        builder.AddMauiDevFlowAgent(options => { options.Port = 9224; });
        builder.AddMauiBlazorDevFlowTools();
#endif

#if IOS
        // iOS App Group shared ingest queue — written by the Share Extension.
        builder.Services.AddSingleton<ISharedIngestQueue>(_ =>
        {
            var container = NSFileManager.DefaultManager.GetContainerUrl(SharingConstants.AppGroupId);
            var dir = container?.Append(SharingConstants.QueueDirectoryName, true)?.Path
                      ?? System.IO.Path.Combine(FileSystem.AppDataDirectory, SharingConstants.QueueDirectoryName);
            return new FileSystemSharedIngestQueue(dir);
        });

        // Processor is Scoped to match IContentImportService (Scoped dep). Resolved inside
        // a created scope at drain time so it never lives longer than one drain cycle.
        builder.Services.AddScoped<ISharedIngestProcessor, SharedIngestProcessor>();

        // Drain queue on every app activation (safe: processor single-flights + auth-gates)
        builder.ConfigureLifecycleEvents(lifecycle =>
        {
            lifecycle.AddiOS(ios => ios.OnActivated(uiApp =>
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var services = IPlatformApplication.Current?.Services;
                        if (services == null) return;

                        using var scope = services.CreateScope();
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                        var processor = scope.ServiceProvider.GetRequiredService<ISharedIngestProcessor>();

                        await processor.DrainAsync(cts.Token);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[SharedIngest] Drain error: {ex.Message}");
                    }
                });
            }));
        });
#endif

        var app = builder.Build();

        // HelpKit ingest after services are built. Fire-and-forget; never blocks boot.
        SentenceStudio.HelpKitIntegration.TriggerBackgroundIngest(app.Services);

        return SentenceStudioAppBuilder.InitializeApp(app);
    }

    private static void ModifyPicker()
    {
        Microsoft.Maui.Handlers.PickerHandler.Mapper.AppendToMapping("GoodByePickerUnderline", (handler, view) =>
        {
#if IOS
            handler.PlatformView.BorderStyle = UIKit.UITextBorderStyle.None;
#endif
        });
    }

    private static void ConfigureWebView()
    {
#if IOS
        Microsoft.Maui.Handlers.WebViewHandler.PlatformViewFactory = (handler) =>
        {
            var config = Microsoft.Maui.Platform.MauiWKWebView.CreateConfiguration();
            config.AllowsInlineMediaPlayback = true;
            config.AllowsAirPlayForMediaPlayback = true;
            config.AllowsPictureInPictureMediaPlayback = true;
            config.MediaTypesRequiringUserActionForPlayback = WebKit.WKAudiovisualMediaTypes.None;
            return new Microsoft.Maui.Platform.MauiWKWebView(CoreGraphics.CGRect.Empty, (Microsoft.Maui.Handlers.WebViewHandler)handler, config);
        };
#endif
    }

    private static void ModifyEntry()
    {
        Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("NoMoreBorders", (handler, view) =>
        {
#if IOS
            handler.PlatformView.BorderStyle = UIKit.UITextBorderStyle.None;
#endif
        });
    }
}
