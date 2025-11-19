using System.Globalization;
using CommunityToolkit.Maui.Storage;
using MauiReactor.Parameters;
using Microsoft.Maui.ApplicationModel.DataTransfer;

namespace SentenceStudio.Pages.Account;

class UserProfilePageState
{
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string NativeLanguage { get; set; } = string.Empty;
    public string TargetLanguage { get; set; } = string.Empty;
    public string DisplayLanguage { get; set; } = string.Empty;
    public string OpenAI_APIKey { get; set; } = string.Empty;
    public int PreferredSessionMinutes { get; set; } = 20;
    public int PreferredSessionMinutesIndex { get; set; } = 3; // 20 min is at index 3
    public string? TargetCEFRLevel { get; set; }
    public int TargetCEFRLevelIndex { get; set; } = 0; // "Not Set" is at index 0
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
                    Label($"{_localize["Name"]}")
                        .ThemeKey(MyTheme.Body1Strong),
                    Border(
                        Entry()
                            .Text(State.Name)
                            .OnTextChanged(text => SetState(s => s.Name = text))
                    ).ThemeKey(MyTheme.InputWrapper),

                    Label($"{_localize["Email"]}")
                        .ThemeKey(MyTheme.Body1Strong),
                    Border(
                        Entry()
                            .Text(State.Email)
                            .OnTextChanged(text => SetState(s => s.Email = text))
                    ).ThemeKey(MyTheme.InputWrapper),

                    Label($"{_localize["NativeLanguage"]}")
                                    .ThemeKey(MyTheme.Body1Strong),
                    Border(
                        Picker()
                            .ItemsSource(Constants.Languages)
                            .SelectedIndex(State.NativeLanguageIndex)
                            .OnSelectedIndexChanged(index => SetState(s =>
                            {
                                s.NativeLanguage = Constants.Languages[index];
                                s.NativeLanguageIndex = index; // Save the index too!
                            }))
                    ).ThemeKey(MyTheme.InputWrapper),

                    Label($"{_localize["TargetLanguage"]}")
                                    .ThemeKey(MyTheme.Body1Strong),
                    Border(
                        Picker()
                            .ItemsSource(Constants.Languages)
                            .SelectedIndex(State.TargetLanguageIndex)
                            .OnSelectedIndexChanged(index => SetState(s =>
                            {
                                s.TargetLanguage = Constants.Languages[index];
                                s.TargetLanguageIndex = index; // Save the index too!
                            }))
                    ).ThemeKey(MyTheme.InputWrapper),

                    Label($"{_localize["DisplayLanguage"]}")
                                    .ThemeKey(MyTheme.Body1Strong),

                    Border(
                        Picker()
                            .ItemsSource(DisplayLanguages)
                            .SelectedIndex(State.DisplayLanguageIndex)
                            .OnSelectedIndexChanged(index =>
                            {
                                string newDisplayLanguage = DisplayLanguages[index];
                                SetState(s =>
                                {
                                    s.DisplayLanguage = newDisplayLanguage;
                                    s.DisplayLanguageIndex = index; // Save the index too!
                                });

                                // Set culture based on display language selection
                                var culture = newDisplayLanguage == "English" ? new CultureInfo("en-US") : new CultureInfo("ko-KR");
                                _localize.SetCulture(culture);
                            })
                    ).ThemeKey(MyTheme.InputWrapper),

                    Label($"{_localize["OpenAI_APIKey"]}")
                        .ThemeKey(MyTheme.Body1Strong),
                    Border(
                        Entry()
                            .IsPassword(true)
                            .Text(State.OpenAI_APIKey)
                            .OnTextChanged(text => SetState(s => s.OpenAI_APIKey = text))
                    ).ThemeKey(MyTheme.InputWrapper),

                    Label("Get an API key from OpenAI to use the AI features in Sentence Studio.")
                        .TextDecorations(TextDecorations.Underline)
                        // .TextColor(Theme.IsLightTheme ? MyTheme.Secondary : MyTheme.SecondaryDark)
                        .OnTapped(GoToOpenAI),

