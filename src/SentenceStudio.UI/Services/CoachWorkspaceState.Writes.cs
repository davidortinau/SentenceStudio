using SentenceStudio.Contracts.Coach;
using SentenceStudio.Services.Api;

namespace SentenceStudio.WebUI.Services;

/// <summary>
/// The learner's side of a proposed change: what is on offer, what stage it is at, and the six
/// actions that can move it.
/// </summary>
/// <remarks>
/// <para>
/// The rule this file exists to keep is that nothing here decides whether a change happened. The
/// server does, on every request, and the card is re-rendered from whatever it answers. An HTTP
/// 200 is evidence a request was accepted; it is not evidence a row moved, and the two come apart
/// in exactly the cases that matter — a claim lost mid-flight, a handler that failed on the way
/// back, a retry that replayed an earlier outcome.
/// </para>
/// <para>
/// The other rule is that the one-use confirmation lives here and nowhere else. It is held in a
/// field, for the length of one confirmation step, and dropped when it is spent, when it expires,
/// when the learner backs out, and when anything resets the workspace. It is never written to the
/// timeline, never put in a URL, never announced, never copied, and never rendered.
/// </para>
/// </remarks>
public sealed partial class CoachWorkspaceState
{
    /// <summary>
    /// The one-use confirmation currently in hand, if any. Never leaves this field except as a
    /// request header.
    /// </summary>
    private CoachWriteConfirmation? _confirmation;

    /// <summary>The operation the learner is currently confirming, if any.</summary>
    private string? _confirmingOperationId;

    /// <summary>The operation an approval request is in flight for, if any.</summary>
    private string? _writeBusyOperationId;

    /// <summary>The operation the last refusal belongs to, if any.</summary>
    private string? _writeErrorOperationId;

    /// <summary>
    /// Operations the server has answered not-found for.
    /// </summary>
    /// <remarks>
    /// Kept rather than deleted from the timeline. A change that has vanished still needs
    /// somewhere for its explanation to appear, and removing the card would take the sentence
    /// "that change is no longer available" off the screen along with the thing it explains. The
    /// card stays, offers nothing, and points at asking again.
    /// </remarks>
    private readonly HashSet<string> _unavailableWrites = new(StringComparer.Ordinal);

    /// <summary>
    /// The resource key for the last refusal, or null when the last action succeeded.
    /// </summary>
    /// <remarks>
    /// A key rather than a message, so the text is localized at render time and changes with the
    /// learner's language without the state service knowing anything about culture.
    /// </remarks>
    public string? WriteErrorKey { get; private set; }

    /// <summary>The operation the current refusal belongs to.</summary>
    public string? WriteErrorOperationId => _writeErrorOperationId;

    /// <summary>True while an approval, decline, confirmation, or reversal is in flight.</summary>
    public bool IsWriteBusy => _writeBusyOperationId is not null;

    /// <summary>The operation an approval request is in flight for.</summary>
    public string? WriteBusyOperationId => _writeBusyOperationId;

    /// <summary>The operation the learner is currently being asked to confirm.</summary>
    public string? ConfirmingWriteOperationId => _confirmingOperationId;

    /// <summary>When the confirmation in hand stops being redeemable.</summary>
    /// <remarks>
    /// The expiry is safe to show and useful: it is the difference between "press Confirm now"
    /// and "this will be refused". The value it belongs to is not, and is not exposed.
    /// </remarks>
    public DateTime? ConfirmationExpiresAtUtc => _confirmation?.ExpiresAtUtc;

    /// <summary>
    /// True when the write surface may render approval controls at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three conditions, and each of them is a different way the surface would otherwise be a
    /// lie. The server has to say the feature is on, because a client that guesses draws buttons
    /// whose requests answer 404. Durable history has to be on, because a proposal is bound to a
    /// conversation and every approval route is nested under one. And a conversation has to be
    /// open, because there is no such thing as an unattached proposal to approve.
    /// </para>
    /// <para>
    /// Every part defaults closed. A server that does not send the flag, an availability call that
    /// failed, and a deployment with the write tools switched off all land in the same place: a
    /// conversation the learner can read and talk to, with no approval affordances in it.
    /// </para>
    /// </remarks>
    public bool IsWriteSurfaceEnabled =>
        (_flags?.IsSamWriteAvailable ?? Availability?.IsSamWriteAvailable ?? false)
        && IsDurableHistoryEnabled
        && ConversationId is not null;

