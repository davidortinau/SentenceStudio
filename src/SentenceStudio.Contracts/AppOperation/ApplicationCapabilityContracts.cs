using System.ComponentModel;

namespace SentenceStudio.Contracts.AppOperation;

public sealed class ApplicationCapabilityIdentity
{
    [Description("The stable application capability code. It is selected from the approved capability set, never invented.")]
    public required string Code { get; init; }

    [Description("The stable capability family that owns the code.")]
    public required string Family { get; init; }

    public bool IsValid() =>
        !string.IsNullOrWhiteSpace(Code)
        && !string.IsNullOrWhiteSpace(Family);
}

public sealed class ApplicationCapabilityVersion
{
    [Description("The positive append-only contract version of the capability.")]
    public required int Value { get; init; }

    public bool IsValid() => Value > 0;
}

public sealed class ApplicationCapabilityReference
{
    [Description("The stable identity of the application capability.")]
    public required ApplicationCapabilityIdentity Capability { get; init; }

    [Description("The exact contract version selected for this invocation or result.")]
    public required ApplicationCapabilityVersion Version { get; init; }

    public bool IsValid() =>
        Capability?.IsValid() == true
        && Version?.IsValid() == true;
}

public sealed class ApplicationCapabilityDefinition
{
    [Description("The capability and contract version described by this definition.")]
    public required ApplicationCapabilityReference Capability { get; init; }

    [Description("The closed application surfaces on which this capability is available.")]
    public IReadOnlyList<ApplicationCapabilitySurface> Surfaces { get; init; } = [];

    [Description("The single trusted authority that executes this capability.")]
    public required ApplicationExecutionAuthority ExecutionAuthority { get; init; }

    [Description("The highest effect class the capability may produce.")]
    public required ApplicationEffectClass Effect { get; init; }

    [Description("The highest sensitivity of information used or produced by the capability.")]
    public required ApplicationSensitivity Sensitivity { get; init; }

    [Description("Whether this capability may be projected to a model. Missing or unknown means denied.")]
    public required ApplicationModelExposureState ModelExposure { get; init; }

    [Description("The learner confirmation required before any consequential effect.")]
    public required ApplicationConfirmationPolicy Confirmation { get; init; }

    [Description("The idempotency rule that execution must enforce.")]
    public required ApplicationIdempotencyPolicy Idempotency { get; init; }

    [Description("The synchronization state required before invocation.")]
    public required ApplicationSynchronizationPolicy Synchronization { get; init; }

    [Description("The continuation behavior permitted after this capability returns.")]
    public required ApplicationContinuationPolicy Continuation { get; init; }
}

public sealed class ApplicationCapabilityQueryRequest
{
    [Description("The approved read capability and exact contract version to invoke.")]
    public required ApplicationCapabilityReference Capability { get; init; }

    public bool IsValid() => Capability?.IsValid() == true;
}

public sealed class ApplicationCapabilityCommandRequest
{
    [Description("The approved consequential capability and exact contract version for which an inert proposal is requested.")]
    public required ApplicationCapabilityReference Capability { get; init; }

    public bool IsValid() => Capability?.IsValid() == true;
}
