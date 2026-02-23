using AppKit;
using CoreFoundation;
using Foundation;
using Microsoft.Maui.Controls;
using ObjCRuntime;

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

        window.Created += (s, e) =>
        {
            // Platform.Maui.MacOS uses FullSizeContentView + TitlebarAppearsTransparent
            // which hides the titlebar behind the WebView. We poll until the native
            // NSWindow is available, then restore a standard draggable titlebar.
            System.Threading.Timer? pollTimer = null;
            int attempts = 0;
            pollTimer = new System.Threading.Timer(state =>
            {
                attempts++;
                DispatchQueue.MainQueue.DispatchAsync(() =>
                {
                    var app = NSApplication.SharedApplication;
                    var windowsObj = app.ValueForKey(new NSString("windows"));
                    if (windowsObj is NSArray windowsArray && windowsArray.Count > 0)
                    {
                        var nsWindow = Runtime.GetNSObject<NSWindow>(windowsArray.ValueAt(0));
                        if (nsWindow != null)
                        {
                            nsWindow.StyleMask &= ~NSWindowStyle.FullSizeContentView;
                            nsWindow.TitlebarAppearsTransparent = false;
                            nsWindow.TitleVisibility = NSWindowTitleVisibility.Visible;
                            nsWindow.Title = "Sentence Studio";
                            pollTimer?.Dispose();
                            return;
                        }
                    }
                    if (attempts >= 20) pollTimer?.Dispose();
                });
            }, null, 500, 500);
        };

        return window;
    }
}
