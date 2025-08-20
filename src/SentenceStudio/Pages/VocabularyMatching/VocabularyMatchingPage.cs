using SentenceStudio.Pages.Dashboard;
using System.Collections.ObjectModel;
using MauiReactor.Shapes;
using ReactorCustomLayouts;
using System.Diagnostics;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Pages.VocabularyMatching;

public class WordWithProgress
{
    public VocabularyWord Word { get; set; }
    public SentenceStudio.Shared.Models.VocabularyProgress Progress { get; set; }
}

public class MatchingTile
{
    public int Id { get; set; }
    public string Text { get; set; }
    public string Language { get; set; } // "native" or "target"
    public int VocabularyWordId { get; set; }
    public bool IsSelected { get; set; }
    public bool IsMatched { get; set; }
    public bool IsVisible { get; set; } = true;
    
    // Enhanced progress tracking
    public SentenceStudio.Shared.Models.VocabularyProgress? Progress { get; set; }
    public VocabularyWord? Word { get; set; }
    
    // Enhanced computed properties
    public bool IsKnown => Progress?.IsKnown ?? false;
    public float MasteryProgress => Progress?.MasteryScore ?? 0f;
}

class VocabularyMatchingPageState
{
    public bool IsBusy { get; set; }
    public List<MatchingTile> Tiles { get; set; } = new();
    public List<MatchingTile> SelectedTiles { get; set; } = new();
    public int MatchedPairs { get; set; }
    public int TotalPairs { get; set; }
    public int IncorrectGuesses { get; set; }
    public bool IsGameComplete { get; set; }
    public string GameMessage { get; set; } = "";
    public bool HideNativeWordsMode { get; set; } = true; // New setting for the requested feature
}

partial class VocabularyMatchingPage : Component<VocabularyMatchingPageState, ActivityProps>
{
    [Inject] LearningResourceRepository _resourceRepo;
    [Inject] UserActivityRepository _userActivityRepository;
    [Inject] VocabularyProgressService _progressService;

    // Enhanced tracking: Response timer for measuring user response time
    private Stopwatch _responseTimer = new Stopwatch();

    LocalizationManager _localize => LocalizationManager.Instance;

    public override VisualNode Render()
    {
        return ContentPage(_localize["VocabularyMatchingTitle"].ToString(),
            ToolbarItem(_localize["NewGame"].ToString())
                .OnClicked(RestartGame),
            ToolbarItem(State.HideNativeWordsMode ? _localize["ShowAllWords"].ToString() : _localize["HideNativeWords"].ToString())
                .OnClicked(ToggleHideNativeWordsMode),
            Grid(rows: "Auto, *", columns: "*",
                // Header with progress
                RenderHeader(),
                
                // Game area
                ContentView(
                    State.IsBusy ? 
                        RenderLoading() :
                        State.IsGameComplete ? 
                            RenderGameComplete() :
                            RenderGameBoard()
                ).GridRow(1)
            ).RowSpacing(12)
        )
        .OnAppearing(LoadVocabulary);
    }

    VisualNode RenderHeader() =>
        VStack(spacing: 2,
            Label(string.Format(_localize["MatchedAndMisses"].ToString(), State.MatchedPairs, State.TotalPairs, State.IncorrectGuesses))
                .HCenter(),

            State.GameMessage.Length > 0 ?
                Label(State.GameMessage)
                    .ThemeKey(MyTheme.Caption1)
                    .HCenter()
                    .TextColor(MyTheme.HighlightDarkest) :
                null
        )
        .Padding(MyTheme.Size60);

    VisualNode RenderLoading() =>
        VStack(spacing: 16,
            ActivityIndicator()
                .IsRunning(true)
                .HCenter(),
            Label(_localize["LoadingVocabulary"].ToString())
                .HCenter()
        )
        .VCenter()
        .HCenter();

    VisualNode RenderGameComplete() =>
        VStack(spacing: 20,
            Label(_localize["Congratulations"].ToString())
                .ThemeKey(MyTheme.Title1)
                .HCenter(),
            
            Label(_localize["AllPairsMatched"].ToString())
                .HCenter(),
                
            Button(_localize["PlayAgain"].ToString())
                .OnClicked(RestartGame)
                .HCenter()
        )
        .VCenter()
        .HCenter()
        .Padding(40);


