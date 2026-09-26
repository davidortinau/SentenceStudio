using SentenceStudio.Contracts.AppOperation;

namespace SentenceStudio.Application.Capabilities;

public sealed class ApplicationCapabilityValidationException(string message) : InvalidOperationException(message);

public static class ApplicationCapabilityDescriptorValidator
{
    public const int MinimumCodeLength = 3;
    public const int MaximumCodeLength = 96;
    public const int MinimumFamilyLength = 3;
    public const int MaximumFamilyLength = 64;
    public const int MaximumDescriptionLength = 512;
    public const int MaximumExampleLength = 256;
    public const int MaximumExamplesPerKind = 8;
    public const int MaximumDeferredIdentityLength = 160;
    public const int MaximumPayloadBytes = 1_048_576;
    public const int MaximumBudgetCost = 100_000;
    public const int MaximumExecutionMilliseconds = 120_000;
    public const int Sha256FingerprintLength = 71;

    public static void Validate(IApplicationCapabilityDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        ValidateName(descriptor.Code, nameof(descriptor.Code), MinimumCodeLength, MaximumCodeLength, allowDots: true);
        ValidateName(descriptor.Family, nameof(descriptor.Family), MinimumFamilyLength, MaximumFamilyLength, allowDots: false);

        if (descriptor.MajorVersion <= 0)
        {
            Fail("MajorVersion must be positive.");
        }

        ValidateText(descriptor.SelectionDescription, nameof(descriptor.SelectionDescription), MaximumDescriptionLength);
        ValidateExamples(descriptor.PositiveExamples, nameof(descriptor.PositiveExamples));
        ValidateExamples(descriptor.NegativeExamples, nameof(descriptor.NegativeExamples));
        ValidateAliases(descriptor);
        ValidateSurfaces(descriptor);
        ValidateEnum(descriptor.ExecutionAuthority, nameof(descriptor.ExecutionAuthority), allowUnknown: false);
        ValidateEnum(descriptor.Effect, nameof(descriptor.Effect), allowUnknown: true);
        ValidateEnum(descriptor.Sensitivity, nameof(descriptor.Sensitivity), allowUnknown: true);
        ValidateEnum(descriptor.Confirmation, nameof(descriptor.Confirmation), allowUnknown: true);
        ValidateEnum(descriptor.Idempotency, nameof(descriptor.Idempotency), allowUnknown: true);
        ValidateEnum(descriptor.Synchronization, nameof(descriptor.Synchronization), allowUnknown: true);
        ValidateEnum(descriptor.Continuation, nameof(descriptor.Continuation), allowUnknown: true);
        ValidateEnum(descriptor.PolicyState, nameof(descriptor.PolicyState), allowUnknown: true);
        ValidateEnum(
            descriptor.CoachExposurePolicy,
            nameof(descriptor.CoachExposurePolicy),
            allowUnknown: true);
        ValidateEnum(descriptor.Rollout, nameof(descriptor.Rollout), allowUnknown: true);
        ValidateEnum(descriptor.Availability, nameof(descriptor.Availability), allowUnknown: true);
        ValidateEnum(descriptor.Dependencies, nameof(descriptor.Dependencies), allowUnknown: true);
        ValidateEnum(
            descriptor.ReleaseValidationStatus,
            nameof(descriptor.ReleaseValidationStatus),
            allowUnknown: true);
        ValidateFingerprint(descriptor.DescriptorFingerprint, nameof(descriptor.DescriptorFingerprint));
        ValidateQualificationCandidate(descriptor);
        ValidateIdentity(descriptor.Handler, nameof(descriptor.Handler), required: true);
        ValidateIdentity(descriptor.CoachProjector, nameof(descriptor.CoachProjector), required: false);
        ValidateBudgets(descriptor);

        ApplicationContractGraphValidator.Validate(descriptor.RequestType, "Request");
        ApplicationContractGraphValidator.ValidateProjectionSeparation(
            descriptor.ClientResultType,
            descriptor.CoachObservationType);

        ValidateMatrix(descriptor);
    }

