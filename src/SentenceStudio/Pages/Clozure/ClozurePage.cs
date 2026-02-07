using MauiReactor.Shapes;
using System.Collections.ObjectModel;
using SentenceStudio.Pages.Dashboard;
using System.Timers;
using Microsoft.Extensions.Logging;
using SentenceStudio.Shared.Models;
using UXDivers.Popups.Maui.Controls;
using UXDivers.Popups.Services;

namespace SentenceStudio.Pages.Clozure;

/// <summary>
/// Clozure (Fill-in-the-Blank) Activity Page - Contextual Vocabulary Practice
/// 
/// USAGE CONTEXTS (CRITICAL - This page serves multiple purposes!):
/// 
/// 1. FROM DAILY PLAN (Structured Learning):
///    - Entry: Dashboard → Today's Plan → Click "Cloze" activity
///    - Props.FromTodaysPlan = true, Props.PlanItemId = set
///    - Content: Pre-selected sentences with target vocabulary
///    - Timer: ActivityTimerBar visible in Shell.TitleView
///    - Completion: Updates plan progress, returns to dashboard
///    - User Expectation: "I'm completing my daily cloze practice"
/// 
/// 2. MANUAL RESOURCE SELECTION (Free Practice):
///    - Entry: Resources → Browse → Select resource → Start Cloze
///    - Props.FromTodaysPlan = false, Props.PlanItemId = null
///    - Content: User-selected resource sentences
///    - Timer: No timer displayed
///    - Completion: Shows summary, offers continue/return options
///    - User Expectation: "I'm practicing fill-in-the-blank with this resource"
/// 
/// 3. FUTURE CONTEXTS (Update this section as new uses are added!):
///    - Adaptive Difficulty: Adjust number of blanks based on performance
///    - Grammar Focus: Target specific grammar patterns
///    - Timed Challenges: Speed-based cloze exercises
/// 
/// IMPORTANT: When modifying this page, ensure changes work correctly for ALL contexts!
/// Test both daily plan flow AND manual resource selection before committing.
/// </summary>

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

	// Platform cache
	public bool IsDesktopPlatform { get; set; }
}

partial class ClozurePage : Component<ClozurePageState, ActivityProps>
{
	#region Dependency Injection & Fields

	[Inject] ClozureService _clozureService;
	[Inject] AiService _aiService;
	[Inject] UserActivityRepository _userActivityRepository;
	[Inject] VocabularyProgressService _progressService;
	[Inject] SentenceStudio.Services.Timer.IActivityTimerService _timerService;
	[Inject] ILogger<ClozurePage> _logger;

	System.Timers.Timer autoNextTimer;
	LocalizationManager _localize => LocalizationManager.Instance;

	#endregion

	#region Constants

	// Timer and transition constants
	private const int AUTO_TRANSITION_DURATION_MS = 4000;
	private const int TIMER_INTERVAL_MS = 100;

	// Sentence count
	private const int SENTENCES_PER_SET = 8;

	// Font sizes
	private const int SENTENCE_FONT_SIZE_DESKTOP = 64;
	private const int SENTENCE_FONT_SIZE_MOBILE = 32;

	// Note: Spacing and padding values now use MyTheme semantic constants
	// (LayoutPadding, LayoutSpacing, CardPadding, CardMargin, SectionSpacing, ComponentSpacing, MicroSpacing)

	#endregion

	#region Lifecycle Methods

	public override VisualNode Render()
	{
		return ContentPage($"{_localize["Clozures"]}",
			Grid(rows: "*, 80", columns: "*",
				ScrollView(
					Grid(rows: "60,*,Auto", columns: "*",
						SentenceScoreboard(),
						SentenceDisplay(),
						UserInput()
					).RowSpacing(MyTheme.ComponentSpacing)
				),
				NavigationFooter(),
				AutoTransitionBar(),
				LoadingOverlay(),
				SessionSummaryOverlay()
			).RowSpacing(MyTheme.CardMargin)
		)
		.Set(MauiControls.Shell.TitleViewProperty, Props?.FromTodaysPlan == true ? new Components.ActivityTimerBar() : null)
		.Set(MauiControls.PlatformConfiguration.iOSSpecific.Page.UseSafeAreaProperty, false)
		.OnAppearing(LoadSentences);
	}

