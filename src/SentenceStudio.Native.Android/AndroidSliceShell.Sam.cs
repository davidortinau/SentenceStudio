using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Native;

namespace SentenceStudio.Native.Android;

/// <summary>
/// Binds the shared <see cref="NativeSamPresenter"/> to the Compose shell. Sam
/// runs its own commands (not the shell's single-action gate) so a running
/// turn never blocks the Dashboard, and the presenter, not this head, decides
/// owner, offline, availability and every write.
/// </summary>
internal sealed partial class AndroidSliceShell
{
    private NativeSamPresenter? _sam;

    private NativeSamState SamState => _sam?.State ?? new NativeSamState();

    private AndroidSamEntryPlacement SamEntryPlacement =>
        AndroidSamProjection.EntryPlacement(Destination, _preview,
            IsMoreScreenOpen || IsResourceFormOpen, SamState) is var placement
        && placement != AndroidSamEntryPlacement.None && HasDashboardOwner(Auth.UserId)
            ? placement : AndroidSamEntryPlacement.None;

    private bool IsSamEntryVisible => SamEntryPlacement != AndroidSamEntryPlacement.None;

    private bool IsSamPresented =>
        AndroidSamProjection.IsPresented(Destination, _preview, SamState)
        && HasDashboardOwner(Auth.UserId);

    private int WindowWidthDp => activity.Resources?.Configuration?.ScreenWidthDp ?? 0;

    private int WindowHeightDp => activity.Resources?.Configuration?.ScreenHeightDp ?? 0;

    /// <summary>The activity's font scale: the system size times the app's text size (AndroidFontScale).</summary>
    private float WindowFontScale => activity.Resources?.Configuration?.FontScale is > 0f and var scale
        ? scale : 1f;

    private NativeSamPresenter? EnsureSam()
    {
        if (_sam is not null || _scope is null || _preview || _disposed)
            return _sam;
        _sam = _scope.ServiceProvider.GetRequiredService<NativeSamPresenter>();
        _sam.Changed += OnSamChanged;
        _sam.PlanChanged += OnSamPlanChanged;
        _sam.VocabularyReviewRequested += OnSamVocabularyReview;
        return _sam;
    }

    private void RefreshSamAvailability()
    {
        if (_preview || !HasDashboardOwner(Auth.UserId))
            return;
        RunSam(sam => sam.RefreshAvailabilityAsync(_lifetime.Token));
    }

    /// <summary>Sign-out or owner change: nothing of the previous owner's Sam survives.</summary>
    private void ClearSamForAccountBoundary()
    {
        _samPlanRefreshPending = false;
        _samListOwner = null;
        _samLastKey = null;
        _samLastConversation = null;
        // Unsynced keystrokes must never reach the next owner's presenter.
        _samDraftSynced = string.Empty;
        _samDraft.Value = AndroidX.Compose.ComposeExtensions.NewTextFieldValue(string.Empty);
        _samRenameEdit.Seed(null, string.Empty);
        _samCandidateEdit.Seed(null, string.Empty);
        _samMemoryEdit.Seed(null, string.Empty);
        _samConversationMenu = null;
        _sam?.ClearForAccountBoundary();
    }

    /// <summary>The scope that owns the presenter is going away (preview switch or dispose).</summary>
    private void DetachSam()
    {
        if (_sam is not { } sam)
            return;
        _sam = null;
        sam.ClearForAccountBoundary();
        sam.Changed -= OnSamChanged;
        sam.PlanChanged -= OnSamPlanChanged;
        sam.VocabularyReviewRequested -= OnSamVocabularyReview;
    }

