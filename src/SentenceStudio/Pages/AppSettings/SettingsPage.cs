using MauiReactor;
using MauiReactor.Shapes;
using Microsoft.Extensions.Logging;
using SentenceStudio.Services;
using SentenceStudio.Services.Speech;
using SentenceStudio.Pages.Controls;
using UXDivers.Popups.Maui.Controls;
using UXDivers.Popups.Services;
using Button = MauiReactor.Button;

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
    public string QuizDirection { get; set; } = "TargetToNative";
    public bool QuizAutoplay { get; set; }
    public bool QuizShowMnemonic { get; set; }
    public double QuizAutoAdvanceDuration { get; set; }

    // Appearance preferences
    public string SelectedTheme { get; set; } = "seoul-pop";
    public bool IsDarkMode { get; set; }
    public double FontScale { get; set; } = 1.0;
}

partial class SettingsPage : Component<SettingsPageState>
{
    [Inject] IVocabularyProgressService _progressService;
    [Inject] DataExportService _exportService;
    [Inject] ILogger<SettingsPage> _logger;
    [Inject] SpeechVoicePreferences _speechVoicePreferences;
    [Inject] VocabularyQuizPreferences _quizPreferences;
    [Inject] IVoiceDiscoveryService _voiceDiscoveryService;
    [Inject] NativeThemeService _themeService;

    LocalizationManager _localize => LocalizationManager.Instance;

    static readonly List<string> _supportedLanguages = new() { "Korean", "English", "French", "German", "Spanish" };

    protected override void OnMounted()
    {
        // Check if streak migration has already been done
        var migrationComplete = Preferences.Get("StreakMigrationComplete", false);

        // Load current preferences
        SetState(s =>
        {
            s.StreakMigrationComplete = migrationComplete;
            s.SelectedLanguage = "Korean"; // Default to Korean
            s.SelectedVoiceId = _speechVoicePreferences.GetVoiceForLanguage("Korean");
            s.QuizDirection = _quizPreferences.DisplayDirection;
            s.QuizAutoplay = _quizPreferences.AutoPlayVocabAudio;
            s.QuizShowMnemonic = _quizPreferences.ShowMnemonicImage;
            s.QuizAutoAdvanceDuration = _quizPreferences.AutoAdvanceDuration / 1000.0;
            s.SelectedTheme = _themeService.CurrentTheme;
            s.IsDarkMode = _themeService.IsDarkMode;
            s.FontScale = _themeService.FontScale;
        });

        // Load voices for default language
        _ = LoadVoicesForLanguageAsync("Korean");

        _themeService.ThemeChanged += OnThemeChanged;
        base.OnMounted();
    }


    protected override void OnWillUnmount()
    {
        _themeService.ThemeChanged -= OnThemeChanged;
        base.OnWillUnmount();
    }

