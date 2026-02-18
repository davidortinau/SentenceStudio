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
        ).RowSpacing(12)
        .Margin(24);
    }

    VisualNode RenderHeader()
    {
        var theme = BootstrapTheme.Current;
        return Grid(
            Label("Choose an image")
                .H3()
                .HStart(),

            HStack(spacing: 6,
                Button()
                    .ImageSource(BootstrapIcons.Create(BootstrapIcons.Images, theme.GetOnBackground(), 20))
                    .Background(Colors.Transparent)
                    .TextColor(theme.GetOnBackground())
                    .Padding(0)
                    .Margin(0)
                    .VCenter()
                    .IsVisible(!_state.IsDeleteVisible),

                Button()
                    .ImageSource(BootstrapIcons.Create(BootstrapIcons.Check2Square, theme.GetOnBackground(), 20))
                    .Background(Colors.Transparent)
                    .TextColor(theme.GetOnBackground())
                    .Padding(0)
                    .Margin(0)
                    .VCenter(),

                Button()
                    .ImageSource(BootstrapIcons.Create(BootstrapIcons.Trash, theme.Danger, 20))
                    .Background(Colors.Transparent)
                    .TextColor(theme.GetOnBackground())
                    .Padding(0)
                    .Margin(0)
                    .VCenter()
                    .IsVisible(_state.IsDeleteVisible)
            )
            .HEnd()
        );
    }

    VisualNode RenderGallery() =>
        CollectionView()
            .ItemsSource(_state.Images, RenderGalleryItem)
            .SelectionMode(_state.SelectionMode)
            .SelectedItems(_state.SelectedImages.Cast<object>().ToList())
            .ItemsLayout(new VerticalGridItemsLayout(2)
                .VerticalItemSpacing(24)
                .HorizontalItemSpacing(24))
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
                .Source(BootstrapIcons.Create(BootstrapIcons.Square, BootstrapTheme.Current.GetOnBackground(), 20))
                .VEnd()
                .HEnd()
                .IsVisible(_state.IsSelecting)
                .Margin(4),

            Image()
                .Source(BootstrapIcons.Create(BootstrapIcons.CheckSquareFill, BootstrapTheme.Current.Primary, 20))
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