    // Draft edits re-render synchronously on the UI thread so Compose keeps the
    // composer's selection and IME (Korean) composition in step with the draft.
    private void OnSamChanged()
    {
        if (global::Android.OS.Looper.MyLooper() == global::Android.OS.Looper.MainLooper)
        {
            if (_disposed)
                return;
            SyncSamDraftFromPresenter();
            SeedSamEdits();
            // A draft-only change is not structural (same Version): the composer
            // scope already shows it, so the whole shell does not re-render per key.
            if (SamState.Version == _samPublishedVersion)
                return;
            _samPublishedVersion = SamState.Version;
            ApplyPublishedState();
        }
        else
            Publish(() =>
            {
                SyncSamDraftFromPresenter();
                SeedSamEdits();
                _samPublishedVersion = SamState.Version;
            });
    }

    private long _samPublishedVersion = -1;

    /// <summary>
    /// The presenter changed the draft itself (cleared on send, or reset at an
    /// account boundary): the composer field follows. Typed edits never loop
    /// back because they are recorded as synced before they reach the presenter.
    /// </summary>
    private void SyncSamDraftFromPresenter()
    {
        var draft = SamState.Draft;
        if (string.Equals(draft, _samDraftSynced, StringComparison.Ordinal))
            return;
        _samDraftSynced = draft;
        if (!string.Equals(_samDraft.Value.Text, draft, StringComparison.Ordinal))
            _samDraft.Value = AndroidX.Compose.ComposeExtensions.NewTextFieldValue(draft);
    }

    private async void RunSam(Func<NativeSamPresenter, Task> action)
    {
        if (_disposed || EnsureSam() is not { } sam)
            return;
        try
        {
            await action(sam);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("SentenceStudio.Native",
                $"Android Sam action failed ({ex.GetType().Name}).");
        }
        Publish(() => { });
    }

    private void OpenSam()
    {
        if (!IsSamEntryVisible)
            return;
        RunSam(sam => sam.OpenAsync(AndroidSamProjection.DefaultSize(WindowWidthDp), _lifetime.Token));
    }

    private void CloseSam() => _sam?.Close();

    private void ToggleSamSize() => _sam?.SetSize(SamState.Size == NativeSamPanelSize.Compact
        ? NativeSamPanelSize.Expanded : NativeSamPanelSize.Compact);

    private void ToggleSamConversations() => RunSam(sam => sam.ToggleConversationsAsync(_lifetime.Token));

    private void NewSamConversation() => RunSam(sam => sam.NewConversationAsync(_lifetime.Token));

    private void OpenSamConversation(string id) => RunSam(sam => sam.OpenConversationAsync(id, _lifetime.Token));

    private void SendSam()
    {
        // Push the field's latest text first: the composer syncs the draft after
        // composition, so a keystroke and Send in one frame would send stale text.
        var typed = _samDraft.Value.Text ?? string.Empty;
        if (!string.Equals(typed, _samDraftSynced, StringComparison.Ordinal))
        {
            _samDraftSynced = typed;
            _sam?.SetDraft(typed);
        }
        if (SamState.CanSend)
            RunSam(sam => sam.SendAsync(_lifetime.Token));
    }

    private void RetrySam() => RunSam(sam => sam.RetryAsync(_lifetime.Token));

    private void StopSam() => RunSam(sam => sam.StopAsync(_lifetime.Token));

    private void DismissSamNotice() => _sam?.DismissNotice();

    private void ReloadSamPlan() => RunSam(sam => sam.ReloadPlanAsync(_lifetime.Token));

    private void AcceptSamSuggestion() => RunSam(sam => sam.AcceptSuggestionAsync(_lifetime.Token));

    private void RejectSamSuggestion() => RunSam(sam => sam.RejectSuggestionAsync(_lifetime.Token));

    private void UndoSam() => RunSam(sam => sam.UndoAsync(_lifetime.Token));

    private void ApplySamChip(NativeSamChip chip) => RunSam(sam => sam.ApplyChipAsync(chip, _lifetime.Token));

    private void ToggleSamEvidence(string entryId) => _sam?.ToggleEvidence(entryId);

