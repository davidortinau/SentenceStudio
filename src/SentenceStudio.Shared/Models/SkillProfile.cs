using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace SentenceStudio.Shared.Models;

[Table("SkillProfiles")]
public class SkillProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string? Title { get; set; }
    public string? Description {get; set;}
    public string Language {get;set;} = "Korean";
    public string? UserProfileId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }    
    public override string ToString() => Title ?? string.Empty;
}
