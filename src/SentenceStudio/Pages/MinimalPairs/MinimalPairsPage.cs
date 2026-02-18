using SentenceStudio.Pages.Controls;
using MauiReactor.Shapes;
using SentenceStudio.Repositories;
using UXDivers.Popups.Maui.Controls;
using UXDivers.Popups.Services;
using SentenceStudio.Services;

namespace SentenceStudio.Pages.MinimalPairs;

/// <summary>
/// Minimal Pairs Landing Page
/// 
/// Responsibilities:
/// - List existing minimal pairs for the current user
/// - Create new minimal pairs from vocabulary words
/// - Choose practice mode (Focus/Mixed)
/// - Start a practice session
/// 
/// Navigation Entry Points:
/// - Dashboard → "Minimal Pairs" tile
/// - Future: Daily Plan → "Minimal Pairs Practice" item
/// </summary>
enum PracticeMode { Focus, Mixed }

class MinimalPairsPageState
{
    public List<MinimalPair> UserPairs { get; set; } = new();
    public bool IsLoading { get; set; }
    public PracticeMode SelectedMode { get; set; } = PracticeMode.Focus;
    public int? SelectedPairId { get; set; } // For Focus mode
}

partial class MinimalPairsPage : Component<MinimalPairsPageState>
{
    [Inject] ILogger<MinimalPairsPage> _logger;
    [Inject] MinimalPairRepository _pairRepo;
    [Inject] NativeThemeService _themeService;

    LocalizationManager _localize => LocalizationManager.Instance;

    protected override void OnMounted()
    {
        _themeService.ThemeChanged += OnThemeChanged;
        _ = LoadUserPairsAsync();
    }


    protected override void OnWillUnmount()
    {
        _themeService.ThemeChanged -= OnThemeChanged;
        base.OnWillUnmount();
    }

    private void OnThemeChanged(object? sender, ThemeChangedEventArgs e) => Invalidate();

