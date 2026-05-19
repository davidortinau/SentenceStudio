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
using SentenceStudio.WebUI.Services;
using Shiny;
#if IOS
using AVFoundation;
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
            ConfigureBlazorWebView();
        });

        builder.Services.AddMauiBlazorWebView();
        builder.Services.AddBlazorUIServices();

#if DEBUG
        builder.Logging
            .AddDebug()
            .AddConsole()
            .SetMinimumLevel(LogLevel.Debug);
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.AddMauiDevFlowAgent(options => { options.Port = 9224; });
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

    /// <summary>
    /// Defensive belt-and-suspenders for the mobile keyboard fix on iOS.
    /// The primary fix lives in <c>wwwroot/index.html</c> (JS that locks
    /// <c>window.scrollTo(0,0)</c> and sets a <c>--app-h</c> CSS variable from
    /// <c>visualViewport.height</c>) plus <c>.app-vh</c> in <c>app.css</c>.
    /// 
    /// Setting <c>ScrollView.ScrollEnabled = false</c> here only blocks
    /// user pan gestures — it does NOT stop WKWebView's programmatic
    /// auto-scroll on input focus. That's why the JS handler is the
    /// actual fix. We still disable user scrolling so a stray flick can't
    /// push the document off and reveal the gap above the sticky header.
    /// Related: dotnet/maui#28790, dotnet/maui#18964.
    /// </summary>
    private static void ConfigureBlazorWebView()
    {
#if IOS
        Microsoft.AspNetCore.Components.WebView.Maui.BlazorWebViewHandler.BlazorWebViewMapper.AppendToMapping(
            "DisableOuterScroll",
            (handler, view) =>
            {
                var wk = handler.PlatformView;
                if (wk?.ScrollView is { } sv)
                {
                    sv.ScrollEnabled = false;
                    sv.Bounces = false;
                    sv.BouncesZoom = false;
                    sv.ShowsVerticalScrollIndicator = false;
                    sv.ShowsHorizontalScrollIndicator = false;
                }
            });
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
