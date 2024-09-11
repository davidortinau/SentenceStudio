using CustomLayouts;

namespace SentenceStudio.Pages.Vocabulary;

public class ListVocabularyPage : ContentPage
{
	private readonly ListVocabularyPageModel _model;
	public ListVocabularyPage(ListVocabularyPageModel model)
	{
		BindingContext = _model = model;
		Build();
	}

	public void Build()
	{
		this.Bind(Page.TitleProperty, "Localize[VocabularyList]");

		Content = new ScrollView
		{
			Content = new VerticalStackLayout
			{
				Padding = (double)Application.Current.Resources["size160"],
				Spacing = (double)Application.Current.Resources["size240"],
				Children =
				{
					new Label
						{
							Style = (Style)Application.Current.Resources["Title1"],
							IsVisible = Microsoft.Maui.Devices.DeviceInfo.Platform != DevicePlatform.WinUI
						}
						.Start()
						.Bind(Label.TextProperty, "Localize[VocabularyList]"),
					
					new HorizontalWrapLayout
						{
							Spacing = (double)Application.Current.Resources["size320"]
						}
						.Bind(BindableLayout.ItemsSourceProperty, nameof(ListVocabularyPageModel.VocabLists))
						.ItemTemplate(new DataTemplate(() =>
							new Border
							{
								StrokeShape = new Rectangle(),
								StrokeThickness = 1,
								Content = new Grid
								{
									WidthRequest = 300,
									HeightRequest = 120,
									Children =
									{
										new Label
										{
											VerticalOptions = LayoutOptions.Center,
											HorizontalOptions = LayoutOptions.Center
										}
										.FormattedText(new []
										{
										new Span().Bind(Span.TextProperty, "Name"),
										new Span().Bind(Span.TextProperty, "Words.Count", stringFormat: " ({0}) ")
										})
									}
								}.BindTapGesture(nameof(ListVocabularyPageModel.ViewListCommand), commandSource: _model, parameterPath: "ID")
							})
						),
					new Border
						{
							StrokeShape = new Rectangle(),
							StrokeThickness = 1,
							HorizontalOptions = LayoutOptions.Start,
							Content = new Grid
							{
								WidthRequest = 300,
								HeightRequest = 60,
								Children =
								{
									new Label{}
										.Bind(Label.TextProperty, "Localize[Add]")
										.Center(),
								}
							}
							.BindTapGesture(nameof(ListVocabularyPageModel.AddVocabularyCommand))
						}
				}
			}
		};
	}
}