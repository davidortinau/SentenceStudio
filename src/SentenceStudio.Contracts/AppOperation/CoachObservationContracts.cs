using System.ComponentModel;

namespace SentenceStudio.Contracts.AppOperation;

public sealed class CoachObservationReference
{
    [Description("The opaque server-assigned observation reference.")]
    public required string ObservationId { get; init; }

    [Description("The read capability and contract version that produced the observation.")]
    public required ApplicationCapabilityReference Capability { get; init; }

    [Description("The append-only version of this observation projection.")]
    public required int ObservationVersion { get; init; }

    [Description("The instant at which the authoritative observation was produced.")]
    public required DateTime AsOfUtc { get; init; }
}

public sealed class CoachObservationEnvelope
{
    [Description("The safe observation reference and its authoritative source metadata.")]
    public required CoachObservationReference Reference { get; init; }

    [Description("The freshness and completeness state of the safe projection.")]
    public required CoachObservationState State { get; init; }

    [Description("A typed limitation when the observation cannot support a claim.")]
    public ApplicationLimitation? Limitation { get; init; }
}
