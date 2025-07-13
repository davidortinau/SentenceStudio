using MauiReactor.Shapes;
using VerticalGridItemsLayout = MauiReactor.VerticalGridItemsLayout;

namespace SentenceStudio.Pages.Scene;

partial class ImageGalleryBottomSheet : Component
{
    [Prop]
    DescribeAScenePageState _state;

    public override VisualNode Render()
    {
        return Grid("Auto,*","",
            RenderHeader(),
            RenderGallery()
        ).RowSpacing(ApplicationTheme.Size120)
        .Margin(ApplicationTheme.Size240);
    }

    VisualNode RenderHeader() =>
        Grid(
            Label("Choose an image")
                .ThemeKey(ApplicationTheme.Title1)
                .HStart(),

            HStack(spacing: ApplicationTheme.Size60,
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

    VisualNode RenderGallery() =>
        CollectionView()
            .ItemsSource(_state.Images, RenderGalleryItem)
            .SelectionMode(_state.SelectionMode)
            .SelectedItems(_state.SelectedImages.Cast<object>().ToList())
            .ItemsLayout(new VerticalGridItemsLayout(2)
                .VerticalItemSpacing(ApplicationTheme.Size240)
                .HorizontalItemSpacing(ApplicationTheme.Size240))
            .GridRow(1);

    VisualNode RenderGalleryItem(SceneImage image) =>
        Grid(
            Image()
                .Source(new Uri(image.Url))
                .Aspect(Aspect.AspectFit)
                .HeightRequest(100)
                // .OnTapped(() => OnImageSelected(image))
                ,

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

    // void OnImageSelected(SceneImage image) =>
    //      would need to be handled by the parent component through a callback
    //     // Similar to how the original handles it through the ViewModel
    // }
} 