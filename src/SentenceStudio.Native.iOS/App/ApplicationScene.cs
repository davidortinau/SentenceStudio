using System.Globalization;
using System.Diagnostics;
using CoreFoundation;
using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Features.Browse;
using SentenceStudio.Contracts.Theme;
using SentenceStudio.Native.iOS.Features.Dashboard;
using SentenceStudio.Native.iOS.Features.LearningResources;
using SentenceStudio.Native.iOS.Features.Quiz;
using SentenceStudio.Native.iOS.Features.Sam;
using SentenceStudio.Native.iOS.Features.Skills;
using SentenceStudio.Native.iOS.Features.Vocabulary;
using SentenceStudio.Native.iOS.Platform;
using SentenceStudio.Native.iOS.Styling;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.App;

internal sealed partial class ApplicationScene : IDisposable
{
    private readonly ApplicationController _app;
    private SourceUiStyle _style;
    private readonly SwiftView _root;
    private SwiftView? _signInSlot;
    private SwiftView? _onboardingSlot;
    private SwiftView? _tabs;
    private SwiftView? _starterOverlay;
    private SwiftView? _overlayCancel;
    private bool _renderedOverlayCancellable;
    private SwiftView? _dashboardSlot;
    private SwiftView? _dashboardStack;
    private SwiftView? _selectorSlot;
    private SwiftView? _selectorSearch;
    private SwiftView? _selectorSheet;
    private SwiftView? _quizSlot;
    private SwiftView? _quizDetailsSlot;
    private SwiftView? _quizSheet;
    private SwiftView? _quizPhotoCover;
    private SwiftView? _quizProgress;
    private CancellationTokenSource? _quizProgressCancellation;
    private SwiftView? _resourcesSlot;
    private SwiftView? _resourcesSearch;
    // The Resources page's phone filter sheet and what the mounted page and sheet show.
    private SwiftView? _resourcesFilterSlot;
    private SwiftView? _resourcesSheet;
    private string? _renderedResourcesArrangement;
    private object? _renderedResourcesKey;
    private string? _renderedResourceSheetKey;
    private SwiftView? _settingsStack;
    private SwiftView? _samTab;
    private SwiftView? _samUnavailableNotice;
    private SwiftView? _skillsSlot;
    private SwiftView? _skillFormSlot;
    private SwiftView? _skillDeleteConfirmation;
    private long _renderedSkillsListVersion = -1;
    private long _renderedSkillFormVersion = -1;
    private SwiftView? _settingsSlot;
    private int _renderedSettingsVersion = -1;
    private SwiftView? _formSlot;
    private SwiftView? _quickSuggestions;
    private SwiftView? _quickSuggestionSlot;
    private SwiftView? _fileImporter;
    private SwiftView? _deleteConfirmation;
    private SwiftView? _conflictAlert;
    private long _renderedFormVersion = -1;
    private bool _renderedResourceFormHidesTabBar = true;
    private long _renderedQuickSuggestionsVersion = -1;
    private RootKind _kind;
    private bool _conflictAcknowledged;
    private bool _updating;
    private bool _updateRequested;
    private bool _disposed;
    private SwiftDynamicTypeSize _dynamicType = SwiftDynamicTypeSize.Large;

    internal ApplicationScene(ApplicationController app)
    {
        _app = app;
        _style = new SourceUiStyle(app.Appearance, 402);
        _root = _style.Page(Views.VStack(Views.Text("SentenceStudio"))
                .WithFlexibleWidth())
            .WithPreferredColorScheme(PreferredSchemeFor(app.Appearance.Selection.Mode))
            .WithAccessibilityIdentifier("native.app.root")
            .OnViewportChanged(ViewportChanged)
            .OnAppear(app.Start);
        _app.Changed += Update;
        _app.ResourceFormTitleChanged += UpdateResourceTitle;
        Update();
    }

    internal SwiftView Root => _root;

    internal static SwiftColorScheme PreferredSchemeFor(ThemeMode mode) => mode switch
    {
        ThemeMode.System => SwiftColorScheme.System,
        ThemeMode.Light => SwiftColorScheme.Light,
        ThemeMode.Dark => SwiftColorScheme.Dark,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unrecognized theme mode.")
    };

    private void Update()
    {
        if (_disposed)
            return;
        if (_updating)
        {
            _updateRequested = true;
            return;
        }
        _updating = true;
        try
        {
            do
            {
                _updateRequested = false;
                UpdateCore();
            }
            while (_updateRequested);
        }
        finally
        {
            _updating = false;
        }
    }

