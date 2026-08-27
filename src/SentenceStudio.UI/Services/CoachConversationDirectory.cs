using SentenceStudio.Contracts.Coach;
using SentenceStudio.Services.Api;

namespace SentenceStudio.WebUI.Services;

/// <summary>Whether this deployment keeps durable coach history.</summary>
/// <remarks>
/// The zero value is <see cref="Unknown"/> so a directory that has not resolved availability yet never claims the feature is
/// on. Showing a conversation list that cannot load is worse than showing none.
/// </remarks>
public enum CoachDurableHistoryAvailability
{
    /// <summary>Availability has not been resolved yet.</summary>
    Unknown = 0,

    /// <summary>The conversations route answered. Durable history is on for this learner.</summary>
    Available,

    /// <summary>The route answered 404. Either the flag is off or no owner resolved.</summary>
    Unavailable
}

/// <summary>
/// The learner's own coach conversations: the list, the selection, and the lifecycle operations
/// that act on a whole thread rather than on one turn.
/// </summary>
/// <remarks>
/// <para>
/// Kept separate from <see cref="CoachWorkspaceState"/> on purpose. The workspace is one open
/// conversation and the run in flight inside it; this is the shelf that conversation was taken
/// from. Folding the two together would mean a failed rename could put the open thread into an
/// error state, and closing the workspace would drop the list the learner is browsing.
/// </para>
/// <para>
/// Registered <b>scoped</b>, like the workspace, so one circuit never sees another learner's
/// shelf.
/// </para>
/// </remarks>
public sealed class CoachConversationDirectory
{
    /// <summary>
    /// One screenful. Large enough that most learners never page, small enough that the first
    /// paint is not waiting on a year of history.
    /// </summary>
    public const int PageSize = 25;

    private readonly ICoachApiClient _client;
    private readonly CoachFeatureFlags _flags;
    private readonly List<CoachConversationDto> _conversations = new();

    public CoachConversationDirectory(ICoachApiClient client, CoachFeatureFlags? flags = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _flags = flags ?? new CoachFeatureFlags(_client);
    }

    /// <summary>Raised whenever any observable property changes. Components call StateHasChanged.</summary>
    public event Action? Changed;

    /// <summary>Whether durable history answered at all. Drives every affordance in the UI.</summary>
    public CoachDurableHistoryAvailability Availability { get; private set; }
        = CoachDurableHistoryAvailability.Unknown;

    /// <summary>Shorthand for "the conversation surface is real here".</summary>
    public bool IsDurableHistoryAvailable => Availability == CoachDurableHistoryAvailability.Available;

    /// <summary>
    /// The learner's conversations, most recently updated first.
    /// </summary>
    /// <remarks>
    /// Sorted here rather than trusted from the wire. The server pages newest-first, but a
    /// conversation the learner just spoke into is newer than the page it arrived in, and
    /// re-sorting locally is what keeps it at the top without a refetch.
    /// </remarks>
    public IReadOnlyList<CoachConversationDto> Conversations => _conversations;

    /// <summary>The conversation the workspace is currently showing, if any.</summary>
    public string? SelectedConversationId { get; private set; }

    /// <summary>
    /// The newest conversation that still accepts turns, or null when the learner has none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the answer to "which thread was I in", asked by a surface that has no id of its own
    /// to resume — the Sam overlay after a page reload, where the circuit that held the id is
    /// gone. Without it the overlay would open a brand new conversation on every reload, and the
    /// durable ledger would fill with empty threads while the learner's actual history sat one
    /// call away.
    /// </para>
    /// <para>
    /// Closed conversations are skipped. A closed thread stays readable, but it refuses new turns,
    /// so resuming into one would hand the learner a composer that cannot send. Starting a fresh
    /// conversation is the honest answer when every thread is closed.
    /// </para>
    /// <para>
    /// Reads <see cref="Conversations"/>, which <see cref="Sort"/> keeps newest-updated first, so
    /// "most recent" means the thread last spoken into rather than the one created last.
    /// </para>
    /// </remarks>
    public string? MostRecentResumableId
    {
        get
        {
            foreach (var conversation in _conversations)
            {
                if (!conversation.IsClosed)
                {
                    return conversation.ConversationId;
                }
            }

            return null;
        }
    }

    /// <summary>True while the first page is loading. Distinct from paging.</summary>
    public bool IsLoading { get; private set; }

    /// <summary>True while an older page is loading.</summary>
    public bool IsLoadingMore { get; private set; }

    /// <summary>True while a rename, close, delete or export is in flight.</summary>
    public bool IsBusy { get; private set; }

    /// <summary>True when the list has been loaded at least once.</summary>
    public bool HasLoaded { get; private set; }

