using System;

namespace SentenceStudio.Shared.Models;

public class UserProfile
{
    public int ID { get; set; }
    public string? Name { get; set; }
    public string NativeLanguage { get; set; } = "English";
    public string TargetLanguage { get; set; } = "Korean";
    public string? DisplayLanguage { get; set; }
    public string? Email { get; set; }
    public string? OpenAI_APIKey { get; set; }
    public DateTime CreatedAt { get; set; }
    public string DisplayCulture => DisplayLanguage switch
    {
        "English" => "en",
        "Korean" => "ko",
        _ => "en"
    };
}
