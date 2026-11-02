namespace SentenceStudio.Native;

/// <summary>
/// Number Drill's audio notice as a shared message: source ShowWarning for
/// offline audio and ShowError for a playback failure (NumberDrill.razor:743,
/// 767). Both are banners that stay until the next action.
/// </summary>
public static class NativeNumberDrillMessages
{
    public static NativeMessage Notice(string key) => key == NativeNumberDrillText.AudioOffline
        ? NativeMessage.Warning(key) : NativeMessage.Error(key);
}
