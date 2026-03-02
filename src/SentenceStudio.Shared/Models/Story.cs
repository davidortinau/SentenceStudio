using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;

namespace SentenceStudio.Shared.Models;

[Table("Stories")]
public class Story
{
    public int Id { get; set; }
    public string ListID {get;set;} = string.Empty;
    public string SkillID {get;set;} = string.Empty;
    public string? Body { get; set; }
    
    [NotMapped]
    public List<Question>? Questions { get; set; }
    
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
