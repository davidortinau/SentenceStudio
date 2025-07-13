using MauiReactor.Shapes;

namespace SentenceStudio.Pages.LearningResources;

class EditLearningResourceState
{
    public LearningResource Resource { get; set; } = new();
    public bool IsLoading { get; set; } = true;
    public bool IsEditing { get; set; } = false;
    public bool IsVocabularySheetVisible { get; set; } = false;
    public VocabularyWord CurrentVocabularyWord { get; set; } = new();
    public bool IsAddingNewWord { get; set; } = false;
    public int MediaTypeIndex { get; set; } = 0;
    public int LanguageIndex { get; set; } = 0;
    public bool IsGeneratingVocabulary { get; set; } = false;
}

partial class EditLearningResourcePage : Component<EditLearningResourceState, ResourceProps>
{
    [Inject] LearningResourceRepository _resourceRepo;
    [Inject] VocabularyService _vocabService;
    [Inject] AiService _aiService;
    
    LocalizationManager _localize => LocalizationManager.Instance;

    public override VisualNode Render()
    {
        return ContentPage(State.Resource.Title ?? $"{_localize["Resource"]}",
            ToolbarItem("Edit").OnClicked(() => SetState(s => s.IsEditing = true)),
                // .IsVisible(!State.IsEditing),
            ToolbarItem("Save").OnClicked(SaveResource),
                // .IsVisible(State.IsEditing),
            ToolbarItem("Cancel").OnClicked(() => SetState(s => s.IsEditing = false)),
                // .IsVisible(State.IsEditing),
            ToolbarItem("Delete").OnClicked(DeleteResource),
                // .IsVisible(!State.IsEditing),
                
            Grid(
                State.IsLoading ? 
                    VStack(
                        ActivityIndicator().IsRunning(true)
                    )
                    .VCenter()
                    .HCenter() :
                    Grid(
                        ScrollView(
                            VStack(
                                State.IsEditing ? 
                                    RenderEditMode() : 
                                    RenderViewMode()
                            )
                            .Padding(new Thickness(15))
                            .Spacing(15)
                        ),
                        
                        // Vocabulary word editor overlay
                        State.IsVocabularySheetVisible ?
                            RenderVocabularyWordEditor() :
                            null
                    )
            )
        ).OnAppearing(LoadResource);
    }

    private VisualNode RenderVocabularyWordEditor()
    {
        return new VocabularyWordEditorSheet(
            State.CurrentVocabularyWord, 
            State.IsAddingNewWord,
            SaveVocabularyWord,
            () => SetState(s => s.IsVocabularySheetVisible = false)
        );
    }

