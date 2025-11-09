using Microsoft.Extensions.Logging;
using MauiReactor.Shapes;

namespace SentenceStudio.Pages.VocabularyManagement;

class VocabularyWordProps
{
    public int VocabularyWordId { get; set; }
}

class EditVocabularyWordPageState
{
    public bool IsLoading { get; set; } = true;
    public bool IsSaving { get; set; } = false;
    public VocabularyWord Word { get; set; } = new();
    public List<LearningResource> AvailableResources { get; set; } = new();
    public List<LearningResource> AssociatedResources { get; set; } = new();
    public HashSet<int> SelectedResourceIds { get; set; } = new();

    // Form fields
    public string TargetLanguageTerm { get; set; } = string.Empty;
    public string NativeLanguageTerm { get; set; } = string.Empty;

    // UI state
    public string ErrorMessage { get; set; } = string.Empty;
}

partial class EditVocabularyWordPage : Component<EditVocabularyWordPageState, VocabularyWordProps>
{
    [Inject] LearningResourceRepository _resourceRepo;
    [Inject] ILogger<EditVocabularyWordPage> _logger;
    LocalizationManager _localize => LocalizationManager.Instance;

    public override VisualNode Render()
    {
        return ContentPage(Props.VocabularyWordId == 0 ? $"{_localize["AddVocabularyWord"]}" : $"{_localize["EditVocabularyWord"]}",
            State.IsLoading ?
                VStack(
                    ActivityIndicator().IsRunning(true).Center()
                ).VCenter().HCenter() :
                Grid(rows: "*,Auto", columns: "*",
                    ScrollView(
                        VStack(spacing: MyTheme.SectionSpacing,
                            RenderWordForm(),
                            RenderResourceAssociations()
                        ).Padding(MyTheme.LayoutSpacing)
                    ),
                    RenderActionButtons()
                ).Set(Layout.SafeAreaEdgesProperty, new SafeAreaEdges(SafeAreaRegions.None))
        )
        .Set(Layout.SafeAreaEdgesProperty, new SafeAreaEdges(SafeAreaRegions.None))
        .OnAppearing(LoadData);
    }

    VisualNode RenderWordForm() =>
        VStack(spacing: 16,
            Label($"{_localize["VocabularyTerms"]}")
                .FontSize(20)
                .FontAttributes(FontAttributes.Bold),

            // Target Language
            VStack(spacing: 8,
                Label($"{_localize["TargetLanguageTerm"]}")
                    .FontSize(14)
                    .FontAttributes(FontAttributes.Bold),
                Border(
                    Entry()
                        .Text(State.TargetLanguageTerm)
                        .OnTextChanged(text => SetState(s => s.TargetLanguageTerm = text))
                        .Placeholder($"{_localize["EnterTargetLanguageTerm"]}")
                        .FontSize(16)
                )
                .ThemeKey(MyTheme.InputWrapper)
                .Padding(MyTheme.CardPadding)
            ),

            // Native Language  
            VStack(spacing: 8,
                Label($"{_localize["NativeLanguageTerm"]}")
                    .FontSize(14)
                    .FontAttributes(FontAttributes.Bold),
                Border(
                    Entry()
                        .Text(State.NativeLanguageTerm)
                        .OnTextChanged(text => SetState(s => s.NativeLanguageTerm = text))
                        .Placeholder($"{_localize["EnterNativeLanguageTerm"]}")
                        .FontSize(16)
                )
                .ThemeKey(MyTheme.InputWrapper)
                .Padding(MyTheme.CardPadding)
            ),

            // Error message
            !string.IsNullOrEmpty(State.ErrorMessage) ?
                Label(State.ErrorMessage)
                    .TextColor(MyTheme.SupportErrorDark)
                    .FontSize(12)
                    .HStart() :
                null
        );

    VisualNode RenderResourceAssociations() =>
        VStack(spacing: 16,
            HStack(spacing: 10,
                Label($"{_localize["ResourceAssociations"]}")
                    .FontSize(20)
                    .FontAttributes(FontAttributes.Bold)
                    .VCenter()
                    .HorizontalOptions(LayoutOptions.FillAndExpand),

                Label(string.Format($"{_localize["Selected"]}", State.SelectedResourceIds.Count))
                    .FontSize(12)
                    .TextColor(MyTheme.Gray600)
                    .VCenter()
            ),

            Label($"{_localize["SelectResourceToAssociate"]}")
                .FontSize(14)
                .TextColor(MyTheme.Gray600),

            State.AvailableResources.Any() ?
                VStack(spacing: 8,
                    State.AvailableResources.Select(resource =>
                        RenderResourceItem(resource)
                    ).ToArray()
                ) :
                Label($"{_localize["NoResourcesAvailable"]}")
                    .FontSize(14)
                    .TextColor(MyTheme.Gray500)
                    .FontAttributes(FontAttributes.Italic)
                    .Center()
        );

