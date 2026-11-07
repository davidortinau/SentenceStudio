using System.Collections.Concurrent;
using Android.Content.PM;
using Android.Media;
using Android.Media.Audiofx;
using AndroidX.Core.Content;
using Microsoft.Extensions.Logging;
using AndroidContext = global::Android.Content.Context;

namespace SentenceStudio.Native.Android;

/// <summary>
/// Conversation voice mode's microphone and speaker on Android (the counterpart of iOS
/// NativeVoiceAudio; non-UI platform code, Captain-approved 2026-11-07). AudioRecord with the
/// voice-communication source (and the platform echo canceller and noise suppressor when the
/// device has them) captures PCM16 mono at 24 kHz and hands it on in 100 ms chunks; partner audio
/// plays through an AudioTrack in streaming mode. Half duplex like iOS and the demo: the microphone
/// is not sent while partner audio plays, nor for a short tail after it.
///
/// Capture and playback each run on their own background thread; Stop ends both at once (no
/// callback or Play has effect after it) and releases the hardware in the background.
/// </summary>
internal sealed class AndroidVoiceAudio(AndroidContext context, ILogger<AndroidVoiceAudio> logger) : INativeVoiceAudio
{
    private const int SampleRate = 24000;
    private const int ChunkBytes = 2400 * 2; // 100 ms of PCM16 mono
    /// <summary>Microphone stays quiet this long after partner audio ends (room reverb, output latency).</summary>
    private static readonly TimeSpan QuietTail = TimeSpan.FromMilliseconds(600);

    private readonly object _lock = new();
    private Run? _current;
    /// <summary>Hardware start and release run here in order, off the main thread.</summary>
    private Task _sessionWork = Task.CompletedTask;

