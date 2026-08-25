using System.Text.Json;
using System.Text.Json.Serialization;
using SentenceStudio.Contracts.LearnerMemory;

namespace SentenceStudio.Api.Coach.Memory;

/// <summary>
/// The stored shape of a memory value: a discriminated union with one branch per kind.
/// </summary>
/// <remarks>
/// Serialized by name so the JSON inside the ciphertext survives enum reordering. Only the branch
/// named by <see cref="Kind"/> is ever populated; the serializer rejects anything else rather than
/// quietly dropping it.
/// </remarks>
public sealed class CoachMemoryStoredValue
{
    /// <summary>Which branch is populated.</summary>
    public CoachMemoryKind Kind { get; set; }

    /// <summary>Normalized study goal text.</summary>
    public string? StudyGoalText { get; set; }

    /// <summary>Closed explanation depth.</summary>
    public CoachMemoryExplanationDepth? ExplanationDepth { get; set; }

    /// <summary>Closed correction timing.</summary>
    public CoachMemoryCorrectionTiming? CorrectionTiming { get; set; }

    /// <summary>Closed example register.</summary>
    public CoachMemoryExampleRegister? ExampleRegister { get; set; }

    /// <summary>
    /// The single line a learner reads and a prompt carries. Identical in both places by design:
    /// there is no separate "display" rendering that could diverge from what the model sees.
    /// </summary>
    public string DisplayText => Kind switch
    {
        CoachMemoryKind.PersistentStudyGoal => StudyGoalText ?? string.Empty,
        CoachMemoryKind.ExplanationDepth => ExplanationDepth?.ToString() ?? string.Empty,
        CoachMemoryKind.CorrectionTiming => CorrectionTiming?.ToString() ?? string.Empty,
        CoachMemoryKind.ExampleRegister => ExampleRegister?.ToString() ?? string.Empty,
        _ => string.Empty
    };

    /// <summary>Builds a study goal value from raw learner text.</summary>
    public static CoachMemoryStoredValue StudyGoal(string text) => new()
    {
        Kind = CoachMemoryKind.PersistentStudyGoal,
        StudyGoalText = CoachMemoryTextPolicy.Normalize(text)
    };

    /// <summary>Builds an explanation-depth value.</summary>
    public static CoachMemoryStoredValue Depth(CoachMemoryExplanationDepth depth) => new()
    {
        Kind = CoachMemoryKind.ExplanationDepth,
        ExplanationDepth = depth
    };

    /// <summary>Builds a correction-timing value.</summary>
    public static CoachMemoryStoredValue Timing(CoachMemoryCorrectionTiming timing) => new()
    {
        Kind = CoachMemoryKind.CorrectionTiming,
        CorrectionTiming = timing
    };

    /// <summary>Builds an example-register value.</summary>
    public static CoachMemoryStoredValue Register(CoachMemoryExampleRegister register) => new()
    {
        Kind = CoachMemoryKind.ExampleRegister,
        ExampleRegister = register
    };

    /// <summary>Maps to the public DTO.</summary>
    public CoachMemoryValueDto ToDto() => new()
    {
        Kind = Kind,
        StudyGoalText = StudyGoalText,
        ExplanationDepth = ExplanationDepth,
        CorrectionTiming = CorrectionTiming,
        ExampleRegister = ExampleRegister
    };

    /// <summary>Maps from the public DTO without validating. Callers must validate before storing.</summary>
    public static CoachMemoryStoredValue FromDto(CoachMemoryValueDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        return new CoachMemoryStoredValue
        {
            Kind = dto.Kind,
            StudyGoalText = dto.StudyGoalText is null ? null : CoachMemoryTextPolicy.Normalize(dto.StudyGoalText),
            ExplanationDepth = dto.ExplanationDepth,
            CorrectionTiming = dto.CorrectionTiming,
            ExampleRegister = dto.ExampleRegister
        };
    }
}

