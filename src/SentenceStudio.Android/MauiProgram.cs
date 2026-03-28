using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Storage;
using Microsoft.Maui.DevFlow.Agent;
using Microsoft.Maui.DevFlow.Blazor;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Plugin.Maui.Audio;
using SentenceStudio;
using SentenceStudio.WebUI.Services;
using Shiny;

namespace SentenceStudio.Android;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.Configuration.AddEmbeddedAppSettings();
        builder.AddMauiServiceDefaults();

        builder
            .UseMauiApp<BlazorApp>()
            .UseMauiCommunityToolkit()
            .UseShiny()
            .UseSentenceStudioApp();

        builder.AddAudio();

        builder.ConfigureMauiHandlers(handlers =>
        {
            ModifyEntry();
            ModifyPicker();
        });

        builder.Services.AddMauiBlazorWebView();
        builder.Services.AddBlazorUIServices();

#if DEBUG
        builder.Logging
            .AddDebug()
            .AddConsole()
            .SetMinimumLevel(LogLevel.Debug);
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.AddMauiDevFlowAgent(options => { options.Port = 9225; });
        builder.AddMauiBlazorDevFlowTools();
#endif

        var app = builder.Build();
        return SentenceStudioAppBuilder.InitializeApp(app);
    }

    private static void ModifyPicker()
    {
        Microsoft.Maui.Handlers.PickerHandler.Mapper.AppendToMapping("GoodByePickerUnderline", (handler, view) =>
        {
#if ANDROID
            handler.PlatformView.BackgroundTintList = global::Android.Content.Res.ColorStateList.ValueOf(global::Android.Graphics.Color.Transparent);
#endif
        });
    }

    private static void ModifyEntry()
    {
        Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("NoMoreBorders", (handler, view) =>
        {
#if ANDROID
            handler.PlatformView.BackgroundTintList = global::Android.Content.Res.ColorStateList.ValueOf(global::Android.Graphics.Color.Transparent);
#endif
        });
    }
}
