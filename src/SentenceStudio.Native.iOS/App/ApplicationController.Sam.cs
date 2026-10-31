using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Features.Sam;
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

    /// <summary>The entry appears only on an authenticated dashboard shell (source CoachSurfaceGate).</summary>
    internal bool IsSamEntryVisible =>
        _destination == NativeDestination.Dashboard && !_preview
        && SamState.IsVisible && !SamState.IsOpen;

    internal bool IsSamPresented => _destination is NativeDestination.Dashboard
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
        StartReview: () => _sam?.StartVocabularyReview());

    private bool _samCompactDefault = true;

    /// <summary>Phones open compact; iPad (regular width) opens expanded (source: below 992 px compact).</summary>
    internal void SetSamViewport(double width) => _samCompactDefault = width < 768;

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

    internal SwiftView RenderSamStream(SourceUiStyle style) =>
        SamView.Stream(style, SamState, Text, SamActions);

    internal SwiftView RenderSamConversations(SourceUiStyle style) =>
        SamView.Conversations(style, SamState, Text, SamActions);
}
