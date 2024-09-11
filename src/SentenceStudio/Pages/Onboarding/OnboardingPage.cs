using SentenceStudio;

namespace SentenceStudio.Pages.Onboarding;

public class OnboardingPage : ContentPage
{
	public OnboardingPage(OnboardingPageModel model)
	{
		BindingContext = model;

		Build();

	}

    IndicatorView indicators;

	public void Build()
	{
        this.Bind(ContentPage.TitleProperty,"Localize[MyProfile]");
        Shell.SetFlyoutBehavior(this, FlyoutBehavior.Disabled);
        Shell.SetNavBarIsVisible(this, false);
             
        Content = new Grid
        {
            Padding = (double)Application.Current.Resources["size160"],
            RowDefinitions = new RowDefinitionCollection
            {
                new RowDefinition { Height = GridLength.Star },
                new RowDefinition { Height = GridLength.Auto }
            },

            Children =
            {
                new CarouselView
                {
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Never,
                    IsSwipeEnabled = false,
                    Loop = false,
                    IndicatorView = indicators,
                }
                .Bind(CarouselView.PositionProperty, "CurrentPosition")
                .Row(0)
                .ItemsSource(new List<ContentView>
                {
                    CreateContentView("Welcome to Sentence Studio!", "Strengthen your language skills with our fun and interactive sentence building activities."),
                    CreateContentViewWithEntry("What should I call you?", "Name", "Enter your name"),
                    CreateContentViewWithPicker("What is your primary language?", "NativeLanguage", new string[]
                    {
                        "English", "Spanish", "French", "German", "Italian", "Portuguese",
                        "Chinese", "Japanese", "Korean", "Arabic", "Russian", "Other"
                    }),
                    CreateContentViewWithPicker("What language are you here to practice?", "TargetLanguage", new string[]
                    {
                        "Korean", "English", "Spanish", "French", "German", "Italian",
                        "Portuguese", "Chinese", "Japanese", "Arabic", "Russian", "Other"
                    }),
                    CreateContentView("Let's begin!", "On the next screen, you will be able to choose from a variety of activities to practice your language skills. Along the way Sentence Studio will keep track of your progress and report your growth.")
                })
                .ItemTemplate(new DataTemplate(() => new ContentView().Bind(ContentView.ContentProperty, "."))),

                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitionCollection
                    {
                        new ColumnDefinition { Width = GridLength.Star }
                    },
                    RowDefinitions = new RowDefinitionCollection
                    {
                        new RowDefinition { Height = GridLength.Auto },
                        new RowDefinition { Height = GridLength.Auto }
                    },
                    RowSpacing = 20,

                    Children =
                    {
                        new Button
                        {
                            Text = "Next"
                        }
                        .Bind(Button.CommandProperty, "NextCommand")
                        .Bind(Button.IsVisibleProperty, "LastPositionReached", converter: (IValueConverter)Application.Current.Resources["InvertedBoolConverter"])
                        .Row(0),

                        new Button
                        {
                            Text = "Continue"
                        }
                        .Bind(Button.CommandProperty, "EndCommand")
                        .Bind(Button.IsVisibleProperty, "LastPositionReached")
                        .Row(0),

                        new IndicatorView
                        {
                            HorizontalOptions = LayoutOptions.Center,
                            IndicatorColor = (Color)Application.Current.Resources["Gray200"],
                            SelectedIndicatorColor = (Color)Application.Current.Resources["Primary"],
                            IndicatorSize = (DeviceInfo.Platform == DevicePlatform.iOS) ? 6 : 8
                        }
                        .Row(1)
                    }
                }
                .Row(1)
            }
        };
    }

    private ContentView CreateContentView(string title, string description)
    {
        return new ContentView
        {
            Content = new Grid
            {
                RowDefinitions = new RowDefinitionCollection
                {
                    new RowDefinition { Height = GridLength.Auto },
                    new RowDefinition { Height = GridLength.Auto }
                },
                RowSpacing = (double)Application.Current.Resources["size160"],
                Margin = (double)Application.Current.Resources["size160"],

                Children =
                {
                    new Label
                    {
                        Text = title,
                        Style = (Style)Application.Current.Resources["Title1"],
                        HorizontalOptions = LayoutOptions.Center
                    },

                    new Label
                    {
                        Text = description,
                        Style = (Style)Application.Current.Resources["Title3"],
                        HorizontalOptions = LayoutOptions.Center
                    }
                    .Row(1)
                }
            }
        };
    }

    private ContentView CreateContentViewWithEntry(string title, string bindingPath, string placeholder)
    {
        return new ContentView
        {
            Content = new Grid
            {
                RowDefinitions = new RowDefinitionCollection
                {
                    new RowDefinition { Height = GridLength.Auto },
                    new RowDefinition { Height = GridLength.Auto }
                },
                RowSpacing = (double)Application.Current.Resources["size160"],
                Margin = (double)Application.Current.Resources["size160"],

                Children =
                {
                    new Label
                    {
                        Text = title,
                        Style = (Style)Application.Current.Resources["Title1"],
                        HorizontalOptions = LayoutOptions.Center
                    },

                    new FormField
                    {
                        Content = new Entry
                        {
                            Placeholder = placeholder,
                            HorizontalOptions = LayoutOptions.Center
                        }
                        .Bind(Entry.TextProperty, bindingPath, BindingMode.TwoWay, source: new RelativeBindingSource(RelativeBindingSourceMode.FindAncestor, typeof(OnboardingPageModel)))
                    }
                    .Row(1)
                }
            }
        };
    }

    private ContentView CreateContentViewWithPicker(string title, string bindingPath, string[] items)
    {
        return new ContentView
        {
            Content = new Grid
            {
                RowDefinitions = new RowDefinitionCollection
                {
                    new RowDefinition { Height = GridLength.Auto },
                    new RowDefinition { Height = GridLength.Auto }
                },
                RowSpacing = (double)Application.Current.Resources["size160"],
                Margin = (double)Application.Current.Resources["size160"],

                Children =
                {
                    new Label
                    {
                        Text = title,
                        Style = (Style)Application.Current.Resources["Title1"],
                        HorizontalOptions = LayoutOptions.Center
                    },

                    new FormField
                    {
                        Content = new Picker
                        {
                            ItemsSource = items
                        }
                        .Bind(Picker.SelectedItemProperty, bindingPath, BindingMode.TwoWay, source: new RelativeBindingSource(RelativeBindingSourceMode.FindAncestor, typeof(OnboardingPageModel)))
                    }
                    .Row(1)
                }
            }
        };
    }
}