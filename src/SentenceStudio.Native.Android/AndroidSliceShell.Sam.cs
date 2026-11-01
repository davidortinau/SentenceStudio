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

    private bool IsSamEntryVisible =>
        AndroidSamProjection.IsEntryVisible(Destination, _preview,
            _appearanceSettingsOpen || IsResourceFormOpen, SamState)
        && HasDashboardOwner(Auth.UserId);

    private bool IsSamPresented =>
        AndroidSamProjection.IsPresented(Destination, _preview, SamState)
        && !_appearanceSettingsOpen && HasDashboardOwner(Auth.UserId);

    private int WindowWidthDp => activity.Resources?.Configuration?.ScreenWidthDp ?? 0;

    private int WindowHeightDp => activity.Resources?.Configuration?.ScreenHeightDp ?? 0;

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
        _samListOwner = null;
        _samLastKey = null;
        _samLastConversation = null;
        // Unsynced keystrokes must never reach the next owner's presenter.
        _samDraftSynced = string.Empty;
        _samDraft.Value = AndroidX.Compose.ComposeExtensions.NewTextFieldValue(string.Empty);
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

    /// <summary>A server-side plan revision must reach the device's Today's Plan (sync, then reload).</summary>
    private void OnSamPlanChanged()
    {
        if (_dashboard is null || !HasDashboardOwner(Auth.UserId)
            || Destination is not (NativeDestination.Dashboard or NativeDestination.TodaysPlan))
            return;
        Execute(async cancellationToken => await _dashboard.RefreshAsync(cancellationToken));
    }

    /// <summary>
    /// Start Vocab Review on a Sam-approved set: the set was created on the
    /// server, so it must sync to this device before the native quiz validates
    /// it against the owner's local resources.
    /// </summary>
    private void OnSamVocabularyReview(string owner, string resourceId) => Execute(async cancellationToken =>
    {
        if (_scope is null || !HasDashboardOwner(owner) || Auth.UserId != owner
            || Destination is not (NativeDestination.Dashboard or NativeDestination.TodaysPlan))
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
