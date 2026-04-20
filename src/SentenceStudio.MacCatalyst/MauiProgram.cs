using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Storage;
#if DEBUG
using Microsoft.Maui.DevFlow.Agent;
using Microsoft.Maui.DevFlow.Blazor;
#endif
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Plugin.Maui.Audio;
using SentenceStudio;
using Shiny;
using SentenceStudio.WebUI.Services;
#if MACCATALYST
using AVFoundation;
#endif

namespace SentenceStudio.MacCatalyst;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.Configuration.AddEmbeddedAppSettings();

        builder
            .UseMauiApp<BlazorApp>()
            .UseMauiCommunityToolkit()
            .UseShiny()
            .UseSentenceStudioApp();

        // Wire HelpKit (Plugin.Maui.HelpKit) — multi-targeted net10/net11 during incubation.
        builder.UseHelpKit();

        builder.AddMauiServiceDefaults();

        builder.AddAudio(
            playbackOptions =>
            {
#if MACCATALYST
                playbackOptions.Category = AVAudioSessionCategory.Playback;
#endif
            },
            recordingOptions =>
            {
#if MACCATALYST
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

#if DEBUG
        builder.Logging
            .AddDebug()
            .AddConsole()
            .SetMinimumLevel(LogLevel.Debug);
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.AddMauiDevFlowAgent();
        builder.AddMauiBlazorDevFlowTools();
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
#if MACCATALYST
            handler.PlatformView.BorderStyle = UIKit.UITextBorderStyle.None;
#endif
        });
    }

    private static void ConfigureWebView()
    {
#if MACCATALYST
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
#if MACCATALYST
            handler.PlatformView.BorderStyle = UIKit.UITextBorderStyle.None;
#endif
        });
    }
}