    VisualNode RenderGameBoard()
    {
        // Arrr, fill the available space with a responsive grid!
        int tileCount = State.Tiles.Count;
        if (tileCount == 0)
            return Grid();

        // Determine columns based on device/orientation
        int columns = 4;
        int rows = 4;

        if (DeviceInfo.Idiom == DeviceIdiom.Phone)
        {
            if (DeviceDisplay.Current.MainDisplayInfo.Orientation == DisplayOrientation.Portrait)
            {
                columns = 2;
            }
            else
            {
                columns = 4;
            }
        }
        else // Desktop/tablet
        {
            if (DeviceDisplay.Current.MainDisplayInfo.Orientation == DisplayOrientation.Portrait)
            {
                columns = 3;
            }
            else
            {
                columns = 4;
            }
        }

        // Calculate rows needed
        rows = (int)Math.Ceiling((double)tileCount / columns);

        // Build row/column definitions
        string rowDef = string.Join(",", Enumerable.Repeat("*", rows));
        string colDef = string.Join(",", Enumerable.Repeat("*", columns));

        var gridChildren = new List<VisualNode>();
        for (int i = 0; i < tileCount; i++)
        {
            int row = i / columns;
            int col = i % columns;
            gridChildren.Add(
                RenderTile(State.Tiles[i], row, col)
            );
        }

        return Grid(rows: rowDef, columns: colDef,
            gridChildren.ToArray()
        )
        .RowSpacing(8)
        .ColumnSpacing(8)
        .Padding(MyTheme.Size160)
        .VFill()
        .HFill();
    }

    // Tile size be fixed for all, so it don't change when selected!
    double GetTileFontSize()
    {
        if (DeviceInfo.Idiom == DeviceIdiom.Phone && DeviceDisplay.Current.MainDisplayInfo.Orientation == DisplayOrientation.Portrait)
            return 13;
        return 16;
    }

    double GetTileHeight()
    {
        if (DeviceInfo.Idiom == DeviceIdiom.Phone && DeviceDisplay.Current.MainDisplayInfo.Orientation == DisplayOrientation.Portrait)
            return 56;
        return 80;
    }

    double GetTileWidth()
    {
        if (DeviceInfo.Idiom == DeviceIdiom.Phone && DeviceDisplay.Current.MainDisplayInfo.Orientation == DisplayOrientation.Portrait)
            return 120;
        return 150;
    }

    VisualNode RenderTile(MatchingTile tile, int row, int col)
    {
        double fontSize = GetTileFontSize();
        double height = GetTileHeight();
        double width = GetTileWidth();

        // If tile is not visible, render an empty placeholder to maintain grid layout
        if (!tile.IsVisible)
        {
            return ContentView()
                .GridRow(row)
                .GridColumn(col);
        }

        return Border(
            Label(tile.Text)
                .FontSize(fontSize)
                .HCenter()
                .VCenter()
                .TextColor(GetTileTextColor(tile))
        )
        .BackgroundColor(GetTileBackgroundColor(tile))
        .StrokeShape(new RoundRectangle().CornerRadius(8))
        .StrokeThickness(1)
        .Stroke(GetTileBorderColor(tile))
        .OnTapped(() => OnTileTapped(tile))
        .Opacity(tile.IsMatched ? 0.3 : 1.0)
        .GridRow(row)
        .GridColumn(col);
    }

    // Footer removed; actions moved to ToolbarItems, arrr!

    Color GetTileBackgroundColor(MatchingTile tile)
    {
        if (tile.IsMatched)
            return MyTheme.Gray200;
        if (tile.IsSelected)
            return MyTheme.HighlightDarkest;
        if (tile.Language == "native")
            return MyTheme.LightBackground;
        return MyTheme.HighlightMedium;
    }

    Color GetTileTextColor(MatchingTile tile)
    {
        if (tile.IsSelected)
            return Colors.White;

        // Arrr, make sure the text be visible in both light and dark seas!
        // If tile is matched, use a more muted color
        if (tile.IsMatched)
        {
            return Theme.IsLightTheme ? MyTheme.Gray500 : MyTheme.Gray300;
        }

        // For native tiles, use the right contrast for the background
        if (tile.Language == "native")
        {
            return Theme.IsLightTheme ? MyTheme.DarkOnLightBackground : MyTheme.LightOnDarkBackground;
        }
        // For target tiles, use the right contrast for secondary background
        return Theme.IsLightTheme ? MyTheme.DarkOnLightBackground : MyTheme.LightOnDarkBackground;
    }