    /// <summary>The cursor for the next older page, or null when the list is complete.</summary>
    public string? NextCursor { get; private set; }

    /// <summary>True when there is an older page to fetch.</summary>
    public bool HasMore => NextCursor is not null;

    /// <summary>
    /// A resource key naming what went wrong, or null. Never a server message: problem titles and
    /// details are diagnostics and are not written for a learner to read.
    /// </summary>
    public string? ErrorKey { get; private set; }

    /// <summary>True when the last attempt failed for want of a network rather than a reason.</summary>
    public bool IsOffline { get; private set; }

    // ================================================================ availability / load

    /// <summary>
    /// Resolves durable history once and loads the first page when it is on.
    /// </summary>
    /// <remarks>
    /// <see cref="CoachAvailabilityResponse.IsDurableHistoryAvailable"/> decides whether to ask.
    /// It replaced probing with a list call and reading the 404, which could not tell "the feature
    /// is off" from "no owner resolved" and so charged every learner without history a request to
    /// find out they had none.
    /// </remarks>
    public async Task<CoachDurableHistoryAvailability> EnsureLoadedAsync(
        CancellationToken cancellationToken = default)
    {
        if (HasLoaded && Availability != CoachDurableHistoryAvailability.Unknown)
        {
            return Availability;
        }

        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        return Availability;
    }

    /// <summary>Reloads the newest page, discarding any pages already walked.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        ErrorKey = null;
        IsOffline = false;
        Notify();

