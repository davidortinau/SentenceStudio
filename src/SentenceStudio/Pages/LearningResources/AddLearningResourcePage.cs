using SentenceStudio.Data;
using SentenceStudio.Shared.Models;
using SentenceStudio.Services;
using LukeMauiFilePicker;

namespace SentenceStudio.Pages.LearningResources;

class AddLearningResourceState
{
    public LearningResource Resource { get; set; } = new()
    {
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };
    public bool IsLoading { get; set; } = false;
    public int MediaTypeIndex { get; set; } = 0;
    public int LanguageIndex { get; set; } = 0;
    public string VocabList { get; set; } = string.Empty;
    public string Delimiter { get; set; } = "comma";
}

partial class AddLearningResourcePage : Component<AddLearningResourceState>
{
    [Inject] LearningResourceRepository _resourceRepo;
    [Inject] IFilePickerService _picker;
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
        return ContentPage($"{_localize["AddResource"]}",
            ToolbarItem("Save").OnClicked(SaveResource),
            ToolbarItem("Cancel").OnClicked(() => MauiControls.Shell.Current.GoToAsync("..")),
                
            Grid(
                State.IsLoading ? 
                    ActivityIndicator().IsRunning(true).VCenter().HCenter() :
                    ScrollView(
                        VStack(
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
                                .ThemeKey(MyTheme.InputWrapper)
                            )
                            .Spacing(MyTheme.MicroSpacing),
                            
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
                                .ThemeKey(MyTheme.InputWrapper)
                            )
                            .Spacing(MyTheme.MicroSpacing),
                            
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
                            .Spacing(MyTheme.MicroSpacing),
                            
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
                            .Spacing(MyTheme.MicroSpacing),
                            
                            // Media URL - show for all types except Vocabulary List
                            State.Resource.MediaType != "Vocabulary List" ?
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
                                    .ThemeKey(MyTheme.InputWrapper)
                                )
                                .Spacing(MyTheme.MicroSpacing) : 
                                null,
                            
                            // Transcript - show for all types except Vocabulary List
                            State.Resource.MediaType != "Vocabulary List" ?
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
                                    .ThemeKey(MyTheme.InputWrapper)
                                )
                                .Spacing(MyTheme.MicroSpacing) : 
                                null,
                            
