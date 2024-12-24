using System.ComponentModel.DataAnnotations;

namespace SentenceStudio.Models;
public class VocabularyList : OfflineClientEntity
{
    [PrimaryKey, AutoIncrement]
    public int PrimaryID { get; set; }

    [Required]
    public string Name { get; set; }
    public DateTime CreatedAt { get; set; }
    
    [ManyToMany(typeof(VocabularyWord))]
    public List<VocabularyWord> Words { get; set; }

    public override string ToString()
    {
        return Name;
    }
}