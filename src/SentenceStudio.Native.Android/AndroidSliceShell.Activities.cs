using AndroidX.Compose;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Native;
using SentenceStudio.Services.Progress;

namespace SentenceStudio.Native.Android;

/// <summary>
/// Registered learning activities (NativeActivityCatalog) launched from Today's
/// Plan cards and Choose My Own tiles. Each activity adds its route to
/// <see cref="RenderActivity"/> and <see cref="OpenActivityPresenterAsync"/>.
/// Back always stops and saves the timer and returns to a reloaded Dashboard.
/// </summary>
internal sealed partial class AndroidSliceShell
{
    private string? _activityRoute;

    private void OpenPlanCard(string planItemId)
    {
        var card = Dashboard.Cards.IsDefaultOrEmpty ? null
            : Dashboard.Cards.FirstOrDefault(item => item.Id == planItemId);
        if (card?.Activity == PlanActivityType.VocabularyReview)
            OpenQuiz(planItemId);
        else if (card is not null && NativeActivityCatalog.IsImplemented(card.Activity))
            Execute(async cancellationToken =>
            {
                if (_scope is null)
                    return;
                var launch = await _scope.ServiceProvider
                    .GetRequiredService<NativeActivityLaunchService>()
                    .FromPlanCardAsync(planItemId, cancellationToken);
                if (launch is null)
                {
                    ShowError(Localize(NativeMatchingText.Unavailable));
                    return;
                }
                await StartActivityAsync(launch, cancellationToken);
            });
    }

    private void OpenChooseOwnActivity(string route) => Execute(async cancellationToken =>
    {
        if (_dashboard is null || _scope is null
            || Dashboard.Mode != NativeDashboardMode.ChooseOwn
            || !NativeActivityCatalog.IsImplemented(route))
            return;
        // Same owned-selection re-validation as the quiz tile.
        var selection = await _dashboard.SelectChooseOwnQuizAsync(cancellationToken);
        if (selection is null)
            return;
        var launch = await _scope.ServiceProvider
            .GetRequiredService<NativeActivityLaunchService>()
            .FromChooseOwnAsync(selection, route, cancellationToken);
        if (launch is null)
        {
            ShowError(Localize("NativeDashboard_SelectionUnavailable"));
            return;
        }
        await StartActivityAsync(launch, cancellationToken);
    });

    private async Task StartActivityAsync(
        NativeActivityLaunch launch, CancellationToken cancellationToken)
    {
        ReleaseActivity();
        Publish(() =>
        {
            _activityRoute = launch.Route;
            Destination = NativeDestination.Activity;
        });
        await OpenActivityPresenterAsync(launch, cancellationToken);
    }

    /// <summary>One case per implemented route.</summary>
    private async Task OpenActivityPresenterAsync(
        NativeActivityLaunch launch, CancellationToken cancellationToken)
    {
        switch (launch.Route)
        {
            case NativeActivityCatalog.VocabularyMatching:
                await OpenMatchingAsync(launch, cancellationToken);
                break;
            case NativeActivityCatalog.Cloze:
                await OpenClozeAsync(launch, cancellationToken);
            case NativeActivityCatalog.Translation:
                await OpenTranslationAsync(launch, cancellationToken);
            case NativeActivityCatalog.NumberDrill:
                await OpenNumberDrillAsync(launch, cancellationToken);
            case NativeActivityCatalog.Writing:
                await OpenWritingAsync(launch, cancellationToken);
                break;
        }
    }

    private ComposableNode RenderActivity(AndroidVisualPalette palette) => _activityRoute switch
    {
        NativeActivityCatalog.VocabularyMatching => RenderMatching(palette),
        NativeActivityCatalog.Cloze => RenderCloze(palette),
        NativeActivityCatalog.Translation => RenderTranslation(palette),
        NativeActivityCatalog.NumberDrill => RenderNumberDrill(palette),
        NativeActivityCatalog.Writing => RenderWriting(palette),
        _ => Body(Localize(NativeMatchingText.Unavailable), palette),
    };

    private void ExitActivity() => Execute(async cancellationToken =>
    {
        switch (_activityRoute)
        {
            case NativeActivityCatalog.VocabularyMatching:
                await ExitMatchingAsync(cancellationToken);
                break;
            case NativeActivityCatalog.Cloze:
                await ExitClozeAsync(cancellationToken);
            case NativeActivityCatalog.Translation:
                await ExitTranslationAsync(cancellationToken);
            case NativeActivityCatalog.NumberDrill:
                await ExitNumberDrillAsync(cancellationToken);
            case NativeActivityCatalog.Writing:
                await ExitWritingAsync(cancellationToken);
                break;
        }
        ReleaseActivity();
        Publish(() => Destination = NativeDestination.Dashboard);
        if (_dashboard is not null)
            await _dashboard.LoadAsync(cancellationToken);
    });

    private void ReleaseActivity()
    {
        ReleaseMatching();
        ReleaseCloze();
        ReleaseTranslation();
        ReleaseNumberDrill();
        ReleaseWriting();
        _activityRoute = null;
    }
}
