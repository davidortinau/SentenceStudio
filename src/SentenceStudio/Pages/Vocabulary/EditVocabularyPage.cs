using Fonts;

namespace SentenceStudio.Pages.Vocabulary;

class EditVocabularyPageState
{
    public List<VocabularyWord> Words { get; set; } = [];
    public string VocabListName { get; set; } = string.Empty;
    public int ListId { get; set; }
    public List<VocabularyWord> SearchResults { get; set; } = [];
    public int CurrentSearchIndex { get; set; }
    public bool IsSearchVisible { get; set; }
    public string SearchText { get; set; } = string.Empty;
}

partial class EditVocabularyPage : Component<EditVocabularyPageState, VocabProps>
{
    [Inject] VocabularyService _vocabService;
    LocalizationManager _localize => LocalizationManager.Instance;

    public override VisualNode Render()
    {
        return ContentPage(State.VocabListName,
                Grid(
                    CollectionView()
                        .Header(RenderHeader())
                        .ItemsSource(State.Words, RenderWordItem)
                        .SelectionMode(Microsoft.Maui.Controls.SelectionMode.None),

                    RenderSearchBottomSheet()
                ),
                ToolbarItem("Find").OnClicked(() => SetState(s => s.IsSearchVisible = true)),
                ToolbarItem("Add").OnClicked(AddVocab),
                ToolbarItem("Save").OnClicked(SaveVocab),
                ToolbarItem("Delete").OnClicked(DeleteList)
            ).OnAppearing(async () => await LoadList());
    }

    private VisualNode RenderHeader() =>
        VStack(
            VStack(
                Label($"{_localize["ListName"]}").HStart(),
                Border(
                    Entry()
                        .Text(State.VocabListName)
                        .OnTextChanged(text => SetState(s => s.VocabListName = text))
                )
                .Style((Style)Application.Current.Resources["InputWrapper"])
            )
            .Spacing((Double)Application.Current.Resources["size120"]),

            VStack(
                Label($"{_localize["Vocabulary"]}").HStart(),
                Grid(
                    Label($"{_localize["Term"]}"),
                    Label($"{_localize["Translation"]}").GridColumn(1)
                )
                .Columns("*,*")
                .Padding((Double)Application.Current.Resources["size240"])
                .ColumnSpacing((Double)Application.Current.Resources["size240"])
            )
            .Spacing((Double)Application.Current.Resources["size120"])
        )
        .Spacing((Double)Application.Current.Resources["size120"])
        .Padding((Double)Application.Current.Resources["size240"]);

    private VisualNode RenderWordItem(VocabularyWord word) =>
        Grid(
            Border(
                Entry()
                    .Text(word.TargetLanguageTerm)
                    .OnTextChanged(text => word.TargetLanguageTerm = text)
            ).Style((Style)Application.Current.Resources["InputWrapper"]),
            
            Border(
                Entry()
                    .Text(word.NativeLanguageTerm)
                    .OnTextChanged(text => word.NativeLanguageTerm = text)
            )
            .Style((Style)Application.Current.Resources["InputWrapper"])
            .GridColumn(1),
            
            Button()
                .BackgroundColor(Colors.Transparent)
                .GridColumn(2)
                .VCenter()
                .ImageSource(SegoeFluentIcons.Delete.ToImageSource())
                .OnClicked(() => DeleteWord(word))
                // .AppThemeColor(
                //     (Color)Application.Current.Resources["DarkOnLightBackground"],
                //     (Color)Application.Current.Resources["LightOnDarkBackground"]
                // )
        )
        .Padding((Double)Application.Current.Resources["size240"])
        .ColumnSpacing((Double)Application.Current.Resources["size240"])
        .Columns("*,*,auto");