    private void ApproveSamSet() => RunSam(sam => sam.ApproveVocabularySetAsync(_lifetime.Token));

    private void DeclineSamSet() => RunSam(sam => sam.DeclineVocabularySetAsync(_lifetime.Token));

    private void StartSamReview() => _sam?.StartVocabularyReview();

    // ------------------------------------------------ proposed changes (source SamWriteCard)

    private void RunSamWrite(AndroidSamWriteAction action, string operationId)
    {
        switch (action)
        {
            case AndroidSamWriteAction.Accept:
                RunSam(sam => sam.AcceptWriteAsync(operationId, _lifetime.Token));
                break;
            case AndroidSamWriteAction.Review:
                RunSam(sam => sam.BeginWriteConfirmationAsync(operationId, _lifetime.Token));
                break;
            case AndroidSamWriteAction.Confirm:
                // The one-use value stays inside the presenter; this head never sees it.
                RunSam(sam => sam.ConfirmWriteAsync(_lifetime.Token));
                break;
            case AndroidSamWriteAction.CancelConfirm:
                _sam?.CancelWriteConfirmation();
                break;
            case AndroidSamWriteAction.Decline:
                RunSam(sam => sam.DeclineWriteAsync(operationId, _lifetime.Token));
                break;
            case AndroidSamWriteAction.Undo:
                RunSam(sam => sam.UndoWriteAsync(operationId, _lifetime.Token));
                break;
            case AndroidSamWriteAction.Refresh:
                RunSam(sam => sam.RefreshWriteAsync(operationId, _lifetime.Token));
                break;
        }
    }

    // ------------------------------------------------ Copy and report (CoachCopyButton, CoachReportControl)

    /// <summary>Copy uses the Essentials clipboard (non-UI service); the control states the outcome.</summary>
    private void CopySamMessage(string messageId) => RunSam(async sam =>
    {
        if (sam.CopyTextFor(messageId) is not { } text)
            return;
        var owner = Auth.UserId;
        bool copied;
        try
        {
            await AndroidSamClipboard.SetTextAsync(text);
            copied = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never the message text: only the failure type.
            global::Android.Util.Log.Warn("SentenceStudio.Native",
                $"Sam message could not be copied ({ex.GetType().Name}).");
            copied = false;
        }
        // The owner changed while the clipboard call was in flight: the result belongs to a
        // previous account's message and must not surface in this one (fail closed).
        if (!AndroidSamCards.ShouldApplyCopyResult(owner, Auth.UserId) || !ReferenceEquals(sam, _sam))
            return;
        sam.ReportCopyResult(messageId, copied);
    });

    private void OpenSamReport(string messageId) => _sam?.OpenReport(messageId);

    private void SelectSamReportReason(SentenceStudio.Contracts.Coach.CoachResponseReportReason reason) =>
        _sam?.SelectReportReason(reason);

    private void SubmitSamReport() => RunSam(sam => sam.SubmitReportAsync(_lifetime.Token));

    private void CancelSamReport() => _sam?.CancelReport();

    // ------------------------------------------------ memory candidate (CoachMemoryCandidateCard)

    private void SaveSamMemoryCandidate() => RunSam(sam => sam.SaveMemoryCandidateAsync(_lifetime.Token));

    private void BeginSamMemoryCandidateEdit() => _sam?.BeginMemoryCandidateEdit();

    private void SaveEditedSamMemoryCandidate()
    {
        PushSamEdit(_samCandidateEdit, text => _sam?.SetMemoryCandidateEdit(text));
        RunSam(sam => sam.SaveEditedMemoryCandidateAsync(_lifetime.Token));
    }

    private void CancelSamMemoryCandidateEdit() => _sam?.CancelMemoryCandidateEdit();

    private void DeclineSamMemoryCandidate() => RunSam(sam => sam.DeclineMemoryCandidateAsync(_lifetime.Token));

    // ------------------------------------------------ conversation shelf (CoachConversationList)