    private void UpdateCore()
    {
        var previousAppearance = _style.Appearance.Selection;
        var selectedAppearance = _app.Appearance.Selection;
        var themeChanged = previousAppearance.ThemeId != selectedAppearance.ThemeId;
        var modeChanged = previousAppearance.Mode != selectedAppearance.Mode;
        var fontScaleChanged =
            previousAppearance.FontScale != selectedAppearance.FontScale;
        if (themeChanged || modeChanged || fontScaleChanged)
        {
            _style = new SourceUiStyle(_app.Appearance, _style.ViewportWidth)
            {
                DynamicType = _dynamicType
            };
            _style.Page(_root);
            _root.PreferredColorScheme = PreferredSchemeFor(selectedAppearance.Mode);
            if (fontScaleChanged || themeChanged)
                SourceUiStyle.RefreshTypography(
                    _root, _app.Appearance, _style.ViewportWidth);
        }

        var target = _app.IsLaunching ? RootKind.Launch : _app.Destination switch
        {
            NativeDestination.Onboarding => RootKind.Onboarding,
            NativeDestination.Dashboard or NativeDestination.VocabularyQuiz
                or NativeDestination.QuizSummary
                or NativeDestination.Activity => RootKind.Tabs,
            _ => RootKind.SignIn,
        };
        if (target == RootKind.Launch)
        {
            if (_kind != target)
                Install(LaunchSurface(), RootKind.Launch);
            return;
        }
        if (_kind != target)
        {
            _kind = target;
            switch (target)
            {
                case RootKind.SignIn:
                    Install(Views.VStack(_app.RenderSignIn(_style)), RootKind.SignIn);
                    _signInSlot = _root.Children.Single();
                    return;
                case RootKind.Onboarding:
                    Install(Views.VStack(_app.RenderOnboarding(_style)), RootKind.Onboarding);
                    _onboardingSlot = _root.Children.Single();
                    return;
                case RootKind.Tabs:
                    InstallTabs();
                    break;
            }
        }

        if (target == RootKind.SignIn)
        {
            Replace(_signInSlot!, _app.RenderSignIn(_style));
            return;
        }
        if (target == RootKind.Onboarding)
        {
            // Suggested names can arrive while the learner types: keep the field focused.
            var focusedId = FindFocusedInput(_onboardingSlot!)?.AccessibilityIdentifier;
            Replace(_onboardingSlot!, _app.RenderOnboarding(_style));
            if (!string.IsNullOrEmpty(focusedId)
                && Find(_onboardingSlot, focusedId) is { IsEnabled: true } focusedInput)
                focusedInput.FocusRequested = true;
            return;
        }

        if (_tabs is not null && _tabs.SelectedIndex != _app.SelectedTab)
            _tabs.SelectedIndex = _app.SelectedTab;
        if (_tabs is not null)
            _tabs.IsEnabled = !_app.NavigationBlocked;
        if (_starterOverlay is not null)
        {
            _starterOverlay.Text = _app.BlockingOverlayTitle;
            _starterOverlay.IsPresented = _app.IsBlockingOverlayPresented;
        }
        if (_overlayCancel is not null && _renderedOverlayCancellable != _app.IsBlockingOverlayCancellable)
        {
            _renderedOverlayCancellable = _app.IsBlockingOverlayCancellable;
            _overlayCancel.WithHidden(!_renderedOverlayCancellable);
        }
        if (_dashboardStack is not null)
            _dashboardStack.CanNavigateBack =
                !_app.NavigationBlocked || _app.CanNavigateBackDuringQuizAudio;
        _dashboardSlot?.WithNavigationTitle(_app.DashboardTitle);
        if (_libraryStack is not null)
            _libraryStack.CanNavigateBack = !_app.NavigationBlocked;
        if (_settingsStack is not null)
            _settingsStack.CanNavigateBack = !_app.NavigationBlocked;
        if (_app.SelectedTab == NativeTabs.Study || _app.IsQuizOpen || themeChanged || modeChanged)
            Replace(_dashboardSlot!, _app.RenderDashboardContent(_style));
        UpdateLibrary(themeChanged || modeChanged);
        if (_resourcesSearch is { } search)
        {
            var query = _app.ResourcesSearchText;
            if (!string.Equals(search.Text, query, StringComparison.Ordinal))
                search.Text = query;
        }

        UpdateChooseOwn();
        UpdateQuiz();
        UpdateActivity();
        UpdateResourceForm(themeChanged || modeChanged);
        UpdateSettings(themeChanged, modeChanged, fontScaleChanged);
        UpdateProfile(themeChanged || modeChanged || fontScaleChanged);
        UpdateActivityLog(themeChanged || modeChanged || fontScaleChanged);
        UpdateSkills(themeChanged || modeChanged || fontScaleChanged);
        UpdateVocabulary(themeChanged || modeChanged || fontScaleChanged);
        if (themeChanged || modeChanged || fontScaleChanged)
            _renderedSamPanel = null;
        UpdateSam();
    }

