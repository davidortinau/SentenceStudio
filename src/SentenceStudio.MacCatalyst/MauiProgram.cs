using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Storage;
using MauiDevFlow.Agent;
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
        RegisterBlazorServices(builder.Services);

#if DEBUG
        builder.Logging
            .AddDebug()
            .AddConsole()
            .SetMinimumLevel(LogLevel.Debug);
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.AddMauiDevFlowAgent(options => { options.Port = 9224; });
#endif

        var app = builder.Build();
        return SentenceStudioAppBuilder.InitializeApp(app);
    }

    private static void RegisterBlazorServices(IServiceCollection services)
    {
        services.AddSingleton<ToastService>();
        services.AddSingleton<ModalService>();
        services.AddSingleton<BlazorLocalizationService>();
        services.AddSingleton<BlazorNavigationService>();
        services.AddScoped<NavigationMemoryService>();
        services.AddScoped<JsInteropService>();
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
