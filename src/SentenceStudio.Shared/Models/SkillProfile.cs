using System;

namespace SentenceStudio.Shared.Models;

public class SkillProfile
{
    public int ID { get; set; }
    public string? Title { get; set; }
    public string? Description {get; set;}
    public string Language {get;set;} = "Korean";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }    
    public override string ToString() => Title ?? string.Empty;
}
