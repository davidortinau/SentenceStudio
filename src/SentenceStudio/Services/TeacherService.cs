using System.Diagnostics;
using System.Text.Json;
using SentenceStudio.Models;
using Scriban;
using SQLite;
using SentenceStudio.Common;

namespace SentenceStudio.Services
{
    public class TeacherService
    {
        private AiService _aiService;
        private VocabularyService _vocabularyService;
        private SQLiteAsyncConnection Database;
        
        private List<Term> _terms;

        public List<Term> Terms {
            get {
                return _terms;
            }
        }

        public TeacherService(IServiceProvider service)
        {
            _aiService = service.GetRequiredService<AiService>();
            _vocabularyService = service.GetRequiredService<VocabularyService>();
        }

        public async Task<List<Challenge>> GetChallenges(int vocabularyListID)
        {
            VocabularyList vocab = await _vocabularyService.GetListAsync(vocabularyListID);

            if (vocab is null || vocab.Terms is null)
                return null;

            var random = new Random();
            
            _terms = vocab.Terms.OrderBy(t => random.Next()).Take(10).ToList();
            
            var prompt = string.Empty;     
            using Stream templateStream = await FileSystem.OpenAppPackageFileAsync("GetChallenges.scriban-txt");
            using (StreamReader reader = new StreamReader(templateStream))
            {
                var template = Template.Parse(reader.ReadToEnd());
                prompt = await template.RenderAsync(new { terms = _terms });
            }

            Debug.WriteLine(prompt);
            try
            {
                string response = await _aiService.SendPrompt(prompt, true);
                var reply = JsonSerializer.Deserialize(response, JsonContext.Default.SentencesResponse);
                return reply.Sentences;
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
                await App.Current.MainPage.DisplayAlert("Error", ex.Message, "Fix it");
            }
        }

        public async Task<int> SaveChallenges(Challenge item)
        {
            await Init();
            await Database.InsertAsync(item);
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

                string response = await _aiService.SendPrompt(prompt, true);
                var reply = JsonSerializer.Deserialize(response, JsonContext.Default.GradeResponse);
                return reply;
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

                var response = await _aiService.SendPrompt(prompt);
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

                string response = await _aiService.SendPrompt(prompt, true);
                var reply = JsonSerializer.Deserialize(response, JsonContext.Default.GradeResponse);
                return reply;
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

                string response = await _aiService.SendPrompt(prompt, true);
                var reply = JsonSerializer.Deserialize(response, JsonContext.Default.GradeResponse);
                return reply;
            }
            catch (Exception ex)
            {
                // Handle any exceptions that occur during the process
                Debug.WriteLine($"An error occurred GradeTranslation: {ex.Message}");
                return new GradeResponse();
            }
        }

        
    }

    

    

    
}