    Color GetTileBorderColor(MatchingTile tile)
    {
        if (tile.IsSelected)
            return MyTheme.HighlightDarkest;
        if (tile.IsMatched)
            return MyTheme.Gray400;
        return MyTheme.Gray300;
    }

    async Task LoadVocabulary()
    {
        if (State.Tiles.Count > 0) return; // Already loaded

        SetState(s => {
            s.IsBusy = true;
            s.GameMessage = "";
        });

        try
        {
            List<VocabularyWord> words = new List<VocabularyWord>();

            // Combine vocabulary from all selected resources
            if (Props.Resources?.Any() == true)
            {
                foreach (var resourceRef in Props.Resources)
                {
                    if (resourceRef?.Id != -1)
                    {
                        var resource = await _resourceRepo.GetResourceAsync(resourceRef.Id);
                        if (resource?.Vocabulary?.Any() == true)
                        {
                            words.AddRange(resource.Vocabulary);
                        }
                    }
                }
            }
            
            // If no words from resources, get some default words for demo
            if (words.Count == 0)
            {
                SetState(s => {
                    s.IsBusy = false;
                    s.GameMessage = _localize["NoVocabularyAvailable"].ToString();
                });
                return;
            }

            // Remove duplicates based on both native and target terms
            words = words.Where(w => !string.IsNullOrWhiteSpace(w.NativeLanguageTerm) && 
                                   !string.IsNullOrWhiteSpace(w.TargetLanguageTerm))
                         .GroupBy(w => new { Native = w.NativeLanguageTerm?.Trim(), Target = w.TargetLanguageTerm?.Trim() })
                         .Select(g => g.First())
                         .ToList();
            
            if (words.Count == 0)
            {
                SetState(s => {
                    s.IsBusy = false;
                    s.GameMessage = "No vocabulary available. Please add words to your vocabulary list.";
                });
                return;
            }

            // Get progress for all words using enhanced tracking
            var wordIds = words.Select(w => w.Id).ToList();
            var progressDict = await _progressService.GetProgressForWordsAsync(wordIds);

            // Prioritize words that need more practice
            var wordsWithProgress = words.Select(word => new WordWithProgress
            {
                Word = word,
                Progress = progressDict[word.Id]
            }).ToList();

            // Filter and prioritize words for matching activity
            var selectedWords = SelectWordsForMatching(wordsWithProgress).Take(8).ToList();
            
            // Create tiles with enhanced progress information
            var tiles = await CreateTilesFromWordsAsync(selectedWords);
            
            SetState(s => {
                s.Tiles = tiles;
                s.TotalPairs = selectedWords.Count;
                s.MatchedPairs = 0;
                s.IsBusy = false;
                s.IsGameComplete = false;
                s.GameMessage = s.HideNativeWordsMode ? 
                    _localize["TapTargetToReveal"].ToString() : 
                    _localize["MatchPairs"].ToString();
            });
        }
        catch (Exception ex)
        {
            SetState(s => {
                s.IsBusy = false;
                s.GameMessage = _localize["ErrorLoadingVocabulary"].ToString();
            });
            System.Diagnostics.Debug.WriteLine($"Error loading vocabulary: {ex.Message}");
        }
    }

    private List<WordWithProgress> SelectWordsForMatching(List<WordWithProgress> wordsWithProgress)
    {
        // Prioritize words that need practice in recognition phase
        // or that are learning but not yet mastered
        return wordsWithProgress
            .Where(wp => !wp.Progress.IsKnown) // Don't include already mastered words
            .OrderBy(wp => wp.Progress.MasteryScore) // Prioritize words that need more practice
            .ThenBy(wp => wp.Progress.CurrentPhase) // Recognition phase first
            .ThenBy(_ => Guid.NewGuid()) // Random for variety
            .ToList();
    }

