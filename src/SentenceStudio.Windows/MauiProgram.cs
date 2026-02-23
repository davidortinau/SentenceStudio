using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Storage;
using MauiDevFlow.Agent;
using Microsoft.Extensions.Logging;
using Plugin.Maui.Audio;
using SentenceStudio;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.Windows;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<BlazorApp>()
            .UseMauiCommunityToolkit()
            .UseSentenceStudioApp();

        builder.Configuration.AddEmbeddedAppSettings();

        builder.AddAudio();

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
}
