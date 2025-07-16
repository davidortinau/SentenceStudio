using Microsoft.Extensions.AI;
using OpenAI;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;

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
        using Stream templateStream = await FileSystem.OpenAppPackageFileAsync("GetClozures.scriban-txt");
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
            var reply = await client.GetResponseAsync<SentencesResponse>(prompt);
            
            if (reply != null && reply.Result.Sentences != null)
            {
                Debug.WriteLine($"🏴‍☠️ ClozureService: AI returned {reply.Result.Sentences.Count} sentences");
                
                // 🏴‍☠️ IMPORTANT: Populate Challenge objects with vocabulary for progress tracking
                Debug.WriteLine($"🏴‍☠️ ClozureService: Processing {reply.Result.Sentences.Count} challenges for vocabulary linking");
                foreach (var challenge in reply.Result.Sentences)
                {
                    Debug.WriteLine($"🏴‍☠️ ClozureService: === Processing challenge ===");
                    Debug.WriteLine($"🏴‍☠️ ClozureService: Challenge.VocabularyWord: '{challenge.VocabularyWord}'");
                    Debug.WriteLine($"🏴‍☠️ ClozureService: Challenge.VocabularyWordAsUsed: '{challenge.VocabularyWordAsUsed}'");
                    Debug.WriteLine($"🏴‍☠️ ClozureService: Available vocabulary words: {string.Join(", ", _words.Select(w => $"'{w.TargetLanguageTerm}' (ID:{w.Id})"))}");
                    
                    // Find the vocabulary word that matches this challenge
                    // Try multiple matching strategies to handle different forms
                    VocabularyWord matchingWord = null;
                    string matchStrategy = "";
                    
                    // Strategy 1: Exact match with VocabularyWord
                    matchingWord = _words.FirstOrDefault(w => 
                        string.Equals(w.TargetLanguageTerm, challenge.VocabularyWord, StringComparison.OrdinalIgnoreCase));
                    if (matchingWord != null) matchStrategy = "Strategy 1: Exact VocabularyWord match";
                    
                    // Strategy 2: Exact match with VocabularyWordAsUsed  
                    if (matchingWord == null)
                    {
                        matchingWord = _words.FirstOrDefault(w => 
                            string.Equals(w.TargetLanguageTerm, challenge.VocabularyWordAsUsed, StringComparison.OrdinalIgnoreCase));
                        if (matchingWord != null) matchStrategy = "Strategy 2: Exact VocabularyWordAsUsed match";
                    }
                    
                    // Strategy 3: VocabularyWord contains target term
                    if (matchingWord == null)
                    {
                        matchingWord = _words.FirstOrDefault(w => 
                            challenge.VocabularyWord?.Contains(w.TargetLanguageTerm, StringComparison.OrdinalIgnoreCase) == true);
                        if (matchingWord != null) matchStrategy = "Strategy 3: VocabularyWord contains target term";
                    }
                    
                    // Strategy 4: Target term contains VocabularyWord
                    if (matchingWord == null)
                    {
                        matchingWord = _words.FirstOrDefault(w => 
                            w.TargetLanguageTerm?.Contains(challenge.VocabularyWord, StringComparison.OrdinalIgnoreCase) == true);
                        if (matchingWord != null) matchStrategy = "Strategy 4: Target term contains VocabularyWord";
                    }
                    
                    // Strategy 5: Try partial matching on VocabularyWordAsUsed
                    if (matchingWord == null)
                    {
                        matchingWord = _words.FirstOrDefault(w => 
                            challenge.VocabularyWordAsUsed?.Contains(w.TargetLanguageTerm, StringComparison.OrdinalIgnoreCase) == true);
                        if (matchingWord != null) matchStrategy = "Strategy 5: VocabularyWordAsUsed contains target term";
                    }
                    
                    // Strategy 6: Try reverse partial matching on VocabularyWordAsUsed
                    if (matchingWord == null)
                    {
                        matchingWord = _words.FirstOrDefault(w => 
                            w.TargetLanguageTerm?.Contains(challenge.VocabularyWordAsUsed, StringComparison.OrdinalIgnoreCase) == true);
                        if (matchingWord != null) matchStrategy = "Strategy 6: Target term contains VocabularyWordAsUsed";
                    }
                    
                    // Strategy 7: Fallback - if AI isn't following instructions, just link to first available word
                    if (matchingWord == null && _words.Any())
                    {
                        matchingWord = _words.First();
                        matchStrategy = "Strategy 7: Fallback to first vocabulary word (AI not following instructions)";
                        Debug.WriteLine($"🏴‍☠️ ClozureService: ⚠️ FALLBACK - AI generated vocabulary word '{challenge.VocabularyWord}' not in list, using fallback");
                    }
                    
                    if (matchingWord != null)
                    {
                        // Create a list containing just this vocabulary word for the challenge
                        challenge.Vocabulary = new List<VocabularyWord> { matchingWord };
                        Debug.WriteLine($"🏴‍☠️ ClozureService: ✅ SUCCESS - Linked challenge using {matchStrategy}");
                        Debug.WriteLine($"🏴‍☠️ ClozureService: Linked to vocabulary word ID {matchingWord.Id} ('{matchingWord.TargetLanguageTerm}')");
                    }
                    else
                    {
                        Debug.WriteLine($"🏴‍☠️ ClozureService: ❌ WARNING - Could not find vocabulary word for challenge");
                        Debug.WriteLine($"🏴‍☠️ ClozureService: Challenge VocabularyWord: '{challenge.VocabularyWord}'");
                        Debug.WriteLine($"🏴‍☠️ ClozureService: Challenge VocabularyWordAsUsed: '{challenge.VocabularyWordAsUsed}'");
                        Debug.WriteLine($"🏴‍☠️ ClozureService: This will prevent vocabulary progress tracking!");
                        // Create empty list to avoid null reference
                        challenge.Vocabulary = new List<VocabularyWord>();
                    }
                    
                    Debug.WriteLine($"🏴‍☠️ ClozureService: === End challenge processing ===");
                }
                
                return reply.Result.Sentences;
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