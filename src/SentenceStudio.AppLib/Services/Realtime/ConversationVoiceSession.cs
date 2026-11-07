#nullable enable
#pragma warning disable MEAI001 // IRealtimeClient is experimental in Microsoft.Extensions.AI.
#pragma warning disable OPENAI002 // OpenAI.Realtime types are experimental in the OpenAI SDK.

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Services.Realtime;

/// <summary>
/// A live voice conversation: microphone audio in, partner audio and transcripts out.
/// </summary>
public interface IConversationVoiceSession : IAsyncDisposable
{
    /// <summary>A chunk of partner audio, base64 PCM16 mono at <see cref="ConversationVoiceSession.SampleRate"/>.</summary>
    event Action<string>? AudioDelta;

    /// <summary>The learner started speaking. Clear any queued partner audio.</summary>
    event Action? SpeechStarted;

    /// <summary>A learner turn was committed. Argument: item id.</summary>
    event Action<string>? UserTurnStarted;

    /// <summary>A learner turn was transcribed. Arguments: item id, transcript (empty when transcription failed).</summary>
    event Action<string, string>? UserTranscriptCompleted;

    /// <summary>Partial partner transcript. Arguments: item id, text delta.</summary>
    event Action<string, string>? AssistantTranscriptDelta;

    /// <summary>Final partner transcript. Arguments: item id, full text.</summary>
    event Action<string, string>? AssistantTranscriptCompleted;

    /// <summary>
    /// The vocabulary tool ran. Arguments: the term, and the matching saved words as
    /// "target = native" (empty when the term is not in the learner's list).
    /// </summary>
    event Action<string, IReadOnlyList<string>>? ToolInvoked;

    /// <summary>The provider reported an error, or the session ended unexpectedly. Argument: message.</summary>
    event Action<string>? Failed;

    /// <summary>The session is gone (the connection failed or the provider closed it) before it was disposed.</summary>
    event Action? Ended;

    /// <summary>Starts receiving events and asks the partner to speak first.</summary>
    Task BeginAsync();

    /// <summary>Appends one chunk of microphone audio (PCM16 mono). Callers send one chunk at a time.</summary>
    Task AppendAudioAsync(byte[] pcm16);
}

/// <summary>
/// One live voice conversation for the Conversation activity, built on <see cref="IRealtimeClient"/>.
/// </summary>
/// <remarks>
/// The page streams microphone audio in and receives events out. The session owns the realtime
/// websocket, the instructions, and one read-only tool over the learner's own vocabulary. It never
/// writes learner data; the page decides what to show and keep.
/// </remarks>
public sealed class ConversationVoiceSession : IConversationVoiceSession
{
    /// <summary>PCM16 mono at 24 kHz, the format both directions use.</summary>
    public const int SampleRate = 24000;

    private const string ToolName = "lookup_my_vocabulary";

    private readonly IRealtimeClientSession _session;
    private readonly IRealtimeClient? _ownedClient;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts;
    private readonly object _lifecycle = new();
    private Task? _receiveLoop;
    private int _disposed;

    /// <summary>A chunk of assistant audio, base64 PCM16 at <see cref="SampleRate"/>.</summary>
    public event Action<string>? AudioDelta;

    /// <summary>The learner started speaking. Clear any queued assistant audio.</summary>
    public event Action? SpeechStarted;

    /// <summary>A learner turn was committed. Arguments: item id.</summary>
    public event Action<string>? UserTurnStarted;

    /// <summary>A learner turn was transcribed. Arguments: item id, transcript.</summary>
    public event Action<string, string>? UserTranscriptCompleted;

    /// <summary>Partial assistant transcript. Arguments: item id, text delta.</summary>
    public event Action<string, string>? AssistantTranscriptDelta;

    /// <summary>Final assistant transcript. Arguments: item id, full text.</summary>
    public event Action<string, string>? AssistantTranscriptCompleted;

    /// <summary>The vocabulary tool ran. Arguments: the term, a short result summary.</summary>
    public event Action<string, IReadOnlyList<string>>? ToolInvoked;

    /// <summary>The provider reported an error. Argument: message.</summary>
    public event Action<string>? Failed;

    /// <inheritdoc />
    public event Action? Ended;

