using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace SentenceStudio.Services;

public class AiService {

    private readonly string _openAiApiKey;
    public AiService(IConfiguration configuration)
    {
        _openAiApiKey = configuration.GetValue<string>("OpenAI:ApiKey", "oops");
    }
    
    public async Task<T> SendPrompt<T>(string prompt) where T : new()
    {
        try{
            // Create a new instance of the OpenAI client
            var aiClient = new AIClient(_openAiApiKey);
            

            // Send the prompt to OpenAI and receive the conversation response
            var response = await aiClient.SendPrompt(prompt);

            // Process the response and return the reply
            var reply = JsonSerializer.Deserialize<T>(response);

            return reply;
        }
        catch (Exception ex)
        {
            // Handle any exceptions that occur during the process
            Debug.WriteLine($"An error occurred SendPrompt: {ex.Message}");
            return new T();
        }
    }
}
    
