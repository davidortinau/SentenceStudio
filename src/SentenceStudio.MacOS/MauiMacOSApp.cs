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
#if DEBUG
        DevFlowMacOSBridge.Trace("▶️ MauiMacOSApp.DidFinishLaunching entered (before base).");
#endif

        base.DidFinishLaunching(notification);

#if DEBUG
        DevFlowMacOSBridge.Trace("◀️ base.DidFinishLaunching returned — MauiApp built, window created.");

        // UPSTREAM WORKAROUND (MAUI DevFlow 0.25.0-dev).
        // AddMauiDevFlowAgent() starts the agent via app.Dispatcher.Dispatch(...) from its own
        // MacOSLifecycle.DidFinishLaunching handler — i.e. it defers work that is already on the
        // main thread. If the main thread blocks before the run loop next drains (SecureStorage /
        // Keychain reads at startup do exactly that on this head), the queued Start never runs and
        // the agent never listens, while stdout still prints "Agent started on port N".
        // Start it synchronously here instead. Idempotent; see DevFlowMacOSBridge for the analysis.
        DevFlowMacOSBridge.StartAgentIfNeeded(Services);
#endif

        // Native Account (Log In / Log Out) menu — installed and re-asserted on activation.
        MacOSAppMenu.RegisterForActivation();
    }
}
