using System.Diagnostics;
using Azure.AI.OpenAI;
using CommunityToolkit.Mvvm.Messaging;
using SentenceStudio.Messages;

namespace SentenceStudio.Services;

public class AIClient
{
    private readonly string _apiKey;

    public AIClient(string apiKey)
    {
        _apiKey = apiKey;
    }

    public async Task<string> SendPrompt(string prompt, bool shouldReturnJson = false, bool streamResponse = false)
    {
        // TODO check connectivity and bypass if not connected
        if(Connectivity.NetworkAccess != NetworkAccess.Internet){
            WeakReferenceMessenger.Default.Send(new ConnectivityChangedMessage(false));  
            return string.Empty;
        }

        // Implement the logic to send the prompt to OpenAI and receive the conversation response
        // using the Azure.AI.OpenAI library
        var client = new OpenAIClient(_apiKey, new OpenAIClientOptions());
        var chatCompletionsOptions = new ChatCompletionsOptions()
        {
            DeploymentName = "gpt-4-turbo",// "gpt-3.5-turbo", // Use DeploymentName for "model" with non-Azure clients
            Messages =
            {
                new ChatRequestUserMessage(prompt),
            }, 
            ResponseFormat = (shouldReturnJson) ? ChatCompletionsResponseFormat.JsonObject : ChatCompletionsResponseFormat.Text
        };
        
        
        if(streamResponse){
            await foreach (StreamingChatCompletionsUpdate chatUpdate in client.GetChatCompletionsStreaming(chatCompletionsOptions))
            {
                if (chatUpdate.Role.HasValue)
                {
                    Console.Write($"{chatUpdate.Role.Value.ToString().ToUpperInvariant()}: ");
                }
                if (!string.IsNullOrEmpty(chatUpdate.ContentUpdate))
                {
                    // Console.Write(chatUpdate.ContentUpdate);
                    WeakReferenceMessenger.Default.Send(new ChatCompletionMessage(chatUpdate.ContentUpdate));  
                }
                if (chatUpdate.FinishReason.HasValue)
                {
                    Console.WriteLine($"Chat completion finished: {chatUpdate.FinishReason.Value}");
                    return "End of line";
                }
            }
        }else{
            try{
                var response = await client.GetChatCompletionsAsync(chatCompletionsOptions);
                ChatResponseMessage responseMessage = response.Value.Choices[0].Message;
                
                return responseMessage.Content;
            }catch(Exception ex){
                Debug.WriteLine(ex.Message);
            }
            return string.Empty;
        }
        
        return string.Empty;
    }

    public async Task<string> SendImage(Uri imageUri, string prompt)
    {
        // TODO check connectivity and bypass if not connected
        if(Connectivity.NetworkAccess != NetworkAccess.Internet){
            WeakReferenceMessenger.Default.Send(new ConnectivityChangedMessage(false));  
            return string.Empty;
        }


        var client = new OpenAIClient(_apiKey, new OpenAIClientOptions());
        var imageItem = new ChatMessageImageContentItem(imageUri);
        
        var chatCompletionsOptions = new ChatCompletionsOptions()
        {
            DeploymentName = "gpt-4-turbo",// "gpt-3.5-turbo", // Use DeploymentName for "model" with non-Azure clients            
            Messages =
            {
                new ChatRequestUserMessage(imageItem),
                new ChatRequestUserMessage(prompt),
            }
        };
        
        try
        {
            var response = await client.GetChatCompletionsAsync(chatCompletionsOptions);
            var responseMessage = response.Value.Choices[0].Message.Content;
            return responseMessage;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.Message);
        }

        return string.Empty;
    }
}