using System;
using System.ComponentModel.DataAnnotations;
using CommunityToolkit.Datasync.Server.EntityFrameworkCore;

namespace SentenceStudio.WebAPI.Models;

public class VocabularyWord : EntityTableData
{
    [Required]
    public string NativeLanguageTerm { get; set; }

    [Required]
    public string TargetLanguageTerm { get; set; } 

    public double Fluency { get; set; }
    public double Accuracy { get; set; }
    public List<VocabularyList> VocabularyLists { get; set; } = new();
}
