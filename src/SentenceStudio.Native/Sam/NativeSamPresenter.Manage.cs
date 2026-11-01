using System.Collections.Immutable;
using System.Net;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.LearnerMemory;
using SentenceStudio.Services.Api;

namespace SentenceStudio.Native;

/// <summary>
/// The learner's management surfaces: the conversation shelf actions (rename,
/// close/reopen, delete), the Settings "Learning Coach" section (delete coach
/// history) and "What Sam remembers". Every call is owner-bound; a 404 is
/// treated as "gone or not yours", deliberately indistinguishable.
/// Source: CoachConversationList/CoachConversationDirectory, Settings.razor,
/// CoachMemoryPanel/CoachMemoryDirectory (frozen reference 59c72262).
/// </summary>
public sealed partial class NativeSamPresenter
{
    // ---------------------------------------------------------------- conversation shelf (J26)

    public void BeginConversationDialog(NativeSamConversationDialogKind kind, string conversationId)
    {
        if (_state.ConversationActionBusy
            || _state.SafeConversations.FirstOrDefault(row => row.Id == conversationId) is not { } row)
            return;
        var title = row.TitleUnreadable ? string.Empty : row.Title;
        Publish(_state with
        {
            ConversationDialog = new NativeSamConversationDialog(kind, conversationId, title,
                kind == NativeSamConversationDialogKind.Rename ? title : string.Empty),
            ConversationActionKey = null,
        });
    }

    public void SetRenameText(string text)
    {
        if (_state.ConversationDialog is { Kind: NativeSamConversationDialogKind.Rename } dialog)
            Publish(_state with { ConversationDialog = dialog with { RenameText = text ?? string.Empty } },
                structural: false);
    }

    public void CancelConversationDialog()
    {
        if (_state.ConversationDialog is not null && !_state.ConversationActionBusy)
            Publish(_state with { ConversationDialog = null });
    }

    /// <summary>Confirms the open Rename, Close or Delete dialog.</summary>
    public Task ConfirmConversationDialogAsync(CancellationToken cancellationToken = default)
    {
        if (_state.ConversationDialog is not { } dialog)
            return Task.CompletedTask;
        return dialog.Kind switch
        {
            NativeSamConversationDialogKind.Rename when !string.IsNullOrWhiteSpace(dialog.RenameText) =>
                UpdateConversationAsync(dialog.ConversationId, dialog.RenameText.Trim(), close: null,
                    "Coach_ConversationRenamed", cancellationToken),
            NativeSamConversationDialogKind.Close =>
                UpdateConversationAsync(dialog.ConversationId, title: null, close: true, null, cancellationToken),
            NativeSamConversationDialogKind.Delete =>
                DeleteConversationAsync(dialog.ConversationId, cancellationToken),
            _ => Task.CompletedTask,
        };
    }

    /// <summary>Reopen needs no confirmation (source CoachConversationList.Reopen).</summary>
    public Task ReopenConversationAsync(string conversationId, CancellationToken cancellationToken = default) =>
        UpdateConversationAsync(conversationId, title: null, close: false, "Coach_ConversationReopened",
            cancellationToken);

