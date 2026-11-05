using AVFoundation;

namespace SentenceStudio.Native.iOS.Platform;

/// <summary>
/// Puts the app's audio session in the Playback category before it plays anything. The default
/// category (SoloAmbient) follows the Ring/Silent switch, so with the phone on silent every
/// clip played without sound (DX24, Shadowing, 2026-11-05). The MAUI head sets Playback at
/// startup (SentenceStudio.iOS/MauiProgram.cs), so its audio plays on silent too.
/// </summary>
internal static class NativeAudioSession
{
    public static void UsePlayback()
    {
        try
        {
            var session = AVAudioSession.SharedInstance();
            session.SetCategory(AVAudioSessionCategory.Playback);
            session.SetActive(true);
        }
        catch (Exception)
        {
            // Playback still uses the default session.
        }
    }
}
