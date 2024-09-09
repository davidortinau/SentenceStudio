using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using Fonts;
using Microsoft.Maui.Platform;
using SentenceStudio.Models;
using SentenceStudio.Services;
using Plugin.Maui.DebugOverlay;





#if IOS
using UIKit;
using Foundation;
#endif

namespace SentenceStudio.Pages.Writing;

public class WritingPage : ContentPage
{
	WritingPageModel _model;

    ScrollView sentencesScrollView;
    Entry userInputField, iMeanToSayField;
    Grid inputUI, loadingOverlay;
    Button TranslationBtn;
    private DataTemplate desktopTemplate;
    private DataTemplate mobileTemplate;
    private AnswerTemplateSelector answerTemplateSelector;

    public WritingPage(WritingPageModel model)
	{
		BindingContext = _model = model;
		_model.Sentences.CollectionChanged += CollectionChanged;

#if IOS
		NSNotificationCenter.DefaultCenter.AddObserver(UIKeyboard.WillShowNotification, KeyboardWillShow);
		NSNotificationCenter.DefaultCenter.AddObserver(UIKeyboard.WillHideNotification, KeyboardWillHide);
#endif

		Build();
	}

    private void Build()
    {
        InitResources();

        InitToolbarItems();

        Content = new Grid
            {
                RowDefinitions = Rows.Define(Auto, Star, Auto),
                Children =
                {
                    SentencesHeader(),

                    SentencesScrollView(),

                    InputUI(),

                    LoadingOverlay()
                }
            };
    }

    private void InitToolbarItems()
    {
        ToolbarItems.Add(new ToolbarItem()
            {
                Text = "Refresh"
            }
            .BindCommand(nameof(WritingPageModel.RefreshVocabCommand))
        );
    }

    private void InitResources()
    {
        Resources.Add("DesktopTemplate", new DataTemplate(() =>
            {
                return new Grid
                {
                    ColumnDefinitions = Columns.Define(Star, Star, Star, Star),
                    Children =
                    {
                        new Label().Bind(Label.TextProperty, nameof(Sentence.Answer)).Column(0),
                        new Label().CenterHorizontal().Bind(Label.TextProperty, nameof(Sentence.Accuracy)).Column(1),
                        new Label().CenterHorizontal().Bind(Label.TextProperty, nameof(Sentence.Fluency)).Column(2),
                        new HorizontalStackLayout
                            {
                                Children =
                                {
                                    new Button
                                    {
                                        Padding = 0,
                                        Margin = 0
                                    }
                                    .AppThemeColorBinding(Button.TextColorProperty,
                                        (Color)Application.Current.Resources["LightOnDarkBackground"],
                                        (Color)Application.Current.Resources["DarkOnLightBackground"]
                                    )
                                    .AppThemeColorBinding(Button.BackgroundColorProperty,
                                        (Color)Application.Current.Resources["DarkBackground"],
                                        (Color)Application.Current.Resources["LightBackground"]
                                    )
                                    .CenterVertical()
                                    .BindCommand(nameof(WritingPageModel.UseVocabCommand), source: _model, parameterPath: nameof(Sentence.Answer))
                                    .Icon(SegoeFluentIcons.Copy).IconSize(24),

                                    new Button
                                        {
                                            BackgroundColor = Colors.Transparent,
                                            TextColor = Colors.Black,
                                            Padding = 0,
                                            Margin = 0,
                                            VerticalOptions = LayoutOptions.Center
                                        }
                                        .BindCommand(nameof(WritingPageModel.ShowExplanationCommand), source: _model, parameterPath: ".")
                                        .Icon(SegoeFluentIcons.Info).IconSize(24),
                                }
                            }
                            .CenterHorizontal()
                            .Column(3)
                    }
                };
            })
        );

        Resources.Add("MobileTemplate", new DataTemplate(() =>
            {
                var sw = new SwipeView();

                return new SwipeView
                {
                    LeftItems = new SwipeItems
                    {
                        {
                            new SwipeItemView
                                {
                                    Content = new Grid
                                    {
                                        WidthRequest = 60,
                                        BackgroundColor = Colors.Red,
                                        Children =
                                        {
                                            new Label()
                                                .Center()
                                                .Icon(SegoeFluentIcons.Copy).IconSize(24),
                                        }
                                    }
                                }
                                .BindCommand(nameof(WritingPageModel.UseVocabCommand), source:_model, parameterPath: nameof(Sentence.Answer))
                        }
                    },
                    RightItems = new SwipeItems
                    {
                        {
                            new SwipeItemView
                                {
                                    Content = new Grid
                                    {
                                        WidthRequest = 60,
                                        BackgroundColor = Colors.Orange,
                                        Children =
                                        {
                                            new Label()
                                                .Center()
                                                .Icon(SegoeFluentIcons.Info).IconSize(24),
                                        }
                                    }
                                }
                                .BindCommand(nameof(WritingPageModel.ShowExplanationCommand), source:_model, parameterPath: ".")
                        }
                    },
                    Content = new Grid
                        {
                            ColumnDefinitions = Columns.Define(Star, Star),
                            RowDefinitions = Rows.Define(Auto),
                            Children =
                            {
                                new Label()
                                    .CenterVertical()
                                    .Bind(Label.TextProperty, nameof(Sentence.Answer))
                                    .Column(0),

                                new Label()
                                    .Center()
                                    .Bind(Label.TextProperty, nameof(Sentence.Accuracy))
                                    .Column(1)
                            }
                        }
                        .AppThemeColorBinding(Grid.BackgroundColorProperty,
                            (Color)Application.Current.Resources["LightBackground"],
                            (Color)Application.Current.Resources["DarkBackground"]
                        )
                };
            })
        );

        answerTemplateSelector = new AnswerTemplateSelector
            {
                DesktopTemplate = desktopTemplate,
                MobileTemplate = mobileTemplate
            };
    }

