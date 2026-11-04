using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Features.Sam;
using SentenceStudio.Native.iOS.Platform;
using SentenceStudio.Native.iOS.Styling;

namespace SentenceStudio.Native.iOS.App;

/// <summary>
/// Wires the shared <see cref="NativeSamPresenter"/> into the native shell. Sam
/// keeps its own busy state so a running turn never blocks app navigation, and
/// the presenter, not this head, decides owner, offline and availability.
/// </summary>
internal sealed partial class ApplicationController
{
    private NativeSamPresenter? _sam;
    private SamActions? _samActions;

    internal NativeSamState SamState => _sam?.State ?? new NativeSamState();

    /// <summary>
    /// Source CoachSurfaceGate: signed in, past onboarding, not syncing. Every
    /// screen of the signed-in tab shell qualifies (the Dashboard and Resources
    /// tabs, the screens pushed over them, the quiz and every activity); sign-in,
    /// the post-login sync and onboarding do not.
    /// </summary>
    internal static bool IsSamShellDestination(NativeDestination destination) =>
        destination is NativeDestination.Dashboard or NativeDestination.VocabularyQuiz
            or NativeDestination.QuizSummary or NativeDestination.Activity;

    /// <summary>Sam is offered to this owner on the current screen (open or not).</summary>
    internal bool IsSamAvailable =>
        IsSamShellDestination(_destination) && !_preview && SamState.IsVisible;

    internal bool IsSamEntryVisible => IsSamAvailable && !SamState.IsOpen;

    internal bool IsSamPresented => IsSamShellDestination(_destination)
        && SamState.IsOpen;

    internal SamActions SamActions => _samActions ??= new SamActions(
        Open: () => RunSam(sam => sam.OpenAsync(
            _samCompactDefault ? NativeSamPanelSize.Compact : NativeSamPanelSize.Expanded, _lifetime.Token)),
        Close: () => _sam?.Close(),
        ToggleSize: () => _sam?.SetSize(SamState.Size == NativeSamPanelSize.Compact
            ? NativeSamPanelSize.Expanded : NativeSamPanelSize.Compact),
        ToggleConversations: () => RunSam(sam => sam.ToggleConversationsAsync(_lifetime.Token)),
        NewConversation: () => RunSam(sam => sam.NewConversationAsync(_lifetime.Token)),
        OpenConversation: id => RunSam(sam => sam.OpenConversationAsync(id, _lifetime.Token)),
        DraftChanged: text => _sam?.SetDraft(text),
        Send: () => RunSam(sam => sam.SendAsync(_lifetime.Token)),
        Retry: () => RunSam(sam => sam.RetryAsync(_lifetime.Token)),
        Stop: () => RunSam(sam => sam.StopAsync(_lifetime.Token)),
        Dismiss: () => _sam?.DismissNotice(),
        ReloadPlan: () => RunSam(sam => sam.ReloadPlanAsync(_lifetime.Token)),
        Accept: () => RunSam(sam => sam.AcceptSuggestionAsync(_lifetime.Token)),
        Reject: () => RunSam(sam => sam.RejectSuggestionAsync(_lifetime.Token)),
        Undo: () => RunSam(sam => sam.UndoAsync(_lifetime.Token)),
        Chip: chip => RunSam(sam => sam.ApplyChipAsync(chip, _lifetime.Token)),
        ToggleEvidence: id => _sam?.ToggleEvidence(id),
        ApproveSet: () => RunSam(sam => sam.ApproveVocabularySetAsync(_lifetime.Token)),
        DeclineSet: () => RunSam(sam => sam.DeclineVocabularySetAsync(_lifetime.Token)),
        StartReview: () => _sam?.StartVocabularyReview(),
        Writes: new SamWriteActions(
            Accept: id => RunSam(sam => sam.AcceptWriteAsync(id, _lifetime.Token)),
            Review: id => RunSam(sam => sam.BeginWriteConfirmationAsync(id, _lifetime.Token)),
            Confirm: () => RunSam(sam => sam.ConfirmWriteAsync(_lifetime.Token)),
            CancelConfirm: () => _sam?.CancelWriteConfirmation(),
            Decline: id => RunSam(sam => sam.DeclineWriteAsync(id, _lifetime.Token)),
            Undo: id => RunSam(sam => sam.UndoWriteAsync(id, _lifetime.Token)),
            Refresh: id => RunSam(sam => sam.RefreshWriteAsync(id, _lifetime.Token))),
        Memory: new SamMemoryActions(
            Save: () => RunSam(sam => sam.SaveMemoryCandidateAsync(_lifetime.Token)),
            BeginEdit: () => _sam?.BeginMemoryCandidateEdit(),
            EditChanged: text => _sam?.SetMemoryCandidateEdit(text),
            SaveEdited: () => RunSam(sam => sam.SaveEditedMemoryCandidateAsync(_lifetime.Token)),
            CancelEdit: () => _sam?.CancelMemoryCandidateEdit(),
            Decline: () => RunSam(sam => sam.DeclineMemoryCandidateAsync(_lifetime.Token))),
        Messages: new SamMessageActions(
            Copy: id => RunSam(sam => CopySamMessageAsync(sam, id)),
            OpenReport: id => _sam?.OpenReport(id),
            SelectReason: reason => _sam?.SelectReportReason(reason),
            SubmitReport: () => RunSam(sam => sam.SubmitReportAsync(_lifetime.Token)),
            CancelReport: () => _sam?.CancelReport()),
        ManageConversations: new SamConversationActions(
            Begin: (kind, id) => _sam?.BeginConversationDialog(kind, id),
            RenameChanged: text => _sam?.SetRenameText(text),
            Confirm: () => RunSam(sam => sam.ConfirmConversationDialogAsync(_lifetime.Token)),
            Cancel: () => _sam?.CancelConversationDialog(),
            Reopen: id => RunSam(sam => sam.ReopenConversationAsync(id, _lifetime.Token))));

