using Microsoft.Maui.Controls;

namespace SentenceStudio.MacOS;

public class MacOSBlazorApp : Application
{
    protected override Window CreateWindow(IActivationState? activationState)
    {
        return new Window(new MacOSBlazorHostPage())
        {
            Width = 1280,
            Height = 800
        };
    }
}
