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

/// <summary>
/// Tab selection, the Library list (each row pushes a page on the Library stack, the
/// Apple Music Library pattern), the Settings tab, Sam's tab slot, and the Minimal Pairs
/// list hosted in the Library.
/// </summary>
internal sealed partial class ApplicationController
{
    private (string Name, string NativeLanguage, string TargetLanguage)? _profileSummary;
    private bool _libraryResourcesOpen;
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
        if (_selectedTab == NativeTabs.Library)
        {
            // The owner's resources (the Resources row's count, and the Resources page when
            // it is open) are read when never read, after a failed read, or when stale.
            EnsureResources();
            ReadResourcesIfStale();
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

    /// <summary>
    /// An editor is open in the Library: Save or Cancel decides how it closes, so nothing
    /// from another tab replaces it. A smart resource being read is not an editor.
    /// </summary>
    internal bool HasLibraryEditor => (IsResourcesFormOpen && !IsResourceFormReadOnly)
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
                // A list read a moment ago (the Library tab's own read) is not read twice.
                ReadResourcesIfStale();
                Publish();
                break;
            case LibrarySection.Vocabulary:
                OpenVocabulary();
                break;
            case LibrarySection.Skills:
                OpenSkills();
                break;
            case LibrarySection.Pairs:
                OpenLibraryPairs(create: false);
                break;
        }
    }

    // ---- Quick add (below the Library list) ----

    /// <summary>
    /// One quick-add button: a stable id suffix (native.library.quickadd.&lt;Id&gt;), an SF
    /// Symbol, a localized label key and the create flow it opens.
    /// </summary>
    internal sealed record LibraryQuickAddItem(string Id, string Symbol, string TitleKey, Action Open);

    /// <summary>
    /// Native Import content and YouTube import are being built separately; their buttons
    /// stay off until <see cref="OpenImportContent"/> and <see cref="OpenYouTubeImport"/> open
    /// a real flow, so the grid ships only working buttons.
    /// </summary>
    private const bool ImportContentReady = false;
    private const bool YouTubeImportReady = false;

    /// <summary>
    /// The quick-add grid under the Library list, in order: every existing native create flow
    /// (a new resource, skill profile or minimal pair), then the imports once they exist.
    /// Add an item here to add a button.
    /// </summary>
    internal IReadOnlyList<LibraryQuickAddItem> LibraryQuickAddItems
    {
        get
        {
            var items = new List<LibraryQuickAddItem>
            {
                new("resource", "doc.badge.plus", "Resources_AddResource",
                    () => OpenLibraryQuickAdd(OpenResourceCreate)),
                new("skill", "target", "Skills_AddSkillProfile",
                    () => OpenLibraryQuickAdd(() => OpenSkills(create: true))),
                new("minimal-pair", "waveform.badge.plus", NativeMinimalPairsText.CreatePair,
                    () => OpenLibraryQuickAdd(() => OpenLibraryPairs(create: true))),
            };
#pragma warning disable CS0162 // The imports are off until their flows exist.
            if (ImportContentReady)
                items.Add(new("import-content", "square.and.arrow.down", "Nav_ImportContent",
                    () => OpenLibraryQuickAdd(OpenImportContent)));
            if (YouTubeImportReady)
                items.Add(new("youtube-import", "play.rectangle", "ImportFromYouTube",
                    () => OpenLibraryQuickAdd(OpenYouTubeImport)));
#pragma warning restore CS0162
            return items;
        }
    }

    /// <summary>A quick-add button works from the Library list, like its rows.</summary>
    private void OpenLibraryQuickAdd(Action open)
    {
        if (_destination != NativeDestination.Dashboard || _selectedTab != NativeTabs.Library
            || NavigationBlocked || IsLibraryPageOpen || IsResourcesFormOpen)
            return;
        _libraryErrorKey = null;
        open();
    }

    /// <summary>
    /// Native Import content (another agent's work): not wired yet, so its quick-add button
    /// is off (<see cref="ImportContentReady"/>).
    /// </summary>
    internal void OpenImportContent() =>
        _logger?.LogWarning("Import content is not available on iOS yet.");

    /// <summary>
    /// Native YouTube import (another agent's work): not wired yet, so its quick-add button
    /// is off (<see cref="YouTubeImportReady"/>).
    /// </summary>
    internal void OpenYouTubeImport() =>
        _logger?.LogWarning("YouTube import is not available on iOS yet.");

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
    /// The one freshness path for the owner's resources: read (in place) unless the last
    /// successful read started after the latest sync. Never read, failed and pre-sync reads
    /// are read again; a fresh list is not read twice.
    /// </summary>
    private void ReadResourcesIfStale() => _resources?.ReadListIfStale();

    /// <summary>
    /// A Library row's action failed: Study's plan is untouched (Run's Dashboard handling is
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
        Publish();
    }

    /// <summary>
    /// Closes the browse pages above the Library list (none of them holds unsaved work) so
    /// a Study entry can open the Resources page from the list. The caller has closed any
    /// resource detail and sets the Resources page itself.
    /// </summary>
    private void ResetLibraryToList()
    {
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

    /// <summary>Where a tap on Sam's slot is when Sam is not offered yet.</summary>
    private enum SamSlotPhase
    {
        Idle,
        /// <summary>Reading availability; nothing shown for the first moment.</summary>
        Checking,
        /// <summary>Still reading: the busy overlay with Cancel is up.</summary>
        CheckingShown,
        /// <summary>The answer alert is up (not offered, offline or not reachable).</summary>
        Answered
    }

    private SamSlotPhase _samSlotPhase;
    private int _samSlotCheck;
    private string _samSlotCheckingText = string.Empty;
    private string _samSlotAnswerText = string.Empty;

    /// <summary>A check that answers within this time shows its answer without a "checking" step.</summary>
    private static readonly TimeSpan SamSlotCheckingDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>The longest the slot waits for the server's answer (an answer given is still published).</summary>
    private static readonly TimeSpan SamSlotCheckLimit = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Sam's slot is shown to every signed-in owner (Captain's request for the trial,
    /// 2026-11-05), also while Sam is not offered to them; the Debug preview has no Sam.
    /// </summary>
    internal bool IsSamSlotShown => IsSamShellDestination(_destination) && !_preview;

    /// <summary>
    /// The slot's brief loading state: the busy overlay (a SwiftUI overlay, not a
    /// presentation) with Cancel, once the check has taken longer than a moment. It blocks
    /// navigation (touch, keyboard and focus) like the starter overlay.
    /// </summary>
    internal bool IsSamSlotCheckingShown => _samSlotPhase == SamSlotPhase.CheckingShown;

    /// <summary>The busy overlay over the tab shell: starter creation, or Sam's slot checking.</summary>
    internal bool IsBlockingOverlayPresented => IsStarterBusy || IsSamSlotCheckingShown;

    internal string BlockingOverlayTitle => !IsStarterBusy && IsSamSlotCheckingShown
        ? _samSlotCheckingText : StarterBusyTitle;

    /// <summary>The overlay's Cancel is shown only for Sam's check (starter creation cannot be cancelled).</summary>
    internal bool IsBlockingOverlayCancellable => !IsStarterBusy && IsSamSlotCheckingShown;

    /// <summary>The answer alert is up: not offered, offline, or not reachable.</summary>
    internal bool IsSamUnavailableNoticePresented => _samSlotPhase == SamSlotPhase.Answered;

    /// <summary>The answer, formatted once when the check finished.</summary>
    internal string SamSlotNoticeText => _samSlotAnswerText;

    internal void DismissSamUnavailableNotice()
    {
        if (_samSlotPhase != SamSlotPhase.Answered)
            return;
        _samSlotPhase = SamSlotPhase.Idle;
        Publish();
    }

    /// <summary>Cancel on the checking overlay: the late answer is ignored.</summary>
    internal void CancelSamSlotCheck()
    {
        if (_samSlotPhase is not (SamSlotPhase.Checking or SamSlotPhase.CheckingShown))
            return;
        _samSlotCheck++;
        _samSlotPhase = SamSlotPhase.Idle;
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
        if (!IsSamSlotShown || _samSlotPhase is SamSlotPhase.Checking or SamSlotPhase.CheckingShown
            || EnsureSam() is not { } sam)
            return;
        var check = ++_samSlotCheck;
        _samSlotPhase = SamSlotPhase.Checking;
        _samSlotCheckingText = SamSlotText(NativeSamAvailabilityText.Checking);
        _ = ShowSamSlotCheckingAsync(check);
        _ = CheckSamForSlotAsync(sam, check);
    }

    /// <summary>The brief loading state: shown only when the check is still running after a moment.</summary>
    private async Task ShowSamSlotCheckingAsync(int check)
    {
        try
        {
            await Task.Delay(SamSlotCheckingDelay, _lifetime.Token);
            if (_disposed || check != _samSlotCheck || _samSlotPhase != SamSlotPhase.Checking)
                return;
            _samSlotPhase = SamSlotPhase.CheckingShown;
            Publish();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Not awaited: log instead of leaving an unobserved exception (as RunSafelyAsync).
            _logger?.LogError(ex, "Sam's slot could not show its checking state.");
        }
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

    /// <summary>
    /// Only this read's answer decides the text: a read that failed or gave up before the
    /// server answered is "not reachable"; a cancelled check (Cancel, sign-out) says nothing.
    /// </summary>
    private async Task CheckSamForSlotAsync(NativeSamPresenter sam, int check)
    {
        try
        {
            NativeSamAvailability answer;
            if (_services?.Provider.GetService<IConnectivityService>() is { IsInternetAvailable: false })
                answer = NativeSamAvailability.Offline;
            else
            {
                answer = NativeSamAvailability.Unreachable;
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                limit.CancelAfter(SamSlotCheckLimit);
                try
                {
                    answer = await sam.RefreshAvailabilityAsync(limit.Token);
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
            if (_disposed || check != _samSlotCheck)
                return;
            _logger?.LogInformation("Sam's slot read availability: {Availability}.", answer);
            if (IsSamAvailable)
            {
                _samSlotPhase = SamSlotPhase.Idle;
                OpenSamPanelFromTab();
            }
            else
            {
                _samSlotAnswerText = SamSlotText(NativeSamAvailabilityText.NoticeKey(answer)
                    ?? NativeSamAvailabilityText.Unreachable);
                _samSlotPhase = SamSlotPhase.Answered;
            }
            Publish();
        }
        catch (Exception ex)
        {
            // Not awaited: log instead of leaving an unobserved exception, and never leave
            // the checking overlay up.
            _logger?.LogError(ex, "Sam's slot check failed.");
            if (!_disposed && check == _samSlotCheck)
            {
                _samSlotPhase = SamSlotPhase.Idle;
                PublishLoggingFailure();
            }
        }
    }

    /// <summary>A slot text with Sam's persona name (the same name as his panel).</summary>
    private string SamSlotText(string key) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, Text(key),
            SamView.Persona(SamState));

    private void ResetSamSlot()
    {
        _samSlotCheck++;
        _samSlotPhase = SamSlotPhase.Idle;
    }

    // ---- Minimal Pairs in the Library ----

    private IActivityScreen? _libraryPairs;
    private IServiceScope? _libraryPairsScope;
    private NativeToastScheduler? _libraryPairsToasts;

    internal bool IsLibraryPairsOpen => _libraryPairs is not null;

    internal bool LibraryPairsHasChildPage => _libraryPairs?.HasChildPage == true;

    internal string LibraryPairsTitle => Text(NativeMinimalPairsText.Title);

    internal string LibraryPairsChildTitle => _libraryPairs?.ChildTitle ?? string.Empty;

    /// <summary>Changes when what the Minimal Pairs list page shows changes.</summary>
    internal long LibraryPairsVersion => _libraryPairs?.PageVersion ?? 0;

    /// <summary>Changes when the create page or the listening session changes.</summary>
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
    private bool _libraryPairsCreateRequested;

    /// <summary>The Minimal Pairs list; with <paramref name="create"/>, its create page opens above it.</summary>
    private void OpenLibraryPairs(bool create)
    {
        if (_destination != NativeDestination.Dashboard || _libraryPairs is not null
            || _services is null || _dashboard is null)
            return;
        _libraryPairsCreateRequested = create;
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
            if (_libraryPairsCreateRequested && ReferenceEquals(_libraryPairs, screen)
                && screen is MinimalPairsScreen pairs)
                pairs.OpenCreate();
            _libraryPairsCreateRequested = false;
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
            // Instead of an endless spinner: the list's error with its Go Back.
            if (ReferenceEquals(_libraryPairs, screen) && !_disposed)
                screen.OnOpenFailed();
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
