using AppKit;
using Foundation;
using Microsoft.Maui.Platforms.MacOS.Hosting;
using Microsoft.Maui.Platforms.MacOS.Platform;

namespace SentenceStudio.MacOS;

[Register("MauiMacOSApp")]
public class MauiMacOSApp : MacOSMauiApplication
{
    protected override MauiApp CreateMauiApp() => MacOSMauiProgram.CreateMauiApp();

    public override void DidFinishLaunching(NSNotification notification)
    {
        base.DidFinishLaunching(notification);

        // Native Account (Log In / Log Out) menu — installed and re-asserted on activation.
        MacOSAppMenu.RegisterForActivation();
    }
}
