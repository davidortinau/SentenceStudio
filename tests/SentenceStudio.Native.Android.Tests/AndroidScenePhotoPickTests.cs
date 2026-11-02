using System.Reflection;

namespace SentenceStudio.Native.Android.Tests;

/// <summary>
/// Describe a Scene photo pick across a configuration change: the pending pick
/// callback lives on the activity instance, so a rotation must not recreate the
/// activity (the source MAUI head opts out of the same changes), the launcher is
/// registered in OnCreate (before STARTED) and a configuration change recomposes.
/// </summary>
public sealed class AndroidScenePhotoPickTests
{
    [Fact]
    public void RotationAndResizeKeepTheActivitySoAPendingPickReachesTheScene()
    {
        var activity = Source("MainActivity.cs");
        foreach (var change in new[]
                 {
                     "ConfigChanges.UiMode", "ConfigChanges.Orientation", "ConfigChanges.ScreenSize",
                     "ConfigChanges.ScreenLayout", "ConfigChanges.SmallestScreenSize", "ConfigChanges.Density"
                 })
            Assert.Contains(change, activity);
        Assert.Contains("_shell?.RefreshForUiMode();", Method(activity, "OnConfigurationChanged"));
    }

    [Fact]
    public void ThePickerIsRegisteredInOnCreateAndTheSceneResultRoutesToItsPresenter()
    {
        var activity = Source("MainActivity.cs");
        var onCreate = Method(activity, "OnCreate");
        var register = onCreate.IndexOf("RegisterForActivityResult(", StringComparison.Ordinal);
        Assert.True(register >= 0);
        Assert.True(register < onCreate.IndexOf("this.SetContent(", StringComparison.Ordinal));
        Assert.Contains("ActivityResultContracts.PickVisualMedia()", onCreate);

        var scene = Source("AndroidSliceShell.Scene.Compose.cs");
        Assert.Contains("activity.LaunchPhotoPicker(", scene);
        Assert.Contains("ReferenceEquals(_scene, presenter)", scene);
        Assert.Contains("scene.AddPhotoAsync(photo, token)", scene);
    }

    private static string Method(string source, string name)
    {
        var start = source.IndexOf($" {name}(", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{name} not found.");
        var open = source.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            depth += source[i] == '{' ? 1 : source[i] == '}' ? -1 : 0;
            if (depth == 0)
                return source[open..(i + 1)];
        }
        return source[open..];
    }

    private static string Source(string name)
    {
        var root = typeof(AndroidScenePhotoPickTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "NativeAndroidHeadRoot").Value!;
        return File.ReadAllText(Path.Combine(root, name));
    }
}