                            // Translation - show for all types except Vocabulary List
                            State.Resource.MediaType != "Vocabulary List" ?
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
                                    .ThemeKey(MyTheme.InputWrapper)
                                )
                                .Spacing(MyTheme.MicroSpacing) : 
                                null,
                            
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
                                .ThemeKey(MyTheme.InputWrapper)
                            )
                            .Spacing(MyTheme.MicroSpacing),
                            
                            // Vocabulary section - show for ALL media types
                            VStack(
                                Label("Vocabulary Words")
                                    .FontAttributes(FontAttributes.Bold)
                                    .HStart(),
                                new SfTextInputLayout{
                                    Editor()
                                        .Text(State.VocabList)
                                        .OnTextChanged(text => SetState(s => s.VocabList = text))
                                        .MinimumHeightRequest(200)
                                        .MaximumHeightRequest(400)
                                    }
                                    .Hint($"{_localize["Vocabulary"]}"),

                                Button()
                                    .ImageSource(MyTheme.IconFileExplorer)
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
                                    Button("Import & Append")
                                        .ThemeKey("Secondary")
                                        .OnClicked(ImportVocabulary)
                                        .IsEnabled(!string.IsNullOrWhiteSpace(State.VocabList))
                                )
                                .Spacing(MyTheme.Size320),
                                
                                // Show current vocabulary count if any exists
                                State.Resource.Vocabulary?.Count > 0 ?
                                    Label($"Current vocabulary: {State.Resource.Vocabulary.Count} words")
                                        .FontSize(14)
                                        .TextColor(Colors.Gray)
                                        .HStart() :
                                    null
                            )
                            .Spacing(MyTheme.MicroSpacing),
                            
                            // Media URL - show for all types except Vocabulary List
                            State.Resource.MediaType != "Vocabulary List" ?
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
                                    .ThemeKey(MyTheme.InputWrapper)
                                )
                                .Spacing(MyTheme.MicroSpacing) : 
                                null,
                            
                            // Transcript - show for all types except Vocabulary List
                            State.Resource.MediaType != "Vocabulary List" ?
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
                                    .ThemeKey(MyTheme.InputWrapper)
                                )
                                .Spacing(MyTheme.MicroSpacing) : 
                                null,
                            
                            // Translation - show for all types except Vocabulary List
                            State.Resource.MediaType != "Vocabulary List" ?
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
                                    .ThemeKey(MyTheme.InputWrapper)
                                )
                                .Spacing(MyTheme.MicroSpacing) : 
                                null,
                            
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
                                .ThemeKey(MyTheme.InputWrapper)
                            )
                            .Spacing(MyTheme.MicroSpacing),
                            
                            // Vocabulary section - show for ALL media types
                            VStack(
                                Label("Vocabulary Words")
                                    .FontAttributes(FontAttributes.Bold)
                                    .HStart(),
                                new SfTextInputLayout{
                                    Editor()
                                        .Text(State.VocabList)
                                        .OnTextChanged(text => SetState(s => s.VocabList = text))
                                        .MinimumHeightRequest(200)
                                        .MaximumHeightRequest(400)
                                    }
                                    .Hint($"{_localize["Vocabulary"]}"),

                                Button()
                                    .ImageSource(MyTheme.IconFileExplorer)
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
                                    Button("Import & Append")
                                        .ThemeKey("Secondary")
                                        .OnClicked(ImportVocabulary)
                                        .IsEnabled(!string.IsNullOrWhiteSpace(State.VocabList))
                                )
                                .Spacing(MyTheme.Size320),
                                
                                // Show current vocabulary count if any exists
                                State.Resource.Vocabulary?.Count > 0 ?
                                    Label($"Current vocabulary: {State.Resource.Vocabulary.Count} words")
                                        .FontSize(14)
                                        .TextColor(Colors.Gray)
                                        .HStart() :
                                    null
                            )
                            .Spacing(MyTheme.MicroSpacing),
                            
                            Button($"{_localize["Save"]}")
                                .OnClicked(SaveResource)
                                .HorizontalOptions(LayoutOptions.Fill)
                        )
                        .Padding(new Thickness(15))
                        .Spacing(MyTheme.LayoutSpacing)
                    )
            )
        );
    }
    
    async Task SaveResource()
    {
        // Validate required fields
        if (string.IsNullOrWhiteSpace(State.Resource.Title))
        {
            await App.Current.MainPage.DisplayAlert("Validation Error", "Title is required", "OK");
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
        
        SetState(s => s.IsLoading = true);
        
        // If there's vocabulary content, create/append the vocabulary words for all media types
        if (!string.IsNullOrWhiteSpace(State.VocabList))
        {
            // Parse vocabulary words from the input
            var newWords = VocabularyWord.ParseVocabularyWords(State.VocabList, State.Delimiter);
            
            // Initialize vocabulary list if it doesn't exist
            if (State.Resource.Vocabulary == null)
            {
                State.Resource.Vocabulary = new List<VocabularyWord>();
            }
            
            // Add new words, checking for duplicates
            foreach (var word in newWords)
            {
                // Check for duplicates based on both terms
                bool isDuplicate = State.Resource.Vocabulary.Any(existing => 
                    (existing.TargetLanguageTerm?.Trim().Equals(word.TargetLanguageTerm?.Trim(), StringComparison.OrdinalIgnoreCase) == true &&
                     existing.NativeLanguageTerm?.Trim().Equals(word.NativeLanguageTerm?.Trim(), StringComparison.OrdinalIgnoreCase) == true) ||
                    existing.TargetLanguageTerm?.Trim().Equals(word.TargetLanguageTerm?.Trim(), StringComparison.OrdinalIgnoreCase) == true);
                
                if (!isDuplicate)
                {
                    word.CreatedAt = DateTime.UtcNow;
                    word.UpdatedAt = DateTime.UtcNow;
                    State.Resource.Vocabulary.Add(word);
                }
            }
        }
        
        // Save the resource
        await _resourceRepo.SaveResourceAsync(State.Resource);
        
        SetState(s => s.IsLoading = false);
        
        // Navigate back to list
        await MauiControls.Shell.Current.GoToAsync("..");
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