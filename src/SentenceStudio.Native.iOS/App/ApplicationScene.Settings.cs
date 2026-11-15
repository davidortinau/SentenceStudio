using System.Globalization;
using CoreFoundation;
using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Features.Settings;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.App;

/// <summary>
/// The Settings tab as a two-column NavigationSplitView, as iOS Settings is (Captain
/// 2026-11-15): the sidebar lists the pages and stays in view without its toggle; the chosen
/// page fills the detail column. Beside the list Profile opens first. In a compact width (a slab
/// iPhone, the folded iPhone Duo) the split view collapses to the list, a row pushes its page,
/// and Back closes it.
/// </summary>
internal sealed partial class ApplicationScene
{
    private SwiftView? _settingsSplit;
    private SwiftView? _settingsSidebarSlot;
    private SwiftView? _settingsDetailSlot;
    private int _renderedSettingsSidebarVersion = -1;
    // The empty detail column showed beside the list: open the first page once navigation allows.
    private bool _settingsDefaultPending;
    // What the detail column shows: a page, or (null) the empty column.
    private NativeSettingsPage? _renderedSettingsPage;

    private SwiftView BuildSettingsSplit()
    {
        _settingsSidebarSlot = Views.VStack(_app.RenderSettingsSidebar(_style))
            .WithFlexibleWidth()
            .WithNavigationTitle(_app.Localize("Settings_Title"))
            .WithInlineNavigationTitle();
        _settingsSidebarSlot.Spacing = 0;
        _settingsSidebarSlot.WithSplitColumnWidth(320, min: 280, max: 400);
        _renderedSettingsSidebarVersion = _app.SettingsSidebarVersion;
        _renderedSettingsPage = null;
        // The detail column's title is the open page's (set when one opens).
        _settingsDetailSlot = Views.VStack(SettingsDetailPlaceholder())
            .WithFlexibleWidth();
        _settingsDetailSlot.Spacing = 0;
        _renderedSettingsVersion = _app.SettingsProjectionVersion;
        _settingsSplit = Views.NavigationSplitView(_settingsSidebarSlot, _settingsDetailSlot,
            onCompactColumnChanged: OnSettingsCompactColumnChanged);
        // The pages sit beside the list, which stays in view: without its toggle the system
        // keeps the sidebar shown (as the Library's sidebar with nothing open).
        _settingsSplit.WithSplitViewStyle(SwiftSplitViewStyle.Balanced);
        _settingsSplit.SidebarToggleHidden = true;
        _settingsSplit.SplitColumnVisibility = SwiftSplitViewVisibility.All;
        _settingsSplit.PreferredCompactColumn = SwiftSplitViewColumn.Sidebar;
        return _settingsSplit.WithAccessibilityIdentifier("native.settings.split");
    }

    /// <summary>
    /// The detail column with no page open. It is left out in a compact width (where the list
    /// shows, and a Back's pop must not bring a page back), so its appearing means the column is
    /// beside the list: the first page opens there.
    /// </summary>
    private SwiftView SettingsDetailPlaceholder()
    {
        var appearance = _style.Appearance;
        return Views.VStack()
            .WithFlexibleWidth()
            .WithFlexibleHeight()
            .WithBackgroundColor(appearance.BodyBackground.Light, appearance.BodyBackground.Dark)
            .WithHiddenInCompactWidth()
            .WithAccessibilityIdentifier("native.settings.detail.placeholder")
            .OnAppear(() =>
            {
                _settingsDefaultPending = true;
                OpenDefaultSettingsPageLater();
            });
    }

    /// <summary>Opens the first page after this update (not while the scene is changing it).</summary>
    private void OpenDefaultSettingsPageLater() => DispatchQueue.MainQueue.DispatchAsync(() =>
    {
        if (!_disposed && _settingsDefaultPending && _app.SelectedTab == NativeTabs.Settings
            && _app.SettingsPage is null)
            _app.SelectDefaultSettingsPage();
    });

