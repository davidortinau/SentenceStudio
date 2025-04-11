using Microsoft.Extensions.Configuration;
using OpenAI.Audio;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Images;

namespace SentenceStudio.Services;

public class AiService {

    private readonly string _openAiApiKey;
    private readonly IChatClient _client;
    private readonly AudioClient _audio;
    private readonly ImageClient _image;
    public AiService(IConfiguration configuration, IChatClient client)
    {
        _client = client;
        _openAiApiKey = configuration.GetRequiredSection("Settings").Get<Settings>().OpenAIKey;

        // _client = new OpenAIClient(_openAiApiKey).AsChatClient(modelId: "gpt-4o-mini");
        _audio = new("tts-1", _openAiApiKey);
        _image = new ImageClient("gpt-4o", _openAiApiKey);
    }

    public async Task<T> SendPrompt<T>(string prompt)
    {
        if(Connectivity.NetworkAccess != NetworkAccess.Internet){
            WeakReferenceMessenger.Default.Send(new ConnectivityChangedMessage(false));  
            return default(T);
        }

        try
        {
            var response = await _client.GetResponseAsync<T>(prompt);
            return response.Result;            
        }
        catch (Exception ex)
        {
            // Handle any exceptions that occur during the process
            Debug.WriteLine($"An error occurred SendPrompt: {ex.Message}");
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
            
            Debug.WriteLine($"Response: {response.Result}");
            return response.Result;
        }
        catch (Exception ex)
        {
            
            // Handle any exceptions that occur during the process
            Debug.WriteLine($"An error occurred SendImage: {ex.Message}");
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
    
