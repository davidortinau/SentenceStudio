using Plugin.Maui.Audio;
using SentenceStudio.Pages.Dashboard;
using System.Text.RegularExpressions;
using SentenceStudio.Services;
using SentenceStudio.Models;
using SentenceStudio.Components;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.Pages.Reading;

/// <summary>
/// Reading Activity Page - Enhanced Audio and Vocabulary Integration
/// 
/// USAGE CONTEXTS (CRITICAL - This page serves multiple purposes!):
/// 
/// 1. FROM DAILY PLAN (Structured Learning):
///    - Entry: Dashboard → Today's Plan → Click "Reading" or "Listening" activity
///    - Props.FromTodaysPlan = true, Props.PlanItemId = set
///    - Content: Pre-selected resource by DeterministicPlanBuilder
///    - Timer: ActivityTimerBar visible in Shell.TitleView
///    - Completion: Updates plan progress, returns to dashboard
///    - User Expectation: "I'm completing my daily reading/listening practice"
/// 
/// 2. MANUAL RESOURCE SELECTION (Free Practice):
///    - Entry: Resources → Browse → Select resource → Start Reading
///    - Props.FromTodaysPlan = false, Props.PlanItemId = null
///    - Content: User-selected resource
///    - Timer: No timer displayed
///    - Completion: Shows summary, offers continue/return options
///    - User Expectation: "I'm reading this specific resource at my own pace"
/// 
/// 3. FUTURE CONTEXTS (Update this section as new uses are added!):
///    - Guided Reading: With comprehension questions
///    - Parallel Reading: Side-by-side with translation
///    - Speed Reading: Timed reading challenges
/// 
/// IMPORTANT: When modifying this page, ensure changes work correctly for ALL contexts!
/// Test both daily plan flow AND manual resource selection before committing.
/// </summary>

class TextSegment
{
    public string Text { get; set; } = string.Empty;
    public bool IsVocabulary { get; set; }
    public bool IsWord { get; set; } // 🎯 NEW: Indicates if this is a tappable word
    public VocabularyWord VocabularyWord { get; set; }
}

class ReadingPageState
{
    // Content
    public LearningResource Resource { get; set; }
    public List<string> Sentences { get; set; } = new();
    public List<VocabularyWord> VocabularyWords { get; set; } = new();

    // Timestamped Audio System (NEW)
    public TimestampedAudio TimestampedAudio { get; set; }
    public bool IsTimestampedAudioLoaded { get; set; } = false;
    public bool IsGeneratingAudio { get; set; } = false;
    public string AudioGenerationStatus { get; set; } = string.Empty;
    public double AudioGenerationProgress { get; set; } = 0.0;

    // Audio Playback State
    public bool IsAudioPlaying { get; set; } = false;
    public bool IsPlaying { get; set; } = false;  // For compatibility with some code paths
    public double CurrentPlaybackTime { get; set; } = 0.0;
    public double AudioDuration { get; set; } = 0.0;
    public float PlaybackSpeed { get; set; } = 1.0f;

    // Reading state
    public int CurrentSentenceIndex { get; set; } = -1;
    public int SelectedSentenceIndex { get; set; } = -1;
    public VocabularyWord SelectedVocabulary { get; set; }
    public bool IsVocabularyBottomSheetVisible { get; set; } = false;

    // 🎯 NEW: Dictionary lookup state
    public string DictionaryWord { get; set; }
    public string DictionaryDefinition { get; set; }
    public bool IsDictionaryBottomSheetVisible { get; set; } = false;
    public bool IsLookingUpWord { get; set; } = false;
    public bool CanRememberWord { get; set; } = false; // 🏴‍☠️ NEW: Can save word to vocabulary
    public bool IsSavingWord { get; set; } = false; // 🏴‍☠️ NEW: Is saving word

    // UI state
    public bool IsBusy { get; set; } = false;
    public double FontSize { get; set; } = 18.0;
    public string ErrorMessage { get; set; }
    public bool HasShownJumpHint { get; set; } = false;
    public bool HasDismissedInstructions { get; set; } = false;

    // 🎯 NEW: Cached UI content for performance
    public VisualNode[] CachedParagraphs { get; set; } = Array.Empty<VisualNode>();
    public List<List<TextSegment>> CachedSentenceSegments { get; set; } = new();
    public bool IsContentCached { get; set; } = false;
    public double CachedFontSize { get; set; } = 0.0;
    public int CachedCurrentSentence { get; set; } = -2; // Use -2 to force initial cache
    public bool CachedIsAudioPlaying { get; set; } = false;

    // 🚀 PERFORMANCE: Smart highlighting cache - avoids rebuilding everything
    public Dictionary<int, VisualNode> CachedParagraphsByIndex { get; set; } = new();
    public Dictionary<int, List<(string, int)>> CachedParagraphSentences { get; set; } = new();
    public bool IsStructuralCacheValid { get; set; } = false;

    // 🎯 NEW: FormattedString caching to avoid span recreation
    public Dictionary<int, Microsoft.Maui.Controls.FormattedString> CachedFormattedStrings { get; set; } = new();
    public Dictionary<int, int> CachedParagraphHighlightedSentence { get; set; } = new();
    public int LastHighlightedSentence { get; set; } = -1;

    // 🏴‍☠️ NEW: Navigation hiding for immersive reading
    public bool IsNavigationVisible { get; set; } = true;
    public double LastScrollY { get; set; } = 0.0;
    public double ScrollThreshold { get; set; } = 50.0; // Pixels to scroll before hiding
    public bool IsScrollingDown { get; set; } = false;
}

partial class ReadingPage : Component<ReadingPageState, ActivityProps>
{
    [Inject] ElevenLabsSpeechService _speechService;
    [Inject] LearningResourceRepository _resourceRepository;
    [Inject] TranslationService _translationService;
    [Inject] UserActivityRepository _userActivityRepository;
    [Inject] ILogger<TimestampedAudioManager> _audioManagerLogger;
    [Inject] ILogger<ReadingPage> _logger;
    [Inject] ILogger<SentenceTimingCalculator> _timingCalculatorLogger;
    [Inject] SentenceStudio.Services.Timer.IActivityTimerService _timerService;
    LocalizationManager _localize => LocalizationManager.Instance;

    private TimestampedAudioManager _audioManager;
    private SentenceTimingCalculator _timingCalculator;

    private MauiControls.ContentPage? _pageRef;
    private MauiControls.Grid? _mainGridRef;
    private ActivityTimerBar? _cachedTimerBar; // 🎯 Cache to prevent recreation every render