    private SamSettingsActions? _samSettingsActions;

    internal SamSettingsActions SamSettingsActions => _samSettingsActions ??= new SamSettingsActions(
        AskDelete: () => EnsureSam()?.AskDeleteHistory(),
        CancelDelete: () => _sam?.CancelDeleteHistory(),
        ConfirmDelete: () => RunSam(sam => sam.ConfirmDeleteHistoryAsync(_lifetime.Token)),
        SelectTab: tab => _sam?.SelectMemoryTab(tab),
        Approve: id => RunSam(sam => sam.ApproveMemoryAsync(id, _lifetime.Token)),
        Reject: id => RunSam(sam => sam.RejectMemoryAsync(id, _lifetime.Token)),
        Forget: id => RunSam(sam => sam.ForgetMemoryAsync(id, _lifetime.Token)),
        BeginEdit: id => _sam?.BeginMemoryEdit(id),
        EditChanged: text => _sam?.SetMemoryEditText(text),
        SaveEdit: () => RunSam(sam => sam.SaveMemoryEditAsync(_lifetime.Token)),
        CancelEdit: () => _sam?.CancelMemoryEdit(),
        AskForgetAll: () => _sam?.AskForgetAllMemories(),
        CancelForgetAll: () => _sam?.CancelForgetAllMemories(),
        ConfirmForgetAll: () => RunSam(sam => sam.ForgetAllMemoriesAsync(_lifetime.Token)));

    /// <summary>
    /// Settings re-renders on Learning Coach or memory changes only; typed edit text is
    /// excluded so the field keeps focus while the learner types.
    /// </summary>
    internal int SamSettingsVersion => HashCode.Combine(SamState.SafeSettings,
        SamState.SafeMemory with { EditText = string.Empty });

    internal SwiftView? RenderSamSettings(SourceUiStyle style) =>
        _preview || _sam is null && _services is null ? null
            : SamSettingsView.Sections(style, SamState, Text, SamSettingsActions);

    /// <summary>Opening Settings reads availability and what Sam remembers for the current owner.</summary>
    private void OpenSamSettings()
    {
        if (_preview)
            return;
        RunSam(async sam =>
        {
            await sam.RefreshAvailabilityAsync(_lifetime.Token);
            await sam.LoadMemoryAsync(_lifetime.Token);
        });
    }

