using MauiReactor;
using MauiReactor.Shapes;
using SentenceStudio.Resources.Styles;
using System.Collections.ObjectModel;

namespace SentenceStudio.Pages.Scene;

partial class ImageGalleryPopup : PopupHost
{
    private CommunityToolkit.Maui.Views.Popup? _popup;

    [Prop]
    DescribeAScenePageState _state;

    [Prop]
    Action<bool> _onClose;

    public override VisualNode Render()
    {
        return new PopupHost(r => _popup = r)
        {
            Grid("Auto,*,Auto", "",
                RenderHeader(),
                RenderGallery(),
                Button("Close")
                    .OnClicked(() => _onClose?.Invoke(false))
                    .GridRow(2)
            )
                .Padding((Double)Application.Current.Resources["size240"])
                .RowSpacing((Double)Application.Current.Resources["size120"])
                .Margin((Double)Application.Current.Resources["size240"]),
        };
    }

    private VisualNode RenderHeader()
    {
        return Grid(
            Label("Choose an image")
                .Style((Style)Application.Current.Resources["Title1"])
                .HStart(),

            HStack(spacing: (Double)Application.Current.Resources["size60"],
                Button()
                    .ImageSource(SegoeFluentIcons.ImageExport.ToFontImageSource())
                    .Background(Colors.Transparent)
                    .TextColor(Colors.Black)
                    .Padding(0)
                    .Margin(0)
                    .VCenter()
                    .IsVisible(!_state.IsDeleteVisible),

                Button()
                    .ImageSource(SegoeFluentIcons.CheckboxCompositeReversed.ToFontImageSource())
                    .Background(Colors.Transparent)
                    .TextColor(Colors.Black)
                    .Padding(0)
                    .Margin(0)
                    .VCenter(),

                Button()
                    .ImageSource(SegoeFluentIcons.Delete.ToFontImageSource())
                    .Background(Colors.Transparent)
                    .TextColor(Colors.Black)
                    .Padding(0)
                    .Margin(0)
                    .VCenter()
                    .IsVisible(_state.IsDeleteVisible)
            )
            .HEnd()
        );
    }

    private VisualNode RenderGallery()
    {
        return CollectionView()
            .ItemsSource(_state.Images, RenderGalleryItem)
            .SelectionMode(_state.SelectionMode)
            .SelectedItems(_state.SelectedImages as IList<object>)
            .ItemsLayout(
                new HorizontalGridItemsLayout(4)
                    .VerticalItemSpacing(ApplicationTheme.Size240)
                    .HorizontalItemSpacing(ApplicationTheme.Size240)
            )
            .GridRow(1);
    }

    private VisualNode RenderGalleryItem(SceneImage image)
    {
        return Grid(
            Image()
                .Source(image.Url)
                .Aspect(Aspect.AspectFill)
                .HeightRequest(100)
                .OnTapped(() => OnImageSelected(image)),

            Image()
                .Source(SegoeFluentIcons.Checkbox.ToFontImageSource())
                .VEnd()
                .HEnd()
                .IsVisible(_state.IsSelecting)
                .Margin(4),

            Image()
                .Source(SegoeFluentIcons.CheckboxCompositeReversed.ToFontImageSource())
                .VEnd()
                .HEnd()
                .IsVisible(image.IsSelected)
                .Margin(4)
        );
    }

    private void OnImageSelected(SceneImage image)
    {
        // This would need to be handled by the parent component through a callback
        // Similar to how the original handles it through the ViewModel
    }
} 