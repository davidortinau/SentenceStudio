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
}

partial class VocabularyMatchingPage : Component<VocabularyMatchingPageState, ActivityProps>
{
    [Inject] VocabularyService _vocabularyService;
    [Inject] UserActivityRepository _userActivityRepository;

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
                ScrollView(
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
        VStack(spacing: 8,
            Label(string.Format(_localize["MatchedAndMisses"].ToString(), State.MatchedPairs, State.TotalPairs, State.IncorrectGuesses))
                .HCenter(),

            State.GameMessage.Length > 0 ?
                Label(State.GameMessage)
                    .Style((Style)Application.Current.Resources["Caption1"])
                    .HCenter()
                    .TextColor(ApplicationTheme.Primary) :
                null
        )
        .Padding(ApplicationTheme.Size160);

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

    VisualNode RenderGameBoard() =>
        Grid(
            new HWrap()
            {
                    State.Tiles.Select(RenderTile)
            }.Spacing(8)
             
        )
        .Padding(ApplicationTheme.Size160);

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

    VisualNode RenderTile(MatchingTile tile)
    {
        double fontSize = GetTileFontSize();
        double height = GetTileHeight();
        double width = GetTileWidth();

        return Border(
            Grid(
                Label(tile.Text)
                    .FontSize(fontSize)
                    .HCenter()
                    .VCenter()
                    .TextColor(GetTileTextColor(tile))
            )
            .HeightRequest(height)
            .WidthRequest(width)
        )
        .BackgroundColor(GetTileBackgroundColor(tile))
        .StrokeShape(new RoundRectangle().CornerRadius(8))
        .StrokeThickness(1)
        .Stroke(GetTileBorderColor(tile))
        .OnTapped(() => OnTileTapped(tile))
        .Opacity(tile.IsMatched ? 0.3 : 1.0);
    }

    // Footer removed; actions moved to ToolbarItems, arrr!

    Color GetTileBackgroundColor(MatchingTile tile)
    {
        if (tile.IsMatched)
            return ApplicationTheme.Gray200;
        if (tile.IsSelected)
            return ApplicationTheme.Primary;
        if (tile.Language == "native")
            return ApplicationTheme.LightBackground;
        return ApplicationTheme.Secondary;
    }

    Color GetTileTextColor(MatchingTile tile)
    {
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

    Color GetTileBorderColor(MatchingTile tile)
    {
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

        try
        {
            List<VocabularyWord> words = new List<VocabularyWord>();
            
            // Try to get vocabulary from the resource first
            if (Props.Resource?.Vocabulary?.Count > 0)
            {
                words = Props.Resource.Vocabulary;
            }
            // Try to get from old vocabulary list ID if available
            else if (Props.Resource?.OldVocabularyListID > 0)
            {
                var vocabularyList = await _vocabularyService.GetListAsync(Props.Resource.OldVocabularyListID.Value);
                words = vocabularyList?.Words ?? new List<VocabularyWord>();
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
                    VocabularyWordId = word.ID
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
                    VocabularyWordId = word.ID
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
                // Check if game is complete
                if (s.MatchedPairs >= s.TotalPairs)
                {
                    s.IsGameComplete = true;
                    s.GameMessage = "";
                }
            });
            // Record successful match activity
            await RecordMatchActivity(true);
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
            });
            // Record unsuccessful match activity
            await RecordMatchActivity(false);
        }
        
        // If game is complete, record overall game completion
        if (State.IsGameComplete)
        {
            await RecordGameCompletion();
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
            var activity = new UserActivity
            {
                Activity = "VocabularyMatching",
                Input = "match_attempt",
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

    async Task RecordGameCompletion()
    {
        try
        {
            var accuracy = State.TotalPairs > 0 ? (double)State.MatchedPairs / State.TotalPairs * 100 : 0;
            
            var activity = new UserActivity
            {
                Activity = "VocabularyMatching",
                Input = $"game_completed_{State.TotalPairs}_pairs",
                Accuracy = accuracy,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            };

            await _userActivityRepository.SaveAsync(activity);
        }
        catch (Exception ex)
        {
            // Log error but don't break game flow
            System.Diagnostics.Debug.WriteLine($"Error recording game completion: {ex.Message}");
        }
    }
}