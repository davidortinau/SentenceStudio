using SentenceStudio.Data;
using SentenceStudio.Models;
using SentenceStudio.Services;

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
}

partial class AddLearningResourcePage : Component<AddLearningResourceState>
{
    [Inject] LearningResourceRepository _resourceRepo;
    LocalizationManager _localize => LocalizationManager.Instance;

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
                                .Style((Style)Application.Current.Resources["InputWrapper"])
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
                                .Style((Style)Application.Current.Resources["InputWrapper"])
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
                                .Style((Style)Application.Current.Resources["InputWrapper"])
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
        
        // Save the resource
        await _resourceRepo.SaveResourceAsync(State.Resource);
        
        SetState(s => s.IsLoading = false);
        
        // Navigate back to list
        await MauiControls.Shell.Current.GoToAsync("..");
    }
}