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
        throw new NotSupportedException("The isolated AI fixture supports only resource responses.");
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
