using System;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Azure.AI.OpenAI;
using Microsoft.Extensions.Configuration;
using SentenceStudio.Models;

namespace SentenceStudio.Services
{
    public class ConversationService
    {
        readonly IConfiguration configuration;
        private AiService _aiService;

        public ConversationService(IServiceProvider service, IConfiguration configuration)
        {
            _aiService = service.GetRequiredService<AiService>();
            this.configuration = configuration;
        }

        public async Task<string> StartConversation()
        {
            string prompt = "Let's have a conversation in Korean. You will start by saying hello and asking my name. Then wait for my reply.";
            // prompt += "I will respond with my name and ask you a question. ";
            // prompt += "You will answer the question and ask me a question. ";
            // prompt += "We will continue this conversation until I say goodbye. ";
            // prompt += "Valid questions are: What is your name? How old are you? When is your birthday? Where are you from? Where do you live? What is your favorite color? What is your favorite food? ";
            prompt += "You will play the role of 김철수 (Kim Cheolsu), a 25-year-old drama writer from Seoul. You are a native Korean speaker, unmarried. Make up the rest of your backstory as needed to answer my questions. ";
            prompt += "Respond naturally as you would in a real conversation. ";
            prompt += "Please begin.";
        
            try
            {
                var key = this.configuration.GetValue<string>("OpenAI:ApiKey", "oops");
                var aiClient = new AIClient(key);
                var response = await aiClient.SendPrompt(prompt);
                return response;

            }
            catch (Exception ex)
            {
                // Handle any exceptions that occur during the process
                Debug.WriteLine($"An error occurred StartConversation: {ex.Message}");
                return string.Empty;
            }
        }   

        public async Task<string> ContinueConveration(List<ConversationChunk> chunks)
        {
            string prompt = "We are having a conversation in Korean. ";
            prompt += "You are playing the role of 김철수 (Kim Cheolsu), a 25-year-old drama writer from Seoul. You are a native Korean speaker, unmarried. Make up the rest of your backstory as needed to answer my questions. ";
            prompt += "If you need to ask me a question, valid topics are: What is your name? How old are you? When is your birthday? Where are you from? Where do you live? What is your favorite color? What is your favorite food? ";
            prompt += "Respond naturally as you would in a real conversation. ";
            prompt += "Here is our conversation so far. Please continue the conversation until I say goodbye.";
        
            foreach (var chunk in chunks)
            {
                prompt += $"{chunk.Author.FirstName} said \"{chunk.Text}\". ";
            }

            try
            {
                var key = this.configuration.GetValue<string>("OpenAI:ApiKey", "oops");
                var aiClient = new AIClient(key);
                var response = await aiClient.SendPrompt(prompt);
                return response;

            }
            catch (Exception ex)
            {
                // Handle any exceptions that occur during the process
                Debug.WriteLine($"An error occurred StartConversation: {ex.Message}");
                return string.Empty;
            }
        }  
    }
}
