using CommunityToolkit.Maui.Core;
using MauiReactor.Internals;
using CommunityToolkit.Maui.Views;

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
        InitializePopup();
        base.OnMounted();
    }

    protected override void OnPropsChanged()
    {
        InitializePopup();
        base.OnPropsChanged();
    }

    void InitializePopup()
    { 
        if (_isShown && MauiControls.Application.Current != null)
        {
            MauiControls.Application.Current?.Dispatcher.Dispatch(() =>
            {
                if (ContainerPage == null ||
                    _popup == null)
                {
                    return;
                }

                ContainerPage.ShowPopup(_popup);
            });
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
            .OnClosed(OnClosed)
            .CanBeDismissedByTappingOutsideOfPopup(true)
            .HorizontalOptions(Microsoft.Maui.Primitives.LayoutAlignment.Fill)
            .VerticalOptions(Microsoft.Maui.Primitives.LayoutAlignment.Fill)
            : null!;
    }

    void OnClosed(object? sender, PopupClosedEventArgs args)
    {
        _onCloseAction?.Invoke(args.Result);
    }
}