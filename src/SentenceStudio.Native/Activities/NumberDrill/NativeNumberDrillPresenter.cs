using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Services.Numbers;

namespace SentenceStudio.Native;

/// <summary>
/// Number Drill (source UI/Pages/NumberDrill.razor, activities.md 12):
/// Setup -> InSession -> Summary over the shared NumberSessionService,
/// KoreanNumberItemGenerator and KoreanNumberAnswerGrader (Korean, "ko").
/// Every submit writes one NumberAttempt and updates NumberMasteryProgress
/// (SM-2) through the shared service; no VocabularyProgress or UserActivity,
/// plan minutes through the activity timer. "I was correct" also re-grades the
/// stored attempt and its mastery row (Captain, 2026-11-02; departure 26).
/// Unlike the source, every read and
/// write is bound to the launch owner and refused when that owner is absent
/// or no longer active, and a learner whose target language is not Korean
/// gets a message instead of a Korean drill.
/// </summary>
public sealed class NativeNumberDrillPresenter(
    NativeActivityOwner owner,
    NativeActivityTimer timer,
    IServiceScopeFactory scopes,
    INativeQuizMedia media,
    NativePendingWrites pendingWrites,
    ILogger<NativeNumberDrillPresenter> logger) : IDisposable
{
    public const string Route = NativeActivityCatalog.NumberDrill;
    public const string LanguageCode = "ko";
    public const int MinItems = 5;
    public const int MaxItems = 20;
    public const int ItemStep = 5;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Stopwatch _itemClock = new();
    private readonly List<int> _latencies = [];
    private readonly Dictionary<string, int> _errorClasses = new(StringComparer.Ordinal);
    private NativeActivityLaunch? _launch;
    private NativeNumberDrillState _state = new(NativePhase.Idle);
    private List<NativeNumberChoice> _allSubModes = [];
    private IServiceScope? _sessionScope;
    private NumberSessionService? _sessions;
    private NumberSession? _session;
    private NumberItem? _item;
    private string? _lastErrorClass;
    private NumberAttemptReceipt? _lastReceipt;
    private long _advance;
    private bool _disposed;
    private bool _exited;

    /// <summary>Source auto-advance after a tap, a time card, or an override: 1500 ms.</summary>
    public TimeSpan AutoAdvanceDelay { get; set; } = TimeSpan.FromMilliseconds(1500);

    public NativeNumberDrillState State =>
        _launch is not null && !owner.IsActive(_launch.UserId)
            && _state.Destination != NativeDestination.TodaysPlan
            ? new(NativePhase.Blocked, ErrorKey: NativeNumberDrillText.Unavailable)
            : _state;

    public event Action<NativeNumberDrillState>? StateChanged;

    public async Task<NativeNumberDrillState> OpenAsync(
        NativeActivityLaunch launch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launch);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_launch is not null)
                return State;
            _launch = launch;
            Publish(new(NativePhase.Loading));
            if (launch.Route != Route || !owner.IsActive(launch.UserId))
                return Refuse();
            // Source NumberDrill ignores ResourceId and the DueOnly query
            // (activities.md 12); the checkbox starts off.
            if (launch.ResourceIds is { IsDefaultOrEmpty: false })
                logger.LogInformation("Number Drill ignores its launch resource (vocabulary-driven).");

            bool korean;
            try
            {
                korean = await IsKoreanLearnerAsync(launch.UserId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Number Drill could not read the learner's target language.");
                return Publish(new(NativePhase.Error, ErrorKey: NativeNumberDrillText.LoadFailed));
            }
            if (!owner.IsActive(launch.UserId))
                return Refuse();
            if (!korean)
                return Publish(new(NativePhase.Empty, ErrorKey: NativeNumberDrillText.KoreanOnly));

            if (!await timer.StartAsync(launch, cancellationToken))
                return Refuse();
            return await LoadSetupCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<NativeNumberDrillState> SelectContextAsync(
        string? contextCode, CancellationToken cancellationToken = default) =>
        SetupAsync(state =>
        {
            if (contextCode is not null
                && (state.Contexts.IsDefault || !state.Contexts.Any(c => c.Code == contextCode)))
                return state;
            var subMode = state.SelectedSubMode;
            // Source OnContextSelected: reset an invalid sub-mode to the first valid one.
            if (!string.IsNullOrEmpty(subMode) && !IsValidCombo(contextCode, subMode))
                subMode = _allSubModes.FirstOrDefault(m => IsValidCombo(contextCode, m.Code))?.Code ?? "";
            return state with
            {
                SelectedContext = contextCode,
                SelectedSubMode = subMode,
                SubModes = ValidSubModes(contextCode),
            };
        }, cancellationToken);

    public Task<NativeNumberDrillState> SelectSubModeAsync(
        string subModeCode, CancellationToken cancellationToken = default) =>
        SetupAsync(state => state.SubModes.IsDefault
            || !state.SubModes.Any(m => m.Code == subModeCode)
                ? state : state with { SelectedSubMode = subModeCode }, cancellationToken);

    /// <summary>Source stepper: 5..20 in steps of 5.</summary>
    public Task<NativeNumberDrillState> ChangeItemCountAsync(
        int direction, CancellationToken cancellationToken = default) =>
        SetupAsync(state => state with
        {
            ItemCount = Math.Clamp(state.ItemCount + Math.Sign(direction) * ItemStep,
                MinItems, MaxItems)
        }, cancellationToken);

    public Task<NativeNumberDrillState> SetDueOnlyAsync(
        bool dueOnly, CancellationToken cancellationToken = default) =>
        SetupAsync(state => state with { DueOnly = dueOnly }, cancellationToken);

    public async Task<NativeNumberDrillState> StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!TryOwner(out var userId))
                return Refuse();
            var setup = _state;
            if (setup.Phase != NativePhase.Ready || setup.Stage != NativeNumberDrillStage.Setup
                || setup.IsBusy)
                return State;
            Publish(setup with { IsBusy = true, ErrorKey = null });
            ReleaseSession();
            try
            {
                _sessionScope = scopes.CreateScope();
                _sessions = _sessionScope.ServiceProvider.GetRequiredService<NumberSessionService>();
                var session = await _sessions.StartSessionAsync(userId, LanguageCode,
                    new NumberSessionRequest(
                        ContextCode: setup.SelectedContext,
                        SubModeCode: setup.SelectedSubMode,
                        ItemCount: setup.ItemCount,
                        DueOnly: setup.DueOnly,
                        Difficulty: 1), cancellationToken);
                if (!owner.IsActive(userId))
                    return Refuse();
                if (session.Items.Count == 0)
                {
                    await _sessions.EndSessionAsync(session.SessionId, cancellationToken);
                    ReleaseSession();
                    return Publish(setup with { IsBusy = false, ErrorKey = NativeNumberDrillText.NoItems });
                }
                _session = session;
                _latencies.Clear();
                _errorClasses.Clear();
                return ShowItem(0, correctCount: 0);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Number Drill could not start a session.");
                ReleaseSession();
                return Publish(setup with { IsBusy = false, ErrorKey = NativeNumberDrillText.StartFailed });
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Typed answer changes; no publish so the text field and Korean IME composition are not rebuilt.</summary>
    public NativeNumberDrillState UpdateAnswer(string? answer)
    {
        if (_state.Stage == NativeNumberDrillStage.InSession && _state.Phase == NativePhase.Ready)
            _state = _state with { Answer = answer ?? string.Empty };
        return State;
    }

    /// <summary>Source PlayAudioAsync: cached or synthesized TTS of the item's audio cue; offline and uncached says so.</summary>
    public async Task<NativeNumberDrillState> PlayAudioAsync(CancellationToken cancellationToken = default)
    {
        string cue;
        string userId;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!TryOwner(out userId))
                return Refuse();
            if (_state.IsPlayingAudio || _state.Stage != NativeNumberDrillStage.InSession
                || _item is null || string.IsNullOrWhiteSpace(_item.AudioCue))
                return State;
            cue = _item.AudioCue;
            Publish(_state with { IsPlayingAudio = true, NoticeKey = null });
        }
        finally
        {
            _gate.Release();
        }

        string? notice = null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _lifetime.Token);
        try
        {
            if (!await media.CanPlayPromptAsync(cue, LanguageCode, linked.Token))
                notice = NativeNumberDrillText.AudioOffline;
            else if (!await media.PlayPromptAsync(cue, LanguageCode, linked.Token)
                && !linked.IsCancellationRequested)
                notice = NativeNumberDrillText.AudioFailed;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Number Drill could not play the item audio.");
            notice = NativeNumberDrillText.AudioFailed;
        }
        if (_lifetime.IsCancellationRequested)
            return _state;

        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            if (!owner.IsActive(userId))
                return Refuse();
            if (_state.Destination != NativeDestination.Activity)
                return State;
            return Publish(_state with { IsPlayingAudio = false, NoticeKey = notice ?? _state.NoticeKey });
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>ListenAndType / ReadAndProduce manual Submit.</summary>
    public async Task<NativeNumberDrillState> SubmitAnswerAsync(
        string? answer = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!TryOwner(out var userId))
                return Refuse();
            if (answer is not null)
                UpdateAnswer(answer);
            var state = _state;
            if (!state.CanSubmitText || _item is null
                || !NativeNumberSubMode.IsTextEntry(_item.SubModeCode))
                return State;
            var typed = state.Answer.Trim();
            var result = await RecordAsync(userId, typed, cancellationToken);
            if (result is null)
                return State;
            return ShowFeedback(FromGrade(result), result.Grade.IsCorrect);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>TapTheCounter chip or ListenAndPlace time card: graded immediately, auto-advance after 1500 ms.</summary>
    public async Task<NativeNumberDrillState> TapChoiceAsync(
        string choice, CancellationToken cancellationToken = default)
    {
        long advance;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!TryOwner(out var userId))
                return Refuse();
            if (_state.Phase != NativePhase.Ready || _state.Stage != NativeNumberDrillStage.InSession
                || _state.IsBusy || _item is not { } item
                || item.SubModeCode is not (NativeNumberSubMode.TapTheCounter
                    or NativeNumberSubMode.ListenAndPlace)
                || item.CounterChoices is not { } choices || !choices.Contains(choice))
                return State;
            var result = await RecordAsync(userId, choice, cancellationToken);
            if (result is null)
                return State;
            ShowFeedback(FromGrade(result), result.Grade.IsCorrect);
            advance = ++_advance;
        }
        finally
        {
            _gate.Release();
        }
        return await AutoAdvanceAsync(advance);
    }

    public Task<NativeNumberDrillState> SelectPairedAnswerAsync(
        bool promptA, string choice, CancellationToken cancellationToken = default) =>
        MutateAsync(state =>
        {
            if (state.Phase != NativePhase.Ready || state.Stage != NativeNumberDrillStage.InSession
                || _item?.SubModeCode != NativeNumberSubMode.Disambiguate)
                return state;
            var choices = promptA ? _item.ChoicesA : _item.ChoicesB;
            if (choices is null || !choices.Contains(choice))
                return state;
            return promptA ? state with { SelectedAnswerA = choice }
                : state with { SelectedAnswerB = choice };
        }, cancellationToken);

    /// <summary>Disambiguate: both-or-nothing grading, one combined attempt.</summary>
    public async Task<NativeNumberDrillState> SubmitBothAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!TryOwner(out var userId))
                return Refuse();
            var state = _state;
            if (!state.CanSubmitBoth || _item is not { } item
                || item.SubModeCode != NativeNumberSubMode.Disambiguate)
                return State;
            var correctA = state.SelectedAnswerA == item.CorrectAnswerA;
            var correctB = state.SelectedAnswerB == item.CorrectAnswerB;
            var both = correctA && correctB;
            var combined = $"{state.SelectedAnswerA} | {state.SelectedAnswerB}";
            var result = await RecordAsync(userId, combined, cancellationToken);
            if (result is null)
                return State;
            // Source builds its own feedback for the pair (NumberDrill.razor:1115-1127).
            var feedback = new NativeNumberDrillFeedback(
                IsCorrect: both,
                Verdict: null,
                VerdictKey: both ? NativeNumberDrillText.BothCorrect
                    : correctA || correctB ? NativeNumberDrillText.PartialCorrect
                    : NativeNumberDrillText.BothIncorrect,
                UserAnswer: combined,
                DigitDisplay: item.DigitDisplay,
                CanonicalAnswer: $"A: {item.CorrectAnswerA}, B: {item.CorrectAnswerB}",
                ErrorClass: both ? null : "system_confusion",
                Tip: null,
                TipKey: both ? null : NativeNumberDrillText.DisambiguateTip,
                ProgressCorrect: result.Snapshot.CorrectCount,
                ProgressTotal: result.Snapshot.TotalCount,
                NextReviewUtc: result.Snapshot.NextDueDate,
                Overridden: false,
                CorrectA: correctA,
                CorrectB: correctB);
            return ShowFeedback(feedback, both);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// "I was correct": re-grades the item's stored attempt as correct and
    /// corrects its long-term mastery (SM-2) through the shared service
    /// (Captain, 2026-11-02; the source only flipped the on-screen grade).
    /// On success it flips the on-screen grade, the local correct count and
    /// the summary patterns, logs the grader miss, then auto-advances after
    /// 1500 ms. When the correction cannot be stored it says so, keeps the
    /// item graded as stored, and the learner continues with Next.
    /// </summary>
    public async Task<NativeNumberDrillState> OverrideAsync(CancellationToken cancellationToken = default)
    {
        long advance;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!TryOwner(out var userId))
                return Refuse();
            var state = _state;
            if (state.Phase != NativePhase.Feedback || state.IsOverriding
                || state.Feedback is not { IsCorrect: false } feedback || _item is not { } item)
                return State;
            logger.LogInformation(
                "Number Drill grader override: subMode={SubMode} context={Context} system={System} " +
                "counter={Counter} digitValue={DigitValue} errorClass={ErrorClass}",
                item.SubModeCode, item.ContextCode, item.System, item.CounterId ?? "(none)",
                item.DigitValue, feedback.ErrorClass ?? "(none)");
            Publish(state with { IsOverriding = true, ErrorKey = null });
            NumberOverrideResult? stored = null;
            try
            {
                if (_sessions is { } sessions && _lastReceipt is { } receipt)
                    stored = await sessions.OverrideAttemptAsync(userId, receipt, cancellationToken);
                else
                    logger.LogWarning("Number Drill override has no stored attempt to correct.");
            }
            catch (OperationCanceledException)
            {
                Publish(state with { IsOverriding = false });
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Number Drill could not store the override.");
            }
            if (!owner.IsActive(userId))
                return Refuse();
            if (stored is not { Outcome: NumberOverrideOutcome.Applied or NumberOverrideOutcome.AlreadyCorrect })
                return Publish(state with
                {
                    IsOverriding = false,
                    ErrorKey = NativeNumberDrillText.OverrideFailed,
                    HasPendingSync = pendingWrites.IsPending(userId),
                });
            _lastReceipt = null;
            if (_lastErrorClass is { } error && _errorClasses.TryGetValue(error, out var count))
            {
                if (count <= 1)
                    _errorClasses.Remove(error);
                else
                    _errorClasses[error] = count - 1;
            }
            _lastErrorClass = null;
            Publish(state with
            {
                IsOverriding = true,
                CorrectCount = state.CorrectCount + 1,
                Feedback = feedback with
                {
                    IsCorrect = true, Overridden = true,
                    Verdict = null, VerdictKey = NativeNumberDrillText.OverriddenVerdict,
                    ProgressCorrect = stored.Snapshot?.CorrectCount ?? feedback.ProgressCorrect,
                    ProgressTotal = stored.Snapshot?.TotalCount ?? feedback.ProgressTotal,
                    NextReviewUtc = stored.Snapshot?.NextDueDate ?? feedback.NextReviewUtc,
                },
                HasPendingSync = pendingWrites.IsPending(userId),
            });
            advance = ++_advance;
        }
        finally
        {
            _gate.Release();
        }
        return await AutoAdvanceAsync(advance);
    }

    /// <summary>Next / Finish.</summary>
    public async Task<NativeNumberDrillState> NextAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!TryOwner(out _))
                return Refuse();
            if (_state.Phase != NativePhase.Feedback)
                return State;
            _advance++;
            return await AdvanceCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>"Another round": back to Setup with the same choices; the timer keeps running.</summary>
    public async Task<NativeNumberDrillState> TryAgainAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!TryOwner(out _))
                return Refuse();
            if (_state.Stage != NativeNumberDrillStage.Summary)
                return State;
            ReleaseSession();
            return Publish(SetupState(_state));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Back / Done / Go Back: stop and save the timer, then return to the Dashboard.</summary>
    public async Task<NativeNumberDrillState> ExitAsync(CancellationToken cancellationToken = default)
    {
        _advance++;
        media.StopPlayback();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _advance++;
            _exited = true;
            string? errorKey = null;
            try
            {
                if (await timer.StopAsync(cancellationToken) == NativeActivityStop.Failed)
                    errorKey = NativeText.SaveFailed;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Number Drill could not save its activity minutes.");
                errorKey = NativeText.SaveFailed;
            }
            if (_session is not null && _sessions is not null)
            {
                try
                {
                    await _sessions.EndSessionAsync(_session.SessionId, cancellationToken);
                }
                catch (InvalidOperationException)
                {
                    // Already ended.
                }
            }
            ReleaseSession();
            return Publish(new(NativePhase.Ready, NativeDestination.TodaysPlan,
                ErrorKey: errorKey,
                HasPendingSync: _launch is not null && pendingWrites.IsPending(_launch.UserId)));
        }
        finally
        {
            _gate.Release();
        }
    }

    // Source IsValidCombo (NumberDrill.razor:630-646).
    public static bool IsValidCombo(string? contextCode, string subModeCode)
    {
        var any = string.IsNullOrEmpty(contextCode);
        if (any && subModeCode is NativeNumberSubMode.TapTheCounter or NativeNumberSubMode.ListenAndPlace)
            return false;
        if (subModeCode == NativeNumberSubMode.TapTheCounter && contextCode != "Counting")
            return false;
        if (subModeCode == NativeNumberSubMode.ListenAndPlace && contextCode != "Time")
            return false;
        return true;
    }

    private static readonly string[] SinoDigits =
        ["영", "일", "이", "삼", "사", "오", "육", "칠", "팔", "구", "십"];

    private static readonly string[] NativeNumbers =
        ["하나", "한", "둘", "두", "셋", "세", "넷", "네", "다섯", "여섯", "일곱", "여덟", "아홉", "열",
         "스물", "스무", "서른", "마흔", "쉰", "예순", "일흔", "여든", "아흔"];

    /// <summary>Source GenerateSpacingHint: "5시 30분" becomes "___시 ___분".</summary>
    public static string SpacingHint(string? answer)
    {
        if (string.IsNullOrEmpty(answer))
            return "___";
        var hint = Regex.Replace(answer, @"\d+", "___");
        foreach (var digit in SinoDigits)
            hint = hint.Replace(digit, "", StringComparison.Ordinal);
        foreach (var number in NativeNumbers.OrderByDescending(n => n.Length))
            hint = hint.Replace(number, "", StringComparison.Ordinal);
        hint = Regex.Replace(hint, @"\s+", " ").Trim();
        if (string.IsNullOrWhiteSpace(hint)
            || !hint.Contains('시') && !hint.Contains('분') && !hint.Contains('살')
                && !hint.Contains('개') && !hint.Contains('명'))
            return "___";
        return Regex.Replace(hint, "(시|분|초|살|개|명|마리|권|잔|병|장|번|원|년|월|일)", "___$1");
    }

    private async Task<bool> IsKoreanLearnerAsync(string userId, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var target = await db.UserProfiles.AsNoTracking()
            .Where(profile => profile.Id == userId)
            .Select(profile => profile.TargetLanguage)
            .SingleOrDefaultAsync(cancellationToken);
        return string.Equals(target, "Korean", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Source LoadSetupDataAsync, after seeding the shared reference content as the MAUI heads do at startup.</summary>
    private async Task<NativeNumberDrillState> LoadSetupCoreAsync(CancellationToken cancellationToken)
    {
        var userId = _launch!.UserId;
        try
        {
            using (var seedScope = scopes.CreateScope())
            {
                try
                {
                    await seedScope.ServiceProvider.GetRequiredService<NumberContentSeeder>()
                        .SeedAsync(LanguageCode, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Number content seeding failed; the drill picker may be empty.");
                }
            }
            if (!owner.IsActive(userId))
                return Refuse();
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var contexts = await db.NumberContexts.AsNoTracking()
                .Where(context => context.IsActive)
                .OrderBy(context => context.SortOrder)
                .Select(context => new NativeNumberChoice(context.Code, context.DisplayName))
                .ToListAsync(cancellationToken);
            // The source query has no ORDER BY, so SQLite returns key order; keep
            // the seed order (lib/content/numbers/ko.json) deterministic instead.
            _allSubModes = (await db.NumberSubModes.AsNoTracking()
                    .Where(mode => mode.IsActive && mode.Phase <= 2)
                    .Select(mode => new { mode.Code, mode.DisplayName, mode.Phase })
                    .ToListAsync(cancellationToken))
                .OrderBy(mode => mode.Phase)
                .ThenBy(mode => SeedOrder(mode.Code))
                .ThenBy(mode => mode.Code, StringComparer.Ordinal)
                .Select(mode => new NativeNumberChoice(mode.Code, mode.DisplayName))
                .ToList();
            if (!owner.IsActive(userId))
                return Refuse();
            var selected = NativeNumberSubMode.ListenAndType;
            if (_allSubModes.Count > 0 && string.IsNullOrEmpty(selected))
                selected = _allSubModes[0].Code;
            return Publish(new(NativePhase.Ready,
                Contexts: [.. contexts],
                SubModes: ValidSubModes(null),
                SelectedContext: null,
                SelectedSubMode: selected,
                HasPendingSync: pendingWrites.IsPending(userId)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Number Drill could not load its setup.");
            // Source keeps the setup card with its error text.
            return Publish(new(NativePhase.Ready, Contexts: [], SubModes: [],
                ErrorKey: NativeNumberDrillText.LoadFailed));
        }
    }

    private static int SeedOrder(string code) => code switch
    {
        NativeNumberSubMode.ListenAndType => 0,
        NativeNumberSubMode.ReadAndProduce => 1,
        NativeNumberSubMode.TapTheCounter => 2,
        NativeNumberSubMode.Disambiguate => 3,
        NativeNumberSubMode.ListenAndPlace => 4,
        _ => 5,
    };

    private ImmutableArray<NativeNumberChoice> ValidSubModes(string? context) =>
        [.. _allSubModes.Where(mode => IsValidCombo(context, mode.Code))];

    private async Task<NumberAttemptResult?> RecordAsync(
        string userId, string answer, CancellationToken cancellationToken)
    {
        var latency = (int)_itemClock.ElapsedMilliseconds;
        Publish(_state with { IsBusy = true, ErrorKey = null });
        try
        {
            var result = await _sessions!.SubmitAnswerAsync(
                userId, _session!.SessionId, _item!, answer, latency, cancellationToken);
            if (!owner.IsActive(userId))
            {
                Refuse();
                return null;
            }
            _itemClock.Stop();
            _latencies.Add(latency);
            _lastErrorClass = null;
            _lastReceipt = result.Receipt;
            if (!result.Grade.IsCorrect)
            {
                var error = string.IsNullOrEmpty(result.Grade.ErrorClass)
                    ? null : result.Grade.ErrorClass;
                if (_item!.SubModeCode == NativeNumberSubMode.Disambiguate)
                    error = "system_confusion";
                if (error is not null)
                {
                    _errorClasses[error] = _errorClasses.GetValueOrDefault(error) + 1;
                    _lastErrorClass = error;
                }
            }
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Number Drill could not record an attempt.");
            Publish(_state with { IsBusy = false, ErrorKey = NativeNumberDrillText.SubmitFailed });
            return null;
        }
    }

    private NativeNumberDrillFeedback FromGrade(NumberAttemptResult result) => new(
        IsCorrect: result.Grade.IsCorrect,
        Verdict: result.Grade.Verdict,
        VerdictKey: null,
        UserAnswer: result.Grade.UserAnswer,
        DigitDisplay: _item!.DigitDisplay,
        CanonicalAnswer: result.Grade.CanonicalAnswer,
        ErrorClass: result.Grade.IsCorrect ? null : result.Grade.ErrorClass,
        Tip: result.Grade.IsCorrect ? null : result.Grade.Tip,
        TipKey: null,
        ProgressCorrect: result.Snapshot.CorrectCount,
        ProgressTotal: result.Snapshot.TotalCount,
        NextReviewUtc: result.Snapshot.NextDueDate,
        Overridden: false);

    private NativeNumberDrillState ShowFeedback(NativeNumberDrillFeedback feedback, bool correct) =>
        Publish(_state with
        {
            Phase = NativePhase.Feedback,
            IsBusy = false,
            Feedback = feedback,
            CorrectCount = _state.CorrectCount + (correct ? 1 : 0),
            HasPendingSync = pendingWrites.IsPending(_launch!.UserId),
        });

    private async Task<NativeNumberDrillState> AutoAdvanceAsync(long advance)
    {
        try
        {
            if (AutoAdvanceDelay > TimeSpan.Zero)
                await Task.Delay(AutoAdvanceDelay, _lifetime.Token);
            await _gate.WaitAsync(_lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            return _state;
        }
        try
        {
            // Next, Back, or another override during the pause supersedes it.
            if (advance != _advance || _state.Phase != NativePhase.Feedback
                || !TryOwner(out _))
                return State;
            return await AdvanceCoreAsync(_lifetime.Token);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<NativeNumberDrillState> AdvanceCoreAsync(CancellationToken cancellationToken)
    {
        var next = _state.ItemIndex + 1;
        if (_session is not null && next < _session.Items.Count)
            return ShowItem(next, _state.CorrectCount);
        return await EndCoreAsync(cancellationToken);
    }

    /// <summary>Source EndSessionAsync; the counts are the page's local counts.</summary>
    private async Task<NativeNumberDrillState> EndCoreAsync(CancellationToken cancellationToken)
    {
        var state = _state;
        try
        {
            if (_session is not null && _sessions is not null)
                await _sessions.EndSessionAsync(_session.SessionId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Number Drill could not end its session.");
            return Publish(state with { ErrorKey = NativeNumberDrillText.EndFailed });
        }
        // The shared service's Phase 1 summary carries no latency or error
        // classes, so the session's own attempts fill the source fields.
        var summary = new NativeNumberDrillSummary(
            state.CorrectCount, _latencies.Count, Median(_latencies),
            [.. _errorClasses.OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)]);
        ReleaseSession();
        return Publish(state with
        {
            Phase = NativePhase.Summary,
            Stage = NativeNumberDrillStage.Summary,
            Item = null,
            Feedback = null,
            Summary = summary,
            IsBusy = false,
            IsOverriding = false,
            IsPlayingAudio = false,
            NoticeKey = null,
        });
    }

    private static int Median(List<int> values)
    {
        if (values.Count == 0)
            return 0;
        var sorted = values.Order().ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle]
            : (int)Math.Round((sorted[middle - 1] + (long)sorted[middle]) / 2.0);
    }

    private NativeNumberDrillState ShowItem(int index, int correctCount)
    {
        var item = _session!.Items[index];
        _item = item;
        _lastErrorClass = null;
        _lastReceipt = null;
        _itemClock.Restart();
        media.StopPlayback();
        return Publish(_state with
        {
            Phase = NativePhase.Ready,
            Stage = NativeNumberDrillStage.InSession,
            Item = Project(item),
            ItemIndex = index,
            ItemTotal = _session.Items.Count,
            CorrectCount = correctCount,
            Answer = string.Empty,
            SelectedAnswerA = null,
            SelectedAnswerB = null,
            Feedback = null,
            IsBusy = false,
            IsOverriding = false,
            IsPlayingAudio = false,
            ErrorKey = null,
            NoticeKey = null,
        });
    }

    private static NativeNumberDrillItem Project(NumberItem item) => new(
        item.ContextCode, item.SubModeCode, item.System.ToString(), item.DisplayPrompt,
        item.NounCue,
        [.. item.Hints ?? []],
        [.. item.CounterChoices ?? []],
        item.PromptA, item.PromptB,
        [.. item.ChoicesA ?? []],
        [.. item.ChoicesB ?? []],
        item.HintA, item.HintB,
        SpacingHint(item.CanonicalAnswer),
        HasAudio: !string.IsNullOrWhiteSpace(item.AudioCue));

    private static NativeNumberDrillState SetupState(NativeNumberDrillState state) => state with
    {
        Phase = NativePhase.Ready,
        Stage = NativeNumberDrillStage.Setup,
        Item = null,
        ItemIndex = 0,
        ItemTotal = 0,
        CorrectCount = 0,
        Answer = string.Empty,
        SelectedAnswerA = null,
        SelectedAnswerB = null,
        Feedback = null,
        Summary = null,
        IsBusy = false,
        IsOverriding = false,
        IsPlayingAudio = false,
        ErrorKey = null,
        NoticeKey = null,
    };

    private Task<NativeNumberDrillState> SetupAsync(
        Func<NativeNumberDrillState, NativeNumberDrillState> change,
        CancellationToken cancellationToken) =>
        MutateAsync(state => state.Phase == NativePhase.Ready
            && state.Stage == NativeNumberDrillStage.Setup && !state.IsBusy
                ? change(state) : state, cancellationToken);

    private async Task<NativeNumberDrillState> MutateAsync(
        Func<NativeNumberDrillState, NativeNumberDrillState> change,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!TryOwner(out _))
                return Refuse();
            var next = change(_state);
            return ReferenceEquals(next, _state) ? State : Publish(next);
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool TryOwner(out string userId)
    {
        userId = _launch?.UserId ?? string.Empty;
        return _launch is not null && !_exited && owner.IsActive(userId)
            && _state.Destination == NativeDestination.Activity
            && _state.Phase is not (NativePhase.Blocked or NativePhase.Idle);
    }

    private void ReleaseSession()
    {
        _session = null;
        _item = null;
        _lastReceipt = null;
        _sessions = null;
        _sessionScope?.Dispose();
        _sessionScope = null;
    }

    private NativeNumberDrillState Refuse()
    {
        _advance++;
        // After Back every command is ignored; the exit state stays.
        if (_exited)
            return State;
        return Publish(new(NativePhase.Blocked, ErrorKey: NativeNumberDrillText.Unavailable));
    }

    private NativeNumberDrillState Publish(NativeNumberDrillState state)
    {
        _state = state;
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
        ReleaseSession();
        timer.Dispose();
    }
}
