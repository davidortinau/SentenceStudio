using SentenceStudio.Pages.Dashboard;
using System.Collections.ObjectModel;
using MauiReactor.Shapes;
using ReactorCustomLayouts;

namespace SentenceStudio.Pages.VocabularyMatching;

public class MatchingTile
{
    public int Id { get; set; }
    public string Text { get; set; }
    public string Language { get; set; } // "native" or "target"
    public int VocabularyWordId { get; set; }
    public bool IsSelected { get; set; }
    public bool IsMatched { get; set; }
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
    public bool IsProgressiveRevealMode { get; set; } = false;
    public bool AreNativeWordsVisible { get; set; } = true;
}

partial class VocabularyMatchingPage : Component<VocabularyMatchingPageState, ActivityProps>
{
    [Inject] LearningResourceRepository _resourceRepo;
    [Inject] UserActivityRepository _userActivityRepository;
    [Inject] UserProfileRepository _userProfileRepository;

    LocalizationManager _localize => LocalizationManager.Instance;

    public override VisualNode Render()
    {
        return ContentPage(_localize["VocabularyMatchingTitle"].ToString(),
            ToolbarItem(_localize["NewGame"].ToString())
                .OnClicked(RestartGame),
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
                    .Style((Style)Application.Current.Resources["Caption1"])
                    .HCenter()
                    .TextColor(ApplicationTheme.Primary) :
                null
        )
        .Padding(ApplicationTheme.Size60);

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
                .Style((Style)Application.Current.Resources["Title1"])
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
        .Padding(ApplicationTheme.Size160)
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

        // In progressive reveal mode, hide native language tiles if they should not be visible
        bool shouldHideTile = State.IsProgressiveRevealMode && 
                              tile.Language == "native" && 
                              !State.AreNativeWordsVisible && 
                              !tile.IsMatched;

        return Border(
            // Grid(
                Label(shouldHideTile ? "?" : tile.Text)
                    .FontSize(fontSize)
                    .HCenter()
                    .VCenter()
                    .TextColor(GetTileTextColor(tile, shouldHideTile))
            // )
            // .HeightRequest(height)
            // .WidthRequest(width)
        )
        .BackgroundColor(GetTileBackgroundColor(tile, shouldHideTile))
        .StrokeShape(new RoundRectangle().CornerRadius(8))
        .StrokeThickness(1)
        .Stroke(GetTileBorderColor(tile, shouldHideTile))
        .OnTapped(() => OnTileTapped(tile))
        .Opacity(tile.IsMatched ? 0.3 : (shouldHideTile ? 0.5 : 1.0))
        .GridRow(row)
        .GridColumn(col);
    }

    // Footer removed; actions moved to ToolbarItems, arrr!

    Color GetTileBackgroundColor(MatchingTile tile, bool shouldHideTile = false)
    {
        if (shouldHideTile)
            return ApplicationTheme.Gray300;
        if (tile.IsMatched)
            return ApplicationTheme.Gray200;
        if (tile.IsSelected)
            return ApplicationTheme.Primary;
        if (tile.Language == "native")
            return ApplicationTheme.LightBackground;
        return ApplicationTheme.Secondary;
    }

    Color GetTileTextColor(MatchingTile tile, bool shouldHideTile = false)
    {
        if (shouldHideTile)
            return ApplicationTheme.Gray600;
        if (tile.IsSelected)
            return Colors.White;

        // Arrr, make sure the text be visible in both light and dark seas!
        // If tile is matched, use a more muted color
        if (tile.IsMatched)
        {
            return Theme.IsLightTheme ? ApplicationTheme.Gray500 : ApplicationTheme.Gray300;
        }

        // For native tiles, use the right contrast for the background
        if (tile.Language == "native")
        {
            return Theme.IsLightTheme ? ApplicationTheme.DarkOnLightBackground : ApplicationTheme.LightOnDarkBackground;
        }
        // For target tiles, use the right contrast for secondary background
        return Theme.IsLightTheme ? ApplicationTheme.DarkOnLightBackground : ApplicationTheme.LightOnDarkBackground;
    }

    Color GetTileBorderColor(MatchingTile tile, bool shouldHideTile = false)
    {
        if (shouldHideTile)
            return ApplicationTheme.Gray400;
        if (tile.IsSelected)
            return ApplicationTheme.Primary;
        if (tile.IsMatched)
            return ApplicationTheme.Gray400;
        return ApplicationTheme.Gray300;
    }

