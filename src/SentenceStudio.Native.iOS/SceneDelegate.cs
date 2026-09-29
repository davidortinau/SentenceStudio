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
    private NativeAppController? _app;

    [Export("window")]
    public UIWindow? Window { get; set; }

    [Export("scene:willConnectToSession:options:")]
    public void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions options)
    {
        if (scene is not UIWindowScene windowScene)
            return;

        var slot = Views.VStack(Views.Text("Checking sign-in..."));
        _root = Views.Navigation("SentenceStudio", slot);
        _hostingController = SwiftUIHost.CreateController(_root);
        Window = new UIWindow(windowScene)
        {
            RootViewController = _hostingController
        };
        Window.MakeKeyAndVisible();
#if AILOHA_DEBUG
        AilohaDebug.Start();
#endif
        _app = new NativeAppController(
            slot, _hostingController,
            () => Window?.RootViewController,
            controller => {
                if (Window is { } window)
                {
                    window.RootViewController = controller;
                    window.MakeKeyAndVisible();
                }
            });
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
        Window = null;
    }
}
