using System.Collections.Immutable;
using System.Net.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Services.Plans;
using SentenceStudio.Services.Progress;
using SentenceStudio.Services.Timer;
using SentenceStudio.Shared.Models;
using SentenceStudio.Shared.Services;

namespace SentenceStudio.Native;

/// <summary>
/// Platform-independent quiz journey. Heads render State and dispatch commands;
/// they never write vocabulary, activity, or plan rows directly.
/// </summary>
public sealed partial class NativeQuizPresenter(
    NativePlanLeaseFactory plans,
    DeviceUserScopeProvider userScope,
    IPreferencesService preferences,
    UserProfileRepository profiles,
    LearningResourceRepository resources,
    VocabularyProgressRepository progress,
    IServiceScopeFactory scopes,
    VocabQuizLaunchValidator validator,
    IProgressService planProgress,
    IActivitySessionService sessions,
    VocabularyQuizPreferences quizPreferences,
    INativeQuizChoices choices,
    INativeQuizEligibility eligibility,
    INativeQuizGrader grader,
    INativeQuizSentenceGrader sentenceGrader,
    INativeQuizSentenceCommitter sentenceCommitter,
    INativeQuizMedia media,
    INativeQuizAttemptCommitter committer,
    INativeSyncReadiness sync,
    NativePendingWrites pendingWrites,
    ILogger<NativeQuizPresenter> logger,
    ILogger<ActivityTimerService> timerLogger) : IDisposable
{
    private const string ActivityType = "VocabularyReview";
    private const int BatchSize = 20;
    private const int TurnsPerRound = 10;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly NativeSliceRegistration.NativeQuizClock _clock = new(timerLogger);
    private NativeQuizLaunch? _launch;
    private string? _contextKey;
    private string? _planItemRowId;
    private string? _adHocPlanItemId;
    private string? _displayDirection;
    private UserProfile? _profile;
    private List<VocabularyQuizItem> _batch = [];
    private List<VocabularyQuizItem> _round = [];
    private List<VocabularyQuizItem> _roundArchive = [];
    private List<VocabularyWord> _reachableWords = [];
    private ActivitySession? _resumable;
    private NativeQuizStoredState? _stored;
    private VocabularyAttempt? _pending;
    private string _turnId = string.Empty;
    private int _turnIndex;
    private int _roundCursor;
    private int _roundsCompleted;
    private int _wordsMastered;
    private int _totalTurns;
    private int _correctCount;
    private bool _requiresCorrection;
    private bool _overrideRetryPending;
    private bool _correctionRetryPending;
    private bool _needsResumeAfterCommitFailure;
    private bool _allMasteredAtLaunch;
    private bool _audioHeard;
    private bool _audioAvailable;
    private CancellationTokenSource? _activeAudio;
    private bool _photoLoaded;
    private bool _photoVisible;
    private bool _promptUsesNative;
    private string _mode = "MultipleChoice";
    private TimeSpan _questionStartedAt;
    private bool _pendingSync;
    private bool _disposed;
    private ImmutableArray<NativeQuizChoice> _choices = [];
    private NativeQuizState _state =
        new(NativePhase.Idle, NativeDestination.VocabularyQuiz);
    private string? _stateOwner;

    public NativeQuizState State =>
        _stateOwner is not null
        && !HasOwner(_stateOwner)
            ? new(NativePhase.Blocked, NativeDestination.VocabularyQuiz,
                ErrorKey: NativeText.QuizUnavailable)
            : _state;

    public event Action<NativeQuizState>? StateChanged;

    public async Task<NativeQuizState> OpenAsync(
        NativeQuizLaunch launch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launch);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ResetDifferentLaunch(launch);
            SetState(new(NativePhase.Loading, NativeDestination.VocabularyQuiz));
            if (!HasOwner(launch.UserId)
                || launch.PlanItemId is null
                || launch.PlanItemId.Length > 0
                    && string.IsNullOrWhiteSpace(launch.PlanItemId))
                return RefuseLaunch();

            var direct = launch.PlanItemId.Length == 0;
            if (direct && launch.IsRepractice)
                return RefuseLaunch();
            using (var lease = plans.ForOwner(launch.UserId))
            {
                if (lease.LocalDate != launch.PlanDate)
                    return RefuseLaunch();
                if (!direct)
                {
                    var plan = await lease.Service.GetTodayAsync(cancellationToken);
                    var card = plan?.Items.SingleOrDefault(item =>
                        item.Id == launch.PlanItemId
                        && item.ActivityType == nameof(PlanActivityType.VocabularyReview));
                    var focusIds = launch.FocusVocabularyIds.IsDefault
                        ? [] : launch.FocusVocabularyIds.AsEnumerable();
                    var resourceIds = launch.ResourceIds.IsDefault
                        ? [] : launch.ResourceIds.AsEnumerable();
                    if (plan is null || plan.GeneratedForDate != launch.PlanDate
                        || card is null || resourceIds.Count() > 1
                        || card.IsCompleted != launch.IsRepractice
                        || !card.FocusVocabularyIds.SequenceEqual(
                            focusIds, StringComparer.Ordinal)
                        || !string.Equals(card.ResourceId,
                            resourceIds.SingleOrDefault(), StringComparison.Ordinal)
                        || !string.Equals(card.SkillId, launch.SkillId,
                            StringComparison.Ordinal))
                        return RefuseLaunch();

                    _planItemRowId = await lease.Database.DailyPlanCompletions.AsNoTracking()
                        .Where(row => row.UserProfileId == launch.UserId
                            && row.Date == launch.PlanDate.ToDateTime(
                                TimeOnly.MinValue, DateTimeKind.Utc)
                            && row.PlanItemId == launch.PlanItemId && !row.IsArchived)
                        .Select(row => row.Id)
                        .SingleOrDefaultAsync(cancellationToken);
                    if (string.IsNullOrWhiteSpace(_planItemRowId))
                        return RefuseLaunch();
                }
            }

            var route = await validator.ValidateRouteAsync(
                launch.UserId,
                launch.ResourceIds.IsDefault ? [] : launch.ResourceIds,
                launch.SkillId);
            if (!route.IsValid || !HasOwner(launch.UserId)
                || direct && (!launch.ResourceIds.IsDefault
                    && launch.ResourceIds.Distinct(StringComparer.Ordinal).Count()
                        != launch.ResourceIds.Length
                    || !launch.FocusVocabularyIds.IsDefault
                        && launch.FocusVocabularyIds.Distinct(StringComparer.Ordinal).Count()
                            != launch.FocusVocabularyIds.Length
                    || route.Resources.Count != (launch.ResourceIds.IsDefault
                        ? 0 : launch.ResourceIds.Length)))
                return RefuseLaunch();

            _profile = await profiles.GetByIdAsync(launch.UserId, cancellationToken);
            if (_profile is null || !HasOwner(launch.UserId))
                return RefuseLaunch();

            _reachableWords = await resources.GetAllVocabularyWordsWithResourcesAsync(launch.UserId);
            if (!HasOwner(launch.UserId))
                return RefuseLaunch();

            var focus = FocusVocabularySelection.NormalizeFocusVocabularyIds(
                launch.FocusVocabularyIds.IsDefault ? [] : launch.FocusVocabularyIds);
            if (focus.Count == 0 && !launch.DueOnly && route.Resources.Count > 0)
            {
                var scopedResources = route.Resources.Select(resource => resource.Id)
                    .ToHashSet(StringComparer.Ordinal);
                _reachableWords = _reachableWords.Where(word =>
                    word.LearningResources.Any(resource =>
                        scopedResources.Contains(resource.Id))).ToList();
            }
            var selected = focus.Count == 0
                ? _reachableWords.Where(FocusVocabularySelection.IsUsableVocabularyWord).ToList()
                : FocusVocabularySelection.SelectFocusWords(_reachableWords, focus);
            if (focus.Count > 0 && selected.Count != focus.Count)
                return RefuseLaunch();
            if (selected.Count == 0)
                return SetState(new(NativePhase.Empty, NativeDestination.VocabularyQuiz,
                    ErrorKey: NativeText.QuizEmpty));

            if (!direct)
            {
                var verified = await planProgress.ValidatePlanItemAsync(
                    launch.UserId, launch.PlanItemId,
                    PlanActivityType.VocabularyReview,
                    route.Resources.Count == 1 ? route.Resources[0].Id : null,
                    route.Skill?.Id,
                    selected.Select(word => word.Id).ToArray(),
                    cancellationToken);
                if (verified is null || !HasOwner(launch.UserId))
                    return RefuseLaunch();
            }

            _launch = launch;
            _contextKey = VocabQuizSessionSnapshot.BuildLaunchContextKey(
                launch.PlanItemId, focus, route.Resources.Select(resource => resource.Id),
                launch.DueOnly, route.Skill?.Id);
            _displayDirection = quizPreferences.DisplayDirection;
            _resumable = await sessions.GetResumableAsync(
                launch.UserId, ActivityType, _contextKey);
            if (!HasOwner(launch.UserId))
                return RefuseLaunch();

            if (_resumable is null)
                return await StartNewAsync(cancellationToken);

            try
            {
                _stored = NativeQuizStoredState.Deserialize(_resumable.StateJson);
                if (direct)
                {
                    using var lease = plans.ForOwner(launch.UserId);
                    var row = await lease.Database.DailyPlanCompletions.AsNoTracking()
                        .SingleOrDefaultAsync(item => item.Id == _stored.PlanItemRowId
                            && item.UserProfileId == launch.UserId
                            && item.Date == launch.PlanDate.ToDateTime(
                                TimeOnly.MinValue, DateTimeKind.Utc)
                            && item.PlanItemId == _stored.AdHocPlanItemId
                            && item.PlanItemId.StartsWith("adhoc-")
                            && item.ActivityType == ActivityType && !item.IsArchived,
                            cancellationToken);
                    if (row is null)
                        throw new InvalidOperationException(
                            "A direct quiz lost its owner-scoped activity row.");
                    _planItemRowId = row.Id;
                    _adHocPlanItemId = row.PlanItemId;
                }
                if (!ValidateStoredState(_stored, selected))
                    throw new InvalidOperationException("Stored quiz context does not match the active plan.");
                return SetState(new(NativePhase.ResumeChoice, NativeDestination.VocabularyQuiz));
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException
                or InvalidOperationException)
            {
                logger.LogWarning(ex, "Native quiz resume state was rejected.");
                _stored = null;
                return SetState(new(NativePhase.ResumeChoice, NativeDestination.VocabularyQuiz,
                    ErrorKey: NativeText.ResumeUnavailable));
            }
        }
        catch (NotSupportedException ex)
        {
            logger.LogWarning(ex, "Native quiz launch requires a shared service boundary.");
            return SetState(new(NativePhase.Blocked, NativeDestination.VocabularyQuiz,
                ErrorKey: NativeText.QuizUnavailable));
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Native quiz launch failed while offline.");
            return SetState(new(NativePhase.Error, NativeDestination.VocabularyQuiz,
                ErrorKey: NativeText.ConnectionError, HasPendingSync: true));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<NativeQuizState> StartFreshAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_launch is null || !HasOwner(_launch.UserId) || _contextKey is null)
                return RefuseLaunch();
            if (_resumable is not null)
            {
                await sessions.AbandonAsync(_launch.UserId, _resumable.Id);
                _resumable = null;
            }
            _stored = null;
            return await StartNewAsync(cancellationToken);
        }
        catch (NotSupportedException ex)
        {
            logger.LogWarning(ex, "Native quiz needs an owner-scoped shared choice provider.");
            return SetState(new(NativePhase.Blocked, NativeDestination.VocabularyQuiz,
                ErrorKey: NativeText.QuizUnavailable));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<NativeQuizState> ResumeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_stored is null || _launch is null || !HasOwner(_launch.UserId))
                return SetState(new(NativePhase.ResumeChoice, NativeDestination.VocabularyQuiz,
                    ErrorKey: NativeText.ResumeUnavailable));
            if (State.Phase == NativePhase.Error
                && _pending is not null && _stored.PendingAttempt is null)
            {
                logger.LogWarning("Quiz resume refused while an unsaved graded turn still needs a snapshot retry.");
                return SetState(State with { ErrorKey = NativeText.SaveFailed });
            }

            var snapshot = _stored.Snapshot;
            var words = _reachableWords.ToDictionary(word => word.Id, StringComparer.Ordinal);
            var ids = snapshot.BatchPool.Select(item => item.WordId).ToList();
            var progressByWord = (await progress.GetByWordIdsForUserAsync(ids, _launch.UserId))
                .ToDictionary(item => item.VocabularyWordId, StringComparer.Ordinal);
            if (!HasOwner(_launch.UserId))
                return RefuseLaunch();

            _batch = snapshot.BatchPool.Select(item =>
            {
                var word = words[item.WordId];
                var quizItem = new VocabularyQuizItem
                {
                    Word = word,
                    Progress = progressByWord.GetValueOrDefault(word.Id)
                        ?? new VocabularyProgress { VocabularyWordId = word.Id, UserId = _launch.UserId },
                    SessionCorrectCount = item.SessionCorrectCount,
                    SessionMCCorrect = item.SessionMCCorrect,
                    SessionTextCorrect = item.SessionTextCorrect,
                    PendingRecognitionCheck = item.PendingRecognitionCheck,
                    LostKnownThisSession = item.LostKnownThisSession,
                    WasCorrectThisSession = item.WasCorrectThisSession,
                    IsDueOnlySession = item.IsDueOnlySession,
                    RequiresFullSessionDemonstration = item.RequiresFullSessionDemonstration,
                    UseKnownWordShortcut = item.UseKnownWordShortcut,
                    RecognitionDemonstrationsBaseline = item.RecognitionDemonstrationsBaseline,
                    ProductionDemonstrationsBaseline = item.ProductionDemonstrationsBaseline
                };
                return quizItem;
            }).ToList();
            await LoadSentenceHintsAsync(cancellationToken);
            if (!HasOwner(_launch.UserId))
                return RefuseLaunch();
            var byId = _batch.ToDictionary(item => item.Word.Id, StringComparer.Ordinal);
            _round = snapshot.RoundWordOrder.Select(id => byId[id]).ToList();
            _roundArchive = (snapshot.SessionItemsWordIds.Count > 0
                    ? snapshot.SessionItemsWordIds : snapshot.RoundWordOrder)
                .Select(id => byId[id]).ToList();
            _turnIndex = snapshot.CurrentTurnInRound;
            _roundCursor = snapshot.RoundCursor;
            _roundsCompleted = snapshot.RoundsCompleted;
            _wordsMastered = snapshot.WordsMastered;
            _totalTurns = snapshot.TotalTurns;
            _correctCount = snapshot.CorrectCount;
            _pending = _stored.PendingAttempt;
            _requiresCorrection = _stored.RequiresCorrection;
            _sentenceSubmissionId = _stored.SentenceSubmissionId;
            _sentenceInput = _stored.SentenceSourceInput;
            _sentenceResults = _stored.SentenceResults.ToImmutableArray();
            _sentenceCreditApplied = _stored.SentenceCreditApplied;
            pendingWrites.RestoreUnmarkedCommittedTurns(
                _launch.UserId, snapshot.TotalTurns - (_pending is null ? 0 : 1));
            _pendingSync = pendingWrites.IsPending(_launch.UserId);
            _overrideRetryPending = false;
            _correctionRetryPending = false;
            _turnId = _stored.TurnId;
            _clock.Stop();
            var elapsedTicks = Math.Max(_stored.ElapsedTicks, _clock.Elapsed.Ticks);
            _clock.Start(_adHocPlanItemId ?? _launch.PlanItemId,
                TimeSpan.FromTicks(elapsedTicks));

            if (_sentenceSubmissionId is not null)
            {
                var receipt = await sentenceCommitter.FindAsync(
                    _launch.UserId, _contextKey!, _turnId,
                    _sentenceSubmissionId, cancellationToken);
                if (receipt is not null && !_sentenceCreditApplied)
                {
                    ApplySentenceReceipt(receipt);
                    await SaveSnapshotAsync(summaryVisible: false, cancellationToken);
                }
                else if (receipt is not null
                    && (receipt.SuccessCount != _sentenceResults.Count(
                        result => result.UsageCorrect && !result.GradeUnavailable)
                        || !receipt.Results.SequenceEqual(_sentenceResults)))
                    throw new InvalidOperationException(
                        "The credited sentence results disagree with their receipt.");
                else if (receipt is null && _sentenceCreditApplied)
                    throw new InvalidOperationException(
                        "The credited sentence submission lost its receipt.");
                var sentenceQuestion = await PresentQuestionAsync(
                    snapshot.PromptUsesNativeLanguage, snapshot.UserMode,
                    graded: false, cancellationToken);
                if (sentenceQuestion.Phase == NativePhase.Blocked)
                    return SetState(sentenceQuestion);
                var sentenceState = sentenceQuestion with
                {
                    Phase = _sentenceResults.Length > 0 && receipt is null
                        ? NativePhase.Error : NativePhase.Ready,
                    Mode = "Sentence",
                    Sentence = NativeQuizModality.RenderSentence(
                        _round[_turnIndex].Word, _profile!.TargetLanguage),
                    SentenceResults = _sentenceCreditApplied ? _sentenceResults : [],
                    SentenceSuccessCount = _sentenceCreditApplied
                        ? _sentenceResults.Count(result => result.UsageCorrect
                            && !result.GradeUnavailable) : 0,
                    SentenceDraft = _sentenceCreditApplied ? null : _sentenceInput,
                    ErrorKey = _sentenceResults.Length > 0 && receipt is null
                        ? NativeText.SaveFailed : null,
                    CanSubmit = false
                };
                return SetState(sentenceState);
            }

            if (_pending is not null)
            {
                // Q25b: a stale snapshot can be graded/pending even though
                // Next already committed this turn. A receipt is mandatory;
                // without it the result cannot be safely replayed.
                var receipt = await committer.FindAsync(
                    _launch.UserId, _contextKey!, _turnId, cancellationToken);
                if (receipt is not null)
                {
                    // Q25b: a process may stop after the SQLite commit and
                    // before its preference marker. The persisted receipt is
                    // authoritative even if an earlier sync had been confirmed.
                    if (!pendingWrites.IsPending(_launch.UserId))
                        pendingWrites.MarkPending(_launch.UserId);
                    return await AdvanceCommittedAsync(receipt, cancellationToken);
                }

                var feedback = await PresentQuestionAsync(
                    snapshot.PromptUsesNativeLanguage, snapshot.UserMode,
                    graded: true, cancellationToken);
                if (feedback.Phase == NativePhase.Blocked)
                    _clock.Stop();
                return SetState(feedback);
            }

            if (_stored.SummaryVisible)
                return SetState(SummaryState());

            var question = await PresentQuestionAsync(
                snapshot.PromptUsesNativeLanguage, snapshot.UserMode,
                graded: false, cancellationToken);
            if (question.Phase == NativePhase.Blocked)
                _clock.Stop();
            var restored = SetState(question);
            _needsResumeAfterCommitFailure = false;
            return restored;
        }
        catch (NotSupportedException ex)
        {
            logger.LogWarning(ex, "Native quiz resume cannot prove whether the pending turn committed.");
            return SetState(new(NativePhase.Blocked, NativeDestination.VocabularyQuiz,
                ErrorKey: NativeText.ResumeUnavailable));
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Native quiz resume could not verify its sentence receipt.");
            return SetState(new(NativePhase.Blocked, NativeDestination.VocabularyQuiz,
                ErrorKey: NativeText.ResumeUnavailable));
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool ValidateStoredState(NativeQuizStoredState stored, IReadOnlyList<VocabularyWord> selected)
    {
        var snapshot = stored.Snapshot;
        if (snapshot.BatchPool is null
            || snapshot.RoundWordOrder is null
            || snapshot.SessionItemsWordIds is null
            || snapshot.FocusVocabularyIds is null
            || snapshot.ResourceIds is null
            || snapshot.BatchPool.Any(item => item is null)
            || snapshot.RoundWordOrder.Any(string.IsNullOrWhiteSpace)
            || snapshot.SessionItemsWordIds.Any(string.IsNullOrWhiteSpace))
            return false;

        return _launch is not null
            && string.Equals(stored.UserId, _launch.UserId, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(stored.PlanItemRowId)
            && string.Equals(stored.PlanItemRowId, _planItemRowId, StringComparison.Ordinal)
            && (string.IsNullOrEmpty(_launch.PlanItemId)
                ? !string.IsNullOrWhiteSpace(stored.AdHocPlanItemId)
                    && stored.AdHocPlanItemId == _adHocPlanItemId
                : stored.AdHocPlanItemId is null)
            && stored.PlanDate == _launch.PlanDate
            && string.Equals(stored.LaunchContextKey, _contextKey, StringComparison.Ordinal)
            && string.Equals(stored.DisplayDirection, _displayDirection, StringComparison.Ordinal)
            && stored.IsRepractice == _launch.IsRepractice
            && VocabQuizLaunchValidator.CountRejectedSnapshotReferences(
                snapshot, _launch.PlanItemId,
                _launch.FocusVocabularyIds.IsDefault ? [] : _launch.FocusVocabularyIds,
                _launch.ResourceIds.IsDefault ? [] : _launch.ResourceIds,
                _launch.DueOnly, _launch.SkillId,
                selected.Select(word => word.Id)) == 0
            && snapshot.BatchPool.Count is > 0 and <= BatchSize
            && snapshot.BatchPool.Select(item => item.WordId)
                .Distinct(StringComparer.Ordinal).Count() == snapshot.BatchPool.Count
            && snapshot.RoundWordOrder.Count is > 0 and <= TurnsPerRound
            && snapshot.RoundWordOrder.Distinct(StringComparer.Ordinal).Count()
                == snapshot.RoundWordOrder.Count
            && snapshot.SessionItemsWordIds.Distinct(StringComparer.Ordinal).Count()
                == snapshot.SessionItemsWordIds.Count
            && snapshot.CurrentTurnInRound >= 0
            && (stored.SummaryVisible
                ? snapshot.CurrentTurnInRound <= snapshot.RoundWordOrder.Count
                : snapshot.CurrentTurnInRound < snapshot.RoundWordOrder.Count)
            && snapshot.UserMode is "Text" or "MultipleChoice"
            && snapshot.TotalTurns >= 0 && snapshot.CorrectCount >= 0
            && snapshot.CorrectCount <= snapshot.TotalTurns
            && snapshot.RoundCursor >= 0 && snapshot.RoundsCompleted >= 0
            && Guid.TryParseExact(stored.TurnId, "N", out _)
            && (_displayDirection == "Mixed"
                || snapshot.PromptUsesNativeLanguage == (_displayDirection == "NativeToTarget"))
            && (!stored.SummaryVisible || stored.PendingAttempt is null)
            && (stored.SentenceSubmissionId is null
                || !stored.SummaryVisible && stored.PendingAttempt is null
                    && !stored.RequiresCorrection
                    && stored.SentenceResults is not null)
            && (!stored.RequiresCorrection || stored.PendingAttempt is {
                WasCorrect: false, InputMode: "Text" })
            && (stored.PendingAttempt is null
                || snapshot.CurrentTurnInRound < snapshot.RoundWordOrder.Count
                && stored.PendingAttempt.UserId == _launch.UserId
                && stored.PendingAttempt.VocabularyWordId
                    == snapshot.RoundWordOrder[snapshot.CurrentTurnInRound]
                && stored.PendingAttempt.Activity == "VocabularyQuiz"
                && stored.PendingAttempt.InputMode == snapshot.UserMode
                && !string.IsNullOrWhiteSpace(stored.PendingAttempt.ExpectedAnswer)
                && !string.IsNullOrWhiteSpace(stored.PendingAttempt.UserInput)
                && stored.PendingAttempt.ResponseTimeMs >= 0);
    }

    private bool HasOwner(string userId) =>
        !string.IsNullOrWhiteSpace(userId)
        && userScope.TryGetUserProfileId(out var active)
        && string.Equals(active, userId, StringComparison.Ordinal)
        && string.Equals(
            preferences.Get(UserProfileRepository.ActiveProfileIdKey, string.Empty),
            userId, StringComparison.Ordinal);

    private void ResetDifferentLaunch(NativeQuizLaunch next)
    {
        if (_launch is null)
            return;

        var nextKey = VocabQuizSessionSnapshot.BuildLaunchContextKey(
            next.PlanItemId,
            next.FocusVocabularyIds.IsDefault ? [] : next.FocusVocabularyIds,
            next.ResourceIds.IsDefault ? [] : next.ResourceIds,
            next.DueOnly, next.SkillId);
        if (_launch.UserId == next.UserId && _contextKey == nextKey)
            return;

        if (_pending is not null && _stored?.PendingAttempt is null)
            logger.LogWarning("Leaving an unsaved graded quiz turn after an account or context change.");
        _clock.Discard();
        media.StopPlayback();
        _launch = null;
        _contextKey = null;
        _planItemRowId = null;
        _adHocPlanItemId = null;
        _profile = null;
        _resumable = null;
        _stored = null;
        _pending = null;
        _batch.Clear();
        _round.Clear();
        _roundArchive.Clear();
        _reachableWords.Clear();
        _choices = [];
        _photoAsset = null;
        _photoDesired = false;
        ResetSentenceSubmission();
        _sentenceHints = new Dictionary<string, IReadOnlyList<VocabQuizSentenceHint>>(
            StringComparer.Ordinal);
        _sentenceHintsExpanded = false;
        _stateOwner = null;
    }

    private NativeQuizState RefuseLaunch()
    {
        logger.LogWarning("Native quiz refused a missing, stale, or mismatched owner/plan context.");
        _clock.Discard();
        media.StopPlayback();
        _launch = null;
        _contextKey = null;
        _planItemRowId = null;
        _adHocPlanItemId = null;
        _pending = null;
        _stored = null;
        _resumable = null;
        _batch.Clear();
        _round.Clear();
        _roundArchive.Clear();
        _reachableWords.Clear();
        _choices = [];
        _photoAsset = null;
        _photoDesired = false;
        ResetSentenceSubmission();
        _sentenceHints = new Dictionary<string, IReadOnlyList<VocabQuizSentenceHint>>(
            StringComparer.Ordinal);
        _sentenceHintsExpanded = false;
        _overrideRetryPending = false;
        _correctionRetryPending = false;
        _needsResumeAfterCommitFailure = false;
        return SetState(new(NativePhase.Blocked, NativeDestination.VocabularyQuiz,
            ErrorKey: NativeText.QuizUnavailable));
    }

    private NativeQuizState SetState(NativeQuizState value)
    {
        _stateOwner = _launch?.UserId;
        _state = value;
        StateChanged?.Invoke(State);
        return State;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _lifetime.Cancel();
        media.StopPlayback();
        _clock.Dispose();
        _launch = null;
        _contextKey = null;
        _planItemRowId = null;
        _adHocPlanItemId = null;
        _profile = null;
        _resumable = null;
        _batch.Clear();
        _round.Clear();
        _roundArchive.Clear();
        _reachableWords.Clear();
        _photoAsset = null;
        _photoDesired = false;
        ResetSentenceSubmission();
        _sentenceHints = new Dictionary<string, IReadOnlyList<VocabQuizSentenceHint>>(
            StringComparer.Ordinal);
        _pending = null;
        _stored = null;
        _stateOwner = null;
        _state = new(NativePhase.Idle, NativeDestination.VocabularyQuiz);
        // Cancelled playback can still be unwinding through this semaphore.
        _lifetime.Dispose();
    }
}