    /// <summary>
    /// The single change the learner may act on right now, or null when there is none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One at a time, deliberately. The ledger permits several proposals in a conversation and
    /// answers for each of them independently; the surface does not, because two live Accept
    /// buttons in one thread is an invitation to approve the wrong one. The newest proposal is
    /// the actionable one — it is the change the learner was last told about — and any older one
    /// still renders as a record of what was offered, without controls.
    /// </para>
    /// <para>
    /// Malformed state is not actionable. A card whose status, risk class, or approval channel the
    /// client cannot read is shown honestly and offers nothing, because offering to approve
    /// something it cannot describe is the one failure mode with no safe recovery.
    /// </para>
    /// </remarks>
    public CoachWriteOperationDto? ActiveWriteOperation { get; private set; }

    /// <summary>True when the server has said this change is not there.</summary>
    public bool IsWriteUnavailable(string? operationId) =>
        operationId is not null && _unavailableWrites.Contains(operationId);

    /// <summary>True when this operation is the one the learner may act on.</summary>
    public bool IsActionable(CoachWriteOperationDto? operation) =>
        operation is not null
        && ActiveWriteOperation is { } active
        && string.Equals(active.OperationId, operation.OperationId, StringComparison.Ordinal);

    /// <summary>
    /// Decides whether a proposal is coherent enough to offer controls for.
    /// </summary>
    /// <remarks>
    /// Every clause is a way the card could otherwise lie. An unknown status cannot be trusted to
    /// mean "waiting"; an unknown risk class cannot pick between Accept and Confirm; an approval
    /// mode that disagrees with the risk class means one of the two was written by something this
    /// build does not understand; a blank operation id addresses nothing. The honest answer to all
    /// of them is the same: show the change, offer nothing, and let the learner ask again.
    /// </remarks>
    public static bool IsWellFormed(CoachWriteOperationDto? operation) =>
        operation is not null
        && operation.OperationId.Length > 0
        && operation.Status != CoachWriteStatus.Unknown
        && operation.RiskClass != CoachWriteRiskClass.Unknown
        && (operation.RiskClass == CoachWriteRiskClass.WriteHard) == operation.RequiresConfirmation
        && string.Equals(
            operation.ApprovalMode,
            operation.RequiresConfirmation ? "confirm" : "accept",
            StringComparison.Ordinal);

    /// <summary>
    /// Re-derives which proposal, if any, the learner may act on.
    /// </summary>
    /// <remarks>
    /// Recomputed from the timeline after every merge and every action rather than tracked
    /// incrementally, because the timeline is rebuilt from the server on every reload and an
    /// incrementally-maintained pointer would survive a reload that invalidated it.
    /// </remarks>
    private void RecomputeActiveWrite()
    {
        CoachWriteOperationDto? active = null;

        foreach (var entry in _timeline)
        {
            if (entry.WriteOperation is not { } write)
            {
                continue;
            }

            if (write.Status == CoachWriteStatus.Proposed
                && IsWellFormed(write)
                && !_unavailableWrites.Contains(write.OperationId))
            {
                active = write;
            }
        }

        ActiveWriteOperation = active;

        // A confirmation belongs to one proposal. If that proposal is no longer the live one —
        // it settled, expired, or was superseded — the value in hand can do nothing except be
        // held, so it is dropped rather than kept "just in case".
        if (_confirmingOperationId is { } confirming
            && !string.Equals(active?.OperationId, confirming, StringComparison.Ordinal))
        {
            DiscardConfirmation();
        }
    }

    /// <summary>
    /// Attaches a live turn's proposal to the exchange that produced it.
    /// </summary>
    /// <remarks>
    /// Only for the session-only path. In durable mode the ledger's own rows already carry the
    /// proposal, and a second copy stamped from the turn body would show the same card twice
    /// under two different identities.
    /// </remarks>
    private void AttachTurnWrite(CoachWriteOperationDto? write, long placement)
    {
        if (write is null)
        {
            return;
        }

        // The same proposal echoed on a later turn is the same decision, not a second one. It
        // updates the card it already has rather than minting another beside it, which is the
        // session-only counterpart of the page-boundary duplicate the durable merge deduplicates.
        if (FindWrite(write.OperationId) is not null)
        {
            ReplaceWrite(write.OperationId, write);
            return;
        }

        var index = _timeline.FindLastIndex(e =>
            e.TurnSequence == placement && e.Kind == CoachTimelineKind.CoachMessage);

        if (index < 0)
        {
            index = _timeline.FindLastIndex(e => e.TurnSequence == placement);
        }

        if (index >= 0)
        {
            _timeline[index] = _timeline[index].WithWriteOperation(write);
        }
    }