    private VisualNode RenderViewMode()
    {
        return VStack(
            // Show media content based on type
            // RenderMediaContent(),
            
            // Title
            Label(State.Resource.Title ?? string.Empty)
                .FontSize(24)
                .FontAttributes(FontAttributes.Bold),
                
            // Description
            Label(State.Resource.Description ?? string.Empty)
                .FontSize(16),
                
            // Metadata
            Border(
                Grid(rows: "Auto, Auto, Auto, Auto, Auto", columns: "Auto, *",
                    Label("Type:")
                        .FontAttributes(FontAttributes.Bold)
                        .GridRow(0)
                        .GridColumn(0),
                    Label(State.Resource.MediaType ?? string.Empty)
                        .GridRow(0)
                        .GridColumn(1),

                    Label("Language:")
                        .FontAttributes(FontAttributes.Bold)
                        .GridRow(1)
                        .GridColumn(0),
                    Label(State.Resource.Language ?? string.Empty)
                        .GridRow(1)
                        .GridColumn(1),

                    Label("Url:")
                        .FontAttributes(FontAttributes.Bold)
                        .GridRow(2)
                        .GridColumn(0),
                    Label(State.Resource.MediaUrl ?? string.Empty)
                        .GridRow(2)
                        .GridColumn(1)
                        .TextDecorations(TextDecorations.Underline)
                        .OnTapped(() => 
                            {
                                if (!string.IsNullOrEmpty(State.Resource.MediaUrl))
                                {
                                    Launcher.OpenAsync(State.Resource.MediaUrl);
                                }
                            }),

                    Label("Created:")
                        .FontAttributes(FontAttributes.Bold)
                        .GridRow(3)
                        .GridColumn(0),
                    Label(State.Resource.CreatedAt.ToString("g"))
                        .GridRow(3)
                        .GridColumn(1),

                    Label("Updated:")
                        .FontAttributes(FontAttributes.Bold)
                        .GridRow(4)
                        .GridColumn(0),
                    Label(State.Resource.UpdatedAt.ToString("g"))
                        .GridRow(4)
                        .GridColumn(1)
                )
                .RowSpacing(5)
                .ColumnSpacing(10)
            )
            .Stroke(Colors.LightGray),
            
            // Transcript if available
            !string.IsNullOrEmpty(State.Resource.Transcript) ?
                VStack(
                    Label("Transcript")
                        .FontAttributes(FontAttributes.Bold)
                        .FontSize(18),
                        
                    Border(
                        Label(State.Resource.Transcript)
                    )
                    .Stroke(Colors.LightGray)
                    .Padding(10)
                )
                .Spacing(10) :
                null,
                
            // Translation if available
            !string.IsNullOrEmpty(State.Resource.Translation) ?
                VStack(
                    Label("Translation")
                        .FontAttributes(FontAttributes.Bold)
                        .FontSize(18),
                        
                    Border(
                        Label(State.Resource.Translation)
                    )
                    .Stroke(Colors.LightGray)
                    .Padding(10)
                )
                .Spacing(10) :
                null,
                
            // Vocabulary section if this is a vocabulary list or has vocabulary
            State.Resource.Vocabulary?.Count > 0 ?
                VStack(
                    Grid(
                        Label("Vocabulary")
                            .FontAttributes(FontAttributes.Bold)
                            .FontSize(18)
                            .GridColumn(0),
                            
                        HStack(
                            Button("Generate")
                                .ThemeKey("Secondary")
                                .OnClicked(GenerateVocabulary)
                                .IsEnabled(!State.IsGeneratingVocabulary)
                                .Opacity(State.IsGeneratingVocabulary ? 0.5 : 1.0),
                                
                            ActivityIndicator()
                                .IsRunning(State.IsGeneratingVocabulary)
                                .IsVisible(State.IsGeneratingVocabulary)
                                .Scale(0.8)
                        )
                        .Spacing(10)
                        .GridColumn(1)
                        .HEnd()
                    )
                    .Columns("*, Auto"),
                        
                    CollectionView()
                        .SelectionMode(SelectionMode.None)
                        .ItemsSource(State.Resource.Vocabulary, word => 
                            Border(
                                Grid(
                                    VStack(
                                        Label(word.TargetLanguageTerm)
                                            .FontAttributes(FontAttributes.Bold)
                                            .FontSize(16),
                                            
                                        Label(word.NativeLanguageTerm)
                                            .FontSize(14)
                                    )
                                    .Spacing(5)
                                )
                                .Padding(10)
                            )
                            .Stroke(Colors.LightGray)
                            .StrokeThickness(1)
                            .StrokeShape(new RoundRectangle().CornerRadius(5))
                            .Margin(new Thickness(0, 0, 0, 5))
                        )
                )
                .Spacing(10) :
                VStack(
                    Grid(
                        Label("Vocabulary")
                            .FontAttributes(FontAttributes.Bold)
                            .FontSize(18)
                            .GridColumn(0),
                            
                        HStack(
                            Button("Generate")
                                .ThemeKey("Secondary")
                                .OnClicked(GenerateVocabulary)
                                .IsEnabled(!State.IsGeneratingVocabulary)
                                .Opacity(State.IsGeneratingVocabulary ? 0.5 : 1.0),
                                
                            ActivityIndicator()
                                .IsRunning(State.IsGeneratingVocabulary)
                                .IsVisible(State.IsGeneratingVocabulary)
                                .Scale(0.8)
                        )
                        .Spacing(10)
                        .GridColumn(1)
                        .HEnd()
                    )
                    .Columns("*, Auto"),
                    
                    State.IsGeneratingVocabulary ?
                        Label("Analyzing transcript and generating vocabulary...")
                            .FontSize(14)
                            .TextColor(Colors.Gray) :
                        Label("No vocabulary words have been added yet.")
                            .FontSize(14)
                            .TextColor(Colors.Gray)
                )
                .Spacing(10)
        )
        .Spacing(15);
    }

