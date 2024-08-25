
using Border = Microsoft.Maui.Controls.Border;

namespace SentenceStudio.Pages.Clozure;

public class ClozurePage : ContentPage
{
	ClozurePageModel _model;
	Grid InputUI;
	FormField UserInputField;
	VerticalStackLayout VocabBlocks;
	ModeSelector ModeSelector;
	Grid LoadingOverlay;

	public ClozurePage(ClozurePageModel model)
	{
		BindingContext = _model = model;

		//ModeSelector.PropertyChanged += Mode_PropertyChanged;

		var userActivityToFontImageSourceConverter = new UserActivityToFontImageSourceConverter();
		var boolToColorConverter = new BoolToObjectConverter
		{
			TrueObject = (Color)Application.Current.Resources["Secondary"],
			FalseObject = (Color)Application.Current.Resources["Gray200"]
		};

		Resources.Add("UserActivityToFontImageSourceConverter", userActivityToFontImageSourceConverter);
		Resources.Add("BoolToColorConverter", boolToColorConverter);

		Build();

		// VisualStateManager.GoToState(InputUI, );
	}

    private void ReloadUI(Type[] obj)
    {
		Debug.WriteLine("🔥 ReloadUI");
        Build();
    }

    private void Mode_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (e.PropertyName == "SelectedMode")
		{
			// Do something when SelectedMode changes
			// VisualStateManager.GoToState(InputUI, ModeSelector.SelectedMode);
		}
	}

	public void Build()
	{
		Title = "Clozures";

		Shell.SetNavBarIsVisible(this, true);

		Content = new Grid
		{
			RowDefinitions = new RowDefinitionCollection
				{
					new RowDefinition { Height = GridLength.Star },
					new RowDefinition { Height = 80 }
				},
			RowSpacing = 12,
			Children = {
					new ScrollView
						{
							Content = new Grid
							{
								RowDefinitions = Rows.Define(60,Star,Auto),
								RowSpacing = 8,
								Children = {
									SentenceDisplay().Row(1),
									UserInput().Row(2).Assign(out InputUI),
									SentenceScoreboard().Row(0).CenterVertical()
								}
							} // Grid
						}, // ScrollView
					NavigationFooter().Row(1),
					AutoTransitionBar().Row(0).Top(),
					LoadingOverlayView()
						.RowSpan(2)
						.Bind(Grid.IsVisibleProperty, nameof(ClozurePageModel.IsBusy))
						.Assign(out LoadingOverlay)
				}
		}; // Content Grid

		RadioButtonGroup.SetGroupName(VocabBlocks, "GuessOptions");
		// RadioButtonGroup.SetSelectedValue(VocabBlocks, nameof(ClozurePageModel.UserGuess));
	}

    private ProgressBar AutoTransitionBar()
    {
        return new ProgressBar
		{
			Progress = 0.5,
			HeightRequest = 4,
			BackgroundColor = Colors.Transparent,
			ProgressColor = (Color)Application.Current.Resources["Primary"]
		}.Bind(ProgressBar.ProgressProperty, nameof(ClozurePageModel.AutoTransitionProgress));
    }

    private Grid LoadingOverlayView()
	{
		return new Grid
		{
			Background = Color.FromArgb("#80000000"),
			Children = {
							new Label {
								Text = "Thinking.....",
							}
							.Font(size: 64)
							.AppThemeColorBinding(Label.TextColorProperty, light: (Color)Application.Current.Resources["DarkOnLightBackground"], dark: (Color)Application.Current.Resources["LightOnDarkBackground"])
							.Center()
						}
		};
	}

	private Grid NavigationFooter()
	{
		return new Grid
		{ // Navigation
			RowDefinitions = Rows.Define(1, Star),
			ColumnDefinitions = Columns.Define(60, 1, Star, 1, 60, 1, 60),
			Children =
			{
				new Button{ Text = "GO" }
					.AppThemeColorBinding(Button.TextColorProperty, light: (Color)Application.Current.Resources["DarkOnLightBackground"], dark: (Color)Application.Current.Resources["LightOnDarkBackground"])
					.Background(Colors.Transparent)
					.Row(1).Column(4)
					.BindCommand(nameof(ClozurePageModel.GradeMeCommand)),
				new ModeSelector{}
					.Row(1).Column(2)
					.Bind(ModeSelector.SelectedModeProperty, nameof(ClozurePageModel.UserMode))
					.Center()
					.Assign(out ModeSelector),
				new Button{}
					.Icon(SegoeFluentIcons.Previous).IconSize(24).IconColor(Colors.Black)
					.Background(Colors.Transparent)
					.Row(1).Column(0)
					.BindCommand(nameof(ClozurePageModel.PreviousSentenceCommand)),
				new Button{}
					.Icon(SegoeFluentIcons.Next).IconSize(24).IconColor(Colors.Black)
					.Background(Colors.Transparent)
					.Row(1).Column(6)
					.BindCommand(nameof(ClozurePageModel.NextSentenceCommand)),
				new BoxView{ Color = Colors.Black, HeightRequest = 1 }
					.ColumnSpan(7),
				new BoxView{ Color = Colors.Black, WidthRequest = 1 }
					.Row(1).Column(1),
				new BoxView{ Color = Colors.Black, WidthRequest = 1 }
					.Row(1).Column(3),
				new BoxView{ Color = Colors.Black, WidthRequest = 1 }
					.Row(1).Column(5)

			} // Grid.Children
		};
	}

	private ScrollView SentenceScoreboard()
	{
		return new ScrollView
		{
			Orientation = ScrollOrientation.Horizontal,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Never,
			Content = new HorizontalStackLayout
			{
				Padding = DeviceInfo.Idiom == DeviceIdiom.Phone ? new Thickness(16, 6) : new Thickness((Double)Application.Current.Resources["size240"]),
				Spacing = 2,
				Children = {
												new ActivityIndicator()
														.Bind(ActivityIndicator.IsRunningProperty, nameof(ClozurePageModel.IsBuffering))
														.Bind(ActivityIndicator.IsVisibleProperty, nameof(ClozurePageModel.IsBuffering))
														.CenterVertical()
														.AppThemeBinding(ActivityIndicator.ColorProperty, (Color)Application.Current.Resources["DarkOnLightBackground"], (Color)Application.Current.Resources["LightOnDarkBackground"])
											}
			}
										.ItemTemplate(() =>
											new Border
											{
												StrokeShape = new RoundRectangle { CornerRadius = 10 },
												StrokeThickness = 2,
												Content = new ImageButton
												{

												}.Size(18, 18).Center().Aspect(Aspect.Center)
													.Bind(ImageButton.SourceProperty, nameof(Challenge.UserActivity), converter: new UserActivityToFontImageSourceConverter())
													.BindCommand(
														path: nameof(ClozurePageModel.JumpToCommand),
														source: _model,
														parameterPath: "."
														) // ImageButton
											}
											.Size(20, 20)
											.Bind(Border.StrokeProperty, nameof(Challenge.IsCurrent), converter: new BoolToObjectConverter
											{
												TrueObject = (Color)Application.Current.Resources["Secondary"],
												FalseObject = (Color)Application.Current.Resources["Gray200"]
											}))
										.Bind(BindableLayout.ItemsSourceProperty, nameof(ClozurePageModel.Sentences))

		};
	}

	private Grid UserInput()
	{
		return new Grid
		{
			ColumnDefinitions = Columns.Define(Star, Auto, Auto, Auto),
			RowDefinitions = Rows.Define(Star, Star),
			RowSpacing = DeviceInfo.Platform == DevicePlatform.WinUI ? 0 : 5,
			Padding = DeviceInfo.Platform == DevicePlatform.WinUI ? new Thickness(30) : new Thickness(15, 0),
			Children = {
				new FormField
					{
						FieldLabel = "Answer",
						Content = new Entry
							{
								ReturnType = ReturnType.Go,
							}

							.Font(size:32)
							.Bind(Entry.TextProperty, nameof(ClozurePageModel.UserInput))
							.Bind(Entry.ReturnCommandProperty, nameof(ClozurePageModel.GradeMeCommand))
					}
					.Bind(VisualElement.IsVisibleProperty, nameof(ClozurePageModel.UserMode), convert: (string text) => (text != "MultipleChoice"))
					.Row(1).Column(0).ColumnSpan(DeviceInfo.Idiom == DeviceIdiom.Phone ? 4 : 1)
					.Margins(bottom:12)
					.Assign(out UserInputField), // FormField
				new VerticalStackLayout
					{
						Spacing = 4,
					}
					.Bind(VisualElement.IsVisibleProperty, nameof(ClozurePageModel.UserMode), convert: (string text) => (text == "MultipleChoice"))
					.ItemTemplate(()=> new RadioButton
						{
							// TODO - wrong and right colors, with the same icon usage as in the scoreboard, show correct answer when wrong
							ControlTemplate = new ControlTemplate(() =>
							{
								return new Border
								{
									StrokeShape = new RoundRectangle { CornerRadius = 4 },
									StrokeThickness = 1,
									Stroke = Colors.Black,
									WidthRequest = 180,
									Content = new Microsoft.Maui.Controls.ContentPresenter().Center(),
									Style = GuessStyle()
								}
								.AppThemeColorBinding(Border.BackgroundProperty, (Color)Application.Current.Resources["LightBackground"], (Color)Application.Current.Resources["DarkBackground"]);
							})
						}
						.OnCheckChanged((RadioButton radioButton) => {
							if(radioButton.IsChecked)
								_model.UserGuess = radioButton.Content.ToString();
							// radioButton.BackgroundColor = radioButton.IsChecked ? (Color)Application.Current.Resources["Primary"] : (Color)Application.Current.Resources["LightBackground"];
						})
						.Bind(RadioButton.ContentProperty, ".")
						.Bind(RadioButton.ValueProperty, "."))
					.Bind(BindableLayout.ItemsSourceProperty, nameof(ClozurePageModel.GuessOptions))
					.Bind(RadioButtonGroup.SelectedValueProperty, nameof(ClozurePageModel.UserGuess))
					.Row(0)
					.Assign(out VocabBlocks) // VerticalStacklayout					
			}
		};
	}

	private Style GuessStyle()
	{
		VisualStateGroupList visualStateGroupList = new() { 
			new VisualStateGroup { 
				Name = "RadioButtonStates", 
				States = { 
					new VisualState { 
						Name = RadioButton.CheckedVisualState, 
						Setters = { 
							new Setter { 
								Property = BackgroundProperty, 
								Value = (Color)Application.Current.Resources["Primary"] 
							} 
						} 
					}, 
					new VisualState { Name = RadioButton.UncheckedVisualState } 
				} 
			}
		 };

		return new(typeof(Border)) { 
			Setters = { 
				new Setter { 
					Property = VisualStateGroupsProperty, 
					Value = visualStateGroupList 
				} 
			} 
		};
	}

	private VerticalStackLayout SentenceDisplay()
	{
		return new VerticalStackLayout
		{
			Spacing = 16,
			Margin = new Thickness(30),
			Children = {
												new Label()
													.FontSize(DeviceInfo.Platform == DevicePlatform.WinUI ? 64 : 32)
													.Bind(Label.TextProperty, nameof(ClozurePageModel.CurrentSentence)),
												new Label()
													.Bind(Label.TextProperty, nameof(ClozurePageModel.RecommendedTranslation))

											}
		};
	}

	// IList<IView> BuildSentences()
	// {
	// 	var sentences = new List<IView>();

	// 	foreach (var sentence in _model.Sentences)
	// 	{
	// 		var s = new Border{
	// 			StrokeShape = new RoundRectangle{ CornerRadius = 10 },
	// 			StrokeThickness = 2,
	// 			Content = new ImageButton
	// 				{

	// 				}.Size(18,18).Center().Aspect(Aspect.Center)
	// 				.Bind(ImageButton.SourceProperty, nameof(Challenge.UserActivity), converter: new UserActivityToFontImageSourceConverter
	// 				{

	// 				})
	// 				.BindCommand(
	// 					path: nameof(ClozurePageModel.JumpToCommand), 
	// 					source: _model,
	// 					parameterPath: "."
	// 					) // ImageButton
	// 		}.Size(20,20) 
	// 		.Bind(Border.StrokeProperty, nameof(Challenge.IsCurrent), converter: new BoolToObjectConverter
	// 		{
	// 			TrueObject = (Color)Application.Current.Resources["Secondary"],
	// 			FalseObject = (Color)Application.Current.Resources["Gray200"]
	// 		});// Border

	// 		sentences.Add(s);
	// 	}

	//     return sentences;
	// }

	// IList<IView> BuildVocabBlocks()
	// {
	// 	var vocabBlocks = new List<IView>();

	// 	foreach (var option in _model.GuessOptions) // this needs to be done bindable since I may not have anything yet
	// 	{
	// 		var radioButton = new RadioButton
	// 			{
	// 				Content = option,
	// 				Value = option,
	// 				ControlTemplate = new ControlTemplate(() =>
	// 				{
	// 					return new Border
	// 						{
	// 							StrokeShape = new RoundRectangle{CornerRadius = 4},
	// 							StrokeThickness = 1,
	// 							Stroke = Colors.Black,
	// 							WidthRequest = 180,								
	// 							Content = new ContentPresenter().Center()
	// 						}.AppThemeColorBinding(Border.BackgroundProperty, (Color)Application.Current.Resources["LightBackground"], (Color)Application.Current.Resources["DarkBackground"]);
	// 				})
	// 			};

	// 		vocabBlocks.Add(radioButton);
	// 	}

	// 	return vocabBlocks;
	// }
}