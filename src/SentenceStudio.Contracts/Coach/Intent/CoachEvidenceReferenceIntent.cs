using System.ComponentModel;

namespace SentenceStudio.Contracts.Coach.Intent;

/// <summary>
/// A pointer to one read-only fact the coach used.
/// The model names the kind and the window only. The application supplies the values.
/// </summary>
[Description("A pointer to one fact from a read-only tool. Add one item for each fact you used.")]
public sealed class CoachEvidenceReferenceIntent
{
    [Description("The kind of fact you used.")]
    public CoachEvidenceKind Kind { get; set; }

    [Description("The number of days in the window of the fact, for example 7, 14, or 30. Leave empty if the fact has no window.")]
    public int? WindowDays { get; set; }
}
