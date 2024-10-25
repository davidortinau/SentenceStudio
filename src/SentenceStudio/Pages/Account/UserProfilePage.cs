using SentenceStudio;

namespace SentenceStudio.Pages.Account;

public class UserProfilePage : ContentPage
{
	public UserProfilePage(UserProfilePageModel model)
	{
		BindingContext = model;

        this.Bind(ContentPage.TitleProperty,"Localize[UserProfile]");
		Build();
	}

	public void Build()
	{
        this.ToolbarItems.Add(
            new ToolbarItem{
                    Text = "Reset",
                }
                .Bind(ToolbarItem.TextProperty, "Localize[Reset]")
                .BindCommand("ResetCommand")
                // .Icon(SegoeFluentIcons.Delete).IconSize((double)Application.Current.Resources["IconSize"])
        );

		Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Spacing = (double)Application.Current.Resources["size320"],
                Margin = new Thickness(24),

                Children =
                {
                    new FormField
                        {
                            ControlTemplate = (ControlTemplate)Application.Current.Resources["FormFieldTemplate"],
                            Content = new Entry().Bind(Entry.TextProperty, "Name", BindingMode.TwoWay)
                        }
                        .Bind(FormField.FieldLabelProperty, "Localize[Name]"),

                    new FormField
                        {
                            ControlTemplate = (ControlTemplate)Application.Current.Resources["FormFieldTemplate"],
                            Content = new Entry().Bind(Entry.TextProperty, "Email", BindingMode.TwoWay)
                        }
                        .Bind(FormField.FieldLabelProperty, "Localize[Email]"),

                    new FormField
                        {
                            ControlTemplate = (ControlTemplate)Application.Current.Resources["FormFieldTemplate"],
                            Content = new Picker
                                {
                                    ItemsSource = new string[]
                                    {
                                        "English", "Spanish", "French", "German", "Italian", "Portuguese",
                                        "Chinese", "Japanese", "Korean", "Arabic", "Russian", "Other"
                                    }
                                }
                                .Bind(Picker.SelectedItemProperty, "NativeLanguage", BindingMode.TwoWay)
                        }
                        .Bind(FormField.FieldLabelProperty, "Localize[NativeLanguage]"),

                    new FormField
                        {
                            ControlTemplate = (ControlTemplate)Application.Current.Resources["FormFieldTemplate"],
                            Content = new Picker
                                {
                                    ItemsSource = new string[]
                                    {
                                        "English", "Spanish", "French", "German", "Italian", "Portuguese",
                                        "Chinese", "Japanese", "Korean", "Arabic", "Russian", "Other"
                                    }
                                }
                                .Bind(Picker.SelectedItemProperty, "TargetLanguage", BindingMode.TwoWay)
                        }
                        .Bind(FormField.FieldLabelProperty, "Localize[TargetLanguage]"),

                    new FormField
                        {
                            ControlTemplate = (ControlTemplate)Application.Current.Resources["FormFieldTemplate"],
                            Content = new Picker
                                {
                                    ItemsSource = new string[]
                                    {
                                        "English", "Korean"
                                    }
                                }
                                .Bind(Picker.SelectedItemProperty, "DisplayLanguage", BindingMode.TwoWay)
                        }
                        .Bind(FormField.FieldLabelProperty, "Localize[DisplayLanguage]"),

                    new FormField
                        {
                            ControlTemplate = (ControlTemplate)Application.Current.Resources["FormFieldTemplate"],
                            Content = new Entry{ IsPassword = true }.Bind(Entry.TextProperty, "OpenAI_APIKey", BindingMode.TwoWay)
                        }
                        .Bind(FormField.FieldLabelProperty, "Localize[OpenAI_APIKey]"),

                    new Label
                        {
                            Text = "Get an API key from OpenAI to use the AI features in Sentence Studio.",
                            TextDecorations = TextDecorations.Underline
                        }
                        .AppThemeColorBinding(Label.TextColorProperty, 
                            (Color)Application.Current.Resources["Secondary"],
                            (Color)Application.Current.Resources["SecondaryDark"])
                        .BindTapGesture(nameof(UserProfilePageModel.GoToOpenAICommand)),

                    new Button
                        {
                            HorizontalOptions = (DeviceInfo.Idiom == DeviceIdiom.Desktop) ? LayoutOptions.Start : LayoutOptions.Fill,
                            WidthRequest = (DeviceInfo.Idiom == DeviceIdiom.Desktop) ? 300 : -1
                        }
                        .Bind(Button.TextProperty, "Localize[Save]")
                        .Bind(Button.CommandProperty, "SaveCommand")
                }
            }
        };
	}
}