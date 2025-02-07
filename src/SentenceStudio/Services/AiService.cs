using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using OpenAI.Audio;
using SentenceStudio.Models;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Images;

namespace SentenceStudio.Services;

public class AiService {

    private readonly string _openAiApiKey;
    private readonly IChatClient _client;
    private readonly AudioClient _audio;
    private readonly ImageClient _image;
    public AiService(IConfiguration configuration)
    {
        _openAiApiKey = configuration.GetRequiredSection("Settings").Get<Settings>().OpenAIKey;

        _client = new OpenAIClient(_openAiApiKey).AsChatClient(modelId: "gpt-4o-mini");
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
            var response = await _client.CompleteAsync<T>(prompt);
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
                new ImageContent(new Uri(imagePath))
            );

            var response = await _client.CompleteAsync<string>(new List<ChatMessage> { message });
            
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

    public async Task<Stream> TextToSpeechAsync(string text, string voice)
    {
        if(Connectivity.NetworkAccess != NetworkAccess.Internet){
            WeakReferenceMessenger.Default.Send(new ConnectivityChangedMessage(false));  
            return default(Stream);
        }

        var aiClient = new AIClient(_openAiApiKey);
        return await aiClient.TextToSpeechAsync(text, voice);
    }
}
    
