using SentenceStudio.Contracts.Coach;
using SentenceStudio.Services.Api;

namespace SentenceStudio.WebUI.Services;

/// <summary>What one report attempt produced, from the learner's point of view.</summary>
/// <remarks>
/// Deliberately three outcomes and not a boolean. "It already was reported" is a success the
/// control renders exactly as a fresh report — the learner's intent was satisfied either way —
/// while "it could not be reported" has to say so rather than quietly settling into the reported
/// state and lying about where the feedback went.
/// </remarks>
public enum CoachReportOutcome
{
    /// <summary>The report was recorded by this attempt.</summary>
    Recorded = 0,

    /// <summary>A report already existed. Nothing changed, and nothing was lost.</summary>
    AlreadyReported,

    /// <summary>The report could not be filed. The control returns to its resting state.</summary>
    Failed
}

/// <summary>
/// The learner's own reports of Sam's responses.
/// </summary>
/// <remarks>
/// <para>
/// Kept beside the durable half rather than inside it because a report is not part of the
/// transcript: the ledger records what was said, and this records what the learner thought of it.
/// Merging the two would have meant a message row that changes after it was written, which is the
/// one thing the message ledger promises never to do.
/// </para>
/// <para>
/// <b>The reported set is server state, not client state.</b> It is read on entry and after a
/// resume, so a browser that forgot everything still renders "Reported for review" on exactly the
/// responses it did before the reload. Nothing here is inferred from what the learner did in this
/// circuit.
/// </para>
/// </remarks>
public sealed partial class CoachWorkspaceState
{
    private readonly HashSet<string> _reportedResponses = new(StringComparer.Ordinal);

    /// <summary>
    /// The report panels currently open, held by control instance rather than by message id.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Deliberately not keyed on the message.</b> A message id recorded here would outlive the
    /// control that put it there — a positional rebind after a history page loads, a reconciled
    /// timeline, or an account switch all replace which response a mounted control is showing. The
    /// registry would then claim a panel is open for a message nobody can see, and Escape would
    /// defer forever to a surface that is not on screen. The instance is the only identity that is
    /// true for exactly as long as the panel is rendered.
    /// </para>
    /// <para>
    /// Reference equality, so two controls are never conflated by an <c>Equals</c> a component
    /// does not define, and so a control that forgot to deregister cannot be silently replaced by
    /// its successor.
    /// </para>
    /// </remarks>
    private readonly HashSet<object> _openReportPanels = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Raised when an outer surface hands an Escape press to whatever report panel is open.
    /// </summary>
    /// <remarks>
    /// The overlay's Escape listener sits on <c>document</c> and fires for presses that never went
    /// near the panel — the composer, the header, the transcript itself. Without this the overlay
    /// would correctly decline to collapse (something inner owns the press) and then nothing would
    /// close, which spends a press and undoes nothing.
    /// </remarks>
    public event Action? ReportPanelCloseRequested;

    /// <summary>True while at least one inline report panel is open.</summary>
    public bool IsReportPanelOpen => _openReportPanels.Count > 0;

    /// <summary>
    /// Records that <paramref name="owner"/>'s panel opened or closed.
    /// </summary>
    /// <remarks>
    /// Does not raise <c>Changed</c>. Nothing renders from this — it exists so an outer Escape
    /// handler can ask whether an inner surface owns the press — and notifying would re-enter the
    /// very controls whose own state transition is mid-flight.
    /// </remarks>
    public void SetReportPanelOpen(object owner, bool open)
    {
        ArgumentNullException.ThrowIfNull(owner);

        if (open)
        {
            _openReportPanels.Add(owner);
        }
        else
        {
            _openReportPanels.Remove(owner);
        }
    }

    /// <summary>
    /// Asks every open report panel to close, as an outer Escape handler standing aside.
    /// </summary>
    /// <returns>True when there was something to close.</returns>
    public bool RequestCloseReportPanels()
    {
        if (_openReportPanels.Count == 0)
        {
            return false;
        }

        ReportPanelCloseRequested?.Invoke();
        return true;
    }

    /// <summary>
    /// True when this deployment accepts reports and the learner may be offered the control.
    /// </summary>
    /// <remarks>
    /// Starts false and is set only by a route that actually answered. A configuration flag on
    /// some other host is not evidence, and offering a control that will 404 is worse than not
    /// offering it: the learner would press it, be told nothing worked, and reasonably conclude
    /// the app is broken rather than that the feature is off.
    /// </remarks>
    public bool IsReportingAvailable { get; private set; }

    /// <summary>The coach responses this learner has already reported, by message id.</summary>
    public IReadOnlyCollection<string> ReportedResponses => _reportedResponses;