    public override VisualNode Render()
    {
        if (State.IsBusy)
        {
            return ContentPage($"{_localize["Reading"]}",
                VStack(
                    ActivityIndicator()
                        .IsRunning(true)
                        .VCenter()
                        .HCenter(),
                    Label($"{_localize["LoadingContent"]}")
                        .HCenter()
                        .ThemeKey(MyTheme.Body1)
                )
                .VCenter()
                .HCenter()
            );
        }

        if (!string.IsNullOrEmpty(State.ErrorMessage))
        {
            return ContentPage($"{_localize["Reading"]}",
                VStack(
                    Label("⚠️")
                        .FontSize(48)
                        .HCenter(),
                    Label(State.ErrorMessage)
                        .HCenter()
                        .ThemeKey(MyTheme.Body1),
                    Button($"{_localize["GoBack"]}")
                        .OnClicked(GoBack)
                        .HCenter()
                )
                .VCenter()
                .HCenter()
                .Spacing(MyTheme.Size160)
            );
        }

        return ContentPage(pageRef => _pageRef = pageRef,
            ToolbarItem()
                .IconImageSource(MyTheme.IconFontDecrease)
                .OnClicked(DecreaseFontSize),
            ToolbarItem()
                .IconImageSource(MyTheme.IconFontIncrease)
                .OnClicked(IncreaseFontSize),
            ToolbarItem()
                .IconImageSource(MyTheme.IconDelete)
                .OnClicked(ClearAudioCache),
            ToolbarItem()
                .IconImageSource(State.IsAudioPlaying ? MyTheme.IconPause : MyTheme.IconPlay)
                .OnClicked(TogglePlayback),
            Grid(rows: "Auto,Auto,*,Auto", columns: "*",
                Props?.FromTodaysPlan == true ? RenderTitleView() : null,
                RenderAudioLoadingBanner(),
                // RenderHeader(),
                RenderReadingContent(),
                RenderAudioControls(),
                RenderVocabularyBottomSheet(),
                RenderDictionaryBottomSheet()
            )
        )
        .Title($"{_localize["Reading"]}")
        .Set(MauiControls.Shell.NavBarIsVisibleProperty, State.IsNavigationVisible)
        .OnAppearing(LoadContentAsync);
    }

    private VisualNode RenderTitleView()
    {
        // 🎯 Reuse cached timer to prevent recreation/flashing every render
        _cachedTimerBar ??= new ActivityTimerBar();
        return Grid(mainGridRef => _mainGridRef = mainGridRef, _cachedTimerBar).HEnd().VCenter();
    }

    private void TrySetShellTitleView()
    {
        if (_pageRef != null && _mainGridRef != null)
        {
            _pageRef.Dispatcher.Dispatch(() =>
            {
                MauiControls.Shell.SetTitleView(_pageRef, Props?.FromTodaysPlan == true ? _mainGridRef : null);
            });
        }
    }

    VisualNode RenderAudioLoadingBanner() =>
        Border(
            HStack(
                ActivityIndicator()
                    .IsRunning(State.IsGeneratingAudio)
                    .Color(MyTheme.HighlightDarkest),
                Label(State.AudioGenerationStatus)
                    .ThemeKey(MyTheme.Body1)
                    .VCenter(),
                ProgressBar()
                    .Progress(State.AudioGenerationProgress)
                    .ProgressColor(MyTheme.HighlightDarkest)
                    .HorizontalOptions(LayoutOptions.FillAndExpand)
                    .IsVisible(State.AudioGenerationProgress > 0)
            )
            .Spacing(MyTheme.Size120)
            .Padding(MyTheme.Size160)
        )
        .Background(MyTheme.HighlightDarkest.WithAlpha(0.2f))
        .Stroke(MyTheme.HighlightDarkest.WithAlpha(0.3f))
        .GridRow(0)
        .IsVisible(State.IsGeneratingAudio);

    VisualNode RenderReadingContent() =>
        ScrollView(
            VStack(
                new RxInteractiveTextRenderer()
                    .Content(State.Sentences, State.VocabularyWords, PrepareSegments())
                    .CurrentSentence(State.CurrentSentenceIndex)
                    .FontSize((float)State.FontSize)
                    .OnVocabularyWordTapped((v) => ShowVocabularyBottomSheet(v))
                    .OnWordTapped(word =>
                    {
                        // Handle word tap for dictionary lookup
                        _logger.LogDebug("Word tapped: {Word}", word);
                        LookupWordInDictionary(word);
                    })
                    .OnSentenceDoubleTapped(sentenceIndex =>
                    {
                        // 🎯 NEW: Handle sentence double-tap to jump to that sentence
                        _logger.LogDebug("Sentence double-tapped: {SentenceIndex}", sentenceIndex);

                        // Show helpful hint for first-time users
                        if (!State.HasShownJumpHint)
                        {
                            _ = Task.Run(async () =>
                            {
                                await AppShell.DisplayToastAsync("🏴‍☠️ Jumping to that sentence, Captain!");
                                SetState(s => s.HasShownJumpHint = true);
                                Preferences.Set("ReadingActivity_HasShownJumpHint", true);
                            });
                        }

                        _ = Task.Run(() => StartPlaybackFromSentence(sentenceIndex));
                    })
                    .HorizontalOptions(LayoutOptions.FillAndExpand)
            )
            .Spacing(MyTheme.Size160)
            .Padding(MyTheme.Size240)
        )
        .OnScrolled(OnScrollViewScrolled)
        .GridRow(2);

    List<List<SentenceStudio.Components.TextSegment>> PrepareSegments()
    {
        var segments = new List<List<SentenceStudio.Components.TextSegment>>();

        foreach (var sentence in State.Sentences)
        {
            // 🏴‍☠️ NEW: Handle paragraph break markers
            if (sentence == "PARAGRAPH_BREAK")
            {
                // Add a special paragraph break segment
                var paragraphBreakSegments = new List<SentenceStudio.Components.TextSegment>
            {
                new SentenceStudio.Components.TextSegment
                {
                    Text = "\n\n", // Double line break for paragraph spacing
                    IsVocabulary = false,
                    IsWord = false,
                    VocabularyWord = null
                }
            };
                segments.Add(paragraphBreakSegments);
                continue;
            }

            var sentenceSegments = new List<SentenceStudio.Components.TextSegment>();
            var words = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            foreach (var word in words)
            {
                var vocab = State.VocabularyWords?.FirstOrDefault(v =>
                    v.TargetLanguageTerm?.ToLowerInvariant() == word.ToLowerInvariant());

                sentenceSegments.Add(new SentenceStudio.Components.TextSegment
                {
                    Text = word,
                    IsVocabulary = vocab != null,
                    IsWord = true,
                    VocabularyWord = vocab
                });

                if (word != words.Last())
                {
                    sentenceSegments.Add(new SentenceStudio.Components.TextSegment
                    {
                        Text = " ",
                        IsVocabulary = false,
                        IsWord = false
                    });
                }
            }

            segments.Add(sentenceSegments);
        }

        return segments;
    }

