using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Scriban;
using SentenceStudio.Abstractions;

namespace SentenceStudio.Services;

/// <summary>
/// AI service powering the Diary activity: generates per-day writing prompts and
/// optional on-demand feedback (recommended rewrite + grammar/style notes + strengths).
/// Does NOT touch vocabulary progress / mastery scoring.
/// </summary>
public class DiaryService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly AiService _aiService;
    private readonly IFileSystemService _fileSystem;
    private readonly ILogger<DiaryService> _logger;

    public DiaryService(IServiceProvider serviceProvider, AiService aiService, IFileSystemService fileSystem, ILogger<DiaryService> logger)
    {
        _serviceProvider = serviceProvider;
        _aiService = aiService;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    public async Task<DiaryPromptResponse?> GeneratePromptAsync(string? targetLanguage = null, string? nativeLanguage = null)
    {
        try
        {
            if (string.IsNullOrEmpty(targetLanguage) || string.IsNullOrEmpty(nativeLanguage))
            {
                var userProfileRepo = _serviceProvider.GetRequiredService<UserProfileRepository>();
                var userProfile = await userProfileRepo.GetAsync();
                targetLanguage ??= userProfile?.TargetLanguage ?? "Korean";
                nativeLanguage ??= userProfile?.NativeLanguage ?? "English";
            }

            string prompt;
            using (Stream templateStream = await _fileSystem.OpenAppPackageFileAsync("GenerateDiaryPrompt.scriban-txt"))
            using (var reader = new StreamReader(templateStream))
            {
                var template = Template.Parse(await reader.ReadToEndAsync());
                prompt = await template.RenderAsync(new
                {
                    target_language = targetLanguage,
                    native_language = nativeLanguage
                });
            }

            return await _aiService.SendPrompt<DiaryPromptResponse>(prompt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating diary prompt");
            return null;
        }
    }

    public async Task<DiaryFeedbackResponse?> GetFeedbackAsync(string entryContent, string? promptText = null, string? targetLanguage = null, string? nativeLanguage = null)
    {
        if (string.IsNullOrWhiteSpace(entryContent)) return null;

        try
        {
            if (string.IsNullOrEmpty(targetLanguage) || string.IsNullOrEmpty(nativeLanguage))
            {
                var userProfileRepo = _serviceProvider.GetRequiredService<UserProfileRepository>();
                var userProfile = await userProfileRepo.GetAsync();
                targetLanguage ??= userProfile?.TargetLanguage ?? "Korean";
                nativeLanguage ??= userProfile?.NativeLanguage ?? "English";
            }

            string prompt;
            using (Stream templateStream = await _fileSystem.OpenAppPackageFileAsync("GradeDiaryEntry.scriban-txt"))
            using (var reader = new StreamReader(templateStream))
            {
                var template = Template.Parse(await reader.ReadToEndAsync());
                prompt = await template.RenderAsync(new
                {
                    target_language = targetLanguage,
                    native_language = nativeLanguage,
                    prompt_text = promptText ?? string.Empty,
                    entry_content = entryContent
                });
            }

            return await _aiService.SendPrompt<DiaryFeedbackResponse>(prompt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating diary feedback");
            return null;
        }
    }
}

public class DiaryPromptResponse
{
    [Description("A short, open-ended diary writing prompt in the learner's target language. One or two sentences, max ~20 target-language words.")]
    public string Prompt { get; set; } = string.Empty;

    [Description("A brief one-line hint in the learner's native language explaining what the prompt is asking, for lower-level learners.")]
    public string Hint { get; set; } = string.Empty;
}

public class DiaryFeedbackResponse
{
    [Description("The learner's diary entry rewritten in clear, natural target-language prose. Preserves the learner's meaning and voice. Do not add content the learner did not express.")]
    public string Recommended { get; set; } = string.Empty;

    [Description("Grammar, vocabulary, and style observations written in the learner's native language. 2-4 short bullet-style sentences focused on the most useful corrections.")]
    public string Notes { get; set; } = string.Empty;

    [Description("1-2 short sentences in the learner's native language pointing out what the learner did well. Be specific and genuine.")]
    public string Strengths { get; set; } = string.Empty;
}
