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

/// <summary>
/// Service for handling the Shadowing activity, which involves generating sentences and audio for shadowing practice.
/// </summary>
public class ShadowingService
{
    private readonly AiService _aiService;
    private readonly VocabularyService _vocabularyService;
    private readonly SkillProfileRepository _skillRepository;
    private readonly UserProfileRepository _userProfileRepository;
    private List<VocabularyWord> _words = new();

    /// <summary>
    /// Gets the vocabulary words currently loaded for shadowing practice.
    /// </summary>
    public List<VocabularyWord> Words => _words;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShadowingService"/> class.
    /// </summary>
    /// <param name="service">The service provider to resolve dependencies.</param>
    public ShadowingService(IServiceProvider service)
    {
        _aiService = service.GetRequiredService<AiService>();
        _vocabularyService = service.GetRequiredService<VocabularyService>();
        _skillRepository = service.GetRequiredService<SkillProfileRepository>();
        _userProfileRepository = service.GetRequiredService<UserProfileRepository>();
    }

    /// <summary>
    /// Generates sentences for shadowing practice based on vocabulary and skill level.
    /// </summary>
    /// <param name="vocabularyListID">The ID of the vocabulary list to use.</param>
    /// <param name="numberOfSentences">The number of sentences to generate.</param>
    /// <param name="skillID">The ID of the skill profile to use.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A list of sentences for shadowing practice.</returns>
    public async Task<List<ShadowingSentence>> GenerateSentencesAsync(
        int vocabularyListID, 
        int numberOfSentences = 10, 
        int skillID = 0, 
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        
        VocabularyList vocab = await _vocabularyService.GetListAsync(vocabularyListID);

        if (vocab is null || vocab.Words is null)
            return new List<ShadowingSentence>();

        var random = new Random();
        
        _words = vocab.Words.OrderBy(t => random.Next()).Take(10).ToList();
        
        // Get the user's native and target languages
        var userProfile = await _userProfileRepository.GetAsync();
        string nativeLanguage = userProfile?.NativeLanguage ?? "English";
        string targetLanguage = userProfile?.TargetLanguage ?? "Korean";
        
        var prompt = string.Empty;     
        using Stream templateStream = await FileSystem.OpenAppPackageFileAsync("GetShadowingSentences.scriban-txt");
        using (StreamReader reader = new StreamReader(templateStream))
        {
            var template = Template.Parse(reader.ReadToEnd());
            prompt = await template.RenderAsync(new { 
                terms = _words,
                native_language = nativeLanguage,
                target_language = targetLanguage
            });
        }
        
        try
        {
            var response = await _aiService.SendPrompt<ShadowingSentencesResponse>(prompt);
            return response?.Sentences ?? new List<ShadowingSentence>();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"An error occurred in GenerateSentencesAsync: {ex.Message}");
            return new List<ShadowingSentence>();
        }
    }

    /// <summary>
    /// Generates audio for the given text using the AI service.
    /// </summary>
    /// <param name="text">The text to convert to audio.</param>
    /// <param name="voice">The voice to use for the audio (default is "echo").</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>An audio stream.</returns>
    public async Task<Stream> GenerateAudioAsync(string text, string voice = "echo", float speed = 1.0f, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        
        try
        {
            return await _aiService.TextToSpeechAsync(text, voice, speed);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"An error occurred in GenerateAudioAsync: {ex.Message}");
            return null;
        }
    }
}