using AndroidX.Compose;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Native;
using SentenceStudio.Services.Progress;

namespace SentenceStudio.Native.Android;

/// <summary>
/// Registered learning activities (NativeActivityCatalog) launched from Today's
/// Plan cards and Choose My Own tiles. Each activity adds its route to
/// <see cref="RenderActivity"/> and <see cref="OpenActivityPresenterAsync"/>
/// and resolves its presenter from <see cref="ActivityServices"/> (one scope
/// per launch). Back always stops and saves the timer and returns to a
/// reloaded Dashboard, also while the activity is still loading.
/// </summary>
internal sealed partial class AndroidSliceShell
{
    private string? _activityRoute;
    private IServiceScope? _activityScope;

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

    /// <summary>Services for the open activity: one DI scope per launch, disposed in <see cref="ReleaseActivity"/>.</summary>
    private IServiceProvider? ActivityServices => _activityScope?.ServiceProvider;

    private Task StartActivityAsync(
        NativeActivityLaunch launch, CancellationToken cancellationToken)
    {
        ReleaseActivity();
        if (_host is null)
            return Task.CompletedTask;
        // As on iOS: the activity's transient presenter and timer live in their
        // own scope, not in the shell scope until sign-out.
        var scope = _host.CreateScope();
        _activityScope = scope;
        Publish(() =>
        {
            _activityRoute = launch.Route;
            Destination = NativeDestination.Activity;
        });
        // The presenter opens outside Execute's Busy gate, so Back (in-page and
        // system) stays enabled and stops the activity at once while it loads.
        _ = OpenActivityOutsideBusyAsync(launch, scope);
        return Task.CompletedTask;
    }

    private async Task OpenActivityOutsideBusyAsync(NativeActivityLaunch launch, IServiceScope scope)
    {
        try
        {
            await OpenActivityPresenterAsync(launch, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (!ReferenceEquals(_activityScope, scope))
        {
            // Back released this activity while it was opening.
            global::Android.Util.Log.Info("SentenceStudio.Native",
                $"A released activity's open ended with {ex.GetType().Name}.");
        }
        catch (Exception ex)
        {
            ShowFailure(ex);
        }
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
                break;
            case NativeActivityCatalog.Translation:
                await OpenTranslationAsync(launch, cancellationToken);
                break;
            case NativeActivityCatalog.NumberDrill:
                await OpenNumberDrillAsync(launch, cancellationToken);
                break;
            case NativeActivityCatalog.Writing:
                await OpenWritingAsync(launch, cancellationToken);
                break;
            case NativeActivityCatalog.Reading:
                await OpenReadingAsync(launch, cancellationToken);
                break;
            case NativeActivityCatalog.Shadowing:
                await OpenShadowingAsync(launch, cancellationToken);
                break;
            case NativeActivityCatalog.HowDoYouSay:
                await OpenHowDoYouSayAsync(launch, cancellationToken);
                break;
            case NativeActivityCatalog.WordAssociation:
                await OpenWordAssociationAsync(launch, cancellationToken);
                break;
            case NativeActivityCatalog.Conversation:
                await OpenConversationAsync(launch, cancellationToken);
                break;
            case NativeActivityCatalog.Diary:
                await OpenDiaryAsync(launch, cancellationToken);
                break;
            case NativeActivityCatalog.MinimalPairs:
                await OpenMinimalPairsAsync(launch, cancellationToken);
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
        NativeActivityCatalog.Reading => RenderReading(palette),
        NativeActivityCatalog.Shadowing => RenderShadowing(palette),
        NativeActivityCatalog.HowDoYouSay => RenderHowDoYouSay(palette),
        NativeActivityCatalog.WordAssociation => RenderWordAssociation(palette),
        NativeActivityCatalog.Conversation => RenderConversation(palette),
        NativeActivityCatalog.Diary => RenderDiary(palette),
        NativeActivityCatalog.MinimalPairs => RenderMinimalPairs(palette),
        _ => Body(Localize(NativeMatchingText.Unavailable), palette),
    };

    private void ExitActivity()
    {
        // Diary editor/viewer Back returns to the Diary list (source /diary).
        if (_activityRoute == NativeActivityCatalog.Diary && DiaryBack())
            return;
        // Minimal Pairs create/session Back returns to the pair list (source /minimal-pairs).
        if (_activityRoute == NativeActivityCatalog.MinimalPairs && MinimalPairsBack())
            return;
        ExitActivityToDashboard();
    }

    private void ExitActivityToDashboard() => Execute(async cancellationToken =>
    {
        switch (_activityRoute)
        {
            case NativeActivityCatalog.VocabularyMatching:
                await ExitMatchingAsync(cancellationToken);
                break;
            case NativeActivityCatalog.Cloze:
                await ExitClozeAsync(cancellationToken);
                break;
            case NativeActivityCatalog.Translation:
                await ExitTranslationAsync(cancellationToken);
                break;
            case NativeActivityCatalog.NumberDrill:
                await ExitNumberDrillAsync(cancellationToken);
                break;
            case NativeActivityCatalog.Writing:
                await ExitWritingAsync(cancellationToken);
                break;
            case NativeActivityCatalog.Reading:
                await ExitReadingAsync(cancellationToken);
                break;
            case NativeActivityCatalog.Shadowing:
                await ExitShadowingAsync(cancellationToken);
                break;
            case NativeActivityCatalog.HowDoYouSay:
                await ExitHowDoYouSayAsync(cancellationToken);
                break;
            case NativeActivityCatalog.WordAssociation:
                await ExitWordAssociationAsync(cancellationToken);
                break;
            case NativeActivityCatalog.Conversation:
                await ExitConversationAsync(cancellationToken);
                break;
            case NativeActivityCatalog.Diary:
                await ExitDiaryAsync(cancellationToken);
                break;
            case NativeActivityCatalog.MinimalPairs:
                await ExitMinimalPairsAsync(cancellationToken);
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
        ReleaseReading();
        ReleaseShadowing();
        ReleaseHowDoYouSay();
        ReleaseWordAssociation();
        ReleaseConversation();
        ReleaseDiary();
        ReleaseMinimalPairs();
        _activityScope?.Dispose();
        _activityScope = null;
        _activityRoute = null;
    }
}
