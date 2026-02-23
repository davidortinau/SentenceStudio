using AppKit;
using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Storage;
using Foundation;
using MauiDevFlow.Agent;
using MauiDevFlow.Blazor;
using Microsoft.AspNetCore.Components.WebView.Maui;
using Microsoft.Maui.Essentials.MacOS;
using Microsoft.Maui.LifecycleEvents;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Platform.MacOS.Hosting;
using ObjCRuntime;
using Plugin.Maui.Audio;
using SentenceStudio;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.MacOS;

public static class MacOSMauiProgram
{
    // Strong reference to prevent GC
    private static NSTimer? _titlebarTimer;

    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiAppMacOS<MacOSBlazorApp>()
            .AddMacOSEssentials()
            .UseMauiCommunityToolkit()
            .UseSentenceStudioApp()
            .ConfigureLifecycleEvents(events =>
            {
                events.AddMacOS(macOS => macOS
                    .DidFinishLaunching(_ =>
                    {
                        // Platform.Maui.MacOS sets FullSizeContentView + transparent titlebar.
                        // Create timer and explicitly add to main run loop.
                        int attempts = 0;
                        _titlebarTimer = NSTimer.CreateRepeatingTimer(0.5, timer =>
                        {
                            attempts++;
                            ApplyTitlebarConfig();
                            if (attempts >= 60)
                            {
                                timer.Invalidate();
                                _titlebarTimer = NSTimer.CreateRepeatingTimer(5.0,
                                    t => ApplyTitlebarConfig());
                                NSRunLoop.Main.AddTimer(_titlebarTimer, NSRunLoopMode.Common);
                            }
                        });
                        NSRunLoop.Main.AddTimer(_titlebarTimer, NSRunLoopMode.Common);
                    })
                );
            });

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

    private static void ApplyTitlebarConfig()
    {
        var windowsObj = NSApplication.SharedApplication.ValueForKey(new NSString("windows"));
        if (windowsObj is NSArray windowsArray && windowsArray.Count > 0)
        {
            var nsWindow = Runtime.GetNSObject<NSWindow>(windowsArray.ValueAt(0));
            if (nsWindow != null && (nsWindow.StyleMask.HasFlag(NSWindowStyle.FullSizeContentView)
                || nsWindow.Title != "Sentence Studio"))
            {
                nsWindow.StyleMask &= ~NSWindowStyle.FullSizeContentView;
                nsWindow.TitlebarAppearsTransparent = false;
                nsWindow.TitleVisibility = NSWindowTitleVisibility.Visible;
                nsWindow.Title = "Sentence Studio";
            }
        }
    }
}
