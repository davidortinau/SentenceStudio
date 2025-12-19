using SentenceStudio.Pages.Controls;
using SentenceStudio.Repositories;

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

    LocalizationManager _localize => LocalizationManager.Instance;

    protected override void OnMounted()
    {
        _ = LoadUserPairsAsync();
    }

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
            await Application.Current!.MainPage!.DisplayAlert(
                $"{_localize["Error"]}",
                "Please select a pair to practice in Focus mode",
                $"{_localize["OK"]}"
            );
            return;
        }

        var pairIds = State.SelectedMode == PracticeMode.Focus
            ? new[] { State.SelectedPairId!.Value }
            : State.UserPairs.Select(p => p.Id).ToArray();

        if (pairIds.Length == 0)
        {
            _logger.LogWarning("No pairs available to start session");
            await Application.Current!.MainPage!.DisplayAlert(
                $"{_localize["Error"]}",
                $"{_localize["MinimalPairsEmptyState"]}",
                $"{_localize["OK"]}"
            );
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
        return ContentPage($"{_localize["MinimalPairsTitle"]}",
            ToolbarItem($"{_localize["MinimalPairsCreatePair"]}")
                .IconImageSource(MyTheme.IconAdd)
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
        .OnAppearing(() => _ = LoadUserPairsAsync());
    }

    private VisualNode RenderModeSelector()
    {
        return HStack(spacing: MyTheme.Size120,
            Label($"{_localize["Mode"]}:")
                .ThemeKey(MyTheme.Body1Strong)
                .VCenter(),

            new SfSegmentedControl(
                new SfSegmentItem()
                    .Text($"{_localize["MinimalPairsModeFocus"]}")
                    .SelectedSegmentTextColor(MyTheme.LightOnDarkBackground),
                new SfSegmentItem()
                    .Text($"{_localize["MinimalPairsModeMixed"]}")
                    .SelectedSegmentTextColor(MyTheme.LightOnDarkBackground)
            )
            .TextStyle(new Syncfusion.Maui.Toolkit.SegmentedControl.SegmentTextStyle()
            {
                TextColor = MyTheme.Gray600
            })
            .SelectionIndicatorSettings(new Syncfusion.Maui.Toolkit.SegmentedControl.SelectionIndicatorSettings()
            {
                Background = MyTheme.PrimaryButtonBackground,
                TextColor = Colors.White
            })
            .SelectedIndex((int)State.SelectedMode)
            .Background(MyTheme.SecondaryButtonBackground)
            .CornerRadius((float)MyTheme.Size80)
            .OnSelectionChanged((s, e) =>
            {
                if (e.NewIndex >= 0)
                {
                    SetState(state =>
                    {
                        state.SelectedMode = (PracticeMode)e.NewIndex;
                        // Clear selection when switching to Mixed mode
                        if (state.SelectedMode == PracticeMode.Mixed)
                        {
                            state.SelectedPairId = null;
                        }
                    });
                }
            })
            .HFill()
        )
        .Padding(MyTheme.Size160);
    }

    private VisualNode RenderEmptyState()
    {
        return Label($"{_localize["MinimalPairsEmptyState"]}")
            .ThemeKey(MyTheme.Body1)
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
        // In Mixed mode, all pairs are considered selected
        // In Focus mode, only the explicitly selected pair is highlighted
        var isSelected = State.SelectedMode == PracticeMode.Mixed ||
                        (State.SelectedMode == PracticeMode.Focus && State.SelectedPairId == pair.Id);

        return Border(
            HStack(spacing: MyTheme.Size120,
                // Pair content
                VStack(spacing: MyTheme.Size80,
                    HStack(spacing: MyTheme.Size120,
                        Label(pair.VocabularyWordA?.TargetLanguageTerm ?? "")
                            .ThemeKey(MyTheme.Title2),

                        Label("vs")
                            .ThemeKey(MyTheme.Caption1),

                        Label(pair.VocabularyWordB?.TargetLanguageTerm ?? "")
                            .ThemeKey(MyTheme.Title2)
                    ),

                    string.IsNullOrEmpty(pair.ContrastLabel)
                        ? null
                        : Label(pair.ContrastLabel)
                            .ThemeKey(MyTheme.Caption1)
                )
                .HFill(),

                // Delete button - far right
                ImageButton()
                    .Source(MyTheme.IconDelete)
                    .OnClicked(async () => await OnDeletePairAsync(pair))
                    .WidthRequest(40)
                    .HeightRequest(40)
            )
            .Padding(MyTheme.Size120)
        )
        .ThemeKey(MyTheme.CardStyle)
        .Background(isSelected ? MyTheme.PrimaryDark : Colors.Transparent)
        .Margin(MyTheme.Size80, MyTheme.Size40)
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
        var confirmed = await Application.Current!.MainPage!.DisplayAlert(
            $"{_localize["MinimalPairsDeleteConfirm"]}",
            $"{pair.VocabularyWordA?.TargetLanguageTerm} vs {pair.VocabularyWordB?.TargetLanguageTerm}",
            $"{_localize["Delete"]}",
            $"{_localize["Cancel"]}"
        );

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
