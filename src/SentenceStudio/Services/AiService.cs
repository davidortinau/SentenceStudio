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

    public async Task<string> SendPrompt(string prompt, bool shouldReturnJson = false)
    {
        try{
            // Create a new instance of the OpenAI client
            var aiClient = new AIClient(_openAiApiKey);
            

            // Send the prompt to OpenAI and receive the conversation response
            var response = await aiClient.SendPrompt(prompt, shouldReturnJson);
            Debug.WriteLine($"Response: {response}");
            return response;
        }
        catch (Exception ex)
        {
            // Handle any exceptions that occur during the process
            Debug.WriteLine($"An error occurred SendPrompt: {ex.Message}");
            return string.Empty;
        }
    }
    
    public async Task<T> SendPrompt<T>(string prompt) where T : new()
    {
        try{
            // Create a new instance of the OpenAI client
            var aiClient = new AIClient(_openAiApiKey);
            

            // Send the prompt to OpenAI and receive the conversation response
            var response = await aiClient.SendPrompt(prompt);
            if(string.IsNullOrEmpty(response))
            {
                return new T();
            }

            Debug.WriteLine($"Response: {response}");
            if (typeof(T) == typeof(string))
            {
                // If T is of type string, directly return the response
                return (T)(object)response;
            }else{
                // Process the response and return the reply
                response = CleanJson(response);
                // Debug.WriteLine($"CleanResponse: {response}");
                var reply = JsonSerializer.Deserialize<T>(response);

                return reply;
            }
        }
        catch (Exception ex)
        {
            // Handle any exceptions that occur during the process
            Debug.WriteLine($"An error occurred SendPrompt: {ex.Message}");
            return new T();
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

    private string CleanJson(string response)
    {
        if (string.IsNullOrEmpty(response))
        {
            return response;
        }

        int startIndex = response.IndexOf('{');
        int endIndex = response.LastIndexOf('}');

        if (startIndex == -1 || endIndex == -1 || startIndex > endIndex)
        {
            // If there's no valid JSON object in the response, return an empty string
            return string.Empty;
        }

        return response.Substring(startIndex, endIndex - startIndex + 1);
    }
}
    
