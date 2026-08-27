using Microsoft.Maui.Controls;
using Microsoft.Maui.Platforms.MacOS;
using Microsoft.Maui.Platforms.MacOS.Platform;

namespace SentenceStudio.MacOS;

// Base type is fully qualified deliberately. This file sits in `SentenceStudio.MacOS`, and the
// shared query layer added a `SentenceStudio.Application` namespace — so a bare `Application`
// binds to that namespace, not to the MAUI type. C# resolves a simple name by walking the
// enclosing namespaces first (`SentenceStudio.MacOS`, then `SentenceStudio`, where the member
// namespace matches) and only reaches the `using` directives afterwards, which is why importing
// `Microsoft.Maui.Controls` above is not enough on its own and a compilation-unit `using` alias
// would not help either. Qualifying the name is the one form that cannot lose the lookup race.
public class MacOSBlazorApp : Microsoft.Maui.Controls.Application
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
