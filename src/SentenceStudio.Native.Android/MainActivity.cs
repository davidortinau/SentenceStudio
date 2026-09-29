using Android.App;
using Android.Content.PM;
using Android.Content.Res;
using Android.OS;
using Android.Views;
using AndroidX.Activity;
using AndroidX.Compose;

namespace SentenceStudio.Native.Android;

[Activity(MainLauncher = true, Exported = true, Theme = "@style/SentenceStudioNativeTheme",
    ConfigurationChanges = ConfigChanges.UiMode)]
public sealed class MainActivity : ComponentActivity
{
    private AndroidSliceShell? _shell;
    private NativeBackCallback? _backCallback;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window?.SetSoftInputMode(SoftInput.AdjustResize);
        _shell = new AndroidSliceShell(this);
        _backCallback = new NativeBackCallback(this);
        OnBackPressedDispatcher.AddCallback(this, _backCallback);
        this.SetContent(_shell.Build);
        _shell.Start();
    }

    public override void OnConfigurationChanged(Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        _shell?.RefreshForUiMode();
    }

    internal void UpdateBackHandling(bool canGoBack)
    {
        if (_backCallback is not null)
            _backCallback.Enabled = canGoBack;
    }

    protected override void OnDestroy()
    {
        _shell?.Dispose();
        _shell = null;
        _backCallback = null;
        base.OnDestroy();
    }

    private sealed class NativeBackCallback(MainActivity activity) : OnBackPressedCallback(false)
    {
        public override void HandleOnBackPressed()
        {
            if (activity._shell?.HandleBack() == true)
                return;
            Enabled = false;
            activity.OnBackPressedDispatcher.OnBackPressed();
        }
    }
}
