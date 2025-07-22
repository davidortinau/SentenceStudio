using ElevenLabs;
using ElevenLabs.Models;
using ElevenLabs.TextToSpeech;

namespace SentenceStudio.Models;

/// <summary>
/// Enhanced audio result with character-level timestamps and full transcript for real-time synchronization
/// </summary>
public class TimestampedAudio
{
    public byte[] AudioData { get; set; } = Array.Empty<byte>();
    public TimestampedTranscriptCharacter[] Characters { get; set; } = Array.Empty<TimestampedTranscriptCharacter>();
    public string FullTranscript { get; set; } = string.Empty;
    public double Duration { get; set; }  // Duration in seconds
}

/// <summary>
/// Legacy result from ElevenLabs text-to-speech with character-level timestamps
/// Use TimestampedAudio for new real-time synchronization features
/// </summary>
public class TimestampedAudioResult
{
    public ReadOnlyMemory<byte> AudioData { get; set; }
    public TimestampedTranscriptCharacter[] Characters { get; set; }
    public TimeSpan Duration { get; set; }
    public string CacheFilePath { get; set; } = string.Empty;
}

/// <summary>
/// Timing information for a specific sentence within the full audio
/// </summary>
public class SentenceTimingInfo
{
    public int SentenceIndex { get; set; }
    public string Text { get; set; } = string.Empty;
    public double StartTime { get; set; }  // Seconds from start of audio
    public double EndTime { get; set; }    // Seconds from start of audio
    public int StartCharIndex { get; set; }  // Character index in full transcript
    public int EndCharIndex { get; set; }    // Character index in full transcript
}
