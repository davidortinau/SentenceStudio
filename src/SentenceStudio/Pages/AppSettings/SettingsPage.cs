using MauiReactor;
using MauiReactor.Shapes;
using Microsoft.Extensions.Logging;
using SentenceStudio.Services;

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
}

class SettingsPage : Component<SettingsPageState>
{
    private IVocabularyProgressService _progressService;
    private DataExportService _exportService;
    private ILogger<SettingsPage> _logger;

    LocalizationManager _localize => LocalizationManager.Instance;

    protected override void OnMounted()
    {
        var services = MauiControls.Application.Current!.Handler!.MauiContext!.Services;
        _progressService = services.GetRequiredService<IVocabularyProgressService>();
        _exportService = services.GetRequiredService<DataExportService>();
        _logger = services.GetRequiredService<ILogger<SettingsPage>>();

        // Check if streak migration has already been done
        var migrationComplete = Preferences.Get("StreakMigrationComplete", false);
        SetState(s => s.StreakMigrationComplete = migrationComplete);

        base.OnMounted();
    }

    public override VisualNode Render()
    {
        return ContentPage($"{_localize["Settings"]}",
            ScrollView(
                VStack(spacing: MyTheme.LayoutSpacing,
                    RenderDataManagementSection(),
                    RenderMigrationSection(),
                    RenderAboutSection()
                )
                .Padding(MyTheme.CardPadding)
            )
        );
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
                .OnClicked(ExportDataAsync)
                .Margin(0, MyTheme.MicroSpacing, 0, 0)
        )
        .Padding(MyTheme.CardPadding)
        .BackgroundColor(MyTheme.CardBackground);
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
                            .OnClicked(RunStreakMigrationAsync)
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
        .BackgroundColor(MyTheme.CardBackground);
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
        .BackgroundColor(MyTheme.CardBackground);
    }

    private async void RunStreakMigrationAsync()
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

    private async void ExportDataAsync()
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
