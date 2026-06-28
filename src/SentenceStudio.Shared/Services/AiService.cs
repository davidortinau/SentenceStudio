using Microsoft.Extensions.Configuration;
using OpenAI.Audio;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net.Http;
using CommunityToolkit.Mvvm.Messaging;
using SentenceStudio.Abstractions;
using SentenceStudio.Messages;
using SentenceStudio.Services.Api;

namespace SentenceStudio.Services;

public class AiService : IAiService
{

    private readonly string _openAiApiKey;
    private readonly IChatClient _client;
    private readonly AudioClient _audio;
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
        IChatClient client,
        ILogger<AiService> logger,
        IConnectivityService connectivity,
        IHttpClientFactory httpClientFactory,
        IServiceProvider serviceProvider,
        IAiGatewayClient? aiGatewayClient = null,
        ISpeechGatewayClient? speechGatewayClient = null)
    {
        _client = client;
        _logger = logger;
        _connectivity = connectivity;
        _httpClientFactory = httpClientFactory;
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _aiGatewayClient = aiGatewayClient;
        _speechGatewayClient = speechGatewayClient;
        _openAiApiKey = configuration.GetRequiredSection("Settings").Get<Settings>().OpenAIKey;

        _ttsModel = configuration["AI:OpenAI:TtsModel"] ?? "tts-1";

        // Route the audio client through the Polly-backed HttpClient and the configured
        // (Foundry) endpoint, same as the tiered chat clients.
        var httpClient = _httpClientFactory.CreateClient("openai");
        var openAiClient = AiClientRegistration.CreateOpenAIClient(configuration, _openAiApiKey, httpClient);

        _audio = openAiClient.GetAudioClient(_ttsModel);
    }

    /// <summary>Resolves the keyed IChatClient for the requested tier, falling back to the default client.</summary>
    private IChatClient ResolveClient(AiTier tier)
    {
        var client = _serviceProvider.GetKeyedService<IChatClient>(tier.ToKey());
        if (client == null)
        {
            return _client;
        }
        return client;
    }

    public async Task<T> SendPrompt<T>(string prompt, AiTier tier = AiTier.Fast, string? reasoningEffort = null)
    {
        if (!_connectivity.IsInternetAvailable)
        {
            _logger.LogWarning("No internet connection available for AI prompt");
            WeakReferenceMessenger.Default.Send(new ConnectivityChangedMessage(false));
            return default(T);
        }

        try
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
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred in SendPrompt");
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

        // Direct client fallback for standalone (non-Aspire) mode
        // Use the "openai" named HttpClient which has Polly resilience configured
        var httpClient = _httpClientFactory.CreateClient("openai");
        var aiClient = new AIClient(
            httpClient, _openAiApiKey, _connectivity,
            configuration: _configuration, ttsModel: _ttsModel);
        return await aiClient.TextToSpeechAsync(text, voice, speed);
    }
}
    