    VisualNode[] RenderParagraphs()
    {
        return PerformanceLogger.Time("RenderParagraphs", () =>
        {
            // 🚀 PERFORMANCE: Check if only sentence highlighting changed (most common case)
            bool onlyHighlightingChanged = State.IsStructuralCacheValid &&
                State.CachedFontSize == State.FontSize &&
                State.CachedCurrentSentence != State.CurrentSentenceIndex &&
                State.CachedIsAudioPlaying == State.IsAudioPlaying;

            if (onlyHighlightingChanged)
            {
                // Fast path: Only update paragraph colors without rebuilding structure
                return PerformanceLogger.Time("FastHighlightingUpdate", () =>
                {
                    var updatedParagraphs = UpdateParagraphHighlighting();
                    State.CachedCurrentSentence = State.CurrentSentenceIndex;
                    _logger.LogTrace("Fast highlighting update completed");
                    return updatedParagraphs;
                }, 2.0); // Warn if > 2ms
            }

            // 🚀 PERFORMANCE: Check full cache validity 
            if (State.IsContentCached &&
                State.CachedFontSize == State.FontSize &&
                State.CachedCurrentSentence == State.CurrentSentenceIndex &&
                State.CachedIsAudioPlaying == State.IsAudioPlaying)
            {
                _logger.LogTrace("Using cached paragraphs");
                return State.CachedParagraphs;
            }

            _logger.LogDebug("Cache miss - rebuilding content (FontSize: {CachedFontSize}->{FontSize}, Sentence: {CachedCurrentSentence}->{CurrentSentenceIndex}, Playing: {CachedIsAudioPlaying}->{IsAudioPlaying})",
                State.CachedFontSize, State.FontSize, State.CachedCurrentSentence, State.CurrentSentenceIndex, State.CachedIsAudioPlaying, State.IsAudioPlaying);

            // Cache is invalid or doesn't exist - rebuild content
            return PerformanceLogger.Time("FullContentRebuild", () =>
            {
                BuildContentCache();
                return State.CachedParagraphs;
            }, 20.0); // Warn if > 20ms
        }, 5.0); // Warn if total > 5ms
    }

    VisualNode[] UpdateParagraphHighlighting()
    {
        return PerformanceLogger.Time("FastHighlightingUpdate", () =>
        {
            // 🚀 PERFORMANCE: New strategy - only update what changed, reuse everything else
            _logger.LogTrace("Fast highlighting update started");

            // Find which paragraphs actually need updates
            var paragraphGroups = GroupSentencesIntoParagraphs();
            var updatedParagraphs = new VisualNode[paragraphGroups.Count];
            var hasAnyChanges = false;

            for (int paragraphIndex = 0; paragraphIndex < paragraphGroups.Count; paragraphIndex++)
            {
                var paragraphSentences = paragraphGroups[paragraphIndex];
                var sentenceIndices = paragraphSentences.Select(s => s.Item2).ToList();

                // Check if this paragraph needs updating
                bool containsCurrentSentence = sentenceIndices.Contains(State.CurrentSentenceIndex);
                bool containsPreviousSentence = sentenceIndices.Contains(State.CachedCurrentSentence);
                bool needsUpdate = containsCurrentSentence || containsPreviousSentence;

                if (needsUpdate && State.CachedParagraphsByIndex.ContainsKey(paragraphIndex))
                {
                    // Rebuild this specific paragraph
                    updatedParagraphs[paragraphIndex] = BuildSingleParagraph(paragraphSentences, paragraphIndex);
                    hasAnyChanges = true;
                }
                else if (State.CachedParagraphsByIndex.ContainsKey(paragraphIndex))
                {
                    // Reuse exactly the same VisualNode instance - no changes
                    updatedParagraphs[paragraphIndex] = State.CachedParagraphsByIndex[paragraphIndex];
                }
                else
                {
                    // First time - need to build
                    updatedParagraphs[paragraphIndex] = BuildSingleParagraph(paragraphSentences, paragraphIndex);
                    hasAnyChanges = true;
                }
            }

            if (hasAnyChanges)
            {
                State.CachedParagraphs = updatedParagraphs;
                _logger.LogTrace("Fast highlighting update completed");
            }
            else
            {
                _logger.LogTrace("No changes needed - reusing cached paragraphs");
            }

            return State.CachedParagraphs;
        }, 10.0); // Warn if > 10ms
    }

    VisualNode BuildSingleParagraph(List<(string, int)> paragraphSentences, int paragraphIndex)
    {
        return PerformanceLogger.Time($"BuildSingleParagraph[{paragraphIndex}]", () =>
        {
            var spans = PerformanceLogger.Time("CreateSpans", () =>
            {
                var spanList = new List<VisualNode>();

                foreach (var (sentence, sentenceIndex) in paragraphSentences)
                {
                    var segments = PerformanceLogger.Time("ParseSentence", () =>
                        ParseSentenceForVocabularyAndWords(sentence), 5.0);

                    foreach (var segment in segments)
                    {
                        var textColor = GetTextColorForSentence(sentenceIndex);

                        if (segment.IsVocabulary)
                        {
                            // Vocabulary word with interaction
                            spanList.Add(
                                Span(segment.Text,
                                    MyTheme.HighlightDarkest,
                                    FontAttributes.None,
                                    TapGestureRecognizer().OnTapped(() => ShowVocabularyBottomSheet(segment.VocabularyWord)))
                                    .TextDecorations(TextDecorations.Underline)
                            );
                        }
                        else if (segment.IsWord)
                        {
                            // Regular word with dictionary lookup capability
                            spanList.Add(
                                Span(segment.Text,
                                    textColor,
                                    FontAttributes.None,
                                    TapGestureRecognizer().OnTapped(() => LookupWordInDictionary(segment.Text)))
                            );
                        }
                        else
                        {
                            // Regular text with highlighting if active
                            spanList.Add(Span(segment.Text, textColor));
                        }
                    }

                    // Add space between sentences
                    if (sentence != paragraphSentences.Last().Item1)
                    {
                        spanList.Add(Span(" "));
                    }
                }

                return spanList;
            }, 15.0);

            var formattedString = PerformanceLogger.Time("CreateFormattedString", () =>
                FormattedString(spans.ToArray()), 5.0);

            var paragraph = PerformanceLogger.Time("CreateParagraphLayout", () =>
            {
                return VStack(
                    Label(formattedString)
                        .FontSize(State.FontSize)
                        .LineHeight(1.5)
                )
                .Padding(MyTheme.Size120)
                .OnTapped(() => StartPlaybackFromSentence(paragraphSentences.First().Item2), 2);
            }, 5.0);

            // Cache this paragraph
            State.CachedParagraphsByIndex[paragraphIndex] = paragraph;
            State.CachedParagraphSentences[paragraphIndex] = paragraphSentences;

            return paragraph;
        }, 8.0); // Warn if total > 8ms
    }

    void BuildContentCache()
    {
        PerformanceLogger.Time("BuildContentCache", () =>
        {
            var paragraphs = new List<VisualNode>();
            var paragraphGroups = GroupSentencesIntoParagraphs();

            _logger.LogDebug("Building cache for {SentenceCount} sentences in {ParagraphCount} paragraphs", State.Sentences.Count, paragraphGroups.Count);

            // Clear previous caches
            State.CachedParagraphsByIndex.Clear();
            State.CachedParagraphSentences.Clear();

            foreach (var (paragraphSentences, paragraphIndex) in paragraphGroups.Select((p, i) => (p, i)))
            {
                var paragraph = BuildSingleParagraph(paragraphSentences, paragraphIndex);
                paragraphs.Add(paragraph);
            }

            // Update cache
            State.CachedParagraphs = paragraphs.ToArray();
            State.IsContentCached = true;
            State.IsStructuralCacheValid = true;
            State.CachedFontSize = State.FontSize;
            State.CachedCurrentSentence = State.CurrentSentenceIndex;
            State.CachedIsAudioPlaying = State.IsAudioPlaying;

            _logger.LogDebug("Cache built for {ParagraphCount} paragraphs", paragraphs.Count);
        }, 30.0); // Warn if > 30ms
    }

