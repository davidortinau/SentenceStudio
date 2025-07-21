using Microsoft.Extensions.AI;
using OpenAI;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using Scriban;

namespace SentenceStudio.Services;

public class ClozureService
{
    private readonly IServiceProvider _serviceProvider;
    private AiService _aiService;
    private SkillProfileRepository _skillRepository;
    private LearningResourceRepository _resourceRepository;
    private ISyncService _syncService;
    
    private List<VocabularyWord> _words;

    public List<VocabularyWord> Words {
        get {
            return _words;
        }
    }

    private readonly string _openAiApiKey;

    public ClozureService(IServiceProvider serviceProvider, IConfiguration configuration)
    {
        _serviceProvider = serviceProvider;
        _openAiApiKey = configuration.GetRequiredSection("Settings").Get<Settings>().OpenAIKey;
        _aiService = serviceProvider.GetRequiredService<AiService>();
        _skillRepository = serviceProvider.GetRequiredService<SkillProfileRepository>();
        _resourceRepository = serviceProvider.GetRequiredService<LearningResourceRepository>();
        _syncService = serviceProvider.GetService<ISyncService>();
    }

    public async Task<List<Challenge>> GetSentences(int resourceID, int numberOfSentences, int skillID)
    {
        Debug.WriteLine($"🏴‍☠️ ClozureService: GetSentences called with resourceID={resourceID}, numberOfSentences={numberOfSentences}, skillID={skillID}");
        var watch = new Stopwatch();
        watch.Start();

        if (resourceID == 0)
        {
            Debug.WriteLine("🏴‍☠️ ClozureService: Resource ID is 0 - no resource selected");
            return new List<Challenge>();
        }

        if (skillID == 0)
        {
            Debug.WriteLine("🏴‍☠️ ClozureService: Skill ID is 0 - no skill selected");
            return new List<Challenge>();
        }

        var resource = await _resourceRepository.GetResourceAsync(resourceID);
        Debug.WriteLine($"🏴‍☠️ ClozureService: Resource retrieved: {resource?.Title ?? "null"}");

        if (resource is null || resource.Vocabulary is null || !resource.Vocabulary.Any())
        {
            Debug.WriteLine($"🏴‍☠️ ClozureService: No resource or vocabulary found - returning empty list");
            return new List<Challenge>(); // Return empty list instead of null
        }

        // 🏴‍☠️ CRITICAL FIX: Send ALL vocabulary words to AI, not just a subset
        // This ensures the AI can only use words from our vocabulary list
        _words = resource.Vocabulary.ToList();
        Debug.WriteLine($"🏴‍☠️ ClozureService: Sending ALL {_words.Count} vocabulary words to AI (not just {numberOfSentences})");

        var skillProfile = await _skillRepository.GetSkillProfileAsync(skillID);
        Debug.WriteLine($"🏴‍☠️ ClozureService: Skill profile retrieved: {skillProfile?.Title ?? "null"}");
        
        var prompt = string.Empty;     
        using Stream templateStream = await FileSystem.OpenAppPackageFileAsync("GetClozuresV2.scriban-txt");
        using (StreamReader reader = new StreamReader(templateStream))
        {
            var template = Template.Parse(await reader.ReadToEndAsync());
            prompt = await template.RenderAsync(new { terms = _words, number_of_sentences = numberOfSentences, skills = skillProfile?.Description}); 
        }

        Debug.WriteLine($"🏴‍☠️ ClozureService: Prompt created, length: {prompt.Length}");
        try
        {
            IChatClient client =
            new OpenAIClient(_openAiApiKey)
                .AsChatClient(modelId: "gpt-4o-mini");

            Debug.WriteLine("🏴‍☠️ ClozureService: Sending prompt to AI service");
            var reply = await client.GetResponseAsync<ClozureResponse>(prompt);
            
            if (reply != null && reply.Result.Sentences != null)
            {
                Debug.WriteLine($"🏴‍☠️ ClozureService: AI returned {reply.Result.Sentences.Count} sentences");
                
                // 🏴‍☠️ IMPORTANT: Convert ClozureDto objects to Challenge objects and link vocabulary
                Debug.WriteLine($"🏴‍☠️ ClozureService: Converting {reply.Result.Sentences.Count} ClozureDto objects to Challenge objects");
                var challenges = new List<Challenge>();
                
                foreach (var clozureDto in reply.Result.Sentences)
                {
                    Debug.WriteLine($"🏴‍☠️ ClozureService: === Processing clozure DTO ===");
                    Debug.WriteLine($"🏴‍☠️ ClozureService: DTO.VocabularyWord: '{clozureDto.VocabularyWord}'");
                    Debug.WriteLine($"🏴‍☠️ ClozureService: DTO.VocabularyWordAsUsed: '{clozureDto.VocabularyWordAsUsed}'");
                    Debug.WriteLine($"🏴‍☠️ ClozureService: DTO.VocabularyWordGuesses: '{clozureDto.VocabularyWordGuesses}'");
                    
                    // Create Challenge object from DTO - keep it simple
                    var challenge = new Challenge
                    {
                        SentenceText = clozureDto.SentenceText,
                        RecommendedTranslation = clozureDto.RecommendedTranslation,
                        VocabularyWord = clozureDto.VocabularyWord,
                        VocabularyWordAsUsed = clozureDto.VocabularyWordAsUsed,
                        VocabularyWordGuesses = clozureDto.VocabularyWordGuesses,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };
                    
                    // Try to find matching vocabulary word for progress tracking
                    var matchingWord = _words.FirstOrDefault(w => 
                        string.Equals(w.TargetLanguageTerm, clozureDto.VocabularyWord, StringComparison.OrdinalIgnoreCase));
                    
                    if (matchingWord != null)
                    {
                        challenge.Vocabulary = new List<VocabularyWord> { matchingWord };
                        Debug.WriteLine($"🏴‍☠️ ClozureService: ✅ Linked to vocabulary word ID {matchingWord.Id} ('{matchingWord.TargetLanguageTerm}')");
                    }
                    else
                    {
                        Debug.WriteLine($"🏴‍☠️ ClozureService: ⚠️ Could not find vocabulary word '{clozureDto.VocabularyWord}' in lesson vocabulary");
                        challenge.Vocabulary = new List<VocabularyWord>();
                    }
                    
                    challenges.Add(challenge);
                    Debug.WriteLine($"🏴‍☠️ ClozureService: ✅ Added clozure challenge");
                }
                
                return challenges;
            }
            else
            {
                Debug.WriteLine("🏴‍☠️ ClozureService: Reply or Sentences is null");
                return new List<Challenge>();
            }
        }
        catch (Exception ex)
        {
            // Handle any exceptions that occur during the process
            Debug.WriteLine($"🏴‍☠️ ClozureService: An error occurred GetChallenges: {ex.Message}");
            Debug.WriteLine($"🏴‍☠️ ClozureService: Stack trace: {ex.StackTrace}");
            return new List<Challenge>();
        }
    }