        try
        {
            await _flags.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

            if (!_flags.IsDurableHistoryAvailable)
            {
                // The server says the feature is not on for this learner, so there is nothing to
                // ask for. Sending the request anyway would only produce a 404 we already know
                // the answer to.
                Availability = CoachDurableHistoryAvailability.Unavailable;
                _conversations.Clear();
                NextCursor = null;
                SelectedConversationId = null;
                return;
            }

            var page = await _client.ListConversationsAsync(PageSize, cursor: null, cancellationToken)
                .ConfigureAwait(false);

            if (page is null)
            {
                // The flag said yes and the route says no: switched off between the two calls, or
                // no owner resolved. The route is the one holding the data, so it wins.
                Availability = CoachDurableHistoryAvailability.Unavailable;
                _conversations.Clear();
                NextCursor = null;
                SelectedConversationId = null;
                return;
            }

            Availability = CoachDurableHistoryAvailability.Available;
            _conversations.Clear();
            _conversations.AddRange(page.Items);
            NextCursor = page.NextCursor;
            Sort();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            IsOffline = true;
            ErrorKey = "Coach_ConversationsOffline";
        }
        catch (CoachApiException)
        {
            ErrorKey = "Coach_ConversationsLoadFailed";
        }
        finally
        {
            IsLoading = false;
            HasLoaded = true;
            Notify();
        }
    }

    /// <summary>
    /// Appends the next older page.
    /// </summary>
    /// <remarks>
    /// A rejected cursor drops back to a clean reload rather than surfacing an error. Cursors go
    /// stale for ordinary reasons — a conversation was deleted out from under the page — and the
    /// learner has no way to act on "invalid cursor", so the only useful response is to fetch a
    /// list that is valid.
    /// </remarks>
    public async Task LoadMoreAsync(CancellationToken cancellationToken = default)
    {
        if (NextCursor is null || IsLoadingMore)
        {
            return;
        }

        IsLoadingMore = true;
        ErrorKey = null;
        Notify();

        try
        {
            var page = await _client.ListConversationsAsync(PageSize, NextCursor, cancellationToken)
                .ConfigureAwait(false);

            if (page is null)
            {
                Availability = CoachDurableHistoryAvailability.Unavailable;
                return;
            }

            foreach (var item in page.Items)
            {
                Upsert(item, notify: false);
            }

            NextCursor = page.NextCursor;
            Sort();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            IsOffline = true;
            ErrorKey = "Coach_ConversationsOffline";
        }
        catch (CoachApiException ex) when (ex.ProblemType == CoachProblemTypes.InvalidCursor)
        {
            NextCursor = null;
            IsLoadingMore = false;
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (CoachApiException)
        {
            ErrorKey = "Coach_ConversationsLoadFailed";
        }
        finally
        {
            IsLoadingMore = false;
            Notify();
        }
    }

    // ================================================================ lifecycle

    /// <summary>
    /// Creates a new conversation and selects it.
    /// </summary>
    /// <remarks>
    /// New always means new. The idempotency key is generated per call, so a learner who asks
    /// twice gets two threads, while a retry of the <em>same</em> call gets the one it already
    /// made.
    /// </remarks>
    public async Task<CoachConversationDto?> CreateAsync(
        string? targetLanguageCode = null,
        CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        ErrorKey = null;
        Notify();

        try
        {
            var created = await _client.CreateConversationAsync(
                new StartCoachConversationRequest
                {
                    IdempotencyKey = NewHandle(),
                    TargetLanguageCode = targetLanguageCode
                },
                cancellationToken).ConfigureAwait(false);

            Upsert(created, notify: false);
            SelectedConversationId = created.ConversationId;
            Availability = CoachDurableHistoryAvailability.Available;
            return created;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            IsOffline = true;
            ErrorKey = "Coach_ConversationsOffline";
            return null;
        }
        catch (CoachApiException ex) when (ex.ProblemType == CoachProblemTypes.Unavailable)
        {
            Availability = CoachDurableHistoryAvailability.Unavailable;
            return null;
        }
        catch (CoachApiException)
        {
            ErrorKey = "Coach_ConversationCreateFailed";
            return null;
        }
        finally
        {
            IsBusy = false;
            Notify();
        }
    }

    /// <summary>
    /// Renames a conversation, refusing to overwrite a title that changed underneath.
    /// </summary>
    /// <returns>True when the rename landed.</returns>
    public async Task<bool> RenameAsync(
        string conversationId,
        string title,
        CancellationToken cancellationToken = default)
        => await UpdateAsync(conversationId, title: title, close: null, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Closes a conversation. It keeps all its history and only refuses new turns.</summary>
    public async Task<bool> CloseAsync(string conversationId, CancellationToken cancellationToken = default)
        => await UpdateAsync(conversationId, title: null, close: true, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Reopens a closed conversation so it accepts turns again.</summary>
    public async Task<bool> ReopenAsync(string conversationId, CancellationToken cancellationToken = default)
        => await UpdateAsync(conversationId, title: null, close: false, cancellationToken)
            .ConfigureAwait(false);

    private async Task<bool> UpdateAsync(
        string conversationId,
        string? title,
        bool? close,
        CancellationToken cancellationToken)
    {
        var known = Find(conversationId);

        IsBusy = true;
        ErrorKey = null;
        Notify();

        try
        {
            var updated = await _client.UpdateConversationAsync(
                conversationId,
                new UpdateCoachConversationRequest
                {
                    ExpectedStateVersion = known?.StateVersion,
                    Title = title,
                    Close = close
                },
                cancellationToken).ConfigureAwait(false);

            Upsert(updated, notify: false);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            IsOffline = true;
            ErrorKey = "Coach_ConversationsOffline";
            return false;
        }
        catch (CoachApiException ex) when (ex.ProblemType == CoachProblemTypes.ConversationStateConflict)
        {
            // Somebody else — another tab, another device — already changed this conversation.
            // Refetch so the learner is looking at what is actually stored before deciding again,
            // rather than being told to retry against state they cannot see.
            ErrorKey = "Coach_ConversationConflict";
            await ReloadOneAsync(conversationId, cancellationToken).ConfigureAwait(false);
            return false;
        }
        catch (CoachApiException ex) when (ex.ProblemType == CoachProblemTypes.ConversationNotFound)
        {
            ErrorKey = "Coach_ConversationGone";
            Remove(conversationId, notify: false);
            return false;
        }
        catch (CoachApiException)
        {
            ErrorKey = "Coach_ConversationUpdateFailed";
            return false;
        }
        finally
        {
            IsBusy = false;
            Notify();
        }
    }

    /// <summary>
    /// Deletes a conversation and drops it from the list.
    /// </summary>
    /// <remarks>
    /// A 404 is the desired end state, not a failure, so the client treats "already gone" as
    /// done. Deleting a conversation removes the transcript only: plan revisions, progress and
    /// saved memories are separate records and are untouched.
    /// </remarks>
    public async Task<bool> DeleteAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        ErrorKey = null;
        Notify();

        try
        {
            await _client.DeleteConversationAsync(conversationId, cancellationToken).ConfigureAwait(false);
            Remove(conversationId, notify: false);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            IsOffline = true;
            ErrorKey = "Coach_ConversationsOffline";
            return false;
        }
        catch (CoachApiException)
        {
            ErrorKey = "Coach_ConversationDeleteFailed";
            return false;
        }
        finally
        {
            IsBusy = false;
            Notify();
        }
    }

    /// <summary>
    /// Opens the export stream for a conversation. The caller owns the stream.
    /// </summary>
    /// <remarks>
    /// Returned as a stream and handed straight to the browser's download path. Nothing is
    /// written to a temporary file and nothing is buffered into a string: a transcript is the
    /// most sensitive thing this feature holds, and a plaintext copy left behind on disk is a
    /// copy nobody asked for.
    /// </remarks>
    public async Task<Stream?> ExportAsync(
        string conversationId,
        CoachExportFormat format,
        CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        ErrorKey = null;
        Notify();

        try
        {
            return await _client.ExportConversationAsync(conversationId, format, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            IsOffline = true;
            ErrorKey = "Coach_ConversationsOffline";
            return null;
        }
        catch (CoachApiException)
        {
            ErrorKey = "Coach_ConversationExportFailed";
            return null;
        }
        finally
        {
            IsBusy = false;
            Notify();
        }
    }

    /// <summary>The default file name for an export, matching what the server streams.</summary>
    public static string ExportFileName(string conversationId, CoachExportFormat format) =>
        $"coach-conversation-{conversationId}.{(format == CoachExportFormat.Markdown ? "md" : "json")}";

    // ================================================================ selection / cache

    /// <summary>Selects a conversation without loading it. The workspace does the loading.</summary>
    public void Select(string? conversationId)
    {
        if (SelectedConversationId == conversationId)
        {
            return;
        }

        SelectedConversationId = conversationId;
        Notify();
    }

    /// <summary>Refetches one conversation so its title and state version are current.</summary>
    public async Task<CoachConversationDto?> ReloadOneAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var current = await _client.GetConversationAsync(conversationId, cancellationToken)
                .ConfigureAwait(false);

            if (current is null)
            {
                Remove(conversationId, notify: true);
                return null;
            }

            Upsert(current, notify: true);
            return current;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            IsOffline = true;
            Notify();
            return null;
        }
        catch (CoachApiException)
        {
            return null;
        }
    }

    /// <summary>The conversation with this id, when the list already holds it.</summary>
    public CoachConversationDto? Find(string? conversationId) => conversationId is null
        ? null
        : _conversations.FirstOrDefault(c =>
            string.Equals(c.ConversationId, conversationId, StringComparison.Ordinal));

    /// <summary>The currently selected conversation, when the list already holds it.</summary>
    public CoachConversationDto? Selected => Find(SelectedConversationId);

    /// <summary>
    /// Adds or replaces a conversation in the list and re-sorts.
    /// </summary>
    /// <remarks>
    /// Public because a completed turn moves a conversation to the top of the list, and the
    /// workspace — not the directory — is what learns that a turn completed.
    /// </remarks>
    public void Upsert(CoachConversationDto conversation, bool notify = true)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        var index = _conversations.FindIndex(c =>
            string.Equals(c.ConversationId, conversation.ConversationId, StringComparison.Ordinal));

        if (index >= 0)
        {
            _conversations[index] = conversation;
        }
        else
        {
            _conversations.Add(conversation);
        }

        Sort();

        if (notify)
        {
            Notify();
        }
    }

    /// <summary>Drops a conversation from the list, clearing the selection when it was selected.</summary>
    public void Remove(string conversationId, bool notify = true)
    {
        _conversations.RemoveAll(c =>
            string.Equals(c.ConversationId, conversationId, StringComparison.Ordinal));

        if (string.Equals(SelectedConversationId, conversationId, StringComparison.Ordinal))
        {
            SelectedConversationId = null;
        }

        if (notify)
        {
            Notify();
        }
    }

    /// <summary>Clears the error so a dismissed message does not reappear on the next render.</summary>
    public void ClearError()
    {
        if (ErrorKey is null && !IsOffline)
        {
            return;
        }

        ErrorKey = null;
        IsOffline = false;
        Notify();
    }

    /// <summary>Resets everything, including the resolved availability. Used on sign-out.</summary>
    public void Reset()
    {
        _conversations.Clear();
        SelectedConversationId = null;
        NextCursor = null;
        ErrorKey = null;
        IsOffline = false;
        IsLoading = false;
        IsLoadingMore = false;
        IsBusy = false;
        HasLoaded = false;
        Availability = CoachDurableHistoryAvailability.Unknown;

        // Sign-out can change who is asking, and the flags are answered per learner, so the
        // cached answer goes with the rest of the previous learner's state.
        _flags.Reset();

        Notify();
    }

    /// <summary>A fresh opaque handle for an idempotency key or an operation id.</summary>
    public static string NewHandle() => Guid.NewGuid().ToString("n");

    // Ties are broken by id so the order is total and a re-render never reshuffles two
    // conversations that were updated in the same millisecond.
    private void Sort() => _conversations.Sort(static (left, right) =>
    {
        var byUpdated = right.UpdatedAtUtc.CompareTo(left.UpdatedAtUtc);
        return byUpdated != 0
            ? byUpdated
            : string.CompareOrdinal(left.ConversationId, right.ConversationId);
    });

    private void Notify() => Changed?.Invoke();
}