    private void OnThemeChanged(object? sender, ThemeChangedEventArgs e) => Invalidate();

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
                VStack(spacing: 24,
                    RenderAppearanceSection(),
                    RenderVoiceAndQuizSection(),
                    RenderDataManagementSection(),
                    RenderMigrationSection(),
                    RenderAboutSection()
                )
                .Padding(16)
            )
        )
        .BackgroundColor(BootstrapTheme.Current.GetBackground());
    }

    private VisualNode RenderAppearanceSection()
    {
        var theme = BootstrapTheme.Current;
        var availableThemes = _themeService.AvailableThemes;

        return Border(
            VStack(spacing: 16,
                // Section heading
                Label($"{_localize["Appearance"]}")
                    .H4()
                    .FontAttributes(Microsoft.Maui.Controls.FontAttributes.Bold),

                // Theme swatches
                Label($"{_localize["ChooseTheme"]}")
                    .Class("form-label")
                    .FontAttributes(Microsoft.Maui.Controls.FontAttributes.Bold),

                FlexLayout(
                    availableThemes.Select(themeId => RenderThemeSwatch(themeId, theme)).ToArray()
                )
                .Wrap(Microsoft.Maui.Layouts.FlexWrap.Wrap)
                .JustifyContent(Microsoft.Maui.Layouts.FlexJustify.Start)
                .AlignItems(Microsoft.Maui.Layouts.FlexAlignItems.Start),

                // Light/Dark mode toggle
                Label($"{_localize["DisplayMode"]}")
                    .Class("form-label")
                    .FontAttributes(Microsoft.Maui.Controls.FontAttributes.Bold),

                new SegmentedButtonGroup()
                    .Left(RenderModeButton($"{_localize["Light"]}", BootstrapIcons.SunFill, !State.IsDarkMode, theme, () =>
                    {
                        _themeService.SetMode("light");
                        SetState(s => s.IsDarkMode = false);
                    }))
                    .Right(RenderModeButton($"{_localize["Dark"]}", BootstrapIcons.MoonFill, State.IsDarkMode, theme, () =>
                    {
                        _themeService.SetMode("dark");
                        SetState(s => s.IsDarkMode = true);
                    }))
                    .CornerRadius(6),

                // Text Size slider
                VStack(spacing: 4,
                    Label($"{_localize["TextSize"]}: {(int)(State.FontScale * 100)}%")
                        .Class("form-label")
                        .FontAttributes(Microsoft.Maui.Controls.FontAttributes.Bold),
                    Slider()
                        .Minimum(0.85)
                        .Maximum(1.5)
                        .Value(State.FontScale)
                        .Class("form-range")
                        .OnValueChanged((s, args) =>
                        {
                            var rounded = Math.Round(args.NewValue / 0.05) * 0.05;
                            _themeService.SetFontScale(rounded);
                            SetState(s => s.FontScale = rounded);
                        }),
                    Grid("Auto", "*,*",
                        Label("85%").Small().Muted().HStart().GridColumn(0),
                        Label("150%").Small().Muted().HEnd().GridColumn(1)
                    )
                )
            )
            .Padding(16)
        )
        .Class("card")
        .Padding(16);
    }

    private VisualNode RenderThemeSwatch(string themeId, BootstrapTheme theme)
    {
        var isSelected = State.SelectedTheme == themeId;
        var (primary, accent) = NativeThemeService.GetThemeSwatchColors(themeId, State.IsDarkMode);
        var displayName = NativeThemeService.GetThemeDisplayName(themeId);

        return VStack(spacing: 4,
            Border(
                HStack(spacing: 0,
                    BoxView()
                        .Color(primary)
                        .WidthRequest(28)
                        .HeightRequest(40),
                    BoxView()
                        .Color(accent)
                        .WidthRequest(28)
                        .HeightRequest(40)
                )
            )
            .StrokeShape(new RoundRectangle().CornerRadius(8))
            .StrokeThickness(isSelected ? 2.5 : 1)
            .Stroke(isSelected ? theme.Primary : theme.GetOutline())
            .Padding(0),

            Label(displayName)
                .Small()
                .HCenter()
                .TextColor(isSelected ? theme.Primary : theme.GetOnBackground())
        )
        .Margin(0, 0, 12, 8)
        .OnTapped(() =>
        {
            _themeService.SetTheme(themeId);
            SetState(s => s.SelectedTheme = themeId);
        });
    }

    private Button RenderModeButton(string text, string iconGlyph, bool isActive, BootstrapTheme theme, Action onClicked)
    {
        var iconColor = isActive ? theme.OnPrimary : theme.GetOnBackground();
        var btn = Button(text)
            .ImageSource(BootstrapIcons.Create(iconGlyph, iconColor, 16))
            .HeightRequest(40)
            .OnClicked(onClicked);

        btn = isActive
            ? btn.Primary()
            : btn.Background(new SolidColorBrush(Colors.Transparent))
                 .TextColor(theme.GetOnBackground());
        return btn.CornerRadius(0).BorderWidth(0);
    }

    private VisualNode RenderDirectionButton(string text, string directionValue, BootstrapTheme theme)
    {
        var isActive = State.QuizDirection == directionValue;
        var btn = Button(text)
            .HeightRequest(40)
            .OnClicked(() =>
            {
                _quizPreferences.DisplayDirection = directionValue;
                SetState(s => s.QuizDirection = directionValue);
            });

        return isActive ? btn.Primary() : btn.Secondary().Outlined();
    }

    private VisualNode RenderVoiceAndQuizSection()
    {
        var theme = BootstrapTheme.Current;

        return Border(
            VStack(spacing: 16,
                Label($"{_localize["VoiceAndQuizSettings"]}")
                    .H4()
                    .FontAttributes(Microsoft.Maui.Controls.FontAttributes.Bold)
                    .TextColor(theme.GetOnBackground()),

                // Language selection for voice
                VStack(spacing: 4,
                    Label($"{_localize["VoiceLanguage"]}")
                        .Class("form-label")
                        .FontAttributes(Microsoft.Maui.Controls.FontAttributes.Bold),
                    Label($"{_localize["VoiceLanguageDescription"]}")
                        .Small()
                        .Muted(),
                    Picker()
                        .Title($"{_localize["VoiceLanguage"]}")
                        .ItemsSource(_supportedLanguages)
                        .SelectedIndex(_supportedLanguages.IndexOf(State.SelectedLanguage))
                        .OnSelectedIndexChanged(OnLanguagePickerChanged)
                        .Class("form-select")
                        .HeightRequest(44)
                        .HFill()
                ),

                // Voice selection for selected language
                VStack(spacing: 4,
                    Label($"{_localize["PreferredVoice"]}")
                        .Class("form-label")
                        .FontAttributes(Microsoft.Maui.Controls.FontAttributes.Bold),
                    Label($"{_localize["PreferredVoiceDescription"]}")
                        .Small()
                        .Muted(),
                    State.IsLoadingVoices
                        ? (VisualNode)HStack(spacing: 8,
                            ActivityIndicator()
                                .IsRunning(true)
                                .HeightRequest(16)
                                .WidthRequest(16),
                            Label($"{_localize["Loading"]}...")
                                .Small()
                                .Muted()
                                .VCenter()
                        )
                        : Picker()
                            .Title($"{_localize["PreferredVoice"]}")
                            .ItemsSource(State.AvailableVoices.Select(v => v.Name).ToList())
                            .SelectedIndex(GetSelectedVoiceIndex())
                            .OnSelectedIndexChanged(OnVoicePickerChanged)
                            .Class("form-select")
                            .HeightRequest(44)
                            .HFill()
                ),

                // Quiz direction — 3-way segmented control
                VStack(spacing: 4,
                    Label($"{_localize["QuizDirection"]}")
                        .Class("form-label")
                        .FontAttributes(Microsoft.Maui.Controls.FontAttributes.Bold),
                    Label($"{_localize["QuizDirectionDescription"]}")
                        .Small()
                        .Muted(),
                    HStack(spacing: 0,
                        RenderDirectionButton($"{_localize["QuizDirectionForward"]}", "TargetToNative", theme),
                        RenderDirectionButton($"{_localize["QuizDirectionReverse"]}", "NativeToTarget", theme),
                        RenderDirectionButton($"{_localize["QuizDirectionMixed"]}", "Mixed", theme)
                    )
                ),

                // Autoplay
                HStack(spacing: 8,
                    VStack(spacing: 2,
                        Label($"{_localize["Autoplay"]}")
                            .Class("form-label")
                            .FontAttributes(Microsoft.Maui.Controls.FontAttributes.Bold),
                        Label($"{_localize["AutoplayDescription"]}")
                            .Small()
                            .Muted()
                    )
                    .HFill(),
                    Switch()
                        .IsToggled(State.QuizAutoplay)
                        .Class("form-switch")
                        .OnToggled((s, args) =>
                        {
                            _quizPreferences.AutoPlayVocabAudio = args.Value;
                            SetState(s => s.QuizAutoplay = args.Value);
                        })
                        .VCenter()
                ),

                // Show mnemonic
                HStack(spacing: 8,
                    VStack(spacing: 2,
                        Label($"{_localize["ShowMnemonic"]}")
                            .Class("form-label")
                            .FontAttributes(Microsoft.Maui.Controls.FontAttributes.Bold),
                        Label($"{_localize["ShowMnemonicDescription"]}")
                            .Small()
                            .Muted()
                    )
                    .HFill(),
                    Switch()
                        .IsToggled(State.QuizShowMnemonic)
                        .Class("form-switch")
                        .OnToggled((s, args) =>
                        {
                            _quizPreferences.ShowMnemonicImage = args.Value;
                            SetState(s => s.QuizShowMnemonic = args.Value);
                        })
                        .VCenter()
                ),

                // Auto-advance duration
                VStack(spacing: 4,
                    Label($"{_localize["AutoAdvanceDuration"]}: {State.QuizAutoAdvanceDuration:F1}s")
                        .Class("form-label")
                        .FontAttributes(Microsoft.Maui.Controls.FontAttributes.Bold),
                    Label($"{_localize["AutoAdvanceDurationDescription"]}")
                        .Small()
                        .Muted(),
                    Slider()
                        .Minimum(1.0)
                        .Maximum(10.0)
                        .Value(State.QuizAutoAdvanceDuration)
                        .Class("form-range")
                        .OnValueChanged((s, args) =>
                        {
                            var rounded = Math.Round(args.NewValue * 2) / 2.0; // step 0.5
                            _quizPreferences.AutoAdvanceDuration = (int)(rounded * 1000);
                            SetState(s => s.QuizAutoAdvanceDuration = rounded);
                        })
                ),

                // Save Preferences button
                Button($"{_localize["SavePreferences"]}")
                    .Primary()
                    .HeightRequest(44)
                    .OnClicked(async () => await AppShell.DisplayToastAsync($"{_localize["PreferencesSaved"]}")),

                // Reset button
                Button($"{_localize["ResetToDefaults"]}")
                    .HeightRequest(44)
                    .Secondary()
                    .Outlined()
                    .OnClicked(() =>
                    {
                        _quizPreferences.ResetToDefaults();
                        _speechVoicePreferences.ResetToDefault();
                        SetState(s =>
                        {
                            s.SelectedVoiceId = _speechVoicePreferences.GetVoiceForLanguage(s.SelectedLanguage);
                            s.QuizDirection = _quizPreferences.DisplayDirection;
                            s.QuizAutoplay = _quizPreferences.AutoPlayVocabAudio;
                            s.QuizShowMnemonic = _quizPreferences.ShowMnemonicImage;
                            s.QuizAutoAdvanceDuration = _quizPreferences.AutoAdvanceDuration / 1000.0;
                        });
                        _ = LoadVoicesForLanguageAsync(State.SelectedLanguage);
                    })
                    .Margin(0, 4, 0, 0)
            )
            .Padding(16)
        )
        .Class("card")
        .Padding(16);
    }

    private string GetSelectedVoiceDisplayName()
    {
        var selectedVoice = State.AvailableVoices.FirstOrDefault(v => v.VoiceId == State.SelectedVoiceId);
        return selectedVoice?.DisplayName ?? $"{_localize["SelectVoice"]}";
    }

    private int GetSelectedVoiceIndex()
    {
        if (string.IsNullOrEmpty(State.SelectedVoiceId) || State.AvailableVoices.Count == 0)
            return -1;
        return State.AvailableVoices.FindIndex(v => v.VoiceId == State.SelectedVoiceId);
    }

    private void OnLanguagePickerChanged(int index)
    {
        if (index < 0 || index >= _supportedLanguages.Count) return;
        var lang = _supportedLanguages[index];
        SetState(st => st.SelectedLanguage = lang);
        _ = LoadVoicesForLanguageAsync(lang);
    }

    private void OnVoicePickerChanged(int index)
    {
        if (index < 0 || index >= State.AvailableVoices.Count) return;
        var voice = State.AvailableVoices[index];
        _speechVoicePreferences.SetVoiceForLanguage(State.SelectedLanguage, voice.VoiceId);
        SetState(s => s.SelectedVoiceId = voice.VoiceId);
    }

    private VisualNode RenderDataManagementSection()
    {
        var theme = BootstrapTheme.Current;

        return Border(
            VStack(spacing: 16,
                Label($"{_localize["DataManagement"]}")
                    .H4()
                    .FontAttributes(Microsoft.Maui.Controls.FontAttributes.Bold)
                    .TextColor(theme.GetOnBackground()),

                Label($"{_localize["DataManagementDescription"]}")
                    .FontSize(14)
                    .TextColor(theme.GetOnBackground()),

                Button(State.IsExporting ? $"{_localize["Exporting"]}..." : $"{_localize["ExportData"]}")
                    .HeightRequest(44)
                    .Secondary()
                    .Outlined()
                    .IsEnabled(!State.IsExporting)
                    .OnClicked(async () => await ExportDataInternalAsync())
                    .Margin(0, 4, 0, 0)
            )
            .Padding(16)
        )
        .Class("card")
        .Padding(16);
    }

    private VisualNode RenderMigrationSection()
    {
        var theme = BootstrapTheme.Current;

        return Border(
            VStack(spacing: 16,
                Label($"{_localize["DatabaseMigrations"]}")
                    .H4()
                    .FontAttributes(Microsoft.Maui.Controls.FontAttributes.Bold)
                    .TextColor(theme.GetOnBackground()),

                // Streak-based scoring migration
                Border(
                    VStack(spacing: 8,
                        HStack(spacing: 8,
                            Label("Sync")
                                .FontSize(24)
                                .VCenter(),
                            VStack(spacing: 2,
                                Label($"{_localize["StreakMigrationTitle"]}")
                                    .FontAttributes(Microsoft.Maui.Controls.FontAttributes.Bold)
                                    .TextColor(theme.GetOnBackground()),
                                Label($"{_localize["StreakMigrationDescription"]}")
                                    .Small()
                                    .Muted()
                            )
                            .HFill()
                        ),

                        State.StreakMigrationComplete ?
                            HStack(spacing: 8,
                                Label("Done")
                                    .FontSize(16),
                                Label($"{_localize["MigrationComplete"]}")
                                    .Small()
                                    .Muted()
                            )
                            :
                            Button(State.IsMigrating ? $"{_localize["Migrating"]}..." : $"{_localize["RunMigration"]}")
                                .Primary()
                                .HeightRequest(44)
                                .IsEnabled(!State.IsMigrating)
                                .OnClicked(async () => await RunStreakMigrationInternalAsync())
                    )
                    .Padding(16)
                )
                .BackgroundColor(theme.GetSurface())
                .Stroke(theme.GetOutline())
                .StrokeThickness(1)
                .StrokeShape(new RoundRectangle().CornerRadius(8))
                .Margin(0, 4, 0, 0),

                // Status message
                !string.IsNullOrEmpty(State.StatusMessage) ?
                    Label(State.StatusMessage)
                        .Small()
                        .Muted()
                        .Margin(0, 4, 0, 0)
                    : null
            )
            .Padding(16)
        )
        .Class("card")
        .Padding(16);
    }

    private VisualNode RenderAboutSection()
    {
        var theme = BootstrapTheme.Current;

        return Border(
            VStack(spacing: 8,
                Label($"{_localize["About"]}")
                    .H4()
                    .FontAttributes(Microsoft.Maui.Controls.FontAttributes.Bold)
                    .TextColor(theme.GetOnBackground()),

                Label($"SentenceStudio v{AppInfo.VersionString} ({AppInfo.BuildString})")
                    .FontSize(14)
                    .TextColor(theme.GetOnBackground()),

                Label($"{_localize["TargetFramework"]}: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}")
                    .Small()
                    .Muted()
            )
            .Padding(16)
        )
        .Class("card")
        .Padding(16);
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
