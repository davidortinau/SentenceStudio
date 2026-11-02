using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native;

/// <summary>
/// Diary (source UI/Pages/Diary.razor, DiaryEditor.razor, DiaryViewer.razor;
/// activities.md 17). Three pages inside one activity: the list (/diary,
/// Back to the Dashboard), today's editor (/diary/today) and the read-only
/// viewer with on-demand AI feedback (/diary/{Id}); editor and viewer go Back
/// to the list, which reloads. No timer, plan item, vocabulary progress or
/// UserActivity, as the source. Unlike the source, every read and write is
/// bound to the launch owner and refused when that owner is absent or no
/// longer active. AI calls run outside the presenter lock; leaving the page or
/// the activity cancels them and a late result is discarded.
/// </summary>
public sealed class NativeDiaryPresenter(
    NativeActivityOwner owner,
    NativeDiaryStore store,
    INativeDiaryAi ai,
    IPreferencesService preferences,
    IServiceScopeFactory scopes,
    NativePendingWrites pendingWrites,
    ILogger<NativeDiaryPresenter> logger,
    TimeProvider? clock = null) : IDisposable
{
    public const int MinFontSize = 12;
    public const int MaxFontSize = 32;
    public const int DefaultFontSize = 16;
    public const int FontStep = 2;
    public const int SnippetLength = 120;
    public const string FontSizePreferenceKey = "diary_editor_font_size";
    public const string PromptCollapsedPreferenceKey = "diary_prompt_collapsed";

    /// <summary>Source autosave: 5 s after the last keystroke.</summary>
    internal TimeSpan AutosaveDelay { get; set; } = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly NativeActivityExit _exit = new();
    private CancellationTokenSource _operation = new();
    private CancellationTokenSource? _autosave;
    private NativeActivityLaunch? _launch;
    private NativeDiaryState _state = new(NativePhase.Idle);
    private NativeDiaryPage _page = NativeDiaryPage.List;
    private NativeDiaryListState _list = new(true, null, []);
    private ImmutableArray<NativeMessage> _notices = [];
    // Editor
    private DiaryEntry _entry = new() { WordGoal = NativeDiaryStars.WordGoal };
    private volatile string _content = string.Empty;
    private string _lastSaved = string.Empty;
    private string _targetLanguage = "Korean";
    private string _nativeLanguage = "English";
    private bool _editorLoading;
    private bool _promptCollapsed;
    private bool _loadingPrompt;
    private int _fontSize = DefaultFontSize;
    private NativeDiarySaveStatus _saveStatus;
    // Viewer
    private NativeDiaryViewerState _viewer = new(true, false);
    private long _generation;
    private bool _exited;
    private bool _disposed;

    /// <summary>The Back/Exit fence (tests force continuation ordering through it).</summary>
    internal NativeActivityExit ExitFence => _exit;

    public NativeDiaryState State =>
        _launch is not null && !owner.IsActive(_launch.UserId)
            && _state.Destination != NativeDestination.TodaysPlan
            ? new(NativePhase.Blocked, ErrorKey: NativeDiaryText.Unavailable)
            : _state;

    public event Action<NativeDiaryState>? StateChanged;

    public async Task<NativeDiaryState> OpenAsync(
        NativeActivityLaunch launch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launch);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_launch is not null)
                return State;
            _launch = launch;
            if (launch.Route != NativeActivityCatalog.Diary || !owner.IsActive(launch.UserId))
                return Refuse();
            Publish(new(NativePhase.Loading));
            await LoadListCoreAsync(cancellationToken);
            return Active() ? PublishReady() : Refuse();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Source OpenToday: /diary/today loads or starts today's entry; a new entry requests a prompt.</summary>
    public async Task<NativeDiaryState> OpenTodayAsync(CancellationToken cancellationToken = default)
    {
        bool needsPrompt;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Exited)
                return State;
            if (!Active())
                return Refuse();
            if (_page != NativeDiaryPage.List || _list.IsLoading)
                return State;
            BeginPage(NativeDiaryPage.Editor);
            _editorLoading = true;
            _loadingPrompt = false;
            _saveStatus = NativeDiarySaveStatus.Idle;
            _fontSize = Math.Clamp(preferences.Get(FontSizePreferenceKey, DefaultFontSize),
                MinFontSize, MaxFontSize);
            _promptCollapsed = preferences.Get(PromptCollapsedPreferenceKey, false);
            PublishReady();

            var userId = _launch!.UserId;
            try
            {
                (_targetLanguage, _nativeLanguage) = await LanguagesAsync(userId);
                var existing = await store.GetByDateAsync(userId, Today(), _targetLanguage,
                    cancellationToken);
                if (!Active())
                    return Refuse();
                needsPrompt = existing is null;
                _entry = existing ?? new DiaryEntry
                {
                    EntryDate = Today(),
                    Language = _targetLanguage,
                    WordGoal = NativeDiaryStars.WordGoal,
                    Content = string.Empty,
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Diary could not load today's entry.");
                _notices = [new(NativeMessageKind.Error, NativeText.UnexpectedError)];
                BeginPage(NativeDiaryPage.List);
                return PublishReady();
            }
            _content = _entry.Content ?? string.Empty;
            _lastSaved = _content;
            _editorLoading = false;
            PublishReady();
        }
        finally
        {
            _gate.Release();
        }
        return needsPrompt ? await RefreshPromptAsync(cancellationToken) : State;
    }

    /// <summary>Source OpenEntry: /diary/{Id}, the owner's entry or the not-found state.</summary>
    public async Task<NativeDiaryState> OpenEntryAsync(string id,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Exited)
                return State;
            if (!Active())
                return Refuse();
            if (_page != NativeDiaryPage.List || _list.IsLoading)
                return State;
            BeginPage(NativeDiaryPage.Viewer);
            _viewer = new(true, false);
            PublishReady();
            DiaryEntry? entry = null;
            try
            {
                entry = await store.GetByIdAsync(_launch!.UserId, id, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Source Load logs and shows the not-found state.
                logger.LogError(ex, "Diary could not load an entry.");
            }
            if (!Active())
                return Refuse();
            _viewer = entry is null ? new(false, true) : Viewer(entry);
            return PublishReady();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Typing updates the content without publishing, so a re-render never
    /// interrupts IME composition; heads refresh word count, stars and the
    /// save status from the returned editor state. Restarts the 5 s autosave.
    /// </summary>
    public NativeDiaryEditorState? SetContent(string? value)
    {
        if (Exited || _page != NativeDiaryPage.Editor || _editorLoading || !Active())
            return null;
        _content = value ?? string.Empty;
        _saveStatus = NativeDiarySaveStatus.Idle;
        var editor = EditorSnapshot();
        _state = _state with { Editor = editor };
        RestartAutosave();
        return editor;
    }

    /// <summary>Source SaveNow (manual Save, or silent for autosave and Back).</summary>
    public async Task<NativeDiaryState> SaveAsync(bool silent = false,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Back flushes the editor itself; an autosave that won the gate defers to it.
            if (Exited)
                return State;
            if (!Active())
                return Refuse();
            if (_page != NativeDiaryPage.Editor || _editorLoading)
                return State;
            CancelAutosave();
            await SaveCoreAsync(silent);
            if (_exit.HasBegun)
                return State;
            return Active() ? PublishReady() : Refuse();

        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Source RefreshPrompt ("New prompt", or automatically for a new entry).</summary>
    public async Task<NativeDiaryState> RefreshPromptAsync(CancellationToken cancellationToken = default)
    {
        string userId;
        long generation;
        CancellationToken operation;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Exited)
                return State;
            if (!Active())
                return Refuse();
            if (_page != NativeDiaryPage.Editor || _editorLoading || _loadingPrompt)
                return State;
            _loadingPrompt = true;
            userId = _launch!.UserId;
            generation = _generation;
            operation = _operation.Token;
            PublishReady();
        }
        finally
        {
            _gate.Release();
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, operation);
        DiaryPromptResponse? result = null;
        Exception? failure = null;
        try
        {
            result = await ai.GeneratePromptAsync(userId, _targetLanguage, _nativeLanguage, linked.Token);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        if (!await TryEnterAsync())
            return _state;
        try
        {
            if (generation != _generation || Exited)
                return State;
            _loadingPrompt = false;
            if (!Active())
                return Refuse();
            if (linked.IsCancellationRequested)
                return PublishReady();
            if (failure is not null || result is null)
            {
                if (failure is not null)
                    logger.LogError(failure, "Diary could not generate a prompt.");
                AddNotice(failure is null ? NativeMessageKind.Warning : NativeMessageKind.Error,
                    NativeDiaryText.PromptFailed);
                return PublishReady();
            }
            _entry.PromptText = result.Prompt;
            _entry.PromptHint = result.Hint;
            if (!string.IsNullOrEmpty(_entry.Id))
            {
                try
                {
                    _entry.Content = _content;
                    _entry = await store.UpsertAsync(userId, _entry, CancellationToken.None);
                    _lastSaved = _entry.Content ?? string.Empty;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Diary could not save a new prompt.");
                    AddNotice(NativeMessageKind.Error, NativeDiaryText.PromptFailed);
                }
            }
            return Active() ? PublishReady() : Refuse();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Source "Get feedback" / "Regenerate": AI feedback saved on the owner's entry.</summary>
    public async Task<NativeDiaryState> RequestFeedbackAsync(CancellationToken cancellationToken = default)
    {
        string userId;
        long generation;
        CancellationToken operation;
        NativeDiaryViewerState viewer;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Exited)
                return State;
            if (!Active())
                return Refuse();
            if (_page != NativeDiaryPage.Viewer || !_viewer.CanRequestFeedback)
                return State;
            _viewer = _viewer with { IsLoadingFeedback = true };
            viewer = _viewer;
            userId = _launch!.UserId;
            generation = _generation;
            operation = _operation.Token;
            PublishReady();
        }
        finally
        {
            _gate.Release();
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, operation);
        DiaryFeedbackResponse? result = null;
        Exception? failure = null;
        try
        {
            var (profileTarget, native) = await LanguagesAsync(userId);
            // Source passes the entry's language; the native language comes from the profile.
            var target = string.IsNullOrWhiteSpace(viewer.Language) ? profileTarget : viewer.Language;
            result = await ai.GetFeedbackAsync(userId, viewer.Content, viewer.PromptText,
                target, native, linked.Token);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        if (!await TryEnterAsync())
            return _state;
        try
        {
            if (generation != _generation || Exited)
                return State;
            _viewer = _viewer with { IsLoadingFeedback = false };
            if (!Active())
                return Refuse();
            if (linked.IsCancellationRequested)
                return PublishReady();
            if (failure is not null || result is null)
            {
                if (failure is not null)
                    logger.LogError(failure, "Diary could not generate feedback.");
                AddNotice(failure is null ? NativeMessageKind.Warning : NativeMessageKind.Error,
                    NativeDiaryText.FeedbackFailed);
                return PublishReady();
            }
            try
            {
                var saved = await store.SaveFeedbackAsync(userId, viewer.Id!, result.Recommended,
                    result.Notes, result.Strengths, CancellationToken.None);
                if (saved is not null)
                    _viewer = Viewer(saved);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Diary could not save feedback.");
                AddNotice(NativeMessageKind.Error, NativeDiaryText.FeedbackFailed);
            }
            return Active() ? PublishReady() : Refuse();
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<NativeDiaryState> TogglePromptCollapsedAsync(CancellationToken cancellationToken = default) =>
        EditorCommandAsync(() =>
        {
            _promptCollapsed = !_promptCollapsed;
            preferences.Set(PromptCollapsedPreferenceKey, _promptCollapsed);
        }, cancellationToken);

    public Task<NativeDiaryState> IncreaseFontSizeAsync(CancellationToken cancellationToken = default) =>
        EditorCommandAsync(() =>
        {
            _fontSize = Math.Min(MaxFontSize, _fontSize + FontStep);
            preferences.Set(FontSizePreferenceKey, _fontSize);
        }, cancellationToken);

    public Task<NativeDiaryState> DecreaseFontSizeAsync(CancellationToken cancellationToken = default) =>
        EditorCommandAsync(() =>
        {
            _fontSize = Math.Max(MinFontSize, _fontSize - FontStep);
            preferences.Set(FontSizePreferenceKey, _fontSize);
        }, cancellationToken);

    public async Task<NativeDiaryState> DismissNoticesAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Exited)
                return State;
            if (!Active())
                return Refuse();
            _notices = [];
            return PublishReady();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Editor/viewer Back (source NavigateTo("/diary")): the editor first
    /// flushes a silent save; the list reloads. On the list this does nothing;
    /// its Back is <see cref="ExitAsync"/>.
    /// </summary>
    public async Task<NativeDiaryState> BackToListAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Exited)
                return State;
            if (!Active())
                return Refuse();
            if (_page == NativeDiaryPage.List)
                return State;
            if (_page == NativeDiaryPage.Editor && !_editorLoading)
            {
                CancelAutosave();
                await SaveCoreAsync(silent: true);
                if (!Active())
                    return Refuse();
            }
            BeginPage(NativeDiaryPage.List);
            _list = _list with { IsLoading = true };
            PublishReady();
            await LoadListCoreAsync(cancellationToken);
            return Active() ? PublishReady() : Refuse();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Leaves the activity for the Dashboard. Cancels in-flight AI at once;
    /// an open editor flushes its silent save first (source dispose flush).
    /// </summary>
    public async Task<NativeDiaryState> ExitAsync(CancellationToken cancellationToken = default)
    {
        var exited = false;
        _exit.Begin();
        try
        {
            _operation.Cancel();
            CancelAutosave();
            await _exit.AfterCancelAsync();
            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (_exited)
                {
                    exited = true;
                    return State;
                }
                // The flush publishes nothing: Back shows no "Saving" on the way out.
                if (_page == NativeDiaryPage.Editor && !_editorLoading && Active())
                    await SaveCoreAsync(silent: true);
                _exited = exited = true;
                _generation++;
                return Publish(new(NativePhase.Ready, NativeDestination.TodaysPlan, _page,
                    Notices: _notices,
                    HasPendingSync: _launch is not null && pendingWrites.IsPending(_launch.UserId)));
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            _exit.End(exited);
        }
    }

    private async Task<NativeDiaryState> EditorCommandAsync(Action command,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Exited)
                return State;
            if (!Active())
                return Refuse();
            if (_page != NativeDiaryPage.Editor || _editorLoading)
                return State;
            command();
            return PublishReady();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SaveCoreAsync(bool silent)
    {
        var content = _content;
        if (content == _lastSaved && !string.IsNullOrEmpty(_entry.Id))
        {
            if (!silent)
                _saveStatus = NativeDiarySaveStatus.Saved;
            return;
        }
        _saveStatus = NativeDiarySaveStatus.Saving;
        if (!_exit.HasBegun)
            PublishReady();
        try
        {
            _entry.Content = content;
            _entry = await store.UpsertAsync(_launch!.UserId, _entry, CancellationToken.None);
            _lastSaved = _entry.Content ?? string.Empty;
            _saveStatus = NativeDiarySaveStatus.Saved;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Diary could not save the entry.");
            AddNotice(NativeMessageKind.Error, NativeDiaryText.SaveFailed);
            _saveStatus = NativeDiarySaveStatus.Idle;
        }
    }

    private void RestartAutosave()
    {
        var next = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        Interlocked.Exchange(ref _autosave, next)?.Cancel();
        _ = AutosaveAsync(next.Token);
    }

    private void CancelAutosave() => Interlocked.Exchange(ref _autosave, null)?.Cancel();

    private async Task AutosaveAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(AutosaveDelay, _clock, token);
            await SaveAsync(silent: true, token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Diary autosave failed.");
        }
    }

    private async Task LoadListCoreAsync(CancellationToken cancellationToken)
    {
        var userId = _launch!.UserId;
        try
        {
            var all = await store.ListAsync(userId, cancellationToken);
            var today = Today();
            var yesterday = today.AddDays(-1);
            NativeDiaryEntrySummary Summary(DiaryEntry entry) => new(
                entry.Id, entry.EntryDate,
                entry.EntryDate == today ? NativeDiaryText.Today
                    : entry.EntryDate == yesterday ? NativeDiaryText.Yesterday : null,
                Snippet(entry.Content), entry.WordCount, entry.HasFeedback);
            var todayEntry = all.FirstOrDefault(entry => entry.EntryDate == today);
            _list = new(false, todayEntry is null ? null : Summary(todayEntry),
                [.. all.Where(entry => entry.EntryDate != today).Select(Summary)]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Diary could not load entries.");
            _list = new(false, null, []);
            AddNotice(NativeMessageKind.Error, NativeText.UnexpectedError);
        }
    }

    private static string Snippet(string? content)
    {
        var trimmed = (content ?? string.Empty).Trim();
        return trimmed.Length <= SnippetLength ? trimmed : trimmed[..SnippetLength] + "…";
    }

    private static NativeDiaryViewerState Viewer(DiaryEntry entry) => new(
        false, false, entry.Id, entry.EntryDate, entry.PromptText, entry.PromptHint,
        entry.Content ?? string.Empty, entry.WordCount, entry.HasFeedback,
        entry.FeedbackRecommended, entry.FeedbackNotes, entry.FeedbackStrengths)
    {
        Language = entry.Language
    };

    private async Task<(string Target, string Native)> LanguagesAsync(string userId)
    {
        using var scope = scopes.CreateScope();
        var profile = await scope.ServiceProvider.GetRequiredService<UserProfileRepository>()
            .GetByIdAsync(userId);
        return (string.IsNullOrWhiteSpace(profile?.TargetLanguage) ? "Korean" : profile.TargetLanguage,
            string.IsNullOrWhiteSpace(profile?.NativeLanguage) ? "English" : profile.NativeLanguage);
    }

    /// <summary>Source today: the UTC calendar day (DiaryEntryRepository.NormalizeEntryDate(DateTime.UtcNow)).</summary>
    private DateTime Today() => DiaryEntryRepository.NormalizeEntryDate(_clock.GetUtcNow().UtcDateTime);

    private void BeginPage(NativeDiaryPage page)
    {
        _generation++;
        _operation.Cancel();
        _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        CancelAutosave();
        _notices = [];
        _page = page;
    }

    private void AddNotice(NativeMessageKind kind, string key) =>
        _notices = _notices.Add(new(kind, key));

    /// <summary>
    /// Re-takes the gate for a continuation. False (gate not held) once Exit has
    /// begun or the presenter was disposed: the caller returns <see cref="_state"/>,
    /// which is then the state Exit published, never the pre-Exit one.
    /// </summary>
    private async Task<bool> TryEnterAsync()
    {
        if (await _exit.EnterAsync(_gate))
            return true;
        _gate.Release();
        return false;
    }

    private bool Exited => _exited || _exit.HasBegun || _state.Destination != NativeDestination.Activity;

    private bool Active() => _launch is not null && owner.IsActive(_launch.UserId);

    private NativeDiaryEditorState EditorSnapshot() => new(
        _editorLoading, _entry.PromptText, _entry.PromptHint, _promptCollapsed, _loadingPrompt,
        _content, DiaryEntryRepository.CountWords(_content, _entry.Language), _fontSize, _saveStatus);

    private NativeDiaryState PublishReady() => Publish(new(
        NativePhase.Ready,
        Page: _page,
        List: _list,
        Editor: _page == NativeDiaryPage.Editor ? EditorSnapshot() : null,
        Viewer: _page == NativeDiaryPage.Viewer ? _viewer : null,
        Notices: _notices,
        HasPendingSync: _launch is not null && pendingWrites.IsPending(_launch.UserId)));

    private NativeDiaryState Refuse()
    {
        _generation++;
        _operation.Cancel();
        CancelAutosave();
        if (_exited)
            return State;
        return Publish(new(NativePhase.Blocked, ErrorKey: NativeDiaryText.Unavailable));
    }

    private NativeDiaryState Publish(NativeDiaryState state)
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
        _exit.Complete();
        CancelAutosave();

        _operation.Cancel();
        _lifetime.Cancel();
    }
}