    /// <summary>
    /// In the collapsed stack the person went back to the list: the page closes, as Back on a
    /// stack closes it. While navigation is blocked the page stays in front.
    /// </summary>
    private void OnSettingsCompactColumnChanged(SwiftSplitViewColumn column)
    {
        if (column != SwiftSplitViewColumn.Sidebar || _settingsSplit is null)
            return;
        // A compact width: the list is the page, so nothing opens by itself.
        _settingsDefaultPending = false;
        if (!_app.CloseSettingsPage())
            _settingsSplit.PreferredCompactColumn = SwiftSplitViewColumn.Detail;
    }

    private void UpdateSettings(
        bool themeChanged, bool modeChanged, bool fontScaleChanged)
    {
        if (_settingsDetailSlot is null || _settingsSidebarSlot is null || _settingsSplit is null)
            return;
        // The Settings projection (with Sam's memory) is read only while Settings is in
        // front; an appearance change re-renders it anyway, and the next visit catches up.
        var visible = _app.SelectedTab == NativeTabs.Settings;
        var version = _app.SettingsProjectionVersion;
        var page = _app.SettingsPage;
        var restyled = themeChanged || modeChanged;
        var sidebarVersion = _app.SettingsSidebarVersion;
        if (restyled || visible && _renderedSettingsSidebarVersion != sidebarVersion)
        {
            ReplaceForm(_settingsSidebarSlot, _app.RenderSettingsSidebar(_style), restyled);
            _renderedSettingsSidebarVersion = sidebarVersion;
        }
        if (page is not null)
            _settingsDefaultPending = false;
        else if (_settingsDefaultPending && visible && !_app.NavigationBlocked)
            // The first page was refused while navigation was blocked: ask again.
            OpenDefaultSettingsPageLater();
        if (page != _renderedSettingsPage)
        {
            if (_renderedSettingsPage == NativeSettingsPage.Profile)
                DropProfileReferences();
            // Profile is drawn by UpdateProfile once it has opened.
            Replace(_settingsDetailSlot, page switch
            {
                null => SettingsDetailPlaceholder(),
                NativeSettingsPage.Profile => SettingsView.Loading(_style, _app.Localize),
                _ => _app.RenderSettings(_style),
            });
            if (page is { } open)
                _settingsDetailSlot.WithNavigationTitle(_app.Localize(SettingsView.TitleKey(open)))
                    .WithInlineNavigationTitle();
            else
                _settingsDetailSlot.WithNavigationTitle(string.Empty);
            _settingsSplit.PreferredCompactColumn = page is null
                ? SwiftSplitViewColumn.Sidebar : SwiftSplitViewColumn.Detail;
            _renderedSettingsPage = page;
            _renderedSettingsVersion = version;
        }
        else if (page is null)
        {
            if (restyled)
                Replace(_settingsDetailSlot, SettingsDetailPlaceholder());
        }
        else if (page != NativeSettingsPage.Profile
            && (restyled || visible && _renderedSettingsVersion != version))
        {
            ReplaceForm(_settingsDetailSlot, _app.RenderSettings(_style), restyled);
            _renderedSettingsVersion = version;
        }
        else if (fontScaleChanged
            && Find(_settingsDetailSlot, "native.settings.scale.label") is { } label)
            label.Text = string.Format(CultureInfo.CurrentCulture,
                _app.Localize("Settings_TextSizeLabel"),
                (int)Math.Round(_app.Appearance.Selection.FontScale * 100));
    }

    /// <summary>
    /// A mounted Form keeps its place (and scroll position): its Sections are swapped. A theme
    /// or mode change mounts the new Form.
    /// </summary>
    private static void ReplaceForm(SwiftView slot, SwiftView next, bool restyled)
    {
        if (!restyled && slot.Children.Count == 1
            && slot.Children[0] is { Kind: "form" } currentForm && next.Kind == "form")
            ReplaceContainerChildren(currentForm, next);
        else
            Replace(slot, next);
    }

    private void DropSettingsReferences()
    {
        _settingsSplit = null;
        _settingsSidebarSlot = null;
        _settingsDetailSlot = null;
        _renderedSettingsSidebarVersion = -1;
        _settingsDefaultPending = false;
        _renderedSettingsVersion = -1;
        _renderedSettingsPage = null;
        DropProfileReferences();
    }
}
