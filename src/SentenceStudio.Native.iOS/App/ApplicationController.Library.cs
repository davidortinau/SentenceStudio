using Foundation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SwiftUI;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Native.iOS.Features.Activities;
using SentenceStudio.Native.iOS.Features.Settings;
using SentenceStudio.Native.iOS.Platform;
using SentenceStudio.Native.iOS.Styling;
using SentenceStudio.Services;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.App;

/// <summary>The iOS tab shell trial (Captain, 2026-11-05): Study, Library, Settings, and Sam's own slot.</summary>
internal static class NativeTabs
{
    internal const int Study = 0;
    internal const int Library = 1;
    internal const int Settings = 2;
    /// <summary>Sam's slot (a search-role tab): selecting it opens Sam and keeps the current tab.</summary>
    internal const int Sam = 3;
}

/// <summary>The Library list rows, in list order; each row pushes its page.</summary>
internal enum LibrarySection
{
    Resources,
    Vocabulary,
    Skills,
    Pairs
}

/// <summary>
/// Tab selection, the Library list (each row pushes a page on the Library stack, the
/// Apple Music Library pattern), the Settings tab, Sam's tab slot, and the Minimal Pairs
/// list hosted in the Library.
/// </summary>
internal sealed partial class ApplicationController
{
    private (string Name, string NativeLanguage, string TargetLanguage)? _profileSummary;
    private bool _libraryResourcesOpen;

    internal void SelectTab(int index)
    {
        if (_destination != NativeDestination.Dashboard)
            return;
        if (index == NativeTabs.Sam)
        {
            // The slot is an action: the selection stays on the current tab.
            OpenSamFromTab();
            Publish();
            return;
        }
        _selectedTab = index is NativeTabs.Library or NativeTabs.Settings
            ? index : NativeTabs.Study;
        if (_selectedTab == NativeTabs.Library)
        {
            // Loads the owner's resources once, which also gives the Resources row its count.
            EnsureResources();
            _resources?.OnBrowserAppeared();
        }
        else if (_selectedTab == NativeTabs.Settings)
            OpenSettings();
        Publish();
        if (_selectedTab == NativeTabs.Study && _refreshChooseOwnAfterSkills)
            ReloadChooseOwnAfterSkills();
    }

    /// <summary>The Resources page is pushed above the Library list.</summary>
    internal bool IsLibraryResourcesOpen => _libraryResourcesOpen;

    /// <summary>A page is open above the Library list.</summary>
    internal bool IsLibraryPageOpen => _libraryResourcesOpen || IsVocabularyOpen
        || IsSkillsOpen || _libraryPairs is not null;

    /// <summary>An editor or second page is open in the Library (its root is not on screen).</summary>
    internal bool HasLibraryRoute => IsResourcesFormOpen || IsVocabularyWordOpen
        || IsRecentLookupsOpen || IsSkillFormOpen || LibraryPairsHasChildPage;

    /// <summary>A Library list row: opens its page, which the scene pushes above the list.</summary>
    internal void OpenLibraryPage(LibrarySection page)
    {
        if (_destination != NativeDestination.Dashboard || _selectedTab != NativeTabs.Library
            || NavigationBlocked || IsLibraryPageOpen || IsResourcesFormOpen
            || !Enum.IsDefined(page))
            return;
        switch (page)
        {
            case LibrarySection.Resources:
                EnsureResources();
                if (_resources is null)
                    return;
                _libraryResourcesOpen = true;
                _resources.OnBrowserAppeared();
                Publish();
                break;
            case LibrarySection.Vocabulary:
                OpenVocabulary();
                break;
            case LibrarySection.Skills:
                OpenSkills();
                break;
            case LibrarySection.Pairs:
                OpenLibraryPairs();
                break;
        }
    }

    /// <summary>Back from the Resources page to the Library list.</summary>
    internal void OnLibraryResourcesPopped()
    {
        if (!_libraryResourcesOpen)
            return;
        _libraryResourcesOpen = false;
        Publish();
    }

    /// <summary>
    /// The Resources row's count: the owner's resources once the browser has loaded them
    /// with no search or filter (no extra query). Null hides the count.
    /// </summary>
    internal int? LibraryResourceCount => _resources?.ListState is
        { Phase: NativeResourceListPhase.Populated or NativeResourceListPhase.Empty,
          SearchText.Length: 0, MediaType: null, Language: null } state
            && !state.Items.IsDefault
        ? state.Items.Length : null;

