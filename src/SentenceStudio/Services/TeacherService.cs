using System;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Azure.AI.OpenAI;
using SentenceStudio.Models;

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
            string t = string.Join(",", _terms.Select(t => t.TargetLanguageTerm));
            Debug.WriteLine(t);

            string prompt = "I am a student that speaks English learning Korean. ";
            prompt += "Using the following Korean vocabulary, generate for me 10 Korean sentences along with English variations. ";
            // prompt += "Use additional vocabulary as needed to make the sentences natural to native speakers. ";
            prompt += "Keep the sentences short and at a beginner Korean level using simple present, past, and future tenses. ";
            prompt += "Use 존댓말. I will read the English variation and write a Korean variation for you to grade for accuracy and fluency. ";
            prompt += "For example if you generate the Korean sentence '저는 학생이에요' because '학생' is one of the provided vocabulary words, you would generate the English sentence 'I am a student'. ";
            prompt += $"The vocabulary is: { t }";
            prompt += "Format your response as json like this: {\"sentences\": [{\"sentence\": \"I live in an apartment.\",\"recommended_translation\": \"저는 아파트에 살아요.\",\"vocabulary\": [{\"original\": \"I\", \"translation\": \"저\"},{\"original\": \"live\", \"translation\": \"살다\"},{\"original\": \"in\", \"translation\": \"\"},{\"original\": \"an\", \"translation\": \"\"},{\"original\": \"apartment\", \"translation\": \"아파트\"}]}]}";
            prompt += "In the response vocabulary include all the English words, but only the Korean translation of that word if the word is necessary for a natural Korean sentence. ";
            prompt += "Use the Korean dictionary form in the response vocabulary array. ";
            //prompt += "The sentence is the Korean sentence translated to English. The vocabulary is the words in the sentence and their translations. ";
            //prompt += "The recommended_translation is the English sentence translated to Korean. The sentence is the Korean sentence translated to English. ";
            //prompt += "The vocabulary is the words in the sentence and their translations.";
        
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

        public async Task<GradeResponse> GradeSentence(string userInput, string originalSentence, string recommendedTranslation)
        {
            try
            {
                string prompt = "I am a student that speaks English, and I'm learning Korean. ";
                prompt += $"Given this sentence: \"{originalSentence}\" you recommended this translation: \"{recommendedTranslation}\". ";
                prompt += $"This is my translation: {userInput}. ";
                prompt += "Please grade my translation for accuracy (does it mean approximately what it should and would I be understood by a native speaker, and uses the vocabulary expected in the recommended translation) and fluency (is this how a native speaker would say this using 존댓말). Use a scale of 0 to 100 where higher is better.";
                prompt += "Format your response as json with properties like this: {\"accuracy_score\": 100, \"accuracy_explanation\": \"\", \"fluency_score\": 100, \"fluency_explanation\": \"\", \"grammar_notes\": {\"original_sentence\": \"\", \"recommended_translation\": \"\", \"explanation\": \"\"}}. grammar_notes is a break down of the recommended sentence as it compares to my translation.";
                GradeResponse response = await _aiService.SendPrompt<GradeResponse>(prompt);
                return response;
            }
            catch (Exception ex)
            {
                // Handle any exceptions that occur during the process
                Debug.WriteLine($"An error occurred GradeSentence: {ex.Message}");
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