    VisualNode RenderResourceItem(LearningResource resource) =>
        Border(
            HStack(
                CheckBox()
                    .IsChecked(State.SelectedResourceIds.Contains(resource.Id))
                    .OnCheckedChanged(isChecked => ToggleResourceSelection(resource.Id, isChecked))
                    .VCenter(),

                VStack(spacing: 4,
                    Label(resource.Title ?? "Unknown Resource")
                        .FontSize(16)
                        .FontAttributes(FontAttributes.Bold),

                    resource.Description != null ?
                        Label(resource.Description)
                            .FontSize(12)
                            .TextColor(MyTheme.Gray600)
                            .MaxLines(2) :
                        null
                ).VCenter().HorizontalOptions(LayoutOptions.FillAndExpand)

            )
        )
        .Stroke(State.SelectedResourceIds.Contains(resource.Id) ? MyTheme.Success : MyTheme.Gray300)
        .StrokeThickness(1)
        .Background(State.SelectedResourceIds.Contains(resource.Id) ?
            (Theme.IsLightTheme ? MyTheme.SupportSuccessLight : MyTheme.DarkSecondaryBackground) :
            (Theme.IsLightTheme ? MyTheme.LightSecondaryBackground : MyTheme.DarkSecondaryBackground))  // Let the theme handle the default background
        .OnTapped(() => ToggleResourceSelection(resource.Id, !State.SelectedResourceIds.Contains(resource.Id)));

    VisualNode RenderActionButtons() =>
        Grid(
            rows: "Auto,Auto",
            columns: Props.VocabularyWordId > 0 ? "*,Auto" : "*",
            // Save/Add button on the left
            Button(Props.VocabularyWordId == 0 ? "Add Vocabulary Word" : "Save Changes")
                .ThemeKey("Primary")
                .OnClicked(SaveVocabularyWord)
                .IsEnabled(!State.IsSaving &&
                          !string.IsNullOrWhiteSpace(State.TargetLanguageTerm.Trim()) &&
                          !string.IsNullOrWhiteSpace(State.NativeLanguageTerm.Trim()))
                .FontSize(16)
                .Padding(MyTheme.LayoutSpacing, MyTheme.CardPadding)
                .GridRow(0)
                .GridColumn(0),

            // Delete icon button on the right (only for existing words)
            Props.VocabularyWordId > 0 ?
                ImageButton()
                    .Set(Microsoft.Maui.Controls.ImageButton.SourceProperty, MyTheme.IconDelete)
                    .BackgroundColor(MyTheme.LightSecondaryBackground)
                    .HeightRequest(36)
                    .WidthRequest(36)
                    .CornerRadius(18)
                    .Padding(MyTheme.MicroSpacing)
                    .OnClicked(DeleteVocabularyWord)
                    .IsEnabled(!State.IsSaving)
                    .GridRow(0)
                    .GridColumn(1) :
                null,

            // Loading indicator row
            State.IsSaving ?
                HStack(spacing: 8,
                    ActivityIndicator()
                        .IsRunning(true)
                        .Scale(0.8),
                    Label("Saving...")
                        .FontSize(14)
                        .TextColor(MyTheme.Gray600)
                        .VCenter()
                )
                .HCenter()
                .GridRow(1)
                .GridColumnSpan(Props.VocabularyWordId > 0 ? 2 : 1) :
                null
        )
        .ThemeKey(MyTheme.Surface1)
        .GridRow(1);

