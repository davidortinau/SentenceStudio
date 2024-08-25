namespace SentenceStudio.Pages.Storyteller;


public class StorytellerPage : ContentPage
{
    private readonly StorytellerPageModel _model;

    public StorytellerPage(StorytellerPageModel model)
	{
		BindingContext = _model = model;

		Build();
	}

    public void Build()
	{
		Title = "Storyteller";//LocalizationManager.Instance["Storyteller"]

		Shell.SetNavBarIsVisible(this, true);

		ToolbarItems.Add(new ToolbarItem
		{
			Text = "Play",
			
		}.BindCommand(nameof(_model.PlayCommand)));

		Content = new Grid
		{
			Padding = DeviceInfo.Idiom == DeviceIdiom.Phone ? new Thickness(16, 6) : new Thickness((Double)Application.Current.Resources["size240"]),
			RowDefinitions = new RowDefinitionCollection
				{
					new RowDefinition { Height = GridLength.Star },
					new RowDefinition { Height = GridLength.Auto },
				},
			RowSpacing = 12,
			Children = {
					new ScrollView
						{
							Content = new VerticalStackLayout
							{
								Children = {
									new Label
                                    {
                                        FontSize = 32,
										LineBreakMode = LineBreakMode.WordWrap,
                                    }.Bind(Label.TextProperty, nameof(_model.Body)),
								}
							} // VerticalStackLayout
						}.Row(0), // ScrollView
					new VerticalStackLayout()
					{
						Spacing = 8,
					}
						.Bind(BindableLayout.ItemsSourceProperty, nameof(_model.Questions))
						.ItemTemplate(new DataTemplate(() =>
						{
							var question = new Label
							{
								FontSize = 18,
								LineBreakMode = LineBreakMode.WordWrap,
							}.Bind(Label.TextProperty, nameof(Question.Body));

							
							var answer = new Entry
							{
								Placeholder = "Answer",
								FontSize = 18,
							};

							return new VerticalStackLayout
							{
								Spacing = 4,
								Children = {
									question,
									answer,
								}
							}; // VerticalStackLayout
						})) // ItemTemplate					
						.Row(1)
				}
		}; // Content Grid

	}
}