    private VisualNode RenderEditMode()
    {
        return VStack(
            // Title
            VStack(
                Label("Title")
                    .FontAttributes(FontAttributes.Bold)
                    .HStart(),
                Border(
                    Entry()
                        .Text(State.Resource.Title)
                        .OnTextChanged(text => SetState(s => s.Resource.Title = text))
                )
                .ThemeKey(ApplicationTheme.InputWrapper)
            )
            .Spacing(5),
            
            // Description
            VStack(
                Label("Description")
                    .FontAttributes(FontAttributes.Bold)
                    .HStart(),
                Border(
                    Editor()
                        .Text(State.Resource.Description)
                        .OnTextChanged(text => SetState(s => s.Resource.Description = text))
                        .HeightRequest(100)
                )
                .ThemeKey(ApplicationTheme.InputWrapper)
            )
            .Spacing(5),
            
            // Media Type
            VStack(
                Label("Media Type")
                    .FontAttributes(FontAttributes.Bold)
                    .HStart(),
                new SfTextInputLayout(
                    Picker()
                        .ItemsSource(Constants.MediaTypes)
                        .SelectedIndex(State.MediaTypeIndex)
                        .OnSelectedIndexChanged(index => SetState(s => {
                            s.MediaTypeIndex = index;
                            s.Resource.MediaType = Constants.MediaTypes[index];
                        }))
                )
                .Hint("Media Type")
            )
            .Spacing(5),
            
            // Language
            VStack(
                Label("Language")
                    .FontAttributes(FontAttributes.Bold)
                    .HStart(),
                new SfTextInputLayout(
                    Picker()
                        .ItemsSource(Constants.Languages)
                        .SelectedIndex(State.LanguageIndex)
                        .OnSelectedIndexChanged(index => SetState(s => {
                            s.LanguageIndex = index; 
                            s.Resource.Language = Constants.Languages[index];
                        }))
                )
                .Hint("Language")
            )
            .Spacing(5),
            
            // Media URL
            VStack(
                Label("Media URL")
                    .FontAttributes(FontAttributes.Bold)
                    .HStart(),
                Border(
                    Entry()
                        .Text(State.Resource.MediaUrl)
                        .OnTextChanged(text => SetState(s => s.Resource.MediaUrl = text))
                        .Keyboard(Keyboard.Url)
                )
                .ThemeKey(ApplicationTheme.InputWrapper)
            )
            .Spacing(5),
            
            // Transcript
            VStack(
                Label("Transcript")
                    .FontAttributes(FontAttributes.Bold)
                    .HStart(),
                Border(
                    Editor()
                        .Text(State.Resource.Transcript)
                        .OnTextChanged(text => SetState(s => s.Resource.Transcript = text))
                        .HeightRequest(150)
                )
                .ThemeKey(ApplicationTheme.InputWrapper)
            )
            .Spacing(5),
            
            // Translation
            VStack(
                Label("Translation")
                    .FontAttributes(FontAttributes.Bold)
                    .HStart(),
                Border(
                    Editor()
                        .Text(State.Resource.Translation)
                        .OnTextChanged(text => SetState(s => s.Resource.Translation = text))
                        .HeightRequest(150)
                )
                .ThemeKey(ApplicationTheme.InputWrapper)
            )
            .Spacing(5),
            
            // Tags
            VStack(
                Label("Tags (comma separated)")
                    .FontAttributes(FontAttributes.Bold)
                    .HStart(),
                Border(
                    Entry()
                        .Text(State.Resource.Tags)
                        .OnTextChanged(text => SetState(s => s.Resource.Tags = text))
                )
                .ThemeKey(ApplicationTheme.InputWrapper)
            )
            .Spacing(5),
            
            // Vocabulary section
            VStack(
                Grid(
                    Label("Vocabulary")
                        .FontAttributes(FontAttributes.Bold)
                        .FontSize(18)
                        .GridColumn(0),
                        
                    HStack(
                        Button("+ Add Word")
                            .ThemeKey("Secondary")
                            .OnClicked(AddVocabularyWord),

                        Button("Generate")
                            .ThemeKey("Secondary")
                            .OnClicked(GenerateVocabulary)
                            .IsEnabled(!State.IsGeneratingVocabulary)
                            .Opacity(State.IsGeneratingVocabulary ? 0.5 : 1.0),
                            
                        ActivityIndicator()
                            .IsRunning(State.IsGeneratingVocabulary)
                            .IsVisible(State.IsGeneratingVocabulary)
                            .Scale(0.8)
                    )
                    .Spacing(10)
                    .GridColumn(1)
                    .HEnd()
                )
                .Columns("*, Auto"),
                
                CollectionView()
                    .SelectionMode(SelectionMode.None)
                    .ItemsSource(State.Resource.Vocabulary, word => 
                        Border(
                            Grid(
                                VStack(
                                    Label(word.TargetLanguageTerm)
                                        .FontAttributes(FontAttributes.Bold)
                                        .FontSize(16),
                                            
                                    Label(word.NativeLanguageTerm)
                                        .FontSize(14)
                                )
                                .Spacing(5)
                                .GridColumn(0),
                                
                                HStack(
                                    Button("Edit")
                                        .OnClicked(() => EditVocabularyWord(word))
                                        .ThemeKey("Secondary"),

                                    Button("Delete")
                                        .OnClicked(() => DeleteVocabularyWord(word))
                                        .ThemeKey("Secondary")
                                )
                                .GridColumn(1)
                                .HEnd()
                                .Spacing(5)
                            )
                            .Columns("*, Auto")
                            .Padding(10)
                        )
                        .Stroke(Colors.LightGray)
                        .StrokeThickness(1)
                        .StrokeShape(new RoundRectangle().CornerRadius(5))
                        .Margin(new Thickness(0, 0, 0, 5))
                    )
            )
            .Spacing(5)
        )
        .Spacing(15);
    }