    private void InstallTabs()
    {
        var localize = _app.Localize;
        _dashboardSlot = Views.VStack(_app.RenderDashboardContent(_style))
            .WithNavigationTitle(_app.DashboardTitle)
            .WithInlineNavigationTitle();
        _selectorSlot = Views.VStack(
            Views.Text(localize("Common_Loading")));
        _selectorSearch = Views.Searchable(_selectorSlot,
            localize("Resources_SearchPlaceholder"),
            _app.ChooseOwnSearch, _app.FilterChooseOwn);
        _selectorSheet = Views.Sheet(_dashboardSlot,
            Views.NavigationStack(_selectorSearch),
            _app.DismissChooseOwnSelector);
        _dashboardStack = Views.NavigationStack(_selectorSheet,
            routes =>
            {
                if (_app.IsActivityLogOpen)
                {
                    OnActivityLogRoutesChanged(routes.Count);
                    return;
                }
                if (OnActivityRoutesChanged(routes.Count) || routes.Count != 0)
                    return;
                if (_app.IsQuizOpen)
                {
                    _quizSlot = null;
                    _quizDetailsSlot = null;
                    _quizSheet = null;
                    _quizPhotoCover = null;
                    _app.OnQuizPopped();
                }
            });

        var library = BuildLibraryStack();
        _settingsSlot = Views.VStack(_app.RenderSettings(_style))
            .WithNavigationTitle(localize("Settings_Title"))
            .WithInlineNavigationTitle();
        _renderedSettingsVersion = _app.SettingsProjectionVersion;
        _settingsStack = Views.NavigationStack(_settingsSlot,
            routes =>
            {
                if (_app.IsProfileOpen)
                    OnProfileRoutesChanged(routes.Count);
            });
        // iOS tab shell trial (Captain, 2026-11-05): Study, Library, Settings, and Sam in the
        // trailing search-role slot with his own symbol and name. Selecting Sam's slot opens
        // Sam and keeps the current tab. A sidebar on iPad.
        _samTab = Views.Tab(SamView.Title(_app.SamState, localize),
                "bubble.left.and.bubble.right.fill",
                Views.VStack().WithAccessibilityIdentifier("native.tab.sam.content"))
            .WithTabRole(SwiftTabRole.Search)
            .WithTabHidden(!_app.IsSamSlotShown)
            .WithAccessibilityIdentifier("native.tab.sam");
        _tabs = Views.TabView(_app.SelectedTab, _app.SelectTab,
                Views.Tab(localize("Nav_Study"), "graduationcap", _dashboardStack)
                    .WithAccessibilityIdentifier("native.tab.study"),
                Views.Tab(localize("Nav_Library"), "books.vertical", library)
                    .WithAccessibilityIdentifier("native.tab.library"),
                Views.Tab(localize("Settings_Title"), "gearshape", _settingsStack)
                    .WithAccessibilityIdentifier("native.tab.settings"),
                _samTab)
            // Tabs only (Captain 2026-11-12): the tabs stay in the tab bar (at the side on the
            // iPhone Duo); the Library's split view has its own sidebar of its places.
            .WithTabViewStyle(SwiftTabViewStyle.TabBarOnly);
        // iOS 26 on iPhone: the tab bar shrinks while a page scrolls down (Sam's slot with
        // it) and returns on scroll up or a tap.
        if (NativeDeviceIdiom.IsPhone)
            _tabs.WithTabBarMinimizeBehavior(SwiftTabBarMinimizeBehavior.OnScrollDown);
        // The slot's answer when it cannot open Sam: not offered, offline or not reachable
        // (its title). It presents once, with the answer; checking is the busy overlay.
        _samUnavailableNotice = Views.Alert(WrapWithSam(_tabs),
                _app.SamSlotNoticeText, string.Empty,
                Views.Button(localize("Common_Close"), _app.DismissSamUnavailableNotice)
                    .WithAccessibilityIdentifier("native.tab.sam.unavailable.close"))
            .WithAccessibilityIdentifier("native.tab.sam.unavailable");
        // Cancel shows only for Sam's check (starter creation cannot be cancelled).
        _renderedOverlayCancellable = _app.IsBlockingOverlayCancellable;
        _overlayCancel = Views.Button(localize("Common_Cancel"), _app.CancelSamSlotCheck)
            .WithAccessibilityIdentifier("native.tab.sam.checking.cancel")
            .WithHidden(!_renderedOverlayCancellable);
        _starterOverlay = Views.StarterBlockingOverlay(
                _samUnavailableNotice, _app.BlockingOverlayTitle, _overlayCancel)
            .WithAccessibilityIdentifier("native.starter.blocking-overlay");
        _starterOverlay.IsPresented = _app.IsBlockingOverlayPresented;
        Install(_starterOverlay, RootKind.Tabs);
    }

    private void OnSkillsRoutesChanged(int count)
    {
        if (count == 0 && _skillsSlot is not null)
        {
            DropSkillsReferences();
            _app.OnSkillsPopped();
        }
        else if (count == 1 && _skillFormSlot is not null)
        {
            DropSkillFormReferences();
            _app.OnSkillFormPopped();
        }
    }

