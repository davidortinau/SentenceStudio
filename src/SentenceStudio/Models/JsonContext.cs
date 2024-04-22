using System.Text.Json.Serialization;
using SentenceStudio.Models;

[JsonSerializable(typeof(Reply))]
public partial class JsonContext : JsonSerializerContext
{
}