using MauiReactor.Shapes;
using System.Collections.ObjectModel;
using SentenceStudio.Pages.Dashboard;
using System.Timers;
using System.Diagnostics;
using SentenceStudio.Shared.Models;

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
	public string FeedbackMessage { get; set; }
	public string FeedbackType { get; set; } // "success", "info", "hint", "achievement"
	public bool ShowFeedback { get; set; }
	public float? UserConfidence { get; set; }

	// Tile-based multiple choice state
	public string SelectedOption { get; set; }
	public bool HasBeenGraded { get; set; }
	public string CorrectAnswer { get; set; }

	// Session wrapup state
	public bool ShowSessionSummary { get; set; }
	public int SessionCorrectCount { get; set; }
	public int SessionTotalCount { get; set; }
}

partial class ClozurePage : Component<ClozurePageState, ActivityProps>
{
	[Inject] ClozureService _clozureService;
	[Inject] AiService _aiService;
	[Inject] UserActivityRepository _userActivityRepository;
	[Inject] VocabularyProgressService _progressService;

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
				LoadingOverlay(),
				SessionSummaryOverlay()
			).RowSpacing(12)
		)
		.Set(MauiControls.PlatformConfiguration.iOSSpecific.Page.UseSafeAreaProperty, false)
		.OnAppearing(LoadSentences);
	}

	VisualNode AutoTransitionBar() =>
		ProgressBar()
			.Progress(State.AutoTransitionProgress)
			.HeightRequest(4)
			.BackgroundColor(Colors.Transparent)
			.ProgressColor(MyTheme.HighlightDarkest)
			.VStart();

	VisualNode LoadingOverlay() =>
		Grid(
			Label("Thinking.....")
				.FontSize(64)
				.TextColor(Theme.IsLightTheme ?
					MyTheme.DarkOnLightBackground :
					MyTheme.LightOnDarkBackground)
				.Center()
		)
		.Background(Color.FromArgb("#80000000"))
		.GridRowSpan(2)
		.IsVisible(State.IsBusy);

	VisualNode SessionSummaryOverlay() =>
		Grid(
			ScrollView(
				VStack(spacing: MyTheme.LayoutSpacing,
					// Header
					Label("📚 Session Complete!")
						.FontSize(24)
						.FontAttributes(FontAttributes.Bold)
						.TextColor(MyTheme.HighlightDarkest)
						.Center(),

					Label("Great work! Here's your progress:")
						.FontSize(16)
						.Center()
						.TextColor(Theme.IsLightTheme ?
							MyTheme.DarkOnLightBackground :
							MyTheme.LightOnDarkBackground),

					// Session stats
					Border(
						VStack(spacing: 12,
							Label("📊 Session Results")
								.FontSize(18)
								.FontAttributes(FontAttributes.Bold)
								.Center()
								.TextColor(MyTheme.HighlightDarkest),

							HStack(spacing: 30,
								VStack(spacing: 4,
									Label($"{State.SessionCorrectCount}")
										.FontSize(32)
										.FontAttributes(FontAttributes.Bold)
										.TextColor(MyTheme.Success)
										.Center(),
									Label("Correct")
										.FontSize(14)
										.Center()
								),
								VStack(spacing: 4,
									Label($"{State.SessionTotalCount - State.SessionCorrectCount}")
										.FontSize(32)
										.FontAttributes(FontAttributes.Bold)
										.TextColor(MyTheme.Error)
										.Center(),
									Label("Incorrect")
										.FontSize(14)
										.Center()
								),
								VStack(spacing: 4,
									Label($"{(State.SessionTotalCount > 0 ? (int)((float)State.SessionCorrectCount / State.SessionTotalCount * 100) : 0)}%")
										.FontSize(32)
										.FontAttributes(FontAttributes.Bold)
										.TextColor(MyTheme.HighlightDarkest)
										.Center(),
									Label("Accuracy")
										.FontSize(14)
										.Center()
								)
							).Center()
						)
						.Padding(16)
					)
					.Background(Theme.IsLightTheme ?
						MyTheme.LightSecondaryBackground :
						MyTheme.DarkSecondaryBackground)
					.StrokeShape(new RoundRectangle().CornerRadius(8))
					.Margin(0, 16),

					// Sentences review
					Label($"You practiced {State.SessionTotalCount} sentence{(State.SessionTotalCount == 1 ? "" : "s")}")
						.FontSize(14)
						.Center()
						.TextColor(Theme.IsLightTheme ?
							MyTheme.DarkOnLightBackground :
							MyTheme.LightOnDarkBackground)
						.Margin(0, 8),

					// Continue button
					Button("Continue Practice")
						.OnClicked(ContinueAfterSummary)
						.Background(MyTheme.HighlightDarkest)
						.TextColor(Colors.White)
						.CornerRadius(8)
						.Padding(20, 12)
						.Margin(0, 16)
				)
				.Padding(MyTheme.LayoutPadding)
			)
		)
		.Background(Theme.IsLightTheme ?
			MyTheme.LightBackground :
			MyTheme.DarkBackground)
		.GridRowSpan(2)
		.IsVisible(State.ShowSessionSummary);

	VisualNode NavigationFooter() =>
		Grid(rows: "1,*", columns: "60,1,*,1,60,1,60",
			Button("GO")
				.TextColor(Theme.IsLightTheme ?
					MyTheme.DarkOnLightBackground :
					MyTheme.LightOnDarkBackground)
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
				.Source(MyTheme.IconPrevious)
				.GridRow(1).GridColumn(0)
				.OnClicked(PreviousSentence),

			ImageButton()
				.Background(Colors.Transparent)
				.Aspect(Aspect.Center)
				.Source(MyTheme.IconNext)
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
						MyTheme.DarkOnLightBackground :
						MyTheme.LightOnDarkBackground)
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
						.WidthRequest(40).HeightRequest(40)
						.StrokeShape(new RoundRectangle().CornerRadius(20))
						.StrokeThickness(2)
						.Stroke(
							GetIndicatorBorderColor(sentence.UserActivity, sentence.IsCurrent))
						.Background(GetIndicatorBackgroundColor(sentence.UserActivity))
					)
				)
			)
			.Padding(DeviceInfo.Idiom == DeviceIdiom.Phone ?
				new Thickness(16, 6) :
				new Thickness(MyTheme.Size240))
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
		.Margin(0, 0, 0, 12);

	VisualNode RenderMultipleChoice()
	{
		if (State.GuessOptions == null || State.GuessOptions.Length == 0)
			return VStack();

		// Single column layout for longer words
		var tiles = new List<VisualNode>();
		for (int i = 0; i < State.GuessOptions.Length; i++)
		{
			tiles.Add(RenderOptionTile(State.GuessOptions[i], i));
		}

		return VStack(spacing: 8,
			tiles.ToArray()
		)
		.Padding(DeviceInfo.Platform == DevicePlatform.WinUI ? new Thickness(30, 0) : new Thickness(15, 0))
		.GridRow(0)
		.GridColumnSpan(4);
	}

	VisualNode RenderOptionTile(string option, int index)
	{
		return Border(
			Label(option)
				.FontSize(20)
				.Center()
				.TextColor(GetOptionTileTextColor(option))
				.Padding(16, 12)
		)
		.Background(GetOptionTileBackgroundColor(option))
		.Stroke(GetOptionTileBorderColor(option))
		.StrokeThickness(2)
		.StrokeShape(new RoundRectangle().CornerRadius(8))
		.Margin(0, 4)
		.OnTapped(() => OnOptionTapped(option));
	}

	ImageSource UserActivityToImageSource(UserActivity activity)
	{
		if (activity == null) return null;

		return activity.Accuracy == 100 ?
			MyTheme.IconCircleCheckmark :
			MyTheme.IconCancel;
	}

	Color GetIndicatorBackgroundColor(UserActivity activity)
	{
		if (activity == null) return Colors.Transparent;

		return activity.Accuracy == 100 ?
			MyTheme.Success :
			MyTheme.Error;
	}

	Color GetIndicatorBorderColor(UserActivity activity, bool isCurrent)
	{
		if (isCurrent)
			return MyTheme.HighlightDark;

		if (activity == null)
			return MyTheme.Gray200;

		return activity.Accuracy == 100 ?
			MyTheme.Success :
			MyTheme.Error;
	}

	// Option tile color methods matching VocabularyQuizPage style
	Color GetOptionTileBackgroundColor(string option)
	{
		var isSelected = State.SelectedOption == option;
		var showFeedback = State.HasBeenGraded;
		var isCorrect = option == State.CorrectAnswer;

		Color backgroundColor = Colors.Transparent;

		if (showFeedback)
		{
			if (isCorrect)
			{
				backgroundColor = MyTheme.Success;
			}
			else if (isSelected && !isCorrect)
			{
				backgroundColor = MyTheme.Error;
			}
		}
		else if (isSelected)
		{
			backgroundColor = MyTheme.HighlightDarkest.WithAlpha(0.1f);
		}

		return backgroundColor;
	}

	Color GetOptionTileTextColor(string option)
	{
		var isSelected = State.SelectedOption == option;
		var showFeedback = State.HasBeenGraded;
		var isCorrect = option == State.CorrectAnswer;

		Color textColor = Theme.IsLightTheme ?
			MyTheme.DarkOnLightBackground :
			MyTheme.LightOnDarkBackground;

		if (showFeedback)
		{
			if (isCorrect)
			{
				textColor = Colors.White;
			}
			else if (isSelected && !isCorrect)
			{
				textColor = Colors.White;
			}
		}

		return textColor;
	}

	Color GetOptionTileBorderColor(string option)
	{
		var isSelected = State.SelectedOption == option;
		var showFeedback = State.HasBeenGraded;
		var isCorrect = option == State.CorrectAnswer;

		Color borderColor = MyTheme.Gray200;

		if (showFeedback)
		{
			if (isCorrect)
			{
				borderColor = MyTheme.Success;
			}
			else if (isSelected && !isCorrect)
			{
				borderColor = MyTheme.Error;
			}
		}
		else if (isSelected)
		{
			borderColor = MyTheme.HighlightDarkest;
		}

		return borderColor;
	}

	void OnOptionTapped(string option)
	{
		if (State.HasBeenGraded) return; // Don't allow changes after grading

		SetState(s =>
		{
			s.SelectedOption = option;
			s.UserGuess = option; // Keep for compatibility with existing GradeMe logic
		});

		// Auto-grade immediately
		GradeMe();
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
			// 🏴‍☠️ CRITICAL: Replace the vocabulary word with __ placeholder for display
			s.CurrentSentence = challenge.SentenceText.Replace(challenge.VocabularyWordAsUsed, "__");
			s.RecommendedTranslation = challenge.RecommendedTranslation;
			s.GuessOptions = challenge.VocabularyWordGuesses?.Split(",").Select(x => x.Trim()).OrderBy(x => Guid.NewGuid()).ToArray();
			s.UserInput = challenge.UserActivity?.Input ?? string.Empty;
			s.UserGuess = null;

			// Reset tile state for new challenge
			s.SelectedOption = null;
			s.HasBeenGraded = false;
			s.CorrectAnswer = challenge.VocabularyWordAsUsed;
		});
	}

	async Task LoadSentences()
	{
		Debug.WriteLine("🏴‍☠️ ClozurePage: Starting LoadSentences");
		SetState(s => s.IsBusy = true);

		try
		{
			// Use the resource Id if available, or fallback to 0
			var resourceId = Props.Resource?.Id ?? 0;
			Debug.WriteLine($"🏴‍☠️ ClozurePage: Resource ID = {resourceId}, Skill ID = {Props.Skill?.Id}");

			var sentences = await _clozureService.GetSentences(resourceId, 8, Props.Skill.Id);
			Debug.WriteLine($"🏴‍☠️ ClozurePage: Retrieved {sentences?.Count() ?? 0} sentences");

			if (sentences?.Any() == true)
			{
				Debug.WriteLine("🏴‍☠️ ClozurePage: Setting up first sentence");
				var first = sentences.First();
				first.IsCurrent = true;

				SetState(s =>
				{
					s.Sentences = new ObservableCollection<Challenge>(sentences);
					// 🏴‍☠️ CRITICAL: Replace the vocabulary word with __ placeholder for display
					s.CurrentSentence = first.SentenceText.Replace(first.VocabularyWordAsUsed, "__");
					s.RecommendedTranslation = first.RecommendedTranslation;
					s.GuessOptions = first.VocabularyWordGuesses?.Split(",")
						.Select(x => x.Trim())
						.OrderBy(x => Guid.NewGuid())
						.ToArray();

					// Initialize tile state
					s.SelectedOption = null;
					s.HasBeenGraded = false;
					s.CorrectAnswer = first.VocabularyWordAsUsed;
				});

				// if (sentences.Count() < 10)
				// {
				// 	Debug.WriteLine("🏴‍☠️ ClozurePage: Loading more sentences in background");
				// 	SetState(s => s.IsBuffering = true);
				// 	var moreSentences = await _clozureService.GetSentences(resourceId, 8, Props.Skill.Id);
				// 	Debug.WriteLine($"🏴‍☠️ ClozurePage: Retrieved {moreSentences?.Count() ?? 0} additional sentences");
				// 	SetState(s =>
				// 	{
				// 		if (moreSentences != null && moreSentences.Any())
				// 		{
				// 			foreach (var sentence in moreSentences)
				// 			{
				// 				s.Sentences.Add(sentence);
				// 			}
				// 		}
				// 		s.IsBuffering = false;
				// 	});
				// }
			}
			else
			{
				Debug.WriteLine("🏴‍☠️ ClozurePage: No sentences returned from service");
				SetState(s => s.CurrentSentence = "No sentences available for this skill. Check yer resource configuration, matey!");
			}
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"🏴‍☠️ ClozurePage: Error loading sentences - {ex.Message}");
			Debug.WriteLine($"🏴‍☠️ ClozurePage: Stack trace - {ex.StackTrace}");
			SetState(s => s.CurrentSentence = $"Error loading sentences: {ex.Message}");
		}
		finally
		{
			Debug.WriteLine("🏴‍☠️ ClozurePage: LoadSentences completed");
			SetState(s => s.IsBusy = false);
		}
	}
	async Task GradeMe()
	{
		var currentChallenge = State.Sentences.FirstOrDefault(s => s.IsCurrent);
		if (currentChallenge == null) return;

		var stopwatch = Stopwatch.StartNew();

		var answer = State.UserMode == InputMode.MultipleChoice.ToString() ?
			State.UserGuess : State.UserInput;

		if (string.IsNullOrWhiteSpace(answer)) return;

		var isCorrect = answer.Equals(currentChallenge.VocabularyWordAsUsed,
			StringComparison.CurrentCultureIgnoreCase);

		stopwatch.Stop();

		// Determine context type based on word usage in sentence
		var contextType = DetermineClozureContextType(currentChallenge);

		// Calculate difficulty based on sentence complexity and word usage
		var difficultyWeight = CalculateClozureDifficulty(currentChallenge, answer);

		// Enhanced vocabulary lookup with comprehensive debugging
		Debug.WriteLine($"🏴‍☠️ ClozurePage: === VOCABULARY LOOKUP DEBUG ===");
		Debug.WriteLine($"🏴‍☠️ ClozurePage: Challenge.VocabularyWord: '{currentChallenge.VocabularyWord}'");
		Debug.WriteLine($"🏴‍☠️ ClozurePage: Challenge.VocabularyWordAsUsed: '{currentChallenge.VocabularyWordAsUsed}'");
		Debug.WriteLine($"🏴‍☠️ ClozurePage: Challenge has {currentChallenge.Vocabulary?.Count ?? 0} vocabulary words");

		if (currentChallenge.Vocabulary?.Any() == true)
		{
			for (int i = 0; i < currentChallenge.Vocabulary.Count; i++)
			{
				var vocab = currentChallenge.Vocabulary[i];
				Debug.WriteLine($"🏴‍☠️ ClozurePage: Vocabulary[{i}] - ID: {vocab.Id}, Native: '{vocab.NativeLanguageTerm}', Target: '{vocab.TargetLanguageTerm}'");
			}
		}
		else
		{
			Debug.WriteLine($"🏴‍☠️ ClozurePage: WARNING - Challenge.Vocabulary is null or empty!");
		}

		// Try multiple matching strategies like ClozureService does
		VocabularyWord vocabularyWord = null;

		if (currentChallenge.Vocabulary?.Any() == true)
		{
			// Strategy 1: Exact match with VocabularyWord
			vocabularyWord = currentChallenge.Vocabulary.FirstOrDefault(v =>
				string.Equals(v.TargetLanguageTerm, currentChallenge.VocabularyWord, StringComparison.OrdinalIgnoreCase));
			Debug.WriteLine($"🏴‍☠️ ClozurePage: Strategy 1 (exact VocabularyWord match): {(vocabularyWord != null ? $"Found ID {vocabularyWord.Id}" : "Not found")}");

			// Strategy 2: Exact match with VocabularyWordAsUsed
			if (vocabularyWord == null)
			{
				vocabularyWord = currentChallenge.Vocabulary.FirstOrDefault(v =>
					string.Equals(v.TargetLanguageTerm, currentChallenge.VocabularyWordAsUsed, StringComparison.OrdinalIgnoreCase));
				Debug.WriteLine($"🏴‍☠️ ClozurePage: Strategy 2 (exact VocabularyWordAsUsed match): {(vocabularyWord != null ? $"Found ID {vocabularyWord.Id}" : "Not found")}");
			}

			// Strategy 3: Contains match - VocabularyWord contains target term
			if (vocabularyWord == null)
			{
				vocabularyWord = currentChallenge.Vocabulary.FirstOrDefault(v =>
					currentChallenge.VocabularyWord?.Contains(v.TargetLanguageTerm, StringComparison.OrdinalIgnoreCase) == true);
				Debug.WriteLine($"🏴‍☠️ ClozurePage: Strategy 3 (VocabularyWord contains target): {(vocabularyWord != null ? $"Found ID {vocabularyWord.Id}" : "Not found")}");
			}

			// Strategy 4: Contains match - target term contains VocabularyWord
			if (vocabularyWord == null)
			{
				vocabularyWord = currentChallenge.Vocabulary.FirstOrDefault(v =>
					v.TargetLanguageTerm?.Contains(currentChallenge.VocabularyWord, StringComparison.OrdinalIgnoreCase) == true);
				Debug.WriteLine($"🏴‍☠️ ClozurePage: Strategy 4 (target contains VocabularyWord): {(vocabularyWord != null ? $"Found ID {vocabularyWord.Id}" : "Not found")}");
			}

			// Strategy 5: Fallback - just take the first one if only one exists
			if (vocabularyWord == null && currentChallenge.Vocabulary.Count == 1)
			{
				vocabularyWord = currentChallenge.Vocabulary.First();
				Debug.WriteLine($"🏴‍☠️ ClozurePage: Strategy 5 (fallback single word): Found ID {vocabularyWord.Id}");
			}
		}

		Debug.WriteLine($"🏴‍☠️ ClozurePage: Final result - Found vocabulary word: {vocabularyWord?.Id ?? 0} ('{vocabularyWord?.TargetLanguageTerm}')");
		Debug.WriteLine($"🏴‍☠️ ClozurePage: === END VOCABULARY LOOKUP DEBUG ===");

		if (vocabularyWord != null)
		{
			Debug.WriteLine($"🏴‍☠️ ClozurePage: Recording attempt for vocabulary word ID {vocabularyWord.Id}");

			var attempt = new VocabularyAttempt
			{
				VocabularyWordId = vocabularyWord.Id,
				UserId = GetCurrentUserId(),
				Activity = "Clozure",
				InputMode = State.UserMode,
				WasCorrect = isCorrect,
				DifficultyWeight = difficultyWeight,
				ContextType = contextType,
				LearningResourceId = Props.Resource?.Id,
				UserInput = answer,
				ExpectedAnswer = currentChallenge.VocabularyWordAsUsed,
				ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds,
				UserConfidence = State.UserConfidence
			};

			// Record attempt using enhanced service
			Debug.WriteLine($"🏴‍☠️ ClozurePage: Calling RecordAttemptAsync for word ID {vocabularyWord.Id}");
			var updatedProgress = await _progressService.RecordAttemptAsync(attempt);
			Debug.WriteLine($"🏴‍☠️ ClozurePage: Progress recorded! IsLearning: {updatedProgress.IsLearning}, IsKnown: {updatedProgress.IsKnown}, MasteryScore: {updatedProgress.MasteryScore:F2}");

			// Update challenge with new progress information
			UpdateChallengeProgress(currentChallenge, updatedProgress);

			// Provide enhanced feedback based on progress
			ShowEnhancedFeedback(currentChallenge, updatedProgress, isCorrect, attempt);

			// Update learning analytics
			await UpdateLearningAnalytics(currentChallenge, attempt, updatedProgress);
		}
		else
		{
			Debug.WriteLine($"🏴‍☠️ ClozurePage: ❌ CRITICAL - No vocabulary word found after all matching strategies!");
			Debug.WriteLine($"🏴‍☠️ ClozurePage: This means vocabulary progress tracking will NOT work for this challenge.");
			Debug.WriteLine($"🏴‍☠️ ClozurePage: Check ClozureService vocabulary linking logic!");
		}

		// Keep original user activity for compatibility
		var activity = new UserActivity
		{
			Activity = SentenceStudio.Shared.Models.Activity.Clozure.ToString(),
			Input = answer,
			Accuracy = isCorrect ? 100 : 0,
			CreatedAt = DateTime.Now,
			UpdatedAt = DateTime.Now
		};

		currentChallenge.UserActivity = activity;
		await _userActivityRepository.SaveAsync(activity);

		// Set graded state to show visual feedback on tiles
		SetState(s => s.HasBeenGraded = true);

		// Track session statistics
		SetState(s =>
		{
			s.SessionTotalCount++;
			if (isCorrect)
				s.SessionCorrectCount++;
		});

		if (isCorrect)
		{
			// 🏴‍☠️ Fill in the blank space with the correct answer (show complete sentence)
			SetState(s =>
			{
				s.UserInput = string.Empty;
				s.CurrentSentence = currentChallenge.SentenceText; // Show complete sentence
			});

			// Delay to show visual feedback before transitioning
			await Task.Delay(1000);
			TransitionToNextSentence();
		}
		else
		{
			// For incorrect answers, show feedback for a longer duration
			await Task.Delay(2000);
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
			SetState(s => s.AutoTransitionProgress = (e.SignalTime - startedTime).TotalMilliseconds / 4000);

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
		SetState(s =>
		{
			s.UserMode = InputMode.Text.ToString();
			s.AutoTransitionProgress = 0; // Reset progress bar
		});

		var currentIndex = State.Sentences.IndexOf(State.Sentences.FirstOrDefault(s => s.IsCurrent));
		if (currentIndex == -1) currentIndex = State.Sentences.Count - 1; // Handle case where no sentence is current

		if (currentIndex >= State.Sentences.Count - 1)
		{
			// Show session summary instead of dialog
			SetState(s => s.ShowSessionSummary = true);
			return;
		}

		var nextChallenge = State.Sentences[currentIndex + 1];
		JumpTo(nextChallenge);
	}

	// Method to continue after summary (load more sentences)
	async Task ContinueAfterSummary()
	{
		SetState(s => s.ShowSessionSummary = false);

		// Reset session stats for next round
		SetState(s =>
		{
			s.SessionCorrectCount = 0;
			s.SessionTotalCount = 0;
		});

		var result = await Application.Current.MainPage.DisplayAlert(
			_localize["Continue Practice"].ToString(),
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

					// Find the current index and jump to the next sentence
					var currentIndex = State.Sentences.IndexOf(State.Sentences.FirstOrDefault(s => s.IsCurrent));
					if (currentIndex >= 0 && currentIndex < State.Sentences.Count - 1)
					{
						JumpTo(State.Sentences[currentIndex + 1]);
					}
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
	}

	async Task PreviousSentence()
	{
		autoNextTimer?.Stop();
		SetState(s =>
		{
			s.UserMode = InputMode.Text.ToString();
			s.AutoTransitionProgress = 0; // Reset progress bar
		});

		var currentIndex = State.Sentences.IndexOf(State.Sentences.FirstOrDefault(s => s.IsCurrent));
		if (currentIndex == -1) return; // Handle case where no sentence is current
		if (currentIndex <= 0) return;

		var previousChallenge = State.Sentences[currentIndex - 1];
		JumpTo(previousChallenge);
	}

	protected override void OnMounted()
	{
		base.OnMounted();
		Debug.WriteLine($"🏴‍☠️ ClozurePage: OnMounted - Resource: {Props.Resource?.Title ?? "null"}, Skill: {Props.Skill?.Title ?? "null"}");
		LoadSentences();
	}

	protected override void OnWillUnmount()
	{
		autoNextTimer?.Dispose();
		base.OnWillUnmount();
	}

	// Enhanced tracking helper methods
	private string DetermineClozureContextType(Challenge currentChallenge)
	{
		var originalWord = currentChallenge.VocabularyWord ?? "";
		var wordAsUsed = currentChallenge.VocabularyWordAsUsed ?? "";

		// Check if word appears in conjugated or modified form
		if (!string.Equals(originalWord, wordAsUsed, StringComparison.CurrentCultureIgnoreCase))
		{
			return "Conjugated"; // Word is conjugated/modified
		}

		// Check sentence complexity (could be enhanced with NLP analysis)
		var sentence = currentChallenge.SentenceText ?? "";
		if (sentence.Split(' ').Length > 10)
		{
			return "Complex"; // Long, complex sentence
		}

		return "Sentence"; // Standard sentence context
	}

	private float CalculateClozureDifficulty(Challenge currentChallenge, string userAnswer)
	{
		float difficulty = 1.0f; // Base difficulty for clozure

		// Adjust based on context type
		var contextType = DetermineClozureContextType(currentChallenge);
		switch (contextType)
		{
			case "Conjugated":
				difficulty *= 1.8f; // Conjugated forms are significantly harder
				break;
			case "Complex":
				difficulty *= 1.4f; // Complex sentences are moderately harder
				break;
			case "Sentence":
				difficulty *= 1.2f; // Standard sentence context is moderately challenging
				break;
		}

		// Adjust based on input mode
		if (State.UserMode == InputMode.Text.ToString())
		{
			difficulty *= 1.3f; // Text entry is harder than multiple choice in context
		}

		// Adjust based on sentence length and complexity
		var sentence = currentChallenge.SentenceText ?? "";
		var wordCount = sentence.Split(' ').Length;
		if (wordCount > 15)
		{
			difficulty *= 1.2f; // Very long sentences are harder
		}

		// Adjust based on position of missing word
		var wordPosition = GetWordPositionInSentence(currentChallenge);
		if (wordPosition == "middle")
		{
			difficulty *= 1.1f; // Words in middle are slightly harder due to more context needed
		}

		return Math.Min(2.0f, Math.Max(0.8f, difficulty)); // Clamp between 0.8 and 2.0
	}

	private int GetCurrentUserId()
	{
		// TODO: Get actual user ID from user service
		return 1; // Default user for now
	}

	private void UpdateChallengeProgress(Challenge challenge, SentenceStudio.Shared.Models.VocabularyProgress progress)
	{
		// Update challenge display with progress information
		// This could update visual indicators showing mastery level
	}

	private void ShowEnhancedFeedback(Challenge challenge, SentenceStudio.Shared.Models.VocabularyProgress progress, bool isCorrect, VocabularyAttempt attempt)
	{
		if (isCorrect)
		{
			var masteryScore = progress.MasteryScore;
			var phaseText = GetPhaseDisplayText(progress.CurrentPhase);

			// Show mastery-based feedback
			if (masteryScore >= 0.8f)
			{
				ShowFeedback($"🎉 Perfect! Word mastered! ({phaseText})", "success");
			}
			else if (masteryScore >= 0.6f)
			{
				ShowFeedback($"🎯 Excellent! Strong progress - {(int)(masteryScore * 100)}% mastery", "success");
			}
			else
			{
				ShowFeedback($"✅ Correct! Building mastery - {(int)(masteryScore * 100)}%", "success");
			}

			// Show context-specific achievements
			if (attempt.ContextType == "Conjugated")
			{
				ShowFeedback("💪 Great job with the conjugated form!", "achievement");
			}
			else if (attempt.DifficultyWeight > 1.5f)
			{
				ShowFeedback("🔥 Impressive! That was a challenging usage!", "achievement");
			}
		}
		else
		{
			var masteryScore = progress.MasteryScore;
			var phaseText = GetPhaseDisplayText(progress.CurrentPhase);

			// Show encouraging feedback with context
			if (attempt.ContextType == "Conjugated")
			{
				ShowFeedback($"📚 Conjugated forms are tricky! Current mastery: {(int)(masteryScore * 100)}%", "info");
			}
			else
			{
				ShowFeedback($"🔍 Keep practicing! Current mastery: {(int)(masteryScore * 100)}% ({phaseText})", "info");
			}

			// Show helpful hints based on the error type
			ShowContextualHints(challenge, attempt);
		}
	}

	private async Task UpdateLearningAnalytics(Challenge challenge, VocabularyAttempt attempt, SentenceStudio.Shared.Models.VocabularyProgress progress)
	{
		// Track error patterns for personalized feedback
		if (!attempt.WasCorrect && attempt.ContextType == "Conjugated")
		{
			// User struggles with conjugated forms - could suggest focused practice
			await LogLearningInsight(attempt.VocabularyWordId, "conjugation_difficulty");
		}

		// Track response time patterns
		if (attempt.ResponseTimeMs > 10000) // More than 10 seconds
		{
			await LogLearningInsight(attempt.VocabularyWordId, "slow_response");
		}
		else if (attempt.ResponseTimeMs < 2000 && attempt.WasCorrect)
		{
			await LogLearningInsight(attempt.VocabularyWordId, "quick_correct");
		}

		// Track difficulty adaptation
		if (attempt.DifficultyWeight > 1.5f && attempt.WasCorrect)
		{
			await LogLearningInsight(attempt.VocabularyWordId, "high_difficulty_success");
		}
	}

	private string GetPhaseDisplayText(LearningPhase phase)
	{
		return phase switch
		{
			LearningPhase.Recognition => "Recognition Phase",
			LearningPhase.Production => "Production Phase",
			LearningPhase.Application => "Application Phase",
			_ => "Learning"
		};
	}

	private string GetWordPositionInSentence(Challenge challenge)
	{
		// Simplified position detection - could be enhanced
		var sentence = challenge.SentenceText ?? "";
		var words = sentence.Split(' ');
		var blankIndex = Array.FindIndex(words, w => w.Contains("__") || w.Contains("..."));

		if (blankIndex == -1) return "unknown";

		var position = (float)blankIndex / words.Length;
		return position switch
		{
			< 0.3f => "beginning",
			> 0.7f => "end",
			_ => "middle"
		};
	}

	private void ShowFeedback(string message, string type)
	{
		SetState(s =>
		{
			s.FeedbackMessage = message;
			s.FeedbackType = type;
			s.ShowFeedback = true;
		});

		// Auto-hide feedback after a few seconds
		Task.Delay(4000).ContinueWith(_ =>
		{
			SetState(s => s.ShowFeedback = false);
		});
	}

	private void ShowContextualHints(Challenge challenge, VocabularyAttempt attempt)
	{
		if (attempt.ContextType == "Conjugated")
		{
			ShowFeedback("💡 Hint: Check if the word needs to be conjugated for this context", "hint");
		}
		else if (attempt.InputMode == "TextEntry")
		{
			ShowFeedback("💡 Hint: Pay attention to the exact spelling and form", "hint");
		}
	}

	private async Task LogLearningInsight(int wordId, string insightType)
	{
		// Log learning insights for analytics
		Debug.WriteLine($"Learning insight for word {wordId}: {insightType}");
		// TODO: Implement actual insight logging to analytics service
	}

	private Color GetFeedbackBackgroundColor(string feedbackType)
	{
		return feedbackType switch
		{
			"success" => MyTheme.SupportSuccessDark,
			"achievement" => MyTheme.SupportSuccessMedium,
			"info" => MyTheme.Warning,
			"hint" => MyTheme.HighlightDarkest,
			_ => MyTheme.Gray400
		};
	}
}