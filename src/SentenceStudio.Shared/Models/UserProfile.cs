using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace SentenceStudio.Shared.Models;

[Table("UserProfiles")]
public class UserProfile
{
    public int Id { get; set; }
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
