using MauiReactor;
using MauiReactor.Shapes;
using Microsoft.Extensions.Logging;
using SentenceStudio.Services;
using SentenceStudio.Services.Speech;

namespace SentenceStudio.Pages.AppSettings;

/// <summary>
/// App-wide settings and administrative utilities page.
/// Accessible from the main flyout menu.
/// </summary>
class SettingsPageState
{
    public bool IsMigrating { get; set; }
    public bool StreakMigrationComplete { get; set; }
    public bool IsExporting { get; set; }
    public string StatusMessage { get; set; } = string.Empty;

    // Voice preferences (per-language)
    public string SelectedLanguage { get; set; } = "Korean";
    public string SelectedVoiceId { get; set; } = string.Empty;
    public List<VoiceInfo> AvailableVoices { get; set; } = new();
    public bool IsLoadingVoices { get; set; }
    
    // Quiz preferences
    public bool QuizDirection { get; set; }
    public bool QuizAutoplay { get; set; }
    public bool QuizShowMnemonic { get; set; }
    public double QuizAutoAdvanceDuration { get; set; }
}

class SettingsPage : Component<SettingsPageState>
{
    private IVocabularyProgressService _progressService;
    private DataExportService _exportService;
    private ILogger<SettingsPage> _logger;
    private SpeechVoicePreferences _speechVoicePreferences;
    private VocabularyQuizPreferences _quizPreferences;
    private IVoiceDiscoveryService _voiceDiscoveryService;

    LocalizationManager _localize => LocalizationManager.Instance;

    protected override void OnMounted()
    {
        var services = MauiControls.Application.Current!.Handler!.MauiContext!.Services;
        _progressService = services.GetRequiredService<IVocabularyProgressService>();
        _exportService = services.GetRequiredService<DataExportService>();
        _logger = services.GetRequiredService<ILogger<SettingsPage>>();
        _speechVoicePreferences = services.GetRequiredService<SpeechVoicePreferences>();
        _quizPreferences = services.GetRequiredService<VocabularyQuizPreferences>();
        _voiceDiscoveryService = services.GetRequiredService<IVoiceDiscoveryService>();

        // Check if streak migration has already been done
        var migrationComplete = Preferences.Get("StreakMigrationComplete", false);

        // Load current preferences
        SetState(s =>
        {
            s.StreakMigrationComplete = migrationComplete;
            s.SelectedLanguage = "Korean"; // Default to Korean
            s.SelectedVoiceId = _speechVoicePreferences.GetVoiceForLanguage("Korean");
            s.QuizDirection = _quizPreferences.DisplayDirection == "TargetToNative";
            s.QuizAutoplay = _quizPreferences.AutoPlayVocabAudio;
            s.QuizShowMnemonic = _quizPreferences.ShowMnemonicImage;
            s.QuizAutoAdvanceDuration = _quizPreferences.AutoAdvanceDuration / 1000.0;
        });

        // Load voices for default language
        _ = LoadVoicesForLanguageAsync("Korean");

        base.OnMounted();
    }

