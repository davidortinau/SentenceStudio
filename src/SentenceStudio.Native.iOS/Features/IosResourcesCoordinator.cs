using CoreFoundation;
using Foundation;
using Microsoft.Extensions.Logging;
using SentenceStudio.Native.iOS.Platform;
using UIKit;

namespace SentenceStudio.Native.iOS.Features;

internal sealed class IosResourcesCoordinator : IDisposable
{
    private readonly NativeResourcePresenter _presenter;
    private readonly ILogger<IosResourcesCoordinator> _logger;
    private readonly Func<string, string> _text;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly NativeResourceSyncAction _syncAction = new();
    private readonly NativeResourceListViewController _list;
    private readonly NavigationObserver _observer;
    private NativeResourceDetailViewController? _detail;
    private NativeResourceEditViewController? _editor;
    private string? _selectedId;
    private bool _detailReloadDeferred;
    private bool _loadingList;
    private bool _disposed;

    internal UINavigationController Navigation { get; }

    internal IosResourcesCoordinator(
        NativeResourcePresenter presenter,
        ILogger<IosResourcesCoordinator> logger,
        Func<string, string> text)
    {
        _presenter = presenter;
        _logger = logger;
        _text = text;
        _list = new NativeResourceListViewController(
            text,
            onAppear: () => Run(LoadListAsync),
            search: value => OnMain(() => _list.Show(_presenter.Search(value))),
            filter: (type, language) =>
                OnMain(() => _list.Show(_presenter.Filter(type, language))),
            open: id => Run(() => OpenAsync(id)),
            synchronize: () => Run(SynchronizeAsync));
        Navigation = new UINavigationController(_list);
        Navigation.NavigationBar.PrefersLargeTitles = false;
        _observer = new NavigationObserver(OnNavigationDidShow);
        Navigation.Delegate = _observer;
    }

    private async Task LoadListAsync()
    {
        if (_loadingList)
            return;
        _loadingList = true;
        try
        {
            var load = _presenter.LoadListAsync(_lifetime.Token);
            OnMain(() => _list.Show(_presenter.List));
            await load;
            OnMain(() => _list.Show(_presenter.List));
        }
        finally
        {
            _loadingList = false;
        }
    }

    private async Task OpenAsync(string id)
    {
        if (_disposed || _detail is not null)
            return;
        _selectedId = id;
        var detail = new NativeResourceDetailViewController(
            _text,
            edit: BeginEdit,
            synchronize: () => Run(SynchronizeAsync),
            retry: () => Run(() => ReloadDetailAsync(id)));
        detail.HidesBottomBarWhenPushed = true;
        _detail = detail;
        Navigation.PushViewController(detail, true);
        var load = _presenter.OpenAsync(id, _lifetime.Token);
        detail.Show(_presenter.Detail);
        await load;
        OnMain(() =>
        {
            if (ReferenceEquals(_detail, detail))
                detail.Show(_presenter.Detail);
        });
    }

    private async Task ReloadDetailAsync(string id)
    {
        if (_disposed || _selectedId != id || _detail is not { } detail)
            return;
        var load = _presenter.OpenAsync(id, _lifetime.Token);
        OnMain(() =>
        {
            if (ReferenceEquals(_detail, detail))
                detail.Show(_presenter.Detail);
        });
        await load;
        OnMain(() =>
        {
            if (ReferenceEquals(_detail, detail))
                detail.Show(_presenter.Detail);
        });
    }

    private void BeginEdit()
    {
        if (_disposed || _editor is not null || !_presenter.BeginEdit())
        {
            _detail?.Show(_presenter.Detail);
            return;
        }
        var editor = new NativeResourceEditViewController(
            _text,
            changed: (field, value) => _presenter.ChangeDraft(field, value),
            cancel: CancelEdit,
            save: () => Run(SaveAsync),
            reload: () => Run(ReloadDraftAsync));
        editor.HidesBottomBarWhenPushed = true;
        _editor = editor;
        editor.SetDraft(_presenter.Detail);
        Navigation.PushViewController(editor, true);
    }

    private void CancelEdit()
    {
        if (_editor is null || _presenter.Detail.Phase == NativeResourceDetailPhase.Saving
            || _presenter.Detail.Reloading)
            return;
        if (_presenter.Detail.Phase is NativeResourceDetailPhase.Conflict
            or NativeResourceDetailPhase.NotFound or NativeResourceDetailPhase.ReadOnly)
            _detailReloadDeferred = true;
        _presenter.CancelEdit();
        Navigation.PopViewController(true);
        _detail?.Show(_presenter.Detail);
    }

