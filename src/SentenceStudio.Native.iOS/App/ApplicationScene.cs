using System.Globalization;
using System.Diagnostics;
using CoreFoundation;
using Microsoft.SwiftUI;
using SentenceStudio.Contracts.Theme;
using SentenceStudio.Native.iOS.Features.Dashboard;
using SentenceStudio.Native.iOS.Features.LearningResources;
using SentenceStudio.Native.iOS.Features.Quiz;
using SentenceStudio.Native.iOS.Features.Skills;
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
    private SwiftView? _resourcesStack;
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
            _style = new SourceUiStyle(_app.Appearance, _style.ViewportWidth);
            _style.Page(_root);
            _root.PreferredColorScheme = PreferredSchemeFor(selectedAppearance.Mode);
            if (fontScaleChanged || themeChanged)
                SourceUiStyle.RefreshTypography(
                    _root, _app.Appearance, _style.ViewportWidth);
        }

        var target = _app.Destination switch
        {
            NativeDestination.Onboarding => RootKind.Onboarding,
            NativeDestination.Dashboard or NativeDestination.VocabularyQuiz
                or NativeDestination.QuizSummary => RootKind.Tabs,
            _ => RootKind.SignIn,
        };
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
            Replace(_onboardingSlot!, _app.RenderOnboarding(_style));
            return;
        }

        if (_tabs is not null && _tabs.SelectedIndex != _app.SelectedTab)
            _tabs.SelectedIndex = _app.SelectedTab;
        if (_tabs is not null)
            _tabs.IsEnabled = !_app.NavigationBlocked;
        if (_starterOverlay is not null)
        {
            _starterOverlay.Text = _app.StarterBusyTitle;
            _starterOverlay.IsPresented = _app.IsStarterBusy;
        }
        if (_dashboardStack is not null)
            _dashboardStack.CanNavigateBack =
                !_app.NavigationBlocked || _app.CanNavigateBackDuringQuizAudio;
        _dashboardSlot?.WithNavigationTitle(_app.DashboardTitle);
        if (_resourcesStack is not null)
            _resourcesStack.CanNavigateBack = !_app.NavigationBlocked;
        if (_app.SelectedTab == 0 || _app.IsQuizOpen || themeChanged || modeChanged)
            Replace(_dashboardSlot!, _app.RenderDashboardContent(_style));
        if (_app.SelectedTab == 1 || themeChanged || modeChanged)
            UpdateResources(themeChanged || modeChanged);
        if (_resourcesSearch is { } search)
        {
            var query = _app.ResourcesSearchText;
            if (!string.Equals(search.Text, query, StringComparison.Ordinal))
                search.Text = query;
        }

        UpdateChooseOwn();
        UpdateQuiz();
        UpdateResourceForm(themeChanged || modeChanged);
        UpdateSettings(themeChanged, modeChanged, fontScaleChanged);
        UpdateProfile(themeChanged || modeChanged || fontScaleChanged);
        UpdateActivityLog(themeChanged || modeChanged || fontScaleChanged);
        UpdateSkills(themeChanged || modeChanged || fontScaleChanged);
        UpdateVocabulary(themeChanged || modeChanged || fontScaleChanged);
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
                if (_app.IsProfileOpen)
                {
                    OnProfileRoutesChanged(routes.Count);
                    return;
                }
                if (_app.IsActivityLogOpen)
                {
                    OnActivityLogRoutesChanged(routes.Count);
                    return;
                }
                if (_app.IsVocabularyOpen && _app.VocabularyOriginTab == 0)
                {
                    OnVocabularyRoutesChanged(routes.Count);
                    return;
                }
                if (_app.IsSkillsOpen && _app.SkillsOriginTab == 0)
                {
                    OnSkillsRoutesChanged(routes.Count);
                    return;
                }
                if (routes.Count != 0)
                    return;
                if (_app.IsQuizOpen)
                {
                    _quizSlot = null;
                    _quizDetailsSlot = null;
                    _quizSheet = null;
                    _quizPhotoCover = null;
                    _app.OnQuizPopped();
                }
                else if (_app.IsSettingsOpen)
                {
                    _settingsSlot = null;
                    _renderedSettingsVersion = -1;
                    _app.OnSettingsPopped();
                }
            });

        _resourcesSlot = Views.VStack(_app.RenderResources(_style))
            .WithNavigationTitle(localize("Resources_Title"))
            .WithInlineNavigationTitle();
        _resourcesSearch = Find(_resourcesSlot, "native.resources.search");
        _resourcesStack = Views.NavigationStack(_resourcesSlot,
            routes =>
            {
                if (_app.IsVocabularyOpen && _app.VocabularyOriginTab == 1)
                {
                    OnVocabularyRoutesChanged(routes.Count);
                    return;
                }
                if (_app.IsSkillsOpen && _app.SkillsOriginTab == 1)
                {
                    OnSkillsRoutesChanged(routes.Count);
                    return;
                }
                if (routes.Count == 0)
                {
                    if (_app.IsResourcesFormOpen)
                        _app.OnResourcesFormPopped();
                    DropFormReferences();
                    if (_app.IsResourcesFormOpen)
                        UpdateResourceForm();
                }
            });
        _tabs = Views.TabView(_app.SelectedTab, _app.SelectTab,
            Views.Tab(localize("Dashboard_Title"), "house",
                    _dashboardStack),
            Views.Tab(localize("Resources_Title"), "books.vertical",
                    _resourcesStack));
        _starterOverlay = Views.StarterBlockingOverlay(
                _tabs, _app.StarterBusyTitle)
            .WithAccessibilityIdentifier("native.starter.blocking-overlay");
        _starterOverlay.IsPresented = _app.IsStarterBusy;
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
        var stack = _app.SkillsOriginTab == 0
            ? _dashboardStack : _resourcesStack;
        if (stack is null)
            return;
        if (_skillsSlot is null && stack.NavigationPath.Count == 0)
        {
            _skillsSlot = Views.VStack(_app.RenderSkillsList(_style))
                .WithNavigationTitle(_app.Localize("Skills_Title"))
                .WithInlineNavigationTitle()
                .WithTabBarHidden();
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
            stack.Push("skill-form",
                _skillDeleteConfirmation.WithTabBarHidden());
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
        }
        if (_skillDeleteConfirmation is not null)
        {
            _skillDeleteConfirmation.IsPresented = _app.IsSkillDeletePresented;
            _skillDeleteConfirmation.DeletePending = _app.IsSkillDeletePending;
        }
    }

    private void UpdateResources(bool replaceAll = false)
    {
        var next = _app.RenderResources(_style);
        var current = _resourcesSlot!.Children.Single();
        if (replaceAll || current.Kind != "toolbar" || next.Kind != "toolbar"
            || current.Children.Count == 0 || next.Children.Count == 0
            || !SameToolbarActions(current, next)
            || current.Children[0].Kind != next.Children[0].Kind
            || current.Children[0].Children.Count == 0
            || next.Children[0].Children.Count == 0
            || !SameBrowserControls(current.Children[0], next.Children[0]))
        {
            Replace(_resourcesSlot, next);
            _resourcesSearch = Find(_resourcesSlot, "native.resources.search");
            return;
        }

        var page = current.Children[0];
        var nextPage = next.Children[0];
        var content = page.Kind == "scroll" ? page.Children[0] : page;
        var nextContent = nextPage.Kind == "scroll"
            ? nextPage.Children[0] : nextPage;
        var search = content.Children[0];
        var nextSearch = nextContent.Children[0];
        if (search.Kind != "textField" || nextSearch.Kind != "textField"
            || search.AccessibilityIdentifier != "native.resources.search"
            || nextSearch.AccessibilityIdentifier != "native.resources.search")
        {
            Replace(_resourcesSlot, next);
            _resourcesSearch = Find(_resourcesSlot, "native.resources.search");
            return;
        }

        if (page.IsEnabled != nextPage.IsEnabled)
            page.IsEnabled = nextPage.IsEnabled;
        if (search.IsEnabled != nextSearch.IsEnabled)
            search.IsEnabled = nextSearch.IsEnabled;
        if (!string.Equals(search.Text, _app.ResourcesSearchText, StringComparison.Ordinal))
            search.Text = _app.ResourcesSearchText;
        if (page.Spacing != nextPage.Spacing)
            page.Spacing = nextPage.Spacing;
        if (content.Spacing != nextContent.Spacing)
            content.Spacing = nextContent.Spacing;
        var controls = content.Children[1];
        var nextControls = nextContent.Children[1];
        if (controls.IsEnabled != nextControls.IsEnabled)
            controls.IsEnabled = nextControls.IsEnabled;
        var filters = controls.Children[0];
        var nextFilters = nextControls.Children[0];
        if (filters.IsEnabled != nextFilters.IsEnabled)
            filters.IsEnabled = nextFilters.IsEnabled;
        for (var index = 0; index < filters.Children.Count; index++)
        {
            var menu = filters.Children[index];
            var nextMenu = nextFilters.Children[index];
            if (menu.IsEnabled != nextMenu.IsEnabled)
                menu.IsEnabled = nextMenu.IsEnabled;
            if (menu.Text != nextMenu.Text)
            {
                menu.Text = nextMenu.Text;
                menu.Children[0].Children[0].Text = nextMenu.Text;
                for (var optionIndex = 1; optionIndex < menu.Children.Count;
                    optionIndex++)
                {
                    var option = menu.Children[optionIndex];
                    var selected = option.Text == nextMenu.Text;
                    option.WithAccessibilitySelected(selected);
                    option.Children[0].Children[1].WithOpacity(selected ? 1 : 0);
                }
            }
        }
        var modes = controls.Children[1];
        var nextModes = nextControls.Children[1];
        if (modes.IsEnabled != nextModes.IsEnabled)
            modes.IsEnabled = nextModes.IsEnabled;
        var modeChanged = modes.SelectedIndex != nextModes.SelectedIndex;
        if (modeChanged)
            modes.SelectedIndex = nextModes.SelectedIndex;
        for (var index = 0; index < modes.Children.Count; index++)
        {
            var button = modes.Children[index];
            var nextButton = nextModes.Children[index];
            if (button.IsEnabled != nextButton.IsEnabled)
                button.IsEnabled = nextButton.IsEnabled;
            if (button.Text != nextButton.Text)
            {
                button.Text = nextButton.Text;
                button.WithAccessibilityLabel(nextButton.Text);
            }
            if (modeChanged)
                _style.UpdateResourceViewModeButton(button,
                    index == modes.SelectedIndex,
                    index == 0 ? SourceButtonGroupPosition.First
                        : SourceButtonGroupPosition.Last);
        }
        for (var index = content.Children.Count - 1; index > 1; index--)
        {
            var old = content.Children[index];
            content.RemoveAt(index);
            old.Dispose();
        }
        while (nextContent.Children.Count > 2)
        {
            var child = nextContent.Children[2];
            nextContent.RemoveAt(2);
            content.Add(child);
        }
        for (var actionIndex = 1; actionIndex < current.Children.Count;
            actionIndex++)
        {
            var action = current.Children[actionIndex];
            var nextAction = next.Children[actionIndex];
            if (action.IsEnabled != nextAction.IsEnabled)
                action.IsEnabled = nextAction.IsEnabled;
            if (action.Text != nextAction.Text)
                action.Text = nextAction.Text;
            for (var index = 0; index < action.Children.Count; index++)
            {
                var child = action.Children[index];
                var nextChild = nextAction.Children[index];
                if (child.IsEnabled != nextChild.IsEnabled)
                    child.IsEnabled = nextChild.IsEnabled;
                if (child.Text != nextChild.Text)
                    child.Text = nextChild.Text;
            }
        }
        next.Dispose();
        _resourcesSearch = search;
    }

    private static bool SameBrowserControls(SwiftView currentPage, SwiftView nextPage)
    {
        if (currentPage.Kind != "scroll" || nextPage.Kind != "scroll"
            || currentPage.Children.Count != 1 || nextPage.Children.Count != 1)
            return false;
        var current = currentPage.Children[0];
        var next = nextPage.Children[0];
        if (current.Kind != "vstack" || next.Kind != "vstack"
            || current.Children.Count < 2 || next.Children.Count < 2)
            return false;
        var controls = current.Children[1];
        var nextControls = next.Children[1];
        if (controls.Kind != "hstack" || nextControls.Kind != "hstack"
            || controls.AccessibilityIdentifier != "native.resources.browser.controls"
            || controls.AccessibilityIdentifier != nextControls.AccessibilityIdentifier
            || controls.Children.Count != 2 || nextControls.Children.Count != 2)
            return false;
        var filters = controls.Children[0];
        var nextFilters = nextControls.Children[0];
        if (filters.Kind != "hstack" || nextFilters.Kind != "hstack"
            || filters.AccessibilityIdentifier != "native.resources.filters"
            || filters.AccessibilityIdentifier != nextFilters.AccessibilityIdentifier
            || filters.Children.Count != 2 || nextFilters.Children.Count != 2)
            return false;
        for (var index = 0; index < filters.Children.Count; index++)
        {
            var menu = filters.Children[index];
            var nextMenu = nextFilters.Children[index];
            if (menu.Kind != "menu" || nextMenu.Kind != "menu"
                || menu.AccessibilityIdentifier != nextMenu.AccessibilityIdentifier
                || menu.Children.Count != nextMenu.Children.Count
                || menu.Children.Count < 2
                || menu.Children[0].Kind != "hstack"
                || nextMenu.Children[0].Kind != "hstack"
                || menu.Children[0].Children.Count != 2
                || nextMenu.Children[0].Children.Count != 2
                || menu.Children[0].Children[0].Kind != "text"
                || nextMenu.Children[0].Children[0].Kind != "text"
                || menu.Children[0].Children[1].Kind != "image"
                || nextMenu.Children[0].Children[1].Kind != "image")
                return false;
            for (var option = 1; option < menu.Children.Count; option++)
                if (menu.Children[option].Kind != "button"
                    || nextMenu.Children[option].Kind != "button"
                    || menu.Children[option].Children.Count != 1
                    || nextMenu.Children[option].Children.Count != 1
                    || menu.Children[option].Children[0].Kind != "hstack"
                    || nextMenu.Children[option].Children[0].Kind != "hstack"
                    || menu.Children[option].Children[0].Children.Count != 2
                    || nextMenu.Children[option].Children[0].Children.Count != 2
                    || menu.Children[option].Children[0].Children[0].Kind != "text"
                    || nextMenu.Children[option].Children[0].Children[0].Kind != "text"
                    || menu.Children[option].Children[0].Children[1].Kind != "image"
                    || nextMenu.Children[option].Children[0].Children[1].Kind != "image"
                    || menu.Children[option].Text != nextMenu.Children[option].Text)
                    return false;
        }
        var modes = controls.Children[1];
        var nextModes = nextControls.Children[1];
        if (modes.Kind != "buttonGroup" || nextModes.Kind != "buttonGroup"
            || modes.AccessibilityIdentifier != "native.resources.view.mode"
            || modes.AccessibilityIdentifier != nextModes.AccessibilityIdentifier
            || modes.Children.Count != 2 || nextModes.Children.Count != 2)
            return false;
        for (var index = 0; index < modes.Children.Count; index++)
            if (modes.Children[index].Kind != "button"
                || nextModes.Children[index].Kind != "button"
                || modes.Children[index].AccessibilityIdentifier
                    != nextModes.Children[index].AccessibilityIdentifier)
                return false;
        return true;
    }

    private static bool SameToolbarActions(SwiftView current, SwiftView next)
    {
        if (current.Children.Count != next.Children.Count)
            return false;
        for (var index = 1; index < current.Children.Count; index++)
        {
            var action = current.Children[index];
            var nextAction = next.Children[index];
            if (action.Kind != nextAction.Kind
                || action.AccessibilityIdentifier
                    != nextAction.AccessibilityIdentifier
                || action.Children.Count != nextAction.Children.Count)
                return false;
            for (var child = 0; child < action.Children.Count; child++)
                if (action.Children[child].Kind != nextAction.Children[child].Kind
                    || action.Children[child].AccessibilityIdentifier
                        != nextAction.Children[child].AccessibilityIdentifier)
                    return false;
        }
        return true;
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
                _dashboardStack.Push("quiz", _quizSheet);
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
        if (_dashboardStack is null || !_app.IsSettingsOpen)
            return;
        if (_settingsSlot is null && _dashboardStack.NavigationPath.Count == 0)
        {
            var version = _app.SettingsProjectionVersion;
            _settingsSlot = Views.VStack(_app.RenderSettings(_style))
                .WithNavigationTitle(_app.Localize("Settings_Title"))
                .WithInlineNavigationTitle()
                .WithTabBarHidden();
            _renderedSettingsVersion = version;
            _dashboardStack.Push("settings", _settingsSlot);
            return;
        }
        if (_settingsSlot is null)
            return;
        if (themeChanged || modeChanged
            || _renderedSettingsVersion != _app.SettingsProjectionVersion)
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
        if (_resourcesStack is null)
            return;
        if (_app.IsResourcesFormOpen)
        {
            if (_resourcesStack.NavigationPath.Count == 0)
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
                _resourcesStack.Push("resource-form",
                    _conflictAlert.WithTabBarHidden());
            }
            else if (_formSlot is not null
                && (_renderedFormVersion != _app.ResourceFormProjectionVersion
                    || themeChanged))
            {
                Replace(_formSlot, ResourceFormContent());
                _renderedFormVersion = _app.ResourceFormProjectionVersion;
            }

            _formSlot?.WithNavigationTitle(_app.ResourceFormTitle);
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
            _formSlot is not null, _resourcesStack.NavigationPath))
        {
            _resourcesStack.Pop();
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
            (_style.ViewportWidth < 768) != (width < 768);
        _style.ViewportWidth = width;
        _dynamicType = dynamicType;
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
            resourceGrid.GridColumns = SourceUiLayout.ResourceColumns(width);
        if (Find(_skillsSlot, "native.skills.grid") is { } skillsGrid)
            skillsGrid.GridColumns = SkillsView.Columns(width);
        if (Find(_vocabularySlot, "native.vocabulary.cards") is { } wordsGrid)
            wordsGrid.GridColumns = _app.VocabularyListMode && width >= 768
                ? 1 : SourceUiLayout.ResourceColumns(width);
        if (Find(_vocabularySlot, "native.vocabulary.wide.filters") is { } filtersGrid)
            filtersGrid.GridColumns = width < 1024 ? 2 : 4;
        if (Find(_dashboardSlot, "native.dashboard.chooseown.grid") is { } activityGrid)
            activityGrid.GridColumns = SourceUiLayout.ActivityColumns(width);
        if (crossedDesktopBreakpoint)
        {
            if (_app.IsVocabularyFilterPresented)
                _app.CancelVocabularyFilters();
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
            _tabs = null;
            _starterOverlay = null;
            _dashboardSlot = null;
            _dashboardStack = null;
            _resourcesSlot = null;
            _resourcesSearch = null;
            _resourcesStack = null;
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

    private enum RootKind { None, SignIn, Onboarding, Tabs }
}