    private VisualNode RenderSearchBottomSheet() =>
        Grid(
            Button($"{_localize["Done"]}")
                .BackgroundColor(Colors.Transparent)
                .OnClicked(() => SetState(s => 
                { 
                    s.IsSearchVisible = false;
                    s.SearchResults.Clear();
                    s.CurrentSearchIndex = 0;
                    s.SearchText = string.Empty;
                })),
                // .AppThemeColor(
                //     (Color)Application.Current.Resources["DarkOnLightBackground"],
                //     (Color)Application.Current.Resources["LightOnDarkBackground"]
                // ),

            Border(
                Grid(
                    Entry()
                        .Placeholder("Search...")
                        .IsSpellCheckEnabled(false)
                        .IsTextPredictionEnabled(false)
                        .ReturnType(ReturnType.Search)
                        .Text(State.SearchText)
                        .OnTextChanged(SearchWords),

                    Label($"{State.CurrentSearchIndex + 1} of {State.SearchResults.Count}")
                        .FontSize(10)
                        .Margin(new Thickness(0, 0, 8, 0))
                        .GridColumn(1)
                        .HEnd()
                        .VCenter()
                )
                .Columns("*,auto")
                .ColumnSpacing(4)
            )
            .GridColumn(1),

            HStack(
                Button()
                    .BackgroundColor(Colors.Transparent)
                    .ImageSource(SegoeFluentIcons.ChevronUp.ToImageSource())
                    .VCenter()
                    .HeightRequest(40).WidthRequest(40)
                    .OnClicked(() => NavigateSearch(false)),

                Button()
                    .BackgroundColor(Colors.Transparent)
                    .ImageSource(SegoeFluentIcons.ChevronDown.ToImageSource())
                    .HeightRequest(40).WidthRequest(40)
                    .VCenter()
                    .OnClicked(() => NavigateSearch(true))
                    // .AppThemeColor(
                    //     (Color)Application.Current.Resources["LightOnDarkBackground"],
                    //     (Color)Application.Current.Resources["DarkOnLightBackground"]
                    // )
            )
            .HEnd()
            .GridColumn(2)
        )
        .Padding(new Thickness(4, 8))
        .Columns("Auto,*,Auto")
        .ColumnSpacing(4)
        // .AppThemeColor(
        //     (Color)Application.Current.Resources["Gray100"],
        //     (Color)Application.Current.Resources["Gray900"]
        // )
        .VEnd()
        .IsVisible(State.IsSearchVisible);

    private async Task LoadList()
    {
        var list = await _vocabService.GetListAsync(Props.ListID);
        SetState(s => 
        {
            s.VocabListName = list.Name;
            s.Words = list.Words;
        });
    }

    private void SearchWords(string searchText)
    {
        SetState(s =>
        {
            s.SearchText = searchText;
            s.SearchResults = s.Words.Where(w => 
                w.TargetLanguageTerm.Contains(searchText, StringComparison.OrdinalIgnoreCase)).ToList();
            s.CurrentSearchIndex = s.SearchResults.Any() ? 0 : -1;
        });

        if (State.SearchResults.Any())
        {
            ScrollToWord(State.SearchResults[0]);
        }
    }

    private void NavigateSearch(bool forward)
    {
        SetState(s =>
        {
            if (forward)
                s.CurrentSearchIndex = (s.CurrentSearchIndex + 1) % s.SearchResults.Count;
            else
                s.CurrentSearchIndex = s.CurrentSearchIndex == 0 ? 
                    s.SearchResults.Count - 1 : s.CurrentSearchIndex - 1;
        });

        if (State.SearchResults.Any())
        {
            ScrollToWord(State.SearchResults[State.CurrentSearchIndex]);
        }
    }

    private void ScrollToWord(VocabularyWord word)
    {
        // Note: MauiReactor doesn't currently expose CollectionView.ScrollTo
        // This would need to be implemented via platform specific code or wait for MauiReactor support
    }

    private async Task SaveVocab()
    {
        var list = new VocabularyList
        {
            ID = State.ListId,
            Name = State.VocabListName,
            Words = State.Words
        };
        
        await _vocabService.SaveListAsync(list);
        await MauiControls.Shell.Current.GoToAsync("..");
    }

    private async Task DeleteList()
    {
        var list = new VocabularyList
        {
            ID = State.ListId,
            Name = State.VocabListName,
            Words = State.Words
        };
        
        await _vocabService.DeleteListAsync(list);
        await MauiControls.Shell.Current.GoToAsync("..");
    }

    private async Task DeleteWord(VocabularyWord word)
    {
        SetState(s => s.Words = s.Words.Where(w => w != word).ToList());
        await _vocabService.DeleteWordAsync(word);
        await _vocabService.DeleteWordFromListAsync(word, State.ListId);
    }

    private async Task AddVocab()
    {
        var word = new VocabularyWord();
        SetState(s => s.Words = new[] { word }.Concat(s.Words).ToList());
        await _vocabService.SaveWordAsync(word);
        await _vocabService.SaveWordToListAsync(word, State.ListId);
    }
}