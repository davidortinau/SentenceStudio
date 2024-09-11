using CommunityToolkit.Maui.Markup;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using System.Collections.ObjectModel;
using Fonts;

namespace SentenceStudio.Pages.Scene;

public class DescribeAScenePage : ContentPage
{
    private const double minPageWidth = 800;
    private CollectionView SentenceList;
    private Grid MainGrid;

    private MauiIcon InfoIcon;

    public DescribeAScenePage(DescribeAScenePageModel model)
    {
        BindingContext = model;

        this.SizeChanged += UpdateLayout;

        // InfoIcon = new MauiIcon(SegoeFluentIcons.Info, 24, (Color)Application.Current.Resources["DarkOnLightBackground"]);

        Build();
    }

    public void Build()
    {
        ToolbarItems.Add(new ToolbarItem
        {
            IconImageSource = new FontImageSource()
            {
                Glyph = FluentUI.info_28_regular,
                FontFamily = FluentUI.FontFamily
            }.AppThemeColorBinding(FontImageSource.ColorProperty,(Color)Application.Current.Resources["DarkOnLightBackground"],(Color)Application.Current.Resources["LightOnDarkBackground"])
        }
        .Bind(ToolbarItem.CommandProperty, "ViewDescriptionCommand"));

        ToolbarItems.Add(new ToolbarItem
        {
            IconImageSource = new FontImageSource()
            {
                Glyph = FluentUI.table_switch_28_regular,
                FontFamily = FluentUI.FontFamily
            }.AppThemeColorBinding(FontImageSource.ColorProperty,(Color)Application.Current.Resources["DarkOnLightBackground"],(Color)Application.Current.Resources["LightOnDarkBackground"])
        }.Bind(ToolbarItem.CommandProperty, "ManageImagesCommand"));

        Content = new Grid
        {
            RowDefinitions = Rows.Define(Auto,Star,Auto,Auto),
            ColumnDefinitions = Columns.Define(Star,Star),
            Children =
            {
                new CollectionView
                {
                    Margin = (Double)Application.Current.Resources["size160"],
                    Header = new ContentView
                    {
                        Padding = (Double)Application.Current.Resources["size160"],
                        Content = new Label
                        {
                            // Style = (Style)Application.Current.Resources["Title1"]
                        }.Bind(Label.TextProperty, "Localize[ISee]")
                    }
                }
                .Bind(CollectionView.ItemsSourceProperty, nameof(DescribeAScenePageModel.Sentences))
                .ItemTemplate(new DataTemplate(()=> new VerticalStackLayout
                        {
                            Padding = (Double)Application.Current.Resources["size160"],
                            Spacing = 2,
                            Children = {
                                new Label
                                {
                                    FontSize = 18
                                }.Bind(Label.TextProperty, "Answer"),

                                new Label
                                {
                                    FontSize = 12
                                }.Bind(Label.TextProperty, "Accuracy", stringFormat: "Accuracy: {0}")
                            }
                        })
                )
                .Row(1).Column(1)
                .Assign(out SentenceList),// CollectionView

                new Grid
                {
                    Children = {
                        new Image
                        {
                            Aspect = Aspect.AspectFit,
                            HorizontalOptions = LayoutOptions.Fill,
                            VerticalOptions = LayoutOptions.Start,
                            Margin = (Double)Application.Current.Resources["size160"]
                        }.Bind(Image.SourceProperty, nameof(DescribeAScenePageModel.ImageUrl))
                        
                    }
                }.Row(1).Column(0),

                new FormField
                {
                    ControlTemplate = (ControlTemplate)Application.Current.Resources["FormFieldTemplate"],
                    Content = InputField()
                }
                .Bind(FormField.FieldLabelProperty, "Localize[WhatDoYouSee]")
                .Row(3).ColumnSpan(2).Margin((Double)Application.Current.Resources["size160"]),

                new Grid
                {
                    BackgroundColor = Color.FromArgb("#80000000"),
                    IsVisible = false,
                    Children =
                    {
                        new Label
                        {
                            Text = "Analyzing the image...",
                            FontSize = 64,
                            TextColor = (Color)Application.Current.Resources["DarkOnLightBackground"]
                        }.Center()
                    }  
                }.Bind(AbsoluteLayout.IsVisibleProperty, nameof(DescribeAScenePageModel.IsBusy))
                .RowSpan(2)
                
            }
        }.Assign(out MainGrid);
    }

    private Grid InputField()
    {
        return new Grid
        {
            ColumnDefinitions = Columns.Define(Star,Auto),
            ColumnSpacing = 2,
            Children =
            {
                new Entry
                    {
                        FontSize = 18,
                        ReturnType = ReturnType.Next
                    }
                    .Bind(Entry.TextProperty, nameof(DescribeAScenePageModel.UserInput), BindingMode.TwoWay)
                    .Bind(Entry.ReturnCommandProperty, nameof(DescribeAScenePageModel.GradeMyDescriptionCommand))
                    .Column(0),

                new Button
                    {
                        BackgroundColor = Colors.Transparent,
                        Padding = 0,
                        Margin = 0
                    }
                    .Icon(SegoeFluentIcons.Previous)
                    .IconSize(24)
                    .IconColor((Color)Application.Current.Resources["DarkOnLightBackground"])
                    .CenterVertical().End()
                    .BindCommand(nameof(DescribeAScenePageModel.TranslateInputCommand))
                    .Column(1),

                new Button
                    {
                        BackgroundColor = Colors.Transparent,
                        Padding = 0,
                        Margin = 0,
                        VerticalOptions = LayoutOptions.Center,
                        HorizontalOptions = LayoutOptions.End
                    }
                    .Icon(SegoeFluentIcons.Delete)
                    .IconSize(24)
                    .IconColor((Color)Application.Current.Resources["DarkOnLightBackground"])
                    .Column(0)
                    .BindCommand(nameof(DescribeAScenePageModel.ClearInputCommand))
            }
        };
    }


    private void UpdateLayout(object sender, EventArgs e)
    {
        double currentWidth = ((VisualElement)sender).Width + Shell.Current.FlyoutWidth; // don't get this, instead get the window size

        Debug.WriteLine($"currentWidth: {currentWidth}, " +
        $"PageWidth: {((VisualElement)sender).Width}, " +
            $"Shell.Current.FlyoutWidth: {Shell.Current.FlyoutWidth}");

        if (currentWidth < minPageWidth)
        {
            MainGrid.ColumnDefinitions = Columns.Define(Star, 0);
            MainGrid.RowDefinitions = Rows.Define(0, Stars(2), Stars(1), Auto);
            
            Grid.SetColumn(SentenceList, 0);
            Grid.SetRow(SentenceList,2);            
        }
        else
        {
            MainGrid.ColumnDefinitions = Columns.Define(Star, Star);
            MainGrid.RowDefinitions = Rows.Define(0, Star, 0, Auto);
            
            Grid.SetColumn(SentenceList, 1);
            Grid.SetRow(SentenceList,1);
        }
    }
}