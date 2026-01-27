using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SentenceStudio.Shared.Models;

/// <summary>
/// Result from the grading agent evaluating user's Korean input.
/// </summary>
public class GradeResult
{
    [Description("Comprehension score from 0.0 to 1.0 indicating how well the user's message was understood")]
    [JsonPropertyName("comprehension_score")]
    public double ComprehensionScore { get; set; }

    [Description("Brief notes about the user's comprehension as a single string")]
    [JsonPropertyName("comprehension_notes")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? ComprehensionNotes { get; set; }

    [Description("List of grammar corrections found in the user's input")]
    [JsonPropertyName("grammar_corrections")]
    public List<GrammarCorrectionDto> GrammarCorrections { get; set; } = new();
}

/// <summary>
/// Flexible JSON converter that handles strings, nulls, arrays, and objects gracefully.
/// </summary>
public class FlexibleStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return reader.GetString();
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.Number:
                return reader.GetDouble().ToString();
            case JsonTokenType.True:
                return "true";
            case JsonTokenType.False:
                return "false";
            case JsonTokenType.StartArray:
                // Read array and join as string
                var items = new List<string>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType == JsonTokenType.String)
                        items.Add(reader.GetString() ?? "");
                }
                return string.Join("; ", items);
            case JsonTokenType.StartObject:
                // Skip the object and return a placeholder
                reader.Skip();
                return "[complex object]";
            default:
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value == null)
            writer.WriteNullValue();
        else
            writer.WriteStringValue(value);
    }
}