    private void UpdateSkills(bool appearanceChanged)
    {
        if (!_app.IsSkillsOpen)
            return;
        // The Library list's Skills row pushes the list; the form sits above it.
        var stack = _libraryStack;
        if (stack is null)
            return;
        if (_skillsSlot is null && stack.NavigationPath.Count == 0)
        {
            // A browse page: the tab bar stays.
            _skillsSlot = Views.VStack(_app.RenderSkillsList(_style))
                .WithNavigationTitle(_app.Localize("Skills_Title"))
                .WithInlineNavigationTitle();
            _renderedSkillsListVersion = _app.SkillsListVersion;
            stack.Push("skills", _skillsSlot);
        }
        if (_skillsSlot is null)
            return;
        if (appearanceChanged
            || _renderedSkillsListVersion != _app.SkillsListVersion)
        {
            Replace(_skillsSlot, _app.RenderSkillsList(_style));
            _renderedSkillsListVersion = _app.SkillsListVersion;
        }
        // "Archived" under the title while the archive shows.
        _skillsSlot.WithNavigationSubtitle(_app.SkillsSubtitle);

        if (_app.IsSkillFormOpen && _skillFormSlot is null
            && stack.NavigationPath.Count == 1)
        {
            _skillFormSlot = Views.VStack(_app.RenderSkillForm(_style))
                .WithNavigationTitle(_app.SkillFormTitle)
                .WithInlineNavigationTitle();
            _renderedSkillFormVersion = _app.SkillFormVersion;
            _skillDeleteConfirmation = Views.ResourceDeleteConfirmation(
                    _skillFormSlot,
                    _app.Localize("SkillEdit_ConfirmDelete"),
                    _app.Localize("SkillEdit_DeleteMessage"),
                    _app.Localize("Common_Close"),
                    _app.Localize("Common_Cancel"),
                    _app.Localize("Common_Delete"),
                    _app.CancelSkillDelete,
                    _app.ConfirmSkillDelete)
                .WithTransparentDeleteBackdrop()
                .WithAccessibilityIdentifier("native.skills.edit.delete");
            stack.Push("skill-form", WithSamToolbar(stack, "skill-form",
                _skillDeleteConfirmation.WithTabBarHidden()));
        }
        else if (ApplicationFormRoutes.ShouldPopSkillForm(
            _app.IsSkillFormOpen, _skillFormSlot is not null,
            stack.CanNavigateBack, stack.NavigationPath))
        {
            stack.Pop();
            DropSkillFormReferences();
        }
        else if (_app.IsSkillFormOpen && _skillFormSlot is not null
            && (appearanceChanged
                || _renderedSkillFormVersion != _app.SkillFormVersion))
        {
            Replace(_skillFormSlot, _app.RenderSkillForm(_style));
            _renderedSkillFormVersion = _app.SkillFormVersion;
            // Another row chosen beside the list can turn Add into Edit.
            _skillFormSlot.WithNavigationTitle(_app.SkillFormTitle);
        }
        if (_skillDeleteConfirmation is not null)
        {
            _skillDeleteConfirmation.IsPresented = _app.IsSkillDeletePresented;
            _skillDeleteConfirmation.DeletePending = _app.IsSkillDeletePending;
        }
    }

    /// <summary>
    /// Draws the Resources page again. A page with the same outline is updated in place
    /// (<see cref="BrowserPageUpdate"/>), so the search field keeps focus and Korean composition
    /// while typing; another layout, sort or grid/list choice mounts a new page (its results start
    /// at the top), with focus handed to its search field.
    /// </summary>
    private void UpdateResources(bool replaceAll = false)
    {
        if (_resourcesSlot is null)
            return;
        _renderedResourcesKey = _app.ResourcesRenderKey;
        _resourcesSlot.WithNavigationSubtitle(_app.ResourcesSubtitle);
        var next = _app.RenderResources(_style);
        var current = _resourcesSlot.Children.Single();
        var oldSearch = Find(current, BrowserPageUpdate.Resources.SearchId);
        var arrangement = _app.ResourcesArrangement;
        replaceAll |= !string.Equals(arrangement, _renderedResourcesArrangement, StringComparison.Ordinal);
        _renderedResourcesArrangement = arrangement;
        if (replaceAll || oldSearch is null
            || !BrowserPageUpdate.Resources.TryUpdateInPlace(current, next))
        {
            var focused = oldSearch?.FocusRequested == true;
            Replace(_resourcesSlot, next);
            _resourcesSearch = Find(_resourcesSlot, BrowserPageUpdate.Resources.SearchId);
            if (focused && _resourcesSearch?.IsEnabled == true)
                _resourcesSearch.FocusRequested = true;
            return;
        }
        if (!string.Equals(oldSearch.Text, _app.ResourcesSearchText, StringComparison.Ordinal))
            oldSearch.Text = _app.ResourcesSearchText;
        _resourcesSearch = oldSearch;
        next.Dispose();
    }

    /// <summary>The phone filter sheet over the Resources page: drawn again when what it shows changes.</summary>
    private void UpdateResourceFilters(bool appearanceChanged)
    {
        if (_resourcesSheet is null || _resourcesFilterSlot is null)
            return;
        var key = _app.ResourceFilterSheetKey;
        if (appearanceChanged || !string.Equals(key, _renderedResourceSheetKey, StringComparison.Ordinal))
        {
            Replace(_resourcesFilterSlot, _app.RenderResourceFilters(_style));
            _renderedResourceSheetKey = key;
        }
        _resourcesSheet.IsPresented = _app.IsResourceFilterPresented;
    }

    private void DropResourcesReferences()
    {
        _resourcesSlot = null;
        _resourcesSearch = null;
        _resourcesFilterSlot = null;
        _resourcesSheet = null;
        _renderedResourcesArrangement = null;
        _renderedResourcesKey = null;
        _renderedResourceSheetKey = null;
    }

    private void UpdateChooseOwn()
    {
        if (_selectorSheet is null || _selectorSlot is null)
            return;
        if (_app.IsChooseOwnSelectorPresented)
        {
            Replace(_selectorSlot, _app.RenderChooseOwnSelector(_style));
            if (_selectorSearch!.Text != _app.ChooseOwnSearch)
                _selectorSearch.Text = _app.ChooseOwnSearch;
        }
        _selectorSheet.IsPresented = _app.IsChooseOwnSelectorPresented;
    }

