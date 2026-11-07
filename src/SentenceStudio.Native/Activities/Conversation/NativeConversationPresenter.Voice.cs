using System.Collections.Immutable;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SentenceStudio.Services.Realtime;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native;

/// <summary>
/// Voice mode (the Techorama demo's Conversation.Voice): a live spoken exchange with the scenario's
/// partner. Spoken turns join the same thread as typed turns, so the chat can continue in text
/// afterwards. Like the demo, voice turns are not graded or scored and nothing is saved.
///
/// Session events go through one ordered queue (so transcript pieces never reorder) and are
/// published at most every <see cref="VoicePublishInterval"/>. Microphone audio goes through its
/// own queue so only one chunk is sent at a time. Every change is checked against the voice
/// generation, so a late event from an ended session never touches the next one.
/// </summary>
public sealed partial class NativeConversationPresenter
{
    /// <summary>Voice events that arrive within this window are published together.</summary>
    internal static readonly TimeSpan VoicePublishInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>Microphone chunks (about 100 ms each) kept while the network is slow; older ones are dropped.</summary>
    internal const int MicrophoneBacklog = 50;

    /// <summary>The input level is published in steps of this size.</summary>
    internal const double MicLevelStep = 0.05;

    /// <summary>Shown for a learner turn whose transcription failed (dropped when voice ends).</summary>
    internal const string Untranscribed = "…";

    private NativeConversationVoice _voice;
    private long _voiceGeneration;
    private IConversationVoiceSession? _voiceSession;
    private CancellationTokenSource? _voiceConnect;
    private Channel<byte[]>? _microphone;
    private Channel<Func<bool>>? _voiceEvents;
    private double _micLevel;
    private readonly Dictionary<string, ConversationChunk> _voiceLearnerTurns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ConversationChunk> _voicePartnerTurns = new(StringComparer.Ordinal);
    private readonly HashSet<ConversationChunk> _voiceChunks = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ConversationChunk, ImmutableArray<NativeConversationToolNote>> _toolNotes =
        new(ReferenceEqualityComparer.Instance);
    private readonly List<NativeConversationToolNote> _pendingToolNotes = [];

    private bool VoiceAvailable => _voiceService is not null && _voiceAudio is not null;

    private string PartnerName => string.IsNullOrWhiteSpace(_active?.PersonaName)
        ? NativeConversationAi.DefaultPersonaName : _active.PersonaName;