    async Task<List<MatchingTile>> CreateTilesFromWordsAsync(List<WordWithProgress> wordsWithProgress)
    {
        var tiles = new List<MatchingTile>();
        int tileId = 0;

        // Create tiles for native language terms
        foreach (var wp in wordsWithProgress)
        {
            var word = wp.Word;
            var progress = wp.Progress;
            
            if (!string.IsNullOrWhiteSpace(word.NativeLanguageTerm))
            {
                tiles.Add(new MatchingTile
                {
                    Id = tileId++,
                    Text = word.NativeLanguageTerm.Trim(),
                    Language = "native",
                    VocabularyWordId = word.Id,
                    IsVisible = !State.HideNativeWordsMode,
                    Progress = progress,
                    Word = word
                });
            }
        }

        // Create tiles for target language terms
        foreach (var wp in wordsWithProgress)
        {
            var word = wp.Word;
            var progress = wp.Progress;
            
            if (!string.IsNullOrWhiteSpace(word.TargetLanguageTerm))
            {
                tiles.Add(new MatchingTile
                {
                    Id = tileId++,
                    Text = word.TargetLanguageTerm.Trim(),
                    Language = "target",
                    VocabularyWordId = word.Id,
                    IsVisible = true,
                    Progress = progress,
                    Word = word
                });
            }
        }

        // Shuffle the tiles
        var random = new Random();
        for (int i = tiles.Count - 1; i > 0; i--)
        {
            int j = random.Next(0, i + 1);
            (tiles[i], tiles[j]) = (tiles[j], tiles[i]);
        }

        return tiles;
    }

    List<MatchingTile> CreateTilesFromWords(List<VocabularyWord> words)
    {
        var tiles = new List<MatchingTile>();
        int tileId = 0;

        // Create tiles for native language terms
        foreach (var word in words)
        {
            if (!string.IsNullOrWhiteSpace(word.NativeLanguageTerm))
            {
                tiles.Add(new MatchingTile
                {
                    Id = tileId++,
                    Text = word.NativeLanguageTerm.Trim(),
                    Language = "native",
                    VocabularyWordId = word.Id,
                    IsVisible = !State.HideNativeWordsMode // Hide native words initially if mode is enabled
                });
            }
        }

        // Create tiles for target language terms
        foreach (var word in words)
        {
            if (!string.IsNullOrWhiteSpace(word.TargetLanguageTerm))
            {
                tiles.Add(new MatchingTile
                {
                    Id = tileId++,
                    Text = word.TargetLanguageTerm.Trim(),
                    Language = "target",
                    VocabularyWordId = word.Id,
                    IsVisible = true // Target words are always visible initially
                });
            }
        }

        // Shuffle the tiles
        var random = new Random();
        for (int i = tiles.Count - 1; i > 0; i--)
        {
            int j = random.Next(0, i + 1);
            (tiles[i], tiles[j]) = (tiles[j], tiles[i]);
        }

        return tiles;
    }

    void OnTileTapped(MatchingTile tile)
    {
        if (tile.IsMatched || State.IsGameComplete || !tile.IsVisible)
            return;

        // In hide native words mode, only allow target words to be selected first
        if (State.HideNativeWordsMode && State.SelectedTiles.Count == 0 && tile.Language == "native")
            return;

        if (tile.IsSelected)
        {
            // Deselect the tile
            DeselectTile(tile);
            return;
        }

        if (State.SelectedTiles.Count >= 2)
        {
            // Already have 2 tiles selected, ignore tap
            SetState(s => s.GameMessage = _localize["WaitCheckingMatch"].ToString());
            return;
        }

        // Enhanced tracking: Start response timer when first tile is selected
        if (State.SelectedTiles.Count == 0)
        {
            _responseTimer.Restart();
        }

        // Select the tile
        SelectTile(tile);

        // If in hide native words mode and a target language word was selected, show native words
        if (State.HideNativeWordsMode && tile.Language == "target" && State.SelectedTiles.Count == 1)
        {
            SetState(s => {
                foreach (var nativeTile in s.Tiles.Where(t => t.Language == "native" && !t.IsMatched))
                {
                    nativeTile.IsVisible = true;
                }
            });
        }

        if (State.SelectedTiles.Count == 2)
        {
            // Check for match after a short delay for visual feedback
            Task.Run(async () =>
            {
                await Task.Delay(800);
                MainThread.BeginInvokeOnMainThread(CheckForMatch);
            });
        }
    }

