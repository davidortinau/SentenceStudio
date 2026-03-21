using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Scriban;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Services;

/// <summary>
/// Orchestrates the YouTube video import pipeline:
///   fetch transcript → AI cleanup → vocab generation → save LearningResource + VocabWords.
/// Works for both manual single-video imports and channel-polled imports.
/// </summary>
public class VideoImportPipelineService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<VideoImportPipelineService> _logger;
    private readonly YouTubeImportService _youtubeImport;
    private readonly TranscriptFormattingService _formatting;
    private readonly AiService _aiService;
    private readonly IFileSystemService _fileSystem;

    public VideoImportPipelineService(
        IServiceProvider serviceProvider,
        ILogger<VideoImportPipelineService> logger,
        YouTubeImportService youtubeImport,
        TranscriptFormattingService formatting,
        AiService aiService,
        IFileSystemService fileSystem)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _youtubeImport = youtubeImport;
        _formatting = formatting;
        _aiService = aiService;
        _fileSystem = fileSystem;
    }

    // ────────────────────────── Queries ──────────────────────────

    public async Task<List<VideoImport>> GetImportHistoryAsync(string userProfileId, int limit = 50)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.VideoImports
            .Where(vi => vi.UserProfileId == userProfileId)
            .OrderByDescending(vi => vi.CreatedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<VideoImport?> GetImportByIdAsync(string id)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.VideoImports
            .Include(vi => vi.LearningResource)
            .FirstOrDefaultAsync(vi => vi.Id == id);
    }

    // ────────────────────────── Pipeline ──────────────────────────

    /// <summary>
    /// Kick off a manual single-video import from a pasted URL.
    /// Creates the VideoImport record and runs the pipeline.
    /// </summary>
    public async Task<VideoImport> ImportFromUrlAsync(string videoUrl, string userProfileId, string? language = null)
    {
        var import = new VideoImport
        {
            UserProfileId = userProfileId,
            VideoUrl = videoUrl,
            Language = language ?? "Korean",
            CreatedAt = DateTime.UtcNow
        };

        // Persist the pending import
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.VideoImports.Add(import);
            await db.SaveChangesAsync();
        }

        // Run the pipeline (updates status in-place)
        await RunPipelineAsync(import);
        return import;
    }

    /// <summary>
    /// Run the full pipeline for an existing VideoImport record.
    /// Each stage updates status so callers can poll for progress.
    /// </summary>
    public async Task RunPipelineAsync(VideoImport import)
    {
        try
        {
            // ── Stage 1: Fetch metadata + transcript ──
            await UpdateStatusAsync(import, VideoImportStatus.FetchingTranscript);

            var metadata = await _youtubeImport.GetVideoMetadataAsync(import.VideoUrl!);
            import.VideoId = metadata.Id.Value;
            import.VideoTitle = metadata.Title;

            var tracks = await _youtubeImport.GetAvailableTranscriptsAsync(import.VideoUrl!);
            var targetTrack = PickBestTrack(tracks, import.Language);

            if (targetTrack == null)
            {
                await FailImportAsync(import, $"No {import.Language} transcript available for this video.");
                return;
            }

            var rawTranscript = await _youtubeImport.DownloadTranscriptTextAsync(targetTrack);
            import.RawTranscript = rawTranscript;
            await SaveImportAsync(import);

            // Guard: skip videos with trivially short transcripts (e.g. Shorts with <100 chars)
            const int MinTranscriptLength = 100;
            if (rawTranscript.Length < MinTranscriptLength)
            {
                await FailImportAsync(import,
                    $"Transcript too short ({rawTranscript.Length} chars). Minimum is {MinTranscriptLength} — this may be a YouTube Short.");
                return;
            }

            // ── Stage 2: Clean transcript ──
            await UpdateStatusAsync(import, VideoImportStatus.CleaningTranscript);

            var cleaned = _formatting.SmartCleanup(rawTranscript, import.Language);
            cleaned = await _formatting.PolishWithAiAsync(cleaned, import.Language);
            import.CleanedTranscript = cleaned;
            await SaveImportAsync(import);

            // ── Stage 3: Generate vocabulary ──
            await UpdateStatusAsync(import, VideoImportStatus.GeneratingVocabulary);

            var vocabWords = await ExtractVocabularyAsync(cleaned, import.Language!);

            // ── Stage 4: Save LearningResource + VocabWords ──
            await UpdateStatusAsync(import, VideoImportStatus.SavingResource);

            var resourceId = await CreateLearningResourceAsync(import, vocabWords);
            import.LearningResourceId = resourceId;

            // ── Done ──
            import.Status = VideoImportStatus.Completed;
            import.CompletedAt = DateTime.UtcNow;
            await SaveImportAsync(import);

            _logger.LogInformation(
                "Import completed: {Title} → {WordCount} vocab words",
                import.VideoTitle, vocabWords.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pipeline failed for import {Id}", import.Id);
            await FailImportAsync(import, ex.Message);
        }
    }

    // ────────────────────── Internal Helpers ──────────────────────

    private TranscriptTrack? PickBestTrack(List<TranscriptTrack> tracks, string? language)
    {
        if (tracks.Count == 0) return null;

        var lang = language?.ToLowerInvariant() ?? "korean";

        // Prefer manual (non-auto) track in target language
        var manual = tracks.FirstOrDefault(t =>
            !t.IsAutoGenerated &&
            (t.LanguageName.Contains(lang, StringComparison.OrdinalIgnoreCase) ||
             t.LanguageCode.StartsWith(GetLangCode(lang), StringComparison.OrdinalIgnoreCase)));

        if (manual != null) return manual;

        // Fall back to auto-generated in target language
        var auto = tracks.FirstOrDefault(t =>
            t.LanguageName.Contains(lang, StringComparison.OrdinalIgnoreCase) ||
            t.LanguageCode.StartsWith(GetLangCode(lang), StringComparison.OrdinalIgnoreCase));

        return auto;
    }

    private static string GetLangCode(string language) => language.ToLowerInvariant() switch
    {
        "korean" => "ko",
        "english" => "en",
        "japanese" => "ja",
        "spanish" => "es",
        "chinese" => "zh",
        "french" => "fr",
        "german" => "de",
        _ => language[..2]
    };

    /// <summary>
    /// Uses AI to extract vocabulary pairs from a transcript.
    /// </summary>
    private async Task<List<VocabularyWord>> ExtractVocabularyAsync(string transcript, string language)
    {
        // Try to get user profile for language context (may not be available in Worker)
        string nativeLanguage = "English";
        string targetLanguage = language ?? "Korean";
        
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var userProfileRepo = scope.ServiceProvider.GetRequiredService<UserProfileRepository>();
            var userProfile = await userProfileRepo.GetAsync();
            if (userProfile != null)
            {
                nativeLanguage = userProfile.NativeLanguage ?? nativeLanguage;
                targetLanguage = language ?? userProfile.TargetLanguage ?? targetLanguage;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("UserProfileRepository not available, using defaults: {Msg}", ex.Message);
        }

        // Load and render the ExtractVocabularyFromTranscript Scriban template
        var prompt = string.Empty;
        using Stream templateStream = await _fileSystem.OpenAppPackageFileAsync("ExtractVocabularyFromTranscript.scriban-txt");
        using (StreamReader reader = new StreamReader(templateStream))
        {
            var template = Template.Parse(await reader.ReadToEndAsync());
            prompt = await template.RenderAsync(new
            {
                native_language = nativeLanguage,
                target_language = targetLanguage,
                video_title = (string?)null,      // Could be passed from import if needed
                channel_name = (string?)null,     // Could be passed from import if needed
                transcript = transcript,
                existing_terms = new List<string>(), // Empty for now - could filter known words
                max_words = 30,                    // Default extraction limit
                proficiency_level = (string?)null  // Optional TOPIK filter
            });
        }

        var result = await _aiService.SendPrompt<VocabularyExtractionResponse>(prompt);

        if (result?.Vocabulary == null || !result.Vocabulary.Any())
        {
            _logger.LogWarning("AI returned no vocabulary items");
            return new List<VocabularyWord>();
        }

        // Convert extracted items to VocabularyWord using the built-in converter
        return result.Vocabulary
            .Select(item => item.ToVocabularyWord(targetLanguage))
            .ToList();
    }

    /// <summary>
    /// Creates a LearningResource from the import and links vocabulary.
    /// </summary>
    private async Task<string> CreateLearningResourceAsync(VideoImport import, List<VocabularyWord> vocabWords)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var resource = new LearningResource
        {
            Id = Guid.NewGuid().ToString(),
            Title = import.VideoTitle,
            Description = $"Imported from YouTube: {import.VideoUrl}",
            MediaType = "Video",
            MediaUrl = import.VideoUrl,
            Transcript = import.CleanedTranscript,
            Language = import.Language,
            UserProfileId = import.UserProfileId,
            Tags = "youtube,video,import",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.LearningResources.Add(resource);

        // Save or match vocab words and create mappings
        foreach (var word in vocabWords)
        {
            // Check for existing word with same target term
            var existing = await db.VocabularyWords
                .FirstOrDefaultAsync(w => w.TargetLanguageTerm == word.TargetLanguageTerm);

            var wordId = existing?.Id ?? word.Id;
            if (existing == null)
            {
                word.Language = import.Language;
                word.CreatedAt = DateTime.UtcNow;
                word.UpdatedAt = DateTime.UtcNow;
                db.VocabularyWords.Add(word);
                wordId = word.Id;
            }

            db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
            {
                Id = Guid.NewGuid().ToString(),
                ResourceId = resource.Id,
                VocabularyWordId = wordId
            });
        }

        await db.SaveChangesAsync();
        return resource.Id;
    }

    private async Task UpdateStatusAsync(VideoImport import, VideoImportStatus status)
    {
        import.Status = status;
        await SaveImportAsync(import);
        _logger.LogDebug("Import {Id} → {Status}", import.Id, status);
    }

    private async Task FailImportAsync(VideoImport import, string errorMessage)
    {
        import.Status = VideoImportStatus.Failed;
        import.ErrorMessage = errorMessage;
        import.CompletedAt = DateTime.UtcNow;
        await SaveImportAsync(import);
        _logger.LogWarning("Import {Id} failed: {Error}", import.Id, errorMessage);
    }

    private async Task SaveImportAsync(VideoImport import)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        var exists = await db.VideoImports.AnyAsync(v => v.Id == import.Id);
        if (exists)
            db.VideoImports.Update(import);
        else
            db.VideoImports.Add(import);
        
        await db.SaveChangesAsync();
    }
}
