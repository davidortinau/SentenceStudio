using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Native;
using SentenceStudio.Services;

namespace SentenceStudio.Native.Android;

/// <summary>The tab shell trial's tabs (Captain, 2026-11-05, as on iOS); Sam's slot is an action, not a tab.</summary>
internal enum ShellTab { Study, Library, Settings }

/// <summary>The Library list rows, in list order; each opens its page above the list.</summary>
internal enum LibrarySection { Resources, Vocabulary, Skills, Pairs }

/// <summary>
/// The tab shell trial on Android, following iOS (7a09daaf, 566817d9, 0a8e1b16, 25fef602):
/// Study (the Dashboard), Library (a list of Resources, Vocabulary, Skills and Minimal Pairs,
/// with a quick-add grid), Settings (profile row, the settings, Sign out), and Sam's slot.
/// Each tab keeps its own page while another tab is shown. The tab bar shows on the tab
/// roots and their browse pages, and hides in editors, activities and the quiz.
/// </summary>
internal sealed partial class AndroidSliceShell
{
    private ShellTab _tab;
    /// <summary>Each tab's page above its root, kept while another tab is shown (as iOS keeps each tab's stack).</summary>
    private readonly MoreScreen[] _tabPages = new MoreScreen[3];
    /// <summary>The Library's Resources page (the Resources destination) stays open while another tab is shown.</summary>
    private bool _libraryResourcesOpen;
    /// <summary>Study's destination (Dashboard or Today's Plan) while another tab is shown.</summary>
    private NativeDestination _studyDestination = NativeDestination.Dashboard;
    private string? _libraryErrorKey;
    private bool _studyReloadPending;
    private (string Name, string NativeLanguage, string TargetLanguage)? _profileSummary;
    /// <summary>The latest profile row read; an older read that finishes later is ignored.</summary>
    private int _profileSummaryRequest;

    /// <summary>The page above the current tab's root; None at the root.</summary>
    private MoreScreen MorePage
    {
        get => _tabPages[(int)_tab];
        set => _tabPages[(int)_tab] = value;
    }

    private bool IsTabDestination =>
        Destination is NativeDestination.Dashboard or NativeDestination.TodaysPlan or NativeDestination.Resources;

    /// <summary>
    /// An editor is up: Save or Cancel decides how it closes, so the tab bar hides (iOS
    /// HasLibraryEditor). A browse page (a list, History, Profile, the import pages) keeps it.
    /// </summary>
    private bool HasPageEditor => IsResourceFormOpen || MorePage switch
    {
        MoreScreen.Skills => _skills?.IsFormOpen == true,
        MoreScreen.Vocabulary => _vocabulary?.IsFormOpen == true,
        MoreScreen.ImportContent => IsImportWordOpen,
        MoreScreen.YouTubeImport => IsYouTubeChannelOpen,
        _ => false,
    };

    private bool ShowsBottomTabs =>
        IsTabDestination && !HasPageEditor && (_preview || HasDashboardOwner(Auth.UserId));

    /// <summary>A save is running on the shown page: the tabs wait, as Back does.</summary>
    private bool IsTabNavigationBlocked => Busy.Value || _resources?.IsNavigationBlocked == true
        || IsMorePageBlocked(MorePage);