    private void BeginSamConversationDialog(NativeSamConversationDialogKind kind, string conversationId)
    {
        _samConversationMenu = null;
        _sam?.BeginConversationDialog(kind, conversationId);
    }

    private void ConfirmSamConversationDialog()
    {
        PushSamEdit(_samRenameEdit, text => _sam?.SetRenameText(text));
        RunSam(sam => sam.ConfirmConversationDialogAsync(_lifetime.Token));
    }

    private void CancelSamConversationDialog() => _sam?.CancelConversationDialog();

    private void ReopenSamConversation(string conversationId)
    {
        _samConversationMenu = null;
        RunSam(sam => sam.ReopenConversationAsync(conversationId, _lifetime.Token));
    }

    private void ToggleSamConversationMenu(string? conversationId) =>
        Publish(() => _samConversationMenu = conversationId);

    // ------------------------------------------------ Settings: Learning Coach + What Sam remembers

    /// <summary>Opening Settings reads availability and what Sam remembers for the current owner.</summary>
    private void OpenSamSettings()
    {
        if (_preview || !HasDashboardOwner(Auth.UserId))
            return;
        RunSam(async sam =>
        {
            await sam.RefreshAvailabilityAsync(_lifetime.Token);
            await sam.LoadMemoryAsync(_lifetime.Token);
        });
    }

    private void AskDeleteSamHistory() => EnsureSam()?.AskDeleteHistory();

    private void CancelDeleteSamHistory() => _sam?.CancelDeleteHistory();

    private void ConfirmDeleteSamHistory() => RunSam(sam => sam.ConfirmDeleteHistoryAsync(_lifetime.Token));

    private void SelectSamMemoryTab(NativeSamMemoryTab tab) => _sam?.SelectMemoryTab(tab);

    private void ApproveSamMemory(string id) => RunSam(sam => sam.ApproveMemoryAsync(id, _lifetime.Token));

    private void RejectSamMemory(string id) => RunSam(sam => sam.RejectMemoryAsync(id, _lifetime.Token));

    private void ForgetSamMemory(string id) => RunSam(sam => sam.ForgetMemoryAsync(id, _lifetime.Token));

    private void BeginSamMemoryEdit(string id) => _sam?.BeginMemoryEdit(id);

    private void SaveSamMemoryEdit()
    {
        PushSamEdit(_samMemoryEdit, text => _sam?.SetMemoryEditText(text));
        RunSam(sam => sam.SaveMemoryEditAsync(_lifetime.Token));
    }

    private void CancelSamMemoryEdit() => _sam?.CancelMemoryEdit();

    private void AskForgetAllSamMemories() => _sam?.AskForgetAllMemories();

    private void CancelForgetAllSamMemories() => _sam?.CancelForgetAllMemories();

    private void ConfirmForgetAllSamMemories() => RunSam(sam => sam.ForgetAllMemoriesAsync(_lifetime.Token));

    // ------------------------------------------------ edit fields

    /// <summary>
    /// A Sam text field owns its TextFieldValue (selection, Korean IME composition);
    /// the presenter's text follows each committed edit. Seeding happens only when
    /// the edit target changes, so a structural re-render never overwrites typing.
    /// </summary>
    private sealed class SamTextEdit
    {
        internal readonly AndroidX.Compose.MutableState<AndroidX.Compose.UI.Text.Input.TextFieldValue> Value =
            new(AndroidX.Compose.ComposeExtensions.NewTextFieldValue(string.Empty));
        internal string? Key;
        internal string Synced = string.Empty;

        internal void Seed(string? key, string text)
        {
            if (string.Equals(key, Key, StringComparison.Ordinal))
                return;
            Key = key;
            Synced = text;
            Value.Value = AndroidX.Compose.ComposeExtensions.NewTextFieldValue(text);
        }
    }

