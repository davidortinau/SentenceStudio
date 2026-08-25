using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Services
{
    public class ConversationService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly AiService _aiService;
        private readonly IFileSystemService _fileSystem;
        private readonly ILogger<ConversationService> _logger;

        // Default persona name - could be made configurable
        private const string DefaultPersonaName = "김철수";

        public ConversationService(IServiceProvider serviceProvider, ILogger<ConversationService> logger)
        {
            _serviceProvider = serviceProvider;
            _aiService = serviceProvider.GetRequiredService<AiService>();
            _fileSystem = serviceProvider.GetRequiredService<IFileSystemService>();
            _logger = logger;
        }

        /// <summary>
        /// All conversation persistence goes through the owner-scoped repository:
        /// this service never queries <c>Conversation</c> or
        /// <c>ConversationChunk</c> directly, so there is no path here that can
        /// read or write another learner's conversation.
        /// </summary>
        private ConversationRepository Repository =>
            _serviceProvider.GetRequiredService<ConversationRepository>();


        /// <summary>
        /// Loads and renders the system prompt template with persona configuration.
        /// </summary>
        private async Task<string> GetSystemPromptAsync(string personaName = DefaultPersonaName)
        {
            using Stream templateStream = await _fileSystem.OpenAppPackageFileAsync("Conversation.system.scriban-txt");
            using var reader = new StreamReader(templateStream);
            var template = Template.Parse(await reader.ReadToEndAsync());
            return await template.RenderAsync(new { name = personaName });
        }

        /// <summary>
        /// Loads and renders the scenario-specific system prompt template.
        /// </summary>
        private async Task<string> GetScenarioSystemPromptAsync(ConversationScenario scenario, string? targetLanguage = null)
        {
            // Get target language if not provided
            if (string.IsNullOrEmpty(targetLanguage))
            {
                var userProfileRepo = _serviceProvider.GetRequiredService<UserProfileRepository>();
                var userProfile = await userProfileRepo.GetAsync();
                targetLanguage = userProfile?.TargetLanguage ?? "Korean";
            }
            
            using Stream templateStream = await _fileSystem.OpenAppPackageFileAsync("Conversation.scenario.scriban-txt");
            using var reader = new StreamReader(templateStream);
            var template = Template.Parse(await reader.ReadToEndAsync());

            return await template.RenderAsync(new
            {
                scenario = new
                {
                    persona_name = scenario.PersonaName,
                    persona_description = scenario.PersonaDescription,
                    situation_description = scenario.SituationDescription,
                    conversation_type = scenario.ConversationType.ToString(),
                    question_bank = scenario.QuestionBank
                },
                target_language = targetLanguage
            });
        }

        /// <summary>
        /// The active learner's most recent conversation. Returns null when they
        /// have none, or when no user is active — never somebody else's thread.
        /// </summary>
        public async Task<Conversation?> ResumeConversation()
            => await Repository.GetMostRecentConversationAsync();

        public async Task SaveConversationChunk(ConversationChunk chunk)
            => await Repository.SaveConversationChunkAsync(chunk);

        public async Task<string> StartConversation(ConversationScenario? scenario = null, string? targetLanguage = null)
        {
            try
            {
                // Get target language if not provided
                if (string.IsNullOrEmpty(targetLanguage))
                {
                    var userProfileRepo = _serviceProvider.GetRequiredService<UserProfileRepository>();
                    var userProfile = await userProfileRepo.GetAsync();
                    targetLanguage = userProfile?.TargetLanguage ?? "Korean";
                }
                
                // Build system prompt based on scenario
                string systemPrompt;
                string userPrompt;

                if (scenario != null)
                {
                    systemPrompt = await GetScenarioSystemPromptAsync(scenario, targetLanguage);
                    _logger.LogInformation("Starting conversation with scenario: {Name}", scenario.Name);

                    // Use scenario-specific start conversation template
                    using Stream scenarioTemplateStream = await _fileSystem.OpenAppPackageFileAsync("StartConversation.scenario.scriban-txt");
                    using var scenarioReader = new StreamReader(scenarioTemplateStream);
                    var scenarioTemplate = Template.Parse(await scenarioReader.ReadToEndAsync());
                    userPrompt = await scenarioTemplate.RenderAsync(new
                    {
                        scenario = new
                        {
                            persona_name = scenario.PersonaName,
                            persona_description = scenario.PersonaDescription,
                            situation_description = scenario.SituationDescription,
                            conversation_type = scenario.ConversationType.ToString()
                        },
                        target_language = targetLanguage
                    });
                }
                else
                {
                    systemPrompt = await GetSystemPromptAsync();

                    // Use default start conversation template
                    using Stream templateStream = await _fileSystem.OpenAppPackageFileAsync("StartConversation.scriban-txt");
                    using var reader = new StreamReader(templateStream);
                    var template = Template.Parse(await reader.ReadToEndAsync());
                    userPrompt = await template.RenderAsync(new { target_language = targetLanguage });
                }

                var combinedPrompt = $"SYSTEM INSTRUCTIONS:\n{systemPrompt}\n\nTASK:\n{userPrompt}";
                var response = await _aiService.SendPrompt<string>(combinedPrompt);
                return response ?? string.Empty;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred in StartConversation");
                return string.Empty;
            }
        }

        public async Task<Reply> ContinueConversation(List<ConversationChunk> chunks, ConversationScenario? scenario = null, string? targetLanguage = null)
        {
            try
            {
                // Get target language if not provided
                if (string.IsNullOrEmpty(targetLanguage))
                {
                    var userProfileRepo = _serviceProvider.GetRequiredService<UserProfileRepository>();
                    var userProfile = await userProfileRepo.GetAsync();
                    targetLanguage = userProfile?.TargetLanguage ?? "Korean";
                }
                
                // Use the single-prompt pattern that works with structured output
                // Build a complete prompt string with persona, history, and instructions
                string prompt;

                if (scenario != null)
                {
                    // Use scenario-specific template
                    using Stream templateStream = await _fileSystem.OpenAppPackageFileAsync("ContinueConversation.scenario.scriban-txt");
                    using var reader = new StreamReader(templateStream);
                    var template = Template.Parse(await reader.ReadToEndAsync());
                    prompt = await template.RenderAsync(new
                    {
                        scenario = new
                        {
                            persona_name = scenario.PersonaName,
                            persona_description = scenario.PersonaDescription,
                            situation_description = scenario.SituationDescription,
                            conversation_type = scenario.ConversationType.ToString()
                            // question_bank = scenario.QuestionBank
                        },
                        chunks = chunks,  // Include ALL chunks including user's latest message
                        target_language = targetLanguage
                    });
                }
                else
                {
                    // Use default template (original working pattern)
                    using Stream templateStream = await _fileSystem.OpenAppPackageFileAsync("ContinueConversation.scriban-txt");
                    using var reader = new StreamReader(templateStream);
                    var template = Template.Parse(await reader.ReadToEndAsync());
                    prompt = await template.RenderAsync(new { 
                        name = DefaultPersonaName, 
                        chunks = chunks,
                        target_language = targetLanguage
                    });  // Include ALL chunks
                }

                var response = await _aiService.SendPrompt<Reply>(prompt);
                return response ?? new Reply { Message = string.Empty };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred in ContinueConversation");
                return new Reply { Message = string.Empty };
            }
        }

        /// <summary>
        /// Persists a conversation for the active learner. Returns the id, or an
        /// empty string when there is no active learner or the row belongs to
        /// someone else (including ownerless legacy rows, which are never claimed).
        /// </summary>
        public async Task<string> SaveConversation(Conversation conversation)
            => await Repository.SaveConversationAsync(conversation) ?? string.Empty;

        public async Task DeleteConversation(Conversation conversation)
        {
            if (conversation is null)
            {
                return;
            }

            await Repository.DeleteConversationAsync(conversation.Id);
        }

        public async Task<List<Conversation>> GetAllConversationsAsync()
            => await Repository.GetAllConversationsAsync();

        public async Task<Conversation?> GetConversationAsync(string id)
            => await Repository.GetConversationAsync(id);

        public async Task<List<ConversationChunk>> GetConversationChunksAsync(string conversationId)
            => await Repository.GetConversationChunksAsync(conversationId);
    }
}
