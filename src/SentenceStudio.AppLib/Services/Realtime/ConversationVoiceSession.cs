#pragma warning disable MEAI001 // IRealtimeClient is experimental in Microsoft.Extensions.AI.
#pragma warning disable OPENAI002 // OpenAI.Realtime types are experimental in the OpenAI SDK.

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Services.Realtime;

/// <summary>
/// One live voice conversation for the Conversation activity, built on <see cref="IRealtimeClient"/>.
/// </summary>
/// <remarks>
/// The page streams microphone audio in and receives events out. The session owns the realtime
/// websocket, the instructions, and one read-only tool over the learner's own vocabulary. It never
/// writes learner data; the page decides what to show and keep.
/// </remarks>
public sealed class ConversationVoiceSession : IAsyncDisposable
{
    /// <summary>PCM16 mono at 24 kHz, the format both directions use.</summary>
    public const int SampleRate = 24000;

    private const string ToolName = "lookup_my_vocabulary";

    private readonly IRealtimeClientSession _session;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts;
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
    public event Action<string, string>? ToolInvoked;

    /// <summary>The provider reported an error. Argument: message.</summary>
    public event Action<string>? Failed;

    private ConversationVoiceSession(IRealtimeClientSession session, ILogger logger, CancellationTokenSource cts)
    {
        _session = session;
        _logger = logger;
        _cts = cts;
    }

    /// <summary>Opens a realtime session configured for spoken practice in <paramref name="targetLanguage"/>.</summary>
    public static async Task<ConversationVoiceSession> StartAsync(
        IRealtimeClient client,
        string instructions,
        string targetLanguage,
        string transcriptionModel,
        IReadOnlyList<VocabularyWord> vocabulary,
        ILogger logger,
        CancellationToken cancellationToken = default)
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

        var session = await client.CreateSessionAsync(options, cts.Token).ConfigureAwait(false);
        voice = new ConversationVoiceSession(session, logger, cts);
        return voice;
    }

    /// <summary>Starts receiving events and asks the partner to speak first.</summary>
    public async Task BeginAsync()
    {
        _receiveLoop = Task.Run(ReceiveLoopAsync);
        await _session.SendAsync(new CreateResponseRealtimeClientMessage(), _cts.Token).ConfigureAwait(false);
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
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Realtime voice session ended unexpectedly");
            Failed?.Invoke(ex.Message);
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
        ToolInvoked?.Invoke(needle, matches.Count == 0 ? "not in your list" : string.Join("; ", matches));
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cts.Cancel();
        try
        {
            if (_receiveLoop is not null)
            {
                await _receiveLoop.ConfigureAwait(false);
            }
        }
        catch
        {
        }

        await _session.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