    private static void ValidateMatrix(IApplicationCapabilityDescriptor descriptor)
    {
        var coachSurface = descriptor.Surfaces.Contains(ApplicationCapabilitySurface.Model);
        var declaredCoachCandidate =
            descriptor.CoachExposurePolicy == ApplicationCapabilityCoachExposurePolicy.Candidate;

        if (declaredCoachCandidate && !coachSurface)
        {
            Fail("A Coach exposure candidate declaration requires the Model surface.");
        }

        if (declaredCoachCandidate
            && descriptor.ClientResultType == descriptor.CoachObservationType)
        {
            Fail("A Coach exposure candidate requires an observation type distinct from the client result type.");
        }

        if (declaredCoachCandidate
            && descriptor.Sensitivity is ApplicationSensitivity.Secret or ApplicationSensitivity.Prohibited)
        {
            Fail("Secret and Prohibited capabilities cannot become Coach exposure candidates.");
        }

        if (declaredCoachCandidate && descriptor.CoachProjector is null)
        {
            Fail("A Coach exposure candidate requires an explicit Coach projector identity.");
        }

        if (declaredCoachCandidate && descriptor.QualificationCandidate is null)
        {
            Fail("A Coach exposure candidate requires static qualification candidate metadata.");
        }

        if (descriptor.ReleaseValidationStatus == ApplicationCapabilityReleaseValidationStatus.Passed
            && (descriptor.Rollout != ApplicationCapabilityRolloutState.Enabled
                || descriptor.Availability != ApplicationCapabilityAvailabilityState.Available
                || descriptor.Dependencies != ApplicationCapabilityDependencyState.Satisfied))
        {
            Fail(
                "Passed release validation requires enabled rollout, available capability, "
                + "and satisfied dependencies.");
        }

        if (declaredCoachCandidate
            && !ApplicationCapabilityStaticValidation.HasCompleteDeclaredPolicy(descriptor))
        {
            Fail("A Coach exposure candidate requires complete static policy, budgets, and release metadata.");
        }

        if (descriptor.Effect is ApplicationEffectClass.Write
            or ApplicationEffectClass.Launch
            or ApplicationEffectClass.ExternalEffect
            or ApplicationEffectClass.Composite)
        {
            if (descriptor.Confirmation is ApplicationConfirmationPolicy.Unknown or ApplicationConfirmationPolicy.None)
            {
                Fail($"{descriptor.Effect} is consequential and requires an explicit confirmation policy.");
            }

            if (descriptor.Idempotency is ApplicationIdempotencyPolicy.Unknown
                or ApplicationIdempotencyPolicy.NotApplicable)
            {
                Fail($"{descriptor.Effect} is consequential and requires an idempotency policy.");
            }
        }

        if (descriptor.Effect is ApplicationEffectClass.ExternalEffect or ApplicationEffectClass.Composite
            && descriptor.Idempotency != ApplicationIdempotencyPolicy.RequiredByKey)
        {
            Fail($"{descriptor.Effect} requires keyed idempotency.");
        }

        if (descriptor.Continuation == ApplicationContinuationPolicy.OneAutomaticResume
            && descriptor.Effect != ApplicationEffectClass.Read)
        {
            Fail("Automatic continuation is allowed only for read effects.");
        }

        if (descriptor.Continuation is ApplicationContinuationPolicy.ClarificationOnly
                or ApplicationContinuationPolicy.OneAutomaticResume
            && descriptor.Budgets is null)
        {
            Fail("A declared continuation requires an explicit bounded continuation budget.");
        }

        if (descriptor.Budgets is { MaxContinuationDepth: > 1 })
        {
            Fail("Continuation nesting is bounded to one level.");
        }

        if (descriptor.Continuation == ApplicationContinuationPolicy.None
            && descriptor.Budgets is { MaxContinuationDepth: not 0 })
        {
            Fail("A capability with no continuation must declare zero continuation depth.");
        }

        if (descriptor.Continuation is ApplicationContinuationPolicy.ClarificationOnly
            or ApplicationContinuationPolicy.OneAutomaticResume
            && descriptor.Budgets is { MaxContinuationDepth: not 1 })
        {
            Fail("A declared continuation must be bounded to exactly one level.");
        }
    }

