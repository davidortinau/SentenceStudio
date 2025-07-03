using System.Diagnostics;
using System.Text.Json;
using SentenceStudio.Shared.Models;
using Scriban;
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
    private readonly ElevenLabsSpeechService _speechService;
    private readonly LearningResourceRepository _resourceRepository;
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
        _speechService = service.GetRequiredService<ElevenLabsSpeechService>();
        _resourceRepository = service.GetRequiredService<LearningResourceRepository>();
        _skillRepository = service.GetRequiredService<SkillProfileRepository>();
        _userProfileRepository = service.GetRequiredService<UserProfileRepository>();
    }

    /// <summary>
    /// Generates sentences for shadowing practice based on learning resource and skill level.
    /// </summary>
    /// <param name="resourceId">The Id of the learning resource to use.</param>
    /// <param name="numberOfSentences">The number of sentences to generate.</param>
    /// <param name="skillID">The Id of the skill profile to use.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A list of sentences for shadowing practice.</returns>
    public async Task<List<ShadowingSentence>> GenerateSentencesAsync(
        int resourceId, 
        int numberOfSentences = 10, 
        int skillID = 0, 
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        
        // Get the learning resource with vocabulary
        LearningResource resource = await _resourceRepository.GetResourceAsync(resourceId);

        // If no resource found or no vocabulary available, return empty list
        if (resource == null || resource.Vocabulary == null || !resource.Vocabulary.Any())
            return new List<ShadowingSentence>();

        var random = new Random();
        
        // Take a random selection of vocabulary words
        _words = resource.Vocabulary.OrderBy(t => random.Next()).Take(10).ToList();
        
        // Get the user's native and target languages
        var userProfile = await _userProfileRepository.GetAsync();
        string nativeLanguage = userProfile?.NativeLanguage ?? "English";
        string targetLanguage = userProfile?.TargetLanguage ?? "Korean";
        
        var prompt = string.Empty;     
        using Stream templateStream = await FileSystem.OpenAppPackageFileAsync("GetShadowingSentences.scriban-txt");
        using (StreamReader reader = new StreamReader(templateStream))
        {
            var template = Template.Parse(await reader.ReadToEndAsync());
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
    /// Generates audio for the given text using ElevenLabs API.
    /// </summary>
    /// <param name="text">The text to convert to audio.</param>
    /// <param name="voice">The voice Id or name to use (from ElevenLabsSpeechService.VoiceOptions).</param>
    /// <param name="speed">Speech speed multiplier (0.5 to 2.0).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>An audio stream.</returns>
    public async Task<Stream> GenerateAudioAsync(
        string text, 
        string voice = "echo", 
        float speed = 1.0f, 
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        
        try
        {
            // Map from the legacy "echo" voice name if needed
            var mappedVoice = voice;
            
            // Set appropriate stability and similarity boost based on the voice and content type
            float stability = 0.5f;       // Default mid-level stability
            float similarityBoost = 0.75f; // Default high similarity to original voice
            
            // Use ElevenLabs for higher quality text-to-speech
            return await _speechService.TextToSpeechAsync(
                text: text,
                voiceId: mappedVoice,
                stability: stability,
                similarityBoost: similarityBoost,
                speed: speed,
                cancellationToken: cancellationToken
            );
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error in ElevenLabs speech generation: {ex.Message}");
            
            // Fallback to OpenAI TTS if ElevenLabs fails
            try
            {
                Debug.WriteLine("Falling back to OpenAI TTS");
                return await _aiService.TextToSpeechAsync(text, voice, speed);
            }
            catch (Exception fallbackEx)
            {
                Debug.WriteLine($"Fallback also failed: {fallbackEx.Message}");
                return null;
            }
        }
    }
}