    private void UpdateQuiz()
    {
        if (_dashboardStack is null)
            return;
        if (_app.IsQuizOpen && !_app.IsQuizReturnPending)
        {
            StopQuizProgress();
            if (_dashboardStack.NavigationPath.Count == 0)
            {
                _quizSlot = Views.VStack(
                        _app.RenderQuizContent(_style, out _quizPhotoCover))
                    .WithNavigationTitle(_app.Localize(NativeText.Quiz))
                    .WithInlineNavigationTitle();
                _quizDetailsSlot = Views.VStack(
                    Views.Text(_app.Localize("Common_Loading")));
                _quizSheet = Views.Sheet(_quizSlot, _quizDetailsSlot,
                    _app.DismissQuizDetails)
                    .WithSheetDetentFraction(0.7)
                    .WithTabBarHidden();
                _dashboardStack.Push("quiz", WithSamToolbar(_dashboardStack, "quiz", _quizSheet));
            }
            else if (_quizSlot is not null)
                Replace(_quizSlot,
                    _app.RenderQuizContent(_style, out _quizPhotoCover));
            if (_quizSheet is not null)
            {
                if (_app.IsQuizDetailsPresented)
                    Replace(_quizDetailsSlot!, _app.RenderQuizDetails(_style));
                _quizSheet.IsPresented = _app.IsQuizDetailsPresented;
            }
            if (_quizPhotoCover is not null)
                _quizPhotoCover.IsPresented = _app.IsQuizPhotoPresented;
            StartQuizProgressIfGraded();
        }
        else if (!_app.IsQuizOpen && _quizSlot is not null
            && _dashboardStack.NavigationPath.Count > 0)
        {
            StopQuizProgress();
            _dashboardStack.Pop();
            _quizSlot = null;
            _quizDetailsSlot = null;
            _quizSheet = null;
            _quizPhotoCover = null;
        }
    }

    private void UpdateSettings(
        bool themeChanged, bool modeChanged, bool fontScaleChanged)
    {
        if (_settingsSlot is null)
            return;
        // The Settings projection (with Sam's memory) is read only while Settings is in
        // front; an appearance change re-renders it anyway, and the next visit catches up.
        var visible = _app.SelectedTab == NativeTabs.Settings;
        if (themeChanged || modeChanged
            || visible && _renderedSettingsVersion != _app.SettingsProjectionVersion)
        {
            var version = _app.SettingsProjectionVersion;
            var next = _app.RenderSettings(_style);
            if (!themeChanged && !modeChanged
                && _settingsSlot.Children.Count == 1
                && _settingsSlot.Children[0] is { Kind: "scroll" } scroll
                && Find(scroll, "native.settings.scroll.content") is { } currentContent
                && next.Kind == "scroll" && next.Children.Count == 1
                && next.Children[0].AccessibilityIdentifier
                    == "native.settings.scroll.content")
            {
                var content = next.Children[0];
                next.RemoveAt(0);
                ReplaceContainerChildren(currentContent, content);
                next.Dispose();
            }
            else
                Replace(_settingsSlot, next);
            _renderedSettingsVersion = version;
        }
        else if (fontScaleChanged
            && Find(_settingsSlot, "native.settings.scale.label") is { } label)
            label.Text = string.Format(CultureInfo.CurrentCulture,
                _app.Localize("Settings_TextSizeLabel"),
                (int)Math.Round(_app.Appearance.Selection.FontScale * 100));
    }

    private void StartQuizProgressIfGraded()
    {
        if (_app.IsBusy)
            return;
        var state = _app.QuizViewState;
        if (state is not {
            Phase: NativePhase.Feedback, RequiresCorrection: false,
            TurnId: not null, FeedbackStartedAt: not null,
            AutoAdvanceDurationMs: > 0 })
            return;
        var progress = Find(_quizSlot, "native.quiz.auto-advance.progress");
        if (progress is null)
            return;
        _quizProgress = progress;
        var duration = state.AutoAdvanceDurationMs;
        var startOffset = Math.Clamp(
            (DateTimeOffset.UtcNow - state.FeedbackStartedAt.Value)
                .TotalMilliseconds, 0, duration);
        progress.Value = startOffset;
        var cancellation = new CancellationTokenSource();
        _quizProgressCancellation = cancellation;
        _ = DriveQuizProgressAsync(progress, startOffset, duration,
            cancellation.Token);
    }

    private async Task DriveQuizProgressAsync(SwiftView progress,
        double startOffset, int durationMs, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(33));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var elapsed = Math.Min(durationMs,
                    startOffset + clock.Elapsed.TotalMilliseconds);
                DispatchQueue.MainQueue.DispatchAsync(() =>
                {
                    if (!_disposed && !cancellationToken.IsCancellationRequested
                        && ReferenceEquals(_quizProgress, progress))
                        progress.Value = elapsed;
                });
                if (elapsed >= durationMs)
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void StopQuizProgress()
    {
        _quizProgress = null;
        _quizProgressCancellation?.Cancel();
        _quizProgressCancellation?.Dispose();
        _quizProgressCancellation = null;
    }