    /// <summary>
    /// Groups sentences into paragraphs based on actual paragraph breaks in the original transcript.
    /// Uses double line breaks (\n\n or \r\n\r\n) to detect paragraph boundaries.
    /// </summary>
    List<List<(string, int)>> GroupSentencesIntoParagraphs()
    {
        if (State.Resource?.Transcript == null || !State.Sentences.Any())
            return new List<List<(string, int)>>();

        // Split transcript by paragraph breaks to find natural groupings
        var transcript = State.Resource.Transcript;
        var paragraphTexts = transcript.Split(new[] { "\r\n\r\n", "\n\n", "\r\r" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim().Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " "))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

        _logger.LogDebug("🔍 Found {Count} paragraphs in transcript", paragraphTexts.Count);

        // Now group sentences by which paragraph they belong to
        var paragraphs = new List<List<(string, int)>>();
        int currentSentenceIndex = 0;

        foreach (var paragraphText in paragraphTexts)
        {
            var paragraphSentences = new List<(string, int)>();

            // Find all sentences that are part of this paragraph
            while (currentSentenceIndex < State.Sentences.Count)
            {
                var sentence = State.Sentences[currentSentenceIndex].Trim();

                // Check if this sentence appears in the current paragraph
                if (paragraphText.Contains(sentence, StringComparison.Ordinal))
                {
                    paragraphSentences.Add((State.Sentences[currentSentenceIndex], currentSentenceIndex));
                    currentSentenceIndex++;
                }
                else
                {
                    // This sentence belongs to the next paragraph
                    break;
                }
            }

            if (paragraphSentences.Any())
            {
                _logger.LogTrace("  Paragraph {Index}: {Count} sentences", paragraphs.Count, paragraphSentences.Count);
                paragraphs.Add(paragraphSentences);
            }
        }

        // Handle any remaining sentences that didn't match (shouldn't happen, but safety)
        if (currentSentenceIndex < State.Sentences.Count)
        {
            var remainingSentences = new List<(string, int)>();
            for (int i = currentSentenceIndex; i < State.Sentences.Count; i++)
            {
                remainingSentences.Add((State.Sentences[i], i));
            }
            _logger.LogWarning("⚠️ Found {Count} unmatched sentences, adding as final paragraph", remainingSentences.Count);
            paragraphs.Add(remainingSentences);
        }

        return paragraphs;
    }

    Color GetTextColorForSentence(int sentenceIndex)
    {
        if (sentenceIndex == State.CurrentSentenceIndex && State.IsAudioPlaying)
            return MyTheme.HighlightDark; // Use secondary color for sentence highlighting (different from vocabulary Primary)
        else
            return MyTheme.IsLightTheme ? MyTheme.DarkOnLightBackground : MyTheme.LightOnDarkBackground;
    }

    VisualNode RenderReadingInstructions() =>
        Border(
            HStack(
                Label("💡")
                    .FontSize(16),
                VStack(
                    Label($"{_localize["ReadingControls"]}")
                        .FontAttributes(FontAttributes.Bold)
                        .FontSize(14)
                            .ThemeKey(MyTheme.Caption1),
                        Label("• Use A-/A+ buttons to adjust text size")
                            .FontSize(12)
                            .ThemeKey(MyTheme.Body1),
                        Label("• Tap vocabulary words for translations")
                            .FontSize(12)
                            .ThemeKey(MyTheme.Body1),
                        Label("• Double-tap any sentence to jump to that point")
                            .FontSize(12)
                            .ThemeKey(MyTheme.Body1)
                    )
                    .Spacing(MyTheme.MicroSpacing),
                    Button("✕")
                        .FontSize(12)
                        .OnClicked(DismissInstructions)
                        .HorizontalOptions(LayoutOptions.End)
                )
                .Spacing(MyTheme.Size120)
            )
            .Background(MyTheme.HighlightMedium.WithAlpha(0.3f))
            .Stroke(MyTheme.HighlightDarkest.WithAlpha(0.5f))
            .Padding(MyTheme.Size120)
            .Margin(MyTheme.Size160)
            .IsVisible(!State.HasDismissedInstructions);

    void DismissInstructions()
    {
        SetState(s => s.HasDismissedInstructions = true);
        Preferences.Set("ReadingActivity_HasDismissedInstructions", true);
    }

    VisualNode RenderAudioControls() =>
        Grid("*", "Auto,*,Auto",
            ImageButton()
                .Source(MyTheme.IconPreviousSm)
                .OnClicked(PreviousSentence)
                .GridColumn(0),
            VStack(
                Label(string.Format($"{_localize["SentenceProgress"]}", State.CurrentSentenceIndex + 1, State.Sentences.Count))
                    .HCenter()
                    .ThemeKey(MyTheme.Caption1),
                Label(FormatPlaybackTime(State.CurrentPlaybackTime))
                    .HCenter()
                    .ThemeKey(MyTheme.Caption1)
                    .FontSize(12)
                    .IsVisible(State.IsTimestampedAudioLoaded)
            )
            .GridColumn(1)
            .VCenter()
            .Spacing(MyTheme.MicroSpacing),
            ImageButton()
                .Source(MyTheme.IconNextSm)
                .OnClicked(NextSentence)
                .GridColumn(2)
        // Label($"{State.PlaybackSpeed:F1}x")
        //     .OnTapped(CyclePlaybackSpeed)
        //     .GridColumn(4)
        //     .VCenter()
        //     .Padding(MyTheme.Size80)
        )
        .Padding(MyTheme.Size160)
        .GridRow(3)
        .IsVisible(State.Sentences.Any() && State.IsNavigationVisible);

    VisualNode RenderVocabularyBottomSheet() =>
        new SfBottomSheet(
            ScrollView(
                VStack(
                    Label(State.SelectedVocabulary?.TargetLanguageTerm)
                        .FontSize(24)
                        .FontAttributes(FontAttributes.Bold)
                        .ThemeKey(MyTheme.Title1)
                        .HCenter(),
                    Label(State.SelectedVocabulary?.NativeLanguageTerm)
                        .FontSize(18)
                        .ThemeKey(MyTheme.Body1)
                        .HCenter(),
                    Button($"{_localize["Close"]}")
                        .OnClicked(CloseVocabularyBottomSheet)
                        .HCenter()
                )
                .Spacing(MyTheme.Size160)
                .Padding(MyTheme.Size240)
            )
        )
        .GridRowSpan(4)
        .IsOpen(State.IsVocabularyBottomSheetVisible);

