using MauiReactor.Shapes;
using System.Collections.ObjectModel;
using SentenceStudio.Pages.Dashboard;
using System.Timers;

namespace SentenceStudio.Pages.Clozure;

class ClozurePageState
{
	public bool IsBusy { get; set; }
	public bool IsBuffering { get; set; }
	public string UserInput { get; set; }
	public string UserGuess { get; set; }
	public string UserMode { get; set; } = InputMode.Text.ToString();
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
			Grid(rows: "*, 80", columns: "*",
				ScrollView(
					Grid(rows: "60,*,Auto", columns: "*",
						SentenceScoreboard(),
						SentenceDisplay(),
						UserInput()
					).RowSpacing(8)
				),
				NavigationFooter(),
				AutoTransitionBar(),
				LoadingOverlay()
			).RowSpacing(12)
		)
		.OnAppearing(LoadSentences);
	}

	VisualNode AutoTransitionBar() =>
		ProgressBar()
			.Progress(State.AutoTransitionProgress)
			.HeightRequest(4)
			.BackgroundColor(Colors.Transparent)
			.ProgressColor(ApplicationTheme.Primary)
			.VStart();

	VisualNode LoadingOverlay() =>
		Grid(
			Label("Thinking.....")
				.FontSize(64)
				.TextColor(Theme.IsLightTheme ? 
					ApplicationTheme.DarkOnLightBackground : 
					ApplicationTheme.LightOnDarkBackground)
				.Center()
		)
		.Background(Color.FromArgb("#80000000"))
		.GridRowSpan(2)
		.IsVisible(State.IsBusy);

	VisualNode NavigationFooter() =>
		Grid(rows: "1,*", columns: "60,1,*,1,60,1,60",
			Button("GO")
				.TextColor(Theme.IsLightTheme ? 
					ApplicationTheme.DarkOnLightBackground : 
					ApplicationTheme.LightOnDarkBackground)
				.Background(Colors.Transparent)
				.GridRow(1).GridColumn(4)
				.OnClicked(GradeMe),

			new ModeSelector()
				.SelectedMode(State.UserMode)
				.OnSelectedModeChanged(mode => SetState(s => s.UserMode = mode))
				.GridRow(1).GridColumn(2),

			ImageButton()
				.Background(Colors.Transparent)
				.Aspect(Aspect.Center)
				.Source(SegoeFluentIcons.Previous.ToImageSource())
				.GridRow(1).GridColumn(0)
				.OnClicked(PreviousSentence),

			ImageButton()
				.Background(Colors.Transparent)
				.Aspect(Aspect.Center)
				.Source(SegoeFluentIcons.Next.ToImageSource())
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

	VisualNode SentenceScoreboard() =>
		ScrollView(
			HStack(spacing: 2,
				ActivityIndicator()
					.IsRunning(State.IsBuffering)
					.IsVisible(State.IsBuffering)
					.Color(Theme.IsLightTheme ? 
						ApplicationTheme.DarkOnLightBackground : 
						ApplicationTheme.LightOnDarkBackground)
					.VCenter(),
				HStack(spacing: 4,
					State.Sentences.Select(sentence =>
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
							ApplicationTheme.Gray200)
					)					
				)
			)
			.Padding(DeviceInfo.Idiom == DeviceIdiom.Phone ? 
				new Thickness(16, 6) : 
				new Thickness(ApplicationTheme.Size240))
		)
		.Orientation(ScrollOrientation.Horizontal)
		.HorizontalScrollBarVisibility(ScrollBarVisibility.Never)
		.GridRow(0)
		.VCenter();

	VisualNode SentenceDisplay() =>
		VStack(spacing: 16,
			Label(State.CurrentSentence)
				.FontSize(DeviceInfo.Platform == DevicePlatform.WinUI ? 64 : 32),
			Label(State.RecommendedTranslation)
		)
		.Margin(30)
		.GridRow(1);

	VisualNode UserInput() =>
		Grid(rows: "*, *", columns: "*, Auto, Auto, Auto",
			State.UserMode == InputMode.MultipleChoice.ToString() ? 
				RenderMultipleChoice() : 
				RenderTextInput()
		)
		.RowSpacing(DeviceInfo.Platform == DevicePlatform.WinUI ? 0 : 5)
		.Padding(DeviceInfo.Platform == DevicePlatform.WinUI ? new Thickness(30) : new Thickness(15, 0))
		.RowSpacing(DeviceInfo.Platform == DevicePlatform.WinUI ? 0 : 5)
		.GridRow(2);

	VisualNode RenderTextInput() =>
		new SfTextInputLayout(
			Entry()
				.FontSize(32)
				.Text(State.UserInput)
				.OnTextChanged((s, e) => SetState(s => s.UserInput = e.NewTextValue))
				.ReturnType(ReturnType.Go)
				.OnCompleted(GradeMe)
		)
		.Hint("Answer")
		.GridRow(1)
		.GridColumn(0)
		.GridColumnSpan(DeviceInfo.Idiom == DeviceIdiom.Phone ? 4 : 1)
		.Margin(0,0,0,12);

	VisualNode RenderMultipleChoice() =>
		VStack(spacing: 4,
			CollectionView()
				.ItemsSource(State.GuessOptions, RenderOption)
		)
		.GridRow(0);

    VisualNode RenderOption(string option) =>
		RadioButton()
			.Content(option)
			.Value(option)
			.IsChecked(State.UserGuess == option)
			.OnCheckedChanged(() => {
				SetState(s => s.UserGuess = option);
				GradeMe();
			});

    ImageSource UserActivityToImageSource(UserActivity activity)
	{
		if (activity == null) return null;
		
		return activity.Accuracy == 100 ? 
			SegoeFluentIcons.StatusCircleCheckmark.ToImageSource(iconSize:14) : 
			SegoeFluentIcons.Cancel.ToImageSource(iconSize:14);
	}

	async Task JumpTo(Challenge challenge)
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

	async Task LoadSentences()
	{
		SetState(s => s.IsBusy = true);

		try
		{
			// Use the resource Id if available, or fallback to 0
			var resourceId = Props.Resource?.Id ?? 0;
			
			var sentences = await _clozureService.GetSentences(resourceId, 2, Props.Skill.Id);
			
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

				if (sentences.Count < 10)
				{
					SetState(s => s.IsBuffering = true);
					var moreSentences = await _clozureService.GetSentences(resourceId, 8, Props.Skill.Id);
					SetState(s =>
					{
						if (moreSentences != null && moreSentences.Any())
						{
							foreach (var sentence in moreSentences)
							{
								s.Sentences.Add(sentence);
							}
						}
						s.IsBuffering = false;
					});
				}
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
	async Task GradeMe()
	{
		var currentChallenge = State.Sentences.FirstOrDefault(s => s.IsCurrent);
		if (currentChallenge == null) return;

		var answer = State.UserMode == InputMode.MultipleChoice.ToString() ? State.UserGuess : State.UserInput;
		if (string.IsNullOrWhiteSpace(answer)) return;

		var activity = new UserActivity
		{
			Activity = SentenceStudio.Shared.Models.Activity.Clozure.ToString(),
			Input = answer,
			Accuracy = answer == currentChallenge.VocabularyWordAsUsed ? 100 : 0,
			CreatedAt = DateTime.Now,
			UpdatedAt = DateTime.Now
		};

		currentChallenge.UserActivity = activity;
		await _userActivityRepository.SaveAsync(activity);

		if (activity.Accuracy == 100)
		{
			// Fill in the blank space with the correct answer
			SetState(s => {
				s.UserInput = string.Empty;
				s.CurrentSentence = currentChallenge.SentenceText;
			});
			
			TransitionToNextSentence();
		}		
	}

	System.Timers.Timer autoNextTimer;

	async Task TransitionToNextSentence()
	{
		var currentIndex = State.Sentences.IndexOf(State.Sentences.First(s => s.IsCurrent));
		if (currentIndex >= State.Sentences.Count - 1)
		{
			NextSentence();
			return;
		}

		autoNextTimer = new System.Timers.Timer(100);
		var startedTime = DateTime.Now;

		ElapsedEventHandler handler = null;
		handler = (sender, e) =>
		{
			SetState(s => s.AutoTransitionProgress = (e.SignalTime - startedTime).TotalMilliseconds / 5000);

			if (State.AutoTransitionProgress >= 1)
			{
				autoNextTimer.Stop();
				autoNextTimer.Elapsed -= handler; // Unsubscribe from the event
				NextSentence();
				SetState(s => s.AutoTransitionProgress = 0);
			}
		};

		autoNextTimer.Elapsed += handler;
		autoNextTimer.Start();
	}

	async Task NextSentence()
	{
		autoNextTimer?.Stop();
		SetState(s => s.UserMode = InputMode.Text.ToString());

		var currentIndex = State.Sentences.IndexOf(State.Sentences.FirstOrDefault(s => s.IsCurrent));
		if (currentIndex == -1) currentIndex = State.Sentences.Count - 1; // Handle case where no sentence is current
		
		if (currentIndex >= State.Sentences.Count - 1)
		{
			var result = await Application.Current.MainPage.DisplayAlert(
				_localize["End of Sentences"].ToString(),
				_localize["Would you like to get more sentences?"].ToString(),
				_localize["Yes"].ToString(),
				_localize["No"].ToString()
			);

			if (result)
			{
				SetState(s => s.IsBuffering = true);
				try 
				{
					// Use resource Id instead of vocabulary ID
					var resourceId = Props.Resource?.Id ?? 0;
					var moreSentences = await _clozureService.GetSentences(resourceId, 8, Props.Skill.Id);
					
					if (moreSentences?.Any() == true)
					{
						SetState(s =>
						{
							foreach (var sentence in moreSentences)
							{
								s.Sentences.Add(sentence);
							}
						});
						JumpTo(State.Sentences[currentIndex + 1]);
					}
					else 
					{
						await Application.Current.MainPage.DisplayAlert(
							_localize["No More Sentences"].ToString(),
							_localize["There are no more sentences available for this resource."].ToString(),
							_localize["OK"].ToString()
						);
					}
				}
				catch (Exception ex)
				{
					Debug.WriteLine(ex.Message);
					await Application.Current.MainPage.DisplayAlert(
						_localize["Error"].ToString(),
						_localize["Unable to load more sentences."].ToString(),
						_localize["OK"].ToString()
					);
				}
				finally
				{
					SetState(s => s.IsBuffering = false);
				}
			}
			return;
		}

		var nextChallenge = State.Sentences[currentIndex + 1];
		JumpTo(nextChallenge);
	}

	async Task PreviousSentence()
	{
		autoNextTimer?.Stop();
		SetState(s => s.UserMode = InputMode.Text.ToString());

		var currentIndex = State.Sentences.IndexOf(State.Sentences.FirstOrDefault(s => s.IsCurrent));
		if (currentIndex == -1) return; // Handle case where no sentence is current
		if (currentIndex <= 0) return;

		var previousChallenge = State.Sentences[currentIndex - 1];
		JumpTo(previousChallenge);
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