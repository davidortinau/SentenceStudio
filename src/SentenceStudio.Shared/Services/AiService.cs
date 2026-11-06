using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net.Http;
using CommunityToolkit.Mvvm.Messaging;
using SentenceStudio.Abstractions;
using SentenceStudio.Messages;
using SentenceStudio.Services.Api;

namespace SentenceStudio.Services;

/// <summary>
/// The app's AI calls. On client hosts (MAUI, the native heads) prompts go through
/// <see cref="IAiGatewayClient"/> and speech through <see cref="ISpeechGatewayClient"/>, that is,
/// through our API, which alone holds the Foundry credentials. Server hosts (API, Workers, web)
/// call Foundry directly through the registered <see cref="IChatClient"/>s. Nothing that only the
/// direct path uses is read or built at construction: the chat client is optional, and the
/// <c>Settings:OpenAIKey</c> credential and the Foundry audio client are taken only when the
/// direct speech path runs.
/// </summary>
public class AiService : IAiService
{
    private readonly IChatClient? _client;
    private readonly ILogger<AiService> _logger;
    private readonly IConnectivityService _connectivity;
    private readonly IAiGatewayClient? _aiGatewayClient;
    private readonly ISpeechGatewayClient? _speechGatewayClient;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly string _ttsModel;

    public AiService(
        IConfiguration configuration,
        ILogger<AiService> logger,
        IConnectivityService connectivity,
        IHttpClientFactory httpClientFactory,
        IServiceProvider serviceProvider,
        IAiGatewayClient? aiGatewayClient = null,
        ISpeechGatewayClient? speechGatewayClient = null,
        IChatClient? client = null)
    {
        _client = client;
        _logger = logger;
        _connectivity = connectivity;
        _httpClientFactory = httpClientFactory;
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _aiGatewayClient = aiGatewayClient;
        _speechGatewayClient = speechGatewayClient;
        _ttsModel = configuration["AI:OpenAI:TtsModel"] ?? "tts-1";
    }

    /// <summary>Resolves the keyed IChatClient for the requested tier, falling back to the default client.</summary>
    private IChatClient ResolveClient(AiTier tier) =>
        _serviceProvider.GetKeyedService<IChatClient>(tier.ToKey())
        ?? _client
        ?? throw new InvalidOperationException(
            "No direct AI chat client is registered on this host; its AI goes through the API gateway.");

    /// <summary>
    /// The credential of the direct (Foundry) speech path, required only there. Server hosts set
    /// it (or "not-configured") at startup; a client host without a speech gateway has none.
    /// </summary>
    private string DirectSpeechApiKey()
    {
        var key = _configuration["Settings:OpenAIKey"];
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException(
                "Direct speech requires Settings:OpenAIKey; this host has no speech gateway or key.");
        return key;
    }

    public Task<T> SendPrompt<T>(string prompt, AiTier tier = AiTier.Fast, string? reasoningEffort = null) =>
        SendWhenOnlineAsync(nameof(SendPrompt), async () =>
        {
            var model = tier == AiTier.Reasoning
                ? AiClientRegistration.ReasoningModel(_configuration)
                : AiClientRegistration.FastModel(_configuration);

            if (!AiChatOptionsFactory.IsSupportedReasoningEffort(reasoningEffort))
            {
                throw new ArgumentOutOfRangeException(nameof(reasoningEffort),
                    "Reasoning effort must be one of: minimal, low, medium, high.");
            }

            if (_aiGatewayClient != null)
            {
                _logger.LogDebug("Sending prompt to AI via gateway (tier: {Tier} → {Model}, effort: {ReasoningEffort})",
                    tier, model, reasoningEffort ?? "<default>");
                var gatewayResult = await _aiGatewayClient.SendPromptAsync<T>(prompt, tier, reasoningEffort);
                _logger.LogDebug("AI gateway response received: {HasResult}", gatewayResult != null);
                return gatewayResult;
            }

            _logger.LogDebug("Sending prompt to AI via direct client (tier: {Tier} → {Model}, effort: {ReasoningEffort}, length: {PromptLength} chars)",
                tier, model, reasoningEffort ?? "<default>", prompt?.Length ?? 0);
            var response = await ResolveClient(tier).GetResponseAsync<T>(
                new[]
                {
                    new ChatMessage(ChatRole.User, prompt)
                },
                AiChatOptionsFactory.Create(reasoningEffort: reasoningEffort));
            var hasResult = response != null && response.Result != null;
            _logger.LogDebug("AI response received: {HasResult}", hasResult);
            return response.Result;
        });

