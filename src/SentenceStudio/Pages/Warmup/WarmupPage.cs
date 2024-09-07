using System.Collections.Specialized;
using System.ComponentModel;

#if IOS
using UIKit;
using Foundation;
#endif

namespace SentenceStudio.Pages.Lesson;

public class WarmupPage : ContentPage
{
    WarmupPageModel _model;
    private ScrollView MessageCollectionView;
    private Grid InputUI;

    public WarmupPage(WarmupPageModel model)
    {
        BindingContext = _model = model;
        _model.Chunks.CollectionChanged += ChunksCollectionChanged;

#if IOS
		NSNotificationCenter.DefaultCenter.AddObserver(UIKeyboard.WillShowNotification, KeyboardWillShow);
		NSNotificationCenter.DefaultCenter.AddObserver(UIKeyboard.WillHideNotification, KeyboardWillHide);
#endif

        Build();
    }

    public void Build()
    {
        this.ToolbarItems.Add(new ToolbarItem
        {
            Text = "New",
            Priority = 0
        }.BindCommand(nameof(WarmupPageModel.NewConversationCommand)));
        
        Resources.Add("InvertedBoolConverter", new InvertedBoolConverter());

        Resources.Add("MessageFromOthers", new DataTemplate(() =>
        {
            return new Grid {
                Children = {
                    new Border{
                        Margin = new Thickness(15, 5),
                        Padding = new Thickness(12, 4, 12, 8),
                        HorizontalOptions = LayoutOptions.Start,
                        Background = (Color)Application.Current.Resources["Primary"],
                        StrokeThickness = 1,
                        Stroke = (Color)Application.Current.Resources["Primary"],
                        StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(10, 10, 2, 10) },
                        Content = new SelectableLabel{
                                Style = (Style)Application.Current.Resources["Body1"],
                                TextColor = Colors.White
                            }
                            .Bind(Label.TextProperty, "Text")
                    }
                }
            };
            
        }));