    private void UpdateResourceForm(bool themeChanged = false)
    {
        if (_libraryStack is null)
            return;
        if (_app.IsResourcesFormOpen)
        {
            if (_formSlot is null
                && !_libraryStack.NavigationPath.Contains(ApplicationFormRoutes.ResourceForm))
            {
                _formSlot = Views.VStack(ResourceFormContent())
                    .WithNavigationTitle(_app.ResourceFormTitle)
                    .WithInlineNavigationTitle();
                _renderedFormVersion = _app.ResourceFormProjectionVersion;
                _fileImporter = Views.FileImporter(_formSlot,
                    _app.OnResourceFileImported,
                    _app.OnResourceFileImportError,
                    _app.OnFileImporterDismissed);
                _deleteConfirmation = Views.ResourceDeleteConfirmation(_fileImporter,
                    _app.Localize("ResourceEdit_ConfirmDelete"),
                    _app.Localize("ResourceEdit_DeleteMessage"),
                    _app.Localize("Common_Close"),
                    _app.Localize("Common_Cancel"),
                    _app.Localize("Common_Delete"),
                    _app.CancelResourceDelete,
                    _app.ConfirmResourceDelete)
                    .WithTransparentDeleteBackdrop()
                    .WithAccessibilityIdentifier("native.resources.edit.delete");
                _conflictAlert = Views.Alert(_deleteConfirmation,
                    _app.Localize("NativeResources_ConflictTitle"),
                    _app.Localize("NativeResources_Conflict"),
                    Views.Button(_app.Localize("NativeResources_ReloadDiscard"),
                            _app.ReloadResourceDraft)
                        .WithAccessibilityIdentifier(NativeAccess.ResourceReload),
                    Views.Button(_app.Localize("NativeResources_KeepEditing"),
                            () =>
                            {
                                _conflictAcknowledged = true;
                                if (_conflictAlert is not null)
                                    _conflictAlert.IsPresented = false;
                            })
                        .WithAccessibilityIdentifier(
                            "native.resources.edit.conflict.keep"));
                _libraryStack.Push("resource-form", WithSamToolbar(_libraryStack, "resource-form",
                    _conflictAlert.WithTabBarHidden(),
                    // A smart resource read in the Library keeps the tab bar and Sam's slot.
                    tabBarShown: () => !_renderedResourceFormHidesTabBar));
            }
            else if (_formSlot is not null
                && (_renderedFormVersion != _app.ResourceFormProjectionVersion
                    || themeChanged))
            {
                Replace(_formSlot, ResourceFormContent());
                _renderedFormVersion = _app.ResourceFormProjectionVersion;
            }

            _formSlot?.WithNavigationTitle(_app.ResourceFormTitle);
            // An editor hides the tab bar (Save or Cancel decides how you leave); a smart
            // resource, which you only read, keeps it, except when Study opened it: its Back
            // returns to Study, as the source detail returns to the plan.
            var hideTabBar = !_app.IsResourceFormReadOnly || _app.IsResourceFormFromStudy;
            if (_conflictAlert is not null && _renderedResourceFormHidesTabBar != hideTabBar)
            {
                _conflictAlert.WithTabBarHidden(hideTabBar);
                _renderedResourceFormHidesTabBar = hideTabBar;
            }
            RefreshQuickSuggestions();
            if (_fileImporter is not null)
                _fileImporter.IsPresented = _app.IsFileImporterPresented;
            if (_deleteConfirmation is not null)
            {
                _deleteConfirmation.IsPresented =
                    _app.IsDeleteConfirmationPresented;
                _deleteConfirmation.DeletePending =
                    _app.IsResourceDeletePending;
            }
            if (_conflictAlert is not null)
            {
                if (!_app.IsConflictPresented)
                {
                    _conflictAcknowledged = false;
                    _conflictAlert.IsPresented = false;
                }
                else if (!_conflictAcknowledged)
                {
                    _conflictAlert.IsPresented = true;
                    _conflictAcknowledged = true;
                }
            }
        }
        else if (ApplicationFormRoutes.ShouldPopResourceForm(
            _formSlot is not null, _libraryStack.NavigationPath))
        {
            _libraryStack.Pop();
            DropFormReferences();
        }
    }

    private SwiftView ResourceFormContent()
    {
        var content = _app.RenderResourceForm(_style);
        _quickSuggestions = null;
        _quickSuggestionSlot = null;
        _renderedQuickSuggestionsVersion = -1;
        var fieldId = _app.QuickAddFieldAutomationId;
        if (!_app.HasQuickAddField || string.IsNullOrWhiteSpace(fieldId)
            || Find(content, fieldId) is not { Kind: "textField" })
            return content;
        _quickSuggestionSlot = _app.RenderResourceQuickAddSuggestions(_style);
        _renderedQuickSuggestionsVersion =
            _app.ResourceQuickAddSuggestionsVersion;
        _quickSuggestions = Views.QuickAddSuggestions(
                content, fieldId, _quickSuggestionSlot,
                _app.DismissResourceQuickAddSuggestions)
            .WithAccessibilityIdentifier("native.resources.edit.quick.overlay");
        _quickSuggestions.IsPresented = _app.IsQuickAddSuggestionsPresented;
        return _quickSuggestions;
    }

    private void RefreshQuickSuggestions()
    {
        if (_quickSuggestions is not { } popup
            || _quickSuggestionSlot is not { } suggestions)
            return;
        if (_renderedQuickSuggestionsVersion
                == _app.ResourceQuickAddSuggestionsVersion
            && popup.IsPresented == _app.IsQuickAddSuggestionsPresented)
            return;

        DispatchQueue.MainQueue.DispatchAsync(() =>
        {
            if (_disposed || !_app.IsResourcesFormOpen
                || !ReferenceEquals(_quickSuggestions, popup)
                || !ReferenceEquals(_quickSuggestionSlot, suggestions))
                return;
            var version = _app.ResourceQuickAddSuggestionsVersion;
            if (_renderedQuickSuggestionsVersion != version)
            {
                ReplaceContainerChildren(suggestions,
                    _app.RenderResourceQuickAddSuggestions(_style));
                _renderedQuickSuggestionsVersion = version;
            }
            var presented = _app.IsQuickAddSuggestionsPresented;
            if (popup.IsPresented != presented)
                popup.IsPresented = presented;
        });
    }

