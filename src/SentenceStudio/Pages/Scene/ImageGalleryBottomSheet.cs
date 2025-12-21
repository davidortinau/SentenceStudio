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
        ).RowSpacing(MyTheme.Size120)
        .Margin(MyTheme.Size240);
    }

    VisualNode RenderHeader() =>
        Grid(
            Label("Choose an image")
                .ThemeKey(MyTheme.Title1)
                .HStart(),

            HStack(spacing: MyTheme.Size60,
                Button()
                    .ImageSource(MyTheme.IconImageExport)
                    .Background(Colors.Transparent)
                    .TextColor(MyTheme.DarkOnLightBackground)
                    .Padding(0)
                    .Margin(0)
                    .VCenter()
                    .IsVisible(!_state.IsDeleteVisible),

                Button()
                    .ImageSource(MyTheme.IconCheckbox)
                    .Background(Colors.Transparent)
                    .TextColor(MyTheme.DarkOnLightBackground)
                    .Padding(0)
                    .Margin(0)
                    .VCenter(),

                Button()
                    .ImageSource(MyTheme.IconDelete)
                    .Background(Colors.Transparent)
                    .TextColor(MyTheme.DarkOnLightBackground)
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
                .VerticalItemSpacing(MyTheme.Size240)
                .HorizontalItemSpacing(MyTheme.Size240))
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
                .Source(MyTheme.IconCheckbox)
                .VEnd()
                .HEnd()
                .IsVisible(_state.IsSelecting)
                .Margin(4),

            Image()
                .Source(MyTheme.IconCheckboxSelected)
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