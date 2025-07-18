using System.Globalization;
using CommunityToolkit.Maui.Storage;
using MauiReactor.Parameters;

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
    
    // Export-related properties
    public bool IsExporting { get; set; } = false;
    public string ExportProgressMessage { get; set; } = string.Empty;
    public string LastExportFilePath { get; set; } = string.Empty;
}

partial class UserProfilePage : Component<UserProfilePageState>
{
    [Inject] UserProfileRepository _userProfileRepository;
    [Inject] LearningResourceRepository _learningResourceRepository;
    [Inject] DataExportService _dataExportService;
    [Inject] IFileSaver _fileSaver;
    [Param] IParameter<AppState> _appState;
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
                        .WidthRequest(DeviceInfo.Idiom == DeviceIdiom.Desktop ? 300 : -1),

                    // Export Data Section
                    VStack(
                        Label($"{_localize["ExportData"]}")
                            .FontAttributes(FontAttributes.Bold)
                            .FontSize(18)
                            .Margin(0, 20, 0, 10),

                        Label($"{_localize["ExportDataDescription"]}")
                            .FontSize(14)
                            .TextColor(Colors.Gray)
                            .Margin(0, 0, 0, 15),

                        State.IsExporting ? 
                        VStack(
                            ActivityIndicator()
                                .IsRunning(true)
                                .HCenter()
                                .HeightRequest(30)
                                .WidthRequest(30)
                                .Margin(0, 0, 0, 10),
                            Label(State.ExportProgressMessage)
                                .FontSize(14)
                                .HCenter()
                                .TextColor(Colors.Gray)
                        ) :
                        HStack(
                            Button($"{_localize["SaveToDevice"]}")
                                .OnClicked(ExportDataToFile)
                                .ImageSource(ApplicationTheme.IconSave)
                                .HorizontalOptions(LayoutOptions.FillAndExpand)
                                .Margin(0, 0, 5, 0),

                            Button($"{_localize["Share"]}")
                                .OnClicked(ExportAndShare)
                                .ImageSource(ApplicationTheme.IconShare)
                                .HorizontalOptions(LayoutOptions.FillAndExpand)
                                .Margin(5, 0, 0, 0)
                        ),

                        !string.IsNullOrEmpty(State.LastExportFilePath) ?
                        Label($"Last exported: {State.LastExportFilePath}")
                            .FontSize(12)
                            .TextColor(Colors.Gray)
                            .Margin(0, 10, 0, 0) : null
                    )
                )
                .Spacing(ApplicationTheme.Size320)
                .Padding(24)
            )
        ).OnAppearing(LoadProfile);
    }

    readonly string[] DisplayLanguages = new[] { "English", "Korean" };

    async Task LoadProfile()
    {
        var profile = await _userProfileRepository.GetOrCreateDefaultAsync();
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

        var resources = await _learningResourceRepository.GetVocabularyListsAsync();
        if(resources.Count == 0)
        {
            var response = await Application.Current.MainPage.DisplayAlert("Vocabulary", 
                _localize["CreateStarterVocabPrompt"].ToString(), 
                _localize["Yes"].ToString(), 
                _localize["NoVocabPromptResponse"].ToString());
            if(response)
                await _learningResourceRepository.GetStarterVocabulary(profile.NativeLanguage, profile.TargetLanguage);
        }
    }    
    
    async Task Reset()
    {
        var response = await Application.Current.MainPage.DisplayAlert(
            _localize["Reset"].ToString(), 
            _localize["ResetProfileConfirmation"].ToString() ?? "Are you sure you want to reset your profile?", 
            _localize["Yes"].ToString(), 
            _localize["No"].ToString());
            
        if(response)
        {
            await _userProfileRepository.DeleteAsync();
            
            // Clear the onboarding preference so user goes through onboarding again
            Preferences.Default.Remove("is_onboarded");
            
            // Update the app state to reflect no profile exists
            _appState.Set(s => s.CurrentUserProfile = null);
            
            // Set culture back to English after reset
            _localize.SetCulture(new CultureInfo("en-US"));
            
            await AppShell.DisplayToastAsync(_localize["ProfileReset"].ToString() ?? "Profile reset");
            
            // Navigate back to root which should trigger the AppShell to re-evaluate and show onboarding
            await MauiControls.Shell.Current.GoToAsync("//");
        }
    }

    Task GoToOpenAI() => 
        Browser.OpenAsync("https://platform.openai.com/account/api-keys");

    async Task ExportDataToFile()
    {
        try
        {
            SetState(s => {
                s.IsExporting = true;
                s.ExportProgressMessage = "Starting export...";
            });

            var progress = new Progress<string>(message => 
            {
                SetState(s => s.ExportProgressMessage = message);
            });

            using var zipStream = await _dataExportService.ExportAllDataAsZipAsync(progress);
            
            SetState(s => s.ExportProgressMessage = "Saving file...");

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string fileName = $"SentenceStudio_Export_{timestamp}.zip";

            var result = await _fileSaver.SaveAsync(fileName, zipStream, CancellationToken.None);

            if (result.IsSuccessful)
            {
                SetState(s => {
                    s.IsExporting = false;
                    s.LastExportFilePath = result.FilePath;
                    s.ExportProgressMessage = "Export completed!";
                });

                await AppShell.DisplayToastAsync(_localize["ExportCompleted"].ToString());
            }
            else
            {
                SetState(s => {
                    s.IsExporting = false;
                    s.ExportProgressMessage = "Export failed";
                });

                await Application.Current.MainPage.DisplayAlert(_localize["ExportError"].ToString(), 
                    result.Exception?.Message ?? "Failed to save export file", "OK");
            }
        }
        catch (Exception ex)
        {
            SetState(s => {
                s.IsExporting = false;
                s.ExportProgressMessage = "Export failed";
            });

            await Application.Current.MainPage.DisplayAlert(_localize["ExportError"].ToString(), 
                $"An error occurred during export: {ex.Message}", "OK");
        }
    }

    async Task ExportAndShare()
    {
        try
        {
            SetState(s => {
                s.IsExporting = true;
                s.ExportProgressMessage = "Preparing export for sharing...";
            });

            var progress = new Progress<string>(message => 
            {
                SetState(s => s.ExportProgressMessage = message);
            });

            using var zipStream = await _dataExportService.ExportAllDataAsZipAsync(progress);
            
            SetState(s => s.ExportProgressMessage = "Opening share dialog...");

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string fileName = $"SentenceStudio_Export_{timestamp}.zip";

            // Save to temporary location first
            var tempResult = await _fileSaver.SaveAsync(fileName, zipStream, CancellationToken.None);

            if (tempResult.IsSuccessful)
            {
                // Use Share API to share the file
                await Share.RequestAsync(new ShareFileRequest
                {
                    Title = "Sentence Studio Data Export",
                    File = new ShareFile(tempResult.FilePath)
                });

                SetState(s => {
                    s.IsExporting = false;
                    s.LastExportFilePath = tempResult.FilePath;
                    s.ExportProgressMessage = "Export shared!";
                });
            }
            else
            {
                SetState(s => {
                    s.IsExporting = false;
                    s.ExportProgressMessage = "Export failed";
                });

                await Application.Current.MainPage.DisplayAlert(_localize["ExportError"].ToString(), 
                    tempResult.Exception?.Message ?? "Failed to prepare export for sharing", "OK");
            }
        }
        catch (Exception ex)
        {
            SetState(s => {
                s.IsExporting = false;
                s.ExportProgressMessage = "Export failed";
            });

            await Application.Current.MainPage.DisplayAlert(_localize["ExportError"].ToString(), 
                $"An error occurred during export: {ex.Message}", "OK");
        }
    }
}