    private async Task UpdateConversationAsync(string conversationId, string? title, bool? close,
        string? successKey, CancellationToken cancellationToken)
    {
        if (!TryOwner(out var owner) || _state.ConversationActionBusy
            || _state.SafeConversations.FirstOrDefault(row => row.Id == conversationId) is not { } known)
            return;
        if (!connectivity.IsInternetAvailable)
        {
            Publish(_state with { ConversationActionKey = "Coach_ConversationsOffline", ConversationDialog = null });
            return;
        }
        Publish(_state with { ConversationActionBusy = true, ConversationActionKey = null });
        try
        {
            var updated = await client.CallAsync(owner,
                (api, token) => api.UpdateConversationAsync(conversationId, new UpdateCoachConversationRequest
                {
                    ExpectedStateVersion = known.StateVersion, Title = title, Close = close
                }, token), cancellationToken);
            if (!IsCurrent(owner))
                return;
            var row = Rows([updated]).Single();
            Publish(_state with
            {
                ConversationActionBusy = false,
                ConversationDialog = null,
                ConversationActionKey = successKey,
                Conversations = _state.SafeConversations
                    .Select(existing => existing.Id == conversationId ? row : existing).ToImmutableArray(),
                // A closed thread offers no composer send path (source CoachChatPane closed notice).
                ConversationClosed = _state.ConversationId == conversationId
                    ? updated.IsClosed : _state.ConversationClosed,
            });
        }
        catch (Exception ex) when (IsRecoverable(ex, cancellationToken))
        {
            logger.LogWarning(ex, "Sam conversation update did not complete.");
            if (!IsCurrent(owner))
                return;
            var gone = ex is CoachApiException { ProblemType: CoachProblemTypes.ConversationNotFound }
                or CoachApiException { StatusCode: HttpStatusCode.NotFound };
            Publish(_state with
            {
                ConversationActionBusy = false,
                ConversationDialog = null,
                ConversationActionKey = ex switch
                {
                    HttpRequestException => "Coach_ConversationsOffline",
                    CoachApiException { ProblemType: CoachProblemTypes.ConversationStateConflict }
                        => "Coach_ConversationConflict",
                    _ when gone => "Coach_ConversationGone",
                    _ => "Coach_ConversationUpdateFailed",
                },
                Conversations = gone
                    ? _state.SafeConversations.Where(row => row.Id != conversationId).ToImmutableArray()
                    : _state.SafeConversations,
            });
            // A conflict means another device changed it: show what is stored now.
            if (ex is CoachApiException { ProblemType: CoachProblemTypes.ConversationStateConflict })
                await LoadConversationsAsync(owner, cancellationToken);
        }
    }

    private async Task DeleteConversationAsync(string conversationId, CancellationToken cancellationToken)
    {
        if (!TryOwner(out var owner) || _state.ConversationActionBusy
            || _state.SafeConversations.All(row => row.Id != conversationId))
            return;
        if (!connectivity.IsInternetAvailable)
        {
            Publish(_state with { ConversationActionKey = "Coach_ConversationsOffline", ConversationDialog = null });
            return;
        }
        Publish(_state with { ConversationActionBusy = true, ConversationActionKey = null });
        try
        {
            await client.CallAsync(owner, async (api, token) =>
            {
                await api.DeleteConversationAsync(conversationId, token);
                return true;
            }, cancellationToken);
            if (!IsCurrent(owner))
                return;
            ForgetConversation(conversationId, "Coach_ConversationDeleted");
        }
        catch (Exception ex) when (IsRecoverable(ex, cancellationToken))
        {
            logger.LogWarning(ex, "Sam conversation delete did not complete; nothing was removed here.");
            if (IsCurrent(owner))
                Publish(_state with
                {
                    ConversationActionBusy = false,
                    ConversationDialog = null,
                    ConversationActionKey = ex is HttpRequestException
                        ? "Coach_ConversationsOffline" : "Coach_ConversationDeleteFailed",
                });
        }
    }

    /// <summary>
    /// Removes a deleted thread from the shelf; if it was the open one, everything
    /// shown for it goes with it and the panel offers a new conversation.
    /// </summary>
    private void ForgetConversation(string conversationId, string noticeKey)
    {
        var wasOpen = _state.ConversationId == conversationId;
        if (wasOpen)
        {
            DropConversation(conversationId);
            if (_sessionId == conversationId)
            {
                _sessionId = null;
                _planVersion = null;
            }
        }
        Publish(_state with
        {
            ConversationActionBusy = false,
            ConversationDialog = null,
            ConversationActionKey = noticeKey,
            NoticeKey = wasOpen ? null : _state.NoticeKey,
            Conversations = _state.SafeConversations.Where(row => row.Id != conversationId).ToImmutableArray(),
        });
    }

    // ---------------------------------------------------------------- Settings: delete coach history (J31)

    public void AskDeleteHistory()
    {
        if (_state.SafeSettings is { IsAvailable: true, Deleting: false } settings)
            Publish(_state with { Settings = settings with { ConfirmingDelete = true, NoticeKey = null } });
    }

    public void CancelDeleteHistory()
    {
        if (_state.SafeSettings.ConfirmingDelete)
            Publish(_state with { Settings = _state.SafeSettings with { ConfirmingDelete = false } });
    }