    async Task LoadData()
    {
        SetState(s => s.IsLoading = true);

        try
        {
            VocabularyWord? word = null;

            // Load existing word or create new one
            if (Props.VocabularyWordId > 0)
            {
                word = await _resourceRepo.GetVocabularyWordByIdAsync(Props.VocabularyWordId);
                if (word == null)
                {
                    await Application.Current.MainPage.DisplayAlert("Error", "Vocabulary word not found", "OK");
                    await NavigateBack();
                    return;
                }
            }
            else
            {
                // Create new word for adding
                word = new VocabularyWord
                {
                    Id = 0,
                    TargetLanguageTerm = string.Empty,
                    NativeLanguageTerm = string.Empty,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
            }

            // Load all available resources
            var allResources = await _resourceRepo.GetAllResourcesAsync();

            // Load associated resources for this word
            var associatedResources = await _resourceRepo.GetResourcesForVocabularyWordAsync(Props.VocabularyWordId);

            SetState(s =>
            {
                s.Word = word;
                s.TargetLanguageTerm = word.TargetLanguageTerm ?? string.Empty;
                s.NativeLanguageTerm = word.NativeLanguageTerm ?? string.Empty;
                s.AvailableResources = allResources?.ToList() ?? new List<LearningResource>();
                s.AssociatedResources = associatedResources?.ToList() ?? new List<LearningResource>();
                s.SelectedResourceIds = new HashSet<int>(associatedResources?.Select(r => r.Id) ?? Enumerable.Empty<int>());
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load vocabulary word data");
            await Application.Current.MainPage.DisplayAlert("Error", $"Failed to load vocabulary word: {ex.Message}", "OK");
            await NavigateBack();
        }
        finally
        {
            SetState(s => s.IsLoading = false);
        }
    }

    void ToggleResourceSelection(int resourceId, bool isSelected)
    {
        SetState(s =>
        {
            if (isSelected)
            {
                s.SelectedResourceIds.Add(resourceId);
            }
            else
            {
                s.SelectedResourceIds.Remove(resourceId);
            }
        });
    }

    async Task SaveVocabularyWord()
    {
        SetState(s =>
        {
            s.IsSaving = true;
            s.ErrorMessage = string.Empty;
        });

        try
        {
            var targetTerm = State.TargetLanguageTerm.Trim();
            var nativeTerm = State.NativeLanguageTerm.Trim();

            if (string.IsNullOrEmpty(targetTerm) || string.IsNullOrEmpty(nativeTerm))
            {
                SetState(s => s.ErrorMessage = "Both target and native language terms are required");
                return;
            }

            // Check for duplicates (excluding current word)
            var existingWord = await _resourceRepo.FindDuplicateVocabularyWordAsync(targetTerm, nativeTerm);
            if (existingWord != null && existingWord.Id != State.Word.Id)
            {
                SetState(s => s.ErrorMessage = "A vocabulary word with these terms already exists");
                return;
            }

            // Update the word
            State.Word.TargetLanguageTerm = targetTerm;
            State.Word.NativeLanguageTerm = nativeTerm;
            State.Word.UpdatedAt = DateTime.UtcNow;

            await _resourceRepo.SaveWordAsync(State.Word);

            // Handle resource associations (only for words with valid IDs)
            if (State.Word.Id > 0)
            {
                var currentResourceIds = State.AssociatedResources.Select(r => r.Id).ToHashSet();
                var newResourceIds = State.SelectedResourceIds;

                // Remove associations
                foreach (var resourceId in currentResourceIds.Except(newResourceIds))
                {
                    await _resourceRepo.RemoveVocabularyFromResourceAsync(resourceId, State.Word.Id);
                }

                // Add associations
                foreach (var resourceId in newResourceIds.Except(currentResourceIds))
                {
                    await _resourceRepo.AddVocabularyToResourceAsync(resourceId, State.Word.Id);
                }
            }
            else
            {
                // For new words, just add the selected associations
                foreach (var resourceId in State.SelectedResourceIds)
                {
                    await _resourceRepo.AddVocabularyToResourceAsync(resourceId, State.Word.Id);
                }
            }


        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save vocabulary word");
            SetState(s => s.ErrorMessage = $"Failed to save: {ex.Message}");
        }
        finally
        {
            SetState(s => s.IsSaving = false);
            await AppShell.DisplayToastAsync(Props.VocabularyWordId == 0 ?
                "✅ Vocabulary word added successfully!" :
                "✅ Vocabulary word updated successfully!");
            await NavigateBack();
        }
    }

    async Task DeleteVocabularyWord()
    {
        bool confirm = await Application.Current.MainPage.DisplayAlert(
            "Confirm Delete",
            $"Are you sure you want to delete '{State.Word.TargetLanguageTerm}'?\n\nThis action cannot be undone.",
            "Delete", "Cancel");

        if (!confirm) return;

        SetState(s => s.IsSaving = true);

        try
        {
            await _resourceRepo.DeleteVocabularyWordAsync(State.Word.Id);

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete vocabulary word");
            await Application.Current.MainPage.DisplayAlert("Error", $"Failed to delete vocabulary word: {ex.Message}", "OK");
        }
        finally
        {
            SetState(s => s.IsSaving = false);
            await AppShell.DisplayToastAsync("🗑️ Vocabulary word deleted successfully!");
            await NavigateBack();
        }
    }

    Task NavigateBack()
    {
        return MauiControls.Shell.Current.GoToAsync("..");
    }
}