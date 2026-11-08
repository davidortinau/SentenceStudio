using Android.Views;
using AndroidX.Compose;
using AndroidX.Compose.Runtime;

namespace SentenceStudio.Native.Android;

/// <summary>
/// The launch look while a remembered session is restored (as iOS 25fef602): the system
/// splash's color and logo (Resources/drawable/launch_icon.xml, the MAUI head's splash)
/// stay until sign-in is resolved, so a restored session goes straight to Study and Sign In
/// (or the profile sync) is drawn only when there is no session or the restore failed.
/// A restore that takes longer than a moment (offline, a slow token refresh) adds a
/// progress indicator under the logo; the screen is announced as loading.
/// </summary>
internal sealed partial class AndroidSliceShell
{
    private bool _launching = true;

    /// <summary>Same value as values/colors.xml launch_background (a contract test keeps them equal).</summary>
    private static readonly Color LaunchBackground = Color.FromHex("#0c0b10");

    private static readonly TimeSpan LaunchProgressDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The logo's size as the system splash draws launch_icon.xml (its 108 dp item scaled into
    /// the splash icon area): measured 404 px against 232 px for a 108 dp image at 3.0 density
    /// on API 36 (emulator, 2026-11-07), so the logo does not jump when the splash hands over.
    /// </summary>
    private const int LaunchLogoDp = 188;

    /// <summary>
    /// Drawn edge to edge, like the system splash, so the logo stays where the splash drew it.
    /// </summary>
    private ComposableNode RenderLaunch() => new Composed(composer =>
    {
        var slow = composer.Remember(() => new MutableState<bool>(false));
        composer.Effect("native.launch.progress", async cancellationToken =>
        {
            await Task.Delay(LaunchProgressDelay, cancellationToken);
            slow.Value = true;
        });
        var loading = Localize("Common_Loading");
        var screen = new Box
        {
            Modifier.FillMaxSize().Background(LaunchBackground).TestTag("native.launch")
                .Semantics(semantics => semantics.ContentDescription($"SentenceStudio. {loading}")),
            new Image(Resource.Mipmap.appicon_foreground, null)
            {
                Modifier = Modifier.Size(LaunchLogoDp).Align(Alignment.Center),
            },
        };
        if (slow.Value)
            screen.Add(new CircularProgressIndicator
            {
                // Under the logo (half its height plus a gap), so the logo does not move.
                Modifier = Modifier.Align(Alignment.Center).Padding(top: LaunchLogoDp + 60).Size(28)
                    .TestTag("native.launch.progress"),
                Color = Color.FromHex("#f2f2f2"),
            });
        return screen;
    });

    /// <summary>The launch look is dark in both modes: light system-bar icons until it ends.</summary>
    private void ApplyLaunchSystemBars() => SetLightSystemBars(false);

    /// <summary>Sign-in is resolved; the next frame sets the bars for the app's theme (<see cref="FollowThemeSystemBars"/>).</summary>
    private void EndLaunch() => _launching = false;

    private bool? _lightSystemBars;

    /// <summary>
    /// The system-bar icons follow the app's own theme (Settings can choose Dark while the
    /// device is light), not only the device mode the window theme starts with.
    /// </summary>
    private void FollowThemeSystemBars(IComposer composer, bool darkScheme)
    {
        var light = !_launching && !darkScheme;
        composer.SideEffect(() =>
        {
            if (_lightSystemBars != light)
                SetLightSystemBars(light);
        });
    }

    private void SetLightSystemBars(bool light)
    {
        _lightSystemBars = light;
        if (activity.Window?.InsetsController is not { } controller)
            return;
        const int mask = (int)(WindowInsetsControllerAppearance.LightStatusBars
            | WindowInsetsControllerAppearance.LightNavigationBars);
        controller.SetSystemBarsAppearance(light ? mask : 0, mask);
    }
}
