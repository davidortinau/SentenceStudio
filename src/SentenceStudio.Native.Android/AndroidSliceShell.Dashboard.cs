using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Native;
using SentenceStudio.Services;

namespace SentenceStudio.Native.Android;

internal sealed partial class AndroidSliceShell
{
    private bool HasDashboardOwner(string? owner)
    {
        if (string.IsNullOrWhiteSpace(owner) || _scope is null)
            return false;
        var provider = _scope.ServiceProvider;
        return provider.GetRequiredService<DeviceUserScopeProvider>()
            .TryGetUserProfileId(out var active)
            && active == owner
            && provider.GetRequiredService<IPreferencesService>()
                .Get(UserProfileRepository.ActiveProfileIdKey, string.Empty) == owner;
    }

    public void SelectDashboardMode(bool chooseOwn) => Execute(async cancellationToken =>
    {
        if (!HasDashboardOwner(Auth.UserId) || _dashboard is null)
            return;
        var mode = chooseOwn
            ? NativeDashboardMode.ChooseOwn : NativeDashboardMode.TodaysPlan;
        if (_dashboard.SetMode(mode).Phase == NativePhase.Blocked)
            return;
        if (chooseOwn)
            await _dashboard.LoadChooseOwnAsync(cancellationToken);
        else
            await _dashboard.LoadAsync(cancellationToken);
    });

    public void ToggleChooseResource(string resourceId) => Execute(_ =>
    {
        if (!HasDashboardOwner(Auth.UserId) || _dashboard is null
            || Dashboard.ChooseOwn is not { ErrorKey: null } selection)
            return Task.CompletedTask;
        var ids = selection.SelectedResourceIds.IsDefault
            ? new List<string>() : selection.SelectedResourceIds.ToList();
        if (!ids.Remove(resourceId))
            ids.Add(resourceId);
        var state = _dashboard.SelectChooseOwn(
            ids, selection.SelectedSkillId);
        if (state.ChooseOwn?.ErrorKey is { } error)
            ShowError(Localize(error));
        return Task.CompletedTask;
    });

    public void SelectChooseSkill(string? skillId) => Execute(_ =>
    {
        if (!HasDashboardOwner(Auth.UserId) || _dashboard is null
            || Dashboard.ChooseOwn is not { ErrorKey: null } selection)
            return Task.CompletedTask;
        var state = _dashboard.SelectChooseOwn(
            selection.SelectedResourceIds.IsDefault
                ? [] : selection.SelectedResourceIds, skillId);
        if (state.ChooseOwn?.ErrorKey is { } error)
            ShowError(Localize(error));
        return Task.CompletedTask;
    });

    public void ToggleActivityCatalog() => Execute(_ =>
    {
        if (HasDashboardOwner(Auth.UserId)
            && _dashboard is not null && Dashboard.ChooseOwn is { } selection)
            _dashboard.SetChooseOwnExpanded(!selection.ShowAllActivities);
        return Task.CompletedTask;
    });
}
