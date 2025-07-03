using System.Globalization;

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
                    .Hint($"{_localize["Name"]}"),

                    new SfTextInputLayout
                    {
                        Entry()
                            .Text(State.Email)
                            .OnTextChanged(text => SetState(s => s.Email = text))
                    }
                    .Hint($"{_localize["Email"]}"),                    new SfTextInputLayout
                    {
                        Picker()
                            .ItemsSource(Constants.Languages)
                            .SelectedIndex(State.NativeLanguageIndex)
                            .OnSelectedIndexChanged(index => SetState(s => {
                                s.NativeLanguage = Constants.Languages[index];
                                s.NativeLanguageIndex = index; // Save the index too!
                            }))
                    }
                    .Hint($"{_localize["NativeLanguage"]}"),

                    new SfTextInputLayout
                    {
                        Picker()
                            .ItemsSource(Constants.Languages)
                            .SelectedIndex(State.TargetLanguageIndex)
                            .OnSelectedIndexChanged(index => SetState(s => {
                                s.TargetLanguage = Constants.Languages[index];
                                s.TargetLanguageIndex = index; // Save the index too!
                            }))
                    }
                    .Hint($"{_localize["TargetLanguage"]}"),

                    new SfTextInputLayout
                    {
                        Picker()
                            .ItemsSource(DisplayLanguages)
                            .SelectedIndex(State.DisplayLanguageIndex)
                            .OnSelectedIndexChanged(index => {
                                string newDisplayLanguage = DisplayLanguages[index];
                                SetState(s => {
                                    s.DisplayLanguage = newDisplayLanguage;
                                    s.DisplayLanguageIndex = index; // Save the index too!
                                });
                                
                                // Set culture based on display language selection
                                var culture = newDisplayLanguage == "English" ? new CultureInfo("en-US") : new CultureInfo("ko-KR");
                                _localize.SetCulture(culture);
                            })
                    }
                    .Hint($"{_localize["DisplayLanguage"]}"),

                    new SfTextInputLayout
                    {
                        Entry()
                            .IsPassword(true)
                            .Text(State.OpenAI_APIKey)
                            .OnTextChanged(text => SetState(s => s.OpenAI_APIKey = text))
                    }
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

    readonly string[] DisplayLanguages = new[] { "English", "Korean" };

    async Task LoadProfile()
    {
        var profile = await _userProfileRepository.GetAsync();
        SetState(s =>
        {
            s.ProfileID = profile.Id;
            s.Name = profile.Name;
            s.Email = profile.Email;
            s.NativeLanguage = profile.NativeLanguage;
            s.TargetLanguage = profile.TargetLanguage;
            s.DisplayLanguage = profile.DisplayLanguage;
            s.OpenAI_APIKey = profile.OpenAI_APIKey;

            s.NativeLanguageIndex = Array.IndexOf(Constants.Languages, profile.NativeLanguage);
            s.TargetLanguageIndex = Array.IndexOf(Constants.Languages, profile.TargetLanguage);
            s.DisplayLanguageIndex = Array.IndexOf(DisplayLanguages, profile.DisplayLanguage);
        });
    }    async Task Save()
    {
        var profile = new UserProfile
        {
            Id = State.ProfileID,
            Name = State.Name,
            Email = State.Email,
            NativeLanguage = State.NativeLanguage,
            TargetLanguage = State.TargetLanguage,
            DisplayLanguage = State.DisplayLanguage,
            OpenAI_APIKey = State.OpenAI_APIKey
        };

        await _userProfileRepository.SaveAsync(profile);
        
        // Make sure to call SaveDisplayCultureAsync to properly update the culture
        string cultureCode = State.DisplayLanguage == "English" ? "en-US" : "ko-KR";
        await _userProfileRepository.SaveDisplayCultureAsync(cultureCode);
        
        await AppShell.DisplayToastAsync(_localize["Saved"].ToString());

        var lists = await _vocabularyService.GetListsAsync();
        if(lists.Count == 0)
        {
            var response = await Application.Current.MainPage.DisplayAlert("Vocabulary", 
                _localize["CreateStarterVocabPrompt"].ToString(), 
                _localize["Yes"].ToString(), 
                _localize["NoVocabPromptResponse"].ToString());
            if(response)
                await _vocabularyService.GetStarterVocabulary(profile.NativeLanguage, profile.TargetLanguage);
        }
    }    async Task Reset()
    {
        var response = await Application.Current.MainPage.DisplayAlert(
            _localize["Reset"].ToString(), 
            _localize["ResetProfileConfirmation"].ToString() ?? "Are you sure you want to reset your profile?", 
            _localize["Yes"].ToString(), 
            _localize["No"].ToString());
            
        if(response)
        {
            await _userProfileRepository.DeleteAsync();
            
            // Set culture back to English after reset
            _localize.SetCulture(new CultureInfo("en-US"));
            
            // Now reload the profile (which will create a new default one)
            await LoadProfile();
            
            // Update the UI to reflect the change
            SetState(s => s.DisplayLanguageIndex = Array.IndexOf(DisplayLanguages, "English"));
            
            await AppShell.DisplayToastAsync(_localize["ProfileReset"].ToString() ?? "Profile reset");
        }
    }

    Task GoToOpenAI() => 
        Browser.OpenAsync("https://platform.openai.com/account/api-keys");
}