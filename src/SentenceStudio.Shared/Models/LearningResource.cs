using System;

namespace SentenceStudio.Shared.Models
{
    public class LearningResource
    {
        public int ID { get; set; }
        public string? Title { get; set; }
        public string? Description { get; set; }
        public string? MediaType { get; set; }
        public string? MediaUrl { get; set; }
        public string? Transcript { get; set; }
        public string? Translation { get; set; }
        public string? Language { get; set; }
        public int? SkillID { get; set; }
        public int? OldVocabularyListID { get; set; }
        public string? Tags { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public class ResourceVocabularyMapping
    {
        public int ID { get; set; }
        public int ResourceID { get; set; }
        public int VocabularyWordID { get; set; }
    }
}
