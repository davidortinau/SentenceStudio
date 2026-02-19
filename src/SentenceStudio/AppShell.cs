using MauiReactor.Parameters;
using UXDivers.Popups.Maui.Controls;
using UXDivers.Popups.Services;
using SentenceStudio.Pages.Account;
using SentenceStudio.Pages.Dashboard;
using SentenceStudio.Pages.Onboarding;
using SentenceStudio.Pages.Skills;
using SentenceStudio.Pages.YouTube;
using SentenceStudio.Pages.LearningResources;
using SentenceStudio.Pages.VocabularyManagement;
using SentenceStudio.Pages.MinimalPairs;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;
using MauiReactor.Shapes;
using System.Collections.Generic;
using System.Linq;

namespace SentenceStudio;

interface IAppState
{
    Microsoft.Maui.ApplicationModel.AppTheme CurrentAppTheme { get; set; }
    UserProfile CurrentUserProfile { get; set; }
}

public class AppState : IAppState
{
    public Microsoft.Maui.ApplicationModel.AppTheme CurrentAppTheme { get; set; }

    public UserProfile CurrentUserProfile { get; set; }
}

public partial class AppShell : Component
{
    [Param]
    private readonly IParameter<AppState> state;

    [Inject] ILogger<AppShell> _logger;
    [Inject] NativeThemeService _themeService;

    private bool _initialized = false;
    private bool _isLoadingProfile = false;
    private bool _flyoutCollapsed = Preferences.Default.Get("flyout_collapsed", false);
    private string _currentRoute = "dashboard";

    [Inject] UserProfileRepository _userProfileRepository;

    private record NavItem(string Title, string Icon, string Route, bool SeparatorBefore = false);

    private static readonly NavItem[] _topNavItems =
    [
        new("Dashboard", BootstrapIcons.HouseDoor, "dashboard"),
        new("Learning Resources", BootstrapIcons.Book, "ListLearningResourcesPage"),
        new("Vocabulary", BootstrapIcons.CardText, "VocabularyManagementPage"),
        new("Minimal Pairs", BootstrapIcons.Soundwave, "MinimalPairsPage"),
        new("Skills", BootstrapIcons.Bullseye, "ListSkillProfilesPage"),
        new("Import", BootstrapIcons.BoxArrowInDown, "YouTubeImportPage"),
    ];

    private static readonly NavItem[] _bottomNavItems =
    [
        new("Profile", BootstrapIcons.Person, "UserProfilePage"),
        new("Settings", BootstrapIcons.Gear, "SettingsPage"),
    ];

    protected override void OnMounted()
    {
        base.OnMounted();

        if (!_initialized && !_isLoadingProfile)
        {
            _initialized = true;
            _themeService.Initialize();
            _themeService.ThemeChanged += OnThemeChanged;
            LoadUserProfileAsync();
        }
    }

    protected override void OnWillUnmount()
    {
        _themeService.ThemeChanged -= OnThemeChanged;
        base.OnWillUnmount();
    }

    private void OnThemeChanged(object sender, ThemeChangedEventArgs e)
    {
        Invalidate();
    }

    private async void LoadUserProfileAsync()
    {
        if (_isLoadingProfile) return;
        _isLoadingProfile = true;

        try
        {
            if (_userProfileRepository != null)
            {
                var profile = await _userProfileRepository.GetAsync();
                state.Set(s => s.CurrentUserProfile = profile);
            }
        }
        finally
        {
            _isLoadingProfile = false;
        }
    }

    // Method to refresh the user profile state (called after reset)
    public async Task RefreshUserProfileAsync()
    {
        var currentProfile = await _userProfileRepository.GetAsync();
        state.Set(s => s.CurrentUserProfile = currentProfile);
    }

