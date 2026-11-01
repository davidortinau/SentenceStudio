using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Native;

namespace SentenceStudio.Native.Android;

/// <summary>Resources tab wiring: owner-scoped coordinator lifetime, tab and back navigation.</summary>
internal sealed partial class AndroidSliceShell
{
    private AndroidResourcesCoordinator? _resources;
    private string? _resourcesOwner;
    private bool _resourceFormFromDashboard;
    private bool _dashboardStarterBusy;
    private string? _dashboardStarterErrorKey;

    private bool IsResourceFormOpen => _resources?.IsFormOpen == true;

    private bool ShowsBottomTabs =>
        AndroidRoutePolicy.UsesBottomTabs && !_preview
        && !_appearanceSettingsOpen && !IsResourceFormOpen
        && AndroidRoutePolicy.IsTopLevel(Destination)
        && HasDashboardOwner(Auth.UserId);

    private AndroidResourcesCoordinator? EnsureResources()
    {
        var owner = Auth.UserId;
        if (_preview || _scope is null || !HasDashboardOwner(owner))
            return null;
        if (_resources is not null && _resourcesOwner == owner)
            return _resources;
        ResetResources();
        var provider = _scope.ServiceProvider;
        var coordinator = new AndroidResourcesCoordinator(
            provider.GetRequiredService<NativeResourcePresenter>(),
            provider.GetRequiredService<ILoggerFactory>()
                .CreateLogger<AndroidResourcesCoordinator>(),
            Localize);
        coordinator.Changed += OnResourcesChanged;
        coordinator.FormOpenChanged += OnResourceFormOpenChanged;
        _resources = coordinator;
        _resourcesOwner = owner;
        return coordinator;
    }

    /// <summary>Owner change or sign-out drops every Resources projection and draft.</summary>
    private void ResetResources()
    {
        var resources = _resources;
        _resources = null;
        _resourcesOwner = null;
        _resourceFormFromDashboard = false;
        _resourceMenu = ResourceMenu.None;
        _dashboardStarterBusy = false;
        _dashboardStarterErrorKey = null;
        if (resources is null)
            return;
        resources.Changed -= OnResourcesChanged;
        resources.FormOpenChanged -= OnResourceFormOpenChanged;
        resources.Dispose();
    }

    // Text input re-renders synchronously on the UI thread so Compose keeps the
    // field's selection and IME composition in step with the shared draft.
    private void OnResourcesChanged()
    {
        if (global::Android.OS.Looper.MyLooper() == global::Android.OS.Looper.MainLooper)
        {
            if (!_disposed)
                ApplyPublishedState();
        }
        else
            Publish(() => { });
    }

    private void OnResourceFormOpenChanged(bool open)
    {
        _resourceMenu = ResourceMenu.None;
        if (open || !_resourceFormFromDashboard)
            return;
        _resourceFormFromDashboard = false;
        Destination = NativeDestination.Dashboard;
        // The resource may have been renamed or deleted; refresh the insight links.
        ReloadDashboardAfterResources();
    }

    public void SelectTopLevel(NativeDestination destination) => Publish(() =>
    {
        if (!AndroidRoutePolicy.IsTopLevel(destination) || Busy.Value
            || !AndroidRoutePolicy.IsTopLevel(Destination)
            || IsResourceFormOpen || _resources?.IsNavigationBlocked == true
            || Destination == destination
            || Destination == NativeDestination.TodaysPlan
                && destination == NativeDestination.Dashboard)
            return;
        _dashboardMenuOpen = false;
        _resourceMenu = ResourceMenu.None;
        if (destination == NativeDestination.Resources)
        {
            if (EnsureResources() is not { } resources)
                return;
            Destination = NativeDestination.Resources;
            resources.OnBrowserAppeared();
            return;
        }
        Destination = NativeDestination.Dashboard;
        ReloadDashboardAfterResources();
    });

