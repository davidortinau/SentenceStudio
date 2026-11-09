using Foundation;

namespace SentenceStudio.iOS;

/// <summary>
/// Adopts the UIScene lifecycle. iOS 27 raises a runtime-issue trap at launch for apps that have
/// not adopted it; under a Debug build the .NET runtime's debugger hook re-runs that trap forever
/// and the app never leaves its launch screen. Named in Info.plist (UIApplicationSceneManifest).
/// </summary>
[Register("SceneDelegate")]
public class SceneDelegate : MauiUISceneDelegate
{
}