    public async Task<int> SaveChallenges(Challenge item)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        // Set timestamps
        if (item.CreatedAt == default)
            item.CreatedAt = DateTime.UtcNow;
            
        item.UpdatedAt = DateTime.UtcNow;
        
        try
        {
            if (item.Id != 0)
            {
                db.Challenges.Update(item);
            }
            else
            {
                db.Challenges.Add(item);
            }
            
            await db.SaveChangesAsync();
            
            _syncService?.TriggerSyncAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"An error occurred SaveChallenges: {ex.Message}");
        }
        return item.Id;
    }

    public async Task<int> SaveGrade(GradeResponse item)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        // Set timestamps
        if (item.CreatedAt == default)
            item.CreatedAt = DateTime.UtcNow;
        
        try
        {
            if (item.Id != 0)
            {
                db.GradeResponses.Update(item);
            }
            else
            {
                db.GradeResponses.Add(item);
            }
            
            await db.SaveChangesAsync();
            
            _syncService?.TriggerSyncAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"An error occurred SaveGrade: {ex.Message}");
        }
        
        return item.Id;
    }

    public async Task<List<Challenge>> GetChallengesAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Challenges.ToListAsync();
    }

    public async Task<Challenge> GetChallengeAsync(int id)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Challenges.Where(c => c.Id == id).FirstOrDefaultAsync();
    }

    public async Task<List<GradeResponse>> GetGradeResponsesAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.GradeResponses.ToListAsync();
    }

    public async Task<GradeResponse> GetGradeResponseAsync(int id)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.GradeResponses.Where(gr => gr.Id == id).FirstOrDefaultAsync();
    }

    public async Task<List<GradeResponse>> GetGradeResponsesForChallengeAsync(int challengeId)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.GradeResponses.Where(gr => gr.ChallengeID == challengeId).ToListAsync();
    }

    public async Task<GradeResponse> GradeTranslation(string userInput, string originalSentence, string recommendedTranslation)
    {
        try
        {
            var prompt = string.Empty;     
            using Stream templateStream = await FileSystem.OpenAppPackageFileAsync("GradeTranslation.scriban-txt");
            using (StreamReader reader = new StreamReader(templateStream))
            {
                var template = Template.Parse(await reader.ReadToEndAsync());
                prompt = await template.RenderAsync(new { original_sentence = originalSentence, recommended_translation = recommendedTranslation, user_input = userInput});

                //Debug.WriteLine(prompt);
            }

            var response = await _aiService.SendPrompt<GradeResponse>(prompt);
            return response;
        }
        catch (Exception ex)
        {
            // Handle any exceptions that occur during the process
            Debug.WriteLine($"An error occurred GradeTranslation: {ex.Message}");
            return new GradeResponse();
        }
    }

    public async Task<string> Translate(string userInput)
    {
        try
        {
            var prompt = string.Empty;     
            using Stream templateStream = await FileSystem.OpenAppPackageFileAsync("Translate.scriban-txt");
            using (StreamReader reader = new StreamReader(templateStream))
            {
                var template = Template.Parse(await reader.ReadToEndAsync());
                prompt = await template.RenderAsync(new { user_input = userInput});

                //Debug.WriteLine(prompt);
            }

            var response = await _aiService.SendPrompt<string>(prompt);
            return response;
        }catch(Exception ex){
            Debug.WriteLine($"An error occurred Translate: {ex.Message}");
            return string.Empty;
        }
    }

    public async Task<GradeResponse> GradeSentence(string userInput, string userMeaning)
    {
        try
        {
            var prompt = string.Empty;     
            using Stream templateStream = await FileSystem.OpenAppPackageFileAsync("GradeSentence.scriban-txt");
            using (StreamReader reader = new StreamReader(templateStream))
            {
                var template = Template.Parse(await reader.ReadToEndAsync());
                prompt = await template.RenderAsync(new { user_input = userInput, user_meaning = userMeaning});

                //Debug.WriteLine(prompt);
            }

            var response = await _aiService.SendPrompt<GradeResponse>(prompt);
            return response;
        }
        catch (Exception ex)
        {
            // Handle any exceptions that occur during the process
            Debug.WriteLine($"An error occurred GradeTranslation: {ex.Message}");
            return new GradeResponse();
        }
    }
    
    public async Task<GradeResponse> GradeDescription(string myDescription, string aiDescription)
    {
        try
        {
            var prompt = string.Empty;     
            using Stream templateStream = await FileSystem.OpenAppPackageFileAsync("GradeMyDescription.scriban-txt");
            using (StreamReader reader = new StreamReader(templateStream))
            {
                var template = Template.Parse(await reader.ReadToEndAsync());
                prompt = await template.RenderAsync(new { my_description = myDescription, ai_description = aiDescription});

                //Debug.WriteLine(prompt);
            }

            var response = await _aiService.SendPrompt<GradeResponse>(prompt);
            return response;
        }
        catch (Exception ex)
        {
            // Handle any exceptions that occur during the process
            Debug.WriteLine($"An error occurred GradeTranslation: {ex.Message}");
            return new GradeResponse();
        }
    }   
}