    /// <summary>
    /// Source Settings.ConfirmDeleteCoachHistory -> CoachWorkspace.DeleteSessionAsync: deletes the
    /// current coach session (DELETE /sessions/{id}); on a durable server that id is the
    /// conversation of the same id, which the server hides and purges. Other saved
    /// conversations are not touched, Today's Plan and progress are unchanged.
    /// One correction to the source: the source toasts "deleted" even when it held no session id
    /// and sent nothing; here the checkpoint is read first, and nothing is claimed unless the
    /// server confirmed the delete.
    /// </summary>
    public async Task ConfirmDeleteHistoryAsync(CancellationToken cancellationToken = default)
    {
        if (!TryOwner(out var owner) || _state.SafeSettings is not { IsAvailable: true, Deleting: false })
            return;
        if (!connectivity.IsInternetAvailable)
        {
            Publish(_state with { Settings = _state.SafeSettings with
                { ConfirmingDelete = false, NoticeKey = "Coach_Offline" } });
            return;
        }
        Publish(_state with { Settings = _state.SafeSettings with
            { ConfirmingDelete = false, Deleting = true, NoticeKey = null } });
        string? notice;
        try
        {
            if (_sessionId is null)
                await LoadSessionAsync(owner, cancellationToken);
            if (!IsCurrent(owner))
                return;
            if (_sessionId is not { } sessionId)
                notice = "NativeSam_CoachHistoryNothingToDelete";
            else
            {
                await client.CallAsync(owner, async (api, token) =>
                {
                    await api.DeleteSessionAsync(sessionId, token);
                    return true;
                }, cancellationToken);
                if (!IsCurrent(owner))
                    return;
                _sessionId = null;
                _planVersion = null;
                Publish(_state with { Suggestion = null, CanUndo = false, UndoRevisionId = null, LatestReceipt = null });
                if (_state.SafeConversations.Any(row => row.Id == sessionId) || _state.ConversationId == sessionId)
                    ForgetConversation(sessionId, "Coach_ConversationDeleted");
                notice = "Coach_SettingsDeleted";
            }
        }
        catch (Exception ex) when (IsRecoverable(ex, cancellationToken))
        {
            logger.LogWarning(ex, "Sam coach history delete did not complete; nothing is claimed.");
            if (!IsCurrent(owner))
                return;
            notice = ex is HttpRequestException ? "Coach_Offline" : "NativeSam_CoachHistoryDeleteFailed";
        }
        Publish(_state with { Settings = _state.SafeSettings with { Deleting = false, NoticeKey = notice } });
        await RefreshAvailabilityAsync(cancellationToken);
    }

    // ---------------------------------------------------------------- Settings: memory (J25)

    /// <summary>Loads both lists; the whole surface disappears when the route group answers 404.</summary>
    public async Task LoadMemoryAsync(CancellationToken cancellationToken = default)
    {
        if (!TryOwner(out var owner))
            return;
        if (_availability is null)
            await RefreshAvailabilityAsync(cancellationToken);
        if (!IsCurrent(owner))
            return;
        if (_availability?.IsMemoryAvailable != true)
        {
            Publish(_state with { Memory = new NativeSamMemoryState(NativeSamMemoryAvailability.Unavailable) });
            return;
        }
        if (!connectivity.IsInternetAvailable)
        {
            Publish(_state with { Memory = _state.SafeMemory with { NoticeKey = "Coach_MemoryOffline" } });
            return;
        }
        Publish(_state with { Memory = _state.SafeMemory with { Busy = true } });
        try
        {
            var active = await client.CallAsync(owner,
                (api, token) => api.ListActiveMemoriesAsync(cancellationToken: token), cancellationToken);
            if (!IsCurrent(owner))
                return;
            if (active is null)
            {
                Publish(_state with { Memory = new NativeSamMemoryState(NativeSamMemoryAvailability.Unavailable) });
                return;
            }
            var candidates = await client.CallAsync(owner,
                (api, token) => api.ListMemoryCandidatesAsync(cancellationToken: token), cancellationToken);
            if (!IsCurrent(owner))
                return;
            Publish(_state with
            {
                Memory = _state.SafeMemory with
                {
                    Availability = NativeSamMemoryAvailability.Available,
                    Active = active.Items.ToImmutableArray(),
                    Candidates = (candidates?.Items ?? []).ToImmutableArray(),
                    Busy = false,
                }
            });
        }
        catch (CoachApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound
            && !cancellationToken.IsCancellationRequested)
        {
            if (IsCurrent(owner))
                Publish(_state with { Memory = new NativeSamMemoryState(NativeSamMemoryAvailability.Unavailable) });
        }
        catch (Exception ex) when (IsRecoverable(ex, cancellationToken))
        {
            logger.LogWarning(ex, "Sam memory could not be read.");
            if (IsCurrent(owner))
                Publish(_state with { Memory = _state.SafeMemory with
                {
                    Busy = false,
                    NoticeKey = ex is HttpRequestException ? "Coach_MemoryOffline" : MemoryNoticeFor(ex),
                } });
        }
    }