    private async Task LoadUserPairsAsync()
    {
        SetState(s => s.IsLoading = true);

        try
        {
            // For now, use userId = 1 (single-user app)
            var pairs = await _pairRepo.GetUserPairsAsync(1);
            SetState(s =>
            {
                s.UserPairs = pairs;
                s.IsLoading = false;
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load minimal pairs");
            SetState(s => s.IsLoading = false);
        }
    }

    public async Task StartSessionAsync()
    {
        if (State.SelectedMode == PracticeMode.Focus && State.SelectedPairId == null)
        {
            _logger.LogWarning("Focus mode selected but no pair chosen");
            await IPopupService.Current.PushAsync(new SimpleActionPopup
            {
                Title = $"{_localize["Error"]}",
                Text = "Please select a pair to practice in Focus mode",
                ActionButtonText = $"{_localize["OK"]}",
                ShowSecondaryActionButton = false
            });
            return;
        }

        var pairIds = State.SelectedMode == PracticeMode.Focus
            ? new[] { State.SelectedPairId!.Value }
            : State.UserPairs.Select(p => p.Id).ToArray();

        if (pairIds.Length == 0)
        {
            _logger.LogWarning("No pairs available to start session");
            await IPopupService.Current.PushAsync(new SimpleActionPopup
            {
                Title = $"{_localize["Error"]}",
                Text = $"{_localize["MinimalPairsEmptyState"]}",
                ActionButtonText = $"{_localize["OK"]}",
                ShowSecondaryActionButton = false
            });
            return;
        }

        // Navigate to session page
        await MauiControls.Shell.Current.GoToAsync<MinimalPairSessionPageProps>(
            nameof(MinimalPairSessionPage),
            props =>
            {
                props.PairIds = pairIds;
                props.Mode = State.SelectedMode.ToString();
                props.PlannedTrialCount = State.SelectedMode == PracticeMode.Focus ? 10 : 20;
            }
        );
    }

    public override VisualNode Render()
    {
        var theme = BootstrapTheme.Current;

        return ContentPage($"{_localize["MinimalPairsTitle"]}",
            ToolbarItem($"{_localize["MinimalPairsCreatePair"]}")
                .IconImageSource(BootstrapIcons.Create(BootstrapIcons.PlusLg, theme.GetOnBackground(), 20))
                .OnClicked(() => OnCreatePair()),

            Grid(rows: "Auto,*", columns: "*",
                // Mode selector
                RenderModeSelector().GridRow(0),

                // Pair list or empty state
                State.IsLoading
                    ? Label($"{_localize["Loading"]}")
                        .Center()
                        .GridRow(1)
                    : State.UserPairs.Count == 0
                        ? RenderEmptyState()
                        : RenderPairList()
            )
        )
        .Set(MauiControls.Shell.TabBarIsVisibleProperty, true)
        .BackgroundColor(BootstrapTheme.Current.GetBackground())
        .OnAppearing(() => _ = LoadUserPairsAsync());
    }

    private VisualNode RenderModeSelector()
    {
        var theme = BootstrapTheme.Current;

        return HStack(spacing: 12,
            Label($"{_localize["Mode"]}:")
                .FontSize(14)
                .FontAttributes(Microsoft.Maui.Controls.FontAttributes.Bold)
                .VCenter(),

            // Bootstrap btn-group: active = primary, inactive = outline
            HStack(spacing: 0,
                Button($"{_localize["MinimalPairsModeFocus"]}")
                    .Background(new SolidColorBrush(State.SelectedMode == PracticeMode.Focus ? theme.Primary : Colors.Transparent))
                    .TextColor(State.SelectedMode == PracticeMode.Focus ? Colors.White : theme.GetOnBackground())
                    .BorderColor(theme.GetOutline())
                    .BorderWidth(1)
                    .CornerRadius(6)
                    .OnClicked(() => SetState(s =>
                    {
                        s.SelectedMode = PracticeMode.Focus;
                    })),

                Button($"{_localize["MinimalPairsModeMixed"]}")
                    .Background(new SolidColorBrush(State.SelectedMode == PracticeMode.Mixed ? theme.Primary : Colors.Transparent))
                    .TextColor(State.SelectedMode == PracticeMode.Mixed ? Colors.White : theme.GetOnBackground())
                    .BorderColor(theme.GetOutline())
                    .BorderWidth(1)
                    .CornerRadius(6)
                    .OnClicked(() => SetState(s =>
                    {
                        s.SelectedMode = PracticeMode.Mixed;
                        // Clear selection when switching to Mixed mode
                        s.SelectedPairId = null;
                    }))
            ),

            // Start Session button
            State.UserPairs.Count > 0
                ? Button(State.SelectedMode == PracticeMode.Focus
                        ? $"{_localize["MinimalPairsStartSession"]}"
                        : $"{_localize["MinimalPairsStartSession"]} ({State.UserPairs.Count} pairs)")
                    .Background(new SolidColorBrush(theme.Primary))
                    .TextColor(Colors.White)
                    .BorderColor(theme.Primary)
                    .BorderWidth(1)
                    .CornerRadius(6)
                    .IsEnabled(State.SelectedMode == PracticeMode.Mixed || State.SelectedPairId != null)
                    .OnClicked(async () => await StartSessionAsync())
                : null
        )
        .Padding(16);
    }

    private VisualNode RenderEmptyState()
    {
        var theme = BootstrapTheme.Current;
        return VStack(spacing: 16,
            Label($"{_localize["MinimalPairsEmptyState"]}")
                .FontSize(14)
                .HCenter(),
            Button($"{_localize["CreateYourFirstPair"]}")
                .OnClicked(() => OnCreatePair())
                .Primary()
                .HCenter()
        )
        .Center()
        .GridRow(1);
    }

    private VisualNode RenderPairList()
    {
        return CollectionView()
            .ItemsSource(State.UserPairs, pair => RenderPairItem(pair))
            .GridRow(1);
    }

    private VisualNode RenderPairItem(MinimalPair pair)
    {
        var theme = BootstrapTheme.Current;

        // In Mixed mode, all pairs are considered selected
        // In Focus mode, only the explicitly selected pair is highlighted
        var isSelected = State.SelectedMode == PracticeMode.Mixed ||
                        (State.SelectedMode == PracticeMode.Focus && State.SelectedPairId == pair.Id);

        return Border(
            HStack(spacing: 12,
                // Pair content
                VStack(spacing: 8,
                    HStack(spacing: 12,
                        Label(pair.VocabularyWordA?.TargetLanguageTerm ?? "")
                            .H4(),

                        Label("vs")
                            .Small(),

                        Label(pair.VocabularyWordB?.TargetLanguageTerm ?? "")
                            .H4()
                    ),

                    string.IsNullOrEmpty(pair.ContrastLabel)
                        ? null
                        : Label(pair.ContrastLabel)
                            .Small()
                )
                .HFill(),

                // Delete button - far right
                ImageButton()
                    .Source(BootstrapIcons.Create(BootstrapIcons.Trash, theme.Danger, 20))
                    .Background(Colors.Transparent)
                    .OnClicked(async () => await OnDeletePairAsync(pair))
                    .WidthRequest(40)
                    .HeightRequest(40)
            )
            .Padding(12)
        )
        .BackgroundColor(isSelected ? theme.Primary : theme.GetSurface())
        .Stroke(isSelected ? theme.Primary : theme.GetOutline())
        .StrokeThickness(1)
        .StrokeShape(new RoundRectangle().CornerRadius(12))
        .Margin(8, 4)
        .OnTapped(() =>
        {
            if (State.SelectedMode == PracticeMode.Focus)
            {
                SetState(s => s.SelectedPairId = pair.Id);
            }
        });
    }

    private async Task OnDeletePairAsync(MinimalPair pair)
    {
        var tcs = new TaskCompletionSource<bool>();
        var confirmPopup = new SimpleActionPopup
        {
            Title = $"{_localize["MinimalPairsDeleteConfirm"]}",
            Text = $"{pair.VocabularyWordA?.TargetLanguageTerm} vs {pair.VocabularyWordB?.TargetLanguageTerm}",
            ActionButtonText = $"{_localize["Delete"]}",
            SecondaryActionButtonText = $"{_localize["Cancel"]}",
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
        var confirmed = await tcs.Task;

        if (!confirmed) return;

        try
        {
            var success = await _pairRepo.DeletePairAsync(pair.Id);
            if (success)
            {
                _ = LoadUserPairsAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete pair");
        }
    }

    private Task OnCreatePair()
    {
        return MauiControls.Shell.Current.GoToAsync(nameof(CreateMinimalPairPage));
    }
}