    VisualNode RenderDictionaryBottomSheet() =>
        new SfBottomSheet(
            ScrollView(
                VStack(
                    Label(State.DictionaryWord)
                        .FontSize(24)
                        .FontAttributes(FontAttributes.Bold)
                        .ThemeKey(MyTheme.Title1)
                        .HCenter(),
                    State.IsLookingUpWord
                        ? VStack(
                            ActivityIndicator()
                                .IsRunning(true)
                                .Color(MyTheme.HighlightDarkest)
                                .HCenter(),
                            Label($"{_localize["LookingUpDefinition"]}")
                                .FontSize(16)
                                .ThemeKey(MyTheme.Body1)
                                .HCenter()
                        )
                        .Spacing(MyTheme.Size120)
                        : VStack(
                            Label(State.DictionaryDefinition)
                                .FontSize(18)
                                .ThemeKey(MyTheme.Body1)
                                .HCenter(),

                            // 🏴‍☠️ NEW: Remember vocabulary word button
                            State.CanRememberWord
                                ? State.IsSavingWord
                                    ? HStack(
                                        ActivityIndicator()
                                            .IsRunning(true)
                                            .Color(MyTheme.HighlightDarkest),
                                        Label($"{_localize["SavingWord"]}")
                                            .ThemeKey(MyTheme.Body1)
                                    )
                                    .HCenter()
                                    .Spacing(MyTheme.Size80)
                                    : Button($"{_localize["RememberThisWord"]}")
                                        .OnClicked(() => RememberVocabularyWord())
                                        .Background(MyTheme.HighlightMedium)
                                        .TextColor(MyTheme.HighlightDarkest)
                                        .HCenter()
                                : null
                        )
                        .Spacing(MyTheme.Size120),

                    Button($"{_localize["Close"]}")
                        .OnClicked(() => CloseDictionaryBottomSheet())
                        .HCenter()
                )
                .Spacing(MyTheme.Size160)
                .Padding(MyTheme.Size240)
            )
        )
        .GridRowSpan(4)
        .IsOpen(State.IsDictionaryBottomSheetVisible);

    // Audio Management
    async Task StartPlaybackFromSentence(int startIndex)
    {
        if (_audioManager == null || !State.IsTimestampedAudioLoaded)
        {
            await AppShell.DisplayToastAsync("🏴‍☠️ Audio not ready yet, Captain!");
            return;
        }

        if (startIndex < 0 || startIndex >= State.Sentences.Count)
            return;

        StopCurrentPlayback();

        SetState(s =>
        {
            s.CurrentSentenceIndex = startIndex;
            s.IsAudioPlaying = true;
        });

        try
        {
            // Use timestamp-based playback
            await _audioManager.PlayFromSentenceAsync(startIndex);
        }
        catch (Exception ex)
        {
            await AppShell.DisplayToastAsync($"🏴‍☠️ Playback error: {ex.Message}");
        }
    }

    Task PlaySentenceFromCache(int index, string audioFilePath)
    {
        // This method is now handled by StartPlaybackFromSentence with timestamps
        return StartPlaybackFromSentence(index);
    }

    Task PlaySentence(int index)
    {
        // This method is now handled by StartPlaybackFromSentence with timestamps
        return StartPlaybackFromSentence(index);
    }

    private void OnSentencePlaybackEnded(object sender, EventArgs e)
    {
        // Auto-advance to next sentence
        if (State.CurrentSentenceIndex < State.Sentences.Count - 1)
        {
            var nextIndex = State.CurrentSentenceIndex + 1;
            Task.Run(() => StartPlaybackFromSentence(nextIndex));
        }
        else
        {
            // End of reading
            SetState(s =>
            {
                s.IsAudioPlaying = false;
                s.CurrentSentenceIndex = -1;
            });
        }
    }

    void StopCurrentPlayback()
    {
        if (_audioManager != null)
        {
            _audioManager.Stop();
        }

        SetState(s => s.IsAudioPlaying = false);
    }

    async Task TogglePlayback()
    {
        // Check if audio is still loading
        if (State.IsGeneratingAudio)
        {
            await AppShell.DisplayToastAsync("🏴‍☠️ Hold yer horses! Audio is still loading, Captain!");
            return;
        }

        if (!State.IsTimestampedAudioLoaded)
        {
            await AppShell.DisplayToastAsync("🏴‍☠️ No audio loaded yet, Captain! Try again in a moment.");
            return;
        }

        if (State.IsAudioPlaying)
        {
            StopCurrentPlayback();
        }
        else
        {
            // Resume from current position or start from beginning
            var startIndex = State.CurrentSentenceIndex >= 0 ?
                State.CurrentSentenceIndex : 0;
            await StartPlaybackFromSentence(startIndex);
        }
    }

    async Task PreviousSentence()
    {
        // Handle case where no sentence is currently selected
        if (State.CurrentSentenceIndex == -1)
        {
            SetState(s => s.CurrentSentenceIndex = 0);
            return;
        }

        if (State.CurrentSentenceIndex > 0)
        {
            var newIndex = State.CurrentSentenceIndex - 1;

            // ALWAYS update the visual highlighting immediately for responsive UI
            SetState(s => s.CurrentSentenceIndex = newIndex);
            _logger.LogDebug("PreviousSentence: Updated visual highlighting to sentence {SentenceIndex}", newIndex);

            // If audio is playing and we have an audio manager, also update audio position
            if (_audioManager != null && State.IsAudioPlaying)
            {
                await _audioManager.PreviousSentenceAsync();
                _logger.LogDebug("PreviousSentence: Updated audio position to sentence {SentenceIndex}", newIndex);
            }
        }
    }

    async Task NextSentence()
    {
        // Handle case where no sentence is currently selected
        if (State.CurrentSentenceIndex == -1)
        {
            SetState(s => s.CurrentSentenceIndex = 0);
            return;
        }

        if (State.CurrentSentenceIndex < State.Sentences.Count - 1)
        {
            var newIndex = State.CurrentSentenceIndex + 1;

            // ALWAYS update the visual highlighting immediately for responsive UI
            SetState(s => s.CurrentSentenceIndex = newIndex);
            _logger.LogDebug("NextSentence: Updated visual highlighting to sentence {SentenceIndex}", newIndex);

            // If audio is playing and we have an audio manager, also update audio position
            if (_audioManager != null && State.IsAudioPlaying)
            {
                await _audioManager.NextSentenceAsync();
                _logger.LogDebug("NextSentence: Updated audio position to sentence {SentenceIndex}", newIndex);
            }
        }
    }

    void CyclePlaybackSpeed()
    {
        var speeds = new[] { 0.5f, 0.75f, 1.0f, 1.25f, 1.5f, 2.0f };
        var currentIndex = Array.IndexOf(speeds, State.PlaybackSpeed);
        var nextIndex = (currentIndex + 1) % speeds.Length;

        SetState(s => s.PlaybackSpeed = speeds[nextIndex]);
        Preferences.Set("ReadingActivity_PlaybackSpeed", State.PlaybackSpeed);
    }