    async Task LoadResource()
    {
        if (Props.ResourceID == 0)
        {
            // Creating a new resource
            SetState(s => {
                s.Resource = new LearningResource {
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                s.IsLoading = false;
                s.IsEditing = true;
            });
            return;
        }
        
        SetState(s => s.IsLoading = true);
        
        var resource = await _resourceRepo.GetResourceAsync(Props.ResourceID);
        
        // Set the indexes for the pickers
        int mediaTypeIndex = Array.IndexOf(Constants.MediaTypes, resource.MediaType);
        int languageIndex = Array.IndexOf(Constants.Languages, resource.Language);
        
        SetState(s => {
            s.Resource = resource;
            s.MediaTypeIndex = mediaTypeIndex >= 0 ? mediaTypeIndex : 0;
            s.LanguageIndex = languageIndex >= 0 ? languageIndex : 0;
            s.IsLoading = false;
        });
    }
    
    async Task SaveResource()
    {
        SetState(s => s.IsLoading = true);
        
        // Validate required fields
        if (string.IsNullOrWhiteSpace(State.Resource.Title))
        {
            await App.Current.MainPage.DisplayAlert("Validation Error", "Title is required", "OK");
            SetState(s => s.IsLoading = false);
            return;
        }
        
        if (string.IsNullOrWhiteSpace(State.Resource.MediaType))
        {
            SetState(s => s.Resource.MediaType = "Other");
        }
        
        if (string.IsNullOrWhiteSpace(State.Resource.Language))
        {
            SetState(s => s.Resource.Language = "Other");
        }
        
        // Save the resource
        await _resourceRepo.SaveResourceAsync(State.Resource);
        
        SetState(s => {
            s.IsLoading = false;
            s.IsEditing = false;
        });
        
        // If this is a new resource, navigate back to the list
        if (Props.ResourceID == 0)
        {
            await MauiControls.Shell.Current.GoToAsync("..");
        }
    }
    
    async Task DeleteResource()
    {
        bool confirm = await App.Current.MainPage.DisplayAlert(
            "Confirm Delete", 
            "Are you sure you want to delete this resource?", 
            "Yes", "No");
            
        if (confirm)
        {
            SetState(s => s.IsLoading = true);
            
            await _resourceRepo.DeleteResourceAsync(State.Resource);
            
            SetState(s => s.IsLoading = false);
            
            await MauiControls.Shell.Current.GoToAsync("..");
        }
    }
    
    void AddVocabularyWord()
    {
        SetState(s => {
            s.CurrentVocabularyWord = new VocabularyWord();
            s.IsAddingNewWord = true;
            s.IsVocabularySheetVisible = true;
        });
    }
    
    void EditVocabularyWord(VocabularyWord word)
    {
        SetState(s => {
            s.CurrentVocabularyWord = new VocabularyWord 
            {
                Id = word.Id,
                TargetLanguageTerm = word.TargetLanguageTerm,
                NativeLanguageTerm = word.NativeLanguageTerm
            };
            s.IsAddingNewWord = false;
            s.IsVocabularySheetVisible = true;
        });
    }
    
    void SaveVocabularyWord(VocabularyWord word)
    {
        SetState(s => {
            if (s.IsAddingNewWord)
            {
                // Add new word to the list
                if (s.Resource.Vocabulary == null)
                {
                    s.Resource.Vocabulary = new List<VocabularyWord>();
                }
                s.Resource.Vocabulary.Add(word);
            }
            else
            {
                // Update existing word
                var index = s.Resource.Vocabulary.FindIndex(w => w.Id == word.Id);
                if (index >= 0)
                {
                    s.Resource.Vocabulary[index] = word;
                }
            }
            s.IsVocabularySheetVisible = false;
        });
    }
    
    void DeleteVocabularyWord(VocabularyWord word)
    {
        SetState(s => s.Resource.Vocabulary.Remove(word));
    }
    
    async Task GenerateVocabulary()
    {
        // Check if there's a transcript to analyze
        if (string.IsNullOrWhiteSpace(State.Resource.Transcript))
        {
            await App.Current.MainPage.DisplayAlert("Error", "No transcript available to generate vocabulary from", "OK");
            return;
        }
        
        SetState(s => s.IsGeneratingVocabulary = true);
        
        try
        {
            string prompt = $@"
You are a language learning assistant. Given the following transcript in {State.Resource.Language}, 
create a vocabulary list with important and useful words or phrases.
For each word, provide:
1. The word or phrase in {State.Resource.Language} (TargetLanguageTerm)
2. The English translation (NativeLanguageTerm)
Include a variety of nouns, verbs, adjectives, and useful expressions.
Format your response as a valid JSON array of objects.

Transcript:
{State.Resource.Transcript}
";

            var vocabulary = await _aiService.SendPrompt<List<VocabularyWord>>(prompt);
            
            if (vocabulary != null && vocabulary.Any())
            {
                SetState(s => {
                    if (s.Resource.Vocabulary == null)
                    {
                        s.Resource.Vocabulary = new List<VocabularyWord>();
                    }
                    
                    // Add the new words, avoiding duplicates
                    foreach (var word in vocabulary)
                    {
                        if (!s.Resource.Vocabulary.Any(w => 
                            w.TargetLanguageTerm == word.TargetLanguageTerm &&
                            w.NativeLanguageTerm == word.NativeLanguageTerm))
                        {
                            word.CreatedAt = DateTime.UtcNow;
                            word.UpdatedAt = DateTime.UtcNow;
                            s.Resource.Vocabulary.Add(word);
                        }
                    }
                    
                    s.IsGeneratingVocabulary = false;
                });
                
                await App.Current.MainPage.DisplayAlert("Success", $"Generated {vocabulary.Count} vocabulary items", "OK");
            }
            else
            {
                SetState(s => s.IsGeneratingVocabulary = false);
                await App.Current.MainPage.DisplayAlert("Error", "Failed to generate vocabulary", "OK");
            }
        }
        catch (Exception ex)
        {
            SetState(s => s.IsGeneratingVocabulary = false);
            await App.Current.MainPage.DisplayAlert("Error", $"An error occurred: {ex.Message}", "OK");
        }
    }
}