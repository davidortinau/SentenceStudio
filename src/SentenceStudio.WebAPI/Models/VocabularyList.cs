using System.ComponentModel.DataAnnotations;
using CommunityToolkit.Datasync.Server.EntityFrameworkCore;

namespace SentenceStudio.WebAPI.Models;

public class VocabularyList : EntityTableData
{
    [Required]
    public string Name { get; set; }
    
    public List<VocabularyWord> Words { get; set; } = new();
}
