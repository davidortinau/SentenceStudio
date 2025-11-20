using ElevenLabs;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger<TimestampedAudioManager> _logger;
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

    public TimestampedAudioManager(SentenceTimingCalculator timingCalculator, ILogger<TimestampedAudioManager> logger)
    {
        _timingCalculator = timingCalculator;
        _logger = logger;
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
        
        _logger.LogDebug("🔍 Split transcript into {Count} sentences (including PARAGRAPH_BREAK markers)", _sentences.Count);
        for (int i = 0; i < Math.Min(10, _sentences.Count); i++)
        {
            _logger.LogDebug("  [{Index}] = {Text}", i, 
                _sentences[i] == "PARAGRAPH_BREAK" ? "<<PARAGRAPH_BREAK>>" : _sentences[i].Substring(0, Math.Min(50, _sentences[i].Length)));
        }
        
        _sentenceCharRanges = BuildSentenceCharRanges(_sentences, audio.FullTranscript, audio.Characters);

        // 🎯 NEW: Pre-build efficient timestamp-to-sentence lookup table
        _sentenceTimings = BuildSentenceTimings(_sentenceCharRanges, audio.Characters);
        _timestampToSentenceMap = BuildTimestampLookupTable(_sentenceTimings);

        _logger.LogDebug("=== Full Transcript ===");
        _logger.LogDebug("{Transcript}", audio.FullTranscript);

        _logger.LogDebug("=== Sentences ===");
        for (int i = 0; i < _sentences.Count; i++)
        {
            _logger.LogTrace("Sentence {Index}: {Text}", i, _sentences[i]);
        }

        _logger.LogTrace("=== Character Timestamp Data ===");
        for (int i = 0; i < audio.Characters.Length; i++)
        {
            var c = audio.Characters[i];
            _logger.LogTrace("CharIdx {Index}: '{Character}' [{StartTime:F2}s - {EndTime:F2}s]", i, c.Character, c.StartTime, c.EndTime);
        }

        _logger.LogDebug("=== Sentence Character Ranges ===");
        for (int i = 0; i < _sentenceCharRanges.Count; i++)
        {
            var (start, end) = _sentenceCharRanges[i];
            _logger.LogTrace("Sentence {Index}: CharIdx {StartIdx} to {EndIdx} => \"{Text}\"",
                i, start, end, audio.FullTranscript.Substring(start, end - start + 1));
        }

        _logger.LogDebug("=== 🎯 Pre-calculated Sentence Timings ===");
        for (int i = 0; i < _sentenceTimings.Count; i++)
        {
            var (startTime, endTime, sentenceIdx) = _sentenceTimings[i];
            _logger.LogDebug("Sentence {Index}: {StartTime:F2}s - {EndTime:F2}s", sentenceIdx, startTime, endTime);
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
            _logger.LogError(ex, "Error loading audio: {Message}", ex.Message);
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
            // 🎯 CRITICAL: Skip PARAGRAPH_BREAK markers - they don't exist in the transcript
            // Don't advance charArrayPos for them, as they consume zero characters
            if (sentence == "PARAGRAPH_BREAK")
            {
                _logger.LogTrace("⏭️ PARAGRAPH_BREAK marker encountered, adding placeholder range without advancing position");
                ranges.Add((-1, -1)); // Placeholder range that won't be used for timing
                continue;
            }
            
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
    /// Skips PARAGRAPH_BREAK markers to avoid overlapping time ranges
    /// </summary>
    private List<(double StartTime, double EndTime, int SentenceIndex)> BuildSentenceTimings(
        List<(int StartCharIdx, int EndCharIdx)> sentenceRanges,
        TimestampedTranscriptCharacter[] characters)
    {
        var timings = new List<(double, double, int)>();

        for (int sentenceIdx = 0; sentenceIdx < sentenceRanges.Count; sentenceIdx++)
        {
            // 🎯 CRITICAL: Skip PARAGRAPH_BREAK markers - they have invalid char ranges
            if (sentenceIdx < _sentences.Count && _sentences[sentenceIdx] == "PARAGRAPH_BREAK")
            {
                _logger.LogTrace("⏭️ Skipping PARAGRAPH_BREAK at index {Index} in timing calculation", sentenceIdx);
                continue;
            }
            
            var (startCharIdx, endCharIdx) = sentenceRanges[sentenceIdx];
            
            // Skip invalid ranges (from PARAGRAPH_BREAK placeholders)
            if (startCharIdx < 0 || endCharIdx < 0)
            {
                _logger.LogTrace("⏭️ Skipping invalid range at index {Index}: ({Start}, {End})", sentenceIdx, startCharIdx, endCharIdx);
                continue;
            }

            // Clamp indices to valid range
            startCharIdx = Math.Max(0, Math.Min(startCharIdx, characters.Length - 1));
            endCharIdx = Math.Max(0, Math.Min(endCharIdx, characters.Length - 1));

            var startTime = characters[startCharIdx].StartTime;
            var endTime = characters[endCharIdx].EndTime;

            timings.Add((startTime, endTime, sentenceIdx));

            _logger.LogTrace("Sentence {Index}: {StartTime:F2}s - {EndTime:F2}s", sentenceIdx, startTime, endTime);
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
            
            // 🔍 Log entries with -1 (gaps) or changes
            if (sentenceIndex == -1)
            {
                _logger.LogTrace("⚠️ Lookup table gap at {Time:F1}s -> -1", key);
            }
            
            lookupTable[key] = sentenceIndex;
        }

        _logger.LogDebug("Built timestamp lookup table with {Count} entries (0.0s to {MaxTime:F1}s)", lookupTable.Count, maxTime);
        
        // Log sample entries around sentence 20 area and later sentences
        _logger.LogDebug("=== Sample lookup entries (early) ===");
        for (double t = 0; t <= Math.Min(3.0, maxTime); t += 0.5)
        {
            var key = Math.Round(t, 1);
            if (lookupTable.TryGetValue(key, out var idx))
            {
                _logger.LogDebug("  {Time:F1}s -> sentence {Index}", key, idx);
            }
        }
        
        // Log entries around the middle (where problems start)
        _logger.LogDebug("=== Sample lookup entries (middle) ===");
        var midTime = maxTime / 2.0;
        for (double t = midTime - 2.0; t <= Math.Min(midTime + 2.0, maxTime); t += 0.5)
        {
            var key = Math.Round(t, 1);
            if (lookupTable.TryGetValue(key, out var idx))
            {
                _logger.LogDebug("  {Time:F1}s -> sentence {Index}", key, idx);
            }
        }

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
        _logger.LogInformation("TimestampedAudioManager: Real-time playback started");
    }

    /// <summary>
    /// Pauses audio playback
    /// </summary>
    public void Pause()
    {
        if (_player == null) return;

        _player.Pause();
        _progressTimer?.Stop();
        _logger.LogInformation("TimestampedAudioManager: Playback paused");
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
        _logger.LogInformation("TimestampedAudioManager: Playback stopped");
    }

    /// <summary>
    /// Seeks to a specific sentence using REAL-TIME character timestamp calculation
    /// No pre-calculated timing - calculates sentence start time on demand!
    /// </summary>
    public async Task PlayFromSentenceAsync(int sentenceIndex)
    {
        if (_player == null || _currentAudio == null || sentenceIndex < 0)
        {
            _logger.LogWarning("Invalid sentence index or no audio loaded: {SentenceIndex}", sentenceIndex);
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
            _logger.LogWarning("Could not calculate timing for sentence {SentenceIndex}", sentenceIndex);
            return;
        }

        try
        {
            // Remember if we were playing before seeking (some players stop on seek)
            bool wasPlaying = IsPlaying;
            
            _player.Seek(sentenceInfo.StartTime);
            _currentSentenceIndex = sentenceIndex;

            // Always ensure playback is active - either resume if it was playing, or start fresh
            if (!IsPlaying)
            {
                _logger.LogDebug("Starting playback after seek (wasPlaying: {WasPlaying})", wasPlaying);
                Play();
            }
            else if (wasPlaying)
            {
                // Some platforms might pause on seek, so explicitly resume if we were playing
                _logger.LogDebug("Ensuring playback continues after seek");
            }

            _logger.LogInformation("TimestampedAudioManager: Playing from sentence {SentenceIndex} at {StartTime:F2}s - '{TextPreview}...'",
                sentenceIndex, sentenceInfo.StartTime, sentenceInfo.Text.Substring(0, Math.Min(40, sentenceInfo.Text.Length)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error seeking to sentence {SentenceIndex}: {Message}", sentenceIndex, ex.Message);
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
            _logger.LogTrace("🔍 Lookup {LookupKey:F1}s -> sentence {SentenceIndex} (currentTime={CurrentTime:F2}s, current={CurrentSentenceIndex})", 
                lookupKey, sentenceIdx, currentTime, _currentSentenceIndex);
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
                _logger.LogTrace("🔍 Fallback lookup closest={ClosestKey:F1}s -> sentence {FallbackIndex} (currentTime={CurrentTime:F2}s)", 
                    closestKey, fallbackIdx, currentTime);
            }
            else
            {
                _logger.LogTrace("🔍 No lookup found for {CurrentTime:F2}s (lookupKey={LookupKey:F1}s)", currentTime, lookupKey);
            }
        }

        // 🎯 Handle gaps by staying at current sentence if we hit -1
        if (newSentenceIndex == -1)
        {
            _logger.LogTrace("🔍 Gap in coverage at {CurrentTime:F2}s, staying at sentence {CurrentSentenceIndex}", 
                currentTime, _currentSentenceIndex);
            return; // Don't change sentence during gaps
        }

        // Update sentence if it changed
        if (newSentenceIndex != _currentSentenceIndex && newSentenceIndex >= 0 && newSentenceIndex < _sentences.Count)
        {
            // 🎯 CRITICAL: Skip PARAGRAPH_BREAK markers - they're not real sentences
            if (_sentences[newSentenceIndex] == "PARAGRAPH_BREAK")
            {
                _logger.LogTrace("⏭️ Skipping PARAGRAPH_BREAK marker at index {Index}", newSentenceIndex);
                _currentSentenceIndex = newSentenceIndex; // Update internal index but don't fire event
                return;
            }
            
            var sentenceText = _sentences[newSentenceIndex].Substring(0, Math.Min(30, _sentences[newSentenceIndex].Length));
                
            _logger.LogDebug("🎯 Sentence changed: {OldIndex} -> {NewIndex} at {CurrentTime:F2}s | '{Text}'",
                _currentSentenceIndex, newSentenceIndex, currentTime, sentenceText);
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
        _logger.LogInformation("TimestampedAudioManager: Playback ended");
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
