using Border = Microsoft.Maui.Controls.Border;
using MauiReactor;
using MauiReactor.Shapes;
using SentenceStudio.Models;
using System.Collections.ObjectModel;
using MauiIcons.SegoeFluent;
using System.Linq;
using System.Diagnostics;
using System.Timers;
using SentenceStudio.Pages.Dashboard;

namespace SentenceStudio.Pages.Clozure;

class ClozurePageState
{
	public bool IsBusy { get; set; }
	public bool IsBuffering { get; set; }
	public string UserInput { get; set; }
	public string UserGuess { get; set; }
	public string UserMode { get; set; } = "Text";
	public string CurrentSentence { get; set; }
	public string RecommendedTranslation { get; set; }
	public double AutoTransitionProgress { get; set; }
	public ObservableCollection<Challenge> Sentences { get; set; } = new();
	public string[] GuessOptions { get; set; }
}

partial class ClozurePage : Component<ClozurePageState, ActivityProps>
{
	[Inject] ClozureService _clozureService;
	[Inject] AiService _aiService;
	[Inject] UserActivityRepository _userActivityRepository;

	LocalizationManager _localize => LocalizationManager.Instance;

	public override VisualNode Render()
	{
		return ContentPage($"{_localize["Clozures"]}",
			Grid(rows: "*, Auto", "*",
				ScrollView(
					Grid(rows: "60,*,Auto","",
						SentenceScoreboard(),
						SentenceDisplay(),
						UserInput()
					).RowSpacing(8)
				),
				NavigationFooter(),
				AutoTransitionBar(),
				LoadingOverlay()
					
			)
		).OnAppearing(LoadSentences);
	}

	private VisualNode AutoTransitionBar() =>
		ProgressBar()
			.Progress(State.AutoTransitionProgress)
			.HeightRequest(4)
			.BackgroundColor(Colors.Transparent)
			.ProgressColor((Color)Application.Current.Resources["Primary"])
			.GridRow(0).VStart();

	private VisualNode LoadingOverlay() =>
		Grid(
			Label("Thinking.....")
				.FontSize(64)
				.TextColor(Theme.IsLightTheme ? 
					(Color)Application.Current.Resources["DarkOnLightBackground"] : 
					(Color)Application.Current.Resources["LightOnDarkBackground"])
				.Center()
		)
		.Background(Color.FromArgb("#80000000"))
		.GridRowSpan(2)
		.IsVisible(State.IsBusy);

	private VisualNode NavigationFooter() =>
		Grid("1,*", "60,1,*,1,60,1,60",
			Button("GO")
				.TextColor(Theme.IsLightTheme ? 
					(Color)Application.Current.Resources["DarkOnLightBackground"] : 
					(Color)Application.Current.Resources["LightOnDarkBackground"])
				.Background(Colors.Transparent)
				.GridRow(1).GridColumn(4)
				.OnClicked(GradeMe),

			new ModeSelector()
				.GridRow(1).GridColumn(2),
				// .SelectedMode(State.UserMode)
				// .OnSelectedModeChanged(mode => SetState(s => s.UserMode = mode))
				// .Center(),

			Button()
				.Background(Colors.Transparent)
				.ImageSource(SegoeFluentIcons.Previous.ToFontImageSource())
				.GridRow(1).GridColumn(0)
				.OnClicked(PreviousSentence),

			Button()
				.Background(Colors.Transparent)
				.ImageSource(SegoeFluentIcons.Next.ToFontImageSource())
				.GridRow(1).GridColumn(6)
				.OnClicked(NextSentence),

			BoxView()
				.Color(Colors.Black)
				.HeightRequest(1)
				.GridColumnSpan(7),

			BoxView()
				.Color(Colors.Black)
				.WidthRequest(1)
				.GridRow(1).GridColumn(1),

			BoxView()
				.Color(Colors.Black)
				.WidthRequest(1)
				.GridRow(1).GridColumn(3),

			BoxView()
				.Color(Colors.Black)
				.WidthRequest(1)
				.GridRow(1).GridColumn(5)
		).GridRow(1);