    private async Task SaveAsync()
    {
        if (_editor is not { } editor
            || _presenter.Detail is not { Draft: { } draft,
                Phase: NativeResourceDetailPhase.Ready or NativeResourceDetailPhase.SaveError })
            return;
        editor.CommitComposingText(draft);
        var save = _presenter.SaveAsync(_lifetime.Token);
        editor.Show(_presenter.Detail);
        if (Navigation.InteractivePopGestureRecognizer is { } gesture)
            gesture.Enabled = false;
        try
        {
            var saved = await save;
            OnMain(() =>
            {
                if (!ReferenceEquals(_editor, editor))
                    return;
                if (saved)
                {
                    _detailReloadDeferred = false;
                    _list.Show(_presenter.List);
                    _detail?.Show(_presenter.Detail);
                    Navigation.PopViewController(true);
                }
                else
                    editor.Show(_presenter.Detail);
            });
        }
        finally
        {
            OnMain(() =>
            {
                if (Navigation.InteractivePopGestureRecognizer is { } gesture)
                    gesture.Enabled = true;
            });
        }
    }

    private async Task ReloadDraftAsync()
    {
        if (_editor is not { } editor)
            return;
        var reload = _presenter.ReloadDraftAsync(_lifetime.Token);
        editor.Show(_presenter.Detail);
        if (Navigation.InteractivePopGestureRecognizer is { } gesture)
            gesture.Enabled = false;
        try
        {
            var loaded = await reload;
            OnMain(() =>
            {
                if (!ReferenceEquals(_editor, editor))
                    return;
                if (loaded && _presenter.Detail.Draft is not null)
                {
                    _detailReloadDeferred = false;
                    editor.SetDraft(_presenter.Detail);
                }
                else if (loaded)
                {
                    _detailReloadDeferred = false;
                    _detail?.Show(_presenter.Detail);
                    Navigation.PopViewController(true);
                }
                else
                    editor.Show(_presenter.Detail);
            });
        }
        finally
        {
            OnMain(() =>
            {
                if (Navigation.InteractivePopGestureRecognizer is { } gesture)
                    gesture.Enabled = true;
            });
        }
    }

    private Task SynchronizeAsync() => _disposed
        ? Task.CompletedTask
        : _syncAction.RunAsync(SynchronizeAndRefreshAsync);

    private async Task SynchronizeAndRefreshAsync()
    {
        var synchronization = _presenter.SynchronizeAsync(_lifetime.Token);
        await synchronization;
        if (!await _presenter.RefreshAfterSyncAsync(synchronization, _lifetime.Token))
            return;
        if (_selectedId is { } id && _detail is not null)
        {
            if (!_presenter.IsLatestSynchronization(synchronization))
                return;
            if (_editor is not null)
                _detailReloadDeferred = true;
            else
                await _presenter.OpenAsync(id, _lifetime.Token);
        }
        OnMain(() =>
        {
            if (!_presenter.IsLatestSynchronization(synchronization))
                return;
            _list.Show(_presenter.List);
            if (_editor is null)
                _detail?.Show(_presenter.Detail);
            else
                _editor.Show(_presenter.Detail);
        });
    }

    private void OnNavigationDidShow(UIViewController shown)
    {
        if (_editor is { } editor && !Navigation.ViewControllers.Contains(editor))
        {
            if (_presenter.Detail.Phase is NativeResourceDetailPhase.Conflict
                or NativeResourceDetailPhase.NotFound or NativeResourceDetailPhase.ReadOnly)
                _detailReloadDeferred = true;
            _presenter.CancelEdit();
            _editor = null;
            editor.Dispose();
            if (_detailReloadDeferred && _selectedId is { } id && _detail is not null)
            {
                _detailReloadDeferred = false;
                Run(() => ReloadDetailAsync(id));
            }
            else
                _detail?.Show(_presenter.Detail);
        }
        if (_detail is { } detail && !Navigation.ViewControllers.Contains(detail))
        {
            _selectedId = null;
            _detailReloadDeferred = false;
            _detail = null;
            _presenter.InvalidateDetail();
            detail.Dispose();
        }
    }

    private async void Run(Func<Task> action)
    {
        if (_disposed)
            return;
        try
        {
            await action();
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Native iOS Resources action failed.");
            OnMain(() => _list.ShowFailure(_text("NativeResources_LoadError")));
        }
    }

    private void OnMain(Action action)
    {
        if (_disposed)
            return;
        if (NSThread.IsMain)
            action();
        else
            DispatchQueue.MainQueue.DispatchAsync(() =>
            {
                if (!_disposed)
                    action();
            });
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _presenter.InvalidateSync();
        _presenter.InvalidateDetail();
        _lifetime.Cancel();
        Navigation.WeakDelegate = null;
        _observer.Dispose();
        _editor?.Dispose();
        _detail?.Dispose();
        _list.Dispose();
        Navigation.Dispose();
        _lifetime.Dispose();
    }

    private sealed class NavigationObserver(Action<UIViewController> shown)
        : UINavigationControllerDelegate
    {
        public override void DidShowViewController(
            UINavigationController navigationController,
            UIViewController viewController, bool animated) => shown(viewController);
    }
}
