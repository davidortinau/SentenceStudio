using SentenceStudio.Services.DTOs;
using SentenceStudio.Services;
using SentenceStudio.Services.Api;
using SentenceStudio.Shared.Models;
using SentenceStudio.Native;

namespace SentenceStudio.Native.Tests;

internal sealed class ScratchResourceAiGateway : IAiGatewayClient, IOwnerBoundNativeAiGateway
{
    public string? StarterResponse { get; set; } = "가,go\n나,I";
    public VocabularyExtractionResponse? TranscriptResponse { get; set; } =
        new()
        {
            Vocabulary =
            [
                new ExtractedVocabularyItem {
                    TargetLanguageTerm = "하늘",
                    NativeLanguageTerm = "sky"
                }
            ]
        };
    public TaskCompletionSource<bool>? TranscriptGate { get; set; }
    public TaskCompletionSource<bool>? StarterGate { get; set; }
    public int Calls { get; private set; }
    /// <summary>Cloze replies are built from the rendered prompt; null replies and failures stay honest.</summary>
    public Func<string, ClozureResponse?>? ClozeResponder { get; set; }
    public Exception? ClozeFailure { get; set; }
    public TaskCompletionSource<bool>? ClozeGate { get; set; }
    /// <summary>A hung gateway: the cloze call waits for it and ignores cancellation.</summary>
    public TaskCompletionSource<bool>? ClozeHang { get; set; }
    /// <summary>The reply to an example sentence prompt (source GeneratedExampleSentencesDto).</summary>
    public GeneratedExampleSentencesDto? ExampleResponse { get; set; }
    public string? LastPrompt { get; private set; }
    public string? LastOwner { get; private set; }

    public Task<T?> SendPromptAsync<T>(
        string owner, string prompt, CancellationToken cancellationToken)
    {
        LastOwner = owner;
        return SendPromptAsync<T>(prompt, cancellationToken: cancellationToken);
    }

    public async Task<T?> SendPromptAsync<T>(
        string prompt, AiTier tier = AiTier.Fast, string? reasoningEffort = null,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        LastPrompt = prompt;
        if (typeof(T) == typeof(string))
        {
            if (StarterGate is not null)
                await StarterGate.Task.WaitAsync(cancellationToken);
            return (T?)(object?)StarterResponse;
        }
        if (typeof(T) == typeof(VocabularyExtractionResponse))
        {
            if (TranscriptGate is not null)
                await TranscriptGate.Task.WaitAsync(cancellationToken);
            return (T?)(object?)TranscriptResponse;
        }
        if (typeof(T) == typeof(ClozureResponse))
        {
            if (ClozeGate is not null)
                await ClozeGate.Task.WaitAsync(cancellationToken);
            if (ClozeHang is not null)
                await ClozeHang.Task;
            if (ClozeFailure is not null)
                throw ClozeFailure;
            return (T?)(object?)ClozeResponder?.Invoke(prompt);
        }
        if (typeof(T) == typeof(GeneratedExampleSentencesDto))
            return (T?)(object?)ExampleResponse;
        throw new NotSupportedException("The isolated AI fixture supports only resource, cloze and example responses.");
    }

    public Task<T?> SendMessagesAsync<T>(
        List<(string role, string content)> messages, string? instructions = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<string> AnalyzeImageAsync(
        string prompt, string imageBase64, string mediaType = "image/jpeg",
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
