using CommunityToolkit.Maui.Converters;
namespace SentenceStudio.Pages.HowDoYouSay;


public class HowDoYouSayPage : ContentPage
{
	private readonly HowDoYouSayPageModel _model;

	public HowDoYouSayPage(HowDoYouSayPageModel model)
	{
		BindingContext = _model = model;

		Build();
	}

	public void Build()
	{
		Title = "How Do You Say";//LocalizationManager.Instance["Storyteller"]

		Shell.SetNavBarIsVisible(this, true);

		Content = new Grid
		{
			Padding = DeviceInfo.Idiom == DeviceIdiom.Phone ? new Thickness(16, 6) : new Thickness((Double)Application.Current.Resources["size240"]),
			RowDefinitions = Rows.Define(Auto,Star),
			RowSpacing = 12,
			Children = {
					
							new VerticalStackLayout
							{
								Spacing = (Double)Application.Current.Resources["size240"],
								Children = {
									new ActivityIndicator{}
										.Bind(ActivityIndicator.IsVisibleProperty, nameof(_model.IsBusy))
										.Bind(ActivityIndicator.IsRunningProperty, nameof(_model.IsBusy)),
									new FormField{
										FieldLabel = "Enter a word or phrase",
										Content = new Editor
										{
											Placeholder = "Enter a word or phrase",
											FontSize = 32,
											MinimumHeightRequest = 200,
											MaximumHeightRequest = 500,
											AutoSize = EditorAutoSizeOption.TextChanges
										}.Bind(Editor.TextProperty, nameof(_model.Phrase)),
									},
									new Button
									{
										Text = "Submit"
									}.BindCommand(nameof(_model.SubmitCommand))
								}
							},
							new ScrollView
							{
								Content = 
									new VerticalStackLayout
									{
										Spacing = (Double)Application.Current.Resources["size240"],
									}
									.Bind(BindableLayout.ItemsSourceProperty, nameof(HowDoYouSayPageModel.StreamHistory))
									.ItemTemplate(() =>
									new HorizontalStackLayout{
										Spacing = (Double)Application.Current.Resources["size120"],
										Children = {
											new Button{
												Background = Colors.Transparent
											}
												.Icon(SegoeFluentIcons.Play).IconSize(24).IconColor(Colors.Black)
												.BindCommand(
													path: nameof(HowDoYouSayPageModel.PlayCommand),
													source: _model,
													parameterPath: "."
													),
											new Label
												{
													FontSize = 24
												}
												.Bind(Label.TextProperty, nameof(StreamHistory.Phrase)),

										}

									})								
							}.Row(1)// ScrollView
						
					
				}
		}; // Content Grid

	}
}