    private static void ReplaceContainerChildren(SwiftView slot, SwiftView next)
    {
        while (slot.Children.Count > 0)
        {
            var previous = slot.Children[0];
            slot.RemoveAt(0);
            previous.Dispose();
        }
        while (next.Children.Count > 0)
        {
            var child = next.Children[0];
            next.RemoveAt(0);
            slot.Add(child);
        }
        if (slot.Spacing != next.Spacing)
            slot.Spacing = next.Spacing;
        next.Dispose();
    }

    private void UpdateResourceTitle(string title) =>
        _formSlot?.WithNavigationTitle(title);

    private void ViewportChanged(double width, SwiftDynamicTypeSize dynamicType)
    {
        if (_disposed || !double.IsFinite(width) || width <= 0
            || Math.Abs(_style.ViewportWidth - width) < 0.5
                && _dynamicType == dynamicType)
            return;

        var crossedDesktopBreakpoint =
            (_style.ViewportWidth < SourceUiLayout.DesktopWidth)
                != (width < SourceUiLayout.DesktopWidth);
        // The Vocabulary page's reported width belonged to the old window; it reports again.
        if (Math.Abs(_style.ViewportWidth - width) >= 0.5)
        {
            _app.ForgetVocabularyPageWidth();
            _app.ForgetResourcesPageWidth();
        }
        _style.ViewportWidth = width;
        _dynamicType = dynamicType;
        _style.DynamicType = dynamicType;
        SourceUiStyle.RefreshTypography(_root, _app.Appearance, width);
        Find(_dashboardSlot, "native.dashboard.scroll.content")?.WithPadding(
            SourceUiLayout.PageGutter(width));
        Find(_resourcesSlot, "native.resources.scroll.content")?.WithPadding(
            SourceUiLayout.PageGutter(width));
        Find(_settingsSlot, "native.settings.scroll.content")?.WithPadding(
            SourceUiLayout.PageGutter(width));
        Find(_activityLogSlot, "native.activity-log.scroll.content")?.WithPadding(
            SourceUiLayout.PageGutter(width));
        Find(_skillsSlot, "native.skills.scroll.content")?.WithPadding(
            SourceUiLayout.PageGutter(width));
        Find(_skillFormSlot, "native.skills.form.scroll.content")?.WithPadding(
            SourceUiLayout.PageGutter(width));
        Find(_vocabularySlot, "native.vocabulary.scroll.content")?.WithPadding(
            SourceUiLayout.PageGutter(width));
        Find(_vocabularyWordSlot, "native.vocabulary.edit.scroll.content")?.WithPadding(
            SourceUiLayout.PageGutter(width));
        if (Find(_resourcesSlot, "native.resources.grid") is { } resourceGrid)
            resourceGrid.GridColumns = LearningResourcesView.GridColumns(
                _app.ResourcesPageWidth ?? width, _style.TextScale, _app.ResourcesFilterPanelShown);
        if (Find(_resourcesSlot, "native.resources.filters.panel") is { } resourcePanel)
            resourcePanel.WithFixedWidth(BrowserPage.FilterColumnWidth(
                _app.ResourcesPageWidth ?? width, _style.TextScale));
        if (Find(_vocabularySlot, "native.vocabulary.cards") is { } wordsGrid)
            wordsGrid.GridColumns = VocabularyListView.CardColumns(
                _app.VocabularyPageWidth ?? width, _style.TextScale,
                _app.VocabularyListMode, _app.VocabularyFilterPanelShown,
                VocabularyListView.IsWide(_app.VocabularyPageWidth ?? width));
        if (Find(_vocabularySlot, "native.vocabulary.filters.panel") is { } filterPanel)
            filterPanel.WithFixedWidth(VocabularyListView.FilterColumnWidth(
                _app.VocabularyPageWidth ?? width, _style.TextScale));
        if (Find(_dashboardSlot, "native.dashboard.chooseown.grid") is { } activityGrid)
            activityGrid.GridColumns = SourceUiLayout.ActivityColumns(width, _style.TextScale);
        if (crossedDesktopBreakpoint)
        {
            if (_app.IsVocabularyFilterPresented)
                _app.CancelVocabularyFilters();
            if (_app.IsResourceFilterPresented)
                _app.CancelResourceFilters();
            if (_dashboardSlot is not null)
                Replace(_dashboardSlot, _app.RenderDashboardContent(_style));
            if (_resourcesSlot is not null)
            {
                var wasSearching = _resourcesSearch?.FocusRequested == true;
                UpdateResources(replaceAll: true);
                if (wasSearching && _resourcesSearch?.IsEnabled == true)
                    _resourcesSearch.FocusRequested = true;
            }
            if (_formSlot is not null && _app.IsResourcesFormOpen)
            {
                var focusedId = FindFocusedInput(_formSlot)?.AccessibilityIdentifier;
                Replace(_formSlot, ResourceFormContent());
                _renderedFormVersion = _app.ResourceFormProjectionVersion;
                if (!string.IsNullOrEmpty(focusedId)
                    && Find(_formSlot, focusedId) is { } input)
                    input.FocusRequested = true;
            }
            if (_skillsSlot is not null && _app.IsSkillsOpen)
            {
                Replace(_skillsSlot, _app.RenderSkillsList(_style));
                _renderedSkillsListVersion = _app.SkillsListVersion;
            }
            if (_activityLogSlot is not null && _app.IsActivityLogOpen)
            {
                Replace(_activityLogSlot, _app.RenderActivityLog(_style));
                _renderedActivityLogVersion = _app.ActivityLogViewVersion;
            }
            if (_skillFormSlot is not null && _app.IsSkillFormOpen)
            {
                var focusedId = FindFocusedInput(_skillFormSlot)?.AccessibilityIdentifier;
                Replace(_skillFormSlot, _app.RenderSkillForm(_style));
                _renderedSkillFormVersion = _app.SkillFormVersion;
                if (!string.IsNullOrEmpty(focusedId)
                    && Find(_skillFormSlot, focusedId) is { } input)
                    input.FocusRequested = true;
            }
            if (_vocabularySlot is not null && _app.IsVocabularyOpen)
                UpdateVocabularyList(replaceAll: true);
            if (_vocabularyWordSlot is not null && _app.IsVocabularyWordOpen)
                UpdateVocabularyWord();
            // Sam moves between the sheet and the side panel at this breakpoint.
            if (_updating)
                _updateRequested = true;
            else
                UpdateSam();
        }
    }

