using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using CustomLayouts;
using SentenceStudio.Models;
using SentenceStudio.Services;

namespace SentenceStudio.Pages.Translation;

public class TranslationPage : ContentPage
{
	TranslationPageModel _model;

	Grid inputUI;
	FormField userInputField;
	HorizontalWrapLayout vocabBlocks;
	Button listenButton,cancelButton;
	ModeSelector modeSelector;
	Label popOverLabel;
	Layout loadingOverlay;

	public TranslationPage(TranslationPageModel model)
	{
		BindingContext = _model = model;
		model.PropertyChanged += Model_PropertyChanged;
		// ModeSelector.PropertyChanged += Mode_PropertyChanged;
		Build();
		// VisualStateManager.GoToState(InputUI, InputMode.Text.ToString());
	}

    private void Build()
    {
        Content = CreateMainGrid();
	}

    private Grid CreateMainGrid()
	{
		return new Grid
		{
			RowDefinitions = Rows.Define(Star,80),
			Children =
			{
				CreateScrollView(),
				CreateBottomGrid().Row(1),
				CreatePopOverLabel(),
				CreateLoadingOverlay().RowSpan(2)
			}
		};
	}

	private ScrollView CreateScrollView()
	{
		return new ScrollView
			{
				Content = new Grid
				{
					RowDefinitions = Rows.Define(30,Star,Auto),
					Children =
					{
						CreateSentenceGrid().Row(1),
						CreateInputUIGrid().Row(2),
						CreateProgressStackLayout().RowSpan(2)
					}
				}
			};
	}

	private Grid CreateSentenceGrid()
	{
		return new Grid
		{
			ColumnDefinitions = (DeviceInfo.Idiom == DeviceIdiom.Phone 
				? Columns.Define(Stars(1)) 
				: Columns.Define(Stars(6),Stars(3))
			),
			Margin = 30,
			Children =
			{
				new Label
					{
						HorizontalOptions = LayoutOptions.Start,
						LineBreakMode = LineBreakMode.WordWrap,
						FontSize = (DeviceInfo.Idiom == DeviceIdiom.Phone) ? 32 : 64,
						IsVisible = false
					}
					.Bind(Label.TextProperty, nameof(TranslationPageModel.CurrentSentence))
					.AppThemeBinding(
						Label.TextColorProperty, 
						(Color)Application.Current.Resources["DarkOnLightBackground"],
						(Color)Application.Current.Resources["LightOnDarkBackground"]
					)
					.Bind(Label.IsVisibleProperty, nameof(TranslationPageModel.HasFeedback), converter: new InvertedBoolConverter()),

				new FeedbackPanel()
					.Bind(FeedbackPanel.IsVisibleProperty, nameof(TranslationPageModel.HasFeedback))
					.Bind(FeedbackPanel.FeedbackProperty, nameof(TranslationPageModel.Feedback))
					.Column(DeviceInfo.Idiom == DeviceIdiom.Phone ? 0 : 1)
			}
		};
	}

	private Grid CreateInputUIGrid()
	{
		return new Grid
		{
			ColumnDefinitions = Columns.Define(Star,Auto,Auto,Auto),
			RowDefinitions = Rows.Define(Star,Star),
			RowSpacing = 0,
			Padding = new Thickness(30),
			ColumnSpacing = 15,
			Children =
			{
				CreateUserInputField().Row(1).ColumnSpan(4),
				CreateVocabBlocks().Row(0).ColumnSpan(4),
				CreateListenButton().Row(0).Column(1),
				CreateCancelButton().Row(0).Column(2)
			}
		}
		.Assign(out inputUI);
	}

	private FormField CreateUserInputField()
	{
		return new FormField
		{
			ControlTemplate = (ControlTemplate)Application.Current.Resources["FormFieldTemplate"],
			FieldLabel = "Translation",
			Content = new Entry
				{
					Placeholder = "그건 한국어로 어떻게 말해요?",
					FontSize = 32,
					ReturnType = ReturnType.Go
				}
				.Bind(Entry.TextProperty, nameof(TranslationPageModel.UserInput))
				.Bind(Entry.ReturnCommandProperty, nameof(TranslationPageModel.GradeMeCommand))
		}
		.Assign(out userInputField);
	}

	private HorizontalWrapLayout CreateVocabBlocks()
	{
		return new HorizontalWrapLayout
		{
			Spacing = 4,
			IsVisible = false
		}
		.ItemTemplate(()=> new Button
			{
				FontSize = (DeviceInfo.Idiom == DeviceIdiom.Phone) ? 18 : 24,
				Padding = (double)Application.Current.Resources["size40"],
				BackgroundColor = (Color)Application.Current.Resources["Gray200"],
				TextColor = (Color)Application.Current.Resources["Gray900"]
			}
			.Bind(Button.TextProperty, ".")
			.BindCommand(nameof(TranslationPageModel.UseVocabCommand), source: _model, parameterPath: ".")
			// .BindTapGesture(
			// 	commandPath: nameof(TranslationPageModel.UseVocabCommand), 
			// 	commandSource: _model,
			// 	parameterPath: "."
			// )
		)
		.Bind(BindableLayout.ItemsSourceProperty, nameof(TranslationPageModel.VocabBlocks))
		.Assign(out vocabBlocks);
	}

