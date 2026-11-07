#nullable enable
#pragma warning disable MEAI001 // IRealtimeClient is experimental in Microsoft.Extensions.AI.
#pragma warning disable OPENAI002 // OpenAI.Realtime types are experimental in the OpenAI SDK.

using System.ClientModel.Primitives;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Services.Realtime;

/// <summary>
/// How fast the partner speaks, and how long the learner may pause before the partner answers
/// (the silence that ends the learner's turn).
/// </summary>
public sealed record ConversationVoicePace(float Speed, TimeSpan PauseBeforeReply)
{
    /// <summary>The provider's defaults: normal speed, half a second of silence.</summary>
    public static ConversationVoicePace Default { get; } = new(1f, TimeSpan.FromMilliseconds(500));

    /// <summary>The provider accepts 0.25 to 1.5.</summary>
    internal float ProviderSpeed => float.IsFinite(Speed) ? Math.Clamp(Speed, 0.25f, 1.5f) : 1f;

    internal TimeSpan ProviderPause => PauseBeforeReply < TimeSpan.FromMilliseconds(200)
        ? TimeSpan.FromMilliseconds(200)
        : PauseBeforeReply > TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : PauseBeforeReply;
}

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

    /// <summary>
    /// Starts receiving events. With <paramref name="readAloud"/>, the partner first says that line
    /// (its own last message) word for word; that turn's audio plays but its transcript is not
    /// raised, since the page already shows the line. Without it, the partner speaks first in its
    /// own words.
    /// </summary>
    Task BeginAsync(string? readAloud = null);

    /// <summary>Appends one chunk of microphone audio (PCM16 mono). Callers send one chunk at a time.</summary>
    Task AppendAudioAsync(byte[] pcm16);

    /// <summary>Changes the partner's speaking speed and the pause before it answers; applies from its next turn.</summary>
    Task SetPaceAsync(ConversationVoicePace pace);
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

    /// <summary>Room for a long message read aloud (audio counts toward the output tokens).</summary>
    private const int ReadAloudMaxOutputTokens = 2000;

    /// <summary>Response metadata that marks the read-aloud turn; the provider echoes it on response.created.</summary>
    public const string PurposeKey = "sentencestudio_purpose";
    public const string ReadAloudPurpose = "read_aloud";

    private readonly IRealtimeClientSession _session;
    private readonly Func<ConversationVoicePace, bool, RealtimeSessionOptions> _options;
    private readonly string _targetLanguage;
    private readonly IRealtimeClient? _ownedClient;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts;
    private readonly object _lifecycle = new();
    private Task? _receiveLoop;
    private int _disposed;
    private string? _readAloudResponse;

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

    private ConversationVoiceSession(IRealtimeClientSession session,
        Func<ConversationVoicePace, bool, RealtimeSessionOptions> options, string targetLanguage,
        IRealtimeClient? ownedClient, ILogger logger, CancellationTokenSource cts)
    {
        _session = session;
        _options = options;
        _targetLanguage = targetLanguage;
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
        bool disposeClient = false,
        ConversationVoicePace? pace = null)
    {
        ArgumentNullException.ThrowIfNull(client);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConversationVoiceSession? voice = null;

        var lookup = AIFunctionFactory.Create(
            (string term) => voice!.LookUp(term, vocabulary),
            ToolName,
            "Look up a word or phrase in the learner's own saved vocabulary list. "
            + "Returns the saved meaning when the learner is studying it, or says it is not in their list.");

        // The voice is set only when the session opens: the provider refuses a voice change once the
        // partner has spoken, so a later update leaves it out.
        RealtimeSessionOptions Options(ConversationVoicePace voicePace, bool opening) => new()
        {
            Instructions = instructions,
            Voice = opening ? "marin" : null,
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
            // Speed and the silence length have no Microsoft.Extensions.AI option; the OpenAI
            // adapter starts from these provider options and adds the ones above.
            RawRepresentationFactory = () => PaceOptions(voicePace),
        };

        IRealtimeClientSession session;
        try
        {
            session = await client.CreateSessionAsync(Options(pace ?? ConversationVoicePace.Default, true), cts.Token)
                .ConfigureAwait(false);
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

        voice = new ConversationVoiceSession(session, Options, targetLanguage,
            disposeClient ? client : null, logger, cts);
        return voice;
    }

    /// <summary>Server voice activity detection that ends the learner's turn after the pace's pause, and the pace's speed.</summary>
    private static OpenAI.Realtime.RealtimeConversationSessionOptions PaceOptions(ConversationVoicePace pace) => new()
    {
        AudioOptions = new OpenAI.Realtime.RealtimeConversationSessionAudioOptions
        {
            InputAudioOptions = new OpenAI.Realtime.RealtimeConversationSessionInputAudioOptions
            {
                TurnDetection = new OpenAI.Realtime.RealtimeServerVadTurnDetection
                {
                    SilenceDuration = pace.ProviderPause,
                    CreateResponseEnabled = true,
                    InterruptResponseEnabled = true,
                },
            },
            OutputAudioOptions = new OpenAI.Realtime.RealtimeConversationSessionOutputAudioOptions
            {
                Speed = pace.ProviderSpeed,
            },
        },
    };

    /// <summary>The one-response instructions that make the partner say its own last message as written.</summary>
    public static string ReadAloudInstructions(string targetLanguage, string text) =>
        $"Read your last message aloud to the learner in {targetLanguage}. Say exactly the text below, "
        + "word for word, adding, leaving out or changing nothing. Then stop and wait for the learner.\n\n"
        + text.Trim();

    /// <inheritdoc />
    public async Task BeginAsync(string? readAloud = null)
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

        var first = string.IsNullOrWhiteSpace(readAloud)
            ? new CreateResponseRealtimeClientMessage()
            : new CreateResponseRealtimeClientMessage
            {
                Instructions = ReadAloudInstructions(_targetLanguage, readAloud),
                ToolMode = ChatToolMode.None,
                MaxOutputTokens = ReadAloudMaxOutputTokens,
                // The OpenAI adapter sends each value as raw JSON, so the string goes JSON-quoted.
                AdditionalProperties = new() { [PurposeKey] = JsonSerializer.Serialize(ReadAloudPurpose) },
            };
        await _session.SendAsync(first, token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task SetPaceAsync(ConversationVoicePace pace)
    {
        ArgumentNullException.ThrowIfNull(pace);
        CancellationToken token;
        lock (_lifecycle)
        {
            // Ending: the token source may already be gone, and a closing socket needs no update.
            if (_disposed != 0)
            {
                return Task.CompletedTask;
            }

            token = _cts.Token;
        }

        return _session.SendAsync(new SessionUpdateRealtimeClientMessage(_options(pace, false)), token);
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

            case ResponseCreatedRealtimeServerMessage created
                when created.Type == RealtimeServerMessageType.ResponseCreated && IsReadAloud(created):
                _readAloudResponse = created.ResponseId;
                _logger.LogInformation("Realtime voice is reading the partner's last message aloud");
                break;

            // The read-aloud turn's words are already on the page.
            case OutputTextAudioRealtimeServerMessage text
                when text.ResponseId is not null && text.ResponseId == _readAloudResponse
                    && (text.Type == RealtimeServerMessageType.OutputAudioTranscriptionDelta
                        || text.Type == RealtimeServerMessageType.OutputAudioTranscriptionDone):
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

            case { RawRepresentation: OpenAI.Realtime.RealtimeServerUpdateSessionUpdated updated }:
                LogPace(updated);
                break;
        }
    }

    private static bool IsReadAloud(ResponseCreatedRealtimeServerMessage created) =>
        created.ResponseId is not null
        && created.AdditionalProperties is { } metadata
        && metadata.TryGetValue(PurposeKey, out var purpose)
        // Echoed as the raw JSON value (BinaryData), so quoted.
        && string.Equals(purpose?.ToString()?.Trim('"'), ReadAloudPurpose, StringComparison.Ordinal);

    /// <summary>Logs the speed and pause the provider confirmed (no content).</summary>
    private void LogPace(OpenAI.Realtime.RealtimeServerUpdateSessionUpdated updated)
    {
        try
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                ((IJsonModel<OpenAI.Realtime.RealtimeServerUpdateSessionUpdated>)updated).Write(writer, ModelReaderWriterOptions.Json);
            }
            using var json = JsonDocument.Parse(buffer.ToArray());
            if (!json.RootElement.TryGetProperty("session", out var session)
                || !session.TryGetProperty("audio", out var audio))
            {
                return;
            }
            double? speed = audio.TryGetProperty("output", out var output)
                && output.TryGetProperty("speed", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : null;
            int? pause = audio.TryGetProperty("input", out var input)
                && input.TryGetProperty("turn_detection", out var turn) && turn.ValueKind == JsonValueKind.Object
                && turn.TryGetProperty("silence_duration_ms", out var ms) && ms.ValueKind == JsonValueKind.Number
                    ? ms.GetInt32() : null;
            _logger.LogInformation("Realtime voice pace: speed {Speed}, pause {PauseMs} ms", speed, pause);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            _logger.LogDebug(ex, "Realtime voice could not read the session update");
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
