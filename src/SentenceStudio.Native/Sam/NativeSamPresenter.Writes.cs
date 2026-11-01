using System.Collections.Immutable;
using System.Net;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.LearnerMemory;
using SentenceStudio.Services.Api;

namespace SentenceStudio.Native;

/// <summary>
/// Proposed changes to the learner's own material, the in-chat memory candidate,
/// response reports, and Copy. Sam proposes; the learner decides. Every state on
/// screen comes from the server: a card never infers that a change happened from a
/// request succeeding, never offers an Undo the receipt did not, and the one-use
/// confirmation value lives only in this presenter, never in published state.
/// Source: CoachWorkspaceState.Writes/Reports, CoachMemoryCandidateCard (59c72262).
/// </summary>
public sealed partial class NativeSamPresenter
{
    private CoachWriteConfirmation? _confirmation;

    private enum WriteChannel { Any, Accept, Confirm }

    /// <summary>Soft write: one Accept for a reversible change (source J19).</summary>
    public Task AcceptWriteAsync(string operationId, CancellationToken cancellationToken = default) =>
        RunWriteAsync(operationId, WriteChannel.Accept,
            (api, conversation, token) => api.AcceptWriteAsync(conversation, operationId, token),
            cancellationToken);

    /// <summary>Both risk classes can be declined; nothing is written.</summary>
    public Task DeclineWriteAsync(string operationId, CancellationToken cancellationToken = default) =>
        RunWriteAsync(operationId, WriteChannel.Any,
            (api, conversation, token) => api.RejectWriteAsync(conversation, operationId, token),
            cancellationToken);

    /// <summary>Reverses an executed change inside the window the server's receipt states.</summary>
    public Task UndoWriteAsync(string operationId, CancellationToken cancellationToken = default) =>
        RunWriteAsync(operationId, WriteChannel.Any,
            (api, conversation, token) => api.UndoWriteAsync(conversation, operationId, token),
            cancellationToken);

    /// <summary>"Check again" when the outcome is unknown: a re-read, never a write.</summary>
    public async Task RefreshWriteAsync(string operationId, CancellationToken cancellationToken = default)
    {
        if (!TryOwner(out var owner) || _state.ConversationId is not { } conversationId
            || string.IsNullOrWhiteSpace(operationId))
            return;
        await RefreshWriteCoreAsync(owner, conversationId, operationId, cancellationToken);
    }

    /// <summary>Hard write, step one: the server mints the one-use confirmation (source J20).</summary>
    public async Task BeginWriteConfirmationAsync(string operationId, CancellationToken cancellationToken = default)
    {
        if (!CanStartWrite(operationId, WriteChannel.Confirm, out var owner, out var conversationId))
            return;
        DiscardConfirmation();
        PublishWrite(busy: operationId, errorId: null, errorKey: null);
        try
        {
            var challenge = await client.CallAsync(owner,
                (api, token) => api.RequestWriteConfirmationAsync(conversationId, operationId, token),
                cancellationToken);
            if (!IsCurrent(owner) || _state.ConversationId != conversationId)
                return;
            if (challenge is null || !challenge.IsUsableAt(DateTime.UtcNow))
            {
                PublishWrite(busy: null, errorId: operationId, errorKey: "Coach_WriteUnavailable");
                await RefreshWriteCoreAsync(owner, conversationId, operationId, cancellationToken);
                return;
            }
            _confirmation = challenge;
            Publish(_state with
            {
                WriteBusyId = null,
                ConfirmingWriteId = operationId,
                ConfirmationExpiresAtUtc = challenge.ExpiresAtUtc,
            });
        }
        catch (Exception ex) when (IsRecoverable(ex, cancellationToken))
        {
            logger.LogWarning(ex, "Sam write confirmation could not be requested.");
            if (!IsCurrent(owner))
                return;
            PublishWrite(busy: null, errorId: operationId, errorKey: WriteErrorKeyFor(ex));
            if (ex is CoachApiException)
                await RefreshWriteCoreAsync(owner, conversationId, operationId, cancellationToken);
        }
        finally
        {
            ReleaseWriteBusy(operationId);
        }
    }