	private Button CreateListenButton()
	{
		return new Button
			{
				BackgroundColor = Colors.Transparent,
				IsVisible = false
			}
			.BindCommand(nameof(TranslationPageModel.StartListeningCommand))
			.Icon(SegoeFluentIcons.Record2).IconSize(24)
			// .IconColor((Color)Application.Current.Resources["DarkOnLightBackground"])
			.AppThemeColorBinding(Button.TextColorProperty, 
				(Color)Application.Current.Resources["DarkOnLightBackground"], 
				(Color)Application.Current.Resources["LightOnDarkBackground"]
			)
			.Assign(out listenButton);
	}

	private Button CreateCancelButton()
	{
		return new Button
			{
				BackgroundColor = Colors.Transparent,
				IsVisible = false
			}
			.BindCommand(nameof(TranslationPageModel.StopListeningCommand))
			.Icon(SegoeFluentIcons.Stop).IconSize(24)
			.AppThemeColorBinding(Button.TextColorProperty, 
				(Color)Application.Current.Resources["DarkOnLightBackground"], 
				(Color)Application.Current.Resources["LightOnDarkBackground"]
			)
			.Assign(out cancelButton);
	}

	private HorizontalStackLayout CreateProgressStackLayout()
	{
		return new HorizontalStackLayout
			{
				Padding = new Thickness(30),
				Spacing = 8,
				HorizontalOptions = LayoutOptions.End,
				VerticalOptions = LayoutOptions.Start,
				Children =
				{
					new ActivityIndicator
						{
							VerticalOptions = LayoutOptions.Center
						}
						.Bind(ActivityIndicator.IsRunningProperty, nameof(TranslationPageModel.IsBuffering))
						.Bind(ActivityIndicator.IsVisibleProperty, nameof(TranslationPageModel.IsBuffering))
						.AppThemeBinding(
							ActivityIndicator.ColorProperty, 
							(Color)Application.Current.Resources["DarkOnLightBackground"], 
							(Color)Application.Current.Resources["LightOnDarkBackground"]
						),

					new Label {}
						.CenterVertical()
						.Bind(Label.TextProperty, "Progress")
						.AppThemeBinding(
							Label.TextColorProperty, 
							(Color)Application.Current.Resources["DarkOnLightBackground"], 
							(Color)Application.Current.Resources["LightOnDarkBackground"]
						)
				}
			};
	}

	private Grid CreateBottomGrid()
	{
		return new Grid
		{
			RowDefinitions = Rows.Define(1,Star),
			ColumnDefinitions = Columns.Define(60,1,Star,1,60,1,60),
			Children =
			{
				CreateGoButton().Row(1).Column(4),
				CreateModeSelector().Row(1).Column(2),
				CreatePreviousButton().Row(1).Column(0),
				CreatePlayButton().Row(1).Column(2),
				CreateNextButton().Row(1).Column(6),
				new BoxView
				{
					HeightRequest = 1
				}
				.AppThemeColorBinding(BoxView.ColorProperty,
					(Color)Application.Current.Resources["DarkOnLightBackground"], 
					(Color)Application.Current.Resources["LightOnDarkBackground"]
				)
				.ColumnSpan(7),

			new BoxView
				{
					WidthRequest = 1
				}
				.AppThemeColorBinding(BoxView.ColorProperty,
					(Color)Application.Current.Resources["DarkOnLightBackground"], 
					(Color)Application.Current.Resources["LightOnDarkBackground"]
				)
				.Row(1).Column(1),

			new BoxView
				{
					WidthRequest = 1
				}
				.AppThemeColorBinding(BoxView.ColorProperty,
					(Color)Application.Current.Resources["DarkOnLightBackground"], 
					(Color)Application.Current.Resources["LightOnDarkBackground"]
				)
				.Row(1).Column(3),

			new BoxView
				{
					WidthRequest = 1
				}
				.AppThemeColorBinding(BoxView.ColorProperty,
					(Color)Application.Current.Resources["DarkOnLightBackground"], 
					(Color)Application.Current.Resources["LightOnDarkBackground"]
				)
				.Row(1).Column(5)
				// CreateBottomGridLines()
			}
		};
	}

	private Button CreateGoButton()
	{
		return new Button
			{
				Text = "GO",
				BackgroundColor = Colors.Transparent
			}
			.Bind(Button.CommandProperty, "GradeMeCommand")
			.AppThemeBinding(Button.TextColorProperty,
				(Color)Application.Current.Resources["DarkOnLightBackground"],
				(Color)Application.Current.Resources["LightOnDarkBackground"]
			)
			;
	}

	

