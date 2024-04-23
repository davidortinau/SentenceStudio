using System.Text.Json.Serialization;
using SentenceStudio.Models;

[JsonSerializable(typeof(Reply))]
[JsonSerializable(typeof(GradeResponse))]
[JsonSerializable(typeof(GrammarNotes))]
[JsonSerializable(typeof(VocabWord))]
[JsonSerializable(typeof(Challenge))]
[JsonSerializable(typeof(SentencesResponse))]
public partial class JsonContext : JsonSerializerContext
{
}