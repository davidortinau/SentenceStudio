using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using OpenAI.Audio;
using SentenceStudio.Models;
using Microsoft.Extensions.AI;
using OpenAI;

namespace SentenceStudio.Services;

public class AiService {

    private readonly string _openAiApiKey;
    public AiService(IConfiguration configuration)
    {
        _openAiApiKey = configuration.GetRequiredSection("Settings").Get<Settings>().OpenAIKey;
    }

    public async Task<string> SendPrompt(string prompt, bool shouldReturnJson = false, bool shouldStreamResponse = false)
    {
        try{
            IChatClient client =
                new OpenAIClient(_openAiApiKey)
                    .AsChatClient(modelId: "gpt-4o-mini");

                var response = await client.CompleteAsync(prompt);


            // Create a new instance of the OpenAI client
            // var aiClient = new AIClient(_openAiApiKey);
            

            // Send the prompt to OpenAI and receive the conversation response
            // var response = await aiClient.SendPrompt(prompt, shouldReturnJson, shouldStreamResponse);
            Debug.WriteLine($"Response: {response.Message.Text}");
            return response.Message.Text;
        }
        catch (Exception ex)
        {
            // Handle any exceptions that occur during the process
            Debug.WriteLine($"An error occurred SendPrompt: {ex.Message}");
            return string.Empty;
        }
    }

    public async Task<string> SendImage(string imagePath, string prompt)
    {
        try
        {
            // Create a new instance of the OpenAI client
            var aiClient = new AIClient(_openAiApiKey);

            // Send the image to OpenAI and receive the response
            var response = await aiClient.SendImage(new Uri(imagePath), prompt);
            Debug.WriteLine($"Response: {response}");
            return response;
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
        var aiClient = new AIClient(_openAiApiKey);
        return await aiClient.TextToSpeechAsync(text, voice);
    }
}
    