	private VisualNode SentenceScoreboard() =>
		ScrollView(
			HStack(spacing: 2,
				ActivityIndicator()
					.IsRunning(State.IsBuffering)
					.IsVisible(State.IsBuffering)
					.Color(Theme.IsLightTheme ? 
						(Color)Application.Current.Resources["DarkOnLightBackground"] : 
						(Color)Application.Current.Resources["LightOnDarkBackground"])
					.VCenter(),
				CollectionView().ItemsSource(State.Sentences, sentence =>
					Border(
						ImageButton()
							.WidthRequest(18).HeightRequest(18)
							.Center()
							.Aspect(Aspect.Center)
							.Source(UserActivityToImageSource(sentence.UserActivity))
							.OnClicked(() => JumpTo(sentence))
					)
					.WidthRequest(20).HeightRequest(20)
					.StrokeShape(new RoundRectangle().CornerRadius(10))
					.StrokeThickness(2)
					.Stroke(sentence.IsCurrent ? 
						(Color)Application.Current.Resources["Secondary"] : 
						(Color)Application.Current.Resources["Gray200"])
				)
			)
			.Padding(DeviceInfo.Idiom == DeviceIdiom.Phone ? 
				new Thickness(16, 6) : 
				new Thickness((double)Application.Current.Resources["size240"]))
		)
		.Orientation(ScrollOrientation.Horizontal)
		.HorizontalScrollBarVisibility(ScrollBarVisibility.Never)
		;

	private VisualNode SentenceDisplay() =>
		VStack(spacing: 16,
			Label(State.CurrentSentence)
				.FontSize(DeviceInfo.Platform == DevicePlatform.WinUI ? 64 : 32),
			Label(State.RecommendedTranslation)
		)
		.Margin(30);

	private VisualNode UserInput() =>
		Grid(rows: "*, *", columns: "*, Auto, Auto, Auto",
			State.UserMode == "MultipleChoice" ? 
				RenderMultipleChoice() : 
				RenderTextInput()
		)
		.RowSpacing(DeviceInfo.Platform == DevicePlatform.WinUI ? 0 : 5)
		.Padding(DeviceInfo.Platform == DevicePlatform.WinUI ? 
			new Thickness(30) : new Thickness(15, 0));

	private VisualNode RenderTextInput() =>
		Border(
			Entry()
				.Placeholder("Answer")
				.FontSize(32)
				.Text(State.UserInput)
				.OnTextChanged((s, e) => SetState(s => s.UserInput = e.NewTextValue))
				.ReturnType(ReturnType.Go)
				.OnCompleted(GradeMe)
		)
		.GridRow(1)
		.GridColumn(0)
		.GridColumnSpan(4)
		.Margin(0,0,0,12)
		.Background(Colors.Transparent)
		.Stroke((Color)Application.Current.Resources["Gray300"])
		.StrokeShape(new RoundRectangle().CornerRadius(4))
		.StrokeThickness(1);

	private VisualNode RenderMultipleChoice() =>
		VStack(spacing: 4,
			CollectionView().ItemsSource(State.GuessOptions ?? Array.Empty<string>(), (option =>
				RadioButton()
					.Content(option)
					.Value(option)
					.IsChecked(State.UserGuess == option)
					.OnCheckedChanged(() => SetState(s => s.UserGuess = option))
					// .ControlTemplate(new ControlTemplate(() =>
					// 	Border()
					// 		.StrokeShape(new RoundRectangle().CornerRadius(4))
					// 		.StrokeThickness(1)
					// 		.Stroke(Colors.Black)
					// 		.WidthRequest(180)
					// 		.Content(
					// 			ContentPresenter().Center()
					// 		)
					// 		.Background(Theme.IsLightTheme ? 
					// 			(Color)Application.Current.Resources["LightBackground"] : 
					// 			(Color)Application.Current.Resources["DarkBackground"])
					// ))
				)
			)
		)
		.GridRow(0);

	private ImageSource UserActivityToImageSource(UserActivity activity)
	{
		if (activity == null) return null;
		
		return activity.Accuracy == 100 ? 
			SegoeFluentIcons.StatusCircleCheckmark.ToFontImageSource() : 
			SegoeFluentIcons.StatusErrorCircle7.ToFontImageSource();
	}