    public void SelectMemoryTab(NativeSamMemoryTab tab)
    {
        if (_state.SafeMemory.Tab != tab)
            Publish(_state with { Memory = _state.SafeMemory with { Tab = tab, EditingId = null } });
    }

    public void BeginMemoryEdit(string factId)
    {
        var memory = _state.SafeMemory;
        if (memory.SafeActive.Concat(memory.SafeCandidates).FirstOrDefault(fact => fact.Id == factId) is { } fact
            && fact.Kind == CoachMemoryKind.PersistentStudyGoal)
            Publish(_state with { Memory = memory with
                { EditingId = factId, EditText = fact.Value.StudyGoalText ?? fact.DisplayText } });
    }

    public void SetMemoryEditText(string text)
    {
        if (_state.SafeMemory.EditingId is not null)
            Publish(_state with { Memory = _state.SafeMemory with { EditText = text ?? string.Empty } },
                structural: false);
    }

    public void CancelMemoryEdit()
    {
        if (_state.SafeMemory.EditingId is not null)
            Publish(_state with { Memory = _state.SafeMemory with { EditingId = null, EditText = string.Empty } });
    }

    public void AskForgetAllMemories()
    {
        if (_state.SafeMemory.Availability == NativeSamMemoryAvailability.Available)
            Publish(_state with { Memory = _state.SafeMemory with { ConfirmingForgetAll = true } });
    }

    public void CancelForgetAllMemories()
    {
        if (_state.SafeMemory.ConfirmingForgetAll)
            Publish(_state with { Memory = _state.SafeMemory with { ConfirmingForgetAll = false } });
    }

    /// <summary>Approve a candidate from Settings (it then enters later turns as bounded context).</summary>
    public Task ApproveMemoryAsync(string factId, CancellationToken cancellationToken = default) =>
        MemoryPanelWriteAsync(factId, async (api, fact, token) =>
            await api.ApproveMemoryAsync(fact.Id, new CoachMemoryApproveRequest(fact.Version), token) is not null,
            cancellationToken);

    /// <summary>"Not now" on a candidate: nothing is remembered.</summary>
    public Task RejectMemoryAsync(string factId, CancellationToken cancellationToken = default) =>
        MemoryPanelWriteAsync(factId, async (api, fact, token) =>
        {
            await api.RejectMemoryAsync(fact.Id, new CoachMemoryRejectRequest(fact.Version), token);
            return true;
        }, cancellationToken);

    /// <summary>Forget one active fact.</summary>
    public Task ForgetMemoryAsync(string factId, CancellationToken cancellationToken = default) =>
        MemoryPanelWriteAsync(factId, async (api, fact, token) =>
        {
            await api.ForgetMemoryAsync(fact.Id, fact.Version, token);
            return true;
        }, cancellationToken);

    /// <summary>Save an edited study goal: approve-with-edit for a candidate, edit for an active fact.</summary>
    public Task SaveMemoryEditAsync(CancellationToken cancellationToken = default)
    {
        var memory = _state.SafeMemory;
        if (memory.EditingId is not { } factId || string.IsNullOrWhiteSpace(memory.EditText))
            return Task.CompletedTask;
        var text = memory.EditText.Trim();
        var isCandidate = memory.SafeCandidates.Any(fact => fact.Id == factId);
        return MemoryPanelWriteAsync(factId, async (api, fact, token) =>
        {
            var value = new CoachMemoryValueDto
            {
                Kind = fact.Kind, StudyGoalText = text,
                ExplanationDepth = fact.Value.ExplanationDepth,
                CorrectionTiming = fact.Value.CorrectionTiming,
                ExampleRegister = fact.Value.ExampleRegister,
            };
            return isCandidate
                ? await api.ApproveMemoryAsync(fact.Id, new CoachMemoryApproveRequest(fact.Version, value), token)
                    is not null
                : await api.EditMemoryAsync(fact.Id, new CoachMemoryEditRequest(fact.Version, value), token)
                    is not null;
        }, cancellationToken);
    }