    async Task LoadVocabulary()
    {
        if (State.Tiles.Count > 0) return; // Already loaded

        SetState(s => {
            s.IsBusy = true;
            s.GameMessage = "";
        });

        // Load user's progressive reveal setting
        var userProfile = await _userProfileRepository.GetOrCreateDefaultAsync();
        bool progressiveRevealMode = userProfile.VocabularyProgressiveReveal;

        try
        {
            List<VocabularyWord> words = new List<VocabularyWord>();

            // Try to get vocabulary from the resource first
            if (Props.Resource?.Id != -1)
            {
                var resource = await _resourceRepo.GetResourceAsync(Props.Resource.Id);
                words = resource.Vocabulary;
            }
            
            // If no words from resource, get some default words for demo
            if (words.Count == 0)
            {
                SetState(s => {
                    s.IsBusy = false;
                    s.GameMessage = _localize["NoVocabularyAvailable"].ToString();
                });
                return;
            }

            // Shuffle the words so each game gets a new random set
            words = words.Where(w => !string.IsNullOrWhiteSpace(w.NativeLanguageTerm) && 
                                   !string.IsNullOrWhiteSpace(w.TargetLanguageTerm))
                         .OrderBy(_ => Guid.NewGuid())
                         .Take(8)
                         .ToList();
            
            if (words.Count == 0)
            {
                SetState(s => {
                    s.IsBusy = false;
                    s.GameMessage = "No vocabulary available. Please add words to your vocabulary list.";
                });
                return;
            }

            var tiles = CreateTilesFromWords(words);
            
            SetState(s => {
                s.Tiles = tiles;
                s.TotalPairs = words.Count;
                s.MatchedPairs = 0;
                s.IsBusy = false;
                s.IsGameComplete = false;
                s.GameMessage = _localize["MatchPairs"].ToString();
                s.IsProgressiveRevealMode = progressiveRevealMode;
                s.AreNativeWordsVisible = !progressiveRevealMode; // Hide native words initially if progressive reveal is on
            });
        }
        catch (Exception ex)
        {
            SetState(s => {
                s.IsBusy = false;
                s.GameMessage = _localize["ErrorLoadingVocabulary"].ToString();
            });
        }
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
                    VocabularyWordId = word.Id
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
                    VocabularyWordId = word.Id
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
        if (tile.IsMatched || State.IsGameComplete)
            return;

        // In progressive reveal mode, prevent tapping hidden native tiles
        if (State.IsProgressiveRevealMode && 
            tile.Language == "native" && 
            !State.AreNativeWordsVisible &&
            !tile.IsMatched)
        {
            return;
        }

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

        // Select the tile
        SelectTile(tile);

        // In progressive reveal mode, show native words when target word is selected
        if (State.IsProgressiveRevealMode && 
            tile.Language == "target" && 
            !State.AreNativeWordsVisible)
        {
            SetState(s => s.AreNativeWordsVisible = true);
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
            s.GameMessage = _localize["MatchPairs"].ToString();
            
            // In progressive reveal mode, hide native words again if no tiles are selected
            if (s.IsProgressiveRevealMode && s.SelectedTiles.Count == 0)
            {
                s.AreNativeWordsVisible = false;
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

        if (isMatch)
        {
            // Record successful match activity
            await RecordMatchActivity(true);

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
                
                // In progressive reveal mode, hide native words again after successful match
                if (s.IsProgressiveRevealMode)
                {
                    s.AreNativeWordsVisible = false;
                }
                
                // Check if game is complete
                if (s.MatchedPairs >= s.TotalPairs)
                {
                    s.IsGameComplete = true;
                    s.GameMessage = "";
                }
            });
            
        }
        else
        {
            // Record unsuccessful match activity
            await RecordMatchActivity(false);
            
            // Not a match
            SetState(s => {
                var tileToUpdate1 = s.Tiles.First(t => t.Id == tile1.Id);
                var tileToUpdate2 = s.Tiles.First(t => t.Id == tile2.Id);
                tileToUpdate1.IsSelected = false;
                tileToUpdate2.IsSelected = false;
                s.SelectedTiles.Clear();
                s.IncorrectGuesses++;
                s.GameMessage = _localize["NotAMatch"].ToString();
                
                // In progressive reveal mode, hide native words again after failed match
                if (s.IsProgressiveRevealMode)
                {
                    s.AreNativeWordsVisible = false;
                }
            });
            
        }
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
            // Reset progressive reveal state - this will be set again in LoadVocabulary
            s.IsProgressiveRevealMode = false;
            s.AreNativeWordsVisible = true;
        });
        LoadVocabulary();
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