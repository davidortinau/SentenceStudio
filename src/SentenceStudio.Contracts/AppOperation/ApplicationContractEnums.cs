using System.Text.Json.Serialization;
using SentenceStudio.Contracts.Wire;

namespace SentenceStudio.Contracts.AppOperation;

internal static class ApplicationWireSafety
{
    public const string UnknownRationale =
        "Unknown is the fail-closed wire value. Consumers must make no capability, authority, effect, "
        + "decision, retry, continuation, observation, or client-action claim from an unreadable value.";
}

[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(ApplicationCapabilitySurface.Unknown), WireEnumFallbackKind.SafeZero, ApplicationWireSafety.UnknownRationale)]
public enum ApplicationCapabilitySurface
{
    Unknown = 0,
    UserInterface = 1,
    Model = 2,
    Automation = 3,
    Internal = 4
}

[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(ApplicationExecutionAuthority.Unknown), WireEnumFallbackKind.SafeZero, ApplicationWireSafety.UnknownRationale)]
public enum ApplicationExecutionAuthority
{
    Unknown = 0,
    NativeLocal = 1,
    Server = 2,
    External = 3,
    Client = 4
}

[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(ApplicationEffectClass.Unknown), WireEnumFallbackKind.SafeZero, ApplicationWireSafety.UnknownRationale)]
public enum ApplicationEffectClass
{
    Unknown = 0,
    Read = 1,
    Write = 2,
    Launch = 3,
    ExternalEffect = 4,
    Composite = 5
}

[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(ApplicationSensitivity.Unknown), WireEnumFallbackKind.SafeZero, ApplicationWireSafety.UnknownRationale)]
public enum ApplicationSensitivity
{
    Unknown = 0,
    Public = 1,
    Aggregate = 2,
    OwnerContent = 3,
    ClientOnly = 4,
    Secret = 5,
    Prohibited = 6
}

[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(ApplicationModelExposureState.Unknown), WireEnumFallbackKind.SafeZero, ApplicationWireSafety.UnknownRationale)]
public enum ApplicationModelExposureState
{
    Unknown = 0,
    Denied = 1,
    Eligible = 2,
    ReviewRequired = 3,
    Blocked = 4
}

[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(ApplicationConfirmationPolicy.Unknown), WireEnumFallbackKind.SafeZero, ApplicationWireSafety.UnknownRationale)]
public enum ApplicationConfirmationPolicy
{
    Unknown = 0,
    None = 1,
    Gesture = 2,
    Accept = 3,
    ProtectedConfirmation = 4
}

[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(ApplicationIdempotencyPolicy.Unknown), WireEnumFallbackKind.SafeZero, ApplicationWireSafety.UnknownRationale)]
public enum ApplicationIdempotencyPolicy
{
    Unknown = 0,
    NotApplicable = 1,
    NaturallyIdempotent = 2,
    RequiredByKey = 3
}

[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(ApplicationSynchronizationPolicy.Unknown), WireEnumFallbackKind.SafeZero, ApplicationWireSafety.UnknownRationale)]
public enum ApplicationSynchronizationPolicy
{
    Unknown = 0,
    Required = 1,
    Cached = 2,
    OnlineOnly = 3,
    NotApplicable = 4
}

[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(ApplicationContinuationPolicy.Unknown), WireEnumFallbackKind.SafeZero, ApplicationWireSafety.UnknownRationale)]
public enum ApplicationContinuationPolicy
{
    Unknown = 0,
    None = 1,
    ClarificationOnly = 2,
    OneAutomaticResume = 3
}

[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(ApplicationRequestKind.Unknown), WireEnumFallbackKind.SafeZero, ApplicationWireSafety.UnknownRationale)]
public enum ApplicationRequestKind
{
    Unknown = 0,
    Query = 1,
    Command = 2
}

[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(ApplicationOperationStatus.Unknown), WireEnumFallbackKind.SafeZero, ApplicationWireSafety.UnknownRationale)]
public enum ApplicationOperationStatus
{
    Unknown = 0,
    Proposed = 1,
    AwaitingProtectedConfirmation = 2,
    Executing = 3,
    Executed = 4,
    Rejected = 5,
    Cancelled = 6,
    Expired = 7,
    Failed = 8,
    Reversed = 9
}

