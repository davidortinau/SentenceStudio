using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Storage;
using MauiDevFlow.Agent;
using MauiDevFlow.Blazor;
using Microsoft.AspNetCore.Components.WebView.Maui;
using Microsoft.Maui.Essentials.MacOS;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Platform.MacOS.Hosting;
using Plugin.Maui.Audio;
using SentenceStudio;
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

        builder.Configuration.AddEmbeddedAppSettings();

        builder.AddAudio();

        builder.Services.AddMauiBlazorWebView();
        builder.AddMacOSBlazorWebView();
        RegisterBlazorServices(builder.Services);

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging
            .AddDebug()
            .AddConsole()
            .SetMinimumLevel(LogLevel.Debug);
        builder.AddMauiDevFlowAgent(options => { options.Port = 9224; });
        builder.AddMauiBlazorDevFlowTools();
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
