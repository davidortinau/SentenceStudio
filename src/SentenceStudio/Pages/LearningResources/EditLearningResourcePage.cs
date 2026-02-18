using MauiReactor.Shapes;
using LukeMauiFilePicker;
using SentenceStudio.Pages.VocabularyProgress;
using SentenceStudio.Pages.VocabularyManagement;
using Microsoft.Extensions.Logging;
using UXDivers.Popups.Maui.Controls;
using UXDivers.Popups.Services;

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
    public bool IsCleaningTranscript { get; set; } = false;
    public bool IsPolishingTranscript { get; set; } = false;
}

partial class EditLearningResourcePage : Component<EditLearningResourceState, ResourceProps>
{
    [Inject] LearningResourceRepository _resourceRepo;
    [Inject] AiService _aiService;
    [Inject] IFilePickerService _picker;
    [Inject] TranscriptFormattingService _formattingService;
    [Inject] UserProfileRepository _userProfileRepo;
    [Inject] ILogger<EditLearningResourcePage> _logger;

    // Track whether we need to save resource relationship (only for new words)
    private bool _shouldSaveResourceRelationship = false;

    LocalizationManager _localize => LocalizationManager.Instance;

    /// <summary>
    /// Returns true if the resource can be edited (not a smart/auto-generated resource)
    /// </summary>
    bool IsEditable => !State.Resource.IsSmartResource;

    static readonly Dictionary<DevicePlatform, IEnumerable<string>> FileType = new()
    {
        { DevicePlatform.Android, new[] { "text/*" } },
        { DevicePlatform.iOS, new[] { "public.json", "public.plain-text" } },
        { DevicePlatform.MacCatalyst, new[] { "public.json", "public.plain-text" } },
        { DevicePlatform.WinUI, new[] { ".txt", ".json" } }
    };

    public override VisualNode Render()
    {
        var theme = BootstrapTheme.Current;

        return ContentPage(State.Resource.Title ?? $"{_localize["Resource"]}",
            // Only show Edit button for non-smart resources when not already editing
            (State.IsEditing || !IsEditable) ? null : ToolbarItem("Edit").OnClicked(() => SetState(s => s.IsEditing = true)),
            State.IsEditing ? ToolbarItem("Cancel").OnClicked(() => SetState(s => s.IsEditing = false)) : null,
            State.IsEditing ? ToolbarItem("Save").OnClicked(SaveResource) : null,
            // Only show Delete button for non-smart resources when editing
            (State.IsEditing && IsEditable) ? ToolbarItem("Delete").OnClicked(DeleteResource) : null,
            ToolbarItem("Progress").OnClicked(ViewVocabularyProgress),

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
                            .Spacing(16)
                        ),

                        // Vocabulary word editor overlay
                        State.IsVocabularySheetVisible ?
                            RenderVocabularyWordEditor() :
                            null,

                        // Activity indicator overlay for AI polishing/cleaning
                        (State.IsPolishingTranscript || State.IsCleaningTranscript) ?
                            Grid(
                                BoxView()
                                    .Background(Colors.Black.WithAlpha(0.5f)),

                                VStack(spacing: 15,
                                    ActivityIndicator()
                                        .IsRunning(true)
                                        .Color(Colors.White),

                                    Label(State.IsPolishingTranscript ? "Polishing transcript with AI..." : "Cleaning up transcript...")
                                        .TextColor(Colors.White)
                                        .FontSize(16)
                                        .HorizontalTextAlignment(TextAlignment.Center),

                                    Label("Please wait, this may take a moment")
                                        .TextColor(Colors.White.WithAlpha(0.8f))
                                        .FontSize(14)
                                        .HorizontalTextAlignment(TextAlignment.Center)
                                )
                                .VCenter()
                                .HCenter()
                                .Padding(30)
                            )
                            : null
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
        var theme = BootstrapTheme.Current;

        return VStack(
            // Show media content based on type
            // RenderMediaContent(),

            // Smart Resource Badge (for auto-generated resources)
            State.Resource.IsSmartResource ?
                HStack(
                    Label("AI")
                        .FontSize(16),
                    Label($"{_localize["SmartResource"]}")
                        .FontSize(14)
                        .FontAttributes(FontAttributes.Bold)
                        .TextColor(theme.Primary),
                    Label($" - {_localize["ReadOnlyResource"]}")
                        .FontSize(12)
                        .Muted()
                )
                .Spacing(4)
                .Padding(new Thickness(8, 4))
                .BackgroundColor(theme.Info.WithAlpha(0.1f))
                : null,

            // Title
            Label(State.Resource.Title ?? string.Empty)
                .H4()
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
                .RowSpacing(4)
                .ColumnSpacing(8)
            )
            .Stroke(theme.GetOutline()),

            // Transcript if available
            !string.IsNullOrEmpty(State.Resource.Transcript) ?
                VStack(
                    Label("Transcript")
                        .FontAttributes(FontAttributes.Bold)
                        .FontSize(18),

                    // Formatting buttons (disabled for smart resources)
                    IsEditable ?
                        HStack(
                            Button("Clean Up Format")
                                .OnClicked(CleanUpTranscript)
                                .IsEnabled(!State.IsCleaningTranscript && !State.IsPolishingTranscript)
                                .Background(new SolidColorBrush(Colors.Transparent))
                                .TextColor(theme.GetOnBackground())
                                .BorderColor(theme.GetOutline())
                                .BorderWidth(1),

                            Button("Polish with AI")
                                .OnClicked(PolishTranscriptWithAi)
                                .IsEnabled(!State.IsCleaningTranscript && !State.IsPolishingTranscript)
                                .Background(new SolidColorBrush(Colors.Transparent))
                                .TextColor(theme.GetOnBackground())
                                .BorderColor(theme.GetOutline())
                                .BorderWidth(1)
                        )
                        .Spacing(8)
                        .HStart() : null,

                    Border(
                        Label(State.Resource.Transcript)
                    )
                    .Stroke(theme.GetOutline())
                    .Padding(8)
                )
                .Spacing(8) :
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
                    .Stroke(theme.GetOutline())
                    .Padding(8)
                )
                .Spacing(8) :
                null,