    void IncreaseFontSize()
    {
        var newSize = Math.Min(State.FontSize + 2, 100.0); // Max font size 72 for better accessibility
        SetState(s =>
        {
            s.FontSize = newSize;
            // 🚀 PERFORMANCE: Invalidate cache when font size changes
            s.IsContentCached = false;
            s.IsStructuralCacheValid = false;
        });
        Preferences.Set("ReadingActivity_FontSize", State.FontSize);
    }

    void DecreaseFontSize()
    {
        var newSize = Math.Max(State.FontSize - 2, 32.0); // Min font size 12
        SetState(s =>
        {
            s.FontSize = newSize;
            // 🚀 PERFORMANCE: Invalidate cache when font size changes
            s.IsContentCached = false;
            s.IsStructuralCacheValid = false;
        });
        Preferences.Set("ReadingActivity_FontSize", State.FontSize);
    }

    string FormatPlaybackTime(double timeSeconds)
    {
        var time = TimeSpan.FromSeconds(timeSeconds);
        if (time.TotalHours >= 1)
        {
            return $"{time:h\\:mm\\:ss}";
        }
        else
        {
            return $"{time:m\\:ss}";
        }
    }

    async Task ClearAudioCache()
    {
        try
        {
            // Stop any current playback
            StopCurrentPlayback();

            // Show performance summary before clearing
            var summary = PerformanceLogger.GetPerformanceSummary();
            _logger.LogDebug("{PerformanceSummary}", summary);

            // Clear timestamped audio (real-time system doesn't use cache files)
            SetState(s =>
            {
                s.TimestampedAudio = null;
                s.IsTimestampedAudioLoaded = false;
                s.IsAudioPlaying = false;
                s.IsPlaying = false;
                s.CurrentPlaybackTime = 0.0;
                s.CurrentSentenceIndex = -1;

                // Clear performance cache too
                s.IsContentCached = false;
                s.IsStructuralCacheValid = false;
                s.CachedParagraphsByIndex.Clear();
                s.CachedParagraphSentences.Clear();
            });

            // Reset performance measurements
            PerformanceLogger.Reset();

            await AppShell.DisplayToastAsync("🏴‍☠️ Audio cache cleared! Fresh voices ahead, Captain!");
        }
        catch (Exception ex)
        {
            await AppShell.DisplayToastAsync($"Failed to clear cache: {ex.Message}");
        }
    }

    // Content Processing
    List<TextSegment> ParseSentenceForVocabularyAndWords(string sentence)
    {
        var segments = new List<TextSegment>();
        var words = sentence.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var word in words)
        {
            var vocabularyMatch = FindVocabularyMatch(word);

            if (vocabularyMatch != null)
            {
                segments.Add(new TextSegment
                {
                    Text = word,
                    IsVocabulary = true,
                    IsWord = true, // Vocabulary words are also tappable words
                    VocabularyWord = vocabularyMatch
                });
            }
            else
            {
                // Check if this is a word (contains letters) vs punctuation/whitespace
                var isWord = Regex.IsMatch(word, @"\p{L}"); // Unicode letter check

                segments.Add(new TextSegment
                {
                    Text = word,
                    IsVocabulary = false,
                    IsWord = isWord // Regular words can be tapped for dictionary lookup
                });
            }

            // Add space after each word except the last
            if (word != words.Last())
            {
                segments.Add(new TextSegment
                {
                    Text = " ",
                    IsVocabulary = false,
                    IsWord = false
                });
            }
        }

