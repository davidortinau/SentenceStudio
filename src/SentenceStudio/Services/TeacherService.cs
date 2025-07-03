using System.Diagnostics;
using System.Text.Json;
using SentenceStudio.Shared.Models;
using Scriban;
using SentenceStudio.Common;
using SentenceStudio.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace SentenceStudio.Services
{
    public class TeacherService
    {
        private AiService _aiService;
        private SkillProfileRepository _skillRepository;
        private LearningResourceRepository _resourceRepository;
        private readonly IServiceProvider _serviceProvider;
        private readonly ISyncService? _syncService;
        
        private List<VocabularyWord> _words;

        public List<VocabularyWord> Words {
            get {
                return _words;
            }
        }

        public TeacherService(IServiceProvider service, ISyncService? syncService = null)
        {
            _aiService = service.GetRequiredService<AiService>();
            _skillRepository = service.GetRequiredService<SkillProfileRepository>();
            _resourceRepository = service.GetRequiredService<LearningResourceRepository>();
            _serviceProvider = service;
            _syncService = syncService;
        }

        public async Task<List<Challenge>> GetChallenges(int resourceID, int numberOfSentences, int skillProfileID)
        {
            var watch = new Stopwatch();
            watch.Start();

            var resource = await _resourceRepository.GetResourceAsync(resourceID);

            if (resource is null || resource.Vocabulary is null || !resource.Vocabulary.Any())
                return null;

            var random = new Random();
            
            _words = resource.Vocabulary.OrderBy(t => random.Next()).Take(numberOfSentences).ToList();

            var skillProfile = await _skillRepository.GetSkillProfileAsync(skillProfileID);
            
            var prompt = string.Empty;     
            using Stream templateStream = await FileSystem.OpenAppPackageFileAsync("GetChallenges.scriban-txt");
            using (StreamReader reader = new StreamReader(templateStream))
            {
                var template = Template.Parse(await reader.ReadToEndAsync());
                prompt = await template.RenderAsync(new { terms = _words, number_of_sentences = numberOfSentences, skills = skillProfile?.Description });
            }

            // //Debug.WriteLine(prompt);
            try
            {
                var response = await _aiService.SendPrompt<SentencesResponse>(prompt);
                watch.Stop();
                Debug.WriteLine($"Received response in: {watch.Elapsed}");
                
                if (response != null && response.Sentences != null)
                {
                    return response.Sentences;
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

        public async Task<int> SaveChallenges(Challenge item)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            
            try
            {
                db.Challenges.Add(item);
                await db.SaveChangesAsync();
                
                _syncService?.TriggerSyncAsync().ConfigureAwait(false);
                
                return item.Id;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"An error occurred SaveChallenges: {ex.Message}");
                return -1;
            }
        }

        public async Task<int> SaveGrade(GradeResponse item)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            
            try
            {
                db.GradeResponses.Add(item);
                await db.SaveChangesAsync();
                
                _syncService?.TriggerSyncAsync().ConfigureAwait(false);
                
                return item.Id;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"An error occurred SaveGrade: {ex.Message}");
                return -1;
            }
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

                    // //Debug.WriteLine(prompt);
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

                    // //Debug.WriteLine(prompt);
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

                    // //Debug.WriteLine(prompt);
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

                    // //Debug.WriteLine(prompt);
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

    

    

    
}