    // Resource adds and deletes change the Choose My Own catalog and the plan.
    private void ReloadDashboardAfterResources()
    {
        if (_dashboard is null || !HasDashboardOwner(Auth.UserId))
            return;
        var chooseOwn = Dashboard.Mode == NativeDashboardMode.ChooseOwn;
        Execute(async cancellationToken =>
        {
            if (chooseOwn)
                await _dashboard.LoadChooseOwnAsync(cancellationToken);
            else
                await _dashboard.LoadAsync(cancellationToken);
        });
    }

    /// <summary>Source Today's Insight titles link to the resource; closing returns to the plan.</summary>
    public void OpenResourceFromDashboard(string resourceId) => Publish(() =>
    {
        if (Destination != NativeDestination.Dashboard || Busy.Value
            || string.IsNullOrWhiteSpace(resourceId)
            || Dashboard.Phase != NativePhase.Ready
            || Dashboard.Narrative?.Resources.Any(resource =>
                resource.Id == resourceId) != true
            || EnsureResources() is not { } resources
            || resources.IsFormOpen)
            return;
        _resourceFormFromDashboard = true;
        Destination = NativeDestination.Resources;
        resources.OnBrowserAppeared();
        resources.Open(resourceId);
    });

    public void OpenResourceAddFromDashboard() => Publish(() =>
    {
        if (Destination != NativeDestination.Dashboard || Busy.Value
            || EnsureResources() is not { } resources || resources.IsFormOpen)
            return;
        Destination = NativeDestination.Resources;
        resources.OnBrowserAppeared();
        resources.OpenCreate();
    });

    /// <summary>New-learner Quick Start: the shared starter path with its duplicate guard.</summary>
    public void CreateDashboardStarter() => Execute(async cancellationToken =>
    {
        if (_scope is null || _dashboard is null || !Dashboard.IsNewUser
            || _dashboardStarterBusy || Auth.UserId is not { Length: > 0 } owner
            || !HasDashboardOwner(owner))
            return;
        Publish(() =>
        {
            _dashboardStarterBusy = true;
            _dashboardStarterErrorKey = null;
        });
        string? errorKey = null;
        try
        {
            var profile = await _scope.ServiceProvider
                .GetRequiredService<UserProfileRepository>()
                .GetByIdAsync(owner, cancellationToken);
            if (profile is null)
                errorKey = "Dashboard_StarterCreateFailed";
            else if (string.IsNullOrWhiteSpace(profile.TargetLanguage))
                errorKey = "NativeResources_SetTargetLanguage";
            else
            {
                var created = await _scope.ServiceProvider
                    .GetRequiredService<NativeResourcePresenter>()
                    .CreateStarterAsync(cancellationToken);
                if (!HasDashboardOwner(owner))
                    return;
                if (created.Result is NativeResourceMutationResult.Success
                    or NativeResourceMutationResult.AlreadyPresent)
                    await _dashboard.LoadAsync(cancellationToken);
                else
                    errorKey = created.ErrorKey switch
                    {
                        "Resources_SetNativeLanguage"
                            or "NativeResources_SetTargetLanguage"
                            or "NativeResources_SavedReloadError" => created.ErrorKey,
                        _ => "Dashboard_StarterCreateFailed"
                    };
            }
        }
        finally
        {
            Publish(() =>
            {
                _dashboardStarterBusy = false;
                _dashboardStarterErrorKey = errorKey;
            });
        }
    });

    private bool HandleResourcesBack()
    {
        if (_resources is { } resources && resources.IsFormOpen)
        {
            if (resources.FormState.ShowDeleteConfirmation)
            {
                if (!resources.IsNavigationBlocked)
                    resources.CancelDelete();
                return true;
            }
            if (_resourceMenu != ResourceMenu.None)
            {
                Publish(() => _resourceMenu = ResourceMenu.None);
                return true;
            }
            resources.CloseForm();
            return true;
        }
        if (Destination != NativeDestination.Resources)
            return false;
        if (_resources?.IsNavigationBlocked == true)
            return true;
        SelectTopLevel(NativeDestination.Dashboard);
        return true;
    }
}
