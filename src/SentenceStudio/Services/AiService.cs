using Microsoft.Extensions.Configuration;
using OpenAI.Audio;
using Microsoft.Extensions.AI;
using OpenAI.Images;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.Services;

public class AiService {

    private readonly string _openAiApiKey;
    private readonly IChatClient _client;
    private readonly AudioClient _audio;
    private readonly ImageClient _image;
    private readonly ILogger<AiService> _logger;

    public AiService(IConfiguration configuration, IChatClient client, ILogger<AiService> logger)
    {
        _client = client;
        _logger = logger;
        _openAiApiKey = configuration.GetRequiredSection("Settings").Get<Settings>().OpenAIKey;

        // _client = new OpenAIClient(_openAiApiKey).AsChatClient(modelId: "gpt-4o-mini");
        _audio = new("tts-1", _openAiApiKey);
        _image = new ImageClient("gpt-4o", _openAiApiKey);
    }

    public async Task<T> SendPrompt<T>(string prompt)
    {
        if(Connectivity.NetworkAccess != NetworkAccess.Internet){
            _logger.LogWarning("No internet connection available for AI prompt");
            WeakReferenceMessenger.Default.Send(new ConnectivityChangedMessage(false));
            return default(T);
        }

        try
        {
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
        if(Connectivity.NetworkAccess != NetworkAccess.Internet){
            WeakReferenceMessenger.Default.Send(new ConnectivityChangedMessage(false));
            return string.Empty;
        }

        try
        {
            var message = new ChatMessage(ChatRole.User, prompt);
            message.Contents.Add(
                new DataContent(new Uri(imagePath), mediaType: "image/jpeg")
            );

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
        if(Connectivity.NetworkAccess != NetworkAccess.Internet){
            WeakReferenceMessenger.Default.Send(new ConnectivityChangedMessage(false));  
            return default(Stream);
        }

        var aiClient = new AIClient(_openAiApiKey);
        return await aiClient.TextToSpeechAsync(text, voice, speed);
    }
}
    
