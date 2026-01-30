using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Extensions;
using MauiReactor.Internals;

namespace SentenceStudio.Pages.Controls;

[Scaffold(typeof(CommunityToolkit.Maui.Views.Popup))]
partial class Popup
{
    protected override void OnAddChild(VisualNode widget, MauiControls.BindableObject childNativeControl)
    {
        if (childNativeControl is MauiControls.View content)
        {
            Validate.EnsureNotNull(NativeControl);
            NativeControl.Content = content;
        }

        base.OnAddChild(widget, childNativeControl);
    }

    protected override void OnRemoveChild(VisualNode widget, MauiControls.BindableObject childNativeControl)
    {
        Validate.EnsureNotNull(NativeControl);

        if (childNativeControl is MauiControls.View content &&
            NativeControl.Content == content)
        {
            NativeControl.Content = null;
        }
        base.OnRemoveChild(widget, childNativeControl);
    }
}

class PopupHost : Component
{
    private CommunityToolkit.Maui.Views.Popup? _popup;
    private bool _isShown;
    private bool _wasShown;
    private Action<object?>? _onCloseAction;
    private readonly Action<CommunityToolkit.Maui.Views.Popup?>? _nativePopupCreateAction;

    public PopupHost(Action<CommunityToolkit.Maui.Views.Popup?>? nativePopupCreateAction = null)
    {
        _nativePopupCreateAction = nativePopupCreateAction;
    }

    public PopupHost IsShown(bool isShown)
    {
        _isShown = isShown;
        return this;
    }

    public PopupHost OnClosed(Action<object?> action)
    {
        _onCloseAction = action;
        return this;
    }

    protected override void OnMounted()
    {
        _wasShown = false;
        InitializePopup();
        base.OnMounted();
    }

    protected override void OnPropsChanged()
    {
        InitializePopup();
        base.OnPropsChanged();
    }

    protected override void OnWillUnmount()
    {
        // Clean up popup reference when component unmounts
        _popup = null;
        base.OnWillUnmount();
    }

    void InitializePopup()
    {
        if (_isShown && !_wasShown && MauiControls.Application.Current != null)
        {
            _wasShown = true;
            MauiControls.Application.Current?.Dispatcher.Dispatch(() =>
            {
                if (ContainerPage == null ||
                    _popup == null)
                {
                    return;
                }

                try
                {
                    ContainerPage.ShowPopup(_popup,
                        new PopupOptions
                        {
                            CanBeDismissedByTappingOutsideOfPopup = true
                        });
                }
                catch (InvalidOperationException)
                {
                    // Popup might already be shown or in invalid state - ignore
                }
            });
        }
        else if (!_isShown && _wasShown)
        {
            _wasShown = false;
            // Don't try to close here - let the OnClosed handler manage state
        }
    }

    public override VisualNode Render()
    {
        var children = Children();
        return _isShown ?
            new Popup(r =>
            {
                _popup = r;
                _nativePopupCreateAction?.Invoke(r);
            })
            {
                children[0]
            }
            .OnClosed(args =>
            {
                _wasShown = false;
                _onCloseAction?.Invoke(args);
                _popup = null;
            })
            .HFill()
            .VFill()
            : null!;
    }
}