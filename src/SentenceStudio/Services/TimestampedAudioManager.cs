using ElevenLabs;
using Plugin.Maui.Audio;
using SentenceStudio.Models;
using System.Timers;

namespace SentenceStudio.Services;

/// <summary>
/// Manages timestamped audio playbook with REAL-TIME character-level sentence synchronization
/// No more pre-calculated sentence timings - uses character timestamps for perfect accuracy
/// </summary>
public class TimestampedAudioManager : IDisposable
{
    private IAudioPlayer? _player;
    private TimestampedAudio? _currentAudio;
    private readonly SentenceTimingCalculator _timingCalculator;
    private System.Timers.Timer? _progressTimer;
    private int _currentSentenceIndex = -1;
    private bool _disposed = false;

    // Events for UI updates
    public event EventHandler<int>? SentenceChanged;
    public event EventHandler<double>? ProgressUpdated;
    public event EventHandler? PlaybackEnded;

    public bool IsPlaying => _player?.IsPlaying ?? false;
    public TimeSpan CurrentPosition => TimeSpan.FromSeconds(_player?.CurrentPosition ?? 0.0);
    public TimeSpan Duration => TimeSpan.FromSeconds(_player?.Duration ?? 0.0);
    public int CurrentSentenceIndex => _currentSentenceIndex;

    private List<(int StartCharIdx, int EndCharIdx)> _sentenceCharRanges = new();
    private List<string> _sentences = new();
    
    // Pre-calculated sentence timing lookup for super-fast sentence detection
    private Dictionary<double, int> _timestampToSentenceMap = new();
    private List<(double StartTime, double EndTime, int SentenceIndex)> _sentenceTimings = new();

    public TimestampedAudioManager(SentenceTimingCalculator timingCalculator)
    {
        _timingCalculator = timingCalculator;
    }

