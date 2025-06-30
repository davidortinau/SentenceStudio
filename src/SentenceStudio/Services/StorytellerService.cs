using System.Diagnostics;
using System.Text.Json;
using SentenceStudio.Models;
using Scriban;
using SQLite;
using SentenceStudio.Common;
using SentenceStudio.Data;

namespace SentenceStudio.Services
{
    public class StorytellerService
    {
        private AiService _aiService;
        private LearningResourceRepository _resourceRepo;
        private SkillProfileRepository _skillRepository;
        private StoryRepository _storyRepository;
        private SQLiteAsyncConnection Database;
        
        private List<VocabularyWord> _words;

        public List<VocabularyWord> Words {
            get {
                return _words;
            }
        }

        public StorytellerService(IServiceProvider service)
        {
            _aiService = service.GetRequiredService<AiService>();
            _resourceRepo = service.GetRequiredService<LearningResourceRepository>();
            _skillRepository = service.GetRequiredService<SkillProfileRepository>();
            _storyRepository = service.GetRequiredService<StoryRepository>();
        }

        public async Task<Story> TellAStory(int resourceId, int numberOfWords, int skillID)
        {
            var watch = new Stopwatch();
            watch.Start();

            var resource = await _resourceRepo.GetResourceAsync(resourceId);
            if (resource is null || resource.Vocabulary is null)
                return null;

            _words = resource.Vocabulary
                .Where(w => !string.IsNullOrWhiteSpace(w.NativeLanguageTerm) && !string.IsNullOrWhiteSpace(w.TargetLanguageTerm))
                .OrderBy(_ => Guid.NewGuid())
                .Take(numberOfWords)
                .ToList();

            var skillProfile = await _skillRepository.GetSkillProfileAsync(skillID);

            var prompt = string.Empty;
            using Stream templateStream = await FileSystem.OpenAppPackageFileAsync("TellAStory.scriban-txt");
            using (StreamReader reader = new StreamReader(templateStream))
            {
                var template = Template.Parse(await reader.ReadToEndAsync());
                prompt = await template.RenderAsync(new { terms = _words, skills = skillProfile?.Description });
            }

            //Debug.WriteLine(prompt);

            try
            {
                var response = await _aiService.SendPrompt<StorytellerResponse>(prompt);
                watch.Stop();
                Debug.WriteLine($"Received response in: {watch.Elapsed}");

                response.Story.ListID = resourceId;
                response.Story.SkillID = skillID;

                await _storyRepository.SaveAsync(response.Story);

                return response.Story;
            }
            catch (Exception ex)
            {
                // Handle any exceptions that occur during the process
                Debug.WriteLine($"An error occurred TellAStory: {ex.Message}");
                return new();
            }
        }

        async Task Init()
        {
            if (Database is not null)
                return;

            Database = new SQLiteAsyncConnection(Constants.DatabasePath, Constants.Flags);

            CreateTableResult result;
            
            try
            {
                result = await Database.CreateTableAsync<Question>();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"{ex.Message}");
                await App.Current.Windows[0].Page.DisplayAlert("Error", ex.Message, "Fix it");
            }
        }

        // public async Task<int> SaveQuestions(Challenge item)
        // {
        //     await Init();
        //     try{
        //         await Database.InsertAsync(item);
        //     }catch(Exception ex){
        //         Debug.WriteLine($"An error occurred SaveChallenges: {ex.Message}");
        //     }
        //     return item.ID;
        // }

        // public async Task<int> SaveGrade(GradeResponse item)
        // {
        //     await Init();
        //     await Database.InsertAsync(item);
        //     return item.ID;
        // }

        // public async Task<GradeResponse> GradeTranslation(string userInput, string originalSentence, string recommendedTranslation)
        // {
        //     try
        //     {
        //         var prompt = string.Empty;     
        //         using Stream templateStream = await FileSystem.OpenAppPackageFileAsync("GradeTranslation.scriban-txt");
        //         using (StreamReader reader = new StreamReader(templateStream))
        //         {
        //             var template = Template.Parse(reader.ReadToEnd());
        //             prompt = await template.RenderAsync(new { original_sentence = originalSentence, recommended_translation = recommendedTranslation, user_input = userInput});

        //             //Debug.WriteLine(prompt);
        //         }

        //         string response = await _aiService.SendPrompt(prompt, true);
        //         var reply = JsonSerializer.Deserialize(response, JsonContext.Default.GradeResponse);
        //         return reply;
        //     }
        //     catch (Exception ex)
        //     {
        //         // Handle any exceptions that occur during the process
        //         Debug.WriteLine($"An error occurred GradeTranslation: {ex.Message}");
        //         return new GradeResponse();
        //     }
        // }

        // public async Task<string> Translate(string userInput)
        // {
        //     try
        //     {
        //         var prompt = string.Empty;     
        //         using Stream templateStream = await FileSystem.OpenAppPackageFileAsync("Translate.scriban-txt");
        //         using (StreamReader reader = new StreamReader(templateStream))
        //         {
        //             var template = Template.Parse(reader.ReadToEnd());
        //             prompt = await template.RenderAsync(new { user_input = userInput});

        //             //Debug.WriteLine(prompt);
        //         }

        //         var response = await _aiService.SendPrompt(prompt);
        //         return response;
        //     }catch(Exception ex){
        //         Debug.WriteLine($"An error occurred Translate: {ex.Message}");
        //         return string.Empty;
        //     }
        // }

        // public async Task<GradeResponse> GradeSentence(string userInput, string userMeaning)
        // {
        //     try
        //     {
        //         var prompt = string.Empty;     
        //         using Stream templateStream = await FileSystem.OpenAppPackageFileAsync("GradeSentence.scriban-txt");
        //         using (StreamReader reader = new StreamReader(templateStream))
        //         {
        //             var template = Template.Parse(reader.ReadToEnd());
        //             prompt = await template.RenderAsync(new { user_input = userInput, user_meaning = userMeaning});

        //             //Debug.WriteLine(prompt);
        //         }

        //         string response = await _aiService.SendPrompt(prompt, true);
        //         var reply = JsonSerializer.Deserialize(response, JsonContext.Default.GradeResponse);
        //         return reply;
        //     }
        //     catch (Exception ex)
        //     {
        //         // Handle any exceptions that occur during the process
        //         Debug.WriteLine($"An error occurred GradeTranslation: {ex.Message}");
        //         return new GradeResponse();
        //     }
        // }
        
        // public async Task<GradeResponse> GradeDescription(string myDescription, string aiDescription)
        // {
        //     try
        //     {
        //         var prompt = string.Empty;     
        //         using Stream templateStream = await FileSystem.OpenAppPackageFileAsync("GradeMyDescription.scriban-txt");
        //         using (StreamReader reader = new StreamReader(templateStream))
        //         {
        //             var template = Template.Parse(reader.ReadToEnd());
        //             prompt = await template.RenderAsync(new { my_description = myDescription, ai_description = aiDescription});

        //             //Debug.WriteLine(prompt);
        //         }

        //         string response = await _aiService.SendPrompt(prompt, true);
        //         var reply = JsonSerializer.Deserialize(response, JsonContext.Default.GradeResponse);
        //         return reply;
        //     }
        //     catch (Exception ex)
        //     {
        //         // Handle any exceptions that occur during the process
        //         Debug.WriteLine($"An error occurred GradeTranslation: {ex.Message}");
        //         return new GradeResponse();
        //     }
        // }

        
    }

    

    

    
}