    /// <summary>
    /// Sends <paramref name="instructions"/> as the system instructions and
    /// <paramref name="message"/> as the one user message (gateway: /api/v1/ai/chat-messages).
    /// Azure's prompt shield rejects instructions pasted into the user message ("SYSTEM
    /// INSTRUCTIONS: ... TASK: ..."), so persona prompts go this way. Runs on the fast tier, the
    /// only one that endpoint serves. Returns default(T) when offline, like
    /// <see cref="SendPrompt{T}"/>; failures are rethrown.
    /// </summary>
    public async Task<T> SendPromptWithInstructions<T>(string instructions, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return await SendWhenOnlineAsync(nameof(SendPromptWithInstructions), async () =>
        {
            if (_aiGatewayClient != null)
            {
                _logger.LogDebug("Sending instructions and message to AI via gateway");
                return await _aiGatewayClient.SendMessagesAsync<T>([("user", message)], instructions);
            }

            _logger.LogDebug("Sending instructions and message to AI via direct client");
            var response = await ResolveClient(AiTier.Fast).GetResponseAsync<T>(
                new[] { new ChatMessage(ChatRole.User, message) },
                AiChatOptionsFactory.Create(instructions: instructions));
            return response.Result;
        });
    }

    /// <summary>
    /// Offline: tells the app and returns default(T) without sending. Online: sends, and logs and
    /// rethrows any failure.
    /// </summary>
    private async Task<T> SendWhenOnlineAsync<T>(string operation, Func<Task<T>> send)
    {
        if (!_connectivity.IsInternetAvailable)
        {
            _logger.LogWarning("No internet connection available for AI prompt");
            WeakReferenceMessenger.Default.Send(new ConnectivityChangedMessage(false));
            return default(T);
        }

        try
        {
            return await send();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred in {Operation}", operation);
            throw;
        }
    }

    public async Task<string> SendImage(string imagePath, string prompt)
    {
        if (!_connectivity.IsInternetAvailable)
        {
            WeakReferenceMessenger.Default.Send(new ConnectivityChangedMessage(false));
            return string.Empty;
        }

        try
        {
            var message = new ChatMessage(ChatRole.User, prompt);
            
            // DataContent requires a data URI (base64), not HTTP URLs
            // Download the image and convert to base64 if it's an HTTP URL
            if (imagePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || 
                imagePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                using var httpClient = new HttpClient();
                var imageBytes = await httpClient.GetByteArrayAsync(imagePath);
                var base64 = Convert.ToBase64String(imageBytes);
                
                // Detect media type from URL or default to jpeg
                var mediaType = imagePath.Contains(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
                var dataUri = $"data:{mediaType};base64,{base64}";
                
                message.Contents.Add(new DataContent(new Uri(dataUri), mediaType: mediaType));
            }
            else
            {
                // Assume it's already a data URI or local file
                message.Contents.Add(new DataContent(new Uri(imagePath), mediaType: "image/jpeg"));
            }

            // Vision/description grading is a reasoning task — run it on the reasoning tier.
            var response = await ResolveClient(AiTier.Reasoning).GetResponseAsync<string>(new List<ChatMessage> { message });

            _logger.LogDebug("SendImage response received: {ResponseLength} chars", response.Result?.Length ?? 0);
            return response.Result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred in SendImage for path {ImagePath}", imagePath);
            return string.Empty;
        }
    }

    public async Task<Stream> TextToSpeechAsync(string text, string voice, float speed = 1.0f)
    {
        if (!_connectivity.IsInternetAvailable)
        {
            WeakReferenceMessenger.Default.Send(new ConnectivityChangedMessage(false));  
            return default(Stream);
        }

        if (_speechGatewayClient != null)
        {
            var stream = await _speechGatewayClient.SynthesizeAsync(text, voice, speed);
            if (stream != null)
            {
                return stream;
            }
        }

        // Direct client fallback for standalone (non-Aspire) mode: the Foundry audio client is
        // built here, only on this path, through the "openai" named HttpClient (Polly resilience).
        var apiKey = DirectSpeechApiKey();
        var httpClient = _httpClientFactory.CreateClient("openai");
        var aiClient = new AIClient(
            httpClient, apiKey, _connectivity,
            configuration: _configuration, ttsModel: _ttsModel);
        return await aiClient.TextToSpeechAsync(text, voice, speed);
    }
}
    