    public override VisualNode Render()
    {
        var isOnboarded = Preferences.Default.Get("is_onboarded", false);
        var hasProfile = state.Value.CurrentUserProfile != null;

        _logger.LogDebug("AppShell Render - isOnboarded: {IsOnboarded}, hasProfile: {HasProfile}", isOnboarded, hasProfile);
        if (hasProfile)
        {
            _logger.LogDebug("Profile exists - Name: '{ProfileName}', Id: {ProfileId}", state.Value.CurrentUserProfile.Name, state.Value.CurrentUserProfile.Id);
        }

        if (!isOnboarded || !hasProfile)
        {
            _logger.LogDebug("Showing OnboardingPage");
            return new OnboardingPage();
        }

        _logger.LogDebug("Showing Main Shell");
        var theme = BootstrapTheme.Current;
        var iconColor = theme.GetOnBackground();
        var isDesktop = DeviceInfo.Idiom == DeviceIdiom.Desktop || DeviceInfo.Idiom == DeviceIdiom.Tablet;
        var collapsed = isDesktop && _flyoutCollapsed;

        return Shell(
            FlyoutItem("Dashboard",
                ShellContent()
                    .Title("Dashboard")
                    .RenderContent(() => new DashboardPage())
                    .Route("dashboard")
            ).Icon(BootstrapIcons.Create(BootstrapIcons.HouseDoor, iconColor, 20)),
            FlyoutItem("Learning Resources",
                ShellContent()
                    .Title("Learning Resources")
                    .RenderContent(() => new ListLearningResourcesPage())
                    .Route(nameof(ListLearningResourcesPage))
            ).Icon(BootstrapIcons.Create(BootstrapIcons.Book, iconColor, 20)),
            FlyoutItem("Vocabulary",
                ShellContent()
                    .Title("Vocabulary")
                    .RenderContent(() => new VocabularyManagementPage())
                    .Route(nameof(VocabularyManagementPage))
            ).Icon(BootstrapIcons.Create(BootstrapIcons.CardText, iconColor, 20)),
            FlyoutItem("Minimal Pairs",
                ShellContent()
                    .Title("Minimal Pairs")
                    .RenderContent(() => new Pages.MinimalPairs.MinimalPairsPage())
                    .Route(nameof(Pages.MinimalPairs.MinimalPairsPage))
            ).Icon(BootstrapIcons.Create(BootstrapIcons.Soundwave, iconColor, 20)),
            FlyoutItem("Skills",
                ShellContent()
                    .Title("Skills")
                    .RenderContent(() => new ListSkillProfilesPage())
                    .Route(nameof(ListSkillProfilesPage))
            ).Icon(BootstrapIcons.Create(BootstrapIcons.Bullseye, iconColor, 20)),
            FlyoutItem("Import",
                ShellContent()
                    .Title("Import")
                    .RenderContent(() => new YouTubeImportPage())
                    .Route(nameof(YouTubeImportPage))
            ).Icon(BootstrapIcons.Create(BootstrapIcons.BoxArrowInDown, iconColor, 20)),
            FlyoutItem("Profile",
                ShellContent()
                    .Title("Profile")
                    .RenderContent(() => new UserProfilePage())
                    .Route(nameof(UserProfilePage))
            ).Icon(BootstrapIcons.Create(BootstrapIcons.Person, iconColor, 20)),
            FlyoutItem("Settings",
                ShellContent()
                    .Title("Settings")
                    .RenderContent(() => new Pages.AppSettings.SettingsPage())
                    .Route(nameof(Pages.AppSettings.SettingsPage))
            ).Icon(BootstrapIcons.Create(BootstrapIcons.Gear, iconColor, 20))
        )
        .FlyoutBehavior(isDesktop ? FlyoutBehavior.Locked : FlyoutBehavior.Flyout)
        .FlyoutWidth(collapsed ? 64 : 240)
        .FlyoutBackgroundColor(theme.GetSurface())
        .FlyoutHeader(RenderFlyoutHeader(theme, collapsed))
        .FlyoutContent(RenderFlyoutNav(theme, collapsed))
        .FlyoutFooter(isDesktop ? RenderFlyoutToggle(theme, collapsed) : null)
        .OnNavigated(OnShellNavigated);
    }

    private void OnShellNavigated(object sender, ShellNavigatedEventArgs e)
    {
        var location = e.Current?.Location?.ToString() ?? "";
        var route = location.TrimStart('/');

        var allItems = _topNavItems.Concat(_bottomNavItems);
        var matchedItem = allItems.FirstOrDefault(n =>
            route.Equals(n.Route, StringComparison.OrdinalIgnoreCase) ||
            route.Contains(n.Route, StringComparison.OrdinalIgnoreCase));

        if (matchedItem != null && _currentRoute != matchedItem.Route)
        {
            _currentRoute = matchedItem.Route;
            Invalidate();
        }
    }

    private VisualNode RenderFlyoutHeader(BootstrapTheme theme, bool collapsed)
    {
        if (collapsed)
        {
            return Grid(
                Image("appicon")
                    .WidthRequest(28)
                    .HeightRequest(28)
                    .Center()
            ).Padding(0, 12);
        }

        return HStack(spacing: 8,
            Image("appicon")
                .WidthRequest(28)
                .HeightRequest(28),
            Label("Sentence Studio")
                .FontAttributes(FontAttributes.Bold)
                .FontSize(14)
                .TextColor(theme.GetOnBackground())
                .VCenter()
        ).Padding(16, 12);
    }

    private VisualNode RenderFlyoutNav(BootstrapTheme theme, bool collapsed)
    {
        var items = new List<VisualNode>();

        foreach (var navItem in _topNavItems)
            items.Add(RenderNavItem(theme, navItem, collapsed));

        // Separator between top and bottom nav groups
        items.Add(BoxView()
            .HeightRequest(1)
            .BackgroundColor(theme.GetOutline())
            .Margin(collapsed ? new Thickness(8, 6) : new Thickness(14, 6)));

        foreach (var navItem in _bottomNavItems)
            items.Add(RenderNavItem(theme, navItem, collapsed));

        return ScrollView(
            VStack(items.ToArray()).Spacing(0)
        );
    }

    private VisualNode RenderNavItem(BootstrapTheme theme, NavItem navItem, bool collapsed)
    {
        var isSelected = _currentRoute == navItem.Route;
        var itemColor = isSelected ? theme.Primary : theme.GetOnBackground();
        var icon = BootstrapIcons.Create(navItem.Icon, itemColor, 20);

        if (collapsed)
        {
            return Grid(
                Image().Source(icon)
                    .WidthRequest(20).HeightRequest(20)
                    .Center()
            )
            .HeightRequest(44)
            .BackgroundColor(Colors.Transparent)
            .OnTapped(async () => await NavigateToRoute(navItem.Route));
        }

        return Grid(
            HStack(spacing: 10,
                Image().Source(icon)
                    .WidthRequest(20).HeightRequest(20).VCenter(),
                Label(navItem.Title)
                    .TextColor(itemColor)
                    .FontSize(14)
                    .VCenter()
            ).Padding(14, 10)
        )
        .BackgroundColor(Colors.Transparent)
        .OnTapped(async () => await NavigateToRoute(navItem.Route));
    }

    private async Task NavigateToRoute(string route)
    {
        _currentRoute = route;
        var isDesktop = DeviceInfo.Idiom == DeviceIdiom.Desktop || DeviceInfo.Idiom == DeviceIdiom.Tablet;
        if (!isDesktop)
            MauiControls.Shell.Current.FlyoutIsPresented = false;
        await MauiControls.Shell.Current.GoToAsync($"//{route}");
        Invalidate();
    }

    private VisualNode RenderFlyoutToggle(BootstrapTheme theme, bool collapsed)
    {
        var chevron = collapsed ? BootstrapIcons.ChevronRight : BootstrapIcons.ChevronLeft;
        var chevronColor = theme.GetOnBackground();
        var iconSource = BootstrapIcons.Create(chevron, chevronColor, 20);

        if (collapsed)
        {
            return Grid(
                Image().Source(iconSource)
                    .WidthRequest(20).HeightRequest(20)
                    .Center()
            )
            .AutomationId("FlyoutToggle")
            .HeightRequest(44)
            .Margin(0, 8)
            .BackgroundColor(Colors.Transparent)
            .OnTapped(() =>
            {
                _flyoutCollapsed = !_flyoutCollapsed;
                Preferences.Default.Set("flyout_collapsed", _flyoutCollapsed);
                Invalidate();
            });
        }

        return Grid(
            HStack(spacing: 8,
                Image().Source(iconSource)
                    .WidthRequest(20).HeightRequest(20)
                    .VCenter(),
                Label("Collapse")
                    .FontSize(13)
                    .TextColor(chevronColor.WithAlpha(0.7f))
                    .VCenter()
            ).Padding(14, 10).VCenter()
        )
        .AutomationId("FlyoutToggle")
        .HeightRequest(44)
        .Margin(0, 8)
        .BackgroundColor(Colors.Transparent)
        .OnTapped(() =>
        {
            _flyoutCollapsed = !_flyoutCollapsed;
            Preferences.Default.Set("flyout_collapsed", _flyoutCollapsed);
            Invalidate();
        });
    }

    public static async Task DisplayToastAsync(string message)
    {
        var toast = new UXDivers.Popups.Maui.Controls.Toast { Title = message };
        await IPopupService.Current.PushAsync(toast);
        _ = Task.Delay(3000).ContinueWith(async _ =>
        {
            try { await IPopupService.Current.PopAsync(toast); } catch { }
        });
    }
}