    /// <summary>True when this response has been reported.</summary>
    public bool IsResponseReported(string? messageId) =>
        messageId is { Length: > 0 } id && _reportedResponses.Contains(id);

    /// <summary>
    /// Reads which of this conversation's responses the learner already reported.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Failure is silent and leaves the control withheld. A learner cannot act on "the report
    /// availability probe failed", and a banner about it would push a real conversation off the
    /// screen to report a fact about a secondary control.
    /// </para>
    /// <para>
    /// Only meaningful for a durable conversation: a session-only turn has no server-side message
    /// identity to report, so there is nothing to read and nothing to offer.
    /// </para>
    /// </remarks>
    public async Task LoadReportedResponsesAsync(CancellationToken cancellationToken = default)
    {
        if (!IsDurableHistoryEnabled || ConversationId is not { Length: > 0 } conversationId)
        {
            SetReportingUnavailable();
            return;
        }

        try
        {
            var reported = await _client.GetReportedResponsesAsync(conversationId, cancellationToken)
                .ConfigureAwait(false);

            if (reported is null)
            {
                // The route answered 404: reporting is off here. Withhold the control rather than
                // showing one that cannot work.
                SetReportingUnavailable();
                return;
            }

            IsReportingAvailable = true;
            _reportedResponses.Clear();

            foreach (var id in reported.MessageIds)
            {
                if (!string.IsNullOrWhiteSpace(id))
                {
                    _reportedResponses.Add(id);
                }
            }

            Notify();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CoachApiException)
        {
            SetReportingUnavailable();
        }
        catch (HttpRequestException)
        {
            SetReportingUnavailable();
        }
    }

    /// <summary>
    /// Reports one of Sam's responses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reported state is applied from the server's answer, never optimistically. A control
    /// that showed "Reported for review" because a request was sent is a control that will
    /// eventually claim feedback reached a person when it did not — and unlike an optimistic
    /// message, there is no later correction the learner would ever see.
    /// </para>
    /// <para>
    /// <see cref="CoachReportOutcome.AlreadyReported"/> is treated as success and settles the
    /// control the same way. Two devices, a double press, or a reload all land there, and each of
    /// them is a learner whose intent was already carried out.
    /// </para>
    /// </remarks>
    public async Task<CoachReportOutcome> ReportResponseAsync(
        string messageId,
        CoachResponseReportReason reason,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(messageId)
            || !IsReportingAvailable
            || ConversationId is not { Length: > 0 } conversationId)
        {
            return CoachReportOutcome.Failed;
        }

        if (_reportedResponses.Contains(messageId))
        {
            return CoachReportOutcome.AlreadyReported;
        }

        try
        {
            var result = await _client
                .ReportResponseAsync(
                    conversationId,
                    messageId,
                    new CoachResponseReportRequest { Reason = reason },
                    cancellationToken)
                .ConfigureAwait(false);

            if (result is null)
            {
                // Reporting was switched off, or this conversation is not readable any more. Both
                // answer 404, and both mean the control should stop being offered rather than
                // failing again on the next press.
                SetReportingUnavailable();
                return CoachReportOutcome.Failed;
            }

            _reportedResponses.Add(result.MessageId);
            Notify();

            return result.State == CoachResponseReportState.AlreadyReported
                ? CoachReportOutcome.AlreadyReported
                : CoachReportOutcome.Recorded;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CoachApiException)
        {
            return CoachReportOutcome.Failed;
        }
        catch (HttpRequestException)
        {
            return CoachReportOutcome.Failed;
        }
    }

    /// <summary>
    /// Withdraws the control and forgets the reported set.
    /// </summary>
    /// <remarks>
    /// Both halves matter. Leaving the set behind while withdrawing the control would let a later
    /// conversation — or a later account — inherit another learner's reported responses the moment
    /// the control came back.
    /// </remarks>
    private void SetReportingUnavailable()
    {
        if (!IsReportingAvailable && _reportedResponses.Count == 0)
        {
            return;
        }

        IsReportingAvailable = false;
        _reportedResponses.Clear();
        _openReportPanels.Clear();
        Notify();
    }

    /// <summary>Clears every report field. Called from <see cref="Reset"/>.</summary>
    /// <remarks>
    /// The open-panel registry goes with them. A control that is being torn down deregisters
    /// itself, but a reset that outran disposal would otherwise leave the overlay deferring Escape
    /// to a panel that no longer exists.
    /// </remarks>
    private void ResetReports()
    {
        IsReportingAvailable = false;
        _reportedResponses.Clear();
        _openReportPanels.Clear();
    }
}
