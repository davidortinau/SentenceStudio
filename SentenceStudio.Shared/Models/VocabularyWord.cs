using System;
using CoreSync.Shared;

namespace SentenceStudio.Shared.Models
{
    [SyncEntity]
    public class VocabularyWord
    {
        public int ID { get; set; }
        public string NativeLanguageTerm { get; set; }
        public string TargetLanguageTerm { get; set; }
        public string PartOfSpeech { get; set; }
        public string Notes { get; set; }
        public string ExampleSentence { get; set; }
        public string ExampleTranslation { get; set; }
        public string Tags { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}