    private ConversationVoiceSession(IRealtimeClientSession session, IRealtimeClient? ownedClient,
        ILogger logger, CancellationTokenSource cts)
    {
        _session = session;
        _ownedClient = ownedClient;
        _logger = logger;
        _cts = cts;
    }

    /// <summary>Opens a realtime session configured for spoken practice in <paramref name="targetLanguage"/>.</summary>
    /// <param name="disposeClient">
    /// True when <paramref name="client"/> was made for this session alone: the session then disposes
    /// it when it ends, or right away when the session cannot open.
    /// </param>
    public static async Task<ConversationVoiceSession> StartAsync(
        IRealtimeClient client,
        string instructions,
        string targetLanguage,
        string transcriptionModel,
        IReadOnlyList<VocabularyWord> vocabulary,
        ILogger logger,
        CancellationToken cancellationToken = default,
        bool disposeClient = false)
    {
        ArgumentNullException.ThrowIfNull(client);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConversationVoiceSession? voice = null;

        var lookup = AIFunctionFactory.Create(
            (string term) => voice!.LookUp(term, vocabulary),
            ToolName,
            "Look up a word or phrase in the learner's own saved vocabulary list. "
            + "Returns the saved meaning when the learner is studying it, or says it is not in their list.");

        var options = new RealtimeSessionOptions
        {
            Instructions = instructions,
            Voice = "marin",
            InputAudioFormat = new RealtimeAudioFormat("audio/pcm", SampleRate),
            OutputAudioFormat = new RealtimeAudioFormat("audio/pcm", SampleRate),
            TranscriptionOptions = new TranscriptionOptions
            {
                ModelId = transcriptionModel,
                SpeechLanguage = ToIsoLanguage(targetLanguage),
            },
            VoiceActivityDetection = new VoiceActivityDetectionOptions { Enabled = true, AllowInterruption = true },
            MaxOutputTokens = 400,
            Tools = [lookup],
        };

        IRealtimeClientSession session;
        try
        {
            session = await client.CreateSessionAsync(options, cts.Token).ConfigureAwait(false);
        }
        catch
        {
            cts.Dispose();
            if (disposeClient)
            {
                client.Dispose();
            }
            throw;
        }

        voice = new ConversationVoiceSession(session, disposeClient ? client : null, logger, cts);
        return voice;
    }

    /// <summary>Starts receiving events and asks the partner to speak first.</summary>
    public async Task BeginAsync()
    {
        CancellationToken token;
        lock (_lifecycle)
        {
            // Ended before it began (End or Back while starting): nothing to start.
            if (_disposed != 0 || _receiveLoop is not null)
            {
                return;
            }

            token = _cts.Token;
            _receiveLoop = Task.Run(ReceiveLoopAsync);
        }

        await _session.SendAsync(new CreateResponseRealtimeClientMessage(), token).ConfigureAwait(false);
    }

    /// <summary>Appends one chunk of microphone audio (PCM16 mono, <see cref="SampleRate"/>).</summary>
    public Task AppendAudioAsync(byte[] pcm16)
    {
        if (Volatile.Read(ref _disposed) != 0 || pcm16.Length == 0)
        {
            return Task.CompletedTask;
        }

        return _session.SendAsync(
            new InputAudioBufferAppendRealtimeClientMessage(new DataContent(pcm16, "audio/pcm")),
            _cts.Token);
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            await foreach (var message in _session.GetStreamingResponseAsync(_cts.Token).ConfigureAwait(false))
            {
                Dispatch(message);
            }

            // The provider closed the session (for example at its time limit). Tell the page, so it
            // does not keep listening on a session that is gone.
            if (!_cts.IsCancellationRequested)
            {
                _logger.LogInformation("Realtime voice session closed by the provider");
                Ended?.Invoke();
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Realtime voice session ended unexpectedly");
            Failed?.Invoke(ex.Message);
            Ended?.Invoke();
        }
    }