[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(ApplicationOperationDecision.Unknown), WireEnumFallbackKind.SafeZero, ApplicationWireSafety.UnknownRationale)]
public enum ApplicationOperationDecision
{
    Unknown = 0,
    Accept = 1,
    Reject = 2,
    Cancel = 3,
    Confirm = 4,
    Reverse = 5,
    Continue = 6
}

[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(ClientActionKind.Unknown), WireEnumFallbackKind.SafeZero, ApplicationWireSafety.UnknownRationale)]
public enum ClientActionKind
{
    Unknown = 0,
    None = 1,
    LaunchActivity = 2,
    RefreshDomainView = 3,
    ReloadLearnerContext = 4
}

[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(ApplicationActivityKind.Unknown), WireEnumFallbackKind.SafeZero, ApplicationWireSafety.UnknownRationale)]
public enum ApplicationActivityKind
{
    Unknown = 0,
    VocabularyReview = 1,
    VocabularyMatching = 2,
    Cloze = 3,
    Reading = 4,
    Listening = 5,
    Shadowing = 6,
    Translation = 7,
    Writing = 8,
    SceneDescription = 9,
    Conversation = 10,
    VideoWatching = 11,
    NumberDrill = 12,
    WordAssociation = 13,
    MinimalPairs = 14,
    HowDoYouSay = 15,
    Flashcard = 16
}

[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(ApplicationLimitationReason.Unknown), WireEnumFallbackKind.SafeZero, ApplicationWireSafety.UnknownRationale)]
public enum ApplicationLimitationReason
{
    Unknown = 0,
    CapabilityUnavailable = 1,
    CapabilityDenied = 2,
    CapabilityVersionUnsupported = 3,
    InvalidRequest = 4,
    StaleState = 5,
    SynchronizationRequired = 6,
    Conflict = 7,
    ProposalExpired = 8,
    ConfirmationRequired = 9,
    ConfirmationInvalid = 10,
    OperationUnavailable = 11,
    BudgetExceeded = 12,
    TemporarilyUnavailable = 13,
    ExternalOutcomeUncertain = 14
}

[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(ApplicationRetryClassification.Unknown), WireEnumFallbackKind.SafeZero, ApplicationWireSafety.UnknownRationale)]
public enum ApplicationRetryClassification
{
    Unknown = 0,
    NotRetryable = 1,
    RetryAfterSynchronization = 2,
    RetryAfterDelay = 3,
    RetryWithFreshRequest = 4
}

[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(ApplicationContinuationWorkflow.Unknown), WireEnumFallbackKind.SafeZero, ApplicationWireSafety.UnknownRationale)]
public enum ApplicationContinuationWorkflow
{
    Unknown = 0,
    Clarification = 1,
    PostReceiptResume = 2
}

[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(ApplicationContinuationState.Unknown), WireEnumFallbackKind.SafeZero, ApplicationWireSafety.UnknownRationale)]
public enum ApplicationContinuationState
{
    Unknown = 0,
    AwaitingDecision = 1,
    ReadyToResume = 2,
    Completed = 3,
    Expired = 4,
    Cancelled = 5
}

[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachObservationState.Unknown), WireEnumFallbackKind.SafeZero, ApplicationWireSafety.UnknownRationale)]
public enum CoachObservationState
{
    Unknown = 0,
    Fresh = 1,
    Stale = 2,
    Incomplete = 3,
    Conflicting = 4,
    Unavailable = 5
}

[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(ApplicationDomainViewKind.Unknown), WireEnumFallbackKind.SafeZero, ApplicationWireSafety.UnknownRationale)]
public enum ApplicationDomainViewKind
{
    Unknown = 0,
    TodayPlan = 1,
    Progress = 2,
    Vocabulary = 3,
    LearningResources = 4,
    Skills = 5,
    Preferences = 6,
    ActivityHistory = 7
}

[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(ApplicationLearnerContextKind.Unknown), WireEnumFallbackKind.SafeZero, ApplicationWireSafety.UnknownRationale)]
public enum ApplicationLearnerContextKind
{
    Unknown = 0,
    Profile = 1,
    ActiveLanguage = 2,
    Preferences = 3,
    Complete = 4
}

[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(ApplicationReversalAvailability.Unknown), WireEnumFallbackKind.SafeZero, ApplicationWireSafety.UnknownRationale)]
public enum ApplicationReversalAvailability
{
    Unknown = 0,
    Unavailable = 1,
    Available = 2,
    Expired = 3,
    Completed = 4
}
