namespace SentenceStudio.Services
{
    public class StorytellerService
    {
        private AiService _aiService;
        private LearningResourceRepository _resourceRepo;
        private SkillProfileRepository _skillRepository;
        private StoryRepository _storyRepository;
        private readonly IServiceProvider _serviceProvider;
        private readonly ISyncService? _syncService;
        
        private List<VocabularyWord> _words;

        public List<VocabularyWord> Words {
            get {
                return _words;
            }
        }

        public StorytellerService(IServiceProvider service, ISyncService? syncService = null)
        {
            _aiService = service.GetRequiredService<AiService>();
            _resourceRepo = service.GetRequiredService<LearningResourceRepository>();
            _skillRepository = service.GetRequiredService<SkillProfileRepository>();
            _storyRepository = service.GetRequiredService<StoryRepository>();
            _serviceProvider = service;
            _syncService = syncService;
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
    }
}
