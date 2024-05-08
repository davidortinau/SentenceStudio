using System.Text.Json.Serialization;
using SentenceStudio.Models;

[JsonSerializable(typeof(Reply))]
[JsonSerializable(typeof(GradeResponse))]
[JsonSerializable(typeof(GrammarNotes))]
[JsonSerializable(typeof(VocabularyWord))]
[JsonSerializable(typeof(Challenge))]
[JsonSerializable(typeof(SentencesResponse))]
[JsonSerializable(typeof(SyntacticSentencesResponse))]
public partial class JsonContext : JsonSerializerContext
{
}