	protected override void OnMounted()
	{
		base.OnMounted();
		_logger.LogDebug("ClozurePage: OnMounted - Resource: {ResourceTitle}, Skill: {SkillTitle}", Props.Resource?.Title ?? "null", Props.Skill?.Title ?? "null");

		// Cache platform check
		SetState(s => s.IsDesktopPlatform = DeviceInfo.Platform == DevicePlatform.WinUI);

		// Start activity timer if launched from Today's Plan
		if (Props?.FromTodaysPlan == true)
		{
			_logger.LogDebug("ClozurePage: Starting activity timer for Clozure, PlanItemId: {PlanItemId}", Props.PlanItemId);
			_timerService.StartSession("Clozure", Props.PlanItemId);
		}

		LoadSentences();
	}

	protected override void OnWillUnmount()
	{
		autoNextTimer?.Dispose();

		// Pause timer when leaving activity
		if (Props?.FromTodaysPlan == true && _timerService.IsActive)
		{
			_logger.LogDebug("ClozurePage: Pausing activity timer");
			_timerService.Pause();
		}

		base.OnWillUnmount();
	}

	#endregion

	#region Main UI Components

	VisualNode AutoTransitionBar() =>
		ProgressBar()
			.Progress(State.AutoTransitionProgress)
			.HeightRequest(4)
			.Background(Colors.Transparent)
			.ProgressColor(MyTheme.HighlightDarkest)
			.VStart();

	VisualNode LoadingOverlay() =>
		Grid(
			Label("Thinking.....")
				.ThemeKey(MyTheme.Display)
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
						.ThemeKey(MyTheme.Title1)
						.TextColor(MyTheme.HighlightDarkest)
						.Center(),

					Label("Great work! Here's your progress:")
						.ThemeKey(MyTheme.Body1)
						.Center()
						.TextColor(Theme.IsLightTheme ?
							MyTheme.DarkOnLightBackground :
							MyTheme.LightOnDarkBackground),

					// Session stats
					Border(
						VStack(spacing: MyTheme.CardMargin,
							Label("📊 Session Results")
								.ThemeKey(MyTheme.Title3)
								.Center()
								.TextColor(MyTheme.HighlightDarkest),

							HStack(spacing: MyTheme.SectionSpacing,
								VStack(spacing: MyTheme.MicroSpacing,
									Label($"{State.SessionCorrectCount}")
										.ThemeKey(MyTheme.Headline)
										.TextColor(MyTheme.Success)
										.Center(),
									Label("Correct")
										.ThemeKey(MyTheme.Body2)
										.Center()
								),
								VStack(spacing: MyTheme.MicroSpacing,
									Label($"{State.SessionTotalCount - State.SessionCorrectCount}")
										.ThemeKey(MyTheme.Headline)
										.TextColor(MyTheme.Error)
										.Center(),
									Label("Incorrect")
										.ThemeKey(MyTheme.Body2)
										.Center()
								),
								VStack(spacing: MyTheme.MicroSpacing,
									Label($"{(State.SessionTotalCount > 0 ? (int)((float)State.SessionCorrectCount / State.SessionTotalCount * 100) : 0)}%")
										.ThemeKey(MyTheme.Headline)
										.TextColor(MyTheme.HighlightDarkest)
										.Center(),
									Label("Accuracy")
										.ThemeKey(MyTheme.Body2)
										.Center()
								)
							).Center()
						)
						.Padding(MyTheme.LayoutPadding)
					)
					.Background(Theme.IsLightTheme ?
						MyTheme.LightSecondaryBackground :
						MyTheme.DarkSecondaryBackground)
					.StrokeShape(new RoundRectangle().CornerRadius(8))
					.Margin(0, MyTheme.LayoutSpacing),

					// Sentences review
					Label($"You practiced {State.SessionTotalCount} sentence{(State.SessionTotalCount == 1 ? "" : "s")}")
						.ThemeKey(MyTheme.Body2)
						.Center()
						.TextColor(Theme.IsLightTheme ?
							MyTheme.DarkOnLightBackground :
							MyTheme.LightOnDarkBackground)
						.Margin(0, MyTheme.ComponentSpacing),

					// Continue button
					Button("Continue Practice")
						.OnClicked(ContinueAfterSummary)
						.Background(MyTheme.HighlightDarkest)
						.TextColor(Colors.White)
						.CornerRadius(8)
						.Padding(MyTheme.SectionSpacing, MyTheme.CardMargin)
						.Margin(0, MyTheme.LayoutSpacing)
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
				.FontSize(State.IsDesktopPlatform ? SENTENCE_FONT_SIZE_DESKTOP : SENTENCE_FONT_SIZE_MOBILE),
			Label(State.RecommendedTranslation)
		)
		.Margin(MyTheme.SectionSpacing)
		.GridRow(1);