    /// <summary>
    /// A tab tap. The shown tab returns to its root (iOS pops the Library to its list);
    /// another tab shows its own root or kept page.
    /// </summary>
    private void SelectTab(ShellTab tab) => Publish(() =>
    {
        if (!ShowsBottomTabs || IsTabNavigationBlocked || !Enum.IsDefined(tab))
            return;
        _libraryErrorKey = null;
        _resourceMenu = ResourceMenu.None;
        _moreMenu = null;
        if (tab == _tab)
        {
            PopTabToRoot();
            return;
        }
        // Leaving: remember where this tab was.
        switch (_tab)
        {
            case ShellTab.Study:
                _studyDestination = Destination == NativeDestination.TodaysPlan
                    ? NativeDestination.TodaysPlan : NativeDestination.Dashboard;
                break;
            case ShellTab.Library:
                _libraryResourcesOpen = Destination == NativeDestination.Resources;
                // Resources, skills or imports may have changed the plan and Choose My Own.
                _studyReloadPending = true;
                break;
            case ShellTab.Settings:
                // A text-size drag still held was never released: not applied.
                _textSizePreview = null;
                break;
        }
        _tab = tab;
        Destination = NativeDestination.Dashboard;
        switch (tab)
        {
            case ShellTab.Study:
                Destination = _studyDestination;
                // Only after a Library visit: a plain switch keeps the plan as it is.
                if (_studyReloadPending)
                {
                    _studyReloadPending = false;
                    ReloadDashboardAfterResources();
                }
                break;
            case ShellTab.Library:
                EnterLibrary();
                break;
            case ShellTab.Settings:
                OpenSettingsTab();
                break;
        }
    });

    /// <summary>The Library shows its kept Resources page, or its list with the resource count read again.</summary>
    private void EnterLibrary()
    {
        var resources = EnsureResources();
        if (_libraryResourcesOpen && resources is not null && MorePage == MoreScreen.None)
            Destination = NativeDestination.Resources;
        else
            _libraryResourcesOpen = false;
        // The Resources row's count (and the page when it is shown) follows the device's list.
        resources?.OnBrowserAppeared();
    }

    /// <summary>Re-tapping the shown tab: its page closes (unless saving) and the root shows.</summary>
    private void PopTabToRoot()
    {
        if (IsMoreScreenOpen)
        {
            CloseMoreScreen();
            return;
        }
        if (_tab == ShellTab.Library && Destination == NativeDestination.Resources && !IsResourceFormOpen)
        {
            _libraryResourcesOpen = false;
            Destination = NativeDestination.Dashboard;
        }
    }

    /// <summary>
    /// System Back at the Library or Settings root returns to Study (Material navigation);
    /// at Study's root Back leaves the app.
    /// </summary>
    private bool HandleTabRootBack()
    {
        if (_tab == ShellTab.Study || Destination != NativeDestination.Dashboard || IsMoreScreenOpen
            || !ShowsBottomTabs)
            return false;
        SelectTab(ShellTab.Study);
        return true;
    }

    private bool HandlesTabRootBack => _tab != ShellTab.Study && Destination == NativeDestination.Dashboard
        && !IsMoreScreenOpen && ShowsBottomTabs;

    /// <summary>Sign-out or an owner change: the next owner starts on Study's root.</summary>
    private void ResetTabs()
    {
        _tab = ShellTab.Study;
        _libraryResourcesOpen = false;
        _studyDestination = NativeDestination.Dashboard;
        _libraryErrorKey = null;
        _studyReloadPending = false;
        _profileSummary = null;
        _profileSummaryRequest++;
        ResetSamSlot();
    }

    // ---- Library ----

