using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Scriban;
using SentenceStudio.Models;

namespace SentenceStudio.Services
{
    public class TranslationService
    {
        private readonly AiService _aiService;
        private readonly VocabularyService _vocabularyService;

        public TranslationService(IServiceProvider service)
        {
            _aiService = service.GetRequiredService<AiService>();
            _vocabularyService = service.GetRequiredService<VocabularyService>();
        }

        public async Task<string> TranslateAsync(string text)
        {
            // Attempt to find an existing translation in the local database
            var existingWord = await _vocabularyService.GetWordByTargetTermAsync(text);
            if (existingWord != null && !string.IsNullOrEmpty(existingWord.NativeLanguageTerm))
            {
                return existingWord.NativeLanguageTerm;
            }

            existingWord = await _vocabularyService.GetWordByNativeTermAsync(text);   
            if (existingWord != null && !string.IsNullOrEmpty(existingWord.TargetLanguageTerm))
            {
                return existingWord.TargetLanguageTerm;
            }

            try
            {
                string prompt;
                using (Stream templateStream = await FileSystem.OpenAppPackageFileAsync("Translate.scriban-txt"))
                using (StreamReader reader = new StreamReader(templateStream))
                {
                    var template = Template.Parse(await reader.ReadToEndAsync());
                    prompt = await template.RenderAsync(new { user_input = text });
                }

                // Get translation using the AI service
                string translation = await _aiService.SendPrompt<string>(prompt);

                // Save the new translation to the local database for future use
                var vocabWord = new VocabularyWord
                {
                    NativeLanguageTerm = text,
                    TargetLanguageTerm = translation,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                };
                await _vocabularyService.SaveWordAsync(vocabWord);

                return translation;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"An error occurred during translation: {ex.Message}");
                return string.Empty;
            }
        }
    }
}