                    // Learning Preferences Section
                    Label("Learning Preferences")
                        .ThemeKey(MyTheme.Title3)
                        .Margin(0, 20, 0, 10),

                    Label("Preferred Session Length")
                        .ThemeKey(MyTheme.Body1Strong),
                    Border(
                        Picker()
                            .ItemsSource(new[] { "5 minutes", "10 minutes", "15 minutes", "20 minutes", "25 minutes", "30 minutes", "45 minutes" })
                            .SelectedIndex(State.PreferredSessionMinutesIndex)
                            .OnSelectedIndexChanged(index =>
                            {
                                var minutes = new[] { 5, 10, 15, 20, 25, 30, 45 }[index];
                                SetState(s =>
                                {
                                    s.PreferredSessionMinutes = minutes;
                                    s.PreferredSessionMinutesIndex = index;
                                });
                            })
                    ).ThemeKey(MyTheme.InputWrapper),
                    Label("How long you'd like to practice each day. Plans adapt to your choice.")
                        .FontSize(12)
                        .TextColor(Colors.Gray)
                        .Margin(0, 4, 0, 12),

                    Label("Target CEFR Level (Optional)")
                        .ThemeKey(MyTheme.Body1Strong),
                    Border(
                        Picker()
                            .ItemsSource(new[] { "Not Set", "A1 - Beginner", "A2 - Elementary", "B1 - Intermediate", "B2 - Upper Intermediate", "C1 - Advanced", "C2 - Mastery" })
                            .SelectedIndex(State.TargetCEFRLevelIndex)
                            .OnSelectedIndexChanged(index =>
                            {
                                var levels = new string?[] { null, "A1", "A2", "B1", "B2", "C1", "C2" };
                                SetState(s =>
                                {
                                    s.TargetCEFRLevel = levels[index];
                                    s.TargetCEFRLevelIndex = index;
                                });
                            })
                    ).ThemeKey(MyTheme.InputWrapper),
                    Label("Your language proficiency goal. Helps AI choose appropriate resources.")
                        .FontSize(12)
                        .TextColor(Colors.Gray)
                        .Margin(0, 4, 0, 12),

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
                                .ImageSource(MyTheme.IconSave)
                                .HorizontalOptions(LayoutOptions.FillAndExpand)
                                .Margin(0, 0, 5, 0),

