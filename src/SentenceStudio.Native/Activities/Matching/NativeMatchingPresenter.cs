using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models;
using SentenceStudio.Shared.Services;

namespace SentenceStudio.Native;

/// <summary>
/// Vocabulary Matching (source UI/Pages/VocabMatching.razor, activities.md 3).
/// Eight target/native pairs; hide-native mode (default on) lets only a
/// target tile be picked first, so a target-language cue starts every pair.
/// Every pair check records one attempt for the first tile's word. Unlike the
/// source, every read and write is bound to the launch owner and refused when
/// that owner is absent or no longer active.
/// </summary>
public sealed class NativeMatchingPresenter(
    NativeActivityOwner owner,
    NativeActivityTimer timer,
    LearningResourceRepository resources,
    IServiceScopeFactory scopes,
    NativePendingWrites pendingWrites,
    ILogger<NativeMatchingPresenter> logger) : IDisposable
{
    public const int PairCount = 8;
    public const string AttemptActivity = "VocabularyMatching";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Stopwatch _response = new();
    private readonly List<Tile> _tiles = [];
    private readonly List<Tile> _selected = [];
    private NativeActivityLaunch? _launch;
    private NativeMatchingState _state = new(NativePhase.Idle);
    private bool _hide = true;
    private int _matched;
    private int _total;
    private int _misses;
    private long _selection;
    private string? _errorKey;
    private bool _usedFocus;
    private bool _disposed;
    private bool _exited;

    /// <summary>Source CheckMatchWithDelay waits 600 ms before checking a pair.</summary>
    public TimeSpan CheckDelay { get; set; } = TimeSpan.FromMilliseconds(600);

    public NativeMatchingState State =>
        _launch is not null && !owner.IsActive(_launch.UserId)
            && _state.Destination != NativeDestination.TodaysPlan
            ? new(NativePhase.Blocked, ErrorKey: NativeMatchingText.Unavailable)
            : _state;

    public event Action<NativeMatchingState>? StateChanged;

    public async Task<NativeMatchingState> OpenAsync(
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
            if (launch.Route != NativeActivityCatalog.VocabularyMatching
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

    public async Task<NativeMatchingState> TapAsync(
        int tileId, CancellationToken cancellationToken = default)
    {
        long check;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_launch is null || !owner.IsActive(_launch.UserId))
                return Refuse();
            if (_exited)
                return State;
            var tile = _tiles.FirstOrDefault(item => item.Id == tileId);
            if (_state.Phase != NativePhase.Ready || tile is null
                || tile.IsMatched || !tile.IsVisible)
                return State;
            // Hide-native mode: only a target tile can be the first pick.
            if (_hide && _selected.Count == 0 && !tile.IsTarget)
                return State;
            if (tile.IsSelected)
            {
                tile.IsSelected = false;
                _selected.Remove(tile);
                _selection++;
                if (_hide && _selected.Count == 0)
                    SetNativeVisibility(false);
                return PublishGame();
            }
            if (_selected.Count >= 2)
                return State;
            if (_selected.Count == 0)
                _response.Restart();
            tile.IsSelected = true;
            _selected.Add(tile);
            if (_hide && tile.IsTarget && _selected.Count == 1)
                SetNativeVisibility(true);
            if (_selected.Count < 2)
                return PublishGame();
            check = ++_selection;
            PublishGame(checking: true);
        }
        finally
        {
            _gate.Release();
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _lifetime.Token);
        try
        {
            if (CheckDelay > TimeSpan.Zero)
                await Task.Delay(CheckDelay, linked.Token);
            await _gate.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            // Disposal or a cancelled caller abandons the pending check; clear the
            // selection so the board is not left locked with two tiles picked.
            if (_lifetime.IsCancellationRequested)
                return _state;
            await _gate.WaitAsync(CancellationToken.None);
            try
            {
                if (check == _selection && !_exited)
                {
                    foreach (var tile in _selected)
                        tile.IsSelected = false;
                    _selected.Clear();
                    _selection++;
                    if (_hide)
                        SetNativeVisibility(false);
                    return PublishGame();
                }
                return State;
            }
            finally
            {
                _gate.Release();
            }
        }
        try
        {
            // A deselect, restart, or exit during the delay supersedes the check.
            if (_exited || check != _selection || _selected.Count != 2)
                return State;
            return await CheckCoreAsync(linked.Token);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<NativeMatchingState> ToggleHideNativeAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_launch is null || !owner.IsActive(_launch.UserId))
                return Refuse();
            if (_exited)
                return State;
            _hide = !_hide;
            if (_hide)
            {
                var targetSelected = _selected.Any(tile => tile.IsTarget);
                foreach (var tile in _tiles.Where(tile => !tile.IsTarget && !tile.IsMatched))
                    tile.IsVisible = targetSelected;
            }
            else
            {
                foreach (var tile in _tiles.Where(tile => !tile.IsTarget))
                    tile.IsVisible = true;
            }
            // A pending pair check keeps the board locked.
            return _state.Phase == NativePhase.Ready
                ? PublishGame(checking: _state.IsChecking)
                : Publish(_state with { HideNativeWords = _hide });
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>"New game" and "Play again": a fresh set of pairs; the timer keeps running.</summary>
    public async Task<NativeMatchingState> RestartAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_launch is null || !owner.IsActive(_launch.UserId))
                return Refuse();
            if (_exited || _state.Phase is NativePhase.Loading or NativePhase.Blocked
                || _state.Destination != NativeDestination.Activity)
                return State;
            _selection++;
            Publish(new(NativePhase.Loading, HideNativeWords: _hide));
            return await LoadCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Back / Go Back: stop and save the timer, then return to the Dashboard.</summary>
    public async Task<NativeMatchingState> ExitAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _selection++;
            _exited = true;
            string? errorKey = null;
            try
            {
                if (await timer.StopAsync(cancellationToken) == NativeActivityStop.Failed)
                    errorKey = NativeText.SaveFailed;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Vocabulary Matching could not save its activity minutes.");
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

    private async Task<NativeMatchingState> LoadCoreAsync(CancellationToken cancellationToken)
    {
        var launch = _launch!;
        var userId = launch.UserId;
        _tiles.Clear();
        _selected.Clear();
        _matched = 0;
        _total = 0;
        _misses = 0;
        _errorKey = null;
        try
        {
            var words = await LoadWordsAsync(launch, cancellationToken);
            if (!owner.IsActive(userId))
                return Refuse();
            if (words.Count == 0)
                return Publish(new(NativePhase.Empty, HideNativeWords: _hide,
                    ErrorKey: NativeMatchingText.NoVocabulary));

            using var scope = scopes.CreateScope();
            var progress = scope.ServiceProvider.GetRequiredService<IVocabularyProgressService>();
            var progressByWord = await progress.GetProgressForWordsAsync(
                words.Select(word => word.Id).ToList(), userId);
            if (!owner.IsActive(userId))
                return Refuse();

            var selected = _usedFocus
                ? words.Take(PairCount).ToList()
                : words
                    .Select(word => (Word: word,
                        Progress: progressByWord.GetValueOrDefault(word.Id)))
                    .Where(item => !(item.Progress?.IsKnown ?? false))
                    .Where(item => !(item.Progress?.IsInGracePeriod ?? false))
                    .OrderBy(item => item.Progress?.MasteryScore ?? 0f)
                    .ThenBy(_ => Random.Shared.Next())
                    .Take(PairCount)
                    .Select(item => item.Word)
                    .ToList();
            if (selected.Count == 0)
                selected = words.OrderBy(_ => Random.Shared.Next()).Take(PairCount).ToList();

            var id = 0;
            foreach (var word in selected)
                _tiles.Add(new(id++, word.NativeLanguageTerm!.Trim(), false, word, !_hide));
            foreach (var word in selected)
                _tiles.Add(new(id++, word.TargetLanguageTerm!.Trim(), true, word, true));
            for (var i = _tiles.Count - 1; i > 0; i--)
            {
                var j = Random.Shared.Next(i + 1);
                (_tiles[i], _tiles[j]) = (_tiles[j], _tiles[i]);
            }
            _total = selected.Count;
            return PublishGame();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Vocabulary Matching could not load vocabulary.");
            return Publish(new(NativePhase.Error, HideNativeWords: _hide,
                ErrorKey: NativeMatchingText.LoadFailed));
        }
    }

    /// <summary>Source LoadVocabulary: plan focus words first, else resource or all owned words.</summary>
    private async Task<List<VocabularyWord>> LoadWordsAsync(
        NativeActivityLaunch launch, CancellationToken cancellationToken)
    {
        var userId = launch.UserId;
        var words = new List<VocabularyWord>();
        _usedFocus = false;
        if (!launch.FocusVocabularyIds.IsDefaultOrEmpty)
        {
            var owned = await resources.GetAllVocabularyWordsWithResourcesAsync(userId);
            words.AddRange(FocusVocabularySelection.SelectFocusWords(owned, launch.FocusVocabularyIds));
            _usedFocus = words.Count > 0;
            if (!_usedFocus)
                logger.LogWarning("Vocabulary Matching focus words were unavailable; using the source fallback selection.");
        }
        if (!_usedFocus)
        {
            if (launch.DueOnly)
                words.AddRange(await resources.GetAllVocabularyWordsWithResourcesAsync(userId));
            else
                foreach (var resourceId in launch.ResourceIds.IsDefault ? [] : launch.ResourceIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var resource = await resources.GetResourceAsync(resourceId, userId);
                    if (resource?.UserProfileId == userId && resource.Vocabulary is { } vocabulary)
                        words.AddRange(vocabulary);
                }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return words.Where(FocusVocabularySelection.IsUsableVocabularyWord)
            .GroupBy(word => word.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
    }

    private async Task<NativeMatchingState> CheckCoreAsync(CancellationToken cancellationToken)
    {
        var userId = _launch!.UserId;
        var first = _selected[0];
        var second = _selected[1];
        var isMatch = first.Word.Id == second.Word.Id && first.IsTarget != second.IsTarget;
        _response.Stop();
        if (!owner.IsActive(userId))
            return Refuse();

        // Source records against the first tile's word for both outcomes.
        var word = first.Word;
        try
        {
            using var scope = scopes.CreateScope();
            var progress = scope.ServiceProvider.GetRequiredService<IVocabularyProgressService>();
            var updated = await progress.RecordAttemptAsync(new VocabularyAttempt
            {
                VocabularyWordId = word.Id,
                UserId = userId,
                Activity = AttemptActivity,
                InputMode = "MultipleChoice",
                WasCorrect = isMatch,
                DifficultyWeight = 0.8f,
                ContextType = "Isolated",
                UserInput = isMatch ? "correct_match" : "incorrect_match",
                ExpectedAnswer = $"{word.NativeLanguageTerm} = {word.TargetLanguageTerm}",
                ResponseTimeMs = (int)_response.ElapsedMilliseconds
            });
            if (updated.IsFamiliar && owner.IsActive(userId))
                await progress.HandleVerificationProbeResultAsync(word.Id, userId, isMatch);
            scope.ServiceProvider.GetService<ProgressCacheService>()?.InvalidateVocabSummary();
            pendingWrites.MarkPending(userId);
            _errorKey = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Vocabulary Matching could not record a match attempt.");
            _errorKey = NativeText.SaveFailed;
        }
        cancellationToken.ThrowIfCancellationRequested();

        first.IsSelected = false;
        second.IsSelected = false;
        _selected.Clear();
        if (isMatch)
        {
            first.IsMatched = true;
            second.IsMatched = true;
            _matched++;
        }
        else
            _misses++;
        if (_hide)
            SetNativeVisibility(false);
        if (isMatch && _matched >= _total)
        {
            foreach (var tile in _tiles)
                tile.IsVisible = true;
            return Publish(GameState() with { Phase = NativePhase.Summary });
        }
        return PublishGame();
    }

    private void SetNativeVisibility(bool visible)
    {
        foreach (var tile in _tiles.Where(tile => !tile.IsTarget && !tile.IsMatched))
            tile.IsVisible = visible;
    }

    private NativeMatchingState GameState(bool checking = false) => new(
        NativePhase.Ready,
        Tiles: _tiles.Select(tile => new NativeMatchingTile(
            tile.Id, tile.Text, tile.IsTarget, tile.Word.Id,
            tile.IsVisible, tile.IsSelected, tile.IsMatched)).ToImmutableArray(),
        MatchedPairs: _matched, TotalPairs: _total, Misses: _misses,
        HideNativeWords: _hide, IsChecking: checking, ErrorKey: _errorKey,
        HasPendingSync: _launch is not null && pendingWrites.IsPending(_launch.UserId));

    private NativeMatchingState PublishGame(bool checking = false) =>
        Publish(GameState(checking));

    private NativeMatchingState Refuse()
    {
        _selection++;
        return Publish(new(NativePhase.Blocked, HideNativeWords: _hide,
            ErrorKey: NativeMatchingText.Unavailable));
    }

    private NativeMatchingState Publish(NativeMatchingState state)
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

    private sealed class Tile(int id, string text, bool isTarget, VocabularyWord word, bool visible)
    {
        public int Id { get; } = id;
        public string Text { get; } = text;
        public bool IsTarget { get; } = isTarget;
        public VocabularyWord Word { get; } = word;
        public bool IsVisible { get; set; } = visible;
        public bool IsSelected { get; set; }
        public bool IsMatched { get; set; }
    }
}
