using SQLite;
using SQLiteNetExtensions.Attributes;

namespace SentenceStudio.Shared.Models;

public class VocabularyListVocabularyWord
{
    [ForeignKey(typeof(VocabularyList))]
    public string VocabularyListId { get; set; } = string.Empty;

    [ForeignKey(typeof(VocabularyWord))]
    public string VocabularyWordId { get; set; } = string.Empty;
}