    private readonly SamTextEdit _samRenameEdit = new();
    private readonly SamTextEdit _samCandidateEdit = new();
    private readonly SamTextEdit _samMemoryEdit = new();
    private string? _samConversationMenu;

    private void SeedSamEdits()
    {
        var state = SamState;
        var rename = AndroidSamCards.RenameSeed(state);
        _samRenameEdit.Seed(rename.Key, rename.Text);
        var candidate = AndroidSamCards.CandidateSeed(state);
        _samCandidateEdit.Seed(candidate.Key, candidate.Text);
        var memory = AndroidSamCards.MemorySeed(state);
        _samMemoryEdit.Seed(memory.Key, memory.Text);
    }

    /// <summary>Pushes the field's latest text before a save, as Send does for the composer.</summary>
    private static void PushSamEdit(SamTextEdit edit, Action<string> push)
    {
        var typed = edit.Value.Value.Text ?? string.Empty;
        if (edit.Key is null || string.Equals(typed, edit.Synced, StringComparison.Ordinal))
            return;
        edit.Synced = typed;
        push(typed);
    }

    /// <summary>A server-side plan revision must reach the device's Today's Plan (sync, then reload).</summary>
    private void OnSamPlanChanged()
    {
        if (_dashboard is null || !HasDashboardOwner(Auth.UserId))
            return;
        // Sam is open over another screen: refresh when the learner is back on the Dashboard.
        if (Destination is not (NativeDestination.Dashboard or NativeDestination.TodaysPlan))
        {
            _samPlanRefreshPending = true;
            return;
        }
        _samPlanRefreshPending = false;
        Execute(async cancellationToken => await _dashboard.RefreshAsync(cancellationToken));
    }

    private bool _samPlanRefreshPending;

    /// <summary>Runs a plan refresh that Sam asked for while another screen was open.</summary>
    private void RunPendingSamPlanRefresh()
    {
        if (_samPlanRefreshPending && !_disposed
            && Destination is NativeDestination.Dashboard or NativeDestination.TodaysPlan)
            OnSamPlanChanged();
    }

    /// <summary>
    /// Start Vocab Review on a Sam-approved set: the set was created on the
    /// server, so it must sync to this device before the native quiz validates
    /// it against the owner's local resources.
    /// </summary>
    private void OnSamVocabularyReview(string owner, string resourceId)
    {
        // The quiz replaces the current screen: an activity, a running quiz or an
        // open resource form must close first.
        if (Destination is NativeDestination.Activity or NativeDestination.VocabularyQuiz
            or NativeDestination.QuizSummary || IsResourceFormOpen)
        {
            _sam?.ReportVocabularyReviewBlockedByScreen();
            return;
        }
        StartSamVocabularyReview(owner, resourceId);
    }

    private void StartSamVocabularyReview(string owner, string resourceId) => Execute(async cancellationToken =>
    {
        if (_scope is null || !HasDashboardOwner(owner) || Auth.UserId != owner
            || Destination is not (NativeDestination.Dashboard or NativeDestination.TodaysPlan
                or NativeDestination.Resources))
            return;
        var provider = _scope.ServiceProvider;
        var outcome = await provider.GetRequiredService<INativeSyncReadiness>()
            .SynchronizeAsync(owner, cancellationToken);
        var launch = outcome == NativeSyncOutcome.Confirmed && HasDashboardOwner(owner)
            ? await provider.GetRequiredService<NativeQuizLaunchService>()
                .CreateChooseOwnAsync(owner, [resourceId], skillId: null,
                    cancellationToken: cancellationToken)
            : null;
        if (launch is null)
        {
            global::Android.Util.Log.Warn("SentenceStudio.Native",
                $"Sam vocabulary set is not launchable yet ({outcome}).");
            _sam?.ReportVocabularyReviewNotReady();
            return;
        }
        Post(() => _sam?.Close());
        await StartQuizAsync(launch, cancellationToken);
    });
}
