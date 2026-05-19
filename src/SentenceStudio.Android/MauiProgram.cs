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
        builder.AddMauiServiceDefaults("Android");

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
        builder.AddMauiDevFlowAgent(options => { options.Port = 9225; });
        builder.AddMauiBlazorDevFlowTools();
#endif

        var app = builder.Build();
        return SentenceStudioAppBuilder.InitializeApp(app);
    }

    /// <summary>
    /// Propagates the IME (on-screen keyboard) bottom inset to the BlazorWebView
    /// as <c>paddingBottom</c>. Since .NET 9, MauiAppCompatActivity calls
    /// <c>WindowCompat.SetDecorFitsSystemWindows(false)</c>, which puts the app
    /// into edge-to-edge mode where <c>android:windowSoftInputMode="adjustResize"</c>
    /// no longer resizes the activity automatically. Without this, the keyboard
    /// floats over the WebView, the WebView keeps its full height, the browser
    /// auto-scrolls the focused input into view, and the sticky PageHeader
    /// (which is sticky within the DOM's <c>&lt;main&gt;</c>) rides off the top.
    /// By padding the WebView's bottom by the IME inset, the WebView's
    /// effective height shrinks so its DOM can scroll the input into view
    /// without disturbing the sticky header.
    /// Related: dotnet/maui#27314 (.NET 9 regression), #18964, #28790, PR #33902.
    /// </summary>
    private static void ConfigureBlazorWebView()
    {
#if ANDROID
        Microsoft.AspNetCore.Components.WebView.Maui.BlazorWebViewHandler.BlazorWebViewMapper.AppendToMapping(
            "ApplyImeInsetPadding",
            (handler, view) =>
            {
                var webView = handler.PlatformView;
                if (webView is null)
                    return;

                AndroidX.Core.View.ViewCompat.SetOnApplyWindowInsetsListener(webView, new ImeInsetListener());

                // Trigger an initial dispatch so the listener fires once the view is attached.
                webView.ViewAttachedToWindow += (_, _) =>
                {
                    AndroidX.Core.View.ViewCompat.RequestApplyInsets(webView);
                };
            });
#endif
    }

#if ANDROID
    private sealed class ImeInsetListener : Java.Lang.Object, AndroidX.Core.View.IOnApplyWindowInsetsListener
    {
        public AndroidX.Core.View.WindowInsetsCompat OnApplyWindowInsets(
            global::Android.Views.View v,
            AndroidX.Core.View.WindowInsetsCompat insets)
        {
            var imeInsets = insets.GetInsets(AndroidX.Core.View.WindowInsetsCompat.Type.Ime());
            var bottom = imeInsets.Bottom;

            // Apply only when the value actually changes to avoid layout thrash.
            if (v.PaddingBottom != bottom)
            {
                v.SetPadding(v.PaddingLeft, v.PaddingTop, v.PaddingRight, bottom);
            }

            return insets;
        }
    }
#endif

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
