using System.Collections.Immutable;
using SentenceStudio.Contracts.Plans;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native;

public enum NativeSyncOutcome { Confirmed, Offline, Unconfirmed }

/// <summary>
/// A completed TriggerSyncAsync task is not a success receipt: the current SyncService
/// catches remote errors and returns normally. A head must supply a verified outcome
/// before this slice treats an absent local profile as an onboarding decision.
/// </summary>
public interface INativeSyncReadiness
{
    Task<NativeSyncOutcome> SynchronizeAsync(string userId, CancellationToken cancellationToken);
}

/// <summary>
/// Shared must compare-and-create the scoped local-day plan atomically.
/// Calling GenerateTodayAsync after a separate GetTodayAsync is unsafe:
/// a competing writer can create a plan between them, and regeneration
/// currently deletes displaced work.
/// </summary>
public interface INativePlanCreation
{
    Task<NativePlanCreateResult> CreateIfAbsentAsync(
        string userId, DateOnly localDate, CancellationToken cancellationToken);
}

public sealed record NativePlanCreateResult(TodaysPlanDto Plan, bool Created);

/// <summary>
/// Shared scoring must persist progress, learning context, and a unique
/// (owner, launch context, turn) receipt atomically. A replay returns the
/// same receipt without applying the attempt twice.
/// </summary>
public interface INativeQuizAttemptCommitter
{
    Task<NativeQuizCommitReceipt?> FindAsync(
        string userId, string launchContextKey, string turnId, CancellationToken cancellationToken);

    Task<NativeQuizCommitReceipt> CommitAsync(
        string userId, string launchContextKey, string turnId,
        VocabularyAttempt attempt, CancellationToken cancellationToken);
}

/// <summary>
/// Source quiz choice construction currently lives inside VocabQuiz.razor.
/// The platform-independent source implementation must enforce distinct,
/// owner-reachable distractors and exclude the correct answer before registration.
/// </summary>
public interface INativeQuizChoices
{
    Task<ImmutableArray<NativeQuizChoice>> CreateAsync(
        string userId, VocabularyWord question, IReadOnlyList<VocabularyWord> reachableWords,
        bool promptUsesNativeLanguage, CancellationToken cancellationToken);
}

/// <summary>
/// The non-focus due/known/grace-period selection in VocabQuiz.razor needs
/// a Shared owner-scoped implementation before native can reuse that path.
/// Focus-card selection uses the existing FocusVocabularySelection helper.
/// </summary>
public interface INativeQuizEligibility
{
    Task<NativeQuizEligibilityResult> SelectAsync(
        string userId, bool dueOnly,
        IReadOnlyList<VocabularyQuizItem> candidates,
        CancellationToken cancellationToken);
}

public sealed record NativeQuizEligibilityResult(
    IReadOnlyList<VocabularyQuizItem> Batch,
    bool AllMastered);

/// <summary>Reuses the source Text and MC evaluation, never a native copy of the grading rule.</summary>
public interface INativeQuizGrader
{
    Task<FuzzyMatchResult> EvaluateAsync(
        string userId, string mode, string answer,
        string expectedAnswer, CancellationToken cancellationToken);
}

/// <summary>Platform media adapters report playback only after it actually succeeds.</summary>
public interface INativeQuizMedia
{
    Task<bool> CanPlayPromptAsync(string text, string language, CancellationToken cancellationToken);
    Task<bool> PlayPromptAsync(string text, string language, CancellationToken cancellationToken);
    Task<bool> CanShowPhotoAsync(string photoUri, CancellationToken cancellationToken);
}

internal sealed class UnconfirmedNativeSyncReadiness(
    SentenceStudio.Abstractions.IConnectivityService connectivity) : INativeSyncReadiness
{
    public Task<NativeSyncOutcome> SynchronizeAsync(string userId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new UnauthorizedAccessException("Sync requires an authenticated owner.");
        cancellationToken.ThrowIfCancellationRequested();
        if (!connectivity.IsInternetAvailable)
            return Task.FromResult(NativeSyncOutcome.Offline);
        // SyncService.TriggerSyncAsync catches remote failures and can push local
        // changes. Without an owner-bound success receipt, doing either here
        // would be a false readiness signal or an unauthorized side effect.
        return Task.FromResult(NativeSyncOutcome.Unconfirmed);
    }
}

public sealed class NativePendingWrites
{
    private const string PendingKeyPrefix = "native_sync_pending_v1_";
    private const string RevisionKeyPrefix = "native_sync_revision_v1_";
    private readonly object _gate = new();
    private SentenceStudio.Abstractions.IPreferencesService? _preferences;

    public NativePendingWrites() { }