            // Vocabulary section - button to open bottom sheet
            RenderVocabularySection()
        )
        .Spacing(16);
    }

    private VisualNode RenderVocabularySection()
    {
        var theme = BootstrapTheme.Current;
        var vocabCount = State.Resource.Vocabulary?.Count ?? 0;

        return VStack(
            Grid(
                // Vocabulary button with count - opens bottom sheet
                Button()
                    .Text(vocabCount > 0
                        ? $"Vocabulary ({vocabCount} terms)"
                        : "Vocabulary (0 terms)")
                    .FontAttributes(FontAttributes.Bold)
                    .FontSize(16)
                    .Background(new SolidColorBrush(Colors.Transparent))
                    .TextColor(theme.GetOnBackground())
                    .BorderColor(theme.GetOutline())
                    .BorderWidth(1)
                    .OnClicked(NavigateToVocabularyManagement)
                    .IsEnabled(!State.IsGeneratingVocabulary)
                    .GridColumn(0)
                    .HStart(),

                // Generate button only for editable resources
                IsEditable ?
                    HStack(
                        Button($"{_localize["Generate"]}")
                            .Background(new SolidColorBrush(Colors.Transparent))
                            .TextColor(theme.GetOnBackground())
                            .BorderColor(theme.GetOutline())
                            .BorderWidth(1)
                            .OnClicked(GenerateVocabulary)
                            .IsEnabled(!State.IsGeneratingVocabulary)
                            .Opacity(State.IsGeneratingVocabulary ? 0.5 : 1.0),

                        ActivityIndicator()
                            .IsRunning(State.IsGeneratingVocabulary)
                            .IsVisible(State.IsGeneratingVocabulary)
                            .Scale(0.8)
                    )
                    .Spacing(8)
                    .GridColumn(1)
                    .HEnd() : null
            )
            .Columns("*, Auto"),

            State.IsGeneratingVocabulary ?
                Label("Analyzing transcript and generating vocabulary...")
                    .Small()
                    .Muted() :
                vocabCount == 0 ?
                    Label("No vocabulary words yet. Tap Generate to extract from transcript.")
                        .Small()
                        .Muted() :
                    null
        )
        .Spacing(4);
    }

    private VisualNode RenderEditMode()
    {
        var theme = BootstrapTheme.Current;

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
                .BackgroundColor(theme.GetSurface())
                .Stroke(theme.GetOutline())
                .StrokeThickness(1)
                .Padding(8)
            )
            .Spacing(4),

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
                .BackgroundColor(theme.GetSurface())
                .Stroke(theme.GetOutline())
                .StrokeThickness(1)
                .Padding(8)
            )
            .Spacing(4),

            // Media Type
            VStack(
                Label("Media Type")
                    .FontAttributes(FontAttributes.Bold)
                    .HStart(),
                Border(
                    Picker()
                        .ItemsSource(Constants.MediaTypes)
                        .SelectedIndex(State.MediaTypeIndex)
                        .OnSelectedIndexChanged(index => SetState(s =>
                        {
                            s.MediaTypeIndex = index;
                            s.Resource.MediaType = Constants.MediaTypes[index];
                        }))
                )
                .BackgroundColor(theme.GetSurface())
                .Stroke(theme.GetOutline())
                .StrokeThickness(1)
                .Padding(8)
            )
            .Spacing(4),

            // Language
            VStack(
                Label("Language")
                    .FontAttributes(FontAttributes.Bold)
                    .HStart(),
                Border(
                    Picker()
                        .ItemsSource(Constants.Languages)
                        .SelectedIndex(State.LanguageIndex)
                        .OnSelectedIndexChanged(index => SetState(s =>
                        {
                            s.LanguageIndex = index;
                            s.Resource.Language = Constants.Languages[index];
                        }))
                )
                .BackgroundColor(theme.GetSurface())
                .Stroke(theme.GetOutline())
                .StrokeThickness(1)
                .Padding(8)
            )
            .Spacing(4),

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
                .BackgroundColor(theme.GetSurface())
                .Stroke(theme.GetOutline())
                .StrokeThickness(1)
                .Padding(8)
            )
            .Spacing(4),

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
                .BackgroundColor(theme.GetSurface())
                .Stroke(theme.GetOutline())
                .StrokeThickness(1)
                .Padding(8)
            )
            .Spacing(4),

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
                .BackgroundColor(theme.GetSurface())
                .Stroke(theme.GetOutline())
                .StrokeThickness(1)
                .Padding(8)
            )
            .Spacing(4),

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
                .BackgroundColor(theme.GetSurface())
                .Stroke(theme.GetOutline())
                .StrokeThickness(1)
                .Padding(8)
            )
            .Spacing(4),

            // Vocabulary section
            VStack(
                Label("Vocabulary")
                        .H3()
                        .GridColumn(0),

                HStack(
                    Button("+ Add Word")
                        .Background(new SolidColorBrush(Colors.Transparent))
                        .TextColor(theme.GetOnBackground())
                        .BorderColor(theme.GetOutline())
                        .BorderWidth(1)
                        .OnClicked(AddVocabularyWord),

                    Button("Generate")
                        .Background(new SolidColorBrush(Colors.Transparent))
                        .TextColor(theme.GetOnBackground())
                        .BorderColor(theme.GetOutline())
                        .BorderWidth(1)
                        .OnClicked(GenerateVocabulary)
                        .IsEnabled(!State.IsGeneratingVocabulary)
                        .Opacity(State.IsGeneratingVocabulary ? 0.5 : 1.0),

                    ActivityIndicator()
                        .IsRunning(State.IsGeneratingVocabulary)
                        .IsVisible(State.IsGeneratingVocabulary)
                        .Scale(0.8)
                )
                .Spacing(8),

                // Vocabulary import section

                Label("Import Vocabulary from File or Paste Text")
                    .FontAttributes(FontAttributes.Bold)
                    .HStart(),
                Grid(
                    Border(
                        Editor()
                            .Text(State.VocabList)
                            .OnTextChanged(text => SetState(s => s.VocabList = text))
                            .MinimumHeightRequest(150)
                            .MaximumHeightRequest(250)
                    )
                    .BackgroundColor(theme.GetSurface())
                    .Stroke(theme.GetOutline())
                    .StrokeThickness(1)
                    .Padding(8),
                    Button()
                        .ImageSource(BootstrapIcons.Create(BootstrapIcons.Folder2Open, theme.GetOnBackground(), 20))
                        .Background(Colors.Transparent)
                        .HEnd()
                        .VEnd()
                        .TranslationY(-30)
                        .OnClicked(ChooseFile)
                ),
                HStack(
                    RadioButton()
                        .Content("Comma").Value("comma")
                        .FontSize(16)
                        .IsChecked(State.Delimiter == "comma")
                        .OnCheckedChanged(e =>
                            { if (e.Value) SetState(s => s.Delimiter = "comma"); }),
                    RadioButton()
                        .Content("Tab").Value("tab")
                        .FontSize(16)
                        .IsChecked(State.Delimiter == "tab")
                        .OnCheckedChanged(e =>
                            { if (e.Value) SetState(s => s.Delimiter = "tab"); }),
                    Button("Import")
                        .Background(new SolidColorBrush(Colors.Transparent))
                        .TextColor(theme.GetOnBackground())
                        .BorderColor(theme.GetOutline())
                        .BorderWidth(1)
                        .OnClicked(ImportVocabulary)
                        .IsEnabled(!string.IsNullOrWhiteSpace(State.VocabList))
                )
                .Spacing(32),

                VStack(
                    State.Resource.Vocabulary?.Select(word =>
                        Border(
                            Grid(
                                VStack(
                                    Label(word.TargetLanguageTerm)
                                        .FontAttributes(FontAttributes.Bold)
                                        .FontSize(16),

                                    Label(word.NativeLanguageTerm)
                                        .FontSize(14)
                                )
                                .Spacing(4)
                                .GridColumn(0),

                                HStack(
                                    Button("Edit")
                                        .OnClicked(() => EditVocabularyWord(word))
                                        .Background(new SolidColorBrush(Colors.Transparent))
                                        .TextColor(theme.GetOnBackground())
                                        .BorderColor(theme.GetOutline())
                                        .BorderWidth(1),

                                    Button("Delete")
                                        .OnClicked(() => DeleteVocabularyWord(word))
                                        .Background(new SolidColorBrush(Colors.Transparent))
                                        .TextColor(theme.GetOnBackground())
                                        .BorderColor(theme.GetOutline())
                                        .BorderWidth(1)
                                )
                                .GridColumn(1)
                                .HEnd()
                                .Spacing(4)
                            )
                            .Columns("*, Auto")
                            .Padding(8)
                        )
                        .Stroke(theme.GetOutline())
                        .StrokeThickness(1)
                        .StrokeShape(new RoundRectangle().CornerRadius(5))
                        .Margin(new Thickness(0, 0, 0, 5))
                    ).ToArray() ?? Array.Empty<VisualNode>()
                )
                .Spacing(4)
            )
            .Spacing(16)
        )
        .Spacing(16);
    }

    async Task LoadResource()
    {
        if (Props.ResourceID == 0)
        {
            // Creating a new resource
            SetState(s =>
            {
                s.Resource = new LearningResource
                {
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

        SetState(s =>
        {
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
            await IPopupService.Current.PushAsync(new SimpleActionPopup
            {
                Title = "Validation Error",
                Text = "Title is required",
                ActionButtonText = "OK",
                ShowSecondaryActionButton = false
            });
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

        SetState(s =>
        {
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
        var tcs = new TaskCompletionSource<bool>();
        var confirmPopup = new SimpleActionPopup
        {
            Title = "Confirm Delete",
            Text = "Are you sure you want to delete this resource?",
            ActionButtonText = "Yes",
            SecondaryActionButtonText = "No",
            CloseWhenBackgroundIsClicked = false,
            ActionButtonCommand = new Command(async () =>
            {
                tcs.TrySetResult(true);
                await IPopupService.Current.PopAsync();
            }),
            SecondaryActionButtonCommand = new Command(async () =>
            {
                tcs.TrySetResult(false);
                await IPopupService.Current.PopAsync();
            })
        };
        await IPopupService.Current.PushAsync(confirmPopup);
        bool confirm = await tcs.Task;

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
        SetState(s =>
        {
            s.CurrentVocabularyWord = new VocabularyWord();
            s.IsAddingNewWord = true;
            s.IsVocabularySheetVisible = true;
        });
    }

    void EditVocabularyWord(VocabularyWord word)
    {
        SetState(s =>
        {
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

            await AppShell.DisplayToastAsync("Vocabulary word saved successfully!");
        }
        catch (Exception ex)
        {
            SetState(s => s.IsLoading = false);
            await IPopupService.Current.PushAsync(new SimpleActionPopup
            {
                Title = "Error",
                Text = $"Failed to save vocabulary word: {ex.Message}",
                ActionButtonText = "OK",
                ShowSecondaryActionButton = false
            });
            _logger.LogError(ex, "SaveVocabularyWordAsync error");
        }
    }

    async Task DeleteVocabularyWord(VocabularyWord word)
    {
        var tcs2 = new TaskCompletionSource<bool>();
        var confirmPopup2 = new SimpleActionPopup
        {
            Title = "Confirm Delete",
            Text = $"Are you sure you want to delete '{word.TargetLanguageTerm}'?",
            ActionButtonText = "Yes",
            SecondaryActionButtonText = "No",
            CloseWhenBackgroundIsClicked = false,
            ActionButtonCommand = new Command(async () =>
            {
                tcs2.TrySetResult(true);
                await IPopupService.Current.PopAsync();
            }),
            SecondaryActionButtonCommand = new Command(async () =>
            {
                tcs2.TrySetResult(false);
                await IPopupService.Current.PopAsync();
            })
        };
        await IPopupService.Current.PushAsync(confirmPopup2);
        bool confirm = await tcs2.Task;

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

            await AppShell.DisplayToastAsync("Vocabulary word removed successfully!");
        }
        catch (Exception ex)
        {
            SetState(s => s.IsLoading = false);
            await IPopupService.Current.PushAsync(new SimpleActionPopup
            {
                Title = "Error",
                Text = $"Failed to delete vocabulary word: {ex.Message}",
                ActionButtonText = "OK",
                ShowSecondaryActionButton = false
            });
            _logger.LogError(ex, "DeleteVocabularyWord error");
        }
    }

    /// <summary>
    /// Get existing vocabulary word from database or create new one.
    /// This ensures we reuse existing words and properly track many-to-many relationships.
    /// </summary>
    async Task<VocabularyWord> GetOrCreateVocabularyWordAsync(string targetTerm, string nativeTerm)
    {
        // Search for existing word by target term (case-insensitive)
        var existingWord = await _resourceRepo.GetWordByTargetTermAsync(targetTerm);

        if (existingWord != null)
        {
            _logger.LogDebug("🏴‍☠️ Found existing word: {TargetTerm}", targetTerm);
            return existingWord;
        }

        // Create new word
        var newWord = new VocabularyWord
        {
            TargetLanguageTerm = targetTerm,
            NativeLanguageTerm = nativeTerm,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        // Save to database
        var result = await _resourceRepo.SaveWordAsync(newWord);

        if (result <= 0)
        {
            throw new Exception($"Failed to save vocabulary word: {targetTerm}");
        }

        _logger.LogDebug("🏴‍☠️ Created new word: {TargetTerm} (ID: {WordId})", targetTerm, newWord.Id);
        return newWord;
    }

    /// <summary>
    /// Detects if a string is likely English text (uses basic Latin alphabet)
    /// </summary>
    bool IsLikelyEnglish(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        // Count how many characters are basic Latin (A-Z, a-z)
        int latinCount = 0;
        int totalLetters = 0;

        foreach (char c in text)
        {
            if (char.IsLetter(c))
            {
                totalLetters++;
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
                {
                    latinCount++;
                }
            }
        }

        // If we have no letters, it's not English
        if (totalLetters == 0)
            return false;

        // If more than 80% of letters are basic Latin, it's likely English
        return (latinCount / (double)totalLetters) > 0.8;
    }

    async Task GenerateVocabulary()
    {
        // Check if there's a transcript to analyze
        if (string.IsNullOrWhiteSpace(State.Resource.Transcript))
        {
            await IPopupService.Current.PushAsync(new SimpleActionPopup
            {
                Title = "Error",
                Text = "No transcript available to generate vocabulary from",
                ActionButtonText = "OK",
                ShowSecondaryActionButton = false
            });
            return;
        }

        SetState(s => s.IsGeneratingVocabulary = true);

        try
        {
            // Get user profile for fallback languages
            var userProfile = await _userProfileRepo.GetOrCreateDefaultAsync();

            // Determine the target and native languages
            var targetLanguage = !string.IsNullOrWhiteSpace(State.Resource.Language)
                ? State.Resource.Language
                : userProfile?.TargetLanguage ?? "Korean";

            var nativeLanguage = userProfile?.NativeLanguage ?? "English";

            _logger.LogDebug("🏴‍☠️ GenerateVocabulary: Starting vocabulary generation for language '{TargetLanguage}'", targetLanguage);
            _logger.LogDebug("🏴‍☠️ Transcript length: {Length} chars", State.Resource.Transcript.Length);
            _logger.LogDebug("🏴‍☠️ Using: Target={TargetLanguage}, Native={NativeLanguage}", targetLanguage, nativeLanguage);

            // Build AI prompt - no need to fetch existing words, let AI extract everything
            string prompt = $@"
You are a language learning assistant. Analyze this {targetLanguage} transcript and extract ALL vocabulary words that would be useful for a learner.

CRITICAL: Pay close attention to which field is which:
- TargetLanguageTerm = The word in {targetLanguage} (the language being learned, NOT {nativeLanguage})
- NativeLanguageTerm = The {nativeLanguage} translation

For EACH word or phrase you find in the transcript, provide:
1. TargetLanguageTerm: The word in {targetLanguage} - use dictionary/base form when appropriate
2. NativeLanguageTerm: The {nativeLanguage} translation

Example for {targetLanguage}:
{{
  ""TargetLanguageTerm"": ""[word in {targetLanguage}]"",
  ""NativeLanguageTerm"": ""[translation in {nativeLanguage}]""
}}

Include:
- Nouns, verbs, adjectives, adverbs
- Common expressions and phrases
- Grammar patterns if useful
- ALL words that appear in the transcript, even if they seem basic

Be generous with vocabulary extraction. Extract as many useful vocabulary words as possible from this transcript.

IMPORTANT: 
- TargetLanguageTerm MUST be in {targetLanguage}
- NativeLanguageTerm MUST be in {nativeLanguage}
- Do NOT swap these fields

Format your response as a valid JSON array of objects with TargetLanguageTerm and NativeLanguageTerm properties.

Transcript:
{State.Resource.Transcript}
";

            _logger.LogDebug("🏴‍☠️ Sending prompt to AI (length: {Length} chars)", prompt.Length);

            var vocabulary = await _aiService.SendPrompt<List<VocabularyWord>>(prompt);

            _logger.LogDebug("🏴‍☠️ AI returned {Count} vocabulary words", vocabulary?.Count ?? 0);

            if (vocabulary != null && vocabulary.Any())
            {
                _logger.LogDebug("🏴‍☠️ Processing {Count} words from AI...", vocabulary.Count);

                int newWordsCreated = 0;
                int existingWordsLinked = 0;
                int localDuplicatesSkipped = 0;
                List<VocabularyWord> wordsToAssociate = new List<VocabularyWord>();

                // Process each word: get or create, then prepare for association
                foreach (var wordData in vocabulary)
                {
                    if (string.IsNullOrWhiteSpace(wordData.TargetLanguageTerm))
                    {
                        _logger.LogWarning("⚠️ Skipping word with empty target term");
                        continue;
                    }

                    // Check if this word is already associated with this resource
                    bool alreadyInResource = State.Resource.Vocabulary?.Any(w =>
                        w.TargetLanguageTerm?.Trim().Equals(wordData.TargetLanguageTerm?.Trim(), StringComparison.OrdinalIgnoreCase) == true) ?? false;

                    if (alreadyInResource)
                    {
                        localDuplicatesSkipped++;
                        _logger.LogDebug("⏭️ Already in resource: {TargetTerm}", wordData.TargetLanguageTerm);
                        continue;
                    }

                    try
                    {
                        // Validate and potentially swap if AI got confused
                        var targetTerm = wordData.TargetLanguageTerm?.Trim() ?? "";
                        var nativeTerm = wordData.NativeLanguageTerm?.Trim() ?? "";

                        // Detect if terms are swapped (English in target, non-English in native)
                        bool targetIsEnglish = IsLikelyEnglish(targetTerm);
                        bool nativeIsEnglish = IsLikelyEnglish(nativeTerm);

                        if (targetIsEnglish && !nativeIsEnglish)
                        {
                            _logger.LogWarning("⚠️ SWAPPED TERMS DETECTED! Swapping: '{TargetTerm}' <-> '{NativeTerm}'", targetTerm, nativeTerm);
                            // Swap them
                            var temp = targetTerm;
                            targetTerm = nativeTerm;
                            nativeTerm = temp;
                        }
                        else if (targetIsEnglish && nativeIsEnglish)
                        {
                            _logger.LogWarning("⚠️ Both terms appear to be English, skipping: '{TargetTerm}' / '{NativeTerm}'", targetTerm, nativeTerm);
                            continue;
                        }

                        // Get existing word or create new one
                        var word = await GetOrCreateVocabularyWordAsync(targetTerm, nativeTerm);

                        // Track if this was new or existing
                        if (word.CreatedAt == word.UpdatedAt &&
                            (DateTime.UtcNow - word.CreatedAt).TotalSeconds < 5)
                        {
                            newWordsCreated++;
                            _logger.LogDebug("✨ New word: {TargetTerm} = {NativeTerm}", word.TargetLanguageTerm, word.NativeLanguageTerm);
                        }
                        else
                        {
                            existingWordsLinked++;
                            _logger.LogDebug("🔗 Linking existing: {TargetTerm} = {NativeTerm}", word.TargetLanguageTerm, word.NativeLanguageTerm);
                        }

                        wordsToAssociate.Add(word);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing word '{TargetTerm}'", wordData.TargetLanguageTerm);
                    }
                }

                _logger.LogDebug("🏴‍☠️ Prepared {Count} words to associate with resource", wordsToAssociate.Count);

                // Add all words to the resource vocabulary collection
                SetState(s =>
                {
                    if (s.Resource.Vocabulary == null)
                    {
                        s.Resource.Vocabulary = new List<VocabularyWord>();
                    }

                    foreach (var word in wordsToAssociate)
                    {
                        s.Resource.Vocabulary.Add(word);
                    }
                });

                // Save the resource to persist all word associations
                _logger.LogDebug("🏴‍☠️ Saving resource to persist {Count} word associations...", wordsToAssociate.Count);
                await _resourceRepo.SaveResourceAsync(State.Resource);
                _logger.LogDebug("✅ Resource saved successfully!");

                SetState(s => s.IsGeneratingVocabulary = false);

                // Build success message with statistics
                var message = $"AI extracted {vocabulary.Count} vocabulary words\n\n";
                message += $"Created {newWordsCreated} new word{(newWordsCreated != 1 ? "s" : "")}\n";
                message += $"Linked {existingWordsLinked} existing word{(existingWordsLinked != 1 ? "s" : "")}";

                if (localDuplicatesSkipped > 0)
                {
                    message += $"\nSkipped {localDuplicatesSkipped} already in this resource";
                }

                message += $"\n\nTotal vocabulary for this resource: {State.Resource.Vocabulary.Count}";

                _logger.LogInformation("🏴‍☠️ Final stats: {NewWords} new, {ExistingWords} existing, {SkippedWords} skipped", newWordsCreated, existingWordsLinked, localDuplicatesSkipped);

                await IPopupService.Current.PushAsync(new SimpleActionPopup
                {
                    Title = "Vocabulary Generated!",
                    Text = message,
                    ActionButtonText = "OK",
                    ShowSecondaryActionButton = false
                });
            }
            else
            {
                _logger.LogWarning("⚠️ AI returned null or empty vocabulary list");
                SetState(s => s.IsGeneratingVocabulary = false);
                await IPopupService.Current.PushAsync(new SimpleActionPopup
                {
                    Title = "Error",
                    Text = "AI did not generate any vocabulary. There might be an issue with the AI service.",
                    ActionButtonText = "OK",
                    ShowSecondaryActionButton = false
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GenerateVocabulary error");
            SetState(s => s.IsGeneratingVocabulary = false);
            await IPopupService.Current.PushAsync(new SimpleActionPopup
            {
                Title = "Error",
                Text = $"An error occurred: {ex.Message}",
                ActionButtonText = "OK",
                ShowSecondaryActionButton = false
            });
        }
    }

    Task ViewVocabularyProgress()
    {
        return MauiControls.Shell.Current.GoToAsync<VocabularyProgressProps>(
            nameof(VocabularyLearningProgressPage),
            props =>
            {
                props.ResourceId = State.Resource.Id;
                props.Title = State.Resource.Title;
            });
    }

    Task NavigateToVocabularyManagement()
    {
        return MauiControls.Shell.Current.GoToAsync<VocabManagementProps>(
            nameof(VocabularyManagementPage),
            props =>
            {
                props.ResourceName = State.Resource.Title;
            });
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
            await IPopupService.Current.PushAsync(new SimpleActionPopup
            {
                Title = "Error",
                Text = "No vocabulary to import",
                ActionButtonText = "OK",
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
                    Title = "Error",
                    Text = "No valid vocabulary words found in the input",
                    ActionButtonText = "OK",
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
            var message = $"Added {addedCount} new vocabulary words.";
            if (duplicateCount > 0)
            {
                message += $" Skipped {duplicateCount} duplicates.";
            }

            await IPopupService.Current.PushAsync(new SimpleActionPopup
            {
                Title = "Import Complete",
                Text = message,
                ActionButtonText = "OK",
                ShowSecondaryActionButton = false
            });
        }
        catch (Exception ex)
        {
            await IPopupService.Current.PushAsync(new SimpleActionPopup
            {
                Title = "Error",
                Text = $"Failed to import vocabulary: {ex.Message}",
                ActionButtonText = "OK",
                ShowSecondaryActionButton = false
            });
        }
    }

    async Task CleanUpTranscript()
    {
        if (string.IsNullOrWhiteSpace(State.Resource.Transcript))
            return;

        try
        {
            SetState(s => s.IsCleaningTranscript = true);

            var cleanedTranscript = _formattingService.SmartCleanup(
                State.Resource.Transcript,
                State.Resource.Language
            );

            SetState(s =>
            {
                s.Resource.Transcript = cleanedTranscript;
                s.IsCleaningTranscript = false;
            });

            // Auto-save after cleaning
            await _resourceRepo.SaveResourceAsync(State.Resource);

            await AppShell.DisplayToastAsync("Transcript cleaned up and saved!");
        }
        catch (Exception ex)
        {
            SetState(s => s.IsCleaningTranscript = false);
            await IPopupService.Current.PushAsync(new SimpleActionPopup
            {
                Title = "Error",
                Text = $"Failed to clean up transcript: {ex.Message}",
                ActionButtonText = "OK",
                ShowSecondaryActionButton = false
            });
        }
    }

    async Task PolishTranscriptWithAi()
    {
        if (string.IsNullOrWhiteSpace(State.Resource.Transcript))
            return;

        try
        {
            SetState(s => s.IsPolishingTranscript = true);

            var originalTranscript = State.Resource.Transcript;
            var polishedTranscript = await _formattingService.PolishWithAiAsync(
                State.Resource.Transcript,
                State.Resource.Language
            );

            // Check if we actually got a result
            if (string.IsNullOrWhiteSpace(polishedTranscript))
            {
                SetState(s => s.IsPolishingTranscript = false);
                await IPopupService.Current.PushAsync(new SimpleActionPopup
            {
                Title = "Error",
                Text = "AI service returned empty result. Check your internet connection and API key.",
                ActionButtonText = "OK",
                ShowSecondaryActionButton = false
            });
                return;
            }

            // Check if the result is actually different
            if (polishedTranscript.Trim() == originalTranscript.Trim())
            {
                SetState(s => s.IsPolishingTranscript = false);
                await AppShell.DisplayToastAsync("Transcript already well-formatted (no changes needed)");
                return;
            }

            SetState(s =>
            {
                s.Resource.Transcript = polishedTranscript;
                s.IsPolishingTranscript = false;
            });

            // Auto-save after polishing
            await _resourceRepo.SaveResourceAsync(State.Resource);

            await AppShell.DisplayToastAsync("Transcript polished with AI and saved!");
        }
        catch (Exception ex)
        {
            SetState(s => s.IsPolishingTranscript = false);
            _logger.LogError(ex, "EditLearningResourcePage: ❌ Polish transcript error");
            await IPopupService.Current.PushAsync(new SimpleActionPopup
            {
                Title = "Error",
                Text = $"Failed to polish transcript: {ex.Message}",
                ActionButtonText = "OK",
                ShowSecondaryActionButton = false
            });
        }
    }
}