    /// <summary>Back out of the confirmation step; the value in hand is dropped, not kept.</summary>
    public void CancelWriteConfirmation()
    {
        if (_confirmation is null && _state.ConfirmingWriteId is null)
            return;
        DiscardConfirmation();
        Publish(_state with { ConfirmingWriteId = null, ConfirmationExpiresAtUtc = null });
    }

    /// <summary>Hard write, step two: the one-use value goes only in the request header.</summary>
    public async Task ConfirmWriteAsync(CancellationToken cancellationToken = default)
    {
        if (_confirmation is not { } confirmation || _state.ConfirmingWriteId is not { } operationId
            || !CanStartWrite(operationId, WriteChannel.Confirm, out var owner, out var conversationId))
            return;
        if (!confirmation.IsUsableAt(DateTime.UtcNow))
        {
            DiscardConfirmation();
            Publish(_state with
            {
                ConfirmingWriteId = null, ConfirmationExpiresAtUtc = null,
                WriteErrorId = operationId, WriteErrorKey = "Coach_WriteConfirmExpired",
            });
            await RefreshWriteCoreAsync(owner, conversationId, operationId, cancellationToken);
            return;
        }
        PublishWrite(busy: operationId, errorId: null, errorKey: null);
        try
        {
            var settled = await client.CallAsync(owner,
                (api, token) => api.ConfirmWriteAsync(conversationId, operationId, confirmation, token),
                cancellationToken);
            DiscardConfirmation();
            if (IsCurrent(owner) && _state.ConversationId == conversationId)
            {
                Publish(_state with { ConfirmingWriteId = null, ConfirmationExpiresAtUtc = null });
                ApplyWriteState(operationId, settled);
            }
        }
        catch (Exception ex) when (IsRecoverable(ex, cancellationToken))
        {
            DiscardConfirmation();
            logger.LogWarning(ex, "Sam protected change was not confirmed.");
            if (!IsCurrent(owner))
                return;
            Publish(_state with { ConfirmingWriteId = null, ConfirmationExpiresAtUtc = null });
            PublishWrite(busy: null, errorId: operationId, errorKey: WriteErrorKeyFor(ex));
            if (ex is CoachApiException)
                await RefreshWriteCoreAsync(owner, conversationId, operationId, cancellationToken);
        }
        finally
        {
            // The value is single-use whatever happened; it is never held "just in case".
            DiscardConfirmation();
            ReleaseWriteBusy(operationId);
        }
    }

    private async Task RunWriteAsync(string operationId, WriteChannel channel,
        Func<ICoachApiClient, string, CancellationToken, Task<CoachWriteOperationDto?>> action,
        CancellationToken cancellationToken)
    {
        if (!CanStartWrite(operationId, channel, out var owner, out var conversationId))
            return;
        PublishWrite(busy: operationId, errorId: null, errorKey: null);
        try
        {
            var settled = await client.CallAsync(owner,
                (api, token) => action(api, conversationId, token), cancellationToken);
            if (IsCurrent(owner) && _state.ConversationId == conversationId)
                ApplyWriteState(operationId, settled);
        }
        catch (Exception ex) when (IsRecoverable(ex, cancellationToken))
        {
            logger.LogWarning(ex, "Sam write decision did not complete.");
            if (!IsCurrent(owner))
                return;
            PublishWrite(busy: null, errorId: operationId, errorKey: WriteErrorKeyFor(ex));
            // The refusal named a state; read what it actually is (source RunWriteActionAsync).
            if (ex is CoachApiException)
                await RefreshWriteCoreAsync(owner, conversationId, operationId, cancellationToken);
        }
        finally
        {
            ReleaseWriteBusy(operationId);
        }
    }

    private async Task RefreshWriteCoreAsync(string owner, string conversationId, string operationId,
        CancellationToken cancellationToken)
    {
        try
        {
            var state = await client.CallAsync(owner,
                (api, token) => api.GetWriteOperationAsync(conversationId, operationId, token),
                cancellationToken);
            if (IsCurrent(owner) && _state.ConversationId == conversationId)
                ApplyWriteState(operationId, state, keepError: true);
        }
        catch (Exception ex) when (IsRecoverable(ex, cancellationToken))
        {
            // A refresh that cannot reach the server leaves the card as it was.
            logger.LogInformation(ex, "Sam write state could not be re-read.");
        }
    }