    private void Dispatch(RealtimeServerMessage message)
    {
        switch (message)
        {
            case OutputTextAudioRealtimeServerMessage audio when audio.Type == RealtimeServerMessageType.OutputAudioDelta:
                if (!string.IsNullOrEmpty(audio.Audio))
                {
                    AudioDelta?.Invoke(audio.Audio);
                }
                break;

            case OutputTextAudioRealtimeServerMessage text when text.Type == RealtimeServerMessageType.OutputAudioTranscriptionDelta:
                AssistantTranscriptDelta?.Invoke(text.ItemId ?? "", text.Text ?? "");
                break;

            case OutputTextAudioRealtimeServerMessage text when text.Type == RealtimeServerMessageType.OutputAudioTranscriptionDone:
                AssistantTranscriptCompleted?.Invoke(text.ItemId ?? "", text.Text ?? "");
                break;

            case InputAudioTranscriptionRealtimeServerMessage heard when heard.Type == RealtimeServerMessageType.InputAudioTranscriptionCompleted:
                UserTranscriptCompleted?.Invoke(heard.ItemId ?? "", heard.Transcription?.Trim() ?? "");
                break;

            case InputAudioTranscriptionRealtimeServerMessage failed when failed.Type == RealtimeServerMessageType.InputAudioTranscriptionFailed:
                _logger.LogWarning("Input transcription failed: {Error}", failed.Error?.Message);
                UserTranscriptCompleted?.Invoke(failed.ItemId ?? "", "");
                break;

            case ResponseOutputItemRealtimeServerMessage item
                when item.Type == RealtimeServerMessageType.ConversationItemAdded && item.Item?.Role == ChatRole.User:
                // In voice mode every learner item comes from committed microphone audio.
                UserTurnStarted?.Invoke(item.Item.Id ?? "");
                break;

            case ErrorRealtimeServerMessage error:
                _logger.LogWarning("Realtime provider error {Code}: {Message}", error.Error?.ErrorCode, error.Error?.Message);
                Failed?.Invoke(error.Error?.Message ?? "Realtime error");
                break;

            case { Type: var type, RawRepresentation: OpenAI.Realtime.RealtimeServerUpdateInputAudioBufferSpeechStarted }
                when type == RealtimeServerMessageType.RawContentOnly:
                SpeechStarted?.Invoke();
                break;
        }
    }

    private string LookUp(string term, IReadOnlyList<VocabularyWord> vocabulary)
    {
        var needle = (term ?? "").Trim();
        if (needle.Length == 0)
        {
            return "No word was given.";
        }

        var matches = vocabulary
            .Where(w => Matches(w.TargetLanguageTerm, needle) || Matches(w.Lemma, needle) || Matches(w.NativeLanguageTerm, needle))
            .OrderByDescending(w => string.Equals(w.TargetLanguageTerm, needle, StringComparison.OrdinalIgnoreCase))
            .Take(3)
            .Select(w => $"{w.TargetLanguageTerm} = {w.NativeLanguageTerm}")
            .ToList();

        var result = matches.Count == 0
            ? $"'{needle}' is not in the learner's saved vocabulary."
            : $"In the learner's saved vocabulary: {string.Join("; ", matches)}";

        _logger.LogInformation("Voice tool {Tool} looked up {Term}: {Count} match(es)", ToolName, needle, matches.Count);
        ToolInvoked?.Invoke(needle, matches);
        return result;
    }

    private static bool Matches(string? candidate, string needle) =>
        !string.IsNullOrWhiteSpace(candidate)
        && (candidate.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || (needle.Length >= 2 && needle.Contains(candidate.Trim(), StringComparison.OrdinalIgnoreCase)));

    /// <summary>Maps the app's language names to ISO 639-1 codes for transcription. Unknown names auto-detect.</summary>
    public static string? ToIsoLanguage(string language) => language?.Trim().ToLowerInvariant() switch
    {
        "korean" => "ko",
        "japanese" => "ja",
        "chinese" or "mandarin" => "zh",
        "spanish" => "es",
        "french" => "fr",
        "german" => "de",
        "italian" => "it",
        "portuguese" => "pt",
        "english" => "en",
        "vietnamese" => "vi",
        _ => null,
    };

    public async ValueTask DisposeAsync()
    {
        Task? receiveLoop;
        lock (_lifecycle)
        {
            if (_disposed != 0)
            {
                return;
            }

            _disposed = 1;
            receiveLoop = _receiveLoop;
        }

        _cts.Cancel();
        try
        {
            if (receiveLoop is not null)
            {
                await receiveLoop.ConfigureAwait(false);
            }
        }
        catch
        {
        }

        try
        {
            await _session.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _ownedClient?.Dispose();
            _cts.Dispose();
        }
    }
}
