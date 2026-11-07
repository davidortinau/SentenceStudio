using System.Runtime.InteropServices;
using AVFoundation;
using Foundation;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.Native.iOS.Platform;

/// <summary>
/// Conversation voice mode's microphone and speaker (the iOS counterpart of the Techorama demo's
/// realtime-voice.js). AVAudioEngine with voice processing (echo cancellation and noise
/// suppression) captures the microphone, converts it to PCM16 mono at 24 kHz and hands it on in
/// 100 ms chunks; partner audio plays through an AVAudioPlayerNode. Half duplex like the demo: the
/// microphone is not sent while partner audio plays, nor for a short tail after it, so a speaker
/// does not feed the partner's voice back as the learner's turn.
///
/// The audio session is PlayAndRecord (voice chat) only while voice runs; Stop puts the app back
/// in Playback (NativeAudioSession). Activating and releasing the session can block, so both run
/// on a background queue, one after the other (AVAudioSession warns against the main thread).
/// </summary>
internal sealed class NativeVoiceAudio(ILogger<NativeVoiceAudio> logger) : INativeVoiceAudio
{
    private const double SampleRate = 24000;
    private const int ChunkSamples = 2400; // 100 ms
    /// <summary>Microphone stays quiet this long after partner audio ends (room reverb, output latency).</summary>
    private const double QuietTailSeconds = 0.6;

    private readonly object _lock = new();
    private AVAudioEngine? _engine;
    private AVAudioPlayerNode? _player;
    private AVAudioConverter? _converter;
    private AVAudioFormat? _wireFormat;
    private AVAudioFormat? _playFormat;
    private Action<byte[]>? _microphone;
    private Action<double>? _level;
    private Action? _playbackIdle;
    private readonly List<byte> _pending = new(ChunkSamples * 2);
    private int _scheduled;
    private long _clearGeneration;
    private DateTime _quietUntil = DateTime.MinValue;
    private bool _loggedFirstChunk;
    /// <summary>Bumped by each start and stop, so a start queued before a stop does nothing.</summary>
    private long _run;
    /// <summary>Session work (start, release) runs here in order, off the main thread.</summary>
    private Task _sessionWork = Task.CompletedTask;