    private bool CanStartWrite(string operationId, WriteChannel channel, out string owner, out string conversationId)
    {
        conversationId = string.Empty;
        if (!TryOwner(out owner) || string.IsNullOrWhiteSpace(operationId)
            || _state.ConversationId is not { } current || !_state.WriteSurfaceEnabled
            || _state.WriteBusyId is not null || _state.Busy)
            return false;
        conversationId = current;
        if (!connectivity.IsInternetAvailable)
        {
            Publish(_state with { WriteErrorId = operationId, WriteErrorKey = "Coach_WriteNetworkFailed" });
            return false;
        }
        var operation = FindWrite(operationId);
        if (!NativeSamWritePresentation.IsWellFormed(operation))
            return false;
        // The channel matches the risk class in both directions: a control that should not
        // exist never sends a request the learner did not ask for.
        // Only the active proposal can be accepted or confirmed; superseded cards are inert
        // even if a stale render or automation still sends their id.
        if (channel is WriteChannel.Accept or WriteChannel.Confirm
            && !string.Equals(operationId, _state.ActiveWriteId, StringComparison.Ordinal))
            return false;
        return channel switch
        {
            WriteChannel.Accept => !operation!.RequiresConfirmation,
            WriteChannel.Confirm => operation!.RequiresConfirmation,
            _ => true,
        };
    }

    private void ApplyWriteState(string operationId, CoachWriteOperationDto? state, bool keepError = false)
    {
        if (state is null)
        {
            // Gone, never existed, or someone else's: indistinguishable on purpose. The card stays.
            Publish(_state with
            {
                UnavailableWrites = (_state.UnavailableWrites ?? ImmutableHashSet<string>.Empty).Add(operationId),
                WriteErrorId = operationId,
                WriteErrorKey = "Coach_WriteUnavailable",
            });
            RecomputeActiveWrite();
            return;
        }
        Publish(_state with
        {
            UnavailableWrites = _state.UnavailableWrites?.Remove(operationId),
            Entries = _state.SafeEntries.Select(entry => entry.Write?.OperationId == operationId
                ? entry with { Write = state } : entry).ToImmutableArray(),
            WriteErrorId = keepError ? _state.WriteErrorId : null,
            WriteErrorKey = keepError ? _state.WriteErrorKey : null,
        });
        RecomputeActiveWrite();
    }

    /// <summary>Only the newest open, readable proposal is actionable (source RecomputeActiveWrite).</summary>
    private void RecomputeActiveWrite()
    {
        string? active = null;
        foreach (var entry in _state.SafeEntries)
            if (entry.Write is { Status: CoachWriteStatus.Proposed } write
                && NativeSamWritePresentation.IsWellFormed(write)
                && !_state.IsWriteUnavailable(write.OperationId))
                active = write.OperationId;
        var confirming = _state.ConfirmingWriteId;
        if (confirming is not null && confirming != active)
        {
            DiscardConfirmation();
            confirming = null;
        }
        if (active != _state.ActiveWriteId || confirming != _state.ConfirmingWriteId)
            Publish(_state with
            {
                ActiveWriteId = active,
                ConfirmingWriteId = confirming,
                ConfirmationExpiresAtUtc = confirming is null ? null : _state.ConfirmationExpiresAtUtc,
            });
    }

    private CoachWriteOperationDto? FindWrite(string operationId) =>
        _state.SafeEntries.Select(entry => entry.Write)
            .FirstOrDefault(write => write?.OperationId == operationId);

    private void PublishWrite(string? busy, string? errorId, string? errorKey) =>
        Publish(_state with { WriteBusyId = busy, WriteErrorId = errorId, WriteErrorKey = errorKey });

    private void ReleaseWriteBusy(string operationId)
    {
        if (_state.WriteBusyId == operationId && TryOwner(out _))
            Publish(_state with { WriteBusyId = null });
    }

    private void DiscardConfirmation() => _confirmation = null;

    private static string WriteErrorKeyFor(Exception ex) => ex switch
    {
        CoachApiException api => api.StatusCode switch
        {
            HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                => "Coach_WriteUnavailable",
            HttpStatusCode.TooManyRequests => "Coach_WriteLimited",
            HttpStatusCode.UnprocessableEntity or HttpStatusCode.Conflict => "Coach_WriteRefused",
            _ => "Coach_WriteNetworkFailed",
        },
        UnauthorizedAccessException => "Coach_WriteUnavailable",
        _ => "Coach_WriteNetworkFailed",
    };