	#endregion

	#region Input Components

	VisualNode UserInput() =>
		Grid(rows: "*, *", columns: "*, Auto, Auto, Auto",
			State.UserMode == InputMode.MultipleChoice.ToString() ?
				RenderMultipleChoice() :
				RenderTextInput()
		)
		.RowSpacing(State.IsDesktopPlatform ? 0 : MyTheme.MicroSpacing)
		.Padding(State.IsDesktopPlatform ? MyTheme.SectionSpacing : new Thickness(MyTheme.LayoutSpacing, 0))
		.GridRow(2);

	VisualNode RenderTextInput() =>

		VStack(
			Label("Answer").ThemeKey(MyTheme.Body1Strong),
			Border(
				Entry()
					.FontSize(32)
					.Text(State.UserInput)
					.OnTextChanged((s, e) => SetState(s => s.UserInput = e.NewTextValue))
					.ReturnType(ReturnType.Go)
					.OnCompleted(GradeMe)
			).ThemeKey(MyTheme.InputWrapper)
		)
		.GridRow(1)
		.GridColumn(0)
		.GridColumnSpan(DeviceInfo.Idiom == DeviceIdiom.Phone ? 4 : 1)
		.Margin(0, 0, 0, MyTheme.CardMargin);

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
		.Padding(State.IsDesktopPlatform ? new Thickness(MyTheme.SectionSpacing, 0) : new Thickness(MyTheme.LayoutSpacing, 0))
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
				.Padding(MyTheme.LayoutSpacing, MyTheme.CardMargin)
		)
		.Background(GetOptionTileBackgroundColor(option))
		.Stroke(GetOptionTileBorderColor(option))
		.StrokeThickness(2)
		.StrokeShape(new RoundRectangle().CornerRadius(8))
		.Margin(0, MyTheme.MicroSpacing)
		.OnTapped(() => OnOptionTapped(option));
	}

	#endregion

	#region UI Styling & Colors

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
			return MyTheme.Dark.Primary;

		if (activity == null)
			return MyTheme.Gray200;

		return activity.Accuracy == 100 ?
			MyTheme.Success :
			MyTheme.Error;
	}

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

	#endregion

	#region User Interaction Handlers

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
		_logger.LogDebug("ClozurePage: === VOCABULARY LOOKUP DEBUG ===");
		_logger.LogDebug("ClozurePage: Challenge.VocabularyWord: '{VocabularyWord}'", currentChallenge.VocabularyWord);
		_logger.LogDebug("ClozurePage: Challenge.VocabularyWordAsUsed: '{VocabularyWordAsUsed}'", currentChallenge.VocabularyWordAsUsed);
		_logger.LogDebug("ClozurePage: Challenge has {VocabularyCount} vocabulary words", currentChallenge.Vocabulary?.Count ?? 0);

		if (currentChallenge.Vocabulary?.Any() == true)
		{
			for (int i = 0; i < currentChallenge.Vocabulary.Count; i++)
			{
				var vocab = currentChallenge.Vocabulary[i];
				_logger.LogDebug("ClozurePage: Vocabulary[{Index}] - ID: {Id}, Native: '{NativeTerm}', Target: '{TargetTerm}'", i, vocab.Id, vocab.NativeLanguageTerm, vocab.TargetLanguageTerm);
			}
		}
		else
		{
			_logger.LogDebug("ClozurePage: WARNING - Challenge.Vocabulary is null or empty!");
		}

		// Try multiple matching strategies like ClozureService does
		VocabularyWord vocabularyWord = null;

		if (currentChallenge.Vocabulary?.Any() == true)
		{
			// Strategy 1: Exact match with VocabularyWord
			vocabularyWord = currentChallenge.Vocabulary.FirstOrDefault(v =>
				string.Equals(v.TargetLanguageTerm, currentChallenge.VocabularyWord, StringComparison.OrdinalIgnoreCase));
			_logger.LogDebug("ClozurePage: Strategy 1 (exact VocabularyWord match): {Result}", vocabularyWord != null ? $"Found ID {vocabularyWord.Id}" : "Not found");

			// Strategy 2: Exact match with VocabularyWordAsUsed
			if (vocabularyWord == null)
			{
				vocabularyWord = currentChallenge.Vocabulary.FirstOrDefault(v =>
					string.Equals(v.TargetLanguageTerm, currentChallenge.VocabularyWordAsUsed, StringComparison.OrdinalIgnoreCase));
				_logger.LogDebug("ClozurePage: Strategy 2 (exact VocabularyWordAsUsed match): {Result}", vocabularyWord != null ? $"Found ID {vocabularyWord.Id}" : "Not found");
			}

			// Strategy 3: Contains match - VocabularyWord contains target term
			if (vocabularyWord == null)
			{
				vocabularyWord = currentChallenge.Vocabulary.FirstOrDefault(v =>
					currentChallenge.VocabularyWord?.Contains(v.TargetLanguageTerm, StringComparison.OrdinalIgnoreCase) == true);
				_logger.LogDebug("ClozurePage: Strategy 3 (VocabularyWord contains target): {Result}", vocabularyWord != null ? $"Found ID {vocabularyWord.Id}" : "Not found");
			}

			// Strategy 4: Contains match - target term contains VocabularyWord
			if (vocabularyWord == null)
			{
				vocabularyWord = currentChallenge.Vocabulary.FirstOrDefault(v =>
					v.TargetLanguageTerm?.Contains(currentChallenge.VocabularyWord, StringComparison.OrdinalIgnoreCase) == true);
				_logger.LogDebug("ClozurePage: Strategy 4 (target contains VocabularyWord): {Result}", vocabularyWord != null ? $"Found ID {vocabularyWord.Id}" : "Not found");
			}

			// Strategy 5: Fallback - just take the first one if only one exists
			if (vocabularyWord == null && currentChallenge.Vocabulary.Count == 1)
			{
				vocabularyWord = currentChallenge.Vocabulary.First();
				_logger.LogDebug("ClozurePage: Strategy 5 (fallback single word): Found ID {Id}", vocabularyWord.Id);
			}
		}

		_logger.LogDebug("ClozurePage: Final result - Found vocabulary word: {WordId} ('{TargetTerm}')", vocabularyWord?.Id ?? 0, vocabularyWord?.TargetLanguageTerm);
		_logger.LogDebug("ClozurePage: === END VOCABULARY LOOKUP DEBUG ===");

		if (vocabularyWord != null)
		{
			_logger.LogDebug("ClozurePage: Recording attempt for vocabulary word ID {WordId}", vocabularyWord.Id);

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
			_logger.LogDebug("ClozurePage: Calling RecordAttemptAsync for word ID {WordId}", vocabularyWord.Id);
			var updatedProgress = await _progressService.RecordAttemptAsync(attempt);
			_logger.LogDebug("ClozurePage: Progress recorded! IsLearning: {IsLearning}, IsKnown: {IsKnown}, MasteryScore: {MasteryScore:F2}", updatedProgress.IsLearning, updatedProgress.IsKnown, updatedProgress.MasteryScore);

			// Update challenge with new progress information
			UpdateChallengeProgress(currentChallenge, updatedProgress);

			// Provide enhanced feedback based on progress
			ShowEnhancedFeedback(currentChallenge, updatedProgress, isCorrect, attempt);

			// Update learning analytics
			await UpdateLearningAnalytics(currentChallenge, attempt, updatedProgress);
		}
		else
		{
			_logger.LogDebug("ClozurePage: ❌ CRITICAL - No vocabulary word found after all matching strategies!");
			_logger.LogDebug("ClozurePage: This means vocabulary progress tracking will NOT work for this challenge.");
			_logger.LogDebug("ClozurePage: Check ClozureService vocabulary linking logic!");
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

	#endregion

	#region Navigation & Flow Control

	void JumpTo(Challenge challenge)
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

	async Task TransitionToNextSentence()
	{
		var currentIndex = State.Sentences.IndexOf(State.Sentences.First(s => s.IsCurrent));
		if (currentIndex >= State.Sentences.Count - 1)
		{
			NextSentence();
			return;
		}

		autoNextTimer = new System.Timers.Timer(TIMER_INTERVAL_MS);
		var startedTime = DateTime.Now;

		ElapsedEventHandler handler = null;
		handler = (sender, e) =>
		{
			SetState(s => s.AutoTransitionProgress = (e.SignalTime - startedTime).TotalMilliseconds / AUTO_TRANSITION_DURATION_MS);

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

	void NextSentence()
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

	void PreviousSentence()
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

	async Task ContinueAfterSummary()
	{
		SetState(s => s.ShowSessionSummary = false);

		// Reset session stats for next round
		SetState(s =>
		{
			s.SessionCorrectCount = 0;
			s.SessionTotalCount = 0;
		});

		var tcs = new TaskCompletionSource<bool>();
		var confirmPopup = new SimpleActionPopup
		{
			Title = $"{_localize["Continue Practice"]}",
			Text = $"{_localize["Would you like to get more sentences?"]}",
			ActionButtonText = $"{_localize["Yes"]}",
			SecondaryActionButtonText = $"{_localize["No"]}",
			CloseWhenBackgroundIsClicked = false,
			ActionButtonCommand = new Command(async () =>
			{
				tcs.TrySetResult(true);
				await IPopupService.Current.PopAsync();
			}),
			SecondaryActionButtonCommand = new Command(async () =>
			{
				tcs.TrySetResult(false);
				await IPopupService.Current.PopAsync();
			})
		};
		await IPopupService.Current.PushAsync(confirmPopup);
		var result = await tcs.Task;

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
					await IPopupService.Current.PushAsync(new SimpleActionPopup
					{
						Title = $"{_localize["No More Sentences"]}",
						Text = $"{_localize["There are no more sentences available for this resource."]}",
						ActionButtonText = $"{_localize["OK"]}",
						ShowSecondaryActionButton = false
					});
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error in CheckAnswer");
				await IPopupService.Current.PushAsync(new SimpleActionPopup
				{
					Title = $"{_localize["Error"]}",
					Text = $"{_localize["Unable to load more sentences."]}",
					ActionButtonText = $"{_localize["OK"]}",
					ShowSecondaryActionButton = false
				});
			}
			finally
			{
				SetState(s => s.IsBuffering = false);
			}
		}
	}

	#endregion

	#region Data Loading

	async Task LoadSentences()
	{
		_logger.LogDebug("ClozurePage: Starting LoadSentences");
		SetState(s => s.IsBusy = true);

		try
		{
			// Use the resource Id if available, or fallback to 0
			var resourceId = Props.Resource?.Id ?? 0;
			_logger.LogDebug("ClozurePage: Resource ID = {ResourceId}, Skill ID = {SkillId}", resourceId, Props.Skill?.Id);

			var sentences = await _clozureService.GetSentences(resourceId, SENTENCES_PER_SET, Props.Skill.Id);
			_logger.LogDebug("ClozurePage: Retrieved {SentenceCount} sentences", sentences?.Count() ?? 0);

			if (sentences?.Any() == true)
			{
				_logger.LogDebug("ClozurePage: Setting up first sentence");
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
				_logger.LogDebug("ClozurePage: No sentences returned from service");
				SetState(s => s.CurrentSentence = "No sentences available for this skill. Check yer resource configuration, matey!");
			}
		}
		catch (Exception ex)
		{
			_logger.LogDebug("ClozurePage: Error loading sentences - {ErrorMessage}", ex.Message);
			_logger.LogDebug("ClozurePage: Stack trace - {StackTrace}", ex.StackTrace);
			SetState(s => s.CurrentSentence = $"Error loading sentences: {ex.Message}");
		}
		finally
		{
			_logger.LogDebug("ClozurePage: LoadSentences completed");
			SetState(s => s.IsBusy = false);
		}
	}

	#endregion

	#region Progress Tracking & Analytics

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

	private void UpdateChallengeProgress(Challenge challenge, SentenceStudio.Shared.Models.VocabularyProgress progress)
	{
		// Update challenge display with progress information
		// This could update visual indicators showing mastery level
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

	private async Task LogLearningInsight(int wordId, string insightType)
	{
		// Log learning insights for analytics
		_logger.LogDebug("Learning insight for word {WordId}: {InsightType}", wordId, insightType);
		// TODO: Implement actual insight logging to analytics service
	}

	#endregion

	#region Feedback & User Notifications

	private void ShowEnhancedFeedback(Challenge challenge, SentenceStudio.Shared.Models.VocabularyProgress progress, bool isCorrect, VocabularyAttempt attempt)
	{
		if (isCorrect)
		{
			var masteryScore = progress.MasteryScore;
			var currentStreak = progress.CurrentStreak;
			var productionInStreak = progress.ProductionInStreak;

			// Show streak-based feedback (NEW)
			if (progress.IsKnown)
			{
				ShowFeedback($"🎉 Word mastered! 🔥 Streak: {currentStreak}", "success");
			}
			else if (masteryScore >= 0.6f)
			{
				ShowFeedback($"🎯 Excellent! 🔥 Streak: {currentStreak} | {(int)(masteryScore * 100)}% mastery", "success");
			}
			else
			{
				ShowFeedback($"✅ Correct! 🔥 Streak: {currentStreak} | Building mastery - {(int)(masteryScore * 100)}%", "success");
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

			// Show encouraging feedback with context (streak reset)
			if (attempt.ContextType == "Conjugated")
			{
				ShowFeedback($"📚 Conjugated forms are tricky! Streak reset - Current mastery: {(int)(masteryScore * 100)}%", "info");
			}
			else
			{
				ShowFeedback($"🔍 Keep practicing! Streak reset - Current mastery: {(int)(masteryScore * 100)}%", "info");
			}

			// Show helpful hints based on the error type
			ShowContextualHints(challenge, attempt);
		}
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

	#endregion

	#region Helper Methods

	private int GetCurrentUserId()
	{
		// TODO: Get actual user ID from user service
		return 1; // Default user for now
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

	#endregion
}