        Resources.Add("MessageFromOtherTyping", new DataTemplate(() =>
        {
            return new Grid{
                Children = {
                    new Border{
                        Margin = new Thickness(15, 5),
                        Padding = new Thickness(12, 4, 12, 8),
                        Background = (Color)Application.Current.Resources["Primary"],
                        StrokeThickness = 1,
                        Stroke = (Color)Application.Current.Resources["Primary"],
                        StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(10, 10, 2, 10) },
                        Content = new Label{
                            Text = "User is typing...",
                            Style = (Style)Application.Current.Resources["Body1"],
                            TextColor = Colors.White
                        }
                    }.Start()
                }
            };
            
        }));

        Resources.Add("MessageFromMe", new DataTemplate(() =>
        {
            return new Grid { 
                IsClippedToBounds = false,
                Children = {
                    new Border{
                            Margin = new Thickness(15, 5),
                            Padding = new Thickness(12, 4, 12, 8),
                            HorizontalOptions = LayoutOptions.End,
                            Background = (Color)Application.Current.Resources["Secondary"],
                            StrokeThickness = 1,
                            Stroke = (Color)Application.Current.Resources["Secondary"],
                            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(10, 0, 10, 2) },
                            Content = new SelectableLabel{
                                Style = (Style)Application.Current.Resources["Body1"],
                                TextColor = Colors.White
                            }.Bind(Label.TextProperty, "Text")
                    },
                    new Border
                        {
                            BackgroundColor = (Color)Application.Current.Resources["Gray200"],
                            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(8, 8, 0, 0) },
                            StrokeThickness = 0,
                            Margin = new Thickness(0, 2, 15, 0),
                            Padding = new Thickness(6, 0, 6, 2),
                            Content = new Label
                                {
                                    TextColor = (Color)Application.Current.Resources["Gray900"],
                                    FontSize = 10
                                }
                                .Center()
                                .Bind(Label.TextProperty, "Comprehension")
                        }
                        .End().Top()
            
                }// Children
             }.BindTapGesture(
                commandPath: nameof(WarmupPageModel.ShowExplanationCommand),
                commandSource: _model,
                parameterPath: ".");
        }));

        Resources.Add("PaddingMessageTop", new DataTemplate(() =>
        {
            return new ContentView { HeightRequest = 130 };
        }));

        Resources.Add("PaddingMessageBottom", new DataTemplate(() =>
        {
            return new ContentView { HeightRequest = 70 };
        }));

        var messageTemplateSelector = new MessageTemplateSelector
        {
            MessageFromMe = (DataTemplate)Resources["MessageFromMe"],
            MessageFromOtherTyping = (DataTemplate)Resources["MessageFromOtherTyping"],
            MessageFromOthers = (DataTemplate)Resources["MessageFromOthers"],
            TopPaddingMessage = (DataTemplate)Resources["PaddingMessageTop"],
            BottomPaddingMessage = (DataTemplate)Resources["PaddingMessageBottom"]
        };

        Resources.Add("MessageTemplateSelector", messageTemplateSelector);

        Content = new Grid {
            RowDefinitions = Rows.Define(Star,Auto),
            Children = {
                new ScrollView {
                    Content = new VerticalStackLayout { 
                        Spacing = 15 
                    }
                    .Bind(BindableLayout.ItemsSourceProperty, nameof(WarmupPageModel.Chunks))
                    .ItemTemplateSelector(messageTemplateSelector)
                    // .Bind(BindableLayout.ItemTemplateSelectorProperty,  "MessageTemplateSelector", source: this)
                }.Assign(out MessageCollectionView),
                Input()
            }
        };
        
        

    }

    Grid Input()
    {
        return new Grid
        {
            Margin = new Thickness(15),
            ColumnSpacing = 15,
            ColumnDefinitions = Columns.Define(Star, Auto),
            Children = {
                new Border
                    {
                        Background = Colors.Transparent,
                        Stroke = (Color)Application.Current.Resources["Gray300"],
                        StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(6) },
                        VerticalOptions = LayoutOptions.End,
                        Padding = new Thickness(15, 0),
                        StrokeThickness = 1,
                        Content = new Entry
                            {
                                Placeholder = "그건 한국어로 어떻게 말해요?",
                                FontSize = Device.GetNamedSize(NamedSize.Medium, typeof(Entry)),
                                VerticalOptions = LayoutOptions.End,
                                ReturnType = ReturnType.Send
                            }
                            .Bind(Entry.TextProperty, "UserInput")
                            
                            // .BindCommand(Entry.ReturnCommandProperty, nameof(WarmupPageModel.SendMessageCommand))
                    },
                    new Button
                        {
                            BackgroundColor = Colors.Transparent
                        }
                        .Column(1)
                        .Icon(SegoeFluentIcons.Add)
                        .IconSize(18)
                        .AppThemeBinding(Button.TextColorProperty, (Color)Application.Current.Resources["DarkOnLightBackground"], (Color)Application.Current.Resources["LightOnDarkBackground"])
                        .CenterVertical()
                        .BindCommand(nameof(WarmupPageModel.GetPhraseCommand))
            }
        }
        .Assign(out InputUI)
        .Row(1)
        .Bottom()
        ;
    }

    private void ConversationViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WarmupPageModel.Chunks))
        {
            if (_model.Chunks != null)
            {
                _model.Chunks.CollectionChanged += ChunksCollectionChanged;
            }
        }
    }

    private void ChunksCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            this.Dispatcher.DispatchAsync(async () =>
            {
                await Task.Delay(100); // Wait for the UI to finish updating
                await MessageCollectionView.ScrollToAsync(0, MessageCollectionView.ContentSize.Height, animated: true);
            });
        }
    }

#if IOS
	private void KeyboardWillShow(NSNotification notification)
	{
		// Handle keyboard will show event here
		InputUI.Margin = new Thickness(15, 15, 15, 40);
	}

	private void KeyboardWillHide(NSNotification notification)
	{
		// Handle keyboard will hide event here
		InputUI.Margin = new Thickness(15);
	}
#endif
}