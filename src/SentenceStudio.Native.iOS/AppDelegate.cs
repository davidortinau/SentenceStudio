using Foundation;
using UIKit;

namespace SentenceStudio.Native.iOS;

[Register("SentenceStudioNativeAppDelegate")]
public sealed class AppDelegate : UIApplicationDelegate
{
    public override bool FinishedLaunching(UIApplication application, NSDictionary launchOptions) => true;
}
