using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using SentenceStudio.Common;
using SentenceStudio.Shared.Models;
using SQLite;
using Scriban;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI;

namespace SentenceStudio.Services
{
    public class ConversationService
    {
        private SQLiteAsyncConnection Database;
        readonly IConfiguration configuration;
        private AiService _aiService;
        private readonly IChatClient _client;
        private readonly string _openAiApiKey;

        public ConversationService(IServiceProvider service, IConfiguration configuration, IChatClient chatClient)
        {
            _aiService = service.GetRequiredService<AiService>();
            _client = chatClient;
            this.configuration = configuration;

            _openAiApiKey = configuration.GetRequiredSection("Settings").Get<Settings>().OpenAIKey;
        }

        async Task Init()
        {
            if (Database is not null)
                return;

            Database = new SQLiteAsyncConnection(Constants.DatabasePath, Constants.Flags);

            CreateTablesResult result;
            
            try
            {
                result = await Database.CreateTablesAsync<Conversation,ConversationChunk>();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"{ex.Message}");
                await App.Current.Windows[0].Page.DisplayAlert("Error", ex.Message, "Fix it");
            }
        }
        
        public async Task<Conversation> ResumeConversation()
        {
            await Init();
            var mostRecentConversation = await Database.Table<Conversation>().OrderByDescending(c => c.ID).FirstOrDefaultAsync();
            if (mostRecentConversation != null)
            {
                var conversationChunks = await Database.Table<ConversationChunk>().Where(cc => cc.ConversationId == mostRecentConversation.ID).ToListAsync();
                mostRecentConversation.Chunks = conversationChunks;
            }
            return mostRecentConversation;
        }

        public async Task SaveConversationChunk(ConversationChunk chunk)
        {
            await Init();
            await Database.InsertAsync(chunk);
        }

        public async Task<string> StartConversation()
        {
            var prompt = string.Empty;     
            using Stream templateStream = await FileSystem.OpenAppPackageFileAsync("StartConversation.scriban-txt");
            using (StreamReader reader = new StreamReader(templateStream))
            {
                var template = Template.Parse(await reader.ReadToEndAsync());
                prompt = await template.RenderAsync();

                // //Debug.WriteLine(prompt);
            }

            try
            {
                
                var response = await _client.GetResponseAsync<string>(prompt);
                return response.Result;

            }
            catch (Exception ex)
            {
                // Handle any exceptions that occur during the process
                Debug.WriteLine($"An error occurred StartConversation: {ex.Message}");
                return string.Empty;
            }
        }   

        public async Task<Reply> ContinueConveration(List<ConversationChunk> chunks)
        {       
            var prompt = string.Empty;     
            using Stream templateStream = await FileSystem.OpenAppPackageFileAsync("ContinueConversation.scriban-txt");
            using (StreamReader reader = new StreamReader(templateStream))
            {
                var template = Template.Parse(await reader.ReadToEndAsync());
                prompt = await template.RenderAsync(new { name = "김철수", chunks = chunks.Take(chunks.Count - 1) });

                // //Debug.WriteLine(prompt);
            }
            
            try
            {
                var response = await _client.GetResponseAsync<Reply>(prompt);
                return response.Result;
            }
            catch (Exception ex)
            {
                // Handle any exceptions that occur during the process
                Debug.WriteLine($"An error occurred StartConversation: {ex.Message}");
                return null;
            }
        }

        public async Task<int> SaveConversation(Conversation conversation)
        {
            await Init();
            await Database.InsertAsync(conversation);
            return conversation.ID;
        }

        public async Task DeleteConversation(Conversation conversation)
        {
            await Init();
            await Database.Table<ConversationChunk>().DeleteAsync(cc => cc.ConversationId == conversation.ID);
            await Database.DeleteAsync(conversation);

            
        }
    }
}
