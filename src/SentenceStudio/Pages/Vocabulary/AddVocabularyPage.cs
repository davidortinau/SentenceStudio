using Fonts;
using SentenceStudio;

namespace SentenceStudio.Pages.Vocabulary;

public class AddVocabularyPage : ContentPage
{
    private readonly AddVocabularyPageModel _model;

    public AddVocabularyPage(AddVocabularyPageModel model)
	{
		BindingContext = _model = model;

		Build();
	}

	public void Build()
	{
		this.Bind(Page.TitleProperty, "Localize[AddVocabularyList]");

		ToolbarItems.Add(new ToolbarItem
			{
				IconImageSource = new FontImageSource()
					{
						Glyph = FluentUI.open_24_regular,
						FontFamily = FluentUI.FontFamily
					}.AppThemeColorBinding(
						FontImageSource.ColorProperty,
						(Color)Application.Current.Resources["DarkOnLightBackground"],
						(Color)Application.Current.Resources["LightOnDarkBackground"]
					)
			}
			.BindCommand(nameof(AddVocabularyPageModel.ChooseFileCommand)));

		Content = new ScrollView
		{
			Content = new VerticalStackLayout
			{
				Spacing = (double)Application.Current.Resources["size320"],
				Margin = 24,
				Children =
				{
					new FormField
					{
						ControlTemplate = (ControlTemplate)Application.Current.Resources["FormFieldTemplate"],
						FieldLabel = "List Name",
						Content = new Entry()
							.Bind(Entry.TextProperty, nameof(AddVocabularyPageModel.VocabListName))
					},
					new FormField
						{
							ControlTemplate = (ControlTemplate)Application.Current.Resources["FormFieldTemplate"],
							FieldLabel = "Vocabulary",
							Content = new Editor
								{
									MinimumHeightRequest = 400,
									MaximumHeightRequest = 600
								}
								.Bind(Editor.TextProperty, nameof(AddVocabularyPageModel.VocabList))
						}
						.Bind(FormField.FieldLabelProperty, "Localize[Vocabulary]"),
					new FormField
					{
						ControlTemplate = (ControlTemplate)Application.Current.Resources["FormFieldTemplate"],
						FieldLabel = "File Type",
						Content = new HorizontalStackLayout
							{
								Spacing = (double)Application.Current.Resources["size320"],
								Children =
								{
									new RadioButton
										{
											Content = "Comma",
											Value = "comma"
										},
									new RadioButton
										{
											Content = "Tab",
											Value = "tab"
										}
								}
							}
							.Bind(RadioButtonGroup.SelectedValueProperty, nameof(AddVocabularyPageModel.Delimiter))
					},
					new Button
						{
							HorizontalOptions = DeviceInfo.Idiom == DeviceIdiom.Desktop ? LayoutOptions.Start : LayoutOptions.Fill,
							WidthRequest = DeviceInfo.Idiom == DeviceIdiom.Desktop ? 300 : -1
						}
						.Bind(Button.TextProperty, "Localize[Save]")
						.BindCommand(nameof(AddVocabularyPageModel.SaveVocabCommand))
				}
			}
		};
	}
}