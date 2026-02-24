using ElevenLabs;
using ElevenLabs.Models;
using ElevenLabs.TextToSpeech;
using ElevenLabs.Voices;
using SentenceStudio.Models;
using Microsoft.Extensions.Logging;

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
    private readonly ILogger<ElevenLabsSpeechService> _logger;
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
    /// <param name="logger">The logger instance.</param>
    public ElevenLabsSpeechService(ElevenLabsClient client, ILogger<ElevenLabsSpeechService> logger)
    {
        _client = client;
        _logger = logger;
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
                _logger.LogInformation("Initialized {VoiceCount} voices from ElevenLabs", voices.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize ElevenLabs voices");
        }
    }

    /// <summary>
    /// Converts text to speech using ElevenLabs API and returns the audio as a stream.
    /// DEPRECATED: Use GenerateTimestampedAudioAsync for better performance and synchronization.
    /// </summary>
    /// <param name="text">The text to convert to speech.</param>
    /// <param name="voiceId">The voice Id or name to use (from VoiceOptions).</param>
    /// <param name="stability">Voice stability (0.0 to 1.0) - higher values make voice more consistent but less expressive.</param>
    /// <param name="similarityBoost">Similarity boost (0.0 to 1.0) - higher values make voice more like original but may sound metallic.</param>
    /// <param name="speed">Speech speed multiplier (0.5 to 2.0).</param>
    /// <param name="previousText">Optional previous sentence for better context and flow.</param>
    /// <param name="nextText">Optional next sentence for better context and flow.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A memory stream containing the generated audio.</returns>
    [Obsolete("Use GenerateTimestampedAudioAsync for better performance and synchronization")]
    public async Task<Stream> TextToSpeechAsync(
        string text,
        string voiceId = "echo",
        float stability = 0.75f,
        float similarityBoost = 0.75f,
        float speed = 1.0f,
        string? previousText = null,
        string? nextText = null,
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

            Console.Error.WriteLine($"🎙️ TextToSpeech: Fetching voice {voiceId}");
            _logger.LogDebug("🎙️ TextToSpeech: Fetching voice {VoiceId} for text '{Text}'", voiceId, text.Length > 50 ? text[..50] + "..." : text);

            Voice voice;
            try
            {
                // Get the voice - this works for voices in the user's account
                voice = await _client.VoicesEndpoint
                    .GetVoiceAsync(voiceId, cancellationToken: cancellationToken);
                Console.Error.WriteLine($"✅ Got voice: {voice.Name}");
            }
            catch (Exception voiceEx)
            {
                Console.Error.WriteLine($"❌ GetVoiceAsync failed for {voiceId}: {voiceEx.Message}");
                _logger.LogError(voiceEx, "❌ Voice {VoiceId} not accessible. Make sure the voice is added to your ElevenLabs account.", voiceId);
                return Stream.Null;
            }

            // Create audio generation options with latest model and context parameters
            var request = new TextToSpeechRequest(
                voice,
                text,
                voiceSettings: new VoiceSettings(stability, similarityBoost) { Speed = speed },
                model: Model.MultiLingualV2,
                previousText: previousText,
                nextText: nextText); // Using latest multilingual model

            // Generate the speech using the proper API call
            var audioBytes = await _client.TextToSpeechEndpoint.TextToSpeechAsync(
                request,
                cancellationToken: cancellationToken);


            // Create a memory stream from the audio bytes
            var audioStream = new MemoryStream(audioBytes.ClipData.ToArray());

            _logger.LogDebug("🎙️ TextToSpeech: Generated {Bytes} bytes of audio", audioBytes.ClipData.Length);

            return audioStream;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error in TextToSpeechAsync for voice {VoiceId} and text '{Text}'", voiceId, text);
            return Stream.Null;
        }
    }

    /// <summary>
    /// Generates timestamped audio for an entire learning resource with character-level synchronization.
    /// This is the preferred method for reading activities as it provides perfect audio-text sync.
    /// </summary>
    /// <param name="resource">The learning resource to generate audio for</param>
    /// <param name="voiceId">The voice Id or name to use (from VoiceOptions)</param>
    /// <param name="stability">Voice stability (0.0 to 1.0)</param>
    /// <param name="similarityBoost">Similarity boost (0.0 to 1.0)</param>
    /// <param name="speed">Speech speed multiplier (0.5 to 2.0)</param>
    /// <param name="cancellationToken">A cancellation token</param>
    /// <returns>Timestamped audio result with character-level timing data</returns>
    public async Task<TimestampedAudioResult> GenerateTimestampedAudioAsync(
        LearningResource resource,
        string voiceId = "jiyoung",  // Default to Korean voice
        float stability = 0.5f,
        float similarityBoost = 0.75f,
        float speed = 1.0f,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            // Check cache first
            var cacheKey = $"timestamped_{resource.Id}_{voiceId}_{speed:F1}";
            var cacheFilePath = Path.Combine(FileSystem.AppDataDirectory, $"{cacheKey}.mp3");
            var cacheMetaPath = Path.Combine(FileSystem.AppDataDirectory, $"{cacheKey}.json");

            if (File.Exists(cacheFilePath) && File.Exists(cacheMetaPath))
            {
                try
                {
                    var metaJson = await File.ReadAllTextAsync(cacheMetaPath, cancellationToken);
                    var cachedResult = System.Text.Json.JsonSerializer.Deserialize<TimestampedAudioResult>(metaJson);

                    if (cachedResult != null)
                    {
                        // Load audio data from file
                        var audioData = await File.ReadAllBytesAsync(cacheFilePath, cancellationToken);
                        cachedResult.AudioData = new ReadOnlyMemory<byte>(audioData);
                        cachedResult.CacheFilePath = cacheFilePath;

                        _logger.LogDebug("🏴‍☠️ Using cached timestamped audio for resource {ResourceId}", resource.Id);
                        return cachedResult;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "⚠️ Failed to load cached audio");
                    // Continue with fresh generation
                }
            }

            // Map simple voice names to actual ElevenLabs voice IDs if needed
            if (VoiceOptions.ContainsKey(voiceId))
            {
                voiceId = VoiceOptions[voiceId];
            }

            var voice = await _client.VoicesEndpoint
                .GetVoiceAsync(voiceId, cancellationToken: cancellationToken);

            // Create timestamped audio generation request
            var request = new TextToSpeechRequest(
                voice: voice,
                text: resource.Transcript,
                voiceSettings: new VoiceSettings(stability, similarityBoost) { Speed = speed },
                model: Model.MultiLingualV2,
                withTimestamps: true  // KEY: Enable character-level timestamps
            );

            _logger.LogInformation("🏴‍☠️ Generating timestamped audio for resource {ResourceId} using voice {VoiceName} ({VoiceId})", resource.Id, voice.Name, voiceId);

            // Generate the speech with timestamps
            var voiceClip = await _client.TextToSpeechEndpoint.TextToSpeechAsync(
                request,
                cancellationToken: cancellationToken);

            // Calculate total duration from timestamps
            var duration = TimeSpan.Zero;
            if (voiceClip.TimestampedTranscriptCharacters?.Length > 0)
            {
                var lastChar = voiceClip.TimestampedTranscriptCharacters[^1];
                duration = TimeSpan.FromSeconds(lastChar.EndTime);
            }

            var result = new TimestampedAudioResult
            {
                AudioData = voiceClip.ClipData,
                Characters = voiceClip.TimestampedTranscriptCharacters ?? Array.Empty<TimestampedTranscriptCharacter>(),
                Duration = duration,
                CacheFilePath = cacheFilePath
            };

            // Cache the result
            try
            {
                await File.WriteAllBytesAsync(cacheFilePath, voiceClip.ClipData.ToArray(), cancellationToken);

                // Save metadata (excluding AudioData to avoid duplication)
                var metaData = new TimestampedAudioResult
                {
                    AudioData = ReadOnlyMemory<byte>.Empty,
                    Characters = result.Characters,
                    Duration = result.Duration,
                    CacheFilePath = result.CacheFilePath
                };

                var metaJson = System.Text.Json.JsonSerializer.Serialize(metaData);
                await File.WriteAllTextAsync(cacheMetaPath, metaJson, cancellationToken);

                _logger.LogDebug("🏴‍☠️ Cached timestamped audio: {AudioSize} bytes, {CharCount} characters, {Duration:F1}s", voiceClip.ClipData.Length, result.Characters.Length, duration.TotalSeconds);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ Failed to cache timestamped audio");
                // Continue without caching
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🏴‍☠️ Error in GenerateTimestampedAudioAsync");
            throw;
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
            _logger.LogError(ex, "Error getting voices");
            return new List<Voice>();
        }
    }
}