    /// <summary>Accepts a reversible change and re-renders from the state that produced.</summary>
    public Task AcceptWriteAsync(string operationId, CancellationToken cancellationToken = default) =>
        RunWriteActionAsync(
            operationId,
            CoachWriteChannel.Accept,
            (client, conversationId, token) => client.AcceptWriteAsync(conversationId, operationId, token),
            "Coach_WriteApplied",
            cancellationToken);

    /// <summary>Declines a proposal. Works for both risk classes.</summary>
    public Task RejectWriteAsync(string operationId, CancellationToken cancellationToken = default) =>
        RunWriteActionAsync(
            operationId,
            CoachWriteChannel.Any,
            (client, conversationId, token) => client.RejectWriteAsync(conversationId, operationId, token),
            "Coach_WriteDeclined",
            cancellationToken);

    /// <summary>Reverses an executed change inside its window.</summary>
    public Task UndoWriteAsync(string operationId, CancellationToken cancellationToken = default) =>
        RunWriteActionAsync(
            operationId,
            CoachWriteChannel.Any,
            (client, conversationId, token) => client.UndoWriteAsync(conversationId, operationId, token),
            "Coach_WriteUndone",
            cancellationToken);

    /// <summary>
    /// Opens the confirmation step for a protected change, asking the server for the one-use
    /// value it will need.
    /// </summary>
    /// <remarks>
    /// The value is requested when the learner opens the step, not when the proposal arrives.
    /// Minting it earlier would leave a usable credential lying in memory for the whole life of a
    /// proposal the learner may never answer.
    /// </remarks>
    public async Task BeginWriteConfirmationAsync(
        string operationId,
        CancellationToken cancellationToken = default)
    {
        if (!CanStartWriteAction(operationId, CoachWriteChannel.Confirm))
        {
            return;
        }

        DiscardConfirmation();
        _writeBusyOperationId = operationId;
        ClearWriteError(operationId);
        Notify();

        try
        {
            var challenge = await _client
                .RequestWriteConfirmationAsync(ConversationId!, operationId, cancellationToken)
                .ConfigureAwait(false);

            if (challenge is null || !challenge.IsUsableAt(DateTime.UtcNow))
            {
                // The proposal is gone, belongs to somebody else, or the value arrived already
                // stale. All three are the same answer to the learner, and refreshing the card is
                // what tells them which state it is really in.
                SetWriteError(operationId, "Coach_WriteUnavailable");
                await RefreshWriteAsync(operationId, cancellationToken).ConfigureAwait(false);
                return;
            }

            _confirmation = challenge;
            _confirmingOperationId = operationId;
            Announce("Coach_WriteConfirmAnnounce");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CoachApiException ex)
        {
            SetWriteError(operationId, KeyFor(ex));
            await RefreshWriteAsync(operationId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            SetWriteError(operationId, "Coach_WriteNetworkFailed");
        }
        finally
        {
            _writeBusyOperationId = null;
            Notify();
        }
    }

    /// <summary>Backs out of a confirmation step without approving anything.</summary>
    public void CancelWriteConfirmation()
    {
        if (_confirmation is null && _confirmingOperationId is null)
        {
            return;
        }

        DiscardConfirmation();
        Notify();
    }

    /// <summary>
    /// Carries out a protected change with the value in hand.
    /// </summary>
    /// <remarks>
    /// The value is dropped before the response is read, not after. It is one-use by construction
    /// on the server, so keeping it past the request can only ever enable a retry that is
    /// guaranteed to be refused — and a refused retry that still had a live value in memory is a
    /// worse position than an honest "ask again".
    /// </remarks>
    public async Task ConfirmWriteAsync(CancellationToken cancellationToken = default)
    {
        if (_confirmation is not { } confirmation
            || _confirmingOperationId is not { } operationId
            || !CanStartWriteAction(operationId, CoachWriteChannel.Confirm))
        {
            return;
        }

        if (!confirmation.IsUsableAt(DateTime.UtcNow))
        {
            DiscardConfirmation();
            SetWriteError(operationId, "Coach_WriteConfirmExpired");
            await RefreshWriteAsync(operationId, cancellationToken).ConfigureAwait(false);
            Notify();
            return;
        }

        _writeBusyOperationId = operationId;
        ClearWriteError(operationId);
        Notify();

        try
        {
            var settled = await _client
                .ConfirmWriteAsync(ConversationId!, operationId, confirmation, cancellationToken)
                .ConfigureAwait(false);

            DiscardConfirmation();
            ApplyWriteState(operationId, settled, "Coach_WriteApplied");
        }
        catch (OperationCanceledException)
        {
            DiscardConfirmation();
            throw;
        }
        catch (CoachApiException ex)
        {
            DiscardConfirmation();
            SetWriteError(operationId, KeyFor(ex));
            await RefreshWriteAsync(operationId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            DiscardConfirmation();
            SetWriteError(operationId, "Coach_WriteNetworkFailed");
        }
        finally
        {
            _writeBusyOperationId = null;
            Notify();
        }
    }

    /// <summary>Re-reads one proposal's authoritative state and re-renders its card.</summary>
    public async Task RefreshWriteAsync(string operationId, CancellationToken cancellationToken = default)
    {
        if (ConversationId is not { } conversationId || string.IsNullOrWhiteSpace(operationId))
        {
            return;
        }

        try
        {
            var state = await _client
                .GetWriteOperationAsync(conversationId, operationId, cancellationToken)
                .ConfigureAwait(false);

            ApplyWriteState(operationId, state, announcementKey: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // A refresh that cannot reach the server leaves the card exactly as it was. The state
            // on screen came from the server too, and replacing it with a guess would be worse
            // than showing something that may be a few seconds old.
        }
    }

    /// <summary>
    /// Runs one approval action and replaces the card with whatever the server answered.
    /// </summary>
    private async Task RunWriteActionAsync(
        string operationId,
        CoachWriteChannel channel,
        Func<ICoachApiClient, string, CancellationToken, Task<CoachWriteOperationDto?>> action,
        string announcementKey,
        CancellationToken cancellationToken)
    {
        if (!CanStartWriteAction(operationId, channel))
        {
            return;
        }

        _writeBusyOperationId = operationId;
        ClearWriteError(operationId);
        Notify();

        try
        {
            var settled = await action(_client, ConversationId!, cancellationToken).ConfigureAwait(false);
            ApplyWriteState(operationId, settled, announcementKey);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CoachApiException ex)
        {
            SetWriteError(operationId, KeyFor(ex));

            // The refusal named a state; this reads what that state actually is. Without it a
            // learner who was refused because the change had already run would be left looking at
            // a card that still offers to run it.
            await RefreshWriteAsync(operationId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            SetWriteError(operationId, "Coach_WriteNetworkFailed");
        }
        finally
        {
            _writeBusyOperationId = null;
            Notify();
        }
    }

    /// <summary>
    /// Decides whether an action may start at all.
    /// </summary>
    /// <remarks>
    /// The double-submit guard is here rather than only on the button, because a disabled button
    /// is a rendering and this is the thing that actually stops a second request. The
    /// well-formedness check is here for the same reason: a card that should never have shown a
    /// control must also refuse to act on one.
    /// </remarks>
    private bool CanStartWriteAction(string operationId, CoachWriteChannel channel)
    {
        if (string.IsNullOrWhiteSpace(operationId)
            || ConversationId is null
            || !IsWriteSurfaceEnabled
            || _writeBusyOperationId is not null)
        {
            return false;
        }

        var operation = FindWrite(operationId);
        if (!IsWellFormed(operation))
        {
            return false;
        }

        // The channel has to match the risk class in both directions. An ordinary acceptance sent
        // for a protected change is refused by the server as the wrong channel, so sending it is
        // never dangerous — but it is a request the learner never asked for, produced by a control
        // that should not have existed, and the honest thing is not to send it at all.
        return channel switch
        {
            CoachWriteChannel.Accept => !operation!.RequiresConfirmation,
            CoachWriteChannel.Confirm => operation!.RequiresConfirmation,
            _ => true
        };
    }

    /// <summary>Replaces the card for one operation with the server's own answer.</summary>
    private void ApplyWriteState(string operationId, CoachWriteOperationDto? state, string? announcementKey)
    {
        if (state is null)
        {
            // The route answered not-found: gone, never existed, or somebody else's. The three are
            // indistinguishable on purpose and the card says so without guessing which. The card
            // itself is kept, because a refusal with nothing to attach it to is a refusal the
            // learner never reads.
            _unavailableWrites.Add(operationId);
            SetWriteError(operationId, "Coach_WriteUnavailable");
            RecomputeActiveWrite();
            return;
        }

        _unavailableWrites.Remove(operationId);
        ReplaceWrite(operationId, state);
        RecomputeActiveWrite();

        if (announcementKey is null)
        {
            return;
        }

        // Announced from the state, never from the action. "Applied" is said only when the server
        // says the change is in place; anything else gets the announcement its own status earns.
        Announce(state.Status switch
        {
            CoachWriteStatus.Executed => "Coach_WriteApplied",
            CoachWriteStatus.Undone => "Coach_WriteUndone",
            CoachWriteStatus.Rejected => "Coach_WriteDeclined",
            CoachWriteStatus.Expired => "Coach_WriteExpired",
            CoachWriteStatus.Executing => "Coach_WriteInDoubt",
            CoachWriteStatus.Failed => "Coach_WriteFailed",
            _ => announcementKey
        });
    }

    /// <summary>Writes one operation's state onto every timeline entry that carries it.</summary>
    /// <remarks>
    /// Every entry, not the first one found. The merge leaves one card per proposal, but that is
    /// an invariant maintained in one place and this is the code that would silently half-apply if
    /// it ever slipped — a second copy left showing "waiting for you" beside a change that has
    /// already been applied is worse than either state on its own, because the learner can act on
    /// it. Updating them all makes the two failures collapse into one.
    /// </remarks>
    private void ReplaceWrite(string operationId, CoachWriteOperationDto? state)
    {
        for (var i = 0; i < _timeline.Count; i++)
        {
            if (_timeline[i].WriteOperation is { } write
                && string.Equals(write.OperationId, operationId, StringComparison.Ordinal))
            {
                _timeline[i] = _timeline[i].WithWriteOperation(state);
            }
        }
    }

    /// <summary>Finds one operation's current state on the timeline.</summary>
    private CoachWriteOperationDto? FindWrite(string operationId) => _timeline
        .Select(entry => entry.WriteOperation)
        .FirstOrDefault(write => write is not null
                                 && string.Equals(write.OperationId, operationId, StringComparison.Ordinal));

    private void SetWriteError(string operationId, string resourceKey)
    {
        _writeErrorOperationId = operationId;
        WriteErrorKey = resourceKey;
    }

    private void ClearWriteError(string operationId)
    {
        if (string.Equals(_writeErrorOperationId, operationId, StringComparison.Ordinal))
        {
            _writeErrorOperationId = null;
            WriteErrorKey = null;
        }
    }

    /// <summary>Drops the confirmation in hand and closes the step it belonged to.</summary>
    private void DiscardConfirmation()
    {
        _confirmation = null;
        _confirmingOperationId = null;
    }

    /// <summary>
    /// Clears every write-surface field. Called whenever the workspace is reset or a different
    /// conversation is opened.
    /// </summary>
    /// <remarks>
    /// The confirmation is dropped here as well as at the end of the step it belongs to. A reload
    /// or a conversation switch is exactly the moment a stale value would otherwise survive into a
    /// context it was never issued for.
    /// </remarks>
    private void ResetWrites()
    {
        DiscardConfirmation();
        ActiveWriteOperation = null;
        _writeBusyOperationId = null;
        _writeErrorOperationId = null;
        WriteErrorKey = null;
        _unavailableWrites.Clear();
    }

    /// <summary>
    /// Maps a refusal onto the sentence the learner reads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately coarse. The server's refusals are already written to be indistinguishable
    /// where it matters — a change that never existed, one owned by somebody else, and one
    /// addressed through the wrong conversation all answer the same way — and a client that split
    /// them into different sentences would undo that. The detail carried on the exception is for
    /// diagnostics and is never shown.
    /// </para>
    /// <para>
    /// Every branch ends in "ask Sam again", because that is genuinely what recovers each of
    /// them: a fresh proposal is cheap, and a stale one can never be revived.
    /// </para>
    /// </remarks>
    /// <summary>Which approval channel an action belongs to.</summary>
    private enum CoachWriteChannel
    {
        /// <summary>Declines and reversals, which both risk classes share.</summary>
        Any = 0,

        /// <summary>The ordinary acceptance a reversible change uses.</summary>
        Accept,

        /// <summary>The protected confirmation a hard change uses.</summary>
        Confirm
    }

    private static string KeyFor(CoachApiException ex) => ex.StatusCode switch
    {
        System.Net.HttpStatusCode.NotFound => "Coach_WriteUnavailable",
        System.Net.HttpStatusCode.TooManyRequests => "Coach_WriteLimited",
        System.Net.HttpStatusCode.UnprocessableEntity => "Coach_WriteRefused",
        System.Net.HttpStatusCode.Conflict => "Coach_WriteRefused",
        System.Net.HttpStatusCode.Unauthorized => "Coach_WriteUnavailable",
        System.Net.HttpStatusCode.Forbidden => "Coach_WriteUnavailable",
        _ => "Coach_WriteNetworkFailed"
    };
}
