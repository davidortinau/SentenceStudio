using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.AppOperation;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Services.Api;

namespace SentenceStudio.Native;

public sealed partial class NativeSamPresenter
{
    /// <summary>Sends the composer draft as one durable, idempotent turn (source J5).</summary>
    public Task SendAsync(CancellationToken cancellationToken = default)
    {
        var text = _state.Draft;
        if (string.IsNullOrWhiteSpace(text))
            return Task.CompletedTask;
        if (text.Length > CoachConstraintLimits.MaxTurnTextLength)
        {
            Publish(_state with { Status = NativeSamStatus.InputTooLong });
            return Task.CompletedTask;
        }
        return RunTurnAsync(new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = text,
            PendingSuggestionId = _state.Suggestion?.SuggestionId,
            ExpectedPlanVersion = _planVersion,
            ClientTurnId = NewHandle()
        }, learnerText: text, applying: false, cancellationToken);
    }

    /// <summary>Starter chip: the same immediate-apply path as a typed change (source J14).</summary>
    public Task ApplyChipAsync(NativeSamChip chip, CancellationToken cancellationToken = default)
    {
        if (!_state.ShowChips)
            return Task.CompletedTask;
        var delta = chip switch
        {
            NativeSamChip.Minutes5 => Minutes(5),
            NativeSamChip.Minutes10 => Minutes(10),
            NativeSamChip.Minutes20 => Minutes(20),
            NativeSamChip.NoAudio => new CoachConstraintDeltaDto
            {
                AudioAllowed = !(_constraints?.AudioAllowed ?? true),
                ChangedFields = [CoachConstraintField.AudioAllowed]
            },
            NativeSamChip.NoSpeech => new CoachConstraintDeltaDto
            {
                SpeechAllowed = !(_constraints?.SpeechAllowed ?? true),
                ChangedFields = [CoachConstraintField.SpeechAllowed]
            },
            NativeSamChip.NoTyping => new CoachConstraintDeltaDto
            {
                TypingAllowed = !(_constraints?.TypingAllowed ?? true),
                ChangedFields = [CoachConstraintField.TypingAllowed]
            },
            NativeSamChip.LowEnergy => new CoachConstraintDeltaDto
            {
                EnergyLevel = _constraints?.EnergyLevel == CoachEnergyLevel.Low
                    ? CoachEnergyLevel.Normal : CoachEnergyLevel.Low,
                ChangedFields = [CoachConstraintField.EnergyLevel]
            },
            _ => throw new ArgumentOutOfRangeException(nameof(chip))
        };
        return RunTurnAsync(new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.ConstraintAction,
            ConstraintAction = delta,
            ExpectedPlanVersion = _planVersion,
            ClientTurnId = NewHandle()
        }, learnerText: null, applying: true, cancellationToken);

        static CoachConstraintDeltaDto Minutes(int minutes) => new()
        {
            AvailableMinutes = minutes,
            ChangedFields = [CoachConstraintField.AvailableMinutes]
        };
    }

    /// <summary>Re-submits the last turn with the same operation id: the server replays, never re-runs.</summary>
    public Task RetryAsync(CancellationToken cancellationToken = default) =>
        _lastRequest is { } request && _state.CanRetry
            ? RunTurnAsync(request, learnerText: null, applying: false, cancellationToken, retry: true)
            : Task.CompletedTask;

    /// <summary>Stop: tells the server first (best effort), then always releases the panel (source J30).</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!TryOwner(out var owner) || _run is null)
            return;
        var conversationId = _state.ConversationId;
        var operationId = _pendingOperationId;
        if (conversationId is not null && operationId is not null)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await client.CallAsync(owner,
                    (api, token) => api.CancelConversationTurnAsync(conversationId, operationId, token),
                    timeout.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogInformation(ex, "Sam stop request was not confirmed; releasing locally.");
            }
        }
        _run?.Cancel();
    }

    private async Task RunTurnAsync(CoachTurnRequest request, string? learnerText, bool applying,
        CancellationToken cancellationToken, bool retry = false)
    {
        if (!TryOwner(out var owner) || !_state.IsOpen || _state.Busy
            || _state.ConversationId is not { } conversationId || _state.ConversationClosed)
            return;
        if (!connectivity.IsInternetAvailable)
        {
            // Online-only: nothing is queued and the draft stays in the composer.
            Publish(_state with { Status = NativeSamStatus.Offline });
            return;
        }
        if (_availability?.State == CoachAvailabilityState.LimitReached)
        {
            Publish(_state with { Status = NativeSamStatus.Limited });
            return;
        }

        Publish(_state with { Busy = true, Status = applying ? NativeSamStatus.Applying : NativeSamStatus.Running,
            CanRetry = false, CanRecover = false, NoticeKey = null, StillWorking = false, CanStop = false });
        try
        {
            await RunTurnCoreAsync(owner, conversationId, request, learnerText, cancellationToken, retry);
        }
        finally
        {
            // Every exit, including an early return when the conversation went away,
            // releases the panel: a turn never leaves every action disabled.
            ReleaseBusy();
        }
    }

    private async Task RunTurnCoreAsync(string owner, string conversationId, CoachTurnRequest request,
        string? learnerText, CancellationToken cancellationToken, bool retry)
    {
        // Native preflight (policy section 10): local practice must reach the server
        // before Sam reads or edits Today's Plan, so it never acts on stale state.
        if (pendingWrites.IsPending(owner))
        {
            var revision = pendingWrites.Revision(owner);
            NativeSyncOutcome outcome;
            try
            {
                outcome = await sync.SynchronizeAsync(owner, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Sam preflight sync failed; the turn was not sent.");
                outcome = NativeSyncOutcome.Unconfirmed;
            }
            if (!IsCurrent(owner))
                return;
            if (outcome != NativeSyncOutcome.Confirmed)
            {
                Publish(_state with
                {
                    Busy = false,
                    Status = outcome == NativeSyncOutcome.Offline
                        ? NativeSamStatus.Offline : NativeSamStatus.SyncRequired
                });
                return;
            }
            pendingWrites.MarkConfirmedIfUnchanged(owner, revision);
            // The synced plan may carry a new version; read it before the turn.
            try
            {
                await LoadSessionAsync(owner, cancellationToken);
            }
            catch (Exception ex) when (IsRecoverable(ex, cancellationToken))
            {
                // Nothing was sent: the draft stays in the composer and nothing is retried.
                logger.LogWarning(ex, "Sam could not re-read Today's Plan after sync; the turn was not sent.");
                if (IsCurrent(owner))
                    Publish(_state with { Busy = false, Status = StatusFor(ex),
                        StillWorking = false, CanStop = false });
                return;
            }
            if (!IsCurrent(owner))
                return;
            request = WithPlanVersion(request);
        }

        if (!retry || _pendingOperationId is null)
        {
            _pendingOperationId = NewHandle();
            _pendingIdempotencyKey = NewHandle();
        }
        _lastRequest = request;

        if (learnerText is not null)
        {
            // Echo immediately, in order, even if the turn later fails (source LC-HIST).
            var local = new NativeSamEntry("local:" + request.ClientTurnId, NativeSamEntryKind.Learner,
                learnerText, DateTime.UtcNow, long.MaxValue, [], [], null, [], IsPending: true);
            _localEntries = _localEntries.Add(local);
            Publish(_state with
            {
                Draft = string.Empty,
                Entries = _state.SafeEntries.Add(local),
                HasLearnerSpoken = true
            });
        }

        var floor = _state.SafeEntries.Where(entry => entry.Sequence != long.MaxValue)
            .Select(entry => entry.Sequence).DefaultIfEmpty(0).Max();
        using var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _run = run;
        var clock = TickStageAsync(run.Token);
        try
        {
            var operationId = _pendingOperationId!;
            var key = _pendingIdempotencyKey!;
            var operation = await client.CallAsync(owner,
                (api, token) => api.SubmitConversationTurnAsync(conversationId,
                    new CoachConversationTurnRequest
                    {
                        Turn = request, OperationId = operationId, IdempotencyKey = key
                    }, token),
                run.Token);
            operation = await SettleAsync(owner, conversationId, operation, run.Token);
            if (!IsCurrent(owner) || _state.ConversationId != conversationId)
                return;
            _pendingOperationId = null;
            _pendingIdempotencyKey = null;
            await ApplyOperationAsync(owner, conversationId, operation, floor, cancellationToken);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested
            && (run.IsCancellationRequested || ex is TurnCancelledOnServerException))
        {
            // Stop (local, or already accepted by the server) is a deliberate action and is
            // never shown as a failure. HttpClient's own timeout is not Stop: it leaves the
            // run token untouched and falls through to the recoverable branch below.
            if (IsCurrent(owner))
            {
                MarkLocalFailed();
                Publish(_state with { Busy = false, Status = NativeSamStatus.Ready,
                    NoticeKey = "Coach_Stopped", StillWorking = false, CanStop = false });
            }
        }
        catch (Exception ex) when (IsRecoverable(ex, cancellationToken))
        {
            logger.LogWarning(ex, "Sam turn did not complete.");
            if (!IsCurrent(owner))
                return;
            MarkLocalFailed();
            var status = StatusFor(ex);
            var recoverable = ex is CoachApiException { ProblemType: CoachProblemTypes.Timeout }
                || ex is TaskCanceledException;
            Publish(_state with
            {
                Busy = false,
                Status = status,
                CanRetry = status is NativeSamStatus.Failed or NativeSamStatus.Incomplete
                    or NativeSamStatus.Offline,
                CanRecover = recoverable && _pendingOperationId is not null,
                StillWorking = false,
                CanStop = false,
            });
        }
        finally
        {
            if (ReferenceEquals(_run, run))
                _run = null;
            run.Cancel();
            await clock;
        }
    }

    /// <summary>
    /// Releases a busy flag no branch cleared (an unexpected early return or fault),
    /// so the panel never stays Running with every action disabled. A run still in
    /// a working status ends as an honest failure.
    /// </summary>
    private void ReleaseBusy()
    {
        if (!_state.Busy || !TryOwner(out _) || !_state.Busy)
            return;
        Publish(_state with
        {
            Busy = false,
            StillWorking = false,
            CanStop = false,
            Status = _state.Status is NativeSamStatus.Running or NativeSamStatus.Applying
                or NativeSamStatus.Undoing ? NativeSamStatus.Failed : _state.Status,
        });
    }

    /// <summary>The server reports the turn as cancelled (a Stop it already accepted).</summary>
    private sealed class TurnCancelledOnServerException()
        : OperationCanceledException("Sam turn was cancelled on the server.");

    /// <summary>Polls a durable operation to a terminal state (source SettleOperationAsync).</summary>
    private async Task<CoachTurnOperationDto> SettleAsync(string owner, string conversationId,
        CoachTurnOperationDto operation, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + PollTimeout;
        var current = operation;
        while (current.State is CoachTurnOperationState.Pending or CoachTurnOperationState.Running)
        {
            if (DateTimeOffset.UtcNow >= deadline)
                throw new CoachApiException(System.Net.HttpStatusCode.RequestTimeout,
                    CoachProblemTypes.Timeout, title: null, detail: null);
            await Task.Delay(PollInterval, cancellationToken);
            var operationId = current.OperationId;
            current = await client.CallAsync(owner,
                    (api, token) => api.GetConversationOperationAsync(conversationId, operationId, token),
                    cancellationToken)
                ?? throw new CoachApiException(System.Net.HttpStatusCode.NotFound,
                    CoachProblemTypes.ConversationNotFound, title: null, detail: null);
        }
        if (current.State == CoachTurnOperationState.Completed)
            return current;
        // A terminal Failed or Cancelled operation is definitive: the server replays
        // that same outcome for its keys, so Try Again must send under new ones.
        // Keys stay only while the outcome is unknown (lost response, timeout).
        _pendingOperationId = null;
        _pendingIdempotencyKey = null;
        if (current.State == CoachTurnOperationState.Cancelled)
            throw new TurnCancelledOnServerException();
        throw new CoachApiException(System.Net.HttpStatusCode.InternalServerError,
            problemType: null, title: null, detail: "Sam turn failed.");
    }

    private async Task ApplyOperationAsync(string owner, string conversationId,
        CoachTurnOperationDto operation, long floor, CancellationToken cancellationToken)
    {
        var result = operation.Result;
        var answerId = operation.Messages
            .Where(item => item.Message.Role == CoachMessageRole.Coach)
            .OrderBy(item => item.Sequence).LastOrDefault()?.Message.MessageId;
        // The ledger is the transcript; read the canonical page back.
        _localEntries = [];
        await LoadTranscriptAsync(owner, conversationId, cancellationToken);
        if (!IsCurrent(owner) || _state.ConversationId != conversationId)
            return;
        answerId ??= _state.SafeEntries
            .Where(entry => entry.Kind != NativeSamEntryKind.Learner && entry.Sequence > floor
                && entry.Sequence != long.MaxValue)
            .LastOrDefault()?.Id;
        if (result is null)
        {
            Publish(_state with { Busy = false, Status = NativeSamStatus.Ready,
                StillWorking = false, CanStop = false });
            return;
        }
        ApplyTurn(result, answerId);
        await LoadVocabularyStateAsync(owner, conversationId, cancellationToken,
            result.VocabularySetProposal);
    }

    private void ApplyTurn(CoachTurnResponse result, string? answerId)
    {
        _sessionId = result.SessionId;
        _planVersion = result.PlanState.PlanVersion;
        _constraints = result.ActiveConstraints;
        if (answerId is not null)
        {
            if (result.Evidence.Count > 0)
                _evidence[answerId] = result.Evidence.ToImmutableArray();
            if (result.Limitation is { } limitation)
                _limitations[answerId] = limitation;
        }
        var entries = _state.SafeEntries.Select(entry => entry.Id == answerId
                ? entry with
                {
                    Evidence = _evidence.TryGetValue(entry.Id, out var evidence) ? evidence : [],
                    Limitation = _limitations.GetValueOrDefault(entry.Id)
                }
                : entry)
            .ToImmutableArray();
        var receipt = result.ChangeReceipt;
        var status = FromTurn(result);
        Publish(_state with
        {
            Busy = false,
            Entries = entries,
            Status = status,
            Suggestion = result.PendingSuggestion,
            ClarifyingQuestion = string.IsNullOrWhiteSpace(result.ClarifyingQuestion)
                ? null : result.ClarifyingQuestion,
            LatestReceipt = receipt ?? _state.LatestReceipt,
            CanUndo = receipt?.CanUndo ?? (result.PlanState.CanUndo && result.PlanState.LastRevision is not null),
            UndoRevisionId = receipt?.Revision.RevisionId ?? result.PlanState.LastRevision?.RevisionId,
            StillWorking = false,
            CanStop = false,
            CanRetry = status is NativeSamStatus.Failed or NativeSamStatus.Incomplete,
        });
        if (receipt is not null)
            PlanChanged?.Invoke();
    }

    // ---------------------------------------------------------------- Today's Plan decisions

    /// <summary>Deterministic tapped acceptance of the pending suggestion (source J15).</summary>
    public Task AcceptSuggestionAsync(CancellationToken cancellationToken = default) =>
        _state.Suggestion is { } suggestion
            ? DecideAsync(NativeSamStatus.Applying, (api, session, token) =>
                api.AcceptSuggestionAsync(session, suggestion.SuggestionId,
                    new CoachSuggestionDecisionRequest
                    {
                        ExpectedPlanVersion = _planVersion, ClientTurnId = NewHandle()
                    }, token), cancellationToken)
            : Task.CompletedTask;

    /// <summary>Rejection never writes; the card resolves to a visible note (source J15).</summary>
    public Task RejectSuggestionAsync(CancellationToken cancellationToken = default) =>
        _state.Suggestion is { } suggestion
            ? DecideAsync(NativeSamStatus.Running, (api, session, token) =>
                api.RejectSuggestionAsync(session, suggestion.SuggestionId,
                    new CoachSuggestionDecisionRequest
                    {
                        ExpectedPlanVersion = _planVersion, ClientTurnId = NewHandle()
                    }, token), cancellationToken, rejected: true)
            : Task.CompletedTask;

    /// <summary>Undoes the latest applied revision only; there is no redo (source J17).</summary>
    public Task UndoAsync(CancellationToken cancellationToken = default) =>
        _state.CanUndo
            ? DecideAsync(NativeSamStatus.Undoing, (api, session, token) =>
                api.UndoAsync(session, new CoachUndoRequest
                {
                    RevisionId = _state.UndoRevisionId,
                    ExpectedPlanVersion = _planVersion,
                    ClientTurnId = NewHandle()
                }, token), cancellationToken)
            : Task.CompletedTask;

    /// <summary>"Reload plan" on a plan changed elsewhere: re-read, never write (source J18).</summary>
    public async Task ReloadPlanAsync(CancellationToken cancellationToken = default)
    {
        if (!TryOwner(out var owner) || !_state.IsOpen)
            return;
        PlanChanged?.Invoke();
        try
        {
            await LoadSessionAsync(owner, cancellationToken);
        }
        catch (Exception ex) when (IsRecoverable(ex, cancellationToken))
        {
            logger.LogWarning(ex, "Sam could not re-read Today's Plan.");
            if (IsCurrent(owner))
                Publish(_state with { Status = StatusFor(ex) });
            return;
        }
        if (IsCurrent(owner))
            Publish(_state with { Status = NativeSamStatus.Ready });
    }

    private async Task DecideAsync(NativeSamStatus busyStatus,
        Func<ICoachApiClient, string, CancellationToken, Task<CoachTurnResponse>> decision,
        CancellationToken cancellationToken, bool rejected = false)
    {
        if (!TryOwner(out var owner) || !_state.IsOpen || _state.Busy
            || _state.ConversationId is not { } conversationId)
            return;
        if (!connectivity.IsInternetAvailable)
        {
            Publish(_state with { Status = NativeSamStatus.Offline });
            return;
        }
        Publish(_state with { Busy = true, Status = busyStatus, NoticeKey = null });
        try
        {
            if (_sessionId is null)
                await LoadSessionAsync(owner, cancellationToken);
            if (!IsCurrent(owner))
                return;
            var session = _sessionId ?? throw new CoachApiException(System.Net.HttpStatusCode.NotFound,
                CoachProblemTypes.SessionNotFound, title: null, detail: null);
            var result = await client.CallAsync(owner,
                (api, token) => decision(api, session, token), cancellationToken);
            if (!IsCurrent(owner) || _state.ConversationId != conversationId)
                return;
            await LoadTranscriptAsync(owner, conversationId, cancellationToken);
            if (!IsCurrent(owner) || _state.ConversationId != conversationId)
                return;
            ApplyTurn(result, answerId: null);
            if (rejected && result.ChangeReceipt is null)
                Publish(_state with { NoticeKey = "Coach_Rejected" });
        }
        catch (Exception ex) when (IsRecoverable(ex, cancellationToken))
        {
            logger.LogWarning(ex, "Sam plan decision did not complete.");
            if (IsCurrent(owner))
            {
                var status = StatusFor(ex);
                Publish(_state with
                {
                    Busy = false,
                    Status = status,
                    // A stale suggestion or undo resolves quietly (source FromProblem).
                    Suggestion = ex is CoachApiException { ProblemType: CoachProblemTypes.SuggestionNotFound }
                        ? null : _state.Suggestion,
                    CanUndo = ex is CoachApiException { ProblemType: CoachProblemTypes.NothingToUndo }
                        ? false : _state.CanUndo,
                });
            }
        }
        finally
        {
            ReleaseBusy();
        }
    }

    // ---------------------------------------------------------------- vocabulary set (J23)

    public Task ApproveVocabularySetAsync(CancellationToken cancellationToken = default) =>
        DecideVocabularySetAsync(ApplicationOperationDecision.Accept, cancellationToken);

    public Task DeclineVocabularySetAsync(CancellationToken cancellationToken = default) =>
        DecideVocabularySetAsync(ApplicationOperationDecision.Reject, cancellationToken);

    /// <summary>Hands the approved resource to the head, which launches the native Vocabulary Quiz.</summary>
    public void StartVocabularyReview()
    {
        if (TryOwner(out var owner) && _state.ApprovedResourceId is { Length: > 0 } resourceId)
            VocabularyReviewRequested?.Invoke(owner, resourceId);
    }

    /// <summary>The approved set has not synced to this device yet; keep Start available.</summary>
    public void ReportVocabularyReviewNotReady()
    {
        if (_state.ApprovedResourceId is not null)
            Publish(_state with { VocabularySetMessageKey = "NativeSam_VocabularyReviewNotReady" });
    }

    private async Task DecideVocabularySetAsync(ApplicationOperationDecision decision,
        CancellationToken cancellationToken)
    {
        if (!TryOwner(out var owner) || _state.VocabularySet is not { } proposal
            || _state.VocabularySetBusy || _state.Busy
            || _state.ConversationId is not { } conversationId)
            return;
        if (!connectivity.IsInternetAvailable)
        {
            Publish(_state with { Status = NativeSamStatus.Offline });
            return;
        }
        Publish(_state with { VocabularySetBusy = true, VocabularySetMessageKey = null });
        try
        {
            var response = await client.CallAsync(owner,
                (api, token) => api.ApproveVocabularySetAsync(new ApproveCoachVocabularySetRequest
                {
                    ProposalReference = proposal.ProposalId, Decision = decision
                }, token), cancellationToken);
            if (!IsCurrent(owner) || _state.ConversationId != conversationId)
                return;
            var accepted = decision == ApplicationOperationDecision.Accept;
            Publish(_state with
            {
                VocabularySetBusy = false,
                VocabularySet = null,
                ApprovedResourceId = accepted ? ResourceIdFrom(response) : null,
                VocabularySetMessageKey = accepted
                    ? "NativeSam_VocabularySetApproved" : "NativeSam_VocabularySetDeclined",
            });
        }
        catch (Exception ex) when (IsRecoverable(ex, cancellationToken))
        {
            logger.LogWarning(ex, "Sam vocabulary set decision failed; nothing was changed.");
            if (IsCurrent(owner))
                Publish(_state with
                {
                    VocabularySetBusy = false,
                    VocabularySetMessageKey = "NativeSam_VocabularySetFailed"
                });
        }
    }

    private static string? ResourceIdFrom(CoachVocabularySetApprovalResponse response)
    {
        if (!string.IsNullOrWhiteSpace(response.ResourceId))
            return response.ResourceId;
        // ActivityPath is the closed /vocab-quiz?resourceIds=<id> client action.
        var path = response.ActivityPath;
        var marker = path?.IndexOf("resourceIds=", StringComparison.Ordinal) ?? -1;
        if (path is null || marker < 0)
            return null;
        var value = path[(marker + "resourceIds=".Length)..].Split('&', ',')[0];
        return value.Length > 0 ? Uri.UnescapeDataString(value) : null;
    }

    private async Task LoadVocabularyStateAsync(string owner, string conversationId,
        CancellationToken cancellationToken, CoachVocabularySetProposal? fromResponse = null)
    {
        try
        {
            var state = await client.CallAsync(owner,
                (api, token) => api.GetConversationVocabularyStateAsync(conversationId, token),
                cancellationToken);
            if (!IsCurrent(owner) || _state.ConversationId != conversationId)
                return;
            var pending = state?.PendingProposal ?? fromResponse;
            if (pending?.ProposalId != _state.VocabularySet?.ProposalId)
                Publish(_state with { VocabularySet = pending });
        }
        catch (Exception ex) when (IsRecoverable(ex, cancellationToken))
        {
            logger.LogInformation(ex, "Sam vocabulary proposal state could not be read.");
            if (IsCurrent(owner) && fromResponse is not null)
                Publish(_state with { VocabularySet = fromResponse });
        }
    }

    // ---------------------------------------------------------------- helpers

    private CoachTurnRequest WithPlanVersion(CoachTurnRequest request) => new()
    {
        InputKind = request.InputKind,
        Text = request.Text,
        ChipId = request.ChipId,
        ConstraintAction = request.ConstraintAction,
        PendingSuggestionId = request.PendingSuggestionId,
        ExpectedPlanVersion = _planVersion,
        ClientTurnId = request.ClientTurnId,
        ClientCapabilities = request.ClientCapabilities,
    };

    private void MarkLocalFailed()
    {
        if (_localEntries.IsDefaultOrEmpty)
            return;
        var ids = _localEntries.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        _localEntries = _localEntries.Select(entry => entry with { IsPending = false, IsFailed = true })
            .ToImmutableArray();
        Publish(_state with
        {
            Entries = _state.SafeEntries.Select(entry => ids.Contains(entry.Id)
                ? entry with { IsPending = false, IsFailed = true } : entry).ToImmutableArray()
        });
    }

    /// <summary>Named stage, then "still working" at 8 s and Stop at 12 s (source CoachStateMachine).</summary>
    private async Task TickStageAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(StillWorkingAfter, cancellationToken);
            if (_state.Busy)
                Publish(_state with { StillWorking = true });
            await Task.Delay(StopAfter - StillWorkingAfter, cancellationToken);
            if (_state.Busy)
                Publish(_state with { CanStop = true });
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Source CoachStateMachine.FromTurn, mapped onto the native statuses.</summary>
    internal static NativeSamStatus FromTurn(CoachTurnResponse turn)
    {
        if (turn.StopReason == CoachStopReason.SessionExpired || turn.SessionStatus == CoachSessionStatus.Expired)
            return NativeSamStatus.Expired;
        if (turn.StopReason == CoachStopReason.RateLimit)
            return NativeSamStatus.Limited;
        // A validated refusal is a safe no-op, not a malfunction.
        if (turn.Status == CoachTurnStatus.Rejected && turn.StopReason == CoachStopReason.ValidationFailed
            && turn.ChangeReceipt is null && string.IsNullOrWhiteSpace(turn.ClarifyingQuestion))
            return NativeSamStatus.Ready;
        if (turn.Status == CoachTurnStatus.Failed)
            return NativeSamStatus.Failed;
        var asks = turn.StopReason == CoachStopReason.ClarificationRequested
            || turn.SessionStatus == CoachSessionStatus.AwaitingClarification
            || !string.IsNullOrWhiteSpace(turn.ClarifyingQuestion);
        if (asks)
            return turn.ClarificationsRemaining <= 0 || string.IsNullOrWhiteSpace(turn.ClarifyingQuestion)
                ? NativeSamStatus.ClarificationLimitReached : NativeSamStatus.Clarification;
        if (turn.Status == CoachTurnStatus.Rejected)
            return NativeSamStatus.Failed;
        if (turn.Status == CoachTurnStatus.Incomplete)
            return NativeSamStatus.Incomplete;
        return NativeSamStatus.Ready;
    }

    /// <summary>Source CoachStateMachine.FromProblem, mapped onto the native statuses.</summary>
    internal static NativeSamStatus FromProblem(CoachApiException exception) => exception.ProblemType switch
    {
        CoachProblemTypes.SessionExpired or CoachProblemTypes.SessionNotFound => NativeSamStatus.Expired,
        CoachProblemTypes.Unavailable or CoachProblemTypes.ConversationNotFound => NativeSamStatus.Unavailable,
        CoachProblemTypes.SuggestionNotFound or CoachProblemTypes.NothingToUndo
            or CoachProblemTypes.IdempotencyConflict or CoachProblemTypes.InvalidCursor
            or CoachProblemTypes.RunCancelled => NativeSamStatus.Ready,
        CoachProblemTypes.PlanVersionConflict or CoachProblemTypes.ConversationStateConflict
            => NativeSamStatus.PlanChangedElsewhere,
        CoachProblemTypes.RunInProgress or CoachProblemTypes.ToolFailure or CoachProblemTypes.Timeout
            => NativeSamStatus.Incomplete,
        CoachProblemTypes.InvalidTurnInput => NativeSamStatus.InputTooLong,
        CoachProblemTypes.RateLimited => NativeSamStatus.Limited,
        _ => NativeSamStatus.Failed,
    };
}

public enum NativeSamChip { Minutes5, Minutes10, Minutes20, NoAudio, NoSpeech, NoTyping, LowEnergy }
