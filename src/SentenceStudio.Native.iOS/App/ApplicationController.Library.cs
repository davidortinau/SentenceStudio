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

/// <summary>The Library switcher pages, in switcher order.</summary>
internal enum LibrarySection
{
    Resources,
    Vocabulary,
    Skills,
    Pairs
}

/// <summary>
/// Tab selection, the Library switcher (remembered on this device), the Settings tab,
/// Sam's tab slot, and the Minimal Pairs list hosted in the Library.
/// </summary>
internal sealed partial class ApplicationController
{
    private const string LibrarySectionKey = "native.library.section";
    private LibrarySection? _librarySection;
    private (string Name, string NativeLanguage, string TargetLanguage)? _profileSummary;

    /// <summary>The Library page last used on this device (read once the services exist).</summary>
    internal LibrarySection SelectedLibrarySection
    {
        get
        {
            if (_librarySection is { } section)
                return section;
            if (_services is null)
                return LibrarySection.Resources;
            var stored = LoadLibrarySection();
            _librarySection = stored;
            return stored;
        }
    }

    private LibrarySection LoadLibrarySection()
    {
        if (_services is null)
            return LibrarySection.Resources;
        var stored = _services.Provider.GetRequiredService<IPreferencesService>()
            .Get(LibrarySectionKey, nameof(LibrarySection.Resources));
        return Enum.TryParse<LibrarySection>(stored, out var section)
            && Enum.IsDefined(section) ? section : LibrarySection.Resources;
    }

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
            OpenLibrarySection();
        else if (_selectedTab == NativeTabs.Settings)
            OpenSettings();
        Publish();
        if (_selectedTab == NativeTabs.Study && _refreshChooseOwnAfterSkills)
            ReloadChooseOwnAfterSkills();
    }

    /// <summary>The switcher: closes the page on screen and opens the chosen one.</summary>
    internal void SelectLibrarySection(LibrarySection section)
    {
        if (_destination != NativeDestination.Dashboard || _selectedTab != NativeTabs.Library
            || NavigationBlocked || !Enum.IsDefined(section) || section == SelectedLibrarySection
            || HasLibraryRoute)
            return;
        CloseLibrarySection();
        _librarySection = section;
        _services?.Provider.GetRequiredService<IPreferencesService>()
            .Set(LibrarySectionKey, section.ToString());
        OpenLibrarySection();
        Publish();
    }

    /// <summary>Study's Add Resource lands on the Library's Resources page, where the new resource is listed.</summary>
    private void ShowLibraryResources()
    {
        if (SelectedLibrarySection == LibrarySection.Resources)
            return;
        CloseLibrarySection();
        _librarySection = LibrarySection.Resources;
        _services?.Provider.GetRequiredService<IPreferencesService>()
            .Set(LibrarySectionKey, nameof(LibrarySection.Resources));
    }

    /// <summary>A page is pushed over the Library root (the switcher is not on screen).</summary>
    internal bool HasLibraryRoute => IsResourcesFormOpen || IsVocabularyWordOpen
        || IsRecentLookupsOpen || IsSkillFormOpen || LibraryPairsHasChildPage;

    private void OpenLibrarySection()
    {
        switch (SelectedLibrarySection)
        {
            case LibrarySection.Resources:
                EnsureResources();
                _resources?.OnBrowserAppeared();
                break;
            case LibrarySection.Vocabulary:
                if (!IsVocabularyOpen)
                    OpenVocabulary();
                break;
            case LibrarySection.Skills:
                if (!IsSkillsOpen)
                    OpenSkills();
                break;
            case LibrarySection.Pairs:
                if (_libraryPairs is null)
                    OpenLibraryPairs();
                break;
        }
    }

    private void CloseLibrarySection()
    {
        switch (SelectedLibrarySection)
        {
            case LibrarySection.Vocabulary:
                if (IsRecentLookupsOpen)
                    OnRecentLookupsPopped();
                _vocabulary?.OnListPopped();
                break;
            case LibrarySection.Skills:
                OnSkillsPopped();
                break;
            case LibrarySection.Pairs:
                ReleaseLibraryPairs();
                break;
        }
    }

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
    /// The trailing slot opens the same Sam as every other entry: full height on a
    /// phone (or a narrow window), the side panel at its usual size on iPad.
    /// </summary>
    private void OpenSamFromTab()
    {
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
            if (launch is null || _libraryPairs is not null
                || SelectedLibrarySection != LibrarySection.Pairs)
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

    /// <summary>The list's Go Back (shown with an error) returns the Library to Resources.</summary>
    private void LeaveLibraryPairs()
    {
        if (SelectedLibrarySection == LibrarySection.Pairs)
            SelectLibrarySection(LibrarySection.Resources);
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
