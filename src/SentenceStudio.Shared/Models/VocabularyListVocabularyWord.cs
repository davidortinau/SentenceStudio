using SQLite;
using SQLiteNetExtensions.Attributes;

namespace SentenceStudio.Shared.Models;

public class VocabularyListVocabularyWord
{
    [ForeignKey(typeof(VocabularyList))]
    public int VocabularyListId { get; set; }

    [ForeignKey(typeof(VocabularyWord))]
    public int VocabularyWordId { get; set; }
}