    public async Task ForgetAllMemoriesAsync(CancellationToken cancellationToken = default)
    {
        if (!TryOwner(out var owner) || _state.SafeMemory is not
            { Availability: NativeSamMemoryAvailability.Available, Busy: false })
            return;
        Publish(_state with { Memory = _state.SafeMemory with { Busy = true, ConfirmingForgetAll = false, NoticeKey = null } });
        var notice = await MemoryWriteAsync(owner,
            async (api, token) => await api.ForgetAllMemoriesAsync(token) is not null, cancellationToken);
        await FinishMemoryWriteAsync(owner, notice, cancellationToken);
    }

    private async Task MemoryPanelWriteAsync(string factId,
        Func<ICoachApiClient, CoachMemoryFactDto, CancellationToken, Task<bool>> write,
        CancellationToken cancellationToken)
    {
        var memory = _state.SafeMemory;
        if (!TryOwner(out var owner) || memory.Availability != NativeSamMemoryAvailability.Available
            || memory.Busy
            || memory.SafeActive.Concat(memory.SafeCandidates).FirstOrDefault(fact => fact.Id == factId)
                is not { } fact)
            return;
        Publish(_state with { Memory = memory with { Busy = true, NoticeKey = null } });
        var notice = await MemoryWriteAsync(owner, (api, token) => write(api, fact, token), cancellationToken);
        await FinishMemoryWriteAsync(owner, notice, cancellationToken);
    }

    private async Task FinishMemoryWriteAsync(string owner, string notice, CancellationToken cancellationToken)
    {
        if (!IsCurrent(owner))
            return;
        Publish(_state with { Memory = _state.SafeMemory with { Busy = false, EditingId = null, EditText = string.Empty } });
        if (notice is not ("Coach_MemoryOffline" or "Coach_MemoryUnavailable"))
            await LoadMemoryAsync(cancellationToken);
        if (IsCurrent(owner))
            Publish(_state with { Memory = _state.SafeMemory with { NoticeKey = notice } });
    }

    /// <summary>One memory write; returns the source notice key for its outcome.</summary>
    private async Task<string> MemoryWriteAsync(string owner,
        Func<ICoachApiClient, CancellationToken, Task<bool>> write, CancellationToken cancellationToken)
    {
        if (!connectivity.IsInternetAvailable)
            return "Coach_MemoryOffline";
        try
        {
            var found = await client.CallAsync(owner, write, cancellationToken);
            return found ? "Coach_MemorySaved" : "Coach_MemoryGone";
        }
        catch (Exception ex) when (IsRecoverable(ex, cancellationToken))
        {
            logger.LogWarning(ex, "Sam memory write did not complete.");
            return ex is HttpRequestException ? "Coach_MemoryOffline" : MemoryNoticeFor(ex);
        }
    }

    private static string MemoryNoticeFor(Exception ex) => ex switch
    {
        CoachApiException { ProblemType: CoachMemoryProblemTypes.Conflict } => "Coach_MemoryConflict",
        CoachApiException { ProblemType: CoachMemoryProblemTypes.ValueRejected } => "Coach_MemoryValueRejected",
        CoachApiException { ProblemType: CoachMemoryProblemTypes.Unavailable } => "Coach_MemoryUnavailable",
        CoachApiException { StatusCode: HttpStatusCode.Conflict } => "Coach_MemoryConflict",
        CoachApiException { StatusCode: HttpStatusCode.UnprocessableEntity } => "Coach_MemoryValueRejected",
        CoachApiException { StatusCode: HttpStatusCode.NotFound } => "Coach_MemoryGone",
        _ => "Coach_MemoryUnavailable",
    };
}
