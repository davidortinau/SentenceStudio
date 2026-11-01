using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Scriban;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models;
using SentenceStudio.Shared.Services;

namespace SentenceStudio.Native;

/// <summary>
/// Translation (source UI/Pages/Translation.razor, TranslationService
/// .GetTranslationSentences and TeacherService.GradeTranslation;
/// activities.md 5). The learner sees a native-language sentence and types
/// its target-language translation, optionally appending vocabulary blocks;
/// AI grades accuracy and fluency. Each grade writes a UserActivity row and
/// scores the challenge vocabulary (activity "Translation", weight 1.5).
/// Unlike the source, every read, AI call and write is bound to the launch
/// owner and refused when that owner is absent or no longer active, and an AI
/// grading failure is reported instead of being recorded as a zero score.
/// </summary>
public sealed class NativeTranslationPresenter(
    NativeActivityOwner owner,
    NativeActivityTimer timer,
    INativeTranslationAi ai,
    UserProfileRepository profiles,
    LearningResourceRepository resources,
    SkillProfileRepository skills,
    IServiceScopeFactory scopes,
    NativePendingWrites pendingWrites,
    ILogger<NativeTranslationPresenter> logger) : IDisposable
{
    public const int SentenceCount = 5;
    public const int MaxVocabularySample = 40;
    public const int MaxIntroVocabulary = 12;
    public const int MaxGrammarBullets = 8;
    public const string AttemptActivity = "Translation";
    public const float DifficultyWeight = 1.5f;

    private const string GenerateTemplate = "SentenceStudio.Native.Prompts.GetTranslations.scriban-txt";
    private const string GradeTemplate = "SentenceStudio.Native.Prompts.GradeTranslation.scriban-txt";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly NativeActivityLoad _load = new();
    private readonly List<Challenge> _sentences = [];
    private readonly HashSet<string> _ownedWordIds = new(StringComparer.Ordinal);
    private List<VocabularyWord> _focusWords = [];
    private NativeActivityLaunch? _launch;
    private NativeTranslationState _state = new(NativePhase.Idle);
    private string _nativeLanguage = "English";
    private string _targetLanguage = "Korean";
    private string _profileTargetLanguage = "Korean";
    private CancellationTokenSource? _grading;
    private int _gradedCount;
    private double _accuracySum;
    private double _fluencySum;
    private bool _disposed;
    private bool _exited;

    public NativeTranslationState State =>
        _launch is not null && !owner.IsActive(_launch.UserId)
            && _state.Destination != NativeDestination.TodaysPlan
            ? new(NativePhase.Blocked, ErrorKey: NativeTranslationText.Unavailable)
            : _state;

    public event Action<NativeTranslationState>? StateChanged;

    public async Task<NativeTranslationState> OpenAsync(
        NativeActivityLaunch launch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launch);
        NativeActivityLoadTicket ticket;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_launch is not null || _exited)
                return State;
            _launch = launch;
            Publish(new(NativePhase.Loading));
            if (launch.Route != NativeActivityCatalog.Translation
                || !owner.IsActive(launch.UserId)
                || !await timer.StartAsync(launch, cancellationToken))
                return Refuse();
            ticket = _load.Begin(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
        return await CompleteLoadAsync(launch, ticket);
    }

    /// <summary>Intro Start button.</summary>
    public Task<NativeTranslationState> StartAsync(CancellationToken cancellationToken = default) =>
        MutateAsync(state => state.Phase == NativePhase.Ready && state.ShowIntro
            ? state with { ShowIntro = false } : state, cancellationToken);

    /// <summary>
    /// The typed answer. Stored without publishing so a re-render never
    /// replaces the field while an input method (Korean Hangul) is composing.
    /// </summary>
    public void SetInput(string? text)
    {
        if (!_exited && IsActive(_state))
            _state = _state with { Input = text ?? string.Empty };
    }

    /// <summary>Source ToggleInputMode: Type (default per sentence) / Blocks.</summary>
    public Task<NativeTranslationState> ToggleInputModeAsync(CancellationToken cancellationToken = default) =>
        MutateAsync(state => IsActive(state)
            ? state with { BlocksMode = !state.BlocksMode } : state, cancellationToken);

    /// <summary>Source UseVocabBlock: appends the block to the answer, space separated.</summary>
    public Task<NativeTranslationState> UseBlockAsync(int index, CancellationToken cancellationToken = default) =>
        MutateAsync(state =>
        {
            if (!IsActive(state) || !state.ShowsBlocks || state.IsGrading
                || index < 0 || index >= state.Blocks.Length)
                return state;
            var block = state.Blocks[index];
            return state with
            {
                Input = string.IsNullOrEmpty(state.Input) ? block : $"{state.Input} {block}"
            };
        }, cancellationToken);

    public Task<NativeTranslationState> ToggleDetailsAsync(CancellationToken cancellationToken = default) =>
        MutateAsync(state => state.Feedback is { HasDetails: true }
            ? state with { DetailsExpanded = !state.DetailsExpanded } : state, cancellationToken);

    public Task<NativeTranslationState> DismissNoticeAsync(CancellationToken cancellationToken = default) =>
        MutateAsync(state => state with { Notice = null }, cancellationToken);

    /// <summary>Previous chevron; disabled on the first sentence and while grading.</summary>
    public Task<NativeTranslationState> PreviousAsync(CancellationToken cancellationToken = default) =>
        MutateAsync(state => IsActive(state) && state.CanGoPrevious
            ? SentenceState(state.Index - 1) : state, cancellationToken);

    /// <summary>Next chevron; on the last sentence it opens the session summary (grading is optional).</summary>
    public Task<NativeTranslationState> NextAsync(CancellationToken cancellationToken = default) =>
        MutateAsync(state =>
        {
            if (!IsActive(state) || state.IsGrading)
                return state;
            if (state.Index < _sentences.Count - 1)
                return SentenceState(state.Index + 1);
            return new(NativePhase.Summary, GradedCount: _gradedCount,
                AverageAccuracy: Average(_accuracySum),
                AverageFluency: Average(_fluencySum),
                HasPendingSync: HasPending());
        }, cancellationToken);

    /// <summary>Grade button / keyboard submit (source GradeMe).</summary>
    public async Task<NativeTranslationState> GradeAsync(CancellationToken cancellationToken = default)
    {
        string input, prompt, reference, userId;
        int index;
        List<VocabularyWord> vocabulary;
        CancellationTokenSource grading;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_launch is null || !owner.IsActive(_launch.UserId))
                return Refuse();
            var state = _state;
            if (_exited || !IsActive(state) || !state.CanGrade)
                return State;
            userId = _launch.UserId;
            index = state.Index;
            input = state.Input;
            var challenge = _sentences[index];
            prompt = state.Prompt;
            reference = challenge.SentenceText ?? string.Empty;
            vocabulary = (challenge.Vocabulary ?? [])
                .Where(word => _ownedWordIds.Contains(word.Id))
                .ToList();
            grading = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _lifetime.Token);
            _grading = grading;
            Publish(state with
            {
                IsGrading = true, Feedback = null, DetailsExpanded = false, Notice = null
            });
        }
        finally
        {
            _gate.Release();
        }

        GradeResponse? feedback = null;
        Exception? failure = null;
        var cancelled = false;
        try
        {
            var rendered = await RenderAsync(GradeTemplate, new
            {
                original_sentence = prompt,
                recommended_translation = reference,
                user_input = input,
                native_language = _nativeLanguage,
                target_language = _profileTargetLanguage
            }, grading.Token);
            feedback = await ai.GradeAsync(userId, rendered, grading.Token);
        }
        catch (OperationCanceledException) when (grading.IsCancellationRequested)
        {
            // Exit or a lifetime end superseded the grade; nothing is recorded.
            cancelled = true;
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            if (ReferenceEquals(_grading, grading))
                _grading = null;
            grading.Dispose();
            if (_exited || _state.Destination != NativeDestination.Activity || _launch is null)
                return State;
            if (!owner.IsActive(userId))
                return Refuse();
            if (cancelled)
                return Publish(_state with { IsGrading = false });
            if (failure is not null || feedback is null)
            {
                if (failure is not null)
                    logger.LogError(failure, "Translation grading failed.");
                else
                    logger.LogWarning("Translation grading returned no verdict.");
                return Publish(_state with
                {
                    IsGrading = false,
                    Notice = new(NativeTranslationText.GradeFailed, IsError: true)
                });
            }
            return await ApplyGradeAsync(userId, index, input, feedback, vocabulary);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Summary "Continue practice": counters reset and a fresh set loads; the timer keeps running.</summary>
    public async Task<NativeTranslationState> ContinueAsync(CancellationToken cancellationToken = default)
    {
        NativeActivityLaunch launch;
        NativeActivityLoadTicket ticket;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_launch is null || !owner.IsActive(_launch.UserId))
                return Refuse();
            if (_exited || _state.Phase != NativePhase.Summary
                || _state.Destination != NativeDestination.Activity)
                return State;
            launch = _launch;
            _gradedCount = 0;
            _accuracySum = 0;
            _fluencySum = 0;
            Publish(new(NativePhase.Loading));
            ticket = _load.Begin(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
        return await CompleteLoadAsync(launch, ticket);
    }

    /// <summary>
    /// Back / Done / Go Back: cancel any grade or sentence load, stop and save
    /// the timer, return to the Dashboard. Neither holds the gate, so Back works
    /// at once even while the AI is slow or hung.
    /// </summary>
    public async Task<NativeTranslationState> ExitAsync(CancellationToken cancellationToken = default)
    {
        CancelGrading();
        _load.Cancel();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _load.Cancel();
            _exited = true;
            string? errorKey = null;
            try
            {
                if (await timer.StopAsync(cancellationToken) == NativeActivityStop.Failed)
                    errorKey = NativeText.SaveFailed;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Translation could not save its activity minutes.");
                errorKey = NativeText.SaveFailed;
            }
            return Publish(new(NativePhase.Ready, NativeDestination.TodaysPlan,
                ErrorKey: errorKey, HasPendingSync: HasPending()));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<NativeTranslationState> ApplyGradeAsync(
        string userId, int index, string input, GradeResponse feedback,
        List<VocabularyWord> vocabulary)
    {
        // The AI often writes the suggestion into GrammarNotes (source fallback).
        var recommended = !string.IsNullOrWhiteSpace(feedback.RecommendedTranslation)
            ? feedback.RecommendedTranslation!
            : feedback.GrammarNotes?.RecommendedTranslation ?? string.Empty;
        _gradedCount++;
        _accuracySum += feedback.Accuracy;
        _fluencySum += feedback.Fluency;

        NativeTranslationNotice notice = new(NativeTranslationText.GradedAccuracy, IsError: false,
            ((int)feedback.Accuracy).ToString(System.Globalization.CultureInfo.CurrentCulture));
        try
        {
            using var scope = scopes.CreateScope();
            var activities = scope.ServiceProvider.GetRequiredService<UserActivityRepository>();
            var saved = await activities.SaveAsync(NativeUserActivity.Create(
                userId, Activity.Translation, input, feedback.Accuracy, feedback.Fluency));
            if (saved < 0)
                throw new InvalidOperationException("The translation activity row was not saved.");
            pendingWrites.MarkPending(userId);

            if (vocabulary.Count > 0 && owner.IsActive(userId))
            {
                var progress = scope.ServiceProvider.GetRequiredService<IVocabularyProgressService>();
                var scored = await progress.ExtractAndScoreVocabularyAsync(
                    feedback.VocabularyAnalysis, vocabulary, userId,
                    AttemptActivity, DifficultyWeight);
                if (scored.Count > 0)
                    scope.ServiceProvider.GetService<ProgressCacheService>()?.InvalidateVocabSummary();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Translation could not record its grade.");
            notice = new(NativeTranslationText.SaveFailed, IsError: true);
        }

        var state = _state.Index == index ? _state : SentenceState(index);
        return Publish(state with
        {
            IsGrading = false,
            Feedback = new(input, feedback.Accuracy, feedback.Fluency, recommended,
                feedback.AccuracyExplanation ?? string.Empty,
                feedback.FluencyExplanation ?? string.Empty,
                feedback.GrammarNotes?.Explanation ?? string.Empty),
            DetailsExpanded = false,
            Notice = notice,
            HasPendingSync = HasPending()
        });
    }

    /// <summary>
    /// Outside the gate: select vocabulary and generate the sentences into a
    /// fresh <see cref="LoadedContent"/>, then apply it under the gate only
    /// while the load is current (not exited, superseded or disposed).
    /// </summary>
    private async Task<NativeTranslationState> CompleteLoadAsync(
        NativeActivityLaunch launch, NativeActivityLoadTicket ticket)
    {
        var content = new LoadedContent();
        var outcome = LoadOutcome.Empty;
        Exception? failure = null;
        try
        {
            outcome = await LoadContentAsync(launch, content, ticket.Token);
        }
        catch (OperationCanceledException) when (ticket.Token.IsCancellationRequested)
        {
            return State;
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            if (_exited || !_load.IsCurrent(ticket))
                return State;
            if (outcome == LoadOutcome.Refused || !owner.IsActive(launch.UserId))
                return Refuse();
            if (failure is not null)
            {
                logger.LogError(failure, "Translation could not load sentences.");
                return Publish(new(NativePhase.Error, ErrorKey: NativeTranslationText.LoadFailed));
            }
            if (outcome == LoadOutcome.Empty)
                return Publish(new(NativePhase.Empty, ErrorKey: NativeTranslationText.NoSentences,
                    Notice: new(NativeTranslationText.NoSentencesReturned, IsError: false, IsWarning: true)));

            _sentences.Clear();
            _sentences.AddRange(content.Sentences);
            _ownedWordIds.Clear();
            _ownedWordIds.UnionWith(content.OwnedWordIds);
            _focusWords = content.FocusWords;
            _nativeLanguage = content.NativeLanguage;
            _targetLanguage = content.TargetLanguage;
            _profileTargetLanguage = content.ProfileTargetLanguage;
            var first = SentenceState(0);
            return Publish(first with
            {
                ShowIntro = content.IntroVocabulary.Length > 0 || content.IntroGrammar.Length > 0
                    || !string.IsNullOrEmpty(content.ResourceTitle),
                IntroResourceTitle = content.ResourceTitle,
                IntroVocabulary = content.IntroVocabulary,
                IntroGrammar = content.IntroGrammar
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<LoadOutcome> LoadContentAsync(
        NativeActivityLaunch launch, LoadedContent content, CancellationToken cancellationToken)
    {
        var userId = launch.UserId;
        {
            var (words, resource, skill) = await SelectVocabularyAsync(launch, content, cancellationToken);
            if (!owner.IsActive(userId))
                return LoadOutcome.Refused;
            if (words.Count == 0)
                return LoadOutcome.Empty;

            var required = content.FocusWords.Where(FocusVocabularySelection.IsUsableVocabularyWord).ToList();
            var requiredIds = required.Select(word => word.Id).ToHashSet(StringComparer.Ordinal);
            var prompt = await RenderAsync(GenerateTemplate, new
            {
                terms = words,
                required_terms = required,
                context_terms = required.Count > 0
                    ? words.Where(word => !requiredIds.Contains(word.Id)).ToList()
                    : words,
                has_required_terms = required.Count > 0,
                number_of_sentences = SentenceCount,
                skills = skill?.Description,
                native_language = content.NativeLanguage,
                target_language = content.TargetLanguage
            }, cancellationToken);
            var reply = await ai.GenerateAsync(userId, prompt, cancellationToken);
            if (!owner.IsActive(userId))
                return LoadOutcome.Refused;
            if (reply?.Sentences is not { Count: > 0 } generated)
                return LoadOutcome.Empty;

            var byTerm = words.Where(FocusVocabularySelection.IsUsableVocabularyWord)
                .GroupBy(word => word.TargetLanguageTerm!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(),
                    StringComparer.OrdinalIgnoreCase);
            foreach (var dto in generated)
                content.Sentences.Add(new Challenge
                {
                    SentenceText = dto.SentenceText,
                    RecommendedTranslation = dto.RecommendedTranslation,
                    Vocabulary = (dto.TranslationVocabulary ?? []).Select(term =>
                        byTerm.TryGetValue(term, out var existing)
                            ? existing
                            : new VocabularyWord
                            {
                                TargetLanguageTerm = term,
                                NativeLanguageTerm = string.Empty,
                                CreatedAt = DateTime.UtcNow,
                                UpdatedAt = DateTime.UtcNow
                            }).ToList(),
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });

            content.IntroVocabulary = FocusVocabularySelection.BuildRequiredFirstPromptVocabulary(
                    content.FocusWords,
                    content.Sentences.SelectMany(sentence => sentence.Vocabulary ?? []),
                    MaxIntroVocabulary)
                .Select(word => new NativeTranslationIntroWord(
                    word.TargetLanguageTerm ?? string.Empty, word.NativeLanguageTerm ?? string.Empty))
                .ToImmutableArray();
            content.IntroGrammar = ParseGrammarBullets(skill?.Description);
            content.ResourceTitle = resource?.Title ?? string.Empty;
            return LoadOutcome.Ready;
        }
    }

    private enum LoadOutcome { Ready, Empty, Refused }

    /// <summary>One load's result, built off the gate and applied to the presenter only if still current.</summary>
    private sealed class LoadedContent
    {
        public List<Challenge> Sentences { get; } = [];
        public HashSet<string> OwnedWordIds { get; } = new(StringComparer.Ordinal);
        public List<VocabularyWord> FocusWords { get; set; } = [];
        public string NativeLanguage { get; set; } = "English";
        public string TargetLanguage { get; set; } = "Korean";
        public string ProfileTargetLanguage { get; set; } = "Korean";
        public string ResourceTitle { get; set; } = string.Empty;
        public ImmutableArray<NativeTranslationIntroWord> IntroVocabulary { get; set; } = [];
        public ImmutableArray<string> IntroGrammar { get; set; } = [];
    }

    /// <summary>
    /// Source GetTranslationSentences selection: needs a resource or focus
    /// words, and a skill unless focus words exist; excludes grace-period
    /// words; samples at most 40. Every read is scoped to the launch owner.
    /// </summary>
    private async Task<(List<VocabularyWord> Words, LearningResource? Resource, SkillProfile? Skill)>
        SelectVocabularyAsync(NativeActivityLaunch launch, LoadedContent content,
            CancellationToken cancellationToken)
    {
        var userId = launch.UserId;
        var profile = await profiles.GetByIdAsync(userId, cancellationToken);
        if (profile is not null && profile.Id != userId)
            profile = null;
        content.NativeLanguage = profile?.NativeLanguage ?? "English";
        content.ProfileTargetLanguage = profile?.TargetLanguage ?? "Korean";
        content.TargetLanguage = content.ProfileTargetLanguage;
        var focusIds = FocusVocabularySelection.NormalizeFocusVocabularyIds(
            launch.FocusVocabularyIds.IsDefault ? [] : launch.FocusVocabularyIds);
        var resourceId = launch.ResourceIds.IsDefaultOrEmpty ? null : launch.ResourceIds[0];

        LearningResource? resource = null;
        if (!string.IsNullOrEmpty(resourceId))
        {
            resource = await resources.GetResourceAsync(resourceId, userId);
            if (resource is not null && resource.UserProfileId != userId)
                resource = null;
            content.TargetLanguage = resource?.Language ?? content.TargetLanguage;
        }
        else if (focusIds.Count == 0)
        {
            logger.LogWarning("Translation launched without a resource or focus words.");
            return ([], null, null);
        }
        if (string.IsNullOrEmpty(launch.SkillId) && focusIds.Count == 0)
        {
            logger.LogWarning("Translation launched without a skill.");
            return ([], resource, null);
        }

        var resourceVocabulary = resource?.Vocabulary?.ToList() ?? [];
        if (resourceVocabulary.Count == 0 && focusIds.Count == 0)
            return ([], resource, null);

        var allOwned = focusIds.Count > 0
            ? await resources.GetAllVocabularyWordsWithResourcesAsync(userId)
            : [];
        cancellationToken.ThrowIfCancellationRequested();
        content.FocusWords = FocusVocabularySelection.SelectFocusWords(allOwned, focusIds);
        if (focusIds.Count > 0 && content.FocusWords.Count == 0)
            logger.LogWarning("Translation focus words were unavailable; using resource vocabulary.");

        var context = resourceVocabulary.Count > 0 ? resourceVocabulary : allOwned;
        using (var scope = scopes.CreateScope())
        {
            var progress = scope.ServiceProvider.GetRequiredService<IVocabularyProgressService>();
            var progressByWord = await progress.GetProgressForWordsAsync(
                context.Select(word => word.Id).ToList(), userId);
            context = context
                .Where(FocusVocabularySelection.IsUsableVocabularyWord)
                .Where(word => !(progressByWord.GetValueOrDefault(word.Id)?.IsInGracePeriod ?? false))
                .ToList();
        }

        List<VocabularyWord> words;
        if (content.FocusWords.Count > 0)
            words = FocusVocabularySelection.BuildRequiredFirstPromptVocabulary(content.FocusWords,
                context.Count > MaxVocabularySample
                    ? context.OrderBy(_ => Random.Shared.Next()).Take(MaxVocabularySample).ToList()
                    : context,
                MaxVocabularySample);
        else
            words = context.Count > MaxVocabularySample
                ? context.OrderBy(_ => Random.Shared.Next()).Take(MaxVocabularySample).ToList()
                : context;
        foreach (var word in words.Concat(content.FocusWords))
            content.OwnedWordIds.Add(word.Id);

        SkillProfile? skill = null;
        if (words.Count > 0 && !string.IsNullOrEmpty(launch.SkillId))
        {
            skill = await skills.GetSkillProfileAsync(launch.SkillId, userId);
            if (skill is not null && skill.UserProfileId != userId)
                skill = null;
        }
        return (words, resource, skill);
    }

    /// <summary>Source SetCurrentSentence: Type mode, cleared input and feedback, fresh blocks.</summary>
    private NativeTranslationState SentenceState(int index)
    {
        var challenge = _sentences[index];
        var blockWords = _focusWords.Count > 0
            ? FocusVocabularySelection.BuildRequiredFirstPromptVocabulary(
                _focusWords, challenge.Vocabulary, MaxIntroVocabulary)
            : (challenge.Vocabulary ?? [])
                .Where(FocusVocabularySelection.IsUsableVocabularyWord)
                .OrderBy(_ => Random.Shared.Next())
                .ToList();
        var current = _state;
        return new(NativePhase.Ready,
            IntroResourceTitle: current.IntroResourceTitle,
            IntroVocabulary: current.IntroVocabulary,
            IntroGrammar: current.IntroGrammar,
            Index: index,
            Count: _sentences.Count,
            Prompt: challenge.RecommendedTranslation ?? string.Empty,
            TargetLanguage: _targetLanguage,
            Blocks: blockWords.Select(word => word.TargetLanguageTerm)
                .Where(term => !string.IsNullOrEmpty(term))
                .Select(term => term!)
                .ToImmutableArray(),
            HasPendingSync: HasPending());
    }

    /// <summary>Source ParseGrammarBullets: up to 8 lines, stopping at example sections.</summary>
    internal static ImmutableArray<string> ParseGrammarBullets(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return [];
        var bullets = ImmutableArray.CreateBuilder<string>();
        foreach (var raw in description.Split('\n'))
        {
            var line = raw.Trim().TrimStart('-', '*', '•', ' ', '\t');
            if (string.IsNullOrWhiteSpace(line))
                continue;
            if (line.StartsWith("###", StringComparison.Ordinal)
                || line.StartsWith("Examples", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Example:", StringComparison.OrdinalIgnoreCase))
                break;
            bullets.Add(line);
            if (bullets.Count >= MaxGrammarBullets)
                break;
        }
        return bullets.ToImmutable();
    }

    private static async Task<string> RenderAsync(
        string resourceName, object model, CancellationToken cancellationToken)
    {
        using var stream = typeof(NativeTranslationPresenter).Assembly
            .GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"The source prompt {resourceName} is missing.");
        using var reader = new StreamReader(stream);
        var template = Template.Parse(await reader.ReadToEndAsync(cancellationToken));
        if (template.HasErrors)
            throw new InvalidOperationException($"The source prompt {resourceName} is invalid.");
        return await template.RenderAsync(model);
    }

    private async Task<NativeTranslationState> MutateAsync(
        Func<NativeTranslationState, NativeTranslationState> change,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_launch is null || !owner.IsActive(_launch.UserId))
                return Refuse();
            if (_exited || _state.Destination != NativeDestination.Activity)
                return State;
            var next = change(_state);
            return ReferenceEquals(next, _state) ? State : Publish(next);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsActive(NativeTranslationState state) =>
        state.Phase == NativePhase.Ready && !state.ShowIntro
        && state.Destination == NativeDestination.Activity;

    private int Average(double sum) => _gradedCount > 0 ? (int)(sum / _gradedCount) : 0;

    private bool HasPending() => _launch is not null && pendingWrites.IsPending(_launch.UserId);

    private void CancelGrading()
    {
        try
        {
            _grading?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The grade finished and released its token concurrently.
        }
    }

    private NativeTranslationState Refuse()
    {
        CancelGrading();
        return Publish(new(NativePhase.Blocked, ErrorKey: NativeTranslationText.Unavailable));
    }

    private NativeTranslationState Publish(NativeTranslationState state)
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
        _load.Dispose();
        timer.Dispose();
    }
}