    public async Task<bool> RequestMicrophoneAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ContextCompat.CheckSelfPermission(context, global::Android.Manifest.Permission.RecordAudio)
            == Permission.Granted)
            return true;
        if (context is not MainActivity activity)
            return false;
        var granted = await activity.RequestMicrophoneAsync().WaitAsync(cancellationToken);
        logger.LogInformation("Microphone permission for voice mode: {Granted}", granted);
        return granted;
    }

    public Task StartAsync(Action<byte[]> microphone, Action<double> level, Action playbackIdle)
    {
        Run run;
        lock (_lock)
        {
            if (_current is not null)
                throw new InvalidOperationException("Voice audio is already running.");
            run = new Run(microphone, level, playbackIdle);
            _current = run;
        }
        return Queue(() => StartCore(run));
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

    private void StartCore(Run run)
    {
        if (run.Stopped)
            return;
        var minRecord = AudioRecord.GetMinBufferSize(SampleRate, ChannelIn.Mono, Encoding.Pcm16bit);
        var minPlay = AudioTrack.GetMinBufferSize(SampleRate, ChannelOut.Mono, Encoding.Pcm16bit);
        if (minRecord <= 0 || minPlay <= 0)
            throw new InvalidOperationException("Voice audio is not supported at 24 kHz on this device.");
#pragma warning disable CA1422 // AudioRecord(source, ...) is the supported constructor at API 36 for this use.
        var record = new AudioRecord(AudioSource.VoiceCommunication, SampleRate, ChannelIn.Mono,
            Encoding.Pcm16bit, Math.Max(minRecord, ChunkBytes * 2));
#pragma warning restore CA1422
        if (record.State != global::Android.Media.State.Initialized)
        {
            record.Release();
            throw new InvalidOperationException("No microphone input is available.");
        }
        var session = record.AudioSessionId;
        AcousticEchoCanceler? echo = null;
        NoiseSuppressor? noise = null;
        AudioTrack? track = null;
        try
        {
            if (AcousticEchoCanceler.IsAvailable)
            {
                echo = AcousticEchoCanceler.Create(session);
                echo?.SetEnabled(true);
            }
            else
                logger.LogWarning("Voice processing is off (no echo canceller on this device).");
            if (NoiseSuppressor.IsAvailable)
            {
                noise = NoiseSuppressor.Create(session);
                noise?.SetEnabled(true);
            }
            track = new AudioTrack.Builder()
                .SetAudioAttributes(new AudioAttributes.Builder()
                    .SetUsage(AudioUsageKind.VoiceCommunication)!
                    .SetContentType(AudioContentType.Speech)!
                    .Build()!)
                .SetAudioFormat(new AudioFormat.Builder()
                    .SetSampleRate(SampleRate)!
                    .SetEncoding(Encoding.Pcm16bit)!
                    .SetChannelMask(ChannelOut.Mono)!
                    .Build()!)
                .SetTransferMode(AudioTrackMode.Stream)
                .SetBufferSizeInBytes(Math.Max(minPlay, ChunkBytes * 2))
                .SetSessionId(session)
                .Build()!;
            record.StartRecording();
            if (record.RecordingState != RecordState.Recording)
                throw new InvalidOperationException("Voice audio could not start the microphone.");
            track.Play();
        }
        catch
        {
            // Whatever was made before the failure is released, so the microphone is not held.
            ReleaseHardware(record, track, echo, noise);
            throw;
        }
        lock (_lock)
        {
            if (run.Stopped)
            {
                // Stopped while starting: nothing is handed on.
                _ = Queue(() => ReleaseHardware(record, track, echo, noise));
                return;
            }
            run.Attach(record, track, echo, noise);
        }
        run.CaptureThread = new Thread(() => Capture(run)) { IsBackground = true, Name = "VoiceCapture" };
        run.PlaybackThread = new Thread(() => Playback(run)) { IsBackground = true, Name = "VoicePlayback" };
        run.CaptureThread.Start();
        run.PlaybackThread.Start();
        logger.LogInformation("Voice audio started: {Rate} Hz mono, echo canceller {Echo}", SampleRate,
            echo is not null);
    }

    public void Play(byte[] pcm16)
    {
        Run? run;
        lock (_lock)
            run = _current;
        if (run is null || run.Stopped || pcm16.Length < 2)
            return;
        run.Enqueue(pcm16);
    }

    public void ClearPlayback()
    {
        Run? run;
        lock (_lock)
            run = _current;
        run?.Clear();
    }

    public void Stop()
    {
        Run? run;
        lock (_lock)
        {
            run = _current;
            _current = null;
        }
        if (run is null)
            return;
        // From here no callback runs and Play is ignored; the hardware is released in the background.
        run.Stop();
        _ = Queue(() =>
        {
            run.CaptureThread?.Join(TimeSpan.FromSeconds(1));
            var ended = run.PlaybackThread?.Join(TimeSpan.FromSeconds(1)) ?? true;
            if (run.Record is { } record && run.Track is { } track)
                ReleaseHardware(record, track, run.Echo, run.Noise);
            if (ended)
                run.DisposeSignal();
            logger.LogInformation("Voice audio stopped");
        });
    }

    /// <summary>Each part is released on its own, so one failure does not leak the others.</summary>
    private void ReleaseHardware(AudioRecord record, AudioTrack? track, AcousticEchoCanceler? echo,
        NoiseSuppressor? noise)
    {
        Try("microphone", () =>
        {
            if (record.RecordingState == RecordState.Recording)
                record.Stop();
            record.Release();
        });
        if (track is not null)
            Try("speaker", () =>
            {
                if (track.PlayState != PlayState.Stopped)
                {
                    track.Pause();
                    track.Flush();
                    track.Stop();
                }
                track.Release();
            });
        if (echo is not null)
            Try("echo canceller", echo.Release);
        if (noise is not null)
            Try("noise suppressor", noise.Release);

        void Try(string part, Action release)
        {
            try
            {
                release();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Voice audio {Part} did not release cleanly.", part);
            }
        }
    }

    private void Capture(Run run)
    {
        var buffer = new byte[ChunkBytes];
        var loggedFirst = false;
        while (!run.Stopped && run.Record is { } record)
        {
            var filled = 0;
            while (filled < buffer.Length && !run.Stopped)
            {
                var read = record.Read(buffer, filled, buffer.Length - filled);
                if (read <= 0)
                {
                    if (read < 0)
                        logger.LogWarning("Voice microphone read failed: {Code}", read);
                    Thread.Sleep(10);
                    continue;
                }
                filled += read;
            }
            if (run.Stopped)
                return;
            // Half duplex: drop the microphone while the partner speaks and for the tail after.
            if (run.IsPartnerAudible)
                continue;
            var chunk = (byte[])buffer.Clone();
            // Speech sits around 0.02-0.25 RMS; scale so the level meter moves (as on iOS).
            var level = Math.Min(1, Rms(chunk) * 4);
            if (!loggedFirst)
            {
                loggedFirst = true;
                logger.LogInformation("Voice microphone is sending audio (first chunk level {Level:0.000})", level);
            }
            if (!run.Deliver(chunk, level))
                return;
        }
    }

    private void Playback(Run run)
    {
        while (!run.Stopped && run.Track is { } track)
        {
            // A clear (the learner started speaking) is done here, so it never races a write.
            if (run.TakeClearRequest())
            {
                track.Pause();
                track.Flush();
                run.ResetWritten();
                track.Play();
                continue;
            }
            if (run.TryTake(out var pcm, out var generation))
            {
                var written = 0;
                // Non-blocking writes, so a clear or stop is seen within a few milliseconds.
                while (written < pcm.Length && !run.Stopped && run.Generation == generation)
                {
                    var result = track.Write(pcm, written, pcm.Length - written, WriteMode.NonBlocking);
                    if (result < 0)
                    {
                        // A dead track (an audio route or server change) fails every write: say so once.
                        if (run.NoteWriteFailure())
                            logger.LogWarning("Voice playback write failed: {Code}", result);
                        break;
                    }
                    if (result == 0)
                    {
                        Thread.Sleep(5);
                        continue;
                    }
                    written += result;
                    run.AddWritten(result / 2);
                }
                continue;
            }
            // The queue is empty: once what was written has played, the partner's turn ends.
            if (run.IsPlaying && track.PlaybackHeadPosition >= run.FramesWritten)
            {
                if (run.FinishPlayback(QuietTail))
                    run.DeliverIdle();
                continue;
            }
            run.WaitForAudio(TimeSpan.FromMilliseconds(20));
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

    /// <summary>One voice session's hardware, queue and callbacks.</summary>
    private sealed class Run(Action<byte[]> microphone, Action<double> level, Action playbackIdle)
    {
        private readonly object _gate = new();
        private readonly ConcurrentQueue<(byte[] Pcm, long Generation)> _queue = new();
        private readonly AutoResetEvent _audio = new(false);
        private readonly object _callbacks = new();
        private volatile bool _stopped;
        private long _generation;
        private long _framesWritten;
        private bool _playing;
        private bool _clearRequested;
        private DateTime _quietUntil = DateTime.MinValue;

        internal AudioRecord? Record { get; private set; }
        internal AudioTrack? Track { get; private set; }
        internal AcousticEchoCanceler? Echo { get; private set; }
        internal NoiseSuppressor? Noise { get; private set; }
        internal Thread? CaptureThread { get; set; }
        internal Thread? PlaybackThread { get; set; }
        internal bool Stopped => _stopped;
        internal long Generation => Interlocked.Read(ref _generation);
        internal long FramesWritten => Interlocked.Read(ref _framesWritten);

        internal bool IsPlaying
        {
            get { lock (_gate) return _playing; }
        }

        /// <summary>Partner audio is queued or playing, or it ended less than the quiet tail ago.</summary>
        internal bool IsPartnerAudible
        {
            get
            {
                lock (_gate)
                    return _playing || !_queue.IsEmpty || DateTime.UtcNow < _quietUntil;
            }
        }

        internal void Attach(AudioRecord record, AudioTrack track, AcousticEchoCanceler? echo, NoiseSuppressor? noise)
        {
            Record = record;
            Track = track;
            Echo = echo;
            Noise = noise;
        }

        internal void Enqueue(byte[] pcm)
        {
            lock (_gate)
            {
                if (_stopped)
                    return;
                _playing = true;
                _queue.Enqueue((pcm, Interlocked.Read(ref _generation)));
            }
            _audio.Set();
        }

        internal bool TryTake(out byte[] pcm, out long generation)
        {
            while (_queue.TryDequeue(out var item))
            {
                if (item.Generation != Interlocked.Read(ref _generation))
                    continue;
                pcm = item.Pcm;
                generation = item.Generation;
                return true;
            }
            pcm = [];
            generation = 0;
            return false;
        }

        internal void AddWritten(int frames) => Interlocked.Add(ref _framesWritten, frames);

        private int _writeFailures;

        /// <summary>True for the first write failure of the run.</summary>
        internal bool NoteWriteFailure() => Interlocked.Increment(ref _writeFailures) == 1;

        /// <summary>Ends a partner turn; true when it was playing (so idle fires once).</summary>
        internal bool FinishPlayback(TimeSpan tail)
        {
            lock (_gate)
            {
                if (!_playing || !_queue.IsEmpty)
                    return false;
                _playing = false;
                _quietUntil = DateTime.UtcNow + tail;
                return true;
            }
        }

        /// <summary>
        /// The learner started speaking: queued partner audio is dropped now, and the playback
        /// thread flushes what the track still holds (its head position starts again at 0).
        /// </summary>
        internal void Clear()
        {
            lock (_gate)
            {
                if (_stopped)
                    return;
                Interlocked.Increment(ref _generation);
                while (_queue.TryDequeue(out _))
                {
                }
                _playing = false;
                _clearRequested = true;
                _quietUntil = DateTime.UtcNow;
            }
            _audio.Set();
        }

        internal bool TakeClearRequest()
        {
            lock (_gate)
            {
                if (!_clearRequested)
                    return false;
                _clearRequested = false;
                return true;
            }
        }

        internal void ResetWritten() => Interlocked.Exchange(ref _framesWritten, 0);

        internal void WaitForAudio(TimeSpan timeout) => _audio.WaitOne(timeout);

        /// <summary>
        /// Hands a microphone chunk and its level on, unless stopped. Stop waits for a delivery in
        /// progress, so no callback runs after Stop returns.
        /// </summary>
        internal bool Deliver(byte[] chunk, double meter)
        {
            lock (_callbacks)
            {
                if (_stopped)
                    return false;
                microphone(chunk);
                level(meter);
                return true;
            }
        }

        internal void DeliverIdle()
        {
            lock (_callbacks)
            {
                if (!_stopped)
                    playbackIdle();
            }
        }

        internal void DisposeSignal() => _audio.Dispose();

        internal void Stop()
        {
            lock (_callbacks)
                _stopped = true;
            lock (_gate)
            {
                Interlocked.Increment(ref _generation);
                while (_queue.TryDequeue(out _))
                {
                }
            }
            _audio.Set();
        }
    }
}