    internal NativePendingWrites UsePersistentStore(
        SentenceStudio.Abstractions.IPreferencesService preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        lock (_gate)
        {
            if (_preferences is not null && !ReferenceEquals(_preferences, preferences))
                throw new InvalidOperationException("Native sync tracking is already bound to another store.");
            _preferences = preferences;
        }
        return this;
    }

    internal long Revision(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        lock (_gate)
            return Store.Get(RevisionKeyPrefix + userId, 0L);
    }

    internal bool IsPending(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        lock (_gate)
            return Store.Get(PendingKeyPrefix + userId, false);
    }

    internal void RestoreUnmarkedCommittedTurns(string userId, int committedTurns)
    {
        if (committedTurns <= 0)
            return;
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        lock (_gate)
        {
            if (Store.Get(RevisionKeyPrefix + userId, 0L) != 0)
                return;

            Store.Set(PendingKeyPrefix + userId, true);
            Store.Set(RevisionKeyPrefix + userId, 1L);
        }
    }

    internal void MarkPending(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        lock (_gate)
        {
            var nextRevision = checked(Store.Get(RevisionKeyPrefix + userId, 0L) + 1);
            Store.Set(PendingKeyPrefix + userId, true);
            Store.Set(RevisionKeyPrefix + userId, nextRevision);
        }
    }

    internal void MarkConfirmedIfUnchanged(string userId, long revision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        lock (_gate)
        {
            var currentRevision = Store.Get(RevisionKeyPrefix + userId, 0L);
            if (revision != currentRevision)
                return;

            Store.Set(RevisionKeyPrefix + userId, checked(currentRevision + 1));
            Store.Set(PendingKeyPrefix + userId, false);
        }
    }

    private SentenceStudio.Abstractions.IPreferencesService Store =>
        _preferences ?? throw new InvalidOperationException(
            "Native sync tracking requires the persistent preference store registered by AddSentenceStudioNativeSlice.");
}

internal sealed class UnavailableNativeQuizAttemptCommitter : INativeQuizAttemptCommitter
{
    private const string MissingSeam =
        "Shared vocabulary progress has no atomic owner/context/turn receipt; refusing an attempt that could be double-counted.";

    public Task<NativeQuizCommitReceipt?> FindAsync(
        string userId, string launchContextKey, string turnId, CancellationToken cancellationToken)
        => throw new NotSupportedException(MissingSeam);

    public Task<NativeQuizCommitReceipt> CommitAsync(
        string userId, string launchContextKey, string turnId,
        VocabularyAttempt attempt, CancellationToken cancellationToken)
        => throw new NotSupportedException(MissingSeam);
}

internal sealed class UnavailableNativePlanCreation : INativePlanCreation
{
    public Task<NativePlanCreateResult> CreateIfAbsentAsync(
        string userId, DateOnly localDate, CancellationToken cancellationToken)
        => throw new NotSupportedException(
            "Shared IPlanService has no atomic owner/date create-if-absent; refusing a generation race that could delete prior work.");
}

internal sealed class UnavailableNativeQuizChoices : INativeQuizChoices
{
    public Task<ImmutableArray<NativeQuizChoice>> CreateAsync(
        string userId, VocabularyWord question, IReadOnlyList<VocabularyWord> reachableWords,
        bool promptUsesNativeLanguage, CancellationToken cancellationToken)
        => throw new NotSupportedException(
            "Shared quiz distractor selection must be extracted from VocabQuiz.razor before native multiple choice can launch.");
}

internal sealed class UnavailableNativeQuizEligibility : INativeQuizEligibility
{
    public Task<NativeQuizEligibilityResult> SelectAsync(
        string userId, bool dueOnly, IReadOnlyList<VocabularyQuizItem> candidates,
        CancellationToken cancellationToken)
        => throw new NotSupportedException(
            "Shared non-focus quiz eligibility must be extracted from VocabQuiz.razor.");
}

internal sealed class UnavailableNativeQuizGrader : INativeQuizGrader
{
    public Task<FuzzyMatchResult> EvaluateAsync(
        string userId, string mode, string answer,
        string expectedAnswer, CancellationToken cancellationToken)
        => throw new NotSupportedException(
            "Shared Vocab Quiz MC/Text grading must be extracted from VocabQuiz.razor.");
}

internal sealed class UnavailableNativeQuizMedia : INativeQuizMedia
{
    public Task<bool> CanPlayPromptAsync(string text, string language, CancellationToken cancellationToken)
        => Task.FromResult(false);

    public Task<bool> PlayPromptAsync(string text, string language, CancellationToken cancellationToken)
        => Task.FromResult(false);

    public Task<bool> CanShowPhotoAsync(string photoUri, CancellationToken cancellationToken)
        => Task.FromResult(false);
}
