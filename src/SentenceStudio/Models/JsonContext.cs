using System.Text.Json.Serialization;
using SentenceStudio.Models;

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(StorytellerResponse))]
[JsonSerializable(typeof(Story))]
[JsonSerializable(typeof(Question))]
[JsonSerializable(typeof(Reply))]
[JsonSerializable(typeof(GradeResponse))]
[JsonSerializable(typeof(GrammarNotes))]
[JsonSerializable(typeof(VocabularyWord))]
[JsonSerializable(typeof(Challenge))]
[JsonSerializable(typeof(SentencesResponse))]
[JsonSerializable(typeof(SyntacticSentencesResponse))]
[JsonSerializable(typeof(GradeResponse))]
[JsonSerializable(typeof(SentencesResponse))]
public partial class JsonContext : JsonSerializerContext
{
}