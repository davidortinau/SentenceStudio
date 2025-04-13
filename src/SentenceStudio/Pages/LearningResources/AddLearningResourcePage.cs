using SentenceStudio.Data;
using SentenceStudio.Models;
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
    [Inject] VocabularyService _vocabService;
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
                                .Style((Style)Application.Current.Resources["InputWrapper"])
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
                                .Style((Style)Application.Current.Resources["InputWrapper"])
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
                            
                            // Only show vocabulary input if Media Type is Vocabulary List
                            State.Resource.MediaType == "Vocabulary List" ?
                                VStack(
                                    // Vocabulary List Input
                                    VStack(
                                        Label("Vocabulary Words")
                                            .FontAttributes(FontAttributes.Bold)
                                            .HStart(),
                                        new SfTextInputLayout{
                                            Editor()
                                                .Text(State.VocabList)
                                                .OnTextChanged(text => SetState(s => s.VocabList = text))
                                                .MinimumHeightRequest(300)
                                                .MaximumHeightRequest(500)
                                            }
                                            .Hint($"{_localize["Vocabulary"]}"),

                                        Button()
                                            .ImageSource(SegoeFluentIcons.FileExplorer.ToImageSource())
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
                                                    { if (e.Value) SetState(s => s.Delimiter = "tab"); })
                                        )
                                        .Spacing((Double)Application.Current.Resources["size320"])
                                    )
                                    .Spacing(5)
                                ) : 
                                null,
                            
                            // Media URL - only show if not vocabulary
                            State.Resource.MediaType != "Vocabulary" ?
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
                                    .Style((Style)Application.Current.Resources["InputWrapper"])
                                )
                                .Spacing(5) : 
                                null,
                            
                            // Transcript - only show if not vocabulary
                            State.Resource.MediaType != "Vocabulary" ?
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
                                    .Style((Style)Application.Current.Resources["InputWrapper"])
                                )
                                .Spacing(5) : 
                                null,
                            
                            // Translation - only show if not vocabulary
                            State.Resource.MediaType != "Vocabulary" ?
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
                                    .Style((Style)Application.Current.Resources["InputWrapper"])
                                )
                                .Spacing(5) : 
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
                                .Style((Style)Application.Current.Resources["InputWrapper"])
                            )
                            .Spacing(5),
                            
                            Button($"{_localize["Save"]}")
                                .OnClicked(SaveResource)
                                .HorizontalOptions(LayoutOptions.Fill)
                        )
                        .Padding(new Thickness(15))
                        .Spacing(15)
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
        
        // If this is a vocabulary resource, create the vocabulary words
        if (State.Resource.MediaType == "Vocabulary List" && !string.IsNullOrWhiteSpace(State.VocabList))
        {
            // Parse vocabulary words from the input and add to the resource
            State.Resource.Vocabulary = VocabularyWord.ParseVocabularyWords(State.VocabList, State.Delimiter);
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
}