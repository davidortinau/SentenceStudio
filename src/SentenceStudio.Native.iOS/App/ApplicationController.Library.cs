using Foundation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SwiftUI;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Native.iOS.Features.Activities;
using SentenceStudio.Native.iOS.Features.Sam;
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

/// <summary>What Sam's slot is saying instead of opening Sam.</summary>
internal enum SamSlotNotice
{
    None,
    /// <summary>The tap is reading availability again.</summary>
    Checking,
    /// <summary>The server answered that Sam is not offered to this owner.</summary>
    NotOffered,
    Offline,
    /// <summary>The availability read failed while the device was online.</summary>
    Unreachable
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
    private bool _resourcesListReadByTab;
    private long _libraryResetVersion;
    private string? _libraryErrorKey;
    /// <summary>Counts the Settings coordinators opened, so a retried one always re-renders.</summary>
    private int _settingsGeneration;

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
        _resourcesListReadByTab = false;
        if (_selectedTab == NativeTabs.Library)
        {
            // The first visit reads the owner's resources once, for the Resources row's
            // count; the Resources page reads them again when it opens.
            EnsureResources();
            if (_resources is { ListState.Phase: NativeResourceListPhase.Idle } resources)
            {
                _resourcesListReadByTab = true;
                resources.OnBrowserAppeared();
            }
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

    /// <summary>
    /// An editor is open in the Library: Save or Cancel decides how it closes, so nothing
    /// from another tab replaces it. A smart resource being read is not an editor.
    /// </summary>
    internal bool HasLibraryEditor => IsResourcesFormOpen && !IsResourceFormReadOnly
        || IsVocabularyWordOpen || IsSkillFormOpen || LibraryPairsHasChildPage;

    /// <summary>Bumped when a Study entry closes the Library's pages; the scene pops to the list.</summary>
    internal long LibraryResetVersion => _libraryResetVersion;

    /// <summary>A Library row that could not open says so under the list.</summary>
    internal string? LibraryErrorText => _libraryErrorKey is { } key ? Text(key) : null;

    /// <summary>A Library list row: opens its page, which the scene pushes above the list.</summary>
    internal void OpenLibraryPage(LibrarySection page)
    {
        if (_destination != NativeDestination.Dashboard || _selectedTab != NativeTabs.Library
            || NavigationBlocked || IsLibraryPageOpen || IsResourcesFormOpen
            || !Enum.IsDefined(page))
            return;
        _libraryErrorKey = null;
        switch (page)
        {
            case LibrarySection.Resources:
                EnsureResources();
                if (_resources is null)
                    return;
                _libraryResourcesOpen = true;
                // A list this Library visit has just read is not read twice.
                if (!_resourcesListReadByTab)
                    _resources.OnBrowserAppeared();
                _resourcesListReadByTab = false;
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

    /// <summary>
    /// A Library row's open failed: Study's plan is untouched (Run's Dashboard handling is
    /// for Study actions) and the Library list says so.
    /// </summary>
    private void OnLibraryActionFailed() =>
        _libraryErrorKey = NativeText.UnexpectedError;

    /// <summary>
    /// A Study entry found an editor open in the Library: the Library tab shows it, so the
    /// learner sees what has to be saved or cancelled first.
    /// </summary>
    private void ShowLibraryEditor()
    {
        _selectedTab = NativeTabs.Library;
        _resourcesListReadByTab = false;
        Publish();
    }

    /// <summary>
    /// Closes the pages a learner was browsing in the Library (none of them holds unsaved
    /// work) so a Study entry can open the Resources page from the list.
    /// </summary>
    private void ResetLibraryToList()
    {
        if (IsResourcesFormOpen)
            _resources!.OnFormPopped();
        _resourceFormFromDashboard = false;
        _libraryResourcesOpen = false;
        if (IsRecentLookupsOpen)
            _recentLookups?.Close();
        if (IsVocabularyOpen)
            OnVocabularyListPopped();
        if (IsSkillsOpen)
            _skills!.Close();
        ReleaseLibraryPairs();
        _libraryResetVersion++;
    }

    // ---- Settings tab ----

    /// <summary>
    /// Every Settings visit re-reads the profile row and what Sam remembers. The owner's
    /// settings stay open between visits (an unsaved quiz edit survives a tab switch) while
    /// their owner is current; a refused open or an owner change is retried on the next visit.
    /// </summary>
    internal void OpenSettings()
    {
        if (_destination != NativeDestination.Dashboard || _services is null)
            return;
#if DEBUG
        const bool debugFixtureBuild = true;
        static string? PreviewOwner() => NativePreviewFixture.FromAssembly()?.Owner;
#else
        const bool debugFixtureBuild = false;
        static string? PreviewOwner() => null;
#endif
        _settingsOpen = true;
        _ = RefreshProfileSummaryAsync();
        OpenSamSettings();
        var provider = _services.Provider;
        var scope = provider.GetRequiredService<DeviceUserScopeProvider>();
        var preferences = provider.GetRequiredService<IPreferencesService>();
        var owner = SettingsOwnerAccess.Resolve(
            _authState, _preview, debugFixtureBuild,
            NSBundle.MainBundle.BundleIdentifier, PreviewOwner,
            scope, preferences, _logger!);
        if (_settings is { } current)
        {
            var state = current.State;
            if (owner is not null && state is { IsOpen: true, OwnerBlocked: false })
            {
                // Voices that could not be read are read again on this visit.
                if (!_preview && state is { VoiceLoading: false, VoiceErrorKey: not null,
                        Quiz: { } quiz })
                    _ = LoadSettingsVoicesAsync(current, owner, quiz.VoiceLanguage);
                return;
            }
            current.Changed -= OnSettingsChanged;
            current.Dispose();
            _settings = null;
        }
        if (owner is null)
            return;
        _settingsGeneration++;
        var settings = new SettingsCoordinator(scope,
            preferences,
            provider.GetRequiredService<NativeQuizSettings>(),
            provider.GetRequiredService<ILogger<SettingsCoordinator>>(),
            allowVoiceDiscovery: !_preview);
        settings.Changed += OnSettingsChanged;
        if (!settings.Open(owner))
        {
            _logger?.LogWarning("Settings refused an unavailable owner; the next visit tries again.");
            settings.Changed -= OnSettingsChanged;
            settings.Dispose();
            return;
        }
        _settings = settings;
        _ = LoadSettingsVoicesAsync(settings, owner, settings.State.Quiz!.VoiceLanguage);
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
            var summary = (profile.Name ?? string.Empty,
                profile.NativeLanguage ?? string.Empty, profile.TargetLanguage ?? string.Empty);
            if (_profileSummary == summary)
                return;
            _profileSummary = summary;
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

    private SamSlotNotice _samSlotNotice;
    private int _samSlotCheck;
    private bool _samSlotCheckingShown;

    /// <summary>A check that answers within this time shows its answer without a "checking" step.</summary>
    private static readonly TimeSpan SamSlotCheckingDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Sam's slot is shown to every signed-in owner (Captain's request for the trial,
    /// 2026-11-05), also while Sam is not offered to them; the Debug preview has no Sam.
    /// </summary>
    internal bool IsSamSlotShown => IsSamShellDestination(_destination) && !_preview;

    /// <summary>
    /// The slot's notice is up: not offered, offline, or not reachable, or "checking" once
    /// the check has taken longer than a moment.
    /// </summary>
    internal bool IsSamUnavailableNoticePresented => _samSlotNotice switch
    {
        SamSlotNotice.None => false,
        SamSlotNotice.Checking => _samSlotCheckingShown,
        _ => true
    };

    internal string SamSlotNoticeText
    {
        get
        {
            var key = _samSlotNotice switch
            {
                SamSlotNotice.Checking => "NativeSam_SlotChecking",
                SamSlotNotice.Offline => "NativeSam_SlotOffline",
                SamSlotNotice.Unreachable => "NativeSam_SlotUnreachable",
                _ => "Coach_SettingsUnavailable"
            };
            var text = Text(key);
            return key.StartsWith("NativeSam_", StringComparison.Ordinal)
                ? string.Format(System.Globalization.CultureInfo.CurrentCulture, text,
                    SamView.Persona(SamState))
                : text;
        }
    }

    internal void DismissSamUnavailableNotice()
    {
        if (_samSlotNotice == SamSlotNotice.None)
            return;
        _samSlotNotice = SamSlotNotice.None;
        _samSlotCheckingShown = false;
        Publish();
    }

    /// <summary>
    /// The trailing slot opens the same Sam as every other entry: full height on a
    /// phone (or a narrow window), the side panel at its usual size on iPad. When Sam is
    /// not offered yet, the tap reads availability again and says "not available" only
    /// after the server answered so; offline and an unreachable server have their own text.
    /// </summary>
    private void OpenSamFromTab()
    {
        if (IsSamAvailable)
        {
            OpenSamPanelFromTab();
            return;
        }
        if (!IsSamSlotShown || EnsureSam() is not { } sam)
            return;
        var check = ++_samSlotCheck;
        _samSlotNotice = SamSlotNotice.Checking;
        _samSlotCheckingShown = false;
        _ = ShowSamSlotCheckingAsync(check);
        _ = CheckSamForSlotAsync(sam, check);
    }

    /// <summary>The brief loading state: shown only when the check is still running after a moment.</summary>
    private async Task ShowSamSlotCheckingAsync(int check)
    {
        try
        {
            await Task.Delay(SamSlotCheckingDelay, _lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (_disposed || check != _samSlotCheck || _samSlotNotice != SamSlotNotice.Checking)
            return;
        _samSlotCheckingShown = true;
        Publish();
    }

    private void OpenSamPanelFromTab()
    {
        if (!IsSamEntryVisible)
            return;
        var size = NativeDeviceIdiom.IsPhone || _samViewportWidth < 768
            ? NativeSamPanelSize.Expanded
            : _samCompactDefault ? NativeSamPanelSize.Compact : NativeSamPanelSize.Expanded;
        RunSam(sam => sam.OpenAsync(size, _lifetime.Token));
    }

    private async Task CheckSamForSlotAsync(NativeSamPresenter sam, int check)
    {
        var offline = _services?.Provider.GetService<IConnectivityService>()
            is { IsInternetAvailable: false };
        if (!offline)
        {
            try
            {
                await sam.RefreshAvailabilityAsync(_lifetime.Token);
            }
            catch (OperationCanceledException) when (_disposed || _lifetime.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Sam's slot could not read availability.");
            }
        }
        // Closed (or tapped again) while checking: nothing more to say.
        if (_disposed || check != _samSlotCheck || _samSlotNotice != SamSlotNotice.Checking)
            return;
        if (IsSamAvailable)
        {
            _samSlotNotice = SamSlotNotice.None;
            OpenSamPanelFromTab();
        }
        else
            _samSlotNotice = offline || sam.State.Availability == NativeSamAvailability.Offline
                ? SamSlotNotice.Offline
                : sam.State.Availability == NativeSamAvailability.NotOffered
                    ? SamSlotNotice.NotOffered : SamSlotNotice.Unreachable;
        _logger?.LogInformation("Sam's slot read availability {Availability}: {Notice}.",
            sam.State.Availability, _samSlotNotice);
        Publish();
    }

    private void ResetSamSlot()
    {
        _samSlotNotice = SamSlotNotice.None;
        _samSlotCheckingShown = false;
        _samSlotCheck++;
    }

    // ---- Minimal Pairs in the Library ----

    private IActivityScreen? _libraryPairs;
    private IServiceScope? _libraryPairsScope;
    private NativeToastScheduler? _libraryPairsToasts;

    internal bool IsLibraryPairsOpen => _libraryPairs is not null;

    internal bool LibraryPairsHasChildPage => _libraryPairs?.HasChildPage == true;

    internal string LibraryPairsTitle => Text(NativeMinimalPairsText.Title);

    internal string LibraryPairsChildTitle => _libraryPairs?.ChildTitle ?? string.Empty;

    /// <summary>Changes with every state the Minimal Pairs screen applies (its list and its child page).</summary>
    internal long LibraryPairsVersion => _libraryPairs?.ChildVersion ?? 0;

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
        RunLibrary(async () =>
        {
            var selection = _dashboard.SelectNavigationEntry();
            if (selection is null)
            {
                _logger?.LogWarning("Minimal Pairs refused an absent owner.");
                _libraryErrorKey = NativeMinimalPairsText.Unavailable;
                return;
            }
            var launch = await _services.Provider
                .GetRequiredService<NativeActivityLaunchService>()
                .FromChooseOwnAsync(selection, NativeActivityCatalog.MinimalPairs, _lifetime.Token);
            if (launch is null || _libraryPairs is not null)
            {
                if (launch is null)
                {
                    _logger?.LogWarning("Minimal Pairs launch was refused.");
                    _libraryErrorKey = NativeMinimalPairsText.Unavailable;
                }
                return;
            }
            var scope = _services.Provider.CreateScope();
            var screen = ActivityScreens.Create(launch.Route, new ActivityScreenHost(
                scope.ServiceProvider, Text, Publish, Run, LeaveLibraryPairs, _logger, _lifetime.Token));
            if (screen is null)
            {
                scope.Dispose();
                _libraryErrorKey = NativeMinimalPairsText.Unavailable;
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

    /// <summary>
    /// The page leaves at once; the screen and its scope are disposed after the presenter's
    /// gated exit, so a command it is still running (a pair delete) finishes or cancels first.
    /// </summary>
    private void ReleaseLibraryPairs()
    {
        _libraryPairsToasts?.Reset();
        if (_libraryPairs is not { } screen)
            return;
        var scope = _libraryPairsScope;
        _libraryPairs = null;
        _libraryPairsScope = null;
        // The page (and the delete alert wrapped around it) leaves the stack; the exit's
        // last state must not touch those disposed views.
        screen.OnPagePopped();
        _ = ExitLibraryPairsAsync(screen, scope);
    }

    private async Task ExitLibraryPairsAsync(IActivityScreen screen, IServiceScope? scope)
    {
        try
        {
            await screen.ExitAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "The Library Minimal Pairs list's exit failed.");
        }
        finally
        {
            screen.Dispose();
            scope?.Dispose();
        }
    }
}
