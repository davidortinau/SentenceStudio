namespace SentenceStudio.Pages.Lesson;

public class LessonStartPage : ContentPage
{
	public LessonStartPage(LessonStartPageModel model)
	{
		BindingContext = model;
		Build();
	}

	public void Build()
	{
		Content = new VerticalStackLayout
		{
			Padding = (double)Application.Current.Resources["size160"],
			Spacing = (double)Application.Current.Resources["size240"],
			Children =
			{
				new Label
					{
						Text = "Let's Learn!",
						Style = (Style)Application.Current.Resources["Title1"]
					}
					.Start(),
				new Label
					{
						Text = "Choose from the following options, and let's get started!"
					}
					.Start(),
				new FormField
					{
						ControlTemplate = (ControlTemplate)Application.Current.Resources["FormFieldTemplate"],
						FieldLabel = "Vocabulary",
						Content = new Picker()
							.Bind(Picker.ItemsSourceProperty, nameof(LessonStartPageModel.VocabLists))
							.Bind(Picker.SelectedItemProperty, nameof(LessonStartPageModel.VocabList))
					},
				new FormField
					{
						ControlTemplate = (ControlTemplate)Application.Current.Resources["FormFieldTemplate"],
						FieldLabel = "Activity",
						Content = new Picker
							{
								ItemsSource = new string[]
								{
									"Clozure",
									"Translate",
									"Warmup",
									"Write"
								}
							}
							.Bind(Picker.SelectedItemProperty, nameof(LessonStartPageModel.SelectedLesson), BindingMode.TwoWay)
					},
				new Button
					{
						HorizontalOptions = DeviceInfo.Platform == DevicePlatform.WinUI ? LayoutOptions.Start : LayoutOptions.Fill,
						WidthRequest = DeviceInfo.Platform == DevicePlatform.WinUI ? 300 : -1,
						Text = "Start Lesson",
					}
					.BindCommand(nameof(LessonStartPageModel.StartLessonCommand))
			}
		};
	}


}