    private static SwiftView? FindFocusedInput(SwiftView root)
    {
        if (root.Kind is ("textField" or "textEditor" or "secureField")
            && root.FocusRequested)
            return root;
        foreach (var child in root.Children)
            if (FindFocusedInput(child) is { } focused)
                return focused;
        return null;
    }

    private static SwiftView? Find(SwiftView? root, string identifier)
    {
        if (root is null)
            return null;
        if (root.AccessibilityIdentifier == identifier)
            return root;
        foreach (var child in root.Children)
            if (Find(child, identifier) is { } found)
                return found;
        return null;
    }

    private void DropFormReferences()
    {
        _formSlot = null;
        _quickSuggestions = null;
        _quickSuggestionSlot = null;
        _fileImporter = null;
        _deleteConfirmation = null;
        _conflictAlert = null;
        _conflictAcknowledged = false;
        _renderedResourceFormHidesTabBar = true;
        _renderedFormVersion = -1;
        _renderedQuickSuggestionsVersion = -1;
    }

    private void DropSkillFormReferences()
    {
        _skillFormSlot = null;
        _skillDeleteConfirmation = null;
        _renderedSkillFormVersion = -1;
    }

    private void DropSkillsReferences()
    {
        DropSkillFormReferences();
        _skillsSlot = null;
        _renderedSkillsListVersion = -1;
    }

    private void Install(SwiftView view, RootKind kind)
    {
        if (_root.Children.Count > 0)
        {
            var old = _root.Children[0];
            _root.RemoveAt(0);
            old.Dispose();
        }
        _root.Add(view);
        _kind = kind;
        _signInSlot = null;
        _onboardingSlot = null;
        if (kind != RootKind.Tabs)
        {
            DropSamReferences();
            _tabs = null;
            _starterOverlay = null;
            _overlayCancel = null;
            _dashboardSlot = null;
            _dashboardStack = null;
            DropResourcesReferences();
            _libraryStack = null;
            _librarySplit = null;
            _libraryRoot = null;
            _libraryListSlot = null;
            _renderedLibraryList = null;
            DropPairsReferences();
            _settingsStack = null;
            _samTab = null;
            _renderedSamTabPersona = null;
            _samUnavailableNotice = null;
            _settingsSlot = null;
            _renderedSettingsVersion = -1;
            DropProfileReferences();
            DropActivityLogReferences();
            _selectorSlot = null;
            _selectorSearch = null;
            _selectorSheet = null;
            _quizSlot = null;
            _quizDetailsSlot = null;
            _quizSheet = null;
            _quizPhotoCover = null;
            DropFormReferences();
            DropSkillsReferences();
            DropVocabularyReferences();
        }
    }

    private static void Replace(SwiftView slot, SwiftView value)
    {
        if (slot.Children.Count != 1)
            throw new InvalidOperationException(
                "A SwiftUI scene slot must have exactly one mounted view.");
        var previous = slot.Children[0];
        slot.RemoveAt(0);
        previous.Dispose();
        slot.Add(value);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        StopQuizProgress();
        _app.Changed -= Update;
        _app.ResourceFormTitleChanged -= UpdateResourceTitle;
        _root.Dispose();
    }

    private enum RootKind { None, Launch, SignIn, Onboarding, Tabs }

    /// <summary>
    /// The LaunchBackground color asset that Info.plist's UILaunchScreen names (the bridge
    /// takes hex colors only); a contract test keeps the two equal, so the handover from the
    /// system launch screen never flashes.
    /// </summary>
    internal const string LaunchBackgroundHex = "#0C0B10";

    /// <summary>
    /// The launch look (Info.plist UILaunchScreen: LaunchBackground and LaunchLogo) while a
    /// remembered session is restored, so the launch screen hands over without a Sign In
    /// frame. Laid out under the safe areas so the logo stays where the launch screen drew it.
    /// </summary>
    private static SwiftView LaunchSurface() =>
        Views.VStack(Views.AssetImage("LaunchLogo"))
            .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
            .WithFlexibleHeight()
            .WithBackgroundColor(LaunchBackgroundHex)
            .WithIgnoresSafeArea()
            .WithAccessibilityIdentifier("native.launch");
}