    /// <summary>
    /// The waveform button: asks for the microphone, opens a realtime session for the owner, starts
    /// the platform audio, and lets the partner speak first.
    /// </summary>
    public async Task<NativeConversationState> StartVoiceAsync(CancellationToken cancellationToken = default)
    {
        long generation;
        string userId;
        NativeConversationVoiceRequest request;
        CancellationTokenSource connect;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!CanCommand(out var refused))
                return refused;
            if (!VoiceAvailable || _voice != NativeConversationVoice.Off
                || _state.Phase != NativePhase.Ready || _waiting || _starting)
                return State;
            StopAudioCore();
            _notices = [];
            _showPhrases = false;
            _voice = NativeConversationVoice.Connecting;
            generation = Interlocked.Increment(ref _voiceGeneration);
            userId = _launch!.UserId;
            request = new(_active, _language, [.. _chunks], [.. _vocabulary]);
            connect = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _lifetime.Token, _exitCancel.Token);
            _voiceConnect = connect;
            PublishCurrent();
        }
        finally
        {
            _gate.Release();
        }

        var granted = false;
        IConversationVoiceSession? session = null;
        Exception? failure = null;
        try
        {
            granted = await _voiceAudio!.RequestMicrophoneAsync(connect.Token);
            if (granted)
                session = await _voiceService!.StartAsync(userId, request, connect.Token);
        }
        catch (OperationCanceledException) when (connect.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        IConversationVoiceSession? discard = session;
        var cancelled = connect.IsCancellationRequested;
        if (!await EnterAsync())
        {
            // Back took over; it cancels and releases the connection attempt.
            await DisposeVoiceSessionAsync(discard);
            return _state;
        }
        try
        {
            // ReleaseVoice may already have cancelled and dropped it; nothing else holds it now.
            if (ReferenceEquals(_voiceConnect, connect))
                _voiceConnect = null;
            connect.Dispose();
            // Ended, restarted or left while connecting: the session is not used.
            if (generation != _voiceGeneration || Exited)
                return State;
            if (!Active())
                return Refuse();
            if (session is null)
            {
                if (failure is not null)
                    logger.LogError(failure, "Conversation voice mode could not start.");
                _voice = NativeConversationVoice.Off;
                if (!cancelled)
                    _notices = [new(NativeMessageKind.Error, granted || failure is not null
                        ? NativeConversationText.VoiceUnavailable : NativeConversationText.VoiceMicDenied)];
                return PublishCurrent();
            }

            discard = null;
            _voiceSession = session;
            var events = Channel.CreateUnbounded<Func<bool>>(new UnboundedChannelOptions { SingleReader = true });
            var microphone = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(MicrophoneBacklog)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true
            });
            _voiceEvents = events;
            _microphone = microphone;
            Wire(session, generation, events.Writer);
            _ = PumpVoiceEventsAsync(generation, events.Reader);
            _ = PumpMicrophoneAsync(session, microphone.Reader);
            try
            {
                var micWriter = microphone.Writer;
                var eventWriter = events.Writer;
                await _voiceAudio!.StartAsync(
                    pcm => micWriter.TryWrite(pcm),
                    level => eventWriter.TryWrite(() => SetMicLevel(level)),
                    () => eventWriter.TryWrite(PartnerFinished));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Conversation voice mode could not start the microphone or speaker.");
                StopVoiceCore();
                _notices = [new(NativeMessageKind.Error, NativeConversationText.VoiceUnavailable)];
                return PublishCurrent();
            }
            // Dispose does not take the gate; it may have released voice while the audio started.
            if (generation != Interlocked.Read(ref _voiceGeneration) || Exited)
                return State;
            _voice = NativeConversationVoice.Listening;
            PublishCurrent();
        }
        finally
        {
            _gate.Release();
            if (discard is not null)
                _ = DisposeVoiceSessionAsync(discard);
        }

        try
        {
            await session.BeginAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Conversation voice session could not begin.");
            await EndVoiceAsync(generation, NativeConversationText.VoiceUnavailable);
        }
        return await _exit.StateAsync(_gate, () => State);
    }

    /// <summary>End voice: stops the microphone and speaker and closes the session. Spoken turns stay in the chat.</summary>
    public async Task<NativeConversationState> StopVoiceAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!CanCommand(out var refused))
                return refused;
            if (_voice == NativeConversationVoice.Off)
                return State;
            StopVoiceCore();
            return PublishCurrent();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EndVoiceAsync(long generation, string noticeKey)
    {
        if (!await EnterAsync())
            return;
        try
        {
            if (generation != _voiceGeneration || _voice == NativeConversationVoice.Off || Exited)
                return;
            StopVoiceCore();
            _notices = [new(NativeMessageKind.Error, noticeKey)];
            PublishCurrent();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Wire(IConversationVoiceSession session, long generation, ChannelWriter<Func<bool>> events)
    {
        bool Current() => Interlocked.Read(ref _voiceGeneration) == generation;

        session.AudioDelta += audio =>
        {
            if (!Current() || string.IsNullOrEmpty(audio))
                return;
            byte[] pcm;
            try
            {
                pcm = Convert.FromBase64String(audio);
            }
            catch (FormatException)
            {
                logger.LogWarning("Conversation voice received audio that was not base64.");
                return;
            }
            _voiceAudio!.Play(pcm);
            events.TryWrite(PartnerStarted);
        };
        session.SpeechStarted += () =>
        {
            if (!Current())
                return;
            _voiceAudio!.ClearPlayback();
            events.TryWrite(LearnerStarted);
        };
        session.UserTurnStarted += itemId => events.TryWrite(() => LearnerTurnStarted(itemId));
        session.UserTranscriptCompleted += (itemId, text) => events.TryWrite(() => LearnerTranscribed(itemId, text));
        session.AssistantTranscriptDelta += (itemId, delta) => events.TryWrite(() =>
        {
            if (string.IsNullOrEmpty(delta))
                return false;
            PartnerTurn(itemId).Text += delta;
            return true;
        });
        session.AssistantTranscriptCompleted += (itemId, text) => events.TryWrite(() =>
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;
            PartnerTurn(itemId).Text = text;
            return true;
        });
        session.ToolInvoked += (term, matches) =>
        {
            var note = new NativeConversationToolNote(term, [.. matches]);
            events.TryWrite(() =>
            {
                _pendingToolNotes.Add(note);
                return false;
            });
        };
        // Provider errors can be recoverable (the session goes on); the demo only logged them.
        session.Failed += message => logger.LogWarning("Conversation voice session reported: {Message}", message);
        session.Ended += () => _ = EndVoiceAsync(generation, NativeConversationText.VoiceUnavailable);
    }

    /// <summary>Applies queued voice events in order, at most one publish per <see cref="VoicePublishInterval"/>.</summary>
    private async Task PumpVoiceEventsAsync(long generation, ChannelReader<Func<bool>> reader)
    {
        try
        {
            while (await reader.WaitToReadAsync())
            {
                if (!await EnterAsync())
                    return;
                try
                {
                    if (generation != _voiceGeneration || Exited)
                        return;
                    if (!Active())
                    {
                        Refuse();
                        return;
                    }
                    var changed = false;
                    while (generation == _voiceGeneration && reader.TryRead(out var apply))
                        changed |= apply();
                    if (changed)
                        PublishCurrent();
                }
                finally
                {
                    _gate.Release();
                }
                await Task.Delay(VoicePublishInterval);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Conversation voice events stopped.");
            await EndVoiceAsync(generation, NativeConversationText.VoiceUnavailable);
        }
    }

    /// <summary>Sends microphone chunks one at a time, in order.</summary>
    private async Task PumpMicrophoneAsync(IConversationVoiceSession session, ChannelReader<byte[]> reader)
    {
        try
        {
            await foreach (var pcm in reader.ReadAllAsync())
                await session.AppendAudioAsync(pcm);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            // The session's receive side reports the failure (Ended); this only stops sending.
            logger.LogWarning(ex, "Conversation voice could not send microphone audio.");
        }
    }

    private bool PartnerStarted()
    {
        if (_voice != NativeConversationVoice.Listening)
            return false;
        _voice = NativeConversationVoice.PartnerSpeaking;
        return true;
    }

    private bool PartnerFinished()
    {
        if (_voice != NativeConversationVoice.PartnerSpeaking)
            return false;
        _voice = NativeConversationVoice.Listening;
        return true;
    }

    private bool LearnerStarted()
    {
        if (_voice != NativeConversationVoice.PartnerSpeaking)
            return false;
        _voice = NativeConversationVoice.Listening;
        return true;
    }

    private bool SetMicLevel(double level)
    {
        var stepped = double.IsFinite(level)
            ? Math.Round(Math.Clamp(level, 0, 1) / MicLevelStep) * MicLevelStep
            : 0;
        if (stepped == _micLevel)
            return false;
        _micLevel = stepped;
        return _voice == NativeConversationVoice.Listening;
    }

    private bool LearnerTurnStarted(string itemId)
    {
        if (_voiceLearnerTurns.ContainsKey(itemId))
            return false;
        _voiceLearnerTurns[itemId] = NewVoiceTurn(ConversationRole.User, UserAuthor);
        return true;
    }

    private bool LearnerTranscribed(string itemId, string text)
    {
        // Noise (a cough, a room speaker) can be committed as a turn and transcribed as "." or
        // "...". A turn with no letters or digits is not something the learner said.
        if (!string.IsNullOrEmpty(text) && !text.Any(char.IsLetterOrDigit))
        {
            if (_voiceLearnerTurns.Remove(itemId, out var noise))
            {
                _chunks.Remove(noise);
                _voiceChunks.Remove(noise);
                return true;
            }
            return false;
        }
        if (!_voiceLearnerTurns.TryGetValue(itemId, out var turn))
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;
            turn = NewVoiceTurn(ConversationRole.User, UserAuthor);
            _voiceLearnerTurns[itemId] = turn;
        }
        turn.Text = string.IsNullOrWhiteSpace(text) ? Untranscribed : text;
        return true;
    }

    private ConversationChunk PartnerTurn(string itemId)
    {
        if (_voicePartnerTurns.TryGetValue(itemId, out var existing))
            return existing;
        var turn = NewVoiceTurn(ConversationRole.Assistant, PartnerName);
        _voicePartnerTurns[itemId] = turn;
        if (_pendingToolNotes.Count > 0)
        {
            _toolNotes[turn] = [.. _pendingToolNotes];
            _pendingToolNotes.Clear();
        }
        return turn;
    }

    private ConversationChunk NewVoiceTurn(ConversationRole role, string author)
    {
        var turn = new ConversationChunk
        {
            Text = string.Empty,
            Author = author,
            Role = role,
            SentTime = DateTime.UtcNow
        };
        _voiceChunks.Add(turn);
        _chunks.Add(turn);
        return turn;
    }

    /// <summary>
    /// Ends voice mode: later events from the session are ignored, the audio stops and the session
    /// closes in the background. Spoken turns stay; placeholders that never got words are dropped.
    /// Caller holds the gate.
    /// </summary>
    private void StopVoiceCore()
    {
        var wasOn = _voice != NativeConversationVoice.Off;
        Interlocked.Increment(ref _voiceGeneration);
        _voice = NativeConversationVoice.Off;
        _micLevel = 0;
        if (wasOn)
            ReleaseVoice();
        _chunks.RemoveAll(chunk => _voiceChunks.Contains(chunk)
            && (string.IsNullOrWhiteSpace(chunk.Text) || chunk.Text == Untranscribed));
        _voiceLearnerTurns.Clear();
        _voicePartnerTurns.Clear();
        _pendingToolNotes.Clear();
    }

    /// <summary>Stops the platform audio and closes the session. Cleanup failures are logged, not thrown.</summary>
    private void ReleaseVoice()
    {
        var connect = _voiceConnect;
        _voiceConnect = null;
        try
        {
            connect?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        _microphone?.Writer.TryComplete();
        _microphone = null;
        _voiceEvents?.Writer.TryComplete();
        _voiceEvents = null;
        try
        {
            _voiceAudio?.Stop();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Conversation voice audio did not stop cleanly.");
        }
        var session = _voiceSession;
        _voiceSession = null;
        if (session is not null)
            _ = DisposeVoiceSessionAsync(session);
    }

    private async Task DisposeVoiceSessionAsync(IConversationVoiceSession? session)
    {
        if (session is null)
            return;
        try
        {
            await session.DisposeAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Conversation voice session did not close cleanly.");
        }
    }

    /// <summary>A new chat (scenario or language change) forgets which turns were spoken. Caller holds the gate.</summary>
    private void ForgetVoiceTurns()
    {
        _voiceChunks.Clear();
        _toolNotes.Clear();
    }

    private NativeConversationMessage Message(ConversationChunk chunk, int index) => new(
        index,
        chunk.Role == ConversationRole.User,
        chunk.Author ?? string.Empty,
        chunk.Text ?? string.Empty,
        chunk.GrammarCorrections
            .Select(item => new NativeConversationCorrection(item.Original, item.Corrected, item.Explanation))
            .ToImmutableArray(),
        Reply.NormalizeComprehension(chunk.Comprehension),
        _voiceChunks.Contains(chunk),
        _toolNotes.TryGetValue(chunk, out var notes) ? notes : []);
}
