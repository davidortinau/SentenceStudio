using Foundation;
using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Platform;
using UIKit;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS;

[Register("SentenceStudioNativeSceneDelegate")]
public sealed class SceneDelegate : UIResponder, IUIWindowSceneDelegate
{
    private SwiftView? _root;
    private UIViewController? _hostingController;
    private UINavigationController? _navigationController;
    private NativeAppController? _app;

    [Export("window")]
    public UIWindow? Window { get; set; }

    [Export("scene:willConnectToSession:options:")]
    public void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions options)
    {
        if (scene is not UIWindowScene windowScene)
            return;

        var slot = Views.VStack(Views.Text("Checking sign-in..."));
        _root = slot;
        _hostingController = SwiftUIHost.CreateController(_root);
        _navigationController = new UINavigationController(_hostingController);
        _navigationController.NavigationBar.PrefersLargeTitles = false;
        _navigationController.SetNavigationBarHidden(true, false);
        Window = new UIWindow(windowScene)
        {
            RootViewController = _navigationController
        };
        Window.MakeKeyAndVisible();
        Microsoft.Maui.ApplicationModel.Platform.Init(() => Window?.RootViewController
            ?? throw new InvalidOperationException("The iOS root controller is not available."));
#if AILOHA_DEBUG
        AilohaDebug.Start();
#endif
        _app = new NativeAppController(_navigationController);
        _app.Start();
    }

    [Export("sceneDidDisconnect:")]
    public void DidDisconnect(UIScene scene)
    {
        if (Window is { } window)
            window.RootViewController = null;
        _app?.Dispose();
        _app = null;
#if AILOHA_DEBUG
        AilohaDebug.Stop();
#endif
        if (_root is not null && _hostingController is not null)
            SwiftUIHost.DetachController(_root, _hostingController);
        _hostingController = null;
        _root?.Dispose();
        _root = null;
        _navigationController?.Dispose();
        _navigationController = null;
        Window = null;
    }
}