                            Button($"{_localize["Share"]}")
                                .OnClicked(ExportAndShare)
                                .ImageSource(MyTheme.IconShare)
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
                .Spacing(MyTheme.Size320)
                .Padding(24)
            )
        ).OnAppearing(LoadProfile);
    }

    readonly string[] DisplayLanguages = new[] { "English", "Korean" };

    async Task LoadProfile()
    {
        var profile = await _userProfileRepository.GetOrCreateDefaultAsync();
        
        // Map minutes to index
        var sessionMinutesOptions = new[] { 5, 10, 15, 20, 25, 30, 45 };
        var sessionIndex = Array.IndexOf(sessionMinutesOptions, profile.PreferredSessionMinutes);
        if (sessionIndex == -1) sessionIndex = 3; // Default to 20 min
        
        // Map CEFR level to index
        var cefrLevels = new string?[] { null, "A1", "A2", "B1", "B2", "C1", "C2" };
        var cefrIndex = Array.IndexOf(cefrLevels, profile.TargetCEFRLevel);
        if (cefrIndex == -1) cefrIndex = 0; // Default to "Not Set"
        
        SetState(s =>
        {
            s.ProfileID = profile.Id;
            s.Name = profile.Name;
            s.Email = profile.Email;
            s.NativeLanguage = profile.NativeLanguage;
            s.TargetLanguage = profile.TargetLanguage;
            s.DisplayLanguage = profile.DisplayLanguage;
            s.OpenAI_APIKey = profile.OpenAI_APIKey;
            s.PreferredSessionMinutes = profile.PreferredSessionMinutes;
            s.PreferredSessionMinutesIndex = sessionIndex;
            s.TargetCEFRLevel = profile.TargetCEFRLevel;
            s.TargetCEFRLevelIndex = cefrIndex;

            s.NativeLanguageIndex = Array.IndexOf(Constants.Languages, profile.NativeLanguage);
            s.TargetLanguageIndex = Array.IndexOf(Constants.Languages, profile.TargetLanguage);
            s.DisplayLanguageIndex = Array.IndexOf(DisplayLanguages, profile.DisplayLanguage);
        });
    }
    async Task Save()
    {
        var profile = new UserProfile
        {
            Id = State.ProfileID,
            Name = State.Name,
            Email = State.Email,
            NativeLanguage = State.NativeLanguage,
            TargetLanguage = State.TargetLanguage,
            DisplayLanguage = State.DisplayLanguage,
            OpenAI_APIKey = State.OpenAI_APIKey,
            PreferredSessionMinutes = State.PreferredSessionMinutes,
            TargetCEFRLevel = State.TargetCEFRLevel
        };

        await _userProfileRepository.SaveAsync(profile);

        // Make sure to call SaveDisplayCultureAsync to properly update the culture
        string cultureCode = State.DisplayLanguage == "English" ? "en-US" : "ko-KR";
        await _userProfileRepository.SaveDisplayCultureAsync(cultureCode);

        await AppShell.DisplayToastAsync($"{_localize["Saved"]}");


    }

    async Task Reset()
    {
        var response = await Application.Current.MainPage.DisplayAlert(
            $"{_localize["Reset"]}",
            $"{_localize["ResetProfileConfirmation"]}" ?? "Are you sure you want to reset your profile?",
            $"{_localize["Yes"]}",
            $"{_localize["No"]}");

        if (response)
        {
            await _userProfileRepository.DeleteAsync();

            // Clear the onboarding preference so user goes through onboarding again
            Preferences.Default.Remove("is_onboarded");

            // Update the app state to reflect no profile exists
            _appState.Set(s => s.CurrentUserProfile = null);

            // Set culture back to English after reset
            _localize.SetCulture(new CultureInfo("en-US"));

            await AppShell.DisplayToastAsync($"{_localize["ProfileReset"]}" ?? "Profile reset");

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
            SetState(s =>
            {
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
                SetState(s =>
                {
                    s.IsExporting = false;
                    s.LastExportFilePath = result.FilePath;
                    s.ExportProgressMessage = "Export completed!";
                });

                await AppShell.DisplayToastAsync($"{_localize["ExportCompleted"]}");
            }
            else
            {
                SetState(s =>
                {
                    s.IsExporting = false;
                    s.ExportProgressMessage = "Export failed";
                });

                await Application.Current.MainPage.DisplayAlert($"{_localize["ExportError"]}",
                    result.Exception?.Message ?? "Failed to save export file", "OK");
            }
        }
        catch (Exception ex)
        {
            SetState(s =>
            {
                s.IsExporting = false;
                s.ExportProgressMessage = "Export failed";
            });

            await Application.Current.MainPage.DisplayAlert($"{_localize["ExportError"]}",
                $"An error occurred during export: {ex.Message}", "OK");
        }
    }

    async Task ExportAndShare()
    {
        try
        {
            SetState(s =>
            {
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

                SetState(s =>
                {
                    s.IsExporting = false;
                    s.LastExportFilePath = tempResult.FilePath;
                    s.ExportProgressMessage = "Export shared!";
                });
            }
            else
            {
                SetState(s =>
                {
                    s.IsExporting = false;
                    s.ExportProgressMessage = "Export failed";
                });

                await Application.Current.MainPage.DisplayAlert($"{_localize["ExportError"]}",
                    tempResult.Exception?.Message ?? "Failed to prepare export for sharing", "OK");
            }
        }
        catch (Exception ex)
        {
            SetState(s =>
            {
                s.IsExporting = false;
                s.ExportProgressMessage = "Export failed";
            });

            await Application.Current.MainPage.DisplayAlert($"{_localize["ExportError"]}",
                $"An error occurred during export: {ex.Message}", "OK");
        }
    }
}