    private static void ValidateAliases(IApplicationCapabilityDescriptor descriptor)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var alias in descriptor.Aliases)
        {
            ValidateName(alias, "Alias", MinimumCodeLength, MaximumCodeLength, allowDots: true);
            if (string.Equals(alias, descriptor.Code, StringComparison.Ordinal) || !seen.Add(alias))
            {
                Fail($"Alias '{alias}' collides with the capability code or another alias.");
            }
        }
    }

    private static void ValidateSurfaces(IApplicationCapabilityDescriptor descriptor)
    {
        var seen = new HashSet<ApplicationCapabilitySurface>();
        foreach (var surface in descriptor.Surfaces)
        {
            ValidateEnum(surface, "Surface", allowUnknown: true);
            if (!seen.Add(surface))
            {
                Fail($"Surface '{surface}' is declared more than once.");
            }
        }
    }

    private static void ValidateExamples(IReadOnlyList<string> examples, string name)
    {
        if (examples.Count == 0 || examples.Count > MaximumExamplesPerKind)
        {
            Fail($"{name} must contain between 1 and {MaximumExamplesPerKind} examples.");
        }

        foreach (var example in examples)
        {
            ValidateText(example, name, MaximumExampleLength);
        }
    }

    private static void ValidateBudgets(IApplicationCapabilityDescriptor descriptor)
    {
        if (descriptor.Budgets is not { } budgets)
        {
            return;
        }

        if (budgets.MaxRequestBytes is <= 0 or > MaximumPayloadBytes
            || budgets.MaxClientResultBytes is <= 0 or > MaximumPayloadBytes
            || budgets.MaxCoachObservationBytes is < 0 or > MaximumPayloadBytes
            || budgets.SchemaTokenCost is <= 0 or > MaximumBudgetCost
            || budgets.RiskCost is <= 0 or > MaximumBudgetCost
            || budgets.ExposureCost is <= 0 or > MaximumBudgetCost
            || budgets.MaxExecutionMilliseconds is <= 0 or > MaximumExecutionMilliseconds
            || budgets.MaxContinuationDepth is < 0 or > 1)
        {
            Fail(
                "Payload, schema-token, risk, exposure, and latency budgets must be positive and within "
                + "catalog bounds; Coach bytes may be zero and continuation depth is 0 or 1.");
        }
    }

    private static void ValidateIdentity(
        ApplicationCapabilityImplementationIdentity? identity,
        string name,
        bool required)
    {
        if (identity is null)
        {
            if (required)
            {
                Fail($"{name} identity is required.");
            }

            return;
        }

        var hasType = identity.ContractType is not null;
        var hasDeferred = identity.DeferredIdentity is not null;
        if (hasType == hasDeferred)
        {
            Fail($"{name} must contain exactly one exact contract type or deferred identity.");
        }

        if (identity.ContractType is { } contractType
            && (contractType.ContainsGenericParameters
                || contractType.IsPointer
                || contractType.IsByRef
                || !contractType.IsInterface))
        {
            Fail($"{name} exact contract type must be a closed interface.");
        }

        if (identity.DeferredIdentity is { } deferred)
        {
            ValidateName(deferred, name, MinimumCodeLength, MaximumDeferredIdentityLength, allowDots: true);
        }
    }

    private static void ValidateQualificationCandidate(IApplicationCapabilityDescriptor descriptor)
    {
        if (descriptor.QualificationCandidate is not { } candidate)
        {
            return;
        }

        ValidateFingerprint(
            candidate.DescriptorFingerprint,
            $"{nameof(descriptor.QualificationCandidate)}.{nameof(candidate.DescriptorFingerprint)}");
        ValidateFingerprint(
            candidate.QualificationReportFingerprint,
            $"{nameof(descriptor.QualificationCandidate)}.{nameof(candidate.QualificationReportFingerprint)}");
        ValidateIdentity(
            ApplicationCapabilityImplementationIdentity.ExactContract(candidate.HandlerContractType),
            $"{nameof(descriptor.QualificationCandidate)}.{nameof(candidate.HandlerContractType)}",
            required: true);
        ValidateIdentity(
            ApplicationCapabilityImplementationIdentity.ExactContract(candidate.CoachProjectorContractType),
            $"{nameof(descriptor.QualificationCandidate)}.{nameof(candidate.CoachProjectorContractType)}",
            required: true);

        if (!string.Equals(
                candidate.DescriptorFingerprint,
                descriptor.DescriptorFingerprint,
                StringComparison.Ordinal))
        {
            Fail("Qualification candidate descriptor fingerprint does not match the capability descriptor.");
        }

        if (descriptor.Handler?.ContractType != candidate.HandlerContractType
            || descriptor.CoachProjector?.ContractType != candidate.CoachProjectorContractType)
        {
            Fail("Qualification candidate handler or Coach projector type does not match the capability descriptor.");
        }
    }

    private static void ValidateFingerprint(string? value, string name)
    {
        if (!IsCanonicalFingerprint(value))
        {
            Fail($"{name} must be a canonical lower-case SHA-256 fingerprint.");
        }
    }

    internal static bool IsCanonicalFingerprint(string? value)
    {
        const string prefix = "sha256:";
        return value is not null
            && value.Length == Sha256FingerprintLength
            && value.StartsWith(prefix, StringComparison.Ordinal)
            && !value.AsSpan(prefix.Length).ContainsAnyExcept("0123456789abcdef");
    }

    private static void ValidateText(string? value, string name, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
        {
            Fail($"{name} must be non-empty and no longer than {maximumLength} characters.");
        }
    }

    private static void ValidateName(
        string? value,
        string name,
        int minimumLength,
        int maximumLength,
        bool allowDots)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length < minimumLength
            || value.Length > maximumLength
            || !IsStableName(value, allowDots))
        {
            Fail(
                $"{name} must be {minimumLength}..{maximumLength} lower-case characters, "
                + $"start with a letter, and use only letters, digits, hyphens{(allowDots ? ", or dots" : string.Empty)}.");
        }
    }

    private static bool IsStableName(string value, bool allowDots)
    {
        if (value[0] is < 'a' or > 'z')
        {
            return false;
        }

        var separator = false;
        foreach (var character in value)
        {
            var isSeparator = character == '-' || (allowDots && character == '.');
            if (!(character is >= 'a' and <= 'z')
                && !(character is >= '0' and <= '9')
                && !isSeparator)
            {
                return false;
            }

            if (isSeparator && separator)
            {
                return false;
            }

            separator = isSeparator;
        }

        return !separator;
    }

    private static void ValidateEnum<TEnum>(TEnum value, string name, bool allowUnknown)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value) || (!allowUnknown && Convert.ToInt32(value) == 0))
        {
            Fail($"{name} contains an unknown or unsupported value.");
        }
    }

    private static void Fail(string message) => throw new ApplicationCapabilityValidationException(message);
}
