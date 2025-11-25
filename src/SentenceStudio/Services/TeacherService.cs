using Microsoft.Extensions.Logging;

namespace SentenceStudio.Services
{
    public class TeacherService
    {
        private AiService _aiService;
        private SkillProfileRepository _skillRepository;
        private LearningResourceRepository _resourceRepository;
        private readonly IServiceProvider _serviceProvider;
        private readonly ISyncService? _syncService;
        private readonly ILogger<TeacherService> _logger;
        
        private List<VocabularyWord> _words;

        public List<VocabularyWord> Words {
            get {
                return _words;
            }
        }

        public TeacherService(IServiceProvider service, ILogger<TeacherService> logger, ISyncService? syncService = null)
        {
            _aiService = service.GetRequiredService<AiService>();
            _skillRepository = service.GetRequiredService<SkillProfileRepository>();
            _resourceRepository = service.GetRequiredService<LearningResourceRepository>();
            _serviceProvider = service;
            _syncService = syncService;
            _logger = logger;
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
                _logger.LogDebug("Received response in: {Elapsed}", watch.Elapsed);

                if (response != null && response.Sentences != null)
                {
                    return response.Sentences;
                }
                else
                {
                    _logger.LogWarning("Reply or Sentences is null");
                    return new List<Challenge>();
                }
            }
            catch (Exception ex)
            {
                // Handle any exceptions that occur during the process
                _logger.LogError(ex, "Error occurred in GetChallenges");
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
                _logger.LogError(ex, "Error occurred in SaveChallenges");
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
                _logger.LogError(ex, "Error occurred in SaveGrade");
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
                _logger.LogError(ex, "Error occurred in GradeTranslation");
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
                _logger.LogError(ex, "Error occurred in Translate");
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
                _logger.LogError(ex, "Error occurred in GradeSentence");
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
                _logger.LogError(ex, "Error occurred in GradeDescription");
                return new GradeResponse();
            }
        }


    }






}
