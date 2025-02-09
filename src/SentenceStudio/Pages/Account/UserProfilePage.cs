using SentenceStudio.Resources.Styles;

namespace SentenceStudio.Pages.Account;

class UserProfilePageState
{
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string NativeLanguage { get; set; } = string.Empty;
    public string TargetLanguage { get; set; } = string.Empty;
    public string DisplayLanguage { get; set; } = string.Empty;
    public string OpenAI_APIKey { get; set; } = string.Empty;
    public int NativeLanguageIndex { get; internal set; }
    public int TargetLanguageIndex { get; internal set; }
    public int DisplayLanguageIndex { get; internal set; }
    public int ProfileID { get; internal set; }
}

partial class UserProfilePage : Component<UserProfilePageState>
{
    [Inject] UserProfileRepository _userProfileRepository;
    [Inject] VocabularyService _vocabularyService;
    LocalizationManager _localize => LocalizationManager.Instance;

    public override VisualNode Render()
    {
        return ContentPage($"{_localize["UserProfile"]}",
            ToolbarItem($"{_localize["Reset"]}").OnClicked(Reset),
            VScrollView(
                VStack(
                    new SfTextInputLayout
                    {
                        Entry()
                            .Text(State.Name)
                            .OnTextChanged(text => SetState(s => s.Name = text))
                    }
                    .ContainerType(Syncfusion.Maui.Toolkit.TextInputLayout.ContainerType.Filled)
                    .ContainerBackground(Colors.White)
                    .Hint($"{_localize["Name"]}"),

                    new SfTextInputLayout
                    {
                        Entry()
                            .Text(State.Email)
                            .OnTextChanged(text => SetState(s => s.Email = text))
                    }
                    .ContainerType(Syncfusion.Maui.Toolkit.TextInputLayout.ContainerType.Filled)
                    .ContainerBackground(Colors.White)
                    .Hint($"{_localize["Email"]}"),

                    new SfTextInputLayout
                    {
                        Picker()
                            .ItemsSource(Languages)
                            .SelectedIndex(State.NativeLanguageIndex)
                            .OnSelectedIndexChanged(index => SetState(s => s.NativeLanguage = Languages[index]))
                    }
                    .ContainerType(Syncfusion.Maui.Toolkit.TextInputLayout.ContainerType.Filled)
                    .ContainerBackground(Colors.White)
                    .Hint($"{_localize["NativeLanguage"]}"),

                    new SfTextInputLayout
                    {
                        Picker()
                            .ItemsSource(Languages)
                            .SelectedIndex(State.TargetLanguageIndex)
                            .OnSelectedIndexChanged(index => SetState(s => s.TargetLanguage = Languages[index]))
                    }
                    .ContainerType(Syncfusion.Maui.Toolkit.TextInputLayout.ContainerType.Filled)
                    .ContainerBackground(Colors.White)
                    .Hint($"{_localize["TargetLanguage"]}"),

                    new SfTextInputLayout
                    {
                        Picker()
                            .ItemsSource(DisplayLanguages)
                            .SelectedIndex(State.DisplayLanguageIndex)
                            .OnSelectedIndexChanged(index => SetState(s => s.DisplayLanguage = DisplayLanguages[index]))
                    }
                    .ContainerType(Syncfusion.Maui.Toolkit.TextInputLayout.ContainerType.Filled)
                    .ContainerBackground(Colors.White)
                    .Hint($"{_localize["DisplayLanguage"]}"),

                    new SfTextInputLayout
                    {
                        Entry()
                            .IsPassword(true)
                            .Text(State.OpenAI_APIKey)
                            .OnTextChanged(text => SetState(s => s.OpenAI_APIKey = text))
                    }
                    .ContainerType(Syncfusion.Maui.Toolkit.TextInputLayout.ContainerType.Filled)
                    .ContainerBackground(Colors.White)
                    .Hint($"{_localize["OpenAI_APIKey"]}"),

                    Label("Get an API key from OpenAI to use the AI features in Sentence Studio.")
                        .TextDecorations(TextDecorations.Underline)
                        // .TextColor(Theme.IsLightTheme ? ApplicationTheme.Secondary : ApplicationTheme.SecondaryDark)
                        .OnTapped(GoToOpenAI),

                    Button($"{_localize["Save"]}")
                        .OnClicked(Save)
                        .HorizontalOptions(DeviceInfo.Idiom == DeviceIdiom.Desktop ? LayoutOptions.Start : LayoutOptions.Fill)
                        .WidthRequest(DeviceInfo.Idiom == DeviceIdiom.Desktop ? 300 : -1)
                )
                .Spacing((double)Application.Current.Resources["size320"])
                .Padding(24)
            )
        ).OnAppearing(LoadProfile);
    }

    private readonly string[] Languages = new[]
    {
        "English", "Spanish", "French", "German", "Italian", "Portuguese",
        "Chinese", "Japanese", "Korean", "Arabic", "Russian", "Other"
    };

    private readonly string[] DisplayLanguages = new[] { "English", "Korean" };

    private async Task LoadProfile()
    {
        var profile = await _userProfileRepository.GetAsync();
        SetState(s =>
        {
            s.ProfileID = profile.ID;
            s.Name = profile.Name;
            s.Email = profile.Email;
            s.NativeLanguage = profile.NativeLanguage;
            s.TargetLanguage = profile.TargetLanguage;
            s.DisplayLanguage = profile.DisplayLanguage;
            s.OpenAI_APIKey = profile.OpenAI_APIKey;

            s.NativeLanguageIndex = Array.IndexOf(Languages, profile.NativeLanguage);
            s.TargetLanguageIndex = Array.IndexOf(Languages, profile.TargetLanguage);
        });
    }

    private async Task Save()
    {
        var profile = new UserProfile
        {
            ID = State.ProfileID,
            Name = State.Name,
            Email = State.Email,
            NativeLanguage = State.NativeLanguage,
            TargetLanguage = State.TargetLanguage,
            DisplayLanguage = State.DisplayLanguage,
            OpenAI_APIKey = State.OpenAI_APIKey
        };

        await _userProfileRepository.SaveAsync(profile);
        await AppShell.DisplayToastAsync(_localize["Saved"].ToString());

        var lists = await _vocabularyService.GetListsAsync();
        if(lists.Count == 0)
        {
            var response = await Application.Current.MainPage.DisplayAlert("Vocabulary", 
                "Would you like me to create a starter vocabulary list for you?", "Yes", "No, I'll do it myself");
            if(response)
                await _vocabularyService.GetStarterVocabulary(profile.NativeLanguage, profile.TargetLanguage);
        }
    }

    private async Task Reset()
    {
        var response = await Application.Current.MainPage.DisplayAlert("Reset", "Are you sure you want to reset your profile?", "Yes", "No");
        if(response)
        {
            await _userProfileRepository.DeleteAsync();
            await LoadProfile();
        }
    }

    private async Task GoToOpenAI() => 
        await Browser.OpenAsync("https://platform.openai.com/account/api-keys");
}