	private async void JumpTo(Challenge challenge)
	{
		var currentIndex = State.Sentences.IndexOf(challenge);
		if (currentIndex < 0) return;

		foreach (var sentence in State.Sentences)
		{
			sentence.IsCurrent = false;
		}
		challenge.IsCurrent = true;

		SetState(s => 
		{
			s.CurrentSentence = challenge.SentenceText.Replace(challenge.VocabularyWordAsUsed, "__");
			s.RecommendedTranslation = challenge.RecommendedTranslation;
			s.GuessOptions = challenge.VocabularyWordGuesses?.Split(",").Select(x => x.Trim()).OrderBy(x => Guid.NewGuid()).ToArray();
			s.UserInput = challenge.UserActivity?.Input ?? string.Empty;
			s.UserGuess = null;
		});
	}

	private async void LoadSentences()
	{
		SetState(s => s.IsBusy = true);

		try
		{
			var sentences = await _clozureService.GetSentences(Props.Vocabulary.ID, 2, Props.Skill.ID);
			
			if (sentences.Any())
			{
				var first = sentences.First();
				first.IsCurrent = true;

				SetState(s =>
				{
					s.Sentences = new ObservableCollection<Challenge>(sentences);
					s.CurrentSentence = first.SentenceText.Replace(first.VocabularyWordAsUsed, "__");
					s.RecommendedTranslation = first.RecommendedTranslation;
					s.GuessOptions = first.VocabularyWordGuesses?.Split(",")
						.Select(x => x.Trim())
						.OrderBy(x => Guid.NewGuid())
						.ToArray();
				});
			}

			if (sentences.Count < 10)
			{
				SetState(s => s.IsBuffering = true);
				var moreSentences = await _clozureService.GetSentences(Props.Vocabulary.ID, 8, Props.Skill.ID);
				SetState(s =>
				{
					foreach (var sentence in moreSentences)
					{
						s.Sentences.Add(sentence);
					}
					s.IsBuffering = false;
				});
			}
		}
		catch (Exception ex)
		{
			Debug.WriteLine(ex.Message);
		}
		finally
		{
			SetState(s => s.IsBusy = false);
		}
	}

	private async void GradeMe()
	{
		var currentChallenge = State.Sentences.FirstOrDefault(s => s.IsCurrent);
		if (currentChallenge == null) return;

		var answer = State.UserMode == "MultipleChoice" ? State.UserGuess : State.UserInput;
		if (string.IsNullOrWhiteSpace(answer)) return;

		var activity = new UserActivity
		{
			Activity = "Clozure",
			Input = answer,
			Accuracy = answer == currentChallenge.VocabularyWordAsUsed ? 100 : 0
		};

		currentChallenge.UserActivity = activity;
		await _userActivityRepository.SaveAsync(activity);

		if (activity.Accuracy == 100)
		{
			TransitionToNextSentence();
		}
	}

	private System.Timers.Timer autoNextTimer;

	private void TransitionToNextSentence()
	{
		autoNextTimer = new System.Timers.Timer(100);
		var startedTime = DateTime.Now;
		
		autoNextTimer.Elapsed += (sender, e) =>
		{
			SetState(s => s.AutoTransitionProgress = (e.SignalTime - startedTime).TotalMilliseconds / 5000);
			
			if (State.AutoTransitionProgress >= 1)
			{
				autoNextTimer.Stop();
				NextSentence();
				SetState(s => s.AutoTransitionProgress = 0);
			}
		};
		
		autoNextTimer.Start();
	}

	private void PreviousSentence()
	{
		autoNextTimer?.Stop();
		SetState(s => s.UserMode = "Text");

		var currentIndex = State.Sentences.IndexOf(State.Sentences.First(s => s.IsCurrent));
		if (currentIndex <= 0) return;

		var previousChallenge = State.Sentences[currentIndex - 1];
		JumpTo(previousChallenge);
	}

	private void NextSentence()
	{
		autoNextTimer?.Stop();
		SetState(s => s.UserMode = "Text");

		var currentIndex = State.Sentences.IndexOf(State.Sentences.First(s => s.IsCurrent));
		if (currentIndex >= State.Sentences.Count - 1)
		{
			// Optional: Load more sentences or show completion message
			return;
		}

		var nextChallenge = State.Sentences[currentIndex + 1];
		JumpTo(nextChallenge);
	}

	protected override void OnMounted()
	{
		base.OnMounted();
		LoadSentences();
	}

	protected override void OnWillUnmount()
	{
		autoNextTimer?.Dispose();
		base.OnWillUnmount();
	}
}