    /// <summary>
    /// Loads timestamped audio with character-level timing data for real-time sync
    /// </summary>
    public async Task LoadAudioAsync(TimestampedAudio audio)
    {
        // Dispose existing player
        _player?.Dispose();
        _progressTimer?.Dispose();
        _currentAudio = audio;

        // Precompute sentence-to-character-index mapping
        _sentences = SentenceTimingCalculator.SplitIntoSentences(audio.FullTranscript);
        _sentenceCharRanges = BuildSentenceCharRanges(_sentences, audio.FullTranscript, audio.Characters);

        // 🎯 NEW: Pre-build efficient timestamp-to-sentence lookup table
        _sentenceTimings = BuildSentenceTimings(_sentenceCharRanges, audio.Characters);
        _timestampToSentenceMap = BuildTimestampLookupTable(_sentenceTimings);

        // Print out the full transcript
        System.Diagnostics.Debug.WriteLine("=== Full Transcript ===");
        System.Diagnostics.Debug.WriteLine(audio.FullTranscript);

        // Print out the sentences
        System.Diagnostics.Debug.WriteLine("=== Sentences ===");
        for (int i = 0; i < _sentences.Count; i++)
        {
            System.Diagnostics.Debug.WriteLine($"Sentence {i}: {_sentences[i]}");
        }

        // Print out the character data with timestamps
        System.Diagnostics.Debug.WriteLine("=== Character Timestamp Data ===");
        for (int i = 0; i < audio.Characters.Length; i++)
        {
            var c = audio.Characters[i];
            System.Diagnostics.Debug.WriteLine($"CharIdx {i}: '{c.Character}' [{c.StartTime:F2}s - {c.EndTime:F2}s]");
        }

        // Print out the sentence-to-character index mapping
        System.Diagnostics.Debug.WriteLine("=== Sentence Character Ranges ===");
        for (int i = 0; i < _sentenceCharRanges.Count; i++)
        {
            var (start, end) = _sentenceCharRanges[i];
            System.Diagnostics.Debug.WriteLine($"Sentence {i}: CharIdx {start} to {end} => \"{audio.FullTranscript.Substring(start, end - start + 1)}\"");
        }

        // Print out the pre-calculated sentence timings
        System.Diagnostics.Debug.WriteLine("=== 🎯 Pre-calculated Sentence Timings ===");
        for (int i = 0; i < _sentenceTimings.Count; i++)
        {
            var (startTime, endTime, sentenceIdx) = _sentenceTimings[i];
            System.Diagnostics.Debug.WriteLine($"Sentence {sentenceIdx}: {startTime:F2}s - {endTime:F2}s");
        }

        try
        {
            // Create audio player from data
            var audioStream = new MemoryStream(audio.AudioData);
            _player = AudioManager.Current.CreatePlayer(audioStream);
            _player.PlaybackEnded += OnPlaybackEnded;

            // Setup progress tracking timer (50ms for smooth real-time updates)
            _progressTimer = new System.Timers.Timer(50);
            _progressTimer.Elapsed += OnProgressTimerElapsed;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"🏴‍☠️ Error loading audio: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Build a mapping from sentence index to (startCharIdx, endCharIdx) in the character array
    /// </summary>
    private List<(int StartCharIdx, int EndCharIdx)> BuildSentenceCharRanges(List<string> sentences, string transcript, TimestampedTranscriptCharacter[] characters)
    {
        var ranges = new List<(int, int)>();
        int charArrayPos = 0;
        foreach (var sentence in sentences)
        {
            // Remove leading/trailing whitespace for matching
            var trimmedSentence = sentence.Trim();
            if (string.IsNullOrEmpty(trimmedSentence))
            {
                ranges.Add((0, 0));
                continue;
            }
            // Find start
            int startCharIdx = -1;
            int matchPos = 0;
            for (; charArrayPos < characters.Length; charArrayPos++)
            {
                // Skip whitespace in both
                while (matchPos < trimmedSentence.Length && char.IsWhiteSpace(trimmedSentence[matchPos])) matchPos++;
                if (matchPos >= trimmedSentence.Length) break;
                if (characters[charArrayPos].Character.ToString() == trimmedSentence[matchPos].ToString())
                {
                    if (startCharIdx == -1)
                        startCharIdx = charArrayPos;
                    matchPos++;
                    // If we've matched the whole sentence, break
                    if (matchPos >= trimmedSentence.Length)
                        break;
                }
            }
            int endCharIdx = charArrayPos;
            // Clamp indices
            startCharIdx = Math.Max(0, Math.Min(startCharIdx, characters.Length - 1));
            endCharIdx = Math.Max(0, Math.Min(endCharIdx, characters.Length - 1));
            ranges.Add((startCharIdx, endCharIdx));
            charArrayPos = endCharIdx + 1;
        }
        return ranges;
    }

    /// <summary>
    /// 🎯 Pre-calculate sentence timings from character ranges for super-fast lookup
    /// </summary>
    private List<(double StartTime, double EndTime, int SentenceIndex)> BuildSentenceTimings(
        List<(int StartCharIdx, int EndCharIdx)> sentenceRanges, 
        TimestampedTranscriptCharacter[] characters)
    {
        var timings = new List<(double, double, int)>();
        
        for (int sentenceIdx = 0; sentenceIdx < sentenceRanges.Count; sentenceIdx++)
        {
            var (startCharIdx, endCharIdx) = sentenceRanges[sentenceIdx];
            
            // Clamp indices to valid range
            startCharIdx = Math.Max(0, Math.Min(startCharIdx, characters.Length - 1));
            endCharIdx = Math.Max(0, Math.Min(endCharIdx, characters.Length - 1));
            
            var startTime = characters[startCharIdx].StartTime;
            var endTime = characters[endCharIdx].EndTime;
            
            timings.Add((startTime, endTime, sentenceIdx));
            
            System.Diagnostics.Debug.WriteLine($"🎯 Sentence {sentenceIdx}: {startTime:F2}s - {endTime:F2}s");
        }
        
        return timings;
    }

    /// <summary>
    /// 🎯 Build a super-fast timestamp lookup table (every 100ms resolution)
    /// Maps timestamp -> sentence index for O(1) lookups during playback
    /// </summary>
    private Dictionary<double, int> BuildTimestampLookupTable(List<(double StartTime, double EndTime, int SentenceIndex)> sentenceTimings)
    {
        var lookupTable = new Dictionary<double, int>();
        
        if (!sentenceTimings.Any()) return lookupTable;
        
        // Resolution: every 100ms for balance of accuracy vs memory
        const double resolution = 0.1; // 100ms
        
        var maxTime = sentenceTimings.Max(t => t.EndTime);
        
        for (double time = 0; time <= maxTime; time += resolution)
        {
            // Find which sentence this timestamp belongs to
            var sentenceIndex = -1;
            
            for (int i = 0; i < sentenceTimings.Count; i++)
            {
                var (startTime, endTime, idx) = sentenceTimings[i];
                if (time >= startTime && time <= endTime)
                {
                    sentenceIndex = idx;
                    break;
                }
            }
            
            // Store the mapping (round to resolution to ensure exact key matches)
            var key = Math.Round(time, 1);
            lookupTable[key] = sentenceIndex;
        }
        
        System.Diagnostics.Debug.WriteLine($"🎯 Built timestamp lookup table with {lookupTable.Count} entries (0.0s to {maxTime:F1}s)");
        
        return lookupTable;
    }

    /// <summary>
    /// Plays audio from the beginning with real-time sentence tracking
    /// </summary>
    public void Play()
    {
        if (_player == null) return;

        _player.Play();
        _progressTimer?.Start();
        System.Diagnostics.Debug.WriteLine("🏴‍☠️ TimestampedAudioManager: Real-time playback started");
    }

    /// <summary>
    /// Pauses audio playback
    /// </summary>
    public void Pause()
    {
        if (_player == null) return;

        _player.Pause();
        _progressTimer?.Stop();
        System.Diagnostics.Debug.WriteLine("🏴‍☠️ TimestampedAudioManager: Playback paused");
    }

    /// <summary>
    /// Stops audio playback
    /// </summary>
    public void Stop()
    {
        if (_player == null) return;

        _player.Stop();
        _progressTimer?.Stop();
        _currentSentenceIndex = -1;
        System.Diagnostics.Debug.WriteLine("🏴‍☠️ TimestampedAudioManager: Playback stopped");
    }

    /// <summary>
    /// Seeks to a specific sentence using REAL-TIME character timestamp calculation
    /// No pre-calculated timing - calculates sentence start time on demand!
    /// </summary>
    public async Task PlayFromSentenceAsync(int sentenceIndex)
    {
        if (_player == null || _currentAudio == null || sentenceIndex < 0)
        {
            System.Diagnostics.Debug.WriteLine($"⚠️ Invalid sentence index or no audio loaded: {sentenceIndex}");
            return;
        }

        // Get sentence timing info calculated in real-time from character timestamps
        var sentenceInfo = _timingCalculator.GetSentenceTimingInfo(
            sentenceIndex, 
            _currentAudio.Characters, 
            _currentAudio.FullTranscript
        );

        if (sentenceInfo == null)
        {
            System.Diagnostics.Debug.WriteLine($"⚠️ Could not calculate timing for sentence {sentenceIndex}");
            return;
        }

        try
        {
            _player.Seek(sentenceInfo.StartTime);
            _currentSentenceIndex = sentenceIndex;
            
            if (!IsPlaying)
            {
                Play();
            }

            System.Diagnostics.Debug.WriteLine($"🏴‍☠️ TimestampedAudioManager: Playing from sentence {sentenceIndex} at {sentenceInfo.StartTime:F2}s - '{sentenceInfo.Text.Substring(0, Math.Min(40, sentenceInfo.Text.Length))}...'");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"🏴‍☠️ Error seeking to sentence {sentenceIndex}: {ex.Message}");
        }
    }

    /// <summary>
    /// Seeks to the next sentence using real-time calculation
    /// </summary>
    public Task NextSentenceAsync()
    {
        return PlayFromSentenceAsync(_currentSentenceIndex + 1);
    }

    /// <summary>
    /// Seeks to the previous sentence using real-time calculation
    /// </summary>
    public async Task PreviousSentenceAsync()
    {
        if (_currentSentenceIndex > 0)
        {
            await PlayFromSentenceAsync(_currentSentenceIndex - 1);
        }
    }

    /// <summary>
    /// Gets real-time timing info for a specific sentence calculated from character timestamps
    /// </summary>
    public SentenceTimingInfo? GetSentenceTimingInfo(int sentenceIndex)
    {
        if (_currentAudio == null) return null;

        return _timingCalculator.GetSentenceTimingInfo(
            sentenceIndex,
            _currentAudio.Characters,
            _currentAudio.FullTranscript
        );
    }

    /// <summary>
    /// Gets all sentences split dynamically from the transcript
    /// </summary>
    public List<string> GetSentences()
    {
        return _sentences ?? new List<string>();
    }

    /// <summary>
    /// 🎯 SUPER-FAST real-time progress tracking using pre-calculated lookup table
    /// No more character searching or looping - just O(1) dictionary lookup!
    /// </summary>
    private void OnProgressTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        if (_player?.IsPlaying != true || _currentAudio == null) 
            return;
            
        var currentTime = _player.CurrentPosition;
        ProgressUpdated?.Invoke(this, currentTime);
        
        // 🎯 FAST LOOKUP: Round to our resolution and check lookup table
        var lookupKey = Math.Round(currentTime, 1); // Round to 100ms resolution
        
        int newSentenceIndex = -1;
        if (_timestampToSentenceMap.TryGetValue(lookupKey, out var sentenceIdx))
        {
            newSentenceIndex = sentenceIdx;
        }
        else
        {
            // Fallback: find closest timestamp in our lookup table
            var closestKey = _timestampToSentenceMap.Keys
                .Where(k => Math.Abs(k - currentTime) < 0.2) // Within 200ms
                .OrderBy(k => Math.Abs(k - currentTime))
                .FirstOrDefault();
                
            if (closestKey > 0 && _timestampToSentenceMap.TryGetValue(closestKey, out var fallbackIdx))
            {
                newSentenceIndex = fallbackIdx;
            }
        }
        
        // Update sentence if it changed
        if (newSentenceIndex != _currentSentenceIndex && newSentenceIndex >= 0 && newSentenceIndex < _sentences.Count)
        {
            System.Diagnostics.Debug.WriteLine($"🎯 [FAST LOOKUP] Sentence changed: {_currentSentenceIndex} -> {newSentenceIndex} at {currentTime:F2}s");
            _currentSentenceIndex = newSentenceIndex;
            SentenceChanged?.Invoke(this, _currentSentenceIndex);
        }
    }

