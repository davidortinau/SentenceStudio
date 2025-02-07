using System.Diagnostics;
using System.Text.Json;
using SentenceStudio.Models;
using Scriban;
using SQLite;
using SentenceStudio.Common;
using SentenceStudio.Data;
using Microsoft.Extensions.AI;
using OpenAI;
using Microsoft.Extensions.Configuration;

namespace SentenceStudio.Services;

public class ClozureService
{
    private AiService _aiService;
    private VocabularyService _vocabularyService;
    private SkillProfileRepository _skillRepository;
    private SQLiteAsyncConnection Database;
    
    private List<VocabularyWord> _words;

    public List<VocabularyWord> Words {
        get {
            return _words;
        }
    }

    private readonly string _openAiApiKey;

    public ClozureService(IServiceProvider service, IConfiguration configuration)
    {
        _openAiApiKey = configuration.GetRequiredSection("Settings").Get<Settings>().OpenAIKey;
        _aiService = service.GetRequiredService<AiService>();
        _vocabularyService = service.GetRequiredService<VocabularyService>();
        _skillRepository = service.GetRequiredService<SkillProfileRepository>();
    }

    public async Task<List<Challenge>> GetSentences(int vocabularyListID, int numberOfSentences, int skillID)
    {
        var watch = new Stopwatch();
        watch.Start();

        VocabularyList vocab = await _vocabularyService.GetListAsync(vocabularyListID);

        if (vocab is null || vocab.Words is null)
            return null;

        // List<Challenge> challenges = await Database.Table<Challenge>().Where(c => vocab.Words.Contains(c.Vocabulary)).ToListAsync();

        _words = vocab.Words.OrderBy(t => Random.Shared.Next()).Take(numberOfSentences).ToList();

        var skillProfile = await _skillRepository.GetSkillProfileAsync(skillID);
        
        var prompt = string.Empty;     
        using Stream templateStream = await FileSystem.OpenAppPackageFileAsync("GetClozures.scriban-txt");
        using (StreamReader reader = new StreamReader(templateStream))
        {
            var template = Template.Parse(reader.ReadToEnd());
            prompt = await template.RenderAsync(new { terms = _words, number_of_sentences = numberOfSentences, skills = skillProfile?.Description}); 
        }

        Debug.WriteLine(prompt);
        try
        {
            IChatClient client =
            new OpenAIClient(_openAiApiKey)
                .AsChatClient(modelId: "gpt-4o-mini");

            var reply = await client.CompleteAsync<SentencesResponse>(prompt);
            
            // string response = await _aiService.SendPrompt(prompt, true, false);
            // watch.Stop();
            // Debug.WriteLine($"Received response in: {watch.Elapsed}");
            // var reply = JsonSerializer.Deserialize(response, JsonContext.Default.SentencesResponse);
            // return reply.Sentences;
            if (reply != null && reply.Result.Sentences != null)
            {
                return reply.Result.Sentences;
            }
            else
            {
                Debug.WriteLine("Reply or Sentences is null");
                return new List<Challenge>();
            }
        }
        catch (Exception ex)
        {
            // Handle any exceptions that occur during the process
            Debug.WriteLine($"An error occurred GetChallenges: {ex.Message}");
            return new List<Challenge>();
        }
    }

    async Task Init()
    {
        if (Database is not null)
            return;

        Database = new SQLiteAsyncConnection(Constants.DatabasePath, Constants.Flags);

        CreateTablesResult result;
        
        try
        {
            result = await Database.CreateTablesAsync<Challenge, GradeResponse>();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"{ex.Message}");
            await App.Current.Windows[0].Page.DisplayAlert("Error", ex.Message, "Fix it");
        }
    }

    public async Task<int> SaveChallenges(Challenge item)
    {
        await Init();
        try{
            await Database.InsertAsync(item);
        }catch(Exception ex){
            Debug.WriteLine($"An error occurred SaveChallenges: {ex.Message}");
        }
        return item.ID;
    }

    public async Task<int> SaveGrade(GradeResponse item)
    {
        await Init();
        await Database.InsertAsync(item);
        return item.ID;
    }

    public async Task<GradeResponse> GradeTranslation(string userInput, string originalSentence, string recommendedTranslation)
    {
        try
        {
            var prompt = string.Empty;     
            using Stream templateStream = await FileSystem.OpenAppPackageFileAsync("GradeTranslation.scriban-txt");
            using (StreamReader reader = new StreamReader(templateStream))
            {
                var template = Template.Parse(reader.ReadToEnd());
                prompt = await template.RenderAsync(new { original_sentence = originalSentence, recommended_translation = recommendedTranslation, user_input = userInput});

                Debug.WriteLine(prompt);
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
                var template = Template.Parse(reader.ReadToEnd());
                prompt = await template.RenderAsync(new { user_input = userInput});

                Debug.WriteLine(prompt);
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
                var template = Template.Parse(reader.ReadToEnd());
                prompt = await template.RenderAsync(new { user_input = userInput, user_meaning = userMeaning});

                Debug.WriteLine(prompt);
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
                var template = Template.Parse(reader.ReadToEnd());
                prompt = await template.RenderAsync(new { my_description = myDescription, ai_description = aiDescription});

                Debug.WriteLine(prompt);
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