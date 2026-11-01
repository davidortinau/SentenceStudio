using System.Collections.Immutable;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.Native;

/// <summary>
/// Flashcard Preview (source UI/Pages/FlashcardActivity.razor, activities.md 16),
/// opened from the Dashboard insight "Preview plan vocabulary". Cards are the
/// owner's cached plan PreviewWords for the launch date: target term on the
/// front, tap flips to the native term, swipe/buttons answer Incorrect or
/// Correct after the source's 160 ms throw, and a summary lists every result.
/// Results are in memory only: no timer, no progress, no session, no rows.
/// Unlike the source, the plan read is bound to the launch owner and refused
/// when that owner is absent or no longer active.
/// </summary>
public sealed class NativeFlashcardPresenter(
    NativeActivityOwner owner,
    NativePlanLeaseFactory plans,
    ILogger<NativeFlashcardPresenter> logger) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly NativeActivityLoad _load = new();
    private readonly List<NativeFlashcardResult> _results = [];
    private ImmutableArray<NativeFlashcard> _cards = [];
    private NativeActivityLaunch? _launch;
    private NativeFlashcardState _state = new(NativePhase.Idle);
    private int _index;
    private bool _flipped;
    private long _answer;
    private bool _exited;
    private bool _disposed;

    /// <summary>Source AnimateSwipeAsync waits 160 ms before recording the answer.</summary>
    public TimeSpan AnswerDelay { get; set; } = TimeSpan.FromMilliseconds(160);

    public NativeFlashcardState State =>
        _launch is not null && !owner.IsActive(_launch.UserId)
            && _state.Destination != NativeDestination.TodaysPlan
            ? new(NativePhase.Blocked, ErrorKey: NativeFlashcardText.Unavailable)
            : _state;

    public event Action<NativeFlashcardState>? StateChanged;

    public async Task<NativeFlashcardState> OpenAsync(
        NativeActivityLaunch launch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launch);
        NativeActivityLoadTicket ticket;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_launch is not null)
                return State;
            _launch = launch;
            if (launch.Route != NativeActivityCatalog.FlashcardPreview
                || launch.IsPlanItem || !owner.IsActive(launch.UserId))
                return Refuse();
            Publish(new(NativePhase.Loading));
            ticket = _load.Begin(_lifetime.Token);
        }
        finally
        {
            _gate.Release();
        }

        ImmutableArray<NativeFlashcard> cards;
        try
        {
            cards = await ReadPreviewWordsAsync(launch, ticket.Token);
        }
        catch (OperationCanceledException)
        {
            // Exit, a refused owner or disposal cancelled the read.
            return State;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Flashcard Preview could not read the cached plan.");
            return await ApplyAsync(ticket, () => Publish(new(NativePhase.Error,
                ErrorKey: NativeFlashcardText.LoadFailed)));
        }

        return await ApplyAsync(ticket, () =>
        {
            if (cards.IsEmpty)
                return Publish(new(NativePhase.Empty, ErrorKey: NativeFlashcardText.NoVocabulary));
            _cards = cards;
            return PublishDeck();
        });
    }

    /// <summary>Tap on the card (source movement within 12 px) flips it.</summary>
    public async Task<NativeFlashcardState> FlipAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_launch is null || !owner.IsActive(_launch.UserId))
                return Refuse();
            if (_exited || _state.Phase != NativePhase.Ready || _state.IsAnimating)
                return State;
            _flipped = !_flipped;
            return PublishDeck();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Swipe right / Correct button (true) or swipe left / Incorrect button
    /// (false): the card is thrown for <see cref="AnswerDelay"/>, then the
    /// result is recorded in memory and the next card (or the summary) shows.
    /// </summary>
    public async Task<NativeFlashcardState> AnswerAsync(
        bool wasCorrect, CancellationToken cancellationToken = default)
    {
        long answer;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_launch is null || !owner.IsActive(_launch.UserId))
                return Refuse();
            if (_exited || _state.Phase != NativePhase.Ready || _state.IsAnimating
                || _index >= _cards.Length)
                return State;
            answer = ++_answer;
            PublishDeck(wasCorrect);
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            if (AnswerDelay > TimeSpan.Zero)
                await Task.Delay(AnswerDelay, _lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            return _state;
        }

        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            if (_exited || answer != _answer || _state.Phase != NativePhase.Ready)
                return State;
            if (_launch is null || !owner.IsActive(_launch.UserId))
                return Refuse();
            _results.Add(new(_cards[_index], wasCorrect));
            _flipped = false;
            _index++;
            return _index >= _cards.Length
                ? Publish(DeckState() with { Phase = NativePhase.Summary })
                : PublishDeck();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Back, Go Back, Done or a native pop: there is nothing to save.</summary>
    public async Task<NativeFlashcardState> ExitAsync(CancellationToken cancellationToken = default)
    {
        _load.Cancel();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _exited = true;
            _answer++;
            return Publish(_state with { Destination = NativeDestination.TodaysPlan, PendingAnswer = null });
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ImmutableArray<NativeFlashcard>> ReadPreviewWordsAsync(
        NativeActivityLaunch launch, CancellationToken cancellationToken)
    {
        using var lease = plans.ForOwner(launch.UserId);
        var plan = await lease.Service.GetTodayAsync(cancellationToken);
        // Source GetCachedPlanAsync(date): only that date's plan supplies cards.
        if (plan is null || plan.GeneratedForDate != launch.PlanDate
            || plan.Narrative?.VocabInsight?.PreviewWords is not { Count: > 0 } words)
            return [];
        return words
            .Where(word => !string.IsNullOrWhiteSpace(word.TargetTerm)
                && !string.IsNullOrWhiteSpace(word.NativeTerm))
            .Select((word, index) => new NativeFlashcard(
                index, word.WordId, word.TargetTerm, word.NativeTerm))
            .ToImmutableArray();
    }

    private async Task<NativeFlashcardState> ApplyAsync(
        NativeActivityLoadTicket ticket, Func<NativeFlashcardState> apply)
    {
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            if (_exited || !_load.IsCurrent(ticket))
                return State;
            if (_launch is null || !owner.IsActive(_launch.UserId))
                return Refuse();
            return apply();
        }
        finally
        {
            _gate.Release();
        }
    }

    private NativeFlashcardState DeckState(bool? pending = null) => new(
        NativePhase.Ready,
        Cards: _cards,
        CurrentIndex: _index,
        IsFlipped: _flipped,
        Results: [.. _results],
        PendingAnswer: pending);

    private NativeFlashcardState PublishDeck(bool? pending = null) => Publish(DeckState(pending));

    private NativeFlashcardState Refuse()
    {
        _load.Cancel();
        _answer++;
        return Publish(new(NativePhase.Blocked, ErrorKey: NativeFlashcardText.Unavailable));
    }

    private NativeFlashcardState Publish(NativeFlashcardState state)
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
        _load.Dispose();
        _lifetime.Cancel();
    }
}