    void SelectTile(MatchingTile tile)
    {
        SetState(s => {
            var tileToUpdate = s.Tiles.First(t => t.Id == tile.Id);
            tileToUpdate.IsSelected = true;
            s.SelectedTiles.Add(tileToUpdate);
            
            if (s.SelectedTiles.Count == 1)
                s.GameMessage = _localize["SelectAnotherTile"].ToString();
            else if (s.SelectedTiles.Count == 2)
                s.GameMessage = _localize["CheckingMatch"].ToString();
        });
    }

    void DeselectTile(MatchingTile tile)
    {
        SetState(s => {
            var tileToUpdate = s.Tiles.First(t => t.Id == tile.Id);
            tileToUpdate.IsSelected = false;
            s.SelectedTiles.RemoveAll(t => t.Id == tile.Id);
            s.GameMessage = s.HideNativeWordsMode && s.SelectedTiles.Count == 0 ? 
                _localize["TapTargetToReveal"].ToString() : 
                _localize["MatchPairs"].ToString();
            
            // If in hide native words mode and we're deselecting the last tile, hide native words again
            if (s.HideNativeWordsMode && s.SelectedTiles.Count == 0)
            {
                foreach (var nativeTile in s.Tiles.Where(t => t.Language == "native" && !t.IsMatched))
                {
                    nativeTile.IsVisible = false;
                }
            }
        });
    }

    async void CheckForMatch()
    {
        if (State.SelectedTiles.Count != 2)
            return;

        var tile1 = State.SelectedTiles[0];
        var tile2 = State.SelectedTiles[1];

        bool isMatch = tile1.VocabularyWordId == tile2.VocabularyWordId && 
                       tile1.Language != tile2.Language;

        // Enhanced tracking: Stop response timer
        _responseTimer.Stop();

        // Find the vocabulary word being matched
        var matchedWord = tile1.Word ?? tile2.Word;
        if (matchedWord != null)
        {
            await RecordEnhancedMatchActivity(matchedWord, isMatch);
        }
        else
        {
            // Fallback to legacy recording
            await RecordMatchActivity(isMatch);
        }

        if (isMatch)
        {
            // It's a match!
            SetState(s => {
                var tileToUpdate1 = s.Tiles.First(t => t.Id == tile1.Id);
                var tileToUpdate2 = s.Tiles.First(t => t.Id == tile2.Id);
                tileToUpdate1.IsMatched = true;
                tileToUpdate1.IsSelected = false;
                tileToUpdate2.IsMatched = true;
                tileToUpdate2.IsSelected = false;
                s.SelectedTiles.Clear();
                s.MatchedPairs++;
                s.GameMessage = _localize["GreatMatch"].ToString();
                
                // Hide native words again if in hide mode
                if (s.HideNativeWordsMode)
                {
                    foreach (var nativeTile in s.Tiles.Where(t => t.Language == "native" && !t.IsMatched))
                    {
                        nativeTile.IsVisible = false;
                    }
                }
                
                // Check if game is complete
                if (s.MatchedPairs >= s.TotalPairs)
                {
                    s.IsGameComplete = true;
                    s.GameMessage = "";
                    // Show all words for the final congratulations view
                    foreach (var anyTile in s.Tiles)
                    {
                        anyTile.IsVisible = true;
                    }
                }
            });
            
        }
        else
        {
            // Not a match
            SetState(s => {
                var tileToUpdate1 = s.Tiles.First(t => t.Id == tile1.Id);
                var tileToUpdate2 = s.Tiles.First(t => t.Id == tile2.Id);
                tileToUpdate1.IsSelected = false;
                tileToUpdate2.IsSelected = false;
                s.SelectedTiles.Clear();
                s.IncorrectGuesses++;
                s.GameMessage = _localize["NotAMatch"].ToString();
                
                // Hide native words again if in hide mode
                if (s.HideNativeWordsMode)
                {
                    foreach (var nativeTile in s.Tiles.Where(t => t.Language == "native" && !t.IsMatched))
                    {
                        nativeTile.IsVisible = false;
                    }
                }
            });
        }
    }