/// <summary>
/// Validates and serializes typed memory values.
/// </summary>
/// <remarks>
/// Every path into storage goes through <see cref="Validate"/> first. A value that has not been
/// validated is never encrypted, because a rejected value that reaches ciphertext is a value no
/// later rule can inspect.
/// </remarks>
public static class CoachMemoryValueSerializer
{
    /// <summary>Deterministic options. Enums by name; nulls dropped so an unused branch costs nothing.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        PropertyNamingPolicy = null,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Screens one value. Checks the branch matches the kind, then applies the content policy to
    /// the only branch that carries free text.
    /// </summary>
    public static CoachMemoryValueRejection Validate(CoachMemoryStoredValue? value)
    {
        if (value is null)
        {
            return CoachMemoryValueRejection.MissingValue;
        }

        if (!Enum.IsDefined(value.Kind))
        {
            return CoachMemoryValueRejection.UnsupportedKind;
        }

        var populated =
            (value.StudyGoalText is not null ? 1 : 0) +
            (value.ExplanationDepth.HasValue ? 1 : 0) +
            (value.CorrectionTiming.HasValue ? 1 : 0) +
            (value.ExampleRegister.HasValue ? 1 : 0);

        if (populated == 0)
        {
            return CoachMemoryValueRejection.MissingValue;
        }

        if (populated > 1)
        {
            return CoachMemoryValueRejection.WrongBranch;
        }

        switch (value.Kind)
        {
            case CoachMemoryKind.PersistentStudyGoal:
                if (value.StudyGoalText is null)
                {
                    return CoachMemoryValueRejection.WrongBranch;
                }

                var normalized = CoachMemoryTextPolicy.Normalize(value.StudyGoalText);
                if (!string.Equals(normalized, value.StudyGoalText, StringComparison.Ordinal))
                {
                    // Callers must hand in normalized text. Silently normalizing here would mean the
                    // bytes screened are not the bytes stored.
                    return CoachMemoryValueRejection.ControlCharacters;
                }

                return CoachMemoryTextPolicy.Screen(normalized, CoachMemoryLimits.StudyGoalMaxLength);

            case CoachMemoryKind.ExplanationDepth:
                return value.ExplanationDepth is { } depth && Enum.IsDefined(depth)
                    ? CoachMemoryValueRejection.None
                    : CoachMemoryValueRejection.WrongBranch;

            case CoachMemoryKind.CorrectionTiming:
                return value.CorrectionTiming is { } timing && Enum.IsDefined(timing)
                    ? CoachMemoryValueRejection.None
                    : CoachMemoryValueRejection.WrongBranch;

            case CoachMemoryKind.ExampleRegister:
                return value.ExampleRegister is { } register && Enum.IsDefined(register)
                    ? CoachMemoryValueRejection.None
                    : CoachMemoryValueRejection.WrongBranch;

            default:
                return CoachMemoryValueRejection.UnsupportedKind;
        }
    }

    /// <summary>Serializes a validated value. Throws when the value has not been screened.</summary>
    public static string Serialize(CoachMemoryStoredValue value)
    {
        var rejection = Validate(value);
        if (rejection != CoachMemoryValueRejection.None)
        {
            throw new InvalidOperationException($"Coach memory value rejected: {rejection}.");
        }

        return JsonSerializer.Serialize(value, Options);
    }

    /// <summary>
    /// Reads a stored value and re-screens it.
    /// </summary>
    /// <remarks>
    /// Re-screening on read is what makes the system fail closed against a row written by an older
    /// ruleset or by anything that bypassed this class. An unreadable or now-forbidden row is
    /// treated as absent, not as trusted.
    /// </remarks>
    public static bool TryDeserialize(string? json, out CoachMemoryStoredValue? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<CoachMemoryStoredValue>(json, Options);
            if (parsed is null || Validate(parsed) != CoachMemoryValueRejection.None)
            {
                return false;
            }

            value = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
