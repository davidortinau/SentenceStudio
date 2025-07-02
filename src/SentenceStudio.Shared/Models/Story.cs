using System;
using System.Collections.Generic;

namespace SentenceStudio.Shared.Models;

public class Story
{
    public int ID { get; set; }
    public int ListID {get;set;}
    public int SkillID {get;set;}
    public string? Body { get; set; }
    public List<Question>? Questions { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