    public async Task<bool> RequestMicrophoneAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (AVAudioApplication.SharedInstance.RecordPermission)
        {
            case AVAudioApplicationRecordPermission.Granted:
                return true;
            case AVAudioApplicationRecordPermission.Denied:
                return false;
        }
        var granted = await AVAudioApplication.RequestRecordPermissionAsync().WaitAsync(cancellationToken);
        logger.LogInformation("Microphone permission for voice mode: {Granted}", granted);
        return granted;
    }

    public Task StartAsync(Action<byte[]> microphone, Action<double> level, Action playbackIdle)
    {
        long run;
        lock (_lock)
            run = ++_run;
        return Queue(() => StartCore(run, microphone, level, playbackIdle));
    }

    private Task Queue(Action work)
    {
        lock (_lock)
        {
            var next = _sessionWork.ContinueWith(_ => work(), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default);
            _sessionWork = next;
            return next;
        }
    }

    private void StartCore(long run, Action<byte[]> microphone, Action<double> level, Action playbackIdle)
    {
        lock (_lock)
        {
            if (run != _run)
                return;
            if (_engine is not null)
                throw new InvalidOperationException("Voice audio is already running.");

            var session = AVAudioSession.SharedInstance();
            var categoryError = session.SetCategory(AVAudioSessionCategory.PlayAndRecord,
                AVAudioSessionCategoryOptions.DefaultToSpeaker | AVAudioSessionCategoryOptions.AllowBluetooth);
            if (categoryError is not null)
                throw new InvalidOperationException($"Voice audio category failed: {categoryError.LocalizedDescription}");
            session.SetMode(AVAudioSessionMode.VoiceChat.GetConstant()!, out var modeError);
            if (modeError is not null)
                logger.LogWarning("Voice chat audio mode was not set: {Description}", modeError.LocalizedDescription);
            session.SetActive(true, out var activeError);
            if (activeError is not null)
            {
                NativeAudioSession.UsePlayback(logger);
                throw new InvalidOperationException($"Voice audio session failed: {activeError.LocalizedDescription}");
            }

            var engine = new AVAudioEngine();
            try
            {
                StartEngine(engine, microphone, level, playbackIdle);
            }
            catch
            {
                _microphone = null;
                _level = null;
                _playbackIdle = null;
                _converter = null;
                engine.Stop();
                engine.Dispose();
                session.SetActive(false, AVAudioSessionSetActiveOptions.NotifyOthersOnDeactivation, out _);
                NativeAudioSession.UsePlayback(logger);
                throw;
            }
        }
    }

    /// <summary>Caller holds the lock and has activated the session.</summary>
    private void StartEngine(AVAudioEngine engine, Action<byte[]> microphone, Action<double> level,
        Action playbackIdle)
    {
        var input = engine.InputNode;
        if (!input.SetVoiceProcessingEnabled(true, out var processingError))
            logger.LogWarning("Voice processing is off (no echo cancellation): {Description}",
                processingError?.LocalizedDescription);

        var inputFormat = input.GetBusOutputFormat(0);
        if (inputFormat.SampleRate <= 0 || inputFormat.ChannelCount == 0)
            throw new InvalidOperationException("No microphone input is available.");
        _wireFormat = new AVAudioFormat(AVAudioCommonFormat.PCMInt16, SampleRate, 1, interleaved: true);
        _converter = new AVAudioConverter(inputFormat, _wireFormat);

        _playFormat = new AVAudioFormat(AVAudioCommonFormat.PCMFloat32, SampleRate, 1, interleaved: false);
        var player = new AVAudioPlayerNode();
        engine.AttachNode(player);
        engine.Connect(player, engine.MainMixerNode, _playFormat);

        _microphone = microphone;
        _level = level;
        _playbackIdle = playbackIdle;
        _pending.Clear();
        _scheduled = 0;
        _quietUntil = DateTime.MinValue;
        _loggedFirstChunk = false;

        input.InstallTapOnBus(0, 4800, inputFormat, OnMicrophone);
        engine.Prepare();
        if (!engine.StartAndReturnError(out var startError))
        {
            input.RemoveTapOnBus(0);
            throw new InvalidOperationException($"Voice audio could not start: {startError?.LocalizedDescription}");
        }
        player.Play();
        _engine = engine;
        _player = player;
        logger.LogInformation("Voice audio started: input {Rate} Hz x{Channels}", inputFormat.SampleRate,
            inputFormat.ChannelCount);
    }

    public void Play(byte[] pcm16)
    {
        lock (_lock)
        {
            if (_player is null || _playFormat is null || pcm16.Length < 2)
                return;
            var frames = pcm16.Length / 2;
            var buffer = new AVAudioPcmBuffer(_playFormat, (uint)frames) { FrameLength = (uint)frames };
            var samples = new float[frames];
            for (var i = 0; i < frames; i++)
                samples[i] = BitConverter.ToInt16(pcm16, i * 2) / 32768f;
            Marshal.Copy(samples, 0, Marshal.ReadIntPtr(buffer.FloatChannelData), frames);
            _scheduled++;
            var generation = _clearGeneration;
            _player.ScheduleBuffer(buffer, () => OnBufferPlayed(generation));
        }
    }

    public void ClearPlayback()
    {
        AVAudioPlayerNode? player;
        lock (_lock)
        {
            player = _player;
            if (player is null)
                return;
            _clearGeneration++;
            _scheduled = 0;
            _quietUntil = DateTime.UtcNow;
        }
        // Outside the lock: Stop runs the dropped buffers' completions (OnBufferPlayed takes the
        // lock and ignores them, as their generation is old).
        player.Stop();
        lock (_lock)
        {
            if (ReferenceEquals(player, _player))
                player.Play();
        }
    }

    public void Stop()
    {
        AVAudioEngine? engine;
        AVAudioPlayerNode? player;
        lock (_lock)
        {
            // From here no callback runs and Play is ignored; the engine stops in the background.
            _run++;
            engine = _engine;
            player = _player;
            _engine = null;
            _player = null;
            _converter = null;
            _clearGeneration++;
            _microphone = null;
            _level = null;
            _playbackIdle = null;
            _pending.Clear();
            _scheduled = 0;
            if (engine is null)
                return;
        }
        _ = Queue(() => Release(engine, player));
    }

    private void Release(AVAudioEngine engine, AVAudioPlayerNode? player)
    {
        try
        {
            engine.InputNode.RemoveTapOnBus(0);
            player?.Stop();
            engine.Stop();
            engine.Dispose();
            var session = AVAudioSession.SharedInstance();
            session.SetActive(false, AVAudioSessionSetActiveOptions.NotifyOthersOnDeactivation, out var error);
            if (error is not null)
                logger.LogWarning("Voice audio session did not deactivate: {Description}", error.LocalizedDescription);
            NativeAudioSession.UsePlayback(logger);
            logger.LogInformation("Voice audio stopped");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Voice audio did not stop cleanly.");
        }
    }

    private void OnBufferPlayed(long generation)
    {
        Action? idle = null;
        lock (_lock)
        {
            if (generation != _clearGeneration || _scheduled == 0)
                return;
            _scheduled--;
            if (_scheduled == 0)
            {
                var latency = AVAudioSession.SharedInstance().OutputLatency;
                _quietUntil = DateTime.UtcNow.AddSeconds(QuietTailSeconds + latency);
                idle = _playbackIdle;
            }
        }
        idle?.Invoke();
    }

    private void OnMicrophone(AVAudioPcmBuffer buffer, AVAudioTime when)
    {
        Action<byte[]>? send = null;
        Action<double>? level = null;
        List<(byte[] Chunk, double Level)>? chunks = null;
        lock (_lock)
        {
            if (_engine is null || _converter is null || _wireFormat is null)
                return;
            // Half duplex: drop the microphone while the partner speaks and for the tail after.
            if (_scheduled > 0 || DateTime.UtcNow < _quietUntil)
            {
                _pending.Clear();
                return;
            }
            var capacity = (uint)Math.Ceiling(buffer.FrameLength * SampleRate / buffer.Format.SampleRate) + 32;
            var converted = new AVAudioPcmBuffer(_wireFormat, capacity);
            var supplied = false;
            var status = _converter.ConvertToBuffer(converted, out var error, (uint _, out AVAudioConverterInputStatus inputStatus) =>
            {
                if (supplied)
                {
                    inputStatus = AVAudioConverterInputStatus.NoDataNow;
                    return null!;
                }
                supplied = true;
                inputStatus = AVAudioConverterInputStatus.HaveData;
                return buffer;
            });
            if (status == AVAudioConverterOutputStatus.Error || error is not null)
            {
                logger.LogWarning("Voice microphone audio could not be converted: {Description}",
                    error?.LocalizedDescription);
                return;
            }
            var frames = (int)converted.FrameLength;
            if (frames > 0)
            {
                var bytes = new byte[frames * 2];
                Marshal.Copy(Marshal.ReadIntPtr(converted.Int16ChannelData), bytes, 0, bytes.Length);
                _pending.AddRange(bytes);
            }
            while (_pending.Count >= ChunkSamples * 2)
            {
                var chunk = _pending.GetRange(0, ChunkSamples * 2).ToArray();
                _pending.RemoveRange(0, ChunkSamples * 2);
                // Speech sits around 0.02-0.25 RMS; scale so the level meter moves (the demo used x4 too).
                (chunks ??= []).Add((chunk, Math.Min(1, Rms(chunk) * 4)));
            }
            send = _microphone;
            level = _level;
            if (chunks is not null && !_loggedFirstChunk)
            {
                _loggedFirstChunk = true;
                logger.LogInformation("Voice microphone is sending audio (first chunk level {Level:0.000})",
                    chunks[0].Level);
            }
        }
        if (chunks is null)
            return;
        foreach (var (chunk, rms) in chunks)
        {
            send?.Invoke(chunk);
            level?.Invoke(rms);
        }
    }

    private static double Rms(byte[] pcm16)
    {
        double sum = 0;
        var samples = pcm16.Length / 2;
        for (var i = 0; i < samples; i++)
        {
            var sample = BitConverter.ToInt16(pcm16, i * 2) / 32768.0;
            sum += sample * sample;
        }
        return samples == 0 ? 0 : Math.Sqrt(sum / samples);
    }
}
