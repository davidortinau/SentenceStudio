using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace SentenceStudio.Shared.Models;

[Table("UserProfiles")]
public class UserProfile
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string NativeLanguage { get; set; } = "English";
    public string TargetLanguage { get; set; } = "Korean";
    public string? DisplayLanguage { get; set; }
    public string? Email { get; set; }
    public string? OpenAI_APIKey { get; set; }
    public bool VocabularyProgressiveReveal { get; set; } = false;
    public DateTime CreatedAt { get; set; }
    
    [NotMapped]
    public string DisplayCulture => DisplayLanguage switch
    {
        "English" => "en",
        "Korean" => "ko",
        _ => "en"
    };
}
