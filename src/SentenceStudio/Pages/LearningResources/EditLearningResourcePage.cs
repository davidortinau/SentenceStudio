using MauiReactor.Shapes;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;
using SentenceStudio.Services;
using LukeMauiFilePicker;
using SentenceStudio.Pages.VocabularyProgress;

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
    public string VocabList { get; set; } = string.Empty;
    public string Delimiter { get; set; } = "comma";
}

partial class EditLearningResourcePage : Component<EditLearningResourceState, ResourceProps>
{
    [Inject] LearningResourceRepository _resourceRepo;
    [Inject] AiService _aiService;
    [Inject] IFilePickerService _picker;
    
    // Track whether we need to save resource relationship (only for new words)
    private bool _shouldSaveResourceRelationship = false;
    
    LocalizationManager _localize => LocalizationManager.Instance;
    
    static readonly Dictionary<DevicePlatform, IEnumerable<string>> FileType = new()
    {
        { DevicePlatform.Android, new[] { "text/*" } },
        { DevicePlatform.iOS, new[] { "public.json", "public.plain-text" } },
        { DevicePlatform.MacCatalyst, new[] { "public.json", "public.plain-text" } },
        { DevicePlatform.WinUI, new[] { ".txt", ".json" } }
    };

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
            ToolbarItem("Progress").OnClicked(ViewVocabularyProgress),
                // Show vocabulary progress for this specific resource
                
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
            word => _ = SaveVocabularyWordAsync(word), // Async wrapper
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
                
            // Vocabulary section - always show for all media types
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
                    
