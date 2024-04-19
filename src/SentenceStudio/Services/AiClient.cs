using System.Diagnostics;
using Azure.AI.OpenAI;

namespace SentenceStudio.Services;

public class AIClient
{
    private readonly string _apiKey;

    public AIClient(string apiKey)
    {
        _apiKey = apiKey;
    }

    public async Task<string> SendPrompt(string prompt)
    {
        // Implement the logic to send the prompt to OpenAI and receive the conversation response
        // using the Azure.AI.OpenAI library
        var client = new OpenAIClient(_apiKey, new OpenAIClientOptions());
        var chatCompletionsOptions = new ChatCompletionsOptions()
        {
            DeploymentName = "gpt-4-turbo",// "gpt-3.5-turbo", // Use DeploymentName for "model" with non-Azure clients
            Messages =
            {
                new ChatRequestUserMessage(prompt),
            }
        };
        
        try{
            var response = await client.GetChatCompletionsAsync(chatCompletionsOptions);
            ChatResponseMessage responseMessage = response.Value.Choices[0].Message;
            
            return responseMessage.Content;
        }catch(Exception ex){
            Debug.WriteLine(ex.Message);
        }

        return string.Empty;

        // Alternatively, you can use the streaming API to receive real-time updates
        // await foreach (StreamingChatCompletionsUpdate chatUpdate in client.GetChatCompletionsStreaming(chatCompletionsOptions))
        // {
        //     if (chatUpdate.Role.HasValue)
        //     {
        //         Console.Write($"{chatUpdate.Role.Value.ToString().ToUpperInvariant()}: ");
        //     }
        //     if (!string.IsNullOrEmpty(chatUpdate.ContentUpdate))
        //     {
        //         Console.Write(chatUpdate.ContentUpdate);
        //     }
        // }
    }

    public async Task<string> SendImage(Uri imageUri, string prompt)
    {
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