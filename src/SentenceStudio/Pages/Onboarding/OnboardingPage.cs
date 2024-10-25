using SentenceStudio;

namespace SentenceStudio.Pages.Onboarding;

public class OnboardingPage : ContentPage
{
    private readonly OnboardingPageModel _model;

	public OnboardingPage(OnboardingPageModel model)
	{
		BindingContext = _model = model;

		Build();

	}

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
                    Loop = false
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
                    ApiKeyStep().Bind(ContentView.IsVisibleProperty, nameof(OnboardingPageModel.NeedsApiKey), source: _model),
                    CreateContentView("Let's begin!", "On the next screen, you will be able to choose from a variety of activities to practice your language skills. Along the way Sentence Studio will keep track of your progress and report your growth.")
                })
                .ItemTemplate(new DataTemplate(() => new ContentView().Bind(ContentView.ContentProperty, "."))),

                new Grid
                    {
                        ColumnDefinitions = Columns.Define(Star),
                        RowDefinitions = Rows.Define(Auto,Auto),
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
                RowDefinitions = Rows.Define(Auto,Auto),
                RowSpacing = (double)Application.Current.Resources["size160"],
                Margin = (double)Application.Current.Resources["size160"],

                Children =
                {
                    new Label
                        {
                            Text = title,
                            Style = (Style)Application.Current.Resources["Title1"]
                        }
                        .CenterHorizontal(),

                    new Label
                        {
                            Text = description,
                            Style = (Style)Application.Current.Resources["Title3"]
                        }
                        .CenterHorizontal()
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
                RowDefinitions = Rows.Define(Auto,Auto),
                RowSpacing = (double)Application.Current.Resources["size160"],
                Margin = (double)Application.Current.Resources["size160"],

                Children =
                {
                    new Label
                        {
                            Text = title,
                            Style = (Style)Application.Current.Resources["Title1"]
                        }
                        .CenterHorizontal(),

                    new FormField
                        {
                            Content = new Entry
                                {
                                    Placeholder = placeholder
                                }
                                .CenterHorizontal()
                                .Bind(Entry.TextProperty, bindingPath, source: _model)
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
                RowDefinitions = Rows.Define(Auto,Auto),
                RowSpacing = (double)Application.Current.Resources["size160"],
                Margin = (double)Application.Current.Resources["size160"],

                Children =
                {
                    new Label
                        {
                            Text = title,
                            Style = (Style)Application.Current.Resources["Title1"]
                        }.CenterHorizontal(),

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

    private ContentView ApiKeyStep()
    {
        return new ContentView
        {
            Content = new VerticalStackLayout
            {
                Spacing = (double)Application.Current.Resources["size160"],
                Margin = (double)Application.Current.Resources["size160"],

                Children =
                {
                    new Label
                        {
                            Text = "Sentence Studio needs an API key from OpenAI to use the AI features in Sentence Studio.",
                            Style = (Style)Application.Current.Resources["Title1"]
                        }
                        .CenterHorizontal(),

                    new FormField
                        {
                            Content = new Entry
                                {
                                    Placeholder = "Enter your OpenAI API key",
                                    IsPassword = true
                                }
                                .CenterHorizontal()
                                .Bind(Entry.TextProperty, nameof(OnboardingPageModel.OpenAI_APIKey), source: _model)
                        },
                    new Label
                        {
                            Text = "Get an API key from OpenAI.com.",
                            TextDecorations = TextDecorations.Underline
                        }
                        .AppThemeColorBinding(Label.TextColorProperty, 
                            (Color)Application.Current.Resources["Secondary"],
                            (Color)Application.Current.Resources["SecondaryDark"])
                        .BindTapGesture(nameof(OnboardingPageModel.GoToOpenAICommand)),
                }
            }
        };
    }
}