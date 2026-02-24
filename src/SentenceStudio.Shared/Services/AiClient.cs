using System.ClientModel;
using OpenAI.Audio;
using OpenAI.Chat;
using OpenAI.Images;
using Microsoft.Extensions.Logging;
using CommunityToolkit.Mvvm.Messaging;
using SentenceStudio.Abstractions;
using SentenceStudio.Messages;

namespace SentenceStudio.Services;

public class AIClient
{
    private readonly string _apiKey;
    private readonly ChatClient _client;
    private readonly AudioClient _audio;
    private readonly ImageClient _image;
    private readonly ILogger<AIClient>? _logger;
    private readonly IConnectivityService _connectivity;

    public AIClient(string apiKey, IConnectivityService connectivity, ILogger<AIClient>? logger = null, bool hd = false)
    {
        _apiKey = apiKey;
        _connectivity = connectivity;
        _logger = logger;
        _client = new ChatClient("gpt-4o", _apiKey);
        _audio = new("tts-1", _apiKey);
        _image = new ImageClient("gpt-4o", _apiKey);
    }

    public async Task<Stream> TextToSpeechAsync(string text, string voice, float speed = 1.0f)
    {
        text = text.Trim();
        try
        {
            BinaryData speech = await _audio.GenerateSpeechAsync(text, GeneratedSpeechVoice.Echo,
            new SpeechGenerationOptions
            {
                SpeedRatio = speed,
                ResponseFormat = GeneratedSpeechFormat.Wav
            });

            // using FileStream stream = File.OpenWrite($"{Guid.NewGuid()}.mp3");
            return speech.ToStream();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred in TextToSpeechAsync");
        }

        return null;
    }

    public async Task<string> SendPrompt(string prompt, bool shouldReturnJson = false, bool streamResponse = false)
    {
        // TODO check connectivity and bypass if not connected
        if (!_connectivity.IsInternetAvailable)
        {
            WeakReferenceMessenger.Default.Send(new ConnectivityChangedMessage(false));  
            return string.Empty;
        }

        // Implement the logic to send the prompt to OpenAI and receive the conversation response
        // using the Azure.AI.OpenAI library
        
        // var chatCompletionsOptions = new ChatCompletionsOptions()
        // {
        //     DeploymentName = "gpt-4o",//"gpt-3.5-turbo", //"gpt-4-turbo",// "gpt-4o" "gpt-3.5-turbo", // Use DeploymentName for "model" with non-Azure clients
        //     Messages =
        //     {
        //         new ChatRequestUserMessage(prompt),
        //     }, 
        //     ResponseFormat = (shouldReturnJson) ? ChatCompletionsResponseFormat.JsonObject : ChatCompletionsResponseFormat.Text
        // };
        
        List<ChatMessage> messages = new List<ChatMessage>()
        {
            new UserChatMessage(prompt)
        };
        
        if(streamResponse){
            await foreach (StreamingChatCompletionUpdate chatUpdate in _client.CompleteChatStreamingAsync(messages))
            {
                if (chatUpdate.Role.HasValue)
                {
                    Console.Write($"{chatUpdate.Role.Value.ToString().ToUpperInvariant()}: ");
                }
                if (chatUpdate.ContentUpdate.Count > 0)
                {
                    // Console.Write(chatUpdate.ContentUpdate);
                    WeakReferenceMessenger.Default.Send(new ChatCompletionMessage(chatUpdate.ContentUpdate[0].Text));  
                }
                
                if (chatUpdate.FinishReason.HasValue)
                {
                    //Console.Writeline($"Chat completion finished: {chatUpdate.FinishReason.Value}");
                    return "End of line";
                }
            }
        }else{
            try{
                ChatCompletionOptions options = new ChatCompletionOptions()
                {
                    ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat()
                };

                ClientResult<ChatCompletion> result = await _client.CompleteChatAsync(messages, options);
                return result.Value.Content[0].Text;
            }catch(Exception ex){
                _logger.LogError(ex, "Error occurred in SendPrompt");
            }
            return string.Empty;
        }

        return string.Empty;
    }

    public async Task<string> SendImage(Uri imageUri, string prompt)
    {
        // TODO check connectivity and bypass if not connected
        if (!_connectivity.IsInternetAvailable)
        {
            WeakReferenceMessenger.Default.Send(new ConnectivityChangedMessage(false));  
            return string.Empty;
        }
        
        List<ChatMessage> messages = new List<ChatMessage>()
        {
            new UserChatMessage(
                ChatMessageContentPart.CreateTextPart(prompt),
                ChatMessageContentPart.CreateImagePart(imageUri)
            )
        };

        ChatCompletionOptions options = new ChatCompletionOptions()
        {
            ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat()
        };
        
        try
        {
            ClientResult<ChatCompletion> result = await _client.CompleteChatAsync(messages, options);
            return result.Value.Content[0].Text;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred in SendImage");
        }

        return string.Empty;
    }
}
