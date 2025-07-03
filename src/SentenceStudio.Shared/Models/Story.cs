using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;

namespace SentenceStudio.Shared.Models;

[Table("Stories")]
public class Story
{
    public int Id { get; set; }
    public int ListID {get;set;}
    public int SkillID {get;set;}
    public string? Body { get; set; }
    
    [NotMapped]
    public List<Question>? Questions { get; set; }
    
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
