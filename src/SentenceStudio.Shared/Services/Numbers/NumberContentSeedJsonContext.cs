using System.Text.Json.Serialization;

namespace SentenceStudio.Services.Numbers;

/// <summary>
/// AOT-safe JSON serialization context for NumberContentSeed deserialization.
/// Required for iOS Release builds with trimming enabled.
/// </summary>
[JsonSerializable(typeof(NumberContentSeed))]
[JsonSerializable(typeof(List<NumberContextDto>))]
[JsonSerializable(typeof(List<NumberSubModeDto>))]
[JsonSerializable(typeof(List<NumberCounterDto>))]
[JsonSerializable(typeof(NumberContextDto))]
[JsonSerializable(typeof(NumberSubModeDto))]
[JsonSerializable(typeof(NumberCounterDto))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class NumberContentSeedJsonContext : JsonSerializerContext
{
}