    // ---------------------------------------------------------------- memory candidate (J24)

    /// <summary>Save what Sam proposed remembering. Nothing is stored without this press.</summary>
    public Task SaveMemoryCandidateAsync(CancellationToken cancellationToken = default) =>
        DecideMemoryCandidateAsync(approve: true, editedGoal: null, cancellationToken);

    /// <summary>Approve with the learner's edited study goal in one action (never approve-then-edit).</summary>
    public Task SaveEditedMemoryCandidateAsync(CancellationToken cancellationToken = default) =>
        string.IsNullOrWhiteSpace(_state.MemoryCandidateEdit)
            ? Task.CompletedTask
            : DecideMemoryCandidateAsync(approve: true, editedGoal: _state.MemoryCandidateEdit!.Trim(),
                cancellationToken);

    /// <summary>"Only this chat" and "Not now" are both a refusal to persist.</summary>
    public Task DeclineMemoryCandidateAsync(CancellationToken cancellationToken = default) =>
        DecideMemoryCandidateAsync(approve: false, editedGoal: null, cancellationToken);

    /// <summary>Seeds the editor with what Sam proposed, so editing is a correction, not a retype.</summary>
    public void BeginMemoryCandidateEdit()
    {
        if (_state.MemoryCandidate is { } fact)
            Publish(_state with { MemoryCandidateEdit = fact.Value.StudyGoalText ?? fact.DisplayText });
    }

    public void SetMemoryCandidateEdit(string text)
    {
        if (_state.MemoryCandidateEdit is not null)
            Publish(_state with { MemoryCandidateEdit = text ?? string.Empty }, structural: false);
    }

    public void CancelMemoryCandidateEdit()
    {
        if (_state.MemoryCandidateEdit is not null)
            Publish(_state with { MemoryCandidateEdit = null });
    }

    private async Task DecideMemoryCandidateAsync(bool approve, string? editedGoal,
        CancellationToken cancellationToken)
    {
        if (!TryOwner(out var owner) || _state.MemoryCandidate is not { } fact || _state.MemoryCandidateBusy)
            return;
        if (!connectivity.IsInternetAvailable)
        {
            Publish(_state with { MemoryCandidateNoticeKey = "Coach_MemoryOffline" });
            return;
        }
        Publish(_state with { MemoryCandidateBusy = true, MemoryCandidateNoticeKey = null });
        var outcome = await MemoryWriteAsync(owner, async (api, token) =>
        {
            if (!approve)
            {
                await api.RejectMemoryAsync(fact.Id, new CoachMemoryRejectRequest(fact.Version), token);
                return true;
            }
            var edited = editedGoal is null ? null : new CoachMemoryValueDto
            {
                Kind = fact.Kind,
                StudyGoalText = fact.Kind == CoachMemoryKind.PersistentStudyGoal ? editedGoal : null,
                ExplanationDepth = fact.Value.ExplanationDepth,
                CorrectionTiming = fact.Value.CorrectionTiming,
                ExampleRegister = fact.Value.ExampleRegister,
            };
            return await api.ApproveMemoryAsync(fact.Id, new CoachMemoryApproveRequest(fact.Version, edited),
                token) is not null;
        }, cancellationToken);
        if (!IsCurrent(owner))
            return;
        var settled = outcome is "Coach_MemorySaved" or "Coach_MemoryGone";
        Publish(_state with
        {
            MemoryCandidateBusy = false,
            // The card resolves on a definite answer; a refused or offline save keeps it.
            MemoryCandidate = settled ? null : _state.MemoryCandidate,
            MemoryCandidateEdit = settled ? null : _state.MemoryCandidateEdit,
            MemoryCandidateNoticeKey = outcome,
        });
        if (settled && _state.SafeMemory.Availability == NativeSamMemoryAvailability.Available)
            await LoadMemoryAsync(cancellationToken);
    }

    // ---------------------------------------------------------------- reports (J27)