        return segments;
    }

    List<TextSegment> ParseSentenceForVocabulary(string sentence)
    {
        var segments = new List<TextSegment>();
        var words = sentence.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var word in words)
        {
            var vocabularyMatch = FindVocabularyMatch(word);

            if (vocabularyMatch != null)
            {
                segments.Add(new TextSegment
                {
                    Text = word,
                    IsVocabulary = true,
                    VocabularyWord = vocabularyMatch
                });
            }
            else
            {
                segments.Add(new TextSegment
                {
                    Text = word,
                    IsVocabulary = false
                });
            }

            // Add space after each word except the last
            if (word != words.Last())
            {
                segments.Add(new TextSegment { Text = " ", IsVocabulary = false });
            }
        }

        return segments;
    }

    VocabularyWord FindVocabularyMatch(string word)
    {
        var cleanWord = word.Trim().ToLowerInvariant()
            .TrimEnd('.', ',', '!', '?', ';', ':');

        return State.VocabularyWords?.FirstOrDefault(v =>
            v.TargetLanguageTerm?.ToLowerInvariant() == cleanWord ||
            v.TargetLanguageTerm?.ToLowerInvariant().Contains(cleanWord) == true
        );
    }

    // Vocabulary UI
    void ShowVocabularyBottomSheet(VocabularyWord vocabularyWord)
    {
        SetState(s =>
        {
            s.SelectedVocabulary = vocabularyWord;
            s.IsVocabularyBottomSheetVisible = true;
        });
    }

    void LookupWordInDictionary(string word)
    {
        // 🎯 NEW: Dictionary lookup for regular words using bottom sheet UI
        _ = Task.Run(async () =>
        {
            try
            {
                _logger.LogDebug("Looking up word: {Word}", word);

                // Clean the word - remove punctuation for better lookup
                var cleanWord = word.Trim().TrimEnd('.', ',', '!', '?', ':', ';', '"', '\'');

                // 🏴‍☠️ NEW: Get the current sentence for context
                string currentSentence = null;
                if (State.CurrentSentenceIndex >= 0 && State.CurrentSentenceIndex < State.Sentences.Count)
                {
                    currentSentence = State.Sentences[State.CurrentSentenceIndex];
                }

                // Show loading state in the dictionary bottom sheet
                SetState(s =>
                {
                    s.DictionaryWord = cleanWord;
                    s.DictionaryDefinition = null;
                    s.IsDictionaryBottomSheetVisible = true;
                    s.IsLookingUpWord = true;
                    s.CanRememberWord = false;
                    s.IsSavingWord = false;
                });

                // First check if we have this word in our local vocabulary
                var vocabularyWords = State.VocabularyWords;
                var localWord = vocabularyWords?.FirstOrDefault(v =>
                    v.TargetLanguageTerm.Equals(cleanWord, StringComparison.OrdinalIgnoreCase));

                if (localWord != null && !string.IsNullOrEmpty(localWord.NativeLanguageTerm))
                {
                    // Found in local vocabulary - show definition, no need to remember
                    _logger.LogDebug("Found local translation: {TargetTerm} = {NativeTerm}", localWord.TargetLanguageTerm, localWord.NativeLanguageTerm);
                    SetState(s =>
                    {
                        s.DictionaryDefinition = localWord.NativeLanguageTerm;
                        s.IsLookingUpWord = false;
                        s.CanRememberWord = false; // Already in vocabulary
                    });
                    return;
                }

                // Not found locally - use AI translation service with context
                _logger.LogDebug("Word not found locally, using AI translation for: {CleanWord} with context: {Context}", cleanWord, currentSentence ?? "(no context)");
                var translation = await _translationService.TranslateAsync(cleanWord, currentSentence);

                if (!string.IsNullOrEmpty(translation))
                {
                    _logger.LogDebug("AI translation found: {CleanWord} = {Translation}", cleanWord, translation);
                    SetState(s =>
                    {
                        s.DictionaryDefinition = translation;
                        s.IsLookingUpWord = false;
                        s.CanRememberWord = true; // Allow saving new word
                    });

                    // Record dictionary lookup activity
                    await _userActivityRepository.SaveAsync(new UserActivity
                    {
                        Activity = SentenceStudio.Shared.Models.Activity.Reading.ToString(),
                        Input = $"Dictionary lookup: {cleanWord}",
                        Accuracy = 100, // Successfully looked up word
                        Fluency = 100,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    });
                }
                else
                {
                    _logger.LogWarning("No translation found for: {CleanWord}", cleanWord);
                    SetState(s =>
                    {
                        s.DictionaryDefinition = $"{_localize["NoDefinitionFound"]}";
                        s.IsLookingUpWord = false;
                        s.CanRememberWord = false;
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in dictionary lookup for word: {Word}", word);
                SetState(s =>
                {
                    s.DictionaryDefinition = "Unable to lookup word definition";
                    s.IsLookingUpWord = false;
                    s.CanRememberWord = false;
                });
            }
        });
    }

    void CloseVocabularyBottomSheet()
    {
        SetState(s => s.IsVocabularyBottomSheetVisible = false);
    }

    void CloseDictionaryBottomSheet()
    {
        SetState(s => s.IsDictionaryBottomSheetVisible = false);
    }

    // 🏴‍☠️ NEW: Remember vocabulary word from dictionary lookup
    void RememberVocabularyWord()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                SetState(s => s.IsSavingWord = true);

                // Create new vocabulary word
                var newWord = new VocabularyWord
                {
                    TargetLanguageTerm = State.DictionaryWord,
                    NativeLanguageTerm = State.DictionaryDefinition,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                // Save to database
                var result = await _resourceRepository.SaveWordAsync(newWord);

                if (result > 0)
                {
                    // Add to current reading resource if we have one
                    if (State.Resource?.Id > 0)
                    {
                        await _resourceRepository.AddVocabularyToResourceAsync(State.Resource.Id, newWord.Id);

                        // Update local vocabulary list
                        SetState(s =>
                        {
                            if (s.VocabularyWords == null)
                                s.VocabularyWords = new List<VocabularyWord>();
                            s.VocabularyWords.Add(newWord);
                            s.IsSavingWord = false;
                            s.CanRememberWord = false; // Word is now remembered
                        });
                    }
                    else
                    {
                        SetState(s => s.IsSavingWord = false);
                    }

                    // Record vocabulary learning activity
                    await _userActivityRepository.SaveAsync(new UserActivity
                    {
                        Activity = SentenceStudio.Shared.Models.Activity.Reading.ToString(),
                        Input = $"Added vocabulary: {State.DictionaryWord}",
                        Accuracy = 100, // Successfully added word
                        Fluency = 100,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    });

                    await AppShell.DisplayToastAsync($"🏴‍☠️ Word '{State.DictionaryWord}' added to vocabulary, Captain!");
                }
                else
                {
                    SetState(s => s.IsSavingWord = false);
                    await AppShell.DisplayToastAsync("❌ Failed to save vocabulary word");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving vocabulary word: {Word}", State.DictionaryWord);
                SetState(s => s.IsSavingWord = false);
                await AppShell.DisplayToastAsync("❌ Error saving vocabulary word");
            }
        });
    }

    // 🏴‍☠️ NEW: Immersive scroll-based navigation hiding
    void OnScrollViewScrolled(object sender, ScrolledEventArgs e)
    {
        // Calculate scroll direction and distance
        var currentScrollY = e.ScrollY;
        var deltaY = currentScrollY - State.LastScrollY;

        // Only trigger changes if we've scrolled a significant amount
        if (Math.Abs(deltaY) > 5) // Small threshold to avoid excessive updates
        {
            var isScrollingDown = deltaY > 0;
            var shouldHideNavigation = isScrollingDown && currentScrollY > State.ScrollThreshold;
            var shouldShowNavigation = !isScrollingDown || currentScrollY <= 20; // Show when scrolling up or near top

            // Update navigation visibility if it needs to change
            if (shouldHideNavigation && State.IsNavigationVisible)
            {
                SetState(s =>
                {
                    s.IsNavigationVisible = false;
                    s.IsScrollingDown = true;
                });
                _logger.LogDebug("Hiding navigation - scrolled down past threshold");
            }
            else if (shouldShowNavigation && !State.IsNavigationVisible)
            {
                SetState(s =>
                {
                    s.IsNavigationVisible = true;
                    s.IsScrollingDown = false;
                });
                _logger.LogDebug("Showing navigation - scrolled up or near top");
            }

            // Update last scroll position
            SetState(s => s.LastScrollY = currentScrollY);
        }
    }

    // Navigation
    async Task GoBack()
    {
        StopCurrentPlayback();

        // Record reading activity when user finishes reading session
        if (State.Resource != null)
        {
            await _userActivityRepository.SaveAsync(new UserActivity
            {
                Activity = SentenceStudio.Shared.Models.Activity.Reading.ToString(),
                Input = $"Reading session: {State.Resource.Title}",
                Accuracy = 100, // Reading activity is considered successful
                Fluency = 100,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        // 🏴‍☠️ Ensure navigation is visible when leaving the page
        SetState(s => s.IsNavigationVisible = true);

        MauiControls.Shell.Current.GoToAsync("..");
    }

    // Lifecycle
    protected override void OnMounted()
    {
        base.OnMounted();

        if (Props?.Resource == null)
        {
            SetState(s => s.ErrorMessage = "No resource provided");
            return;
        }

        if (string.IsNullOrWhiteSpace(Props.Resource.Transcript))
        {
            SetState(s => s.ErrorMessage = "Resource has no transcript");
            return;
        }

        // Start activity timer if launched from Today's Plan
        if (Props?.FromTodaysPlan == true)
        {
            _timerService.StartSession("Reading", Props.PlanItemId);
        }
        // Removed: Initialization logic that called InitializeAudioSystemAsync
    }

    private async Task InitializeAudioSystemAsync()
    {
        if (State.Resource?.Transcript == null) return;

        // Show loading state
        SetState(s =>
        {
            s.IsGeneratingAudio = true;
            s.AudioGenerationStatus = "🏴‍☠️ Initializing audio system...";
            s.AudioGenerationProgress = 0.1;
        });

        _logger.LogDebug("InitializeAudioSystemAsync: Loading state set, should be visible now");

        // Add a small delay to ensure loading UI shows
        await Task.Delay(500);

        try
        {
            _logger.LogDebug("InitializeAudioSystemAsync: Starting audio system initialization");

            _timingCalculator = new SentenceTimingCalculator(_timingCalculatorLogger);
            _audioManager = new TimestampedAudioManager(_timingCalculator, _audioManagerLogger);

            // Update progress
            SetState(s =>
            {
                s.AudioGenerationStatus = "🏴‍☠️ Generating timestamped audio...";
                s.AudioGenerationProgress = 0.3;
            });

            // Generate timestamped audio for the entire resource
            _logger.LogDebug("InitializeAudioSystemAsync: Generating audio for transcript length: {TranscriptLength}", State.Resource.Transcript.Length);
            var timestampedAudioResult = await _speechService.GenerateTimestampedAudioAsync(State.Resource);

            if (timestampedAudioResult != null)
            {
                _logger.LogDebug("InitializeAudioSystemAsync: Generated audio with {CharacterCount} characters, duration: {Duration:F1}s", timestampedAudioResult.Characters.Length, timestampedAudioResult.Duration.TotalSeconds);

                // Update progress
                SetState(s =>
                {
                    s.AudioGenerationStatus = "🏴‍☠️ Processing character timestamps...";
                    s.AudioGenerationProgress = 0.7;
                });

                // Convert to the new TimestampedAudio model
                var timestampedAudio = new TimestampedAudio
                {
                    AudioData = timestampedAudioResult.AudioData.ToArray(),
                    Characters = timestampedAudioResult.Characters,
                    FullTranscript = State.Resource.Transcript,
                    Duration = timestampedAudioResult.Duration.TotalSeconds
                };

                // Update progress
                SetState(s =>
                {
                    s.AudioGenerationStatus = "🏴‍☠️ Loading audio into player...";
                    s.AudioGenerationProgress = 0.9;
                });

                // Load audio into manager (no pre-calculated timings needed!)
                await _audioManager.LoadAudioAsync(timestampedAudio);
                _audioManager.SentenceChanged += OnCurrentSentenceChanged;
                _audioManager.ProgressUpdated += OnProgressUpdated;

                // 🎯 CRITICAL: Use audio manager's sentence list (without PARAGRAPH_BREAK markers)
                // This ensures sentence indices match between UI and audio timing
                var audioSentences = _audioManager.GetSentences();
                _logger.LogInformation("🔍 Audio manager loaded {Count} sentences (excluding paragraph breaks)", audioSentences.Count);

                SetState(s =>
                {
                    // Replace the paragraph-aware sentence list with the audio manager's list
                    // Paragraph breaks are handled in rendering via GroupSentencesIntoParagraphs()
                    s.Sentences = audioSentences;
                });
                _audioManager.PlaybackEnded += OnPlaybackEnded;

                // Update state with loaded status
                SetState(s =>
                {
                    s.IsTimestampedAudioLoaded = true;
                    s.TimestampedAudio = timestampedAudio;
                    s.IsGeneratingAudio = false;
                    s.AudioGenerationStatus = "🏴‍☠️ Audio ready for playback!";
                    s.AudioGenerationProgress = 1.0;
                });

                _logger.LogDebug("🏴‍☠️ Real-time audio system initialized with character-level timestamps!");
            }
            else
            {
                _logger.LogDebug("🏴‍☠️ InitializeAudioSystemAsync: Failed to generate timestamped audio");
                SetState(s =>
                {
                    s.IsGeneratingAudio = false;
                    s.AudioGenerationStatus = "⚠️ Failed to generate audio";
                    s.AudioGenerationProgress = 0.0;
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🏴‍☠️ Error initializing audio");
            SetState(s =>
            {
                s.IsGeneratingAudio = false;
                s.AudioGenerationStatus = $"⚠️ Audio error: {ex.Message}";
                s.AudioGenerationProgress = 0.0;
            });
        }
    }

    private void OnCurrentSentenceChanged(object? sender, int sentenceIndex)
    {
        PerformanceLogger.StartTimer("OnCurrentSentenceChanged");
        _logger.LogDebug("🏴‍☠️ OnCurrentSentenceChanged: RECEIVED EVENT! New sentence index {SentenceIndex}", sentenceIndex);
        _logger.LogDebug("🏴‍☠️ OnCurrentSentenceChanged: Previous sentence index was {PreviousIndex}", State.CurrentSentenceIndex);

        // Only update if the sentence index actually changed to avoid unnecessary re-renders
        if (State.CurrentSentenceIndex != sentenceIndex)
        {
            SetState(s =>
            {
                _logger.LogDebug("🏴‍☠️ OnCurrentSentenceChanged: Setting state from {OldIndex} to {NewIndex}", s.CurrentSentenceIndex, sentenceIndex);
                s.CurrentSentenceIndex = sentenceIndex;
            });

            _logger.LogDebug("🏴‍☠️ OnCurrentSentenceChanged: State updated, current sentence is now {CurrentIndex}", State.CurrentSentenceIndex);
        }
        else
        {
            _logger.LogDebug("🏴‍☠️ OnCurrentSentenceChanged: No change needed, already at sentence {SentenceIndex}", sentenceIndex);
        }

        PerformanceLogger.EndTimer("OnCurrentSentenceChanged", 5.0); // Warn if > 5ms
    }

    private void OnProgressUpdated(object? sender, double currentTime)
    {
        SetState(s => s.CurrentPlaybackTime = currentTime);
    }

    private void OnPlaybackEnded(object? sender, EventArgs e)
    {
        _logger.LogDebug("🏴‍☠️ OnPlaybackEnded: Playback finished");
        SetState(s => s.IsPlaying = false);
    }

    protected override void OnWillUnmount()
    {
        base.OnWillUnmount();

        // Pause timer when leaving activity
        if (Props?.FromTodaysPlan == true && _timerService.IsActive)
        {
            _timerService.Pause();
        }

        // Clean up audio resources
        StopCurrentPlayback();

        // Clean up TimestampedAudioManager
        if (_audioManager != null)
        {
            _audioManager.SentenceChanged -= OnCurrentSentenceChanged;
            _audioManager.ProgressUpdated -= OnProgressUpdated;
            _audioManager.PlaybackEnded -= OnPlaybackEnded;
            _audioManager.Dispose();
        }
    }

    async Task LoadContentAsync()
    {
        TrySetShellTitleView();

        SetState(s => s.IsBusy = true);

        try
        {
            // Load complete resource with vocabulary
            var fullResource = await _resourceRepository.GetResourceAsync(Props.Resource.Id);

            SetState(s =>
            {
                s.Resource = fullResource;
                // Sentences will be set by InitializeAudioSystemAsync after audio loads
                s.VocabularyWords = fullResource.Vocabulary ?? new List<VocabularyWord>();
                s.FontSize = Preferences.Get("ReadingActivity_FontSize", 18.0);
                s.PlaybackSpeed = Preferences.Get("ReadingActivity_PlaybackSpeed", 1.0f);
                s.HasShownJumpHint = Preferences.Get("ReadingActivity_HasShownJumpHint", false);
                s.HasDismissedInstructions = Preferences.Get("ReadingActivity_HasDismissedInstructions", false);
                s.IsBusy = false;

                // 🚀 PERFORMANCE: Invalidate cache when new content is loaded
                s.IsContentCached = false;
                s.IsStructuralCacheValid = false;
            });

            // Initialize timestamped audio system
            await InitializeAudioSystemAsync();
        }
        catch (Exception ex)
        {
            SetState(s =>
            {
                s.ErrorMessage = $"Failed to load content: {ex.Message}";
                s.IsBusy = false;
            });
        }
    }
}