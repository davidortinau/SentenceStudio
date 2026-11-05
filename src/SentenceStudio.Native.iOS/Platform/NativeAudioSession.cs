using AVFoundation;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.Native.iOS.Platform;

/// <summary>
/// Puts the app's audio session in the Playback category once, at startup. The default category
/// (SoloAmbient) follows the Ring/Silent switch, so with the phone on silent every clip played
/// without sound (DX24, Shadowing, 2026-11-05). The MAUI head sets Playback at startup too
/// (SentenceStudio.iOS/MauiProgram.cs). AVAudioPlayer activates the session when it plays.
/// </summary>
internal static class NativeAudioSession
{
    public static void UsePlayback(ILogger logger)
    {
        var error = AVAudioSession.SharedInstance().SetCategory(AVAudioSessionCategory.Playback);
        if (error is not null)
            logger.LogWarning("Could not set the Playback audio category ({Code}): {Description}",
                error.Code, error.LocalizedDescription);
    }
}