                State.Resource.Vocabulary?.Count > 0 ?
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
                        ) :
                    State.IsGeneratingVocabulary ?
                        Label("Analyzing transcript and generating vocabulary...")
                            .FontSize(14)
                            .TextColor(Colors.Gray) :
                        Label("No vocabulary words have been added yet. Use Generate to extract from transcript or add words manually in Edit mode.")
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
                
                // Vocabulary import section
                VStack(
                    Label("Import Vocabulary from File or Paste Text")
                        .FontAttributes(FontAttributes.Bold)
                        .HStart(),
                    new SfTextInputLayout{
                        Editor()
                            .Text(State.VocabList)
                            .OnTextChanged(text => SetState(s => s.VocabList = text))
                            .MinimumHeightRequest(150)
                            .MaximumHeightRequest(250)
                        }
                        .Hint($"{_localize["Vocabulary"]}"),

                    Button()
                        .ImageSource(ApplicationTheme.IconFileExplorer)
                        .Background(Colors.Transparent)
                        .HEnd()
                        .OnClicked(ChooseFile),

                    HStack(
                        RadioButton()
                            .Content("Comma").Value("comma")
                            .IsChecked(State.Delimiter == "comma")
                            .OnCheckedChanged(e =>
                                { if (e.Value) SetState(s => s.Delimiter = "comma"); }),
                        RadioButton()
                            .Content("Tab").Value("tab")
                            .IsChecked(State.Delimiter == "tab")
                            .OnCheckedChanged(e =>
                                { if (e.Value) SetState(s => s.Delimiter = "tab"); }),
                        Button("Import")
                            .ThemeKey("Secondary")
                            .OnClicked(ImportVocabulary)
                            .IsEnabled(!string.IsNullOrWhiteSpace(State.VocabList))
                    )
                    .Spacing(ApplicationTheme.Size320)
                )
                .Spacing(5),
                
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
    
    async Task SaveVocabularyWordAsync(VocabularyWord word)
    {
        try
        {
            SetState(s => s.IsLoading = true);
            
            // Set timestamps
            if (word.CreatedAt == default)
                word.CreatedAt = DateTime.UtcNow;
            word.UpdatedAt = DateTime.UtcNow;
            
            // Save the vocabulary word to database
            var result = await _resourceRepo.SaveWordAsync(word);
            
            if (result <= 0)
            {
                throw new Exception("Failed to save vocabulary word to database");
            }
            
            SetState(s => 
            {
                if (s.IsAddingNewWord)
                {
                    // For new words, add to resource and save relationship
                    if (s.Resource.Vocabulary == null)
                    {
                        s.Resource.Vocabulary = new List<VocabularyWord>();
                    }
                    s.Resource.Vocabulary.Add(word);
                    
                    // Only save resource for new words to establish relationship
                    _shouldSaveResourceRelationship = true;
                }
                else
                {
                    // For existing words, just update the local list - no need to save resource
                    var index = s.Resource.Vocabulary.FindIndex(w => w.Id == word.Id);
                    if (index >= 0)
                    {
                        s.Resource.Vocabulary[index] = word;
                    }
                    _shouldSaveResourceRelationship = false;
                }
                s.IsVocabularySheetVisible = false;
                s.IsLoading = false;
            });
            
            // Only save resource relationship for new words
            if (State.Resource.Id > 0 && _shouldSaveResourceRelationship)
            {
                await _resourceRepo.SaveResourceAsync(State.Resource);
            }
            
            await AppShell.DisplayToastAsync("✅ Vocabulary word saved successfully!");
        }
        catch (Exception ex)
        {
            SetState(s => s.IsLoading = false);
            await App.Current.MainPage.DisplayAlert("Error", $"Failed to save vocabulary word: {ex.Message}", "OK");
            System.Diagnostics.Debug.WriteLine($"SaveVocabularyWordAsync error: {ex}");
        }
    }
    
    async Task DeleteVocabularyWord(VocabularyWord word)
    {
        bool confirm = await App.Current.MainPage.DisplayAlert(
            "Confirm Delete", 
            $"Are you sure you want to delete '{word.TargetLanguageTerm}'?", 
            "Yes", "No");
            
        if (!confirm) return;
        
        try
        {
            SetState(s => s.IsLoading = true);
            
            // Remove from the resource relationship first if word has an ID
            if (word.Id > 0 && State.Resource.Id > 0)
            {
                await _resourceRepo.RemoveVocabularyFromResourceAsync(State.Resource.Id, word.Id);
            }
            
            // Remove from local state
            SetState(s => 
            {
                s.Resource.Vocabulary.Remove(word);
                s.IsLoading = false;
            });
            
            await AppShell.DisplayToastAsync("🗑️ Vocabulary word removed successfully!");
        }
        catch (Exception ex)
        {
            SetState(s => s.IsLoading = false);
            await App.Current.MainPage.DisplayAlert("Error", $"Failed to delete vocabulary word: {ex.Message}", "OK");
            System.Diagnostics.Debug.WriteLine($"DeleteVocabularyWord error: {ex}");
        }
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
    
    Task ViewVocabularyProgress()
    {
        return MauiControls.Shell.Current.GoToAsync<VocabularyProgressProps>(
            nameof(VocabularyLearningProgressPage),
            props => props.ResourceId = State.Resource.Id);
    }
    
    async Task ChooseFile()
    {
        var file = await _picker.PickFileAsync("Select a file", FileType);

        if (file != null)
        {
            using var stream = await file.OpenReadAsync();
            using var reader = new StreamReader(stream);
            var content = await reader.ReadToEndAsync();
            SetState(s => s.VocabList = content);
        }
    }
    
    async Task ImportVocabulary()
    {
        if (string.IsNullOrWhiteSpace(State.VocabList))
        {
            await App.Current.MainPage.DisplayAlert("Error", "No vocabulary to import", "OK");
            return;
        }
        
        try
        {
            // Parse vocabulary words from the input
            var newWords = VocabularyWord.ParseVocabularyWords(State.VocabList, State.Delimiter);
            
            if (!newWords.Any())
            {
                await App.Current.MainPage.DisplayAlert("Error", "No valid vocabulary words found in the input", "OK");
                return;
            }
            
            int addedCount = 0;
            int duplicateCount = 0;
            
            SetState(s => {
                // Initialize vocabulary list if it doesn't exist
                if (s.Resource.Vocabulary == null)
                {
                    s.Resource.Vocabulary = new List<VocabularyWord>();
                }
                
                // Add new words, checking for duplicates
                foreach (var word in newWords)
                {
                    // Check for duplicates based on both terms
                    bool isDuplicate = s.Resource.Vocabulary.Any(existing => 
                        (existing.TargetLanguageTerm?.Trim().Equals(word.TargetLanguageTerm?.Trim(), StringComparison.OrdinalIgnoreCase) == true &&
                         existing.NativeLanguageTerm?.Trim().Equals(word.NativeLanguageTerm?.Trim(), StringComparison.OrdinalIgnoreCase) == true) ||
                        existing.TargetLanguageTerm?.Trim().Equals(word.TargetLanguageTerm?.Trim(), StringComparison.OrdinalIgnoreCase) == true);
                    
                    if (!isDuplicate)
                    {
                        word.CreatedAt = DateTime.UtcNow;
                        word.UpdatedAt = DateTime.UtcNow;
                        s.Resource.Vocabulary.Add(word);
                        addedCount++;
                    }
                    else
                    {
                        duplicateCount++;
                    }
                }
                
                // Clear the input after successful import
                s.VocabList = string.Empty;
            });
            
            // Show result message
            var message = $"Added {addedCount} new vocabulary words.";
            if (duplicateCount > 0)
            {
                message += $" Skipped {duplicateCount} duplicates.";
            }
            
            await App.Current.MainPage.DisplayAlert("Import Complete", message, "OK");
        }
        catch (Exception ex)
        {
            await App.Current.MainPage.DisplayAlert("Error", $"Failed to import vocabulary: {ex.Message}", "OK");
        }
    }
}