    private async Task RecordEnhancedMatchActivity(VocabularyWord word, bool isCorrect)
    {
        try
        {
            // Get current resource ID for context tracking
            var currentResourceId = GetCurrentResourceId();
            
            // Create detailed attempt record
            var attempt = new VocabularyAttempt
            {
                VocabularyWordId = word.Id,
                UserId = GetCurrentUserId(),
                Activity = "VocabularyMatching",
                InputMode = InputMode.MultipleChoice.ToString(), // Matching is essentially multiple choice
                WasCorrect = isCorrect,
                DifficultyWeight = 0.8f, // Matching is slightly easier than text entry
                ContextType = "Isolated", // Words are shown in isolation
                LearningResourceId = currentResourceId,
                UserInput = isCorrect ? "correct_match" : "incorrect_match",
                ExpectedAnswer = $"{word.NativeLanguageTerm} = {word.TargetLanguageTerm}",
                ResponseTimeMs = (int)_responseTimer.ElapsedMilliseconds,
                UserConfidence = null // Not captured in matching activity
            };
            
            // Record attempt using enhanced service
            var updatedProgress = await _progressService.RecordAttemptAsync(attempt);
            
            // Update the tiles with new progress
            var tilesForWord = State.Tiles.Where(t => t.VocabularyWordId == word.Id).ToList();
            foreach (var tile in tilesForWord)
            {
                tile.Progress = updatedProgress;
            }
            
            System.Diagnostics.Debug.WriteLine($"Enhanced progress tracking: Word {word.Id} - Correct: {isCorrect}, Mastery: {updatedProgress.MasteryScore:F2}");
        }
        catch (Exception ex)
        {
            // Log error but don't break game flow
            System.Diagnostics.Debug.WriteLine($"Error recording enhanced match activity: {ex.Message}");
            
            // Fallback to legacy recording
            await RecordMatchActivity(isCorrect);
        }
    }

    private int GetCurrentUserId()
    {
        // For now, return default user ID
        // In a real implementation, this would come from user authentication
        return 1;
    }

    private int? GetCurrentResourceId()
    {
        // Try to get the first resource ID from Props.Resources
        if (Props.Resources?.Any() == true)
        {
            return Props.Resources.First().Id;
        }
        
        // Fallback to Props.Resource for backward compatibility
        return Props.Resource?.Id;
    }

    void RestartGame()
    {
        SetState(s => {
            s.Tiles.Clear();
            s.SelectedTiles.Clear();
            s.MatchedPairs = 0;
            s.TotalPairs = 0;
            s.IncorrectGuesses = 0;
            s.IsGameComplete = false;
            s.GameMessage = "";
        });
        LoadVocabulary();
    }

    void ToggleHideNativeWordsMode()
    {
        SetState(s => {
            s.HideNativeWordsMode = !s.HideNativeWordsMode;
            
            // Update visibility of native tiles based on new mode
            if (s.HideNativeWordsMode)
            {
                // Hide native words unless a target word is currently selected
                bool hasTargetSelected = s.SelectedTiles.Any(t => t.Language == "target");
                foreach (var nativeTile in s.Tiles.Where(t => t.Language == "native" && !t.IsMatched))
                {
                    nativeTile.IsVisible = hasTargetSelected;
                }
            }
            else
            {
                // Show all native words in traditional mode
                foreach (var nativeTile in s.Tiles.Where(t => t.Language == "native"))
                {
                    nativeTile.IsVisible = true;
                }
            }
        });
    }

    async Task NavigateBack()
    {
        await MauiControls.Shell.Current.GoToAsync("..");
    }

    async Task RecordMatchActivity(bool isCorrect)
    {
        try
        {
            // Get the target language word from the selected tiles
            var targetLanguageWord = State.SelectedTiles.FirstOrDefault(t => t.Language == "target")?.Text ?? "unknown";
            
            var activity = new UserActivity
            {
                Activity = SentenceStudio.Shared.Models.Activity.VocabularyMatching.ToString(),
                Input = targetLanguageWord,
                Accuracy = isCorrect ? 100 : 0,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            };

            await _userActivityRepository.SaveAsync(activity);
        }
        catch (Exception ex)
        {
            // Log error but don't break game flow
            System.Diagnostics.Debug.WriteLine($"Error recording match activity: {ex.Message}");
        }
    }

}