    /// <summary>
    /// Find the character index in the timestamp array for a given audio time
    /// </summary>
    private int GetCharacterIndexAtTime(double timeSeconds, TimestampedTranscriptCharacter[] characters)
    {
        for (int i = 0; i < characters.Length; i++)
        {
            var character = characters[i];
            if (timeSeconds >= character.StartTime && timeSeconds <= character.EndTime)
            {
                return i;
            }
        }
        // If exact match not found, find the closest character
        var closestIndex = -1;
        var minDifference = double.MaxValue;
        for (int i = 0; i < characters.Length; i++)
        {
            var character = characters[i];
            var difference = Math.Abs(timeSeconds - character.StartTime);
            if (difference < minDifference)
            {
                minDifference = difference;
                closestIndex = i;
            }
        }
        return closestIndex;
    }

    private void OnPlaybackEnded(object? sender, EventArgs e)
    {
        _progressTimer?.Stop();
        _currentSentenceIndex = -1;
        PlaybackEnded?.Invoke(this, EventArgs.Empty);
        System.Diagnostics.Debug.WriteLine("🏴‍☠️ TimestampedAudioManager: Playback ended");
    }

    /// <summary>
    /// Helper methods for sentence detection (consistent with SentenceTimingCalculator)
    /// </summary>
    private bool IsSentenceDelimiter(char c)
    {
        return c == '.' || c == '!' || c == '?' || c == '。' || c == '！' || c == '？';
    }

    private bool IsEndOfSentence(string text, int delimiterIndex)
    {
        if (delimiterIndex + 1 >= text.Length) return true;
        
        var nextChar = text[delimiterIndex + 1];
        if (!char.IsWhiteSpace(nextChar)) return false;
        
        for (int i = delimiterIndex + 1; i < text.Length; i++)
        {
            if (!char.IsWhiteSpace(text[i]))
            {
                return char.IsUpper(text[i]) || char.IsDigit(text[i]);
            }
        }
        
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;

        _progressTimer?.Dispose();
        _player?.Dispose();
        _disposed = true;
    }
}