    private Grid SentencesHeader()
    {
        return new Grid
        {
            Margin = (double)Application.Current.Resources["size160"],
            ColumnDefinitions = Columns.Define(Star, Star, Star, Star),
            Children =
            {
                new Label
                {
                    Style = (Style)Application.Current.Resources["Title3"]
                }
                .Bind(Label.TextProperty, "Localize[Sentence]")
                .Column(0),

                new Label
                {
                    Style = (Style)Application.Current.Resources["Title3"],
                    HorizontalOptions = LayoutOptions.Center
                }
                .Bind(Label.TextProperty, "Localize[Accuracy]")
                .Column(1),

                new Label
                {
                    Style = (Style)Application.Current.Resources["Title3"],
                    HorizontalOptions = LayoutOptions.Center
                }
                .Bind(Label.TextProperty, "Localize[Fluency]")
                .Column(2),

                new Label
                {
                    Style = (Style)Application.Current.Resources["Title3"],
                    HorizontalOptions = LayoutOptions.Center
                }
                .Bind(Label.TextProperty, "Localize[Actions]")
                .Column(3)
            }
        };
    }

    private ScrollView SentencesScrollView()
    {
        return new ScrollView
            {
                Content = new VerticalStackLayout
                {
                    Margin = new Thickness(16, 0),
                    Spacing = 0
                }
                .Bind(BindableLayout.ItemsSourceProperty, nameof(WritingPageModel.Sentences))
                .ItemTemplateSelector(answerTemplateSelector)
            }
            .Row(1)
            .Assign(out sentencesScrollView);
    }

