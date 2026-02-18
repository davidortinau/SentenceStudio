using SentenceStudio.Data;
using SentenceStudio.Shared.Models;
using SentenceStudio.Services;
using LukeMauiFilePicker;
using MauiReactor.Shapes;
using UXDivers.Popups.Maui.Controls;
using UXDivers.Popups.Services;

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
    [Inject] NativeThemeService _themeService;
    LocalizationManager _localize => LocalizationManager.Instance;

    static readonly Dictionary<DevicePlatform, IEnumerable<string>> FileType = new()
    {
        { DevicePlatform.Android, new[] { "text/*" } },
        { DevicePlatform.iOS, new[] { "public.json", "public.plain-text" } },
        { DevicePlatform.MacCatalyst, new[] { "public.json", "public.plain-text" } },
        { DevicePlatform.WinUI, new[] { ".txt", ".json" } }
    };


    protected override void OnMounted()
    {
        _themeService.ThemeChanged += OnThemeChanged;
        base.OnMounted();
    }


    protected override void OnWillUnmount()
    {
        _themeService.ThemeChanged -= OnThemeChanged;
        base.OnWillUnmount();
    }

    private void OnThemeChanged(object? sender, ThemeChangedEventArgs e) => Invalidate();

    public override VisualNode Render()
    {
        var theme = BootstrapTheme.Current;

        return ContentPage($"{_localize["AddResource"]}",
            ToolbarItem($"{_localize["Save"]}").OnClicked(SaveResource),
            ToolbarItem($"{_localize["Cancel"]}").OnClicked(() => MauiControls.Shell.Current.GoToAsync("..")),

            Grid(
                State.IsLoading ?
                    ActivityIndicator().IsRunning(true).VCenter().HCenter() :
                    ScrollView(
                        VStack(
                            // Basic Information card
                            Border(
                                VStack(
                                    Label($"{_localize["BasicInformation"]}")
                                        .H5().FontAttributes(FontAttributes.Bold).HStart(),

                                    // Title
                                    VStack(
                                        Label($"{_localize["Title"]} *")
                                            .Class("form-label")
                                            .HStart(),
                                        Entry()
                                            .Text(State.Resource.Title)
                                            .Placeholder($"{_localize["ResourceTitle"]}")
                                            .OnTextChanged(text => SetState(s => s.Resource.Title = text))
                                            .Class("form-control")
                                    ).Spacing(4),

                                    // Description
                                    VStack(
                                        Label($"{_localize["Description"]}")
                                            .Class("form-label")
                                            .HStart(),
                                        Editor()
                                            .Text(State.Resource.Description)
                                            .Placeholder($"{_localize["BriefDescription"]}")
                                            .OnTextChanged(text => SetState(s => s.Resource.Description = text))
                                            .HeightRequest(100)
                                            .Class("form-control")
                                    ).Spacing(4),

                                    // Media Type & Language side by side
                                    Grid("Auto", "*, *",
                                        VStack(
                                            Label($"{_localize["MediaType"]}")
                                                .Class("form-label")
                                                .HStart(),
                                            Picker()
                                                .ItemsSource(Constants.MediaTypes)
                                                .SelectedIndex(State.MediaTypeIndex)
                                                .OnSelectedIndexChanged(index => SetState(s =>
                                                {
                                                    s.MediaTypeIndex = index;
                                                    s.Resource.MediaType = Constants.MediaTypes[index];
                                                }))
                                                .Class("form-select")
                                        ).Spacing(4).GridColumn(0),

                                        VStack(
                                            Label($"{_localize["Language"]}")
                                                .Class("form-label")
                                                .HStart(),
                                            Picker()
                                                .ItemsSource(Constants.Languages)
                                                .SelectedIndex(State.LanguageIndex)
                                                .OnSelectedIndexChanged(index => SetState(s =>
                                                {
                                                    s.LanguageIndex = index;
                                                    s.Resource.Language = Constants.Languages[index];
                                                }))
                                                .Class("form-select")
                                        ).Spacing(4).GridColumn(1)
                                    ).ColumnSpacing(12),

                                    // Tags
                                    VStack(
                                        Label($"{_localize["Tags"]}")
                                            .Class("form-label")
                                            .HStart(),
                                        Entry()
                                            .Text(State.Resource.Tags)
                                            .Placeholder("tag1, tag2, tag3")
                                            .OnTextChanged(text => SetState(s => s.Resource.Tags = text))
                                            .Class("form-control")
                                    ).Spacing(4)
                                ).Spacing(12)
                            )
                            .BackgroundColor(theme.GetSurface())
                            .Stroke(theme.GetOutline())
                            .StrokeThickness(1)
                            .StrokeShape(new RoundRectangle().CornerRadius(12))
                            .Padding(16),

                            // Media Content card - show for all types except Vocabulary List
                            State.Resource.MediaType != "Vocabulary List" ?
                                Border(
                                    VStack(
                                        Label($"{_localize["MediaContent"]}")
                                            .H5().FontAttributes(FontAttributes.Bold).HStart(),

                                        VStack(
                                            Label($"{_localize["MediaURL"]}")
                                                .Class("form-label")
                                                .HStart(),
                                            Entry()
                                                .Text(State.Resource.MediaUrl)
                                                .Placeholder("https://...")
                                                .OnTextChanged(text => SetState(s => s.Resource.MediaUrl = text))
                                                .Keyboard(Keyboard.Url)
                                                .Class("form-control")
                                        ).Spacing(4),

                                        VStack(
                                            Label($"{_localize["Transcript"]}")
                                                .Class("form-label")
                                                .HStart(),
                                            Editor()
                                                .Text(State.Resource.Transcript)
                                                .Placeholder($"{_localize["PasteTranscript"]}")
                                                .OnTextChanged(text => SetState(s => s.Resource.Transcript = text))
                                                .HeightRequest(150)
                                                .Class("form-control")
                                        ).Spacing(4),

                                        VStack(
                                            Label($"{_localize["Translation"]}")
                                                .Class("form-label")
                                                .HStart(),
                                            Editor()
                                                .Text(State.Resource.Translation)
                                                .Placeholder($"{_localize["PasteTranslation"]}")
                                                .OnTextChanged(text => SetState(s => s.Resource.Translation = text))
                                                .HeightRequest(150)
                                                .Class("form-control")
                                        ).Spacing(4)
                                    ).Spacing(12)
                                )
                                .BackgroundColor(theme.GetSurface())
                                .Stroke(theme.GetOutline())
                                .StrokeThickness(1)
                                .StrokeShape(new RoundRectangle().CornerRadius(12))
                                .Padding(16) :
                                null,

                            // Vocabulary card
                            Border(
                                VStack(
                                    Label($"{_localize["Vocabulary"]}")
                                        .H5().FontAttributes(FontAttributes.Bold).HStart(),

                                    VStack(
                                        Label($"{_localize["PasteVocabulary"]}")
                                            .Class("form-label")
                                            .HStart(),
                                        Editor()
                                            .Text(State.VocabList)
                                            .OnTextChanged(text => SetState(s => s.VocabList = text))
                                            .MinimumHeightRequest(150)
                                            .MaximumHeightRequest(400)
                                            .Class("form-control")
                                    ).Spacing(4),

                                    HStack(
                                        RadioButton()
                                            .Content($"{_localize["CommaDelimiter"]}").Value("comma")
                                            .IsChecked(State.Delimiter == "comma")
                                            .OnCheckedChanged(e =>
                                                { if (e.Value) SetState(s => s.Delimiter = "comma"); }),
                                        RadioButton()
                                            .Content($"{_localize["TabDelimiter"]}").Value("tab")
                                            .IsChecked(State.Delimiter == "tab")
                                            .OnCheckedChanged(e =>
                                                { if (e.Value) SetState(s => s.Delimiter = "tab"); }),
                                        Button($"{_localize["ImportVocabulary"]}")
                                            .Secondary()
                                            .OnClicked(ImportVocabulary)
                                            .IsEnabled(!string.IsNullOrWhiteSpace(State.VocabList))
                                            .HEnd(),
                                        Button()
                                            .ImageSource(BootstrapIcons.Create(BootstrapIcons.Folder2Open, theme.GetOnBackground(), 20))
                                            .Background(Colors.Transparent)
                                            .OnClicked(ChooseFile)
                                    ).Spacing(8),

                                    State.Resource.Vocabulary?.Count > 0 ?
                                        Label($"{State.Resource.Vocabulary.Count} {_localize["VocabularyWordsAdded"]}")
                                            .FontSize(14).Muted().HStart() :
                                        null
                                ).Spacing(12)
                            )
                            .BackgroundColor(theme.GetSurface())
                            .Stroke(theme.GetOutline())
                            .StrokeThickness(1)
                            .StrokeShape(new RoundRectangle().CornerRadius(12))
                            .Padding(16),

                            Button($"{_localize["SaveResource"]}")
                                .Primary()
                                .OnClicked(SaveResource)
                                .HFill()
                        )
                        .Padding(16)
                        .Spacing(16)
                    )
            )
        )
        .BackgroundColor(BootstrapTheme.Current.GetBackground());
    }

    async Task SaveResource()
    {
        // Validate required fields
        if (string.IsNullOrWhiteSpace(State.Resource.Title))
        {
            await IPopupService.Current.PushAsync(new SimpleActionPopup
            {
                Title = $"{_localize["ValidationError"]}",
                Text = $"{_localize["TitleRequired"]}",
                ActionButtonText = $"{_localize["OK"]}",
                ShowSecondaryActionButton = false
            });
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
        var file = await _picker.PickFileAsync($"{_localize["SelectFile"]}", FileType);

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
            await IPopupService.Current.PushAsync(new SimpleActionPopup
            {
                Title = $"{_localize["Error"]}",
                Text = $"{_localize["NoVocabularyToImport"]}",
                ActionButtonText = $"{_localize["OK"]}",
                ShowSecondaryActionButton = false
            });
            return;
        }

        try
        {
            // Parse vocabulary words from the input
            var newWords = VocabularyWord.ParseVocabularyWords(State.VocabList, State.Delimiter);

            if (!newWords.Any())
            {
                await IPopupService.Current.PushAsync(new SimpleActionPopup
                {
                    Title = $"{_localize["Error"]}",
                    Text = $"{_localize["NoValidVocabularyFound"]}",
                    ActionButtonText = $"{_localize["OK"]}",
                    ShowSecondaryActionButton = false
                });
                return;
            }

            int addedCount = 0;
            int duplicateCount = 0;

            SetState(s =>
            {
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
            var message = duplicateCount > 0
                ? string.Format($"{_localize["VocabularyImportWithDuplicates"]}", addedCount, duplicateCount)
                : string.Format($"{_localize["VocabularyImportSuccess"]}", addedCount);

            await IPopupService.Current.PushAsync(new SimpleActionPopup
            {
                Title = $"{_localize["ImportComplete"]}",
                Text = message,
                ActionButtonText = $"{_localize["OK"]}",
                ShowSecondaryActionButton = false
            });
        }
        catch (Exception ex)
        {
            await IPopupService.Current.PushAsync(new SimpleActionPopup
            {
                Title = $"{_localize["Error"]}",
                Text = string.Format($"{_localize["FailedToImportVocabulary"]}", ex.Message),
                ActionButtonText = $"{_localize["OK"]}",
                ShowSecondaryActionButton = false
            });
        }
    }
}
