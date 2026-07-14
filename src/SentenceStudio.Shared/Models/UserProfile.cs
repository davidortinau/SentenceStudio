using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace SentenceStudio.Shared.Models;

[Table("UserProfiles")]
public class UserProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string? Name { get; set; }
    public string NativeLanguage { get; set; } = "English";
    
    /// <summary>
    /// Legacy single target language field. Use TargetLanguages for multi-language support.
    /// Kept for backward compatibility.
    /// </summary>
    public string TargetLanguage { get; set; } = "Korean";
    
    /// <summary>
    /// Comma-separated list of target languages the user is studying (e.g., "Korean,German,Spanish").
    /// This enables multi-language learning where each resource has its own language.
    /// </summary>
    public string? TargetLanguages { get; set; }
    
    public string? DisplayLanguage { get; set; }
    public string? Email { get; set; }
    public string? OpenAI_APIKey { get; set; }
    public int PreferredSessionMinutes { get; set; } = 20;
    public string? TargetCEFRLevel { get; set; }
    /// <summary>
    /// IANA timezone identifier (e.g. "America/Chicago", "Asia/Seoul") captured from
    /// the user's browser (webapp) or device (MAUI). Used by IPlanDateContext to resolve
    /// the user's local "today" for plan-date keying. When null, plan dates fall back to
    /// UTC — which is correct for server-created plans before the user's timezone is known,
    /// but means the day boundary aligns with UTC midnight, not the user's local midnight.
    /// The null-means-UTC fallback is intentional: it avoids hardcoding any locale
    /// (America/Chicago was explicitly rejected) and ensures deterministic behavior
    /// until the user's actual timezone is captured.
    /// </summary>
    public string? IanaTimeZoneId { get; set; }

    /// <summary>
    /// When true, the vocabulary term text is shown alongside the photo in quiz items
    /// that have an assigned/active photo. Default false hides the text so the learner
    /// must rely on the image alone.
    /// </summary>
    public bool VocabQuizShowTextWithPhoto { get; set; } = false;

    public DateTime CreatedAt { get; set; }
    
    [NotMapped]
    public string DisplayCulture => DisplayLanguage switch
    {
        "English" => "en",
        "Korean" => "ko",
        _ => "en"
    };
    
    /// <summary>
    /// Gets the list of target languages as a string array.
    /// Falls back to the legacy TargetLanguage if TargetLanguages is not set.
    /// </summary>
    [NotMapped]
    public string[] TargetLanguagesList
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(TargetLanguages))
            {
                return TargetLanguages.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
            // Fallback to legacy single language
            return string.IsNullOrWhiteSpace(TargetLanguage) 
                ? Array.Empty<string>() 
                : new[] { TargetLanguage };
        }
        set
        {
            TargetLanguages = value?.Length > 0 ? string.Join(",", value) : null;
            // Keep legacy field in sync with first language
            TargetLanguage = value?.FirstOrDefault() ?? "Korean";
        }
    }
}
