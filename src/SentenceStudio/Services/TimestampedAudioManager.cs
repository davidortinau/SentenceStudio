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
        if (_currentAudio == null || string.IsNullOrEmpty(_currentAudio.FullTranscript))
            return new List<string>();

        // Use the timing calculator's sentence splitting for consistency
        var sentences = new List<string>();
        var transcript = _currentAudio.FullTranscript;
        var currentSentence = "";
        
        for (int i = 0; i < transcript.Length; i++)
        {
            var c = transcript[i];
            currentSentence += c;
            
            if (IsSentenceDelimiter(c) && IsEndOfSentence(transcript, i))
            {
                sentences.Add(currentSentence.Trim());
                currentSentence = "";
            }
        }
        
        if (!string.IsNullOrWhiteSpace(currentSentence))
        {
            sentences.Add(currentSentence.Trim());
        }
        
        return sentences.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
    }

    /// <summary>
    /// Real-time progress tracking with character-level sentence detection
    /// </summary>
    private void OnProgressTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        if (_player?.IsPlaying != true || _currentAudio == null) 
            return;

        var currentTime = _player.CurrentPosition;
        ProgressUpdated?.Invoke(this, currentTime);

        // Use REAL-TIME sentence detection based on character timestamps!
        var newSentenceIndex = _timingCalculator.GetCurrentSentenceIndex(
            currentTime, 
            _currentAudio.Characters, 
            _currentAudio.FullTranscript
        );

        // Fire event if sentence changed
        if (newSentenceIndex != _currentSentenceIndex && newSentenceIndex >= 0)
        {
            _currentSentenceIndex = newSentenceIndex;
            SentenceChanged?.Invoke(this, _currentSentenceIndex);
        }
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
