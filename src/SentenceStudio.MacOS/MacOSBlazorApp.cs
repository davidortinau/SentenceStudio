using Microsoft.Maui.Controls;
using Microsoft.Maui.Platform.MacOS;

namespace SentenceStudio.MacOS;

public class MacOSBlazorApp : Application
{
    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new MacOSBlazorHostPage())
        {
            Width = 1280,
            Height = 800
        };

        // Disable FullSizeContentView so BlazorWebView doesn't cover the titlebar.
        // This makes the window draggable (see shinyorg/mauiplatforms docs/macos/window.md).
        MacOSWindow.SetFullSizeContentView(window, false);

        return window;
    }
}
