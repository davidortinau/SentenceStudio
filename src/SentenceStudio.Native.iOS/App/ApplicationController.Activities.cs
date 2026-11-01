using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Features.Activities;
using SentenceStudio.Native.iOS.Styling;
using SentenceStudio.Services.Progress;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.App;

/// <summary>
/// Registered learning activities (NativeActivityCatalog) launched from a
/// Today's Plan card or a Choose My Own tile and pushed on the Dashboard
/// NavigationStack, like the quiz. Exit always stops and saves the timer and
/// returns to a refreshed Dashboard.
/// </summary>
internal sealed partial class ApplicationController
{
    private IActivityScreen? _activity;
    private bool _activityPopPending;

    internal bool IsActivityOpen =>
        _destination == NativeDestination.Activity && _activity is not null;

    internal string ActivityTitle => _activity is null ? string.Empty : Text(_activity.TitleKey);

    internal SwiftView RenderActivity(SourceUiStyle style) =>
        _activity?.Render(style) ?? Views.VStack();

    private async Task OpenPlanCardAsync(string cardId)
    {
        var card = _dashboardState.Cards.IsDefaultOrEmpty ? null
            : _dashboardState.Cards.FirstOrDefault(item => item.Id == cardId);
        if (card?.Activity == PlanActivityType.VocabularyReview)
        {
            await OpenAssignedQuizAsync(cardId);
            return;
        }
        if (_services is null || _settingsOpen || card is null
            || !NativeActivityCatalog.IsImplemented(card.Activity))
            return;
        var launch = await _services.Provider
            .GetRequiredService<NativeActivityLaunchService>()
            .FromPlanCardAsync(cardId, _lifetime.Token);
        if (launch is null)
        {
            _dashboardState = _dashboardState with {
                Phase = NativePhase.Error, ErrorKey = NativeMatchingText.Unavailable };
            return;
        }
        await StartActivityAsync(launch);
    }

    private async Task OpenChooseOwnActivityAsync(string route)
    {
        if (_dashboard is null || _services is null || _settingsOpen
            || _dashboardState.Mode != NativeDashboardMode.ChooseOwn
            || !NativeActivityCatalog.IsImplemented(route))
            return;
        // Same re-validation against owned resources/skills as the quiz tile.
        var selection = await _dashboard.SelectChooseOwnQuizAsync(_lifetime.Token);
        _dashboardState = _dashboard.State;
        if (selection is null)
            return;
        var launch = await _services.Provider
            .GetRequiredService<NativeActivityLaunchService>()
            .FromChooseOwnAsync(selection, route, _lifetime.Token);
        if (launch is null)
        {
            _dashboardState = _dashboardState with {
                ChooseOwn = _dashboardState.ChooseOwn is { } choice
                    ? choice with { ErrorKey = "NativeDashboard_SelectionUnavailable" }
                    : null
            };
            return;
        }
        await StartActivityAsync(launch);
    }

    private async Task StartActivityAsync(NativeActivityLaunch launch)
    {
        if (_services is null)
            return;
        _activity?.Dispose();
        _activity = ActivityScreens.Create(launch.Route, new ActivityScreenHost(
            _services.Provider, Text, Publish, Run, ExitActivity, _logger, _lifetime.Token));
        if (_activity is null)
            return;
        _activityPopPending = false;
        _destination = NativeDestination.Activity;
        Publish();
        await _activity.OpenAsync(launch, _lifetime.Token);
    }

    internal void ExitActivity() => Run(ExitActivityAsync);

    private async Task ExitActivityAsync()
    {
        if (_activity is not { } screen)
            return;
        _logger?.LogInformation("Native iOS activity {Title} exit requested.", screen.TitleKey);
        await screen.ExitAsync(_lifetime.Token);
        _activity = null;
        await ReturnToDashboardAsync();
        screen.Dispose();
    }

    /// <summary>The NavigationStack popped the activity (system Back or swipe).</summary>
    internal void OnActivityPopped()
    {
        if (!IsActivityOpen || _activityPopPending)
            return;
        _activityPopPending = true;
        if (!_busy)
            HandlePoppedActivity();
    }

    private void HandlePoppedActivity()
    {
        if (!_activityPopPending || !IsActivityOpen)
            return;
        Run(async () =>
        {
            try
            {
                await ExitActivityAsync();
            }
            finally
            {
                _activityPopPending = false;
            }
        });
    }

    private void ReleaseActivity()
    {
        _activity?.Dispose();
        _activity = null;
        _activityPopPending = false;
    }
}