    private Grid InputUI()
    {
        return new Grid
        {
            RowDefinitions = Rows.Define(Auto, Auto, Auto),
            ColumnDefinitions = Columns.Define(Star, Auto),
            RowSpacing = (double)Application.Current.Resources["size40"],
            Padding = (double)Application.Current.Resources["size160"],
            Children =
            {
                new ScrollView
                    {
                        Orientation = ScrollOrientation.Horizontal,
                        Content = new VerticalStackLayout
                        {
                            Spacing = (double)Application.Current.Resources["size40"],
                            Children =
                            {
                                new Label
                                    {
                                        Style = (Style)Application.Current.Resources["Title3"]
                                    }
                                    .Bind(Label.TextProperty, "Localize[ChooseAVocabularyWord]"),

                                new HorizontalStackLayout
                                    {
                                        Spacing = (double)Application.Current.Resources["size40"]
                                    }
                                    .Bind(BindableLayout.ItemsSourceProperty, nameof(WritingPageModel.VocabBlocks))
                                    .ItemTemplate(new DataTemplate(() =>
                                        {
                                            return new Button
                                                {
                                                    BackgroundColor = (Color)Application.Current.Resources["Gray200"],
                                                    TextColor = (Color)Application.Current.Resources["Gray900"],
                                                    FontSize = 18,
                                                    Padding = (double)Application.Current.Resources["size40"],
                                                    MinimumHeightRequest = -1,
                                                    VerticalOptions = LayoutOptions.Start
                                                }
                                                .Bind(Button.TextProperty, nameof(VocabularyWord.TargetLanguageTerm))
                                                .BindCommand(nameof(WritingPageModel.UseVocabCommand), source: _model, parameterPath: nameof(VocabularyWord.TargetLanguageTerm));
                                        }))
                            }
                        }
                    }
                    .ColumnSpan(2),

                new FormField
                    {
                        ControlTemplate = (ControlTemplate)Application.Current.Resources["FormFieldTemplate"],
                        Content = new Grid
                            {
                                ColumnDefinitions = Columns.Define(Star, Auto),
                                ColumnSpacing = 2,
                                Children =
                                {
                                    new Entry
                                        {
                                            FontSize = (DeviceInfo.Idiom == DeviceIdiom.Phone) ? 16 : 32
                                        }
                                        .Bind(Entry.ReturnTypeProperty, nameof(WritingPageModel.ShowMore), converter: new BoolToReturnTypeConverter())
                                        .Bind(Entry.ReturnCommandProperty,nameof(WritingPageModel.GradeMeCommand))
                                        .Bind(Entry.TextProperty, nameof(WritingPageModel.UserInput))
                                        .Bind(Entry.PlaceholderProperty, "Localize[UserInputPlaceholder]")
                                        .Assign(out userInputField),

                                    new Button
                                        {
                                            BackgroundColor = Colors.Transparent,
                                            Padding = 0,
                                            Margin = 0
                                        }
                                        .CenterVertical()
                                        .End()
                                        .Column(1)
                                        .Icon(SegoeFluentIcons.Dictionary).IconSize(24)
                                        .AppThemeColorBinding(Button.TextColorProperty,
                                            (Color)Application.Current.Resources["LightOnDarkBackground"],
                                            (Color)Application.Current.Resources["DarkOnLightBackground"]
                                        )
                                        .OnClicked((s,e)=>{
                                            _model.TranslateInputCommand.Execute(TranslationBtn);
                                        })
                                        .Assign(out TranslationBtn),

                                    new Button
                                        {
                                            BackgroundColor = Colors.Transparent,
                                            Padding = 0,
                                            Margin = 0
                                        }
                                        .CenterVertical()
                                        .End()
                                        .BindCommand(nameof(WritingPageModel.ClearInputCommand))
                                        .Column(0)
                                        .Icon(SegoeFluentIcons.Dictionary).IconSize(24)
                                        .AppThemeColorBinding(Button.TextColorProperty,
                                            (Color)Application.Current.Resources["LightOnDarkBackground"],
                                            (Color)Application.Current.Resources["DarkOnLightBackground"]
                                        )
                                }
                            }
                    }
                    .Bind(FormField.FieldLabelProperty, "Localize[WhatDoYouWantToSay]")
                    .Row(1)
                    .Column(0),

                new Button
                    {
                        BackgroundColor = Colors.Transparent,
                        Padding = 0,
                        Margin = 0,
                    }
                    .CenterVertical()
                    .Row(1)
                    .Column(1)
                    .End()
                    .Icon(SegoeFluentIcons.More).IconSize(24)
                    .AppThemeColorBinding(Button.TextColorProperty,
                        (Color)Application.Current.Resources["LightOnDarkBackground"],
                        (Color)Application.Current.Resources["DarkOnLightBackground"]
                    )
                    .BindCommand(nameof(WritingPageModel.ToggleMoreCommand)),

                new FormField
                    {
                        ControlTemplate = (ControlTemplate)Application.Current.Resources["FormFieldTemplate"],
                        Content = new Entry
                            {
                                Placeholder = "What I mean to say is...",
                                FontSize = (DeviceInfo.Idiom == DeviceIdiom.Phone) ? 16 : 32,
                                ReturnType = ReturnType.Go
                            }
                            .Bind(Entry.ReturnCommandProperty, nameof(WritingPageModel.GradeMeCommand))
                            .Bind(Entry.TextProperty, nameof(WritingPageModel.UserMeaning))
                            .Assign(out iMeanToSayField)
                    }
                    .Bind(FormField.IsVisibleProperty, nameof(WritingPageModel.ShowMore))
                    .Row(2)
                    .ColumnSpan(2)
            }
        }
        .Row(2)
        .Assign(out inputUI);
    }

    private Grid LoadingOverlay()
    {
        return new Grid
            {
                BackgroundColor = Color.FromArgb("#80000000"),
                Children =
                {
                    new Label
                        {
                            Text = "Thinking...",
                            FontSize = 64                                        
                        }
                        .AppThemeColorBinding(Label.TextColorProperty,
                            (Color)Application.Current.Resources["LightOnDarkBackground"],
                            (Color)Application.Current.Resources["DarkOnLightBackground"]
                        )
                        .Center()
                }
            }
            .Bind(Grid.IsVisibleProperty, nameof(WritingPageModel.IsBusy))
            .RowSpan(2)
            .Assign(out loadingOverlay);
    }

    private void CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            this.Dispatcher.DispatchAsync(async () =>
            {
                await Task.Delay(100); // Wait for the UI to finish updating
                await sentencesScrollView.ScrollToAsync(0, sentencesScrollView.ContentSize.Height, animated: true);
            });
        }
    }

#if IOS
	private void KeyboardWillShow(NSNotification notification)
	{
		// Handle keyboard will show event here
		inputUI.Margin = new Thickness(0, 0, 0, 40);
	}

	private void KeyboardWillHide(NSNotification notification)
	{
		// Handle keyboard will hide event here
		inputUI.Margin = new Thickness(0, 0, 0, 0);
	}
#endif

}