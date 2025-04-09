using System.Diagnostics;
using ElevenLabs;
using ElevenLabs.Models;
using ElevenLabs.TextToSpeech;
using ElevenLabs.Voices;

namespace SentenceStudio.Services;

/// <summary>
/// Mapping of simple voice names to ElevenLabs voice IDs.
/// </summary>
public static class Voices
{
    // Korean voices
    public const string HyunBin = "s07IwTCOrCDCaETjUVjx"; // Cool Korean male voice. Great for Professional corporate PR narration.
    public const string DoHyeon = "FQ3MuLxZh0jHcZmA5vW1"; // Older male
    public const string YohanKoo = "4JJwo477JUAx3HV0T7n7"; // Conversational - The voice of a confident, authoritative man in his 30s.
    public const string Jina = "sSoVF9lUgTGJz0Xz3J9y"; // A mid-aged Korean female voice. Works well for News broadcasting
    public const string JiYoung = "AW5wrnG1jVizOYY7R1Oo"; // A warm and clear Korean female voice with a friendly and natural tone. Suitable for narration, tutorials, and conversational content.
    public const string Jennie = "z6Kj0hecH20CdetSElRT"; // Informative and youthful, exuding professionalism with a friendly, engaging tone that captivates listeners. It's perfect for podcasts, tutorials, and content creation, delivering clarity and enthusiasm that keeps audiences connected and informed.
    public const string Yuna = "xi3rF0t7dg7uN2M0WUhr"; // Young Korean female voice with soft/cheerful voice specialized in narrative and storytelling.
    
    // English voices
    public const string Rachel = "21m00Tcm4TlvDq8ikWAM"; // Female - American (default)
    public const string Antoni = "ED0k6LqFEfpMua5GXpMG"; // Male - American
    public const string Elli = "jsCqWAovK2LkecY7zXl4"; // Female - American
    public const string Adam = "kgG8YXSrynzpPIncHKrx"; // Male - American
    public const string Dorothy = "5Q0t7uMcjvnagumLfvZi"; // Female - British
}

/// <summary>
/// Service for handling text-to-speech conversion using the ElevenLabs API.
/// </summary>
public class ElevenLabsSpeechService
{
    private readonly ElevenLabsClient _client;
    private readonly Dictionary<string, Voice> _cachedVoices = new();
    private bool _voicesInitialized = false;

    /// <summary>
    /// Mapping of simple voice names to ElevenLabs voice IDs.
    /// </summary>
    public Dictionary<string, string> VoiceOptions { get; private set; } = new()
    {
        // English voices with friendly names
        { "echo", Voices.Rachel }, // Default - English female
        { "onyx", Voices.Antoni }, // English male
        { "nova", Voices.Elli },   // English female
        { "shimmer", Voices.Adam }, // English male
        { "fable", Voices.Dorothy }, // English female - British
        
        // Korean voices
        { "yuna", Voices.Yuna },   // Korean female - young, cheerful
        { "jiyoung", Voices.JiYoung }, // Korean female - warm, clear
        { "hyunbin", Voices.HyunBin }, // Korean male - cool, professional
        { "jennie", Voices.Jennie },  // Korean female - informative, youthful
        { "jina", Voices.Jina },  // Korean female - mid-aged, news broadcaster
        { "dohyeon", Voices.DoHyeon }, // Korean male - older, mature
        { "yohankoo", Voices.YohanKoo } // Korean male - confident, authoritative
    };

    /// <summary>
    /// Gets a dictionary mapping friendly voice names to display names
    /// </summary>
    public Dictionary<string, string> VoiceDisplayNames { get; } = new()
    {
        { "yuna", "Yuna (Korean)" },
        { "jiyoung", "Ji-Young (Korean)" },
        { "hyunbin", "Hyun-Bin (Korean)" },
        { "jennie", "Jennie (Korean)" },
        { "jina", "Jina (Korean)" },
        { "dohyeon", "Do-Hyeon (Korean)" },
        { "yohankoo", "Yohan Koo (Korean)" }
    };
    public string DefaultVoiceId { get; } = Voices.HyunBin; // Default voice ID

    /// <summary>
    /// Initializes a new instance of the <see cref="ElevenLabsSpeechService"/> class.
    /// </summary>
    /// <param name="client">The ElevenLabs client instance.</param>
    public ElevenLabsSpeechService(ElevenLabsClient client)
    {
        _client = client;
    }

    /// <summary>
    /// Initializes the voice list from the API.
    /// </summary>
    public async Task InitializeVoicesAsync()
    {
        if (_voicesInitialized)
            return;
        
        try
        {
            // Get available voices from ElevenLabs API
            var voices = await _client.VoicesEndpoint.GetAllVoicesAsync();
            
            if (voices != null)
            {
                foreach (var voice in voices)
                {
                    _cachedVoices[voice.Id] = voice;
                    // Optionally populate VoiceOptions with actual voice names
                }
                
                _voicesInitialized = true;
                Debug.WriteLine($"Initialized {voices.Count} voices from ElevenLabs");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to initialize ElevenLabs voices: {ex.Message}");
        }
    }

    /// <summary>
    /// Converts text to speech using ElevenLabs API and returns the audio as a stream.
    /// </summary>
    /// <param name="text">The text to convert to speech.</param>
    /// <param name="voiceId">The voice ID or name to use (from VoiceOptions).</param>
    /// <param name="stability">Voice stability (0.0 to 1.0) - higher values make voice more consistent but less expressive.</param>
    /// <param name="similarityBoost">Similarity boost (0.0 to 1.0) - higher values make voice more like original but may sound metallic.</param>
    /// <param name="speed">Speech speed multiplier (0.5 to 2.0).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A memory stream containing the generated audio.</returns>
    public async Task<Stream> TextToSpeechAsync(
        string text,
        string voiceId = "echo",
        float stability = 0.5f,
        float similarityBoost = 0.75f,
        float speed = 1.0f,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            // Map simple voice names to actual ElevenLabs voice IDs if needed
            if (VoiceOptions.ContainsKey(voiceId))
            {
                voiceId = VoiceOptions[voiceId];
            }

            // Create the voice settings
            var voiceSettings = new VoiceSettings(
                stability: stability, 
                similarityBoost: similarityBoost);
                
            var voice = await _client.VoicesEndpoint
                .GetVoiceAsync(voiceId, cancellationToken: cancellationToken);

            // Create audio generation options
            var request = new TextToSpeechRequest(voice, text, model: Model.MultiLingualV2);//eleven_multilingual_v2

            // Generate the speech using the proper API call
            var audioBytes = await _client.TextToSpeechEndpoint.TextToSpeechAsync(
                request, 
                cancellationToken: cancellationToken);

            // Create a memory stream from the audio bytes
            var audioStream = new MemoryStream(audioBytes.ClipData.ToArray());
            
            // Debug.WriteLine($"Generated speech audio: {audioBytes.ClipData.Length} bytes using voice ID {voiceId}");
            
            return audioStream;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error in TextToSpeechAsync: {ex.Message}");
            return Stream.Null;
        }
    }

    /// <summary>
    /// Gets available voices from ElevenLabs.
    /// </summary>
    /// <returns>A list of voice models.</returns>
    public async Task<List<Voice>> GetVoicesAsync()
    {
        try
        {
            await InitializeVoicesAsync();
            return _cachedVoices.Values.ToList();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error getting voices: {ex.Message}");
            return new List<Voice>();
        }
    }
}