    /// <summary>The Vocabulary row's count: the word total Study already shows. Null hides it.</summary>
    internal int? LibraryVocabularyCount => _dashboardState.VocabSummary is { } summary
        ? summary.New + summary.Learning + summary.Familiar + summary.Review + summary.Known
        : null;

    // ---- Settings tab ----

    /// <summary>The Settings tab opens its owner's settings once and keeps them until sign-out.</summary>
    internal void OpenSettings()
    {
        if (_destination != NativeDestination.Dashboard || _services is null)
            return;
        // Every visit re-reads what Sam remembers, as each Settings push did before.
        if (_settingsOpen)
        {
            OpenSamSettings();
            return;
        }
#if DEBUG
        const bool debugFixtureBuild = true;
        static string? PreviewOwner() => NativePreviewFixture.FromAssembly()?.Owner;
#else
        const bool debugFixtureBuild = false;
        static string? PreviewOwner() => null;
#endif
        var provider = _services.Provider;
        var scope = provider.GetRequiredService<DeviceUserScopeProvider>();
        var preferences = provider.GetRequiredService<IPreferencesService>();
        var owner = SettingsOwnerAccess.Resolve(
            _authState, _preview, debugFixtureBuild,
            NSBundle.MainBundle.BundleIdentifier, PreviewOwner,
            scope, preferences, _logger!);
        if (owner is null)
        {
            _settingsOpen = true;
            return;
        }
        var settings = new SettingsCoordinator(scope,
            preferences,
            provider.GetRequiredService<NativeQuizSettings>(),
            provider.GetRequiredService<ILogger<SettingsCoordinator>>(),
            allowVoiceDiscovery: !_preview);
        settings.Changed += OnSettingsChanged;
        _settings = settings;
        if (!settings.Open(owner))
        {
            _logger?.LogWarning("Settings refused an unavailable owner.");
            settings.Dispose();
            _settings = null;
        }
        _settingsOpen = true;
        OpenSamSettings();
        if (_settings is not null)
            _ = LoadSettingsVoicesAsync(
                settings, owner, settings.State.Quiz!.VoiceLanguage);
        _ = RefreshProfileSummaryAsync();
    }

    /// <summary>The name and languages on the Settings profile row, read for the signed-in owner only.</summary>
    private async Task RefreshProfileSummaryAsync()
    {
        if (_services is null || _authState is not {
            Phase: NativePhase.Ready, UserId: { Length: > 0 } owner })
            return;
        try
        {
            var profile = await _services.Provider.GetRequiredService<UserProfileRepository>()
                .GetByIdAsync(owner, _lifetime.Token);
            if (profile is null || _authState.UserId != owner)
                return;
            _profileSummary = (profile.Name ?? string.Empty,
                profile.NativeLanguage ?? string.Empty, profile.TargetLanguage ?? string.Empty);
            Publish();
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "The Settings profile row could not read the profile.");
        }
    }

    // ---- Sam's tab slot ----

    /// <summary>
    /// Sam's slot is shown to every signed-in owner (Captain's request for the trial,
    /// 2026-11-05), also while Sam is not offered to them; the Debug preview has no Sam.
    /// </summary>
    internal bool IsSamSlotShown => IsSamShellDestination(_destination) && !_preview;

    /// <summary>Sam is not offered to this owner and the slot's notice is up.</summary>
    internal bool IsSamUnavailableNoticePresented { get; private set; }

    internal void DismissSamUnavailableNotice()
    {
        if (!IsSamUnavailableNoticePresented)
            return;
        IsSamUnavailableNoticePresented = false;
        Publish();
    }

    /// <summary>
    /// The trailing slot opens the same Sam as every other entry: full height on a
    /// phone (or a narrow window), the side panel at its usual size on iPad. When Sam is
    /// not offered to this owner it says so with Sam's own settings text instead.
    /// </summary>
    private void OpenSamFromTab()
    {
        if (!IsSamAvailable)
        {
            IsSamUnavailableNoticePresented = IsSamSlotShown;
            return;
        }
        if (!IsSamEntryVisible)
            return;
        var size = NativeDeviceIdiom.IsPhone || _samViewportWidth < 768
            ? NativeSamPanelSize.Expanded
            : _samCompactDefault ? NativeSamPanelSize.Compact : NativeSamPanelSize.Expanded;
        RunSam(sam => sam.OpenAsync(size, _lifetime.Token));
    }

    // ---- Minimal Pairs in the Library ----

    private IActivityScreen? _libraryPairs;
    private IServiceScope? _libraryPairsScope;
    private NativeToastScheduler? _libraryPairsToasts;

    internal bool IsLibraryPairsOpen => _libraryPairs is not null;

    internal bool LibraryPairsHasChildPage => _libraryPairs?.HasChildPage == true;

    internal string LibraryPairsTitle => Text(NativeMinimalPairsText.Title);

    internal string LibraryPairsChildTitle => _libraryPairs?.ChildTitle ?? string.Empty;

    internal long LibraryPairsChildVersion => _libraryPairs?.ChildVersion ?? 0;

    internal SwiftView RenderLibraryPairs(SourceUiStyle style) =>
        _libraryPairs?.Render(style) ?? Views.Text(Text("Common_Loading"));

    internal SwiftView RenderLibraryPairsChild(SourceUiStyle style) =>
        _libraryPairs?.RenderChild(style) ?? Views.VStack();

    /// <summary>The pair list's delete alert, wrapped once around the mounted list.</summary>
    internal SwiftView PresentLibraryPairsOver(SwiftView page) =>
        _libraryPairs?.PresentOver(page) ?? page;

    internal void OnLibraryPairsChildPopped() => _libraryPairs?.OnChildPopped();

    /// <summary>The newest success or info message, shown once as a toast (as on the activity page).</summary>
    internal NativeMessage? LibraryPairsToast
    {
        get
        {
            if (_libraryPairs is not { } screen)
                return null;
            var toasts = _libraryPairsToasts ??= CreateLibraryPairsToasts();
            toasts.Offer(screen.Toast);
            return toasts.Current;
        }
    }

    internal void DismissLibraryPairsToast()
    {
        if (_libraryPairsToasts?.Dismiss() == true)
            Publish();
    }

    private NativeToastScheduler CreateLibraryPairsToasts()
    {
        var toasts = new NativeToastScheduler();
        toasts.Changed += Publish;
        return toasts;
    }

    /// <summary>
    /// Source NavMenu Minimal Pairs: the pair list for the signed-in owner with no
    /// resource or skill. Minimal Pairs keeps no timer or plan item, so the list can
    /// stay open in the Library like any other page.
    /// </summary>
    private void OpenLibraryPairs()
    {
        if (_destination != NativeDestination.Dashboard || _libraryPairs is not null
            || _services is null || _dashboard is null)
            return;
        Run(async () =>
        {
            var selection = _dashboard.SelectNavigationEntry();
            if (selection is null)
            {
                _logger?.LogWarning("Minimal Pairs refused an absent owner.");
                return;
            }
            var launch = await _services.Provider
                .GetRequiredService<NativeActivityLaunchService>()
                .FromChooseOwnAsync(selection, NativeActivityCatalog.MinimalPairs, _lifetime.Token);
            if (launch is null || _libraryPairs is not null)
            {
                if (launch is null)
                    _logger?.LogWarning("Minimal Pairs launch was refused.");
                return;
            }
            var scope = _services.Provider.CreateScope();
            var screen = ActivityScreens.Create(launch.Route, new ActivityScreenHost(
                scope.ServiceProvider, Text, Publish, Run, LeaveLibraryPairs, _logger, _lifetime.Token));
            if (screen is null)
            {
                scope.Dispose();
                return;
            }
            _libraryPairsScope = scope;
            _libraryPairs = screen;
            _ = OpenLibraryPairsOutsideBusyAsync(screen, launch);
        });
    }

    private async Task OpenLibraryPairsOutsideBusyAsync(IActivityScreen screen, NativeActivityLaunch launch)
    {
        try
        {
            await screen.OpenAsync(launch, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_disposed || _lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (!ReferenceEquals(_libraryPairs, screen))
        {
            _logger?.LogInformation("A released Minimal Pairs list's open ended with {Failure}.",
                ex.GetType().Name);
            return;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Native iOS Minimal Pairs open failed.");
        }
        if (ReferenceEquals(_libraryPairs, screen) && !_disposed)
            Publish();
    }

    /// <summary>The list's Go Back (shown with an error) returns to the Library list.</summary>
    private void LeaveLibraryPairs()
    {
        if (_libraryPairs is null)
            return;
        ReleaseLibraryPairs();
        Publish();
    }

    /// <summary>Back from the Minimal Pairs page to the Library list.</summary>
    internal void OnLibraryPairsPopped()
    {
        if (_libraryPairs is null)
            return;
        ReleaseLibraryPairs();
        Publish();
    }

    private void ReleaseLibraryPairs()
    {
        _libraryPairs?.Dispose();
        _libraryPairs = null;
        _libraryPairsToasts?.Reset();
        _libraryPairsScope?.Dispose();
        _libraryPairsScope = null;
    }
}
