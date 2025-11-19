using System.ComponentModel;

namespace SentenceStudio.Shared.Models.DailyPlanGeneration;

public class DailyPlanRequest
{
    [Description("User's preferred session length in minutes (5-45 range). LLM should target this duration ±5 minutes.")]
    public int PreferredSessionMinutes { get; set; } = 20;
    
    [Description("User's target CEFR level (A1, A2, B1, B2, C1, C2) or 'Not Set'")]
    public string TargetLevel { get; set; } = "Not Set";
    
    [Description("User's native language")]
    public string NativeLanguage { get; set; } = "English";
    
    [Description("User's target learning language")]
    public string TargetLanguage { get; set; } = "Korean";
    
    [Description("Number of vocabulary words due for spaced repetition review today")]
    public int VocabularyDueCount { get; set; }
    
    [Description("Summary of learning activities completed in last 14 days")]
    public List<ActivitySummary> RecentHistory { get; set; } = new();
    
    [Description("All available learning resources with metadata")]
    public List<ResourceOption> AvailableResources { get; set; } = new();
    
    [Description("All available skill areas for practice")]
    public List<SkillOption> AvailableSkills { get; set; } = new();
}

public class ActivitySummary
{
    [Description("Date activity was completed")]
    public DateTime Date { get; set; }
    
    [Description("Type: VocabularyReview, Reading, Listening, Shadowing, Cloze, Translation, VocabularyGame")]
    public string ActivityType { get; set; } = string.Empty;
    
    [Description("ID of resource used, if applicable")]
    public int? ResourceId { get; set; }
    
    [Description("Title of resource used, if applicable")]
    public string? ResourceTitle { get; set; }
    
    [Description("ID of skill practiced, if applicable")]
    public int? SkillId { get; set; }
    
    [Description("Name of skill practiced, if applicable")]
    public string? SkillName { get; set; }
    
    [Description("Minutes user spent on activity")]
    public int MinutesSpent { get; set; }
}

public class ResourceOption
{
    [Description("Unique identifier for this resource")]
    public int Id { get; set; }
    
    [Description("Display title of the resource")]
    public string Title { get; set; } = string.Empty;
    
    [Description("Type of media: Podcast, Video, Text, Vocabulary List")]
    public string MediaType { get; set; } = string.Empty;
    
    [Description("Language of the content")]
    public string Language { get; set; } = string.Empty;
    
    [Description("Approximate number of vocabulary words in this resource")]
    public int WordCount { get; set; }
}

public class SkillOption
{
    [Description("Unique identifier for this skill")]
    public int Id { get; set; }
    
    [Description("Name of the skill area")]
    public string Title { get; set; } = string.Empty;
    
    [Description("Description of what this skill focuses on")]
    public string Description { get; set; } = string.Empty;
}
