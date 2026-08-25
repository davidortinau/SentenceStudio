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

    /// <summary>
    /// Whether the learner has put this skill away.
    /// </summary>
    /// <remarks>
    /// Archiving replaces deletion for skills. A skill is referenced by resources and by plan
    /// history, so removing the row would leave those references pointing at nothing while the
    /// learner believed they had merely tidied a list. An archived skill keeps its identifier and
    /// every reference to it, and is simply not offered for new practice.
    /// </remarks>
    public bool IsArchived { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }    
    public override string ToString() => Title ?? string.Empty;
}