    private async Task LoadReportedResponsesAsync(string owner, string conversationId,
        CancellationToken cancellationToken)
    {
        if (!_state.IsDurableHistoryAvailable)
        {
            Publish(_state with { ReportingAvailable = false, Reported = null });
            return;
        }
        try
        {
            var reported = await client.CallAsync(owner,
                (api, token) => api.GetReportedResponsesAsync(conversationId, token), cancellationToken);
            if (!IsCurrent(owner) || _state.ConversationId != conversationId)
                return;
            Publish(_state with
            {
                // A 404 is the feature probe: no control rather than a failure.
                ReportingAvailable = reported is not null,
                Reported = reported is null ? null
                    : reported.MessageIds.Where(id => !string.IsNullOrWhiteSpace(id))
                        .ToImmutableHashSet(StringComparer.Ordinal),
            });
        }
        catch (Exception ex) when (IsRecoverable(ex, cancellationToken))
        {
            logger.LogInformation(ex, "Sam reported responses could not be read; reporting is not offered.");
            if (IsCurrent(owner) && _state.ConversationId == conversationId)
                Publish(_state with { ReportingAvailable = false, Reported = null, ReportingMessageId = null });
        }
    }

    /// <summary>Opens the inline closed-choice report panel under one Sam response.</summary>
    public void OpenReport(string messageId)
    {
        if (!_state.ReportingAvailable || _state.IsReported(messageId)
            || !_state.SafeEntries.Any(entry => entry.Id == messageId && entry.IsReportable))
            return;
        Publish(_state with { ReportingMessageId = messageId, ReportReason = null, ReportFailedId = null });
    }

    public void SelectReportReason(CoachResponseReportReason reason)
    {
        if (_state.ReportingMessageId is not null && !_state.ReportBusy)
            Publish(_state with { ReportReason = reason });
    }

    public void CancelReport()
    {
        if (_state.ReportingMessageId is not null && !_state.ReportBusy)
            Publish(_state with { ReportingMessageId = null, ReportReason = null, ReportFailedId = null });
    }

    /// <summary>Report the response; idempotent, settles to "Reported for review".</summary>
    public async Task SubmitReportAsync(CancellationToken cancellationToken = default)
    {
        if (!TryOwner(out var owner) || _state.ReportingMessageId is not { } messageId
            || _state.ReportReason is not { } reason || _state.ReportBusy
            || _state.ConversationId is not { } conversationId || !_state.ReportingAvailable)
            return;
        if (_state.IsReported(messageId))
        {
            Publish(_state with { ReportingMessageId = null, ReportReason = null });
            return;
        }
        Publish(_state with { ReportBusy = true, ReportFailedId = null });
        try
        {
            var result = await client.CallAsync(owner,
                (api, token) => api.ReportResponseAsync(conversationId, messageId,
                    new CoachResponseReportRequest { Reason = reason }, token), cancellationToken);
            if (!IsCurrent(owner) || _state.ConversationId != conversationId)
                return;
            if (result is null)
            {
                Publish(_state with
                {
                    ReportBusy = false, ReportingAvailable = false, Reported = null,
                    ReportingMessageId = null, ReportReason = null, ReportFailedId = messageId,
                });
                return;
            }
            Publish(_state with
            {
                ReportBusy = false,
                Reported = (_state.Reported ?? ImmutableHashSet<string>.Empty).Add(result.MessageId),
                ReportingMessageId = null,
                ReportReason = null,
            });
        }
        catch (Exception ex) when (IsRecoverable(ex, cancellationToken))
        {
            logger.LogWarning(ex, "Sam response report was not sent; nothing was reported.");
            if (IsCurrent(owner))
                Publish(_state with { ReportBusy = false, ReportFailedId = messageId });
        }
    }

    // ---------------------------------------------------------------- copy (J28)

    /// <summary>The readable text Copy puts on the clipboard; only for what Sam said.</summary>
    public string? CopyTextFor(string messageId) =>
        _state.SafeEntries.FirstOrDefault(entry => entry.Id == messageId
            && entry.Kind is NativeSamEntryKind.Sam or NativeSamEntryKind.Notice
            && entry.IsSettled) is { } entry && entry.ReadableText.Length > 0
            ? entry.ReadableText : null;

    /// <summary>The head reports the platform clipboard outcome; the control says Copied or Copy failed.</summary>
    public void ReportCopyResult(string messageId, bool copied) =>
        Publish(_state with
        {
            CopiedMessageId = copied ? messageId : null,
            CopyFailedMessageId = copied ? null : messageId,
        });
}