    /// <summary>Copy uses the Essentials clipboard (non-UI service); the control states the outcome.</summary>
    private async Task CopySamMessageAsync(NativeSamPresenter sam, string messageId)
    {
        if (sam.CopyTextFor(messageId) is not { } text)
            return;
        try
        {
            await NativeClipboard.SetTextAsync(text);
            sam.ReportCopyResult(messageId, copied: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Sam message could not be copied.");
            sam.ReportCopyResult(messageId, copied: false);
        }
    }

    private bool _samCompactDefault = true;

    /// <summary>The panel opens compact below 992 pt and expanded at or above it (source J1).</summary>
    internal void SetSamViewport(double width) => _samCompactDefault = width < 992;

    internal void DismissSam() => _sam?.Close();

    private NativeSamPresenter? EnsureSam()
    {
        if (_sam is not null || _services is null)
            return _sam;
        _sam = _services.Provider.GetRequiredService<NativeSamPresenter>();
        _sam.Changed += Publish;
        _sam.PlanChanged += OnSamPlanChanged;
        _sam.VocabularyReviewRequested += OnSamVocabularyReview;
        return _sam;
    }

    private void RefreshSamAvailability()
    {
        if (_preview)
            return;
        RunSam(sam => sam.RefreshAvailabilityAsync(_lifetime.Token));
    }

    private void ClearSamForAccountBoundary() => _sam?.ClearForAccountBoundary();

    private async void RunSam(Func<NativeSamPresenter, Task> action)
    {
        if (_disposed || EnsureSam() is not { } sam)
            return;
        try
        {
            await action(sam);
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Native iOS Sam action failed.");
        }
        Publish();
    }

    /// <summary>A server-side plan revision must reach the device's Today's Plan (sync, then reload).</summary>
    private void OnSamPlanChanged()
    {
        if (_dashboard is null || _destination != NativeDestination.Dashboard)
            return;
        _ = RefreshDashboardForSamAsync();
    }

    private async Task RefreshDashboardForSamAsync()
    {
        try
        {
            _dashboardRefreshing = true;
            Publish();
            _dashboardState = await _dashboard!.RefreshAsync(_lifetime.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !_disposed)
        {
            _logger?.LogWarning(ex, "Dashboard refresh after a Sam plan change failed.");
        }
        finally
        {
            _dashboardRefreshing = false;
            Publish();
        }
    }

    /// <summary>
    /// Start Vocab Review on a Sam-approved set: the set was created on the server,
    /// so it must sync to this device before the native Vocabulary Quiz validates it
    /// against the owner's local resources.
    /// </summary>
    private void OnSamVocabularyReview(string owner, string resourceId)
    {
        // The quiz opens over the Dashboard tab's root, so a screen open over it
        // (an activity, the quiz, or a pushed Dashboard screen) must close first.
        if (_destination != NativeDestination.Dashboard || _settingsOpen || IsProfileOpen
            || IsSkillsOpen || IsVocabularyOpen || IsActivityLogOpen)
        {
            if (IsSamShellDestination(_destination))
                _sam?.ReportVocabularyReviewBlockedByScreen();
            return;
        }
        Run(async () =>
        {
            if (_services is null || _destination != NativeDestination.Dashboard)
                return;
            var outcome = await _services.Provider.GetRequiredService<INativeSyncReadiness>()
                .SynchronizeAsync(owner, _lifetime.Token);
            var launch = outcome == NativeSyncOutcome.Confirmed
                ? await _services.Provider.GetRequiredService<NativeQuizLaunchService>()
                    .CreateChooseOwnAsync(owner, [resourceId], skillId: null,
                        cancellationToken: _lifetime.Token)
                : null;
            if (launch is null)
            {
                _logger?.LogWarning("Sam vocabulary set {Resource} is not launchable yet ({Outcome}).",
                    resourceId, outcome);
                _sam?.ReportVocabularyReviewNotReady();
                return;
            }
            _sam?.Close();
            _selectedTab = 0;
            await StartQuizAsync(launch);
        });
    }

    /// <summary>Floats the Sam entry over the dashboard's bottom trailing edge, inside the safe area.</summary>
    private SwiftView WithSamEntry(SwiftView page, SourceUiStyle style) =>
        IsSamEntryVisible && page.Kind == "scroll"
            ? Microsoft.SwiftUI.SwiftUI.BottomInset(page,
                SamView.Fab(style, SamState, Text, SamActions.Open))
            : page;

    /// <summary>The same entry on the Resources tab (source: the FAB is on every authenticated page).</summary>
    private SwiftView? ResourcesSamEntry(SourceUiStyle style) =>
        IsSamEntryVisible
            ? SamView.Fab(style, SamState, Text, SamActions.Open, "native.sam.fab.resources")
            : null;

    internal SwiftView RenderSamStream(SourceUiStyle style) =>
        SamView.Stream(style, SamState, Text, SamActions);

    internal SwiftView RenderSamConversations(SourceUiStyle style) =>
        SamView.Conversations(style, SamState, Text, SamActions);
}