    /// <summary>A Library row: opens its page above the list.</summary>
    private void OpenLibraryPage(LibrarySection section) => Publish(() =>
    {
        if (!IsLibraryListShown || Busy.Value || !Enum.IsDefined(section))
            return;
        _libraryErrorKey = null;
        switch (section)
        {
            case LibrarySection.Resources:
                if (EnsureResources() is not { } resources)
                {
                    _libraryErrorKey = "NativeResources_LoadError";
                    return;
                }
                Destination = NativeDestination.Resources;
                resources.OnBrowserAppeared();
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
    });

    private bool IsLibraryListShown => _tab == ShellTab.Library && Destination == NativeDestination.Dashboard
        && !IsMoreScreenOpen;

    /// <summary>One quick-add button: id suffix (native.library.quickadd.&lt;Id&gt;), drawable, label key, flow.</summary>
    private sealed record LibraryQuickAddItem(string Id, int Icon, string TitleKey, Action Open);

    private IReadOnlyList<LibraryQuickAddItem>? _libraryQuickAddItems;

    /// <summary>
    /// The quick-add grid under the Library list, in iOS order: a new resource, skill profile
    /// or minimal pair, then Import Content and YouTube import.
    /// </summary>
    private IReadOnlyList<LibraryQuickAddItem> LibraryQuickAddItems => _libraryQuickAddItems ??=
    [
        new("resource", Resource.Drawable.ic_note_add, "Resources_AddResource",
            () => OpenLibraryQuickAdd(OpenLibraryResourceCreate)),
        new("skill", Resource.Drawable.ic_track_changes, "Skills_AddSkillProfile",
            () => OpenLibraryQuickAdd(() => OpenSkills(create: true))),
        new("minimal-pair", Resource.Drawable.ic_graphic_eq, NativeMinimalPairsText.CreatePair,
            () => OpenLibraryQuickAdd(() => OpenLibraryPairs(create: true))),
        new("import-content", Resource.Drawable.ic_file_download, "Nav_ImportContent",
            () => OpenLibraryQuickAdd(OpenImportContent)),
        new("youtube-import", Resource.Drawable.ic_video_library, "Import_YouTubeImport",
            () => OpenLibraryQuickAdd(OpenYouTubeImport)),
    ];

    private void OpenLibraryQuickAdd(Action open)
    {
        if (!IsLibraryListShown || Busy.Value)
            return;
        Publish(() => _libraryErrorKey = null);
        open();
    }

    /// <summary>Quick add "Add Resource": the new-resource form; closing it returns to the Library list.</summary>
    private void OpenLibraryResourceCreate() => Publish(() =>
    {
        if (!IsLibraryListShown || Busy.Value)
            return;
        if (EnsureResources() is not { } resources || resources.IsFormOpen)
        {
            _libraryErrorKey ??= "NativeResources_LoadError";
            return;
        }
        _resourceFormFromDashboard = true;
        _resourceFormReturn = NativeDestination.Dashboard;
        Destination = NativeDestination.Resources;
        resources.OnBrowserAppeared();
        resources.OpenCreate();
    });

    /// <summary>
    /// The Resources row's count: the owner's resources once the list is read with no search
    /// or filter (no extra query, as on iOS). Null hides the count.
    /// </summary>
    private int? LibraryResourceCount => _resources?.ListState is
        { Phase: NativeResourceListPhase.Populated or NativeResourceListPhase.Empty,
          SearchText.Length: 0, MediaType: null, Language: null } state
            && !state.Items.IsDefault
        ? state.Items.Length : null;

    /// <summary>
    /// Source NavMenu Minimal Pairs: the pair list for the signed-in owner with no resource
    /// or skill (no timer or plan item), opened from the Library; Back returns to the list.
    /// With <paramref name="create"/>, its create page opens above it (quick add).
    /// </summary>
    private void OpenLibraryPairs(bool create) => Execute(async cancellationToken =>
    {
        if (_dashboard is null || _scope is null || _tab != ShellTab.Library)
            return;
        var selection = _dashboard.SelectNavigationEntry();
        var launch = selection is null ? null
            : await _scope.ServiceProvider.GetRequiredService<NativeActivityLaunchService>()
                .FromChooseOwnAsync(selection, NativeActivityCatalog.MinimalPairs, cancellationToken);
        if (launch is null)
        {
            global::Android.Util.Log.Warn("SentenceStudio.Native", selection is null
                ? "Minimal Pairs refused an absent owner." : "Minimal Pairs launch was refused.");
            Publish(() => _libraryErrorKey = NativeMinimalPairsText.Unavailable);
            return;
        }
        await StartActivityAsync(launch, cancellationToken, openMinimalPairsCreate: create);
    });

    // ---- Settings ----

    /// <summary>
    /// Every Settings visit reads the profile row and what Sam remembers again. The owner's
    /// settings stay open between visits (an unsaved quiz edit survives a tab switch) while
    /// their owner is current; a refused open or an owner change is tried again next visit.
    /// </summary>
    private void OpenSettingsTab()
    {
        if (_scope is null)
            return;
        _ = RefreshProfileSummaryAsync();
        OpenSamSettings();
        var owner = MoreOwner();
        if (_settings is { } current)
        {
            if (owner is not null && current.State is { IsOpen: true, OwnerBlocked: false })
                return;
            ReleaseSettings();
        }
        if (owner is null)
        {
            global::Android.Util.Log.Warn("SentenceStudio.Native",
                "Settings refused an absent or untrusted owner; the next visit tries again.");
            return;
        }
        var provider = _scope.ServiceProvider;
        OpenSettingsCoordinator(provider, provider.GetRequiredService<DeviceUserScopeProvider>(), owner);
    }

    /// <summary>The name and languages on the Settings profile row, read for the signed-in owner only.</summary>
    private async Task RefreshProfileSummaryAsync()
    {
        if (_scope is null || _preview || Auth is not { Phase: NativePhase.Ready, UserId: { Length: > 0 } owner }
            || !HasDashboardOwner(owner))
            return;
        var repository = _scope.ServiceProvider.GetRequiredService<UserProfileRepository>();
        var request = ++_profileSummaryRequest;
        try
        {
            var profile = await Task.Run(() => repository.GetByIdAsync(owner, _lifetime.Token));
            (string, string, string)? summary = profile is null ? null
                : (profile.Name ?? string.Empty, profile.NativeLanguage ?? string.Empty,
                    profile.TargetLanguage ?? string.Empty);
            Publish(() =>
            {
                // Only the newest read decides the row (a later Settings visit or Profile close).
                if (request == _profileSummaryRequest && Auth.UserId == owner && HasDashboardOwner(owner))
                    _profileSummary = summary;
            });
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("SentenceStudio.Native",
                $"The Settings profile row could not read the profile ({ex.GetType().Name}).");
        }
    }

    // ---- Sam's tab slot ----

    /// <summary>Where a tap on Sam's slot is when Sam is not offered yet.</summary>
    private enum SamSlotPhase
    {
        Idle,
        /// <summary>Reading availability; nothing shown for the first moment.</summary>
        Checking,
        /// <summary>Still reading: the checking dialog with Cancel is up.</summary>
        CheckingShown,
        /// <summary>The answer dialog is up (not offered, offline or not reachable).</summary>
        Answered,
    }

    private SamSlotPhase _samSlotPhase;
    /// <summary>The running check (also its identity): Cancel, sign-out and the limit cancel it.</summary>
    private CancellationTokenSource? _samSlotCheck;
    private string _samSlotText = string.Empty;

    private static readonly TimeSpan SamSlotCheckingDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan SamSlotCheckLimit = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Sam's slot shows for every signed-in owner (Captain's request for the trial, iOS
    /// 566817d9), also while Sam is not offered to them; the Debug preview has no Sam.
    /// </summary>
    private bool IsSamSlotShown => !_preview && HasDashboardOwner(Auth.UserId)
        && AndroidSamProjection.IsShellDestination(Destination);

    /// <summary>
    /// The slot opens the same Sam as every other entry. When Sam is not offered, the tap
    /// reads availability again and says "not available" only after the server answered so;
    /// offline and an unreachable server have their own text.
    /// </summary>
    private void OpenSamFromTab()
    {
        if (!IsSamSlotShown || SamState.IsOpen)
            return;
        if (SamState.IsVisible)
        {
            OpenSamPanel();
            return;
        }
        if (_samSlotPhase is SamSlotPhase.Checking or SamSlotPhase.CheckingShown
            || EnsureSam() is not { } sam)
            return;
        var check = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        check.CancelAfter(SamSlotCheckLimit);
        _samSlotCheck = check;
        _samSlotPhase = SamSlotPhase.Checking;
        _samSlotText = SamSlotText(NativeSamAvailabilityText.Checking);
        _ = ShowSamSlotCheckingAsync(check);
        _ = CheckSamForSlotAsync(sam, check);
    }

    private async Task ShowSamSlotCheckingAsync(CancellationTokenSource check)
    {
        try
        {
            await Task.Delay(SamSlotCheckingDelay, check.Token);
            Publish(() =>
            {
                if (ReferenceEquals(check, _samSlotCheck) && _samSlotPhase == SamSlotPhase.Checking)
                    _samSlotPhase = SamSlotPhase.CheckingShown;
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
            // The check finished and was disposed first.
        }
    }

    /// <summary>
    /// Only this read's answer decides the text: a read that failed or reached the limit
    /// before the server answered is "not reachable"; a cancelled check says nothing.
    /// </summary>
    private async Task CheckSamForSlotAsync(NativeSamPresenter sam, CancellationTokenSource check)
    {
        try
        {
            NativeSamAvailability answer;
            if (_scope?.ServiceProvider.GetService<IConnectivityService>() is { IsInternetAvailable: false })
                answer = NativeSamAvailability.Offline;
            else
            {
                answer = NativeSamAvailability.Unreachable;
                try
                {
                    answer = await sam.RefreshAvailabilityAsync(check.Token);
                }
                catch (OperationCanceledException) when (_disposed || !ReferenceEquals(check, _samSlotCheck)
                    || _lifetime.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    global::Android.Util.Log.Warn("SentenceStudio.Native",
                        $"Sam's slot could not read availability ({ex.GetType().Name}).");
                }
            }
            Publish(() =>
            {
                if (!ReferenceEquals(check, _samSlotCheck))
                    return;
                global::Android.Util.Log.Info("SentenceStudio.Native", $"Sam's slot read availability: {answer}.");
                if (SamState.IsVisible)
                {
                    _samSlotPhase = SamSlotPhase.Idle;
                    OpenSamPanel();
                }
                else
                {
                    _samSlotText = SamSlotText(NativeSamAvailabilityText.NoticeKey(answer)
                        ?? NativeSamAvailabilityText.Unreachable);
                    _samSlotPhase = SamSlotPhase.Answered;
                }
                _samSlotCheck = null;
                check.Dispose();
            });
        }
        catch (Exception ex)
        {
            // Not awaited: log it, and answer "not reachable" instead of a tap that seems to do nothing.
            global::Android.Util.Log.Error("SentenceStudio.Native", $"Sam's slot check failed: {ex}");
            Publish(() =>
            {
                if (!ReferenceEquals(check, _samSlotCheck))
                    return;
                _samSlotText = SamSlotText(NativeSamAvailabilityText.Unreachable);
                _samSlotPhase = SamSlotPhase.Answered;
                _samSlotCheck = null;
                check.Dispose();
            });
        }
    }

    /// <summary>Cancel on the checking dialog: the request stops and says nothing.</summary>
    private void CancelSamSlotCheck() => Publish(() =>
    {
        if (_samSlotPhase is SamSlotPhase.Checking or SamSlotPhase.CheckingShown)
            ResetSamSlot();
    });

    private void DismissSamSlotNotice() => Publish(() =>
    {
        if (_samSlotPhase == SamSlotPhase.Answered)
            _samSlotPhase = SamSlotPhase.Idle;
    });

    private void ResetSamSlot()
    {
        var check = _samSlotCheck;
        _samSlotCheck = null;
        _samSlotPhase = SamSlotPhase.Idle;
        if (check is null)
            return;
        check.Cancel();
        check.Dispose();
    }

    /// <summary>A slot text with Sam's persona name (the same name as his panel).</summary>
    private string SamSlotText(string key) =>
        string.Format(CultureInfo.CurrentCulture, Localize(key), SamPersona(SamState));
}