	private ModeSelector CreateModeSelector()
	{
		return new ModeSelector
			{
				HorizontalOptions = LayoutOptions.Center,
				VerticalOptions = LayoutOptions.Center
			}
			.Bind(ModeSelector.SelectedModeProperty, "UserMode")
			.Assign(out modeSelector);
	}

	private Button CreatePreviousButton()
	{
		return new Button
			{
				BackgroundColor = Colors.Transparent
			}
			.BindCommand(nameof(TranslationPageModel.PreviousSentenceCommand))
			.Icon(SegoeFluentIcons.Previous).IconSize(24)
			.AppThemeColorBinding(Button.TextColorProperty, 
				(Color)Application.Current.Resources["DarkOnLightBackground"], 
				(Color)Application.Current.Resources["LightOnDarkBackground"]
			)
			;
	}

	private Button CreatePlayButton()
	{
		return new Button
			{
				BackgroundColor = Colors.Transparent,
				HorizontalOptions = LayoutOptions.End
			}
			.BindCommand(nameof(TranslationPageModel.PlayAudioCommand))
			.Icon(SegoeFluentIcons.Play).IconSize(24)
			.AppThemeColorBinding(Button.TextColorProperty, 
				(Color)Application.Current.Resources["DarkOnLightBackground"], 
				(Color)Application.Current.Resources["LightOnDarkBackground"]
			);
	}

	private Button CreateNextButton()
	{
		return new Button
			{
				BackgroundColor = Colors.Transparent
			}
			.BindCommand(nameof(TranslationPageModel.NextSentenceCommand))
			.Icon(SegoeFluentIcons.Next).IconSize(24)
			.AppThemeColorBinding(Button.TextColorProperty, 
				(Color)Application.Current.Resources["DarkOnLightBackground"], 
				(Color)Application.Current.Resources["LightOnDarkBackground"]
			);
	}

	private Label CreatePopOverLabel()
	{
		return new Label
			{
				Padding = 8,
				LineHeight = 1,
				IsVisible = false,
				ZIndex = 10,
				FontSize = 64,
				HorizontalOptions = LayoutOptions.Start,
				VerticalOptions = LayoutOptions.Start
			}
			.AppThemeBinding(Label.BackgroundColorProperty, 
				(Color)Application.Current.Resources["DarkBackground"],
				(Color)Application.Current.Resources["LightBackground"]
			)
			.AppThemeBinding(Label.TextColorProperty, 
				(Color)Application.Current.Resources["LightOnDarkBackground"],
				(Color)Application.Current.Resources["DarkOnLightBackground"]
			)
			.Assign(out popOverLabel);
	}

	private AbsoluteLayout CreateLoadingOverlay()
	{
		return new AbsoluteLayout
			{
				BackgroundColor = Color.FromArgb("#80000000"),
				IsVisible = false
			}
			.Bind(AbsoluteLayout.IsVisibleProperty, "IsBusy")
			.Assign(out loadingOverlay);
	}		

    private async void Model_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
		if (e.PropertyName == nameof(TranslationPageModel.UserMode))
		{
			Debug.WriteLine($"UserMode changed to {_model.UserMode}");
			// VisualStateManager.GoToState(inputUI, _model.UserMode);
			if(_model.UserMode == InputMode.MultipleChoice.ToString())
			{
				vocabBlocks.IsVisible = true;
				// listenButton.IsVisible = false;
				// cancelButton.IsVisible = false;
			}else{
				userInputField.IsVisible = true;
				vocabBlocks.IsVisible = false;
				// listenButton.IsVisible = false;
				// cancelButton.IsVisible = false;
			}
		}
    }

	void PointerGestureRecognizer_PointerEntered(System.Object sender, Microsoft.Maui.Controls.PointerEventArgs e)
	{
		// Assuming sender is of type that has TargetLanguageTerm property
		if ((sender as Element).BindingContext is VocabularyWord obj)
		{
			popOverLabel.Text = obj.TargetLanguageTerm;
			popOverLabel.IsVisible = true;

			var p = e.GetPosition(Content);

			// Set the position of the label
			popOverLabel.TranslationX = p.Value.X;
			popOverLabel.TranslationY = p.Value.Y;
		}
	}
		
	void PointerGestureRecognizer_PointerExited(System.Object sender, Microsoft.Maui.Controls.PointerEventArgs e)
	{
		popOverLabel.Text = "";
		popOverLabel.IsVisible = false;
	}

	void PointerGestureRecognizer_PointerMoved(System.Object sender, Microsoft.Maui.Controls.PointerEventArgs e)
	{
		var p = e.GetPosition(Content);

		// Set the position of the label
		popOverLabel.TranslationX = p.Value.X;
		popOverLabel.TranslationY = p.Value.Y;
	}

}