    private async Task LoadVoicesForLanguageAsync(string language)
    {
        SetState(s => s.IsLoadingVoices = true);
        
        try
        {
            var voices = await _voiceDiscoveryService.GetVoicesForLanguageAsync(language);
            var currentVoiceId = _speechVoicePreferences.GetVoiceForLanguage(language);
            
            SetState(s =>
            {
                s.AvailableVoices = voices;
                s.SelectedVoiceId = currentVoiceId;
                s.IsLoadingVoices = false;
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load voices for {Language}", language);
            SetState(s => s.IsLoadingVoices = false);
        }
    }

    public override VisualNode Render()
    {
        return ContentPage($"{_localize["Settings"]}",
            ScrollView(
                VStack(spacing: MyTheme.LayoutSpacing,
                    RenderVoiceAndQuizSection(),
                    RenderDataManagementSection(),
                    RenderMigrationSection(),
                    RenderAboutSection()
                )
                .Padding(MyTheme.CardPadding)
            )
        );
    }

    private VisualNode RenderVoiceAndQuizSection()
    {
        var supportedLanguages = _voiceDiscoveryService?.SupportedLanguages?.ToList() 
            ?? new List<string> { "English", "French", "German", "Korean", "Spanish" };
        
        var voiceDisplayNames = State.AvailableVoices.Select(v => v.DisplayName).ToList();
        var selectedVoiceIndex = State.AvailableVoices.FindIndex(v => v.VoiceId == State.SelectedVoiceId);
        if (selectedVoiceIndex < 0) selectedVoiceIndex = 0;

        return VStack(spacing: MyTheme.MicroSpacing,
            Label($"{_localize["VoiceAndQuizSettings"]}")
                .ThemeKey(MyTheme.Title2),

            // Language selection for voice
            VStack(spacing: MyTheme.MicroSpacing,
                Label($"{_localize["VoiceLanguage"]}")
                    .ThemeKey(MyTheme.Body1Strong),
                Label($"{_localize["VoiceLanguageDescription"]}")
                    .ThemeKey(MyTheme.Caption1),
                Picker()
                    .ItemsSource(supportedLanguages)
                    .SelectedIndex(supportedLanguages.IndexOf(State.SelectedLanguage))
                    .OnSelectedIndexChanged(async idx =>
                    {
                        if (idx >= 0 && idx < supportedLanguages.Count)
                        {
                            var language = supportedLanguages[idx];
                            SetState(s => s.SelectedLanguage = language);
                            await LoadVoicesForLanguageAsync(language);
                        }
                    })
            ),

            // Voice selection for selected language
            VStack(spacing: MyTheme.MicroSpacing,
                Label($"{_localize["PreferredVoice"]}")
                    .ThemeKey(MyTheme.Body1Strong),
                Label($"{_localize["PreferredVoiceDescription"]}")
                    .ThemeKey(MyTheme.Caption1),
                State.IsLoadingVoices
                    ? Label($"{_localize["Loading"]}...")
                        .ThemeKey(MyTheme.Caption1)
                    : Picker()
                        .ItemsSource(voiceDisplayNames.Count > 0 ? voiceDisplayNames : new List<string> { "No voices available" })
                        .SelectedIndex(selectedVoiceIndex >= 0 ? selectedVoiceIndex : 0)
                        .IsEnabled(voiceDisplayNames.Count > 0)
                        .OnSelectedIndexChanged(idx =>
                        {
                            if (idx >= 0 && idx < State.AvailableVoices.Count)
                            {
                                var voice = State.AvailableVoices[idx];
                                _speechVoicePreferences.SetVoiceForLanguage(State.SelectedLanguage, voice.VoiceId);
                                SetState(s => s.SelectedVoiceId = voice.VoiceId);
                            }
                        })
            ),

            //Quiz direction
            HStack(spacing: MyTheme.MicroSpacing,
                VStack(spacing: 2,
                    Label($"{_localize["QuizDirection"]}")
                        .ThemeKey(MyTheme.Body1Strong),
                    Label($"{_localize["QuizDirectionDescription"]}")
                        .ThemeKey(MyTheme.Caption1)
                )
                .HFill(),
                Switch()
                    .IsToggled(State.QuizDirection)
                    .OnToggled((s, args) =>
                    {
                        var toggled = args.Value;
                        _quizPreferences.DisplayDirection = toggled ? "TargetToNative" : "NativeToTarget";
                        SetState(s => s.QuizDirection = toggled);
                    })
                    .VCenter()
            ),

            // Autoplay
            HStack(spacing: MyTheme.MicroSpacing,
                VStack(spacing: 2,
                    Label($"{_localize["Autoplay"]}")
                        .ThemeKey(MyTheme.Body1Strong),
                    Label($"{_localize["AutoplayDescription"]}")
                        .ThemeKey(MyTheme.Caption1)
                )
                .HFill(),
                Switch()
                    .IsToggled(State.QuizAutoplay)
                    .OnToggled((s, args) =>
                    {
                        _quizPreferences.AutoPlayVocabAudio = args.Value;
                        SetState(s => s.QuizAutoplay = args.Value);
                    })
                    .VCenter()
            ),

            // Show mnemonic
            HStack(spacing: MyTheme.MicroSpacing,
                VStack(spacing: 2,
                    Label($"{_localize["ShowMnemonic"]}")
                        .ThemeKey(MyTheme.Body1Strong),
                    Label($"{_localize["ShowMnemonicDescription"]}")
                        .ThemeKey(MyTheme.Caption1)
                )
                .HFill(),
                Switch()
                    .IsToggled(State.QuizShowMnemonic)
                    .OnToggled((s, args) =>
                    {
                        _quizPreferences.ShowMnemonicImage = args.Value;
                        SetState(s => s.QuizShowMnemonic = args.Value);
                    })
                    .VCenter()
            ),

            // Auto-advance duration
            VStack(spacing: MyTheme.MicroSpacing,
                Label($"{_localize["AutoAdvanceDuration"]}: {State.QuizAutoAdvanceDuration:F1}s")
                    .ThemeKey(MyTheme.Body1Strong),
                Label($"{_localize["AutoAdvanceDurationDescription"]}")
                    .ThemeKey(MyTheme.Caption1),
                Slider()
                    .Minimum(0.5)
                    .Maximum(5.0)
                    .Value(State.QuizAutoAdvanceDuration)
                    .OnValueChanged((s, args) =>
                    {
                        var rounded = Math.Round(args.NewValue, 1);
                        _quizPreferences.AutoAdvanceDuration = (int)(rounded * 1000);
                        SetState(s => s.QuizAutoAdvanceDuration = rounded);
                    })
            ),

            // Reset button
            Button($"{_localize["ResetToDefaults"]}")
                .ThemeKey(MyTheme.Secondary)
                .OnClicked(() =>
                {
                    _quizPreferences.ResetToDefaults();
                    _speechVoicePreferences.ResetToDefault();
                    SetState(s =>
                    {
                        s.SelectedVoiceId = _speechVoicePreferences.GetVoiceForLanguage(s.SelectedLanguage);
                        s.QuizDirection = _quizPreferences.DisplayDirection == "TargetToNative";
                        s.QuizAutoplay = _quizPreferences.AutoPlayVocabAudio;
                        s.QuizShowMnemonic = _quizPreferences.ShowMnemonicImage;
                        s.QuizAutoAdvanceDuration = _quizPreferences.AutoAdvanceDuration / 1000.0;
                    });
                    _ = LoadVoicesForLanguageAsync(State.SelectedLanguage);
                })
                .Margin(0, MyTheme.MicroSpacing, 0, 0)
        )
        .Padding(MyTheme.CardPadding)
        .Background(MyTheme.CardBackground);
    }

    private VisualNode RenderDataManagementSection()
    {
        return VStack(spacing: MyTheme.MicroSpacing,
            Label($"{_localize["DataManagement"]}")
                .ThemeKey(MyTheme.Title2),

            Label($"{_localize["DataManagementDescription"]}")
                .ThemeKey(MyTheme.Body2),

            Button(State.IsExporting ? $"{_localize["Exporting"]}..." : $"📤 {_localize["ExportData"]}")
                .ThemeKey(MyTheme.Secondary)
                .IsEnabled(!State.IsExporting)
                .OnClicked(async () => await ExportDataInternalAsync())
                .Margin(0, MyTheme.MicroSpacing, 0, 0)
        )
        .Padding(MyTheme.CardPadding)
        .Background(MyTheme.CardBackground);
    }

    private VisualNode RenderMigrationSection()
    {
        return VStack(spacing: MyTheme.MicroSpacing,
            Label($"{_localize["DatabaseMigrations"]}")
                .ThemeKey(MyTheme.Title2),

            // Streak-based scoring migration
            Border(
                VStack(spacing: MyTheme.MicroSpacing,
                    HStack(spacing: MyTheme.MicroSpacing,
                        Label("🔄")
                            .FontSize(24)
                            .VCenter(),
                        VStack(spacing: 2,
                            Label($"{_localize["StreakMigrationTitle"]}")
                                .ThemeKey(MyTheme.Body1Strong),
                            Label($"{_localize["StreakMigrationDescription"]}")
                                .ThemeKey(MyTheme.Caption1)
                        )
                        .HFill()
                    ),

                    State.StreakMigrationComplete ?
                        HStack(spacing: MyTheme.MicroSpacing,
                            Label("✅")
                                .FontSize(16),
                            Label($"{_localize["MigrationComplete"]}")
                                .ThemeKey(MyTheme.Caption1)
                        )
                        :
                        Button(State.IsMigrating ? $"{_localize["Migrating"]}..." : $"{_localize["RunMigration"]}")
                            .ThemeKey(MyTheme.PrimaryButton)
                            .IsEnabled(!State.IsMigrating)
                            .OnClicked(async () => await RunStreakMigrationInternalAsync())
                )
                .Padding(MyTheme.CardPadding)
            )
            .Stroke(MyTheme.CardBorder)
            .StrokeShape(new RoundRectangle().CornerRadius(8))
            .Margin(0, MyTheme.MicroSpacing, 0, 0),

            // Status message
            !string.IsNullOrEmpty(State.StatusMessage) ?
                Label(State.StatusMessage)
                    .ThemeKey(MyTheme.Caption1)
                    .Margin(0, MyTheme.MicroSpacing, 0, 0)
                : null
        )
        .Padding(MyTheme.CardPadding)
        .Background(MyTheme.CardBackground);
    }

    private VisualNode RenderAboutSection()
    {
        return VStack(spacing: MyTheme.MicroSpacing,
            Label($"{_localize["About"]}")
                .ThemeKey(MyTheme.Title2),

            Label($"SentenceStudio v{AppInfo.VersionString} ({AppInfo.BuildString})")
                .ThemeKey(MyTheme.Body2),

            Label($"{_localize["TargetFramework"]}: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}")
                .ThemeKey(MyTheme.Caption1)
        )
        .Padding(MyTheme.CardPadding)
        .Background(MyTheme.CardBackground);
    }

    private async Task RunStreakMigrationInternalAsync()
    {
        if (State.IsMigrating || State.StreakMigrationComplete)
            return;

        SetState(s =>
        {
            s.IsMigrating = true;
            s.StatusMessage = $"{_localize["MigratingProgress"]}...";
        });

        try
        {
            _logger.LogInformation("🔄 Starting streak-based scoring migration from Settings page");

            var migratedCount = await _progressService.MigrateToStreakBasedScoringAsync();

            // Mark migration as complete
            Preferences.Set("StreakMigrationComplete", true);

            SetState(s =>
            {
                s.IsMigrating = false;
                s.StreakMigrationComplete = true;
                s.StatusMessage = string.Format(_localize["MigrationSuccessCount"].ToString(), migratedCount);
            });

            _logger.LogInformation("✅ Streak migration complete. Migrated {Count} vocabulary progress records", migratedCount);

            await AppShell.DisplayToastAsync($"{_localize["MigrationComplete"]}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Streak migration failed");

            SetState(s =>
            {
                s.IsMigrating = false;
                s.StatusMessage = $"{_localize["MigrationFailed"]}: {ex.Message}";
            });
        }
    }

    private async Task ExportDataInternalAsync()
    {
        if (State.IsExporting)
            return;

        SetState(s => s.IsExporting = true);

        try
        {
            await _exportService.ExportAllDataAsZipAsync();
            await AppShell.DisplayToastAsync($"{_localize["ExportComplete"]}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Data export failed");
            await AppShell.DisplayToastAsync($"{_localize["ExportFailed"]}");
        }
        finally
        {
            SetState(s => s.IsExporting = false);
        }
    }
}
