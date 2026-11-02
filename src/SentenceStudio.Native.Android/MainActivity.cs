using Android.App;
using Android.Content.PM;
using Android.Content.Res;
using Android.OS;
using Android.Views;
using AndroidX.Activity;
using AndroidX.Activity.Result;
using AndroidX.Activity.Result.Contract;
using AndroidX.Compose;

namespace SentenceStudio.Native.Android;

// Rotation, resizing and density changes do not recreate the activity (as the
// source MAUI head's MainActivity): the shell, the open activity presenter and a
// pending system photo pick survive them, and OnConfigurationChanged recomposes.
[Activity(MainLauncher = true, Exported = true, Theme = "@style/SentenceStudioNativeTheme",
    ConfigurationChanges = ConfigChanges.UiMode | ConfigChanges.Orientation | ConfigChanges.ScreenSize
        | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public sealed class MainActivity : ComponentActivity
{
    private AndroidSliceShell? _shell;
    private NativeBackCallback? _backCallback;
    private ActivityResultLauncher? _photoPicker;
    private Action<global::Android.Net.Uri?>? _photoPicked;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window?.SetSoftInputMode(SoftInput.AdjustResize);
        _shell = new AndroidSliceShell(this);
        _backCallback = new NativeBackCallback(this);
        OnBackPressedDispatcher.AddCallback(this, _backCallback);
        // System photo picker for Describe a Scene (Captain-approved 2026-11-01).
        // Registered before STARTED as the Activity Result API requires.
        _photoPicker = RegisterForActivityResult(
            new ActivityResultContracts.PickVisualMedia(), new PhotoPickedCallback(this));
        this.SetContent(_shell.Build);
        _shell.Start();
    }

    public override void OnConfigurationChanged(Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        // Theme, orientation and window size all re-read at composition.
        _shell?.RefreshForUiMode();
    }

    internal void UpdateBackHandling(bool canGoBack)
    {
        if (_backCallback is not null)
            _backCallback.Enabled = canGoBack;
    }

    /// <summary>
    /// Launches the system photo picker (PickVisualMedia, images only). The
    /// picker is system UI, not an app-authored view; the chosen content URI
    /// (or null when cancelled) arrives on the main thread.
    /// </summary>
    internal bool LaunchPhotoPicker(Action<global::Android.Net.Uri?> onPicked)
    {
        if (_photoPicker is null || _photoPicked is not null)
            return false;
        _photoPicked = onPicked;
        try
        {
            _photoPicker.Launch(new PickVisualMediaRequest.Builder()
                .SetMediaType(ActivityResultContracts.PickVisualMedia.ImageOnly.Instance)
                .Build());
            return true;
        }
        catch (Java.Lang.Exception)
        {
            _photoPicked = null;
            return false;
        }
    }

    private sealed class PhotoPickedCallback(MainActivity activity)
        : Java.Lang.Object, IActivityResultCallback
    {
        public void OnActivityResult(Java.Lang.Object? result)
        {
            // Registered in OnCreate, so a result always reaches the current activity.
            // Only a process-death recreation (which also rebuilds the shell and has no
            // Scene presenter left to receive it) arrives with no pending pick.
            var callback = activity._photoPicked;
            activity._photoPicked = null;
            callback?.Invoke(result as global::Android.Net.Uri);
        }

    }

    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        if (e?.KeyCode == Keycode.Escape
            && e.Action == KeyEventActions.Down
            && _shell?.HandlePhotoEscape() == true)
            return true;
        return base.DispatchKeyEvent(e);
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
