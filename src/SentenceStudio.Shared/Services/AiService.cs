using Microsoft.Extensions.Configuration;
using OpenAI.Audio;
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;
using OpenAI.Images;
using Microsoft.Extensions.Logging;
using System.Net.Http;
using CommunityToolkit.Mvvm.Messaging;
using SentenceStudio.Abstractions;
using SentenceStudio.Messages;
using SentenceStudio.Services.Api;

namespace SentenceStudio.Services;

public class AiService {

    private readonly string _openAiApiKey;
    private readonly IChatClient _client;
    private readonly AudioClient _audio;
    private readonly ImageClient _image;
    private readonly ILogger<AiService> _logger;
    private readonly IConnectivityService _connectivity;
    private readonly IAiGatewayClient? _aiGatewayClient;
    private readonly ISpeechGatewayClient? _speechGatewayClient;
    public AiService(
        IConfiguration configuration,
        IChatClient client,
        ILogger<AiService> logger,
        IConnectivityService connectivity,
        IAiGatewayClient? aiGatewayClient = null,
        ISpeechGatewayClient? speechGatewayClient = null)
    {
        _client = client;
        _logger = logger;
        _connectivity = connectivity;
        _aiGatewayClient = aiGatewayClient;
        _speechGatewayClient = speechGatewayClient;
        _openAiApiKey = configuration.GetRequiredSection("Settings").Get<Settings>().OpenAIKey;

        // _client = new OpenAIClient(_openAiApiKey).AsChatClient(modelId: "gpt-4o-mini");
        _audio = new("tts-1", _openAiApiKey);
        _image = new ImageClient("gpt-4o", _openAiApiKey);
    }

    public async Task<T> SendPrompt<T>(string prompt)
    {
        if (!_connectivity.IsInternetAvailable)
        {
            _logger.LogWarning("No internet connection available for AI prompt");
            WeakReferenceMessenger.Default.Send(new ConnectivityChangedMessage(false));
            return default(T);
        }

        try
        {
            if (_aiGatewayClient != null)
            {
                try
                {
                    var gatewayResult = await _aiGatewayClient.SendPromptAsync<T>(prompt);
                    _logger.LogDebug("AI gateway response received: {HasResult}", gatewayResult != null);
                    return gatewayResult;
                }
                catch (HttpRequestException ex) when (_client != null)
                {
                    _logger.LogWarning(ex, "AI gateway unavailable, falling back to direct client");
                }
            }

            _logger.LogDebug("Sending prompt to AI (length: {PromptLength} chars)", prompt?.Length ?? 0);
            var response = await _client.GetResponseAsync<T>(prompt);
            var hasResult = response != null && response.Result != null;
            _logger.LogDebug("AI response received: {HasResult}", hasResult);
            return response.Result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred in SendPrompt");
            return default(T);
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

            var response = await _client.GetResponseAsync<string>(new List<ChatMessage> { message });

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
            try
            {
                var stream = await _speechGatewayClient.SynthesizeAsync(text, voice, speed);
                if (stream != null)
                {
                    return stream;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Speech gateway synthesis failed, falling back to local AI client");
            }
        }

        var aiClient = new AIClient(_openAiApiKey, _connectivity);
        return await aiClient.TextToSpeechAsync(text, voice, speed);
    }
}
    
