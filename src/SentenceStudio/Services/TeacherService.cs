using System;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Azure.AI.OpenAI;
using SentenceStudio.Models;
using Scriban;

namespace SentenceStudio.Services
{
    public class TeacherService
    {
        private AiService _aiService;
        private VocabularyService _vocabularyService;
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
                var response = await _aiService.SendPrompt<SentencesResponse>(prompt);
                return response.Sentences;
            }
            catch (Exception ex)
            {
                // Handle any exceptions that occur during the process
                Debug.WriteLine($"An error occurred GetChallenges: {ex.Message}");
                return new List<Challenge>();
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
                    var template = Template.Parse(reader.ReadToEnd());
                    prompt = await template.RenderAsync(new { original_sentence = originalSentence, recommended_translation = recommendedTranslation, user_input = userInput});

                    Debug.WriteLine(prompt);
                }

                GradeResponse response = await _aiService.SendPrompt<GradeResponse>(prompt);
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

                GradeResponse response = await _aiService.SendPrompt<GradeResponse>(prompt);
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

                GradeResponse response = await _aiService.SendPrompt<GradeResponse>(prompt);
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

    public class SentencesResponse
    {
        [JsonPropertyName("sentences")]
        public List<Challenge> Sentences { get; set; }
    }

    public class Challenge
    {
        [JsonPropertyName("sentence")]
        public string SentenceText { get; set; }

        [JsonPropertyName("recommended_translation")]
        public string RecommendedTranslation { get; set; }

        [JsonPropertyName("vocabulary")]
        public List<VocabWord> Vocabulary { get; set; }
    }

    public class VocabWord
    {
        [JsonPropertyName("original")]
        public string NativeLanguageTerm { get; set; }
        
        [JsonPropertyName("translation")]
        public string TargetLanguageTerm { get; set; }
    }

    public class GradeResponse
    {
        [JsonPropertyName("fluency_score")]
        public double Fluency { get; set; }

        [JsonPropertyName("fluency_explanation")]
        public string FluencyExplanation { get; set; }

        [JsonPropertyName("accuracy_score")]
        public double Accuracy { get; set; }

        [JsonPropertyName("accuracy_explanation")]
        public string AccuracyExplanation { get; set; }

        [JsonPropertyName("recommended_translation")]
        public string RecommendedTranslation { get; set; }

        [JsonPropertyName("grammar_notes")]
        public GrammarNotes GrammarNotes { get; set; }
    }

    public class GrammarNotes
    {
        [JsonPropertyName("original_sentence")]
        public string OriginalSentence { get; set; }

        [JsonPropertyName("recommended_translation")]
        public string RecommendedTranslation { get; set; }

        [JsonPropertyName("explanation")]
        public string Explanation { get; set; }
    }
    

    
}
