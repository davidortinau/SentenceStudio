using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.EntityFrameworkCore;

namespace SentenceStudio.Services
{
    public class ConversationService
    {
        private readonly IServiceProvider _serviceProvider;
        readonly IConfiguration configuration;
        private AiService _aiService;
        private readonly IChatClient _client;
        private readonly string _openAiApiKey;
        private ISyncService _syncService;

        public ConversationService(IServiceProvider serviceProvider, IConfiguration configuration, IChatClient chatClient)
        {
            _serviceProvider = serviceProvider;
            _aiService = serviceProvider.GetRequiredService<AiService>();
            _client = chatClient;
            this.configuration = configuration;
            _syncService = serviceProvider.GetService<ISyncService>();

            _openAiApiKey = configuration.GetRequiredSection("Settings").Get<Settings>().OpenAIKey;
        }

        public async Task<Conversation> ResumeConversation()
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            
            var mostRecentConversation = await db.Conversations
                .Include(c => c.Chunks)
                .OrderByDescending(c => c.Id)
                .FirstOrDefaultAsync();
            
            return mostRecentConversation;
        }

        public async Task SaveConversationChunk(ConversationChunk chunk)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            
            // Set timestamps
            if (chunk.CreatedAt == default)
                chunk.CreatedAt = DateTime.UtcNow;
            
            try
            {
                if (chunk.Id != 0)
                {
                    db.ConversationChunks.Update(chunk);
                }
                else
                {
                    db.ConversationChunks.Add(chunk);
                }
                
                await db.SaveChangesAsync();
                
                _syncService?.TriggerSyncAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"An error occurred SaveConversationChunk: {ex.Message}");
            }
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
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            
            // Set timestamps
            if (conversation.CreatedAt == default)
                conversation.CreatedAt = DateTime.UtcNow;
            
            try
            {
                if (conversation.Id != 0)
                {
                    db.Conversations.Update(conversation);
                }
                else
                {
                    db.Conversations.Add(conversation);
                }
                
                await db.SaveChangesAsync();
                
                _syncService?.TriggerSyncAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"An error occurred SaveConversation: {ex.Message}");
            }
            
            return conversation.Id;
        }

        public async Task DeleteConversation(Conversation conversation)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            
            try
            {
                // EF Core will handle cascade delete of chunks automatically if configured
                db.Conversations.Remove(conversation);
                await db.SaveChangesAsync();
                
                _syncService?.TriggerSyncAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"An error occurred DeleteConversation: {ex.Message}");
            }
        }

        public async Task<List<Conversation>> GetAllConversationsAsync()
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            
            return await db.Conversations
                .Include(c => c.Chunks)
                .OrderByDescending(c => c.CreatedAt)
                .ToListAsync();
        }

        public async Task<Conversation> GetConversationAsync(int id)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            
            return await db.Conversations
                .Include(c => c.Chunks)
                .Where(c => c.Id == id)
                .FirstOrDefaultAsync();
        }

        public async Task<List<ConversationChunk>> GetConversationChunksAsync(int conversationId)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            
            return await db.ConversationChunks
                .Where(cc => cc.ConversationId == conversationId)
                .OrderBy(cc => cc.CreatedAt)
                .ToListAsync();
        }
    }
}
