using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models;
using SentenceStudio.Shared.Services;

namespace SentenceStudio.Native;

/// <summary>
/// Writing (source UI/Pages/Writing.razor, activities.md 6). Four
/// target-language Word blocks, free sentence entry, AI grading with the
/// source GradeSentence prompt, a translate helper, the in-session list of
/// graded sentences, "End session" after three, and a summary with Continue /
/// Done. Every graded sentence writes a Writer UserActivity and scores the
/// loaded vocabulary through ExtractAndScoreVocabularyAsync (weight 1.5).
/// Unlike the source, every read and write is bound to the launch owner and
/// refused when that owner is absent or no longer active.
/// </summary>
public sealed class NativeWritingPresenter(
    NativeActivityOwner owner,
    NativeActivityTimer timer,
    LearningResourceRepository resources,
    INativeWritingAi ai,
    IServiceScopeFactory scopes,
    NativePendingWrites pendingWrites,
    ILogger<NativeWritingPresenter> logger) : IDisposable
{
    public const int BlockCount = 4;
    public const string ScoringActivity = "Writing";
    public const float DifficultyWeight = 1.5f;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<VocabularyWord> _vocabulary = [];
    private readonly List<NativeWritingSentence> _sentences = [];
    private List<VocabularyWord> _blocks = [];
    private NativeActivityLaunch? _launch;
    private NativeWritingState _state = new(NativePhase.Idle);
    private ImmutableArray<NativeWritingNotice> _notices = [];
    private string _draft = string.Empty;
    private bool _usingFocus;
    private bool _grading;
    private bool _translating;
    private int _graded;
    private double _accuracySum;
    private double _fluencySum;
    private long _generation;
    private bool _disposed;

    public NativeWritingState State =>
        _launch is not null && !owner.IsActive(_launch.UserId)
            && _state.Destination != NativeDestination.TodaysPlan
            ? new(NativePhase.Blocked, ErrorKey: NativeWritingText.Unavailable)
            : _state;

    public event Action<NativeWritingState>? StateChanged;

    public async Task<NativeWritingState> OpenAsync(
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
            if (launch.Route != NativeActivityCatalog.Writing
                || !owner.IsActive(launch.UserId)
                || !await timer.StartAsync(launch, cancellationToken))
                return Refuse();
            return await LoadCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Typing updates the draft without publishing, so a re-render never
    /// interrupts IME composition; heads refresh Grade/Translate availability
    /// from <see cref="State"/> themselves.
    /// </summary>
    public void SetDraft(string? value)
    {
        _draft = value ?? string.Empty;
        _state = _state with { Draft = _draft };
    }

    /// <summary>Source UseVocab: tapping a block appends its word to the sentence.</summary>
    public async Task<NativeWritingState> UseBlockAsync(
        int index, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Exited)
                return State;
            if (!Active())
                return Refuse();
            if (_state.Phase != NativePhase.Ready || index < 0 || index >= _blocks.Count)
                return State;
            var word = _blocks[index].TargetLanguageTerm ?? string.Empty;
            _draft = string.IsNullOrEmpty(_draft) ? word : $"{_draft} {word}";
            return PublishReady();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Source ShuffleVocab: four random blocks (focus mode keeps the first four).</summary>
    public async Task<NativeWritingState> ShuffleAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Exited)
                return State;
            if (!Active())
                return Refuse();
            if (_state.Phase != NativePhase.Ready)
                return State;
            Shuffle();
            return PublishReady();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<NativeWritingState> DismissNoticesAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Exited)
                return State;
            if (!Active())
                return Refuse();
            _notices = [];
            return Publish(_state with { Notices = _notices });
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Source GradeMe. The AI call runs outside the gate so Back stays
    /// responsive; an exit or owner change while it runs discards the result
    /// without writing anything.
    /// </summary>
    public async Task<NativeWritingState> GradeAsync(CancellationToken cancellationToken = default)
    {
        string sentence;
        string userId;
        long generation;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Exited)
                return State;
            if (!Active())
                return Refuse();
            if (_state.Phase != NativePhase.Ready || _grading
                || string.IsNullOrWhiteSpace(_draft))
                return State;
            _grading = true;
            _notices = [];
            sentence = _draft;
            userId = _launch!.UserId;
            generation = _generation;
            PublishReady();
        }
        finally
        {
            _gate.Release();
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _lifetime.Token);
        GradeResponse? grade = null;
        Exception? failure = null;
        try
        {
            var (target, native) = await LanguagesAsync(userId);
            grade = await ai.GradeAsync(userId, sentence, target, native, linked.Token);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            failure = null;
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        try
        {
            await _gate.WaitAsync(_lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            return _state;
        }
        try
        {
            _grading = false;
            if (generation != _generation || Exited)
                return State;
            if (!Active())
                return Refuse();
            if (linked.IsCancellationRequested)
                return PublishReady();
            if (failure is not null || grade is null)
            {
                if (failure is not null)
                    logger.LogError(failure, "Writing could not grade a sentence.");
                else
                    logger.LogWarning("Writing grading returned no verdict.");
                _notices = [new(NativeWritingNoticeKind.Error, NativeWritingText.GradeFailed)];
                return PublishReady();
            }
            return await ApplyGradeAsync(userId, sentence, grade);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Source TranslationService.TranslateAsync: the owner's own vocabulary
    /// first (target then native term), else the AI Translate prompt, whose
    /// result the source saves as a new word for the active owner.
    /// </summary>
    public async Task<NativeWritingState> TranslateAsync(
        CancellationToken cancellationToken = default)
    {
        string text;
        string userId;
        long generation;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Exited)
                return State;
            if (!Active())
                return Refuse();
            if (_state.Phase != NativePhase.Ready || _translating
                || string.IsNullOrWhiteSpace(_draft))
                return State;
            _translating = true;
            text = _draft;
            userId = _launch!.UserId;
            generation = _generation;
            PublishReady();
        }
        finally
        {
            _gate.Release();
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _lifetime.Token);
        string? translation = null;
        var cached = false;
        Exception? failure = null;
        try
        {
            var existing = await resources.GetWordByTargetTermAsync(text, userId);
            if (!string.IsNullOrEmpty(existing?.NativeLanguageTerm))
                (translation, cached) = (existing.NativeLanguageTerm, true);
            else
            {
                existing = await resources.GetWordByNativeTermAsync(text, userId);
                if (!string.IsNullOrEmpty(existing?.TargetLanguageTerm))
                    (translation, cached) = (existing.TargetLanguageTerm, true);
                else if (owner.IsActive(userId))
                    translation = await ai.TranslateAsync(userId, text, linked.Token);
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        try
        {
            await _gate.WaitAsync(_lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            return _state;
        }
        try
        {
            _translating = false;
            if (generation != _generation || Exited)
                return State;
            if (!Active())
                return Refuse();
            if (linked.IsCancellationRequested)
                return PublishReady();
            if (failure is not null || string.IsNullOrWhiteSpace(translation))
            {
                if (failure is not null)
                    logger.LogError(failure, "Writing could not translate the sentence.");
                _notices = [new(NativeWritingNoticeKind.Error, NativeWritingText.TranslateFailed)];
                return PublishReady();
            }
            if (!cached)
                await SaveTranslationAsync(userId, text, translation);
            _notices = [new(NativeWritingNoticeKind.Info, null, translation)];
            return PublishReady();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Source "End session" (three or more graded sentences).</summary>
    public async Task<NativeWritingState> EndSessionAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Exited)
                return State;
            if (!Active())
                return Refuse();
            if (!_state.CanEndSession)
                return State;
            _notices = [];
            return Publish(Snapshot(NativePhase.Summary));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Source ContinueAfterSummary: clear the session and reload vocabulary; the timer keeps running.</summary>
    public async Task<NativeWritingState> ContinueAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Exited)
                return State;
            if (!Active())
                return Refuse();
            if (_state.Phase != NativePhase.Summary)
                return State;
            _generation++;
            _sentences.Clear();
            _graded = 0;
            _accuracySum = 0;
            _fluencySum = 0;
            Publish(new(NativePhase.Loading));
            return await LoadCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Back / Done / Go Back: stop and save the timer, then return to the Dashboard.</summary>
    public async Task<NativeWritingState> ExitAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _generation++;
            string? errorKey = null;
            try
            {
                await timer.StopAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Writing could not save its activity minutes.");
                errorKey = NativeText.SaveFailed;
            }
            return Publish(new(NativePhase.Ready, NativeDestination.TodaysPlan,
                ErrorKey: errorKey,
                HasPendingSync: _launch is not null && pendingWrites.IsPending(_launch.UserId)));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<NativeWritingState> ApplyGradeAsync(
        string userId, string sentence, GradeResponse grade)
    {
        var notices = new List<NativeWritingNotice>();
        _sentences.Add(new NativeWritingSentence(
            _sentences.Count, sentence, grade.Accuracy, grade.Fluency,
            grade.GrammarNotes?.RecommendedTranslation,
            grade.AccuracyExplanation, grade.FluencyExplanation,
            grade.GrammarNotes?.Explanation));
        _graded++;
        _accuracySum += grade.Accuracy;
        _fluencySum += grade.Fluency;

        var saveFailed = false;
        using (var scope = scopes.CreateScope())
        {
            try
            {
                var activities = scope.ServiceProvider.GetRequiredService<UserActivityRepository>();
                var now = DateTime.UtcNow;
                // UserActivity.CreatedAt is a UTC instant (ProgressService.ComputeStreak).
                var saved = await activities.SaveAsync(new UserActivity
                {
                    Activity = Activity.Writer.ToString(),
                    Input = sentence,
                    Accuracy = grade.Accuracy,
                    Fluency = grade.Fluency,
                    UserProfileId = userId,
                    CreatedAt = now,
                    UpdatedAt = now
                });
                if (saved < 0)
                {
                    logger.LogError("Writing could not save its UserActivity row.");
                    saveFailed = true;
                }
                else
                    pendingWrites.MarkPending(userId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Writing could not save its UserActivity row.");
                saveFailed = true;
            }

            if (owner.IsActive(userId))
            {
                try
                {
                    var progress = scope.ServiceProvider.GetRequiredService<IVocabularyProgressService>();
                    var results = await progress.ExtractAndScoreVocabularyAsync(
                        grade.VocabularyAnalysis, [.. _vocabulary], userId,
                        ScoringActivity, DifficultyWeight);
                    foreach (var result in results.Where(result => result.UpdatedProgress.IsFamiliar))
                        notices.Add(result.WasCorrect
                            ? new(NativeWritingNoticeKind.Success, NativeWritingText.ProbeConfirmed)
                            : new(NativeWritingNoticeKind.Info, NativeWritingText.ProbeTricky));
                    if (results.Count > 0)
                        pendingWrites.MarkPending(userId);
                    scope.ServiceProvider.GetService<ProgressCacheService>()?.InvalidateVocabSummary();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Writing could not score vocabulary for a graded sentence.");
                    saveFailed = true;
                }
            }
        }

        if (!owner.IsActive(userId))
            return Refuse();
        if (saveFailed)
            notices.Add(new(NativeWritingNoticeKind.Error, NativeText.SaveFailed));
        _draft = string.Empty;
        Shuffle();
        notices.Add(new(NativeWritingNoticeKind.Success, NativeWritingText.GradedAccuracy,
            ((int)grade.Accuracy).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        _notices = [.. notices];
        return PublishReady();
    }

    private async Task SaveTranslationAsync(string userId, string text, string translation)
    {
        try
        {
            var now = DateTime.UtcNow;
            var saved = await resources.SaveWordAsync(new VocabularyWord
            {
                NativeLanguageTerm = text,
                TargetLanguageTerm = translation,
                UserProfileId = userId,
                CreatedAt = now,
                UpdatedAt = now
            }, userId);
            if (saved < 0)
                logger.LogError("Writing could not save the translated word.");
            else
                pendingWrites.MarkPending(userId);
        }
        catch (Exception ex)
        {
            // The source returns the translation even when its save fails.
            logger.LogError(ex, "Writing could not save the translated word.");
        }
    }

    private async Task<(string Target, string Native)> LanguagesAsync(string userId)
    {
        using var scope = scopes.CreateScope();
        var profile = await scope.ServiceProvider.GetRequiredService<UserProfileRepository>()
            .GetByIdAsync(userId);
        return (string.IsNullOrWhiteSpace(profile?.TargetLanguage) ? "Korean" : profile.TargetLanguage,
            string.IsNullOrWhiteSpace(profile?.NativeLanguage) ? "English" : profile.NativeLanguage);
    }

    private async Task<NativeWritingState> LoadCoreAsync(CancellationToken cancellationToken)
    {
        var launch = _launch!;
        var userId = launch.UserId;
        _vocabulary.Clear();
        _blocks = [];
        _notices = [];
        _draft = string.Empty;
        _usingFocus = false;
        try
        {
            if (!launch.FocusVocabularyIds.IsDefaultOrEmpty)
            {
                var owned = await resources.GetAllVocabularyWordsWithResourcesAsync(userId);
                var selected = FocusVocabularySelection.SelectFocusWords(owned, launch.FocusVocabularyIds);
                _vocabulary.AddRange(WritingActivityVocabularyFilter.FilterToWordsOnly(selected));
                _usingFocus = _vocabulary.Count > 0;
                if (!_usingFocus)
                    logger.LogWarning("Writing focus vocabulary had no Word entries ({Count} selected); using the source resource fallback.", selected.Count);
            }
            if (!owner.IsActive(userId))
                return Refuse();

            if (!_usingFocus)
            {
                var resourceId = launch.ResourceIds.IsDefaultOrEmpty ? null : launch.ResourceIds[0];
                if (string.IsNullOrWhiteSpace(resourceId))
                    return Empty(new(NativeWritingNoticeKind.Warning, NativeWritingText.SelectResource));
                cancellationToken.ThrowIfCancellationRequested();
                var resource = await resources.GetResourceAsync(resourceId, userId);
                if (!owner.IsActive(userId))
                    return Refuse();
                if (resource?.UserProfileId != userId || resource.Vocabulary is not { Count: > 0 } words)
                    return Empty(new(NativeWritingNoticeKind.Warning, NativeWritingText.NoResourceVocabulary));

                using var scope = scopes.CreateScope();
                var progress = scope.ServiceProvider.GetRequiredService<IVocabularyProgressService>();
                var byWord = await progress.GetProgressForWordsAsync(
                    words.Select(word => word.Id).ToList(), userId);
                if (!owner.IsActive(userId))
                    return Refuse();
                var usable = words
                    .Where(FocusVocabularySelection.IsUsableVocabularyWord)
                    .Where(word => !(byWord.GetValueOrDefault(word.Id)?.IsInGracePeriod ?? false))
                    .ToList();
                _vocabulary.AddRange(WritingActivityVocabularyFilter.FilterToWordsOnly(usable));
                if (_vocabulary.Count == 0 && usable.Count > 0)
                    logger.LogWarning("Writing filtered out all {Count} entries of its resource (no Word entries).", usable.Count);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (_vocabulary.Count == 0)
                return Empty(null);
            Shuffle();
            return PublishReady();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Writing could not load vocabulary.");
            return Empty(new(NativeWritingNoticeKind.Error, NativeWritingText.LoadFailed));
        }
    }

    private void Shuffle() =>
        _blocks = _usingFocus
            ? _vocabulary.Take(BlockCount).ToList()
            : _vocabulary.OrderBy(_ => Random.Shared.Next()).Take(BlockCount).ToList();

    private bool Exited => _state.Destination != NativeDestination.Activity;

    private bool Active() => _launch is not null && owner.IsActive(_launch.UserId);

    private NativeWritingState Empty(NativeWritingNotice? notice)
    {
        _notices = notice is null ? [] : [notice];
        return Publish(new(NativePhase.Empty, Notices: _notices,
            ErrorKey: NativeWritingText.NoVocabulary));
    }

    private NativeWritingState Snapshot(NativePhase phase) => new(
        phase,
        Blocks: _blocks.Select((word, index) =>
            new NativeWritingBlock(index, word.Id, word.TargetLanguageTerm ?? string.Empty))
            .ToImmutableArray(),
        Sentences: [.. _sentences],
        Draft: _draft,
        IsGrading: _grading,
        IsTranslating: _translating,
        GradedCount: _graded,
        AverageAccuracy: _graded > 0 ? (int)(_accuracySum / _graded) : 0,
        AverageFluency: _graded > 0 ? (int)(_fluencySum / _graded) : 0,
        Notices: _notices,
        HasPendingSync: _launch is not null && pendingWrites.IsPending(_launch.UserId));

    private NativeWritingState PublishReady() => Publish(Snapshot(NativePhase.Ready));

    private NativeWritingState Refuse()
    {
        _generation++;
        return Publish(new(NativePhase.Blocked, ErrorKey: NativeWritingText.Unavailable));
    }

    private NativeWritingState Publish(NativeWritingState state)
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
        timer.Dispose();
    }
}
