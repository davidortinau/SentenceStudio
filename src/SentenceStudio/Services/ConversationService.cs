using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using SentenceStudio.Common;
using SentenceStudio.Models;
using SQLite;
using Scriban;

namespace SentenceStudio.Services
{
    public class ConversationService
    {
        private SQLiteAsyncConnection Database;
        readonly IConfiguration configuration;
        private AiService _aiService;

        public ConversationService(IServiceProvider service, IConfiguration configuration)
        {
            _aiService = service.GetRequiredService<AiService>();
            this.configuration = configuration;
        }

        async Task Init()
        {
            if (Database is not null)
                return;

            Database = new SQLiteAsyncConnection(Constants.DatabasePath, Constants.Flags);

            CreateTableResult result;
            
            try
            {
                result = await Database.CreateTableAsync<ConversationChunk>();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"{ex.Message}");
                await App.Current.MainPage.DisplayAlert("Error", ex.Message, "Fix it");
            }
        }
        
        public async Task<List<ConversationChunk>> ResumeConversation()
        {
            await Init();
            var conversationChunks = await Database.Table<ConversationChunk>().ToListAsync();
            return conversationChunks;
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
                var template = Template.Parse(reader.ReadToEnd());
                prompt = await template.RenderAsync();

                Debug.WriteLine(prompt);
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

        public async Task<string> ContinueConveration(List<ConversationChunk> chunks)
        {       
            var prompt = string.Empty;     
            using Stream templateStream = await FileSystem.OpenAppPackageFileAsync("ContinueConversation.scriban-txt");
            using (StreamReader reader = new StreamReader(templateStream))
            {
                var template = Template.Parse(reader.ReadToEnd());
                prompt = await template.RenderAsync(new { name = "김철수", chunks = chunks.Take(chunks.Count - 1) });

                Debug.WriteLine(prompt);
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

        public async Task ClearConversation()
        {
            await Init();
            await Database.DeleteAllAsync<ConversationChunk>();
        }
    }
}
