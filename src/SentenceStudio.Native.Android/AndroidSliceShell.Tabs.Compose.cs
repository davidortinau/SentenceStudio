using System.Globalization;
using AndroidX.Compose;
using SentenceStudio.Native.Features.Settings;
using ComposeScrollState = AndroidX.Compose.ScrollState;

namespace SentenceStudio.Native.Android;

/// <summary>
/// Compose for the tab shell trial: the Material 3 navigation bar (Study, Library,
/// Settings and Sam's slot), the Library list with its quick-add grid, the Settings
/// profile row and Sign out, and Sam's slot dialogs.
/// </summary>
internal sealed partial class AndroidSliceShell
{
    private ComposableNode NavigationTabs(AndroidVisualPalette palette)
    {
        var bar = new NavigationBar
        {
            Modifier = Modifier.FillMaxWidth().TestTag("native.tabs"),
        };
        var enabled = !IsTabNavigationBlocked;
        // Study keeps the Dashboard tab's automation id.
        bar.Add(TabItem(ShellTab.Study, NativeAccess.DashboardTab, Localize("Nav_Study"),
            Resource.Drawable.ic_school, enabled, palette));
        bar.Add(TabItem(ShellTab.Library, "native.tab.library", Localize("Nav_Library"),
            Resource.Drawable.ic_library_books, enabled, palette));
        bar.Add(TabItem(ShellTab.Settings, "native.tab.settings", Localize("Settings_Title"),
            null, enabled, palette));
        if (IsSamSlotShown)
            bar.Add(SamTabSlot(enabled, palette));
        return bar;
    }

    /// <summary>A tab: the drawable, or the core Settings icon when <paramref name="drawable"/> is null.</summary>
    private ComposableNode TabItem(ShellTab tab, string tag, string label, int? drawable,
        bool enabled, AndroidVisualPalette palette)
    {
        var selected = _tab == tab;
        var tint = selected ? palette.Primary : palette.SecondaryText;
        var icon = drawable is { } id
            ? new Icon(id, null) { Modifier = Modifier.Size(24), Tint = tint }
            : new Icon(Icons.Default.Settings, null) { Modifier = Modifier.Size(24), Tint = tint };
        return new NavigationBarItem(selected: selected, onClick: () => SelectTab(tab),
            enabled: enabled, alwaysShowLabel: true)
        {
            Modifier = Modifier.TestTag(tag).Semantics(semantics =>
            {
                semantics.ContentDescription(label);
                semantics.Selected(selected);
            }),
            Icon = icon,
            Label = new Text(label),
        };
    }

    /// <summary>Sam's slot: his icon and name; an action that keeps the current tab selected.</summary>
    private ComposableNode SamTabSlot(bool enabled, AndroidVisualPalette palette)
    {
        var persona = SamPersona(SamState);
        var label = AndroidSamProjection.EntryLabel(persona, Localize);
        return new NavigationBarItem(selected: false, onClick: OpenSamFromTab,
            enabled: enabled, alwaysShowLabel: true)
        {
            Modifier = Modifier.TestTag("native.tab.sam").Semantics(semantics =>
            {
                semantics.ContentDescription(label);
                semantics.Role(SemanticsRole.Button);
            }),
            Icon = new Icon(Icons.Default.Face, null) { Modifier = Modifier.Size(24), Tint = palette.SecondaryText },
            Label = new Text(persona),
        };
    }

    /// <summary>Sam's slot dialogs: checking (with Cancel) once the read takes a moment, then the answer.</summary>
    private ComposableNode? SamSlotDialog(AndroidVisualPalette palette)
    {
        if (_samSlotPhase == SamSlotPhase.CheckingShown)
        {
            var cancel = new TextButton(CancelSamSlotCheck, enabled: true)
            {
                Modifier = Modifier.TestTag("native.sam.slot.cancel"),
            };
            cancel.Add(new Text(Localize("Common_Cancel")));
            return new AndroidX.Compose.AlertDialog(CancelSamSlotCheck)
            {
                Modifier = Modifier.TestTag("native.sam.slot.checking"),
                Text = new Row(Arrangement.SpacedBy(12), Alignment.Vertical.CenterVertically)
                {
                    new CircularProgressIndicator { Modifier = Modifier.Size(24) },
                    Body(_samSlotText, palette, modifier: Modifier.Weight(1f)),
                },
                ConfirmButton = cancel,
            };
        }
        if (_samSlotPhase != SamSlotPhase.Answered)
            return null;
        var close = new TextButton(DismissSamSlotNotice, enabled: true)
        {
            Modifier = Modifier.TestTag("native.sam.slot.close"),
        };
        close.Add(new Text(Localize("Common_Close")));
        return new AndroidX.Compose.AlertDialog(DismissSamSlotNotice)
        {
            Modifier = Modifier.TestTag("native.sam.slot.notice"),
            Title = new Text(SamPersona(SamState)),
            Text = new Text(_samSlotText),
            ConfirmButton = close,
        };
    }

    // ---- Library ----

    private ComposableNode RenderLibrary(AndroidVisualPalette palette) =>
        new Composed(composer => LibraryPage(palette, composer.Remember(() => new ComposeScrollState())));

    /// <summary>
    /// The Library root (iOS 0a8e1b16, the Apple Music Library pattern): Resources (with
    /// its count once read), Vocabulary, Skills and Minimal Pairs, then Quick add.
    /// </summary>
    private ComposableNode LibraryPage(AndroidVisualPalette palette, ComposeScrollState scroll)
    {
        var viewport = new Column(Arrangement.SpacedBy(16))
        {
            Modifier = Modifier.FillMaxWidth().Weight(1f).VerticalScroll(scroll)
                .Padding(AndroidVisualTheme.PagePadding)
                .TestTag("native.library.list"),
        };
        var rows = new Column
        {
            Modifier.FillMaxWidth(),
        };
        var sections = new (LibrarySection Section, int Icon, string TitleKey, int? Count, string Id)[]
        {
            (LibrarySection.Resources, Resource.Drawable.ic_book, "Library_Resources", LibraryResourceCount,
                "native.library.row.resources"),
            (LibrarySection.Vocabulary, Resource.Drawable.ic_translate, "Nav_Vocabulary", null,
                "native.library.row.vocabulary"),
            (LibrarySection.Skills, Resource.Drawable.ic_track_changes, "Nav_Skills", null,
                "native.library.row.skills"),
            (LibrarySection.Pairs, Resource.Drawable.ic_graphic_eq, "Nav_MinimalPairs", null,
                "native.library.row.minimal-pairs"),
        };
        for (var index = 0; index < sections.Length; index++)
        {
            if (index > 0)
                rows.Add(new HorizontalDivider { Color = palette.Border });
            var (section, icon, titleKey, count, id) = sections[index];
            rows.Add(LibraryRow(section, icon, Localize(titleKey), count, id, palette));
        }
        var card = new Surface
        {
            Modifier = Modifier.FillMaxWidth(),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.CardRadius),
            Color = palette.Card,
        };
        card.Add(rows);
        viewport.Add(card);
        if (!string.IsNullOrWhiteSpace(Error.Value))
            viewport.Add(StatusAlert(Error.Value, palette, error: true, automationId: "native.shell.error"));
        if (_libraryErrorKey is { } error)
            viewport.Add(StatusAlert(Localize(error), palette, error: true, automationId: "native.library.error"));
        viewport.Add(QuickAddGrid(palette));
        return new Column
        {
            Modifier.FillMaxSize().TestTag("native.library.screen"),
            ScreenHeader(Localize("Nav_Library"), palette),
            viewport,
        };
    }

    /// <summary>A list row: icon, title, the count when it is already read, and a chevron.</summary>
    private ComposableNode LibraryRow(LibrarySection section, int icon, string title, int? count, string id,
        AndroidVisualPalette palette)
    {
        var label = count is { } shown
            ? string.Create(CultureInfo.CurrentCulture, $"{title}, {shown}") : title;
        var row = new Row(Arrangement.SpacedBy(12), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth().HeightIn(min: AndroidVisualTheme.ControlHeight)
                .TestTag(id)
                .Clickable(() => OpenLibraryPage(section))
                .Padding(horizontal: AndroidVisualTheme.CardPadding, vertical: 12)
                .Semantics(semantics =>
                {
                    semantics.ContentDescription(label);
                    semantics.Role(SemanticsRole.Button);
                }),
            new Icon(icon, null) { Modifier = Modifier.Size(22), Tint = palette.Primary },
            Body(title, palette, modifier: Modifier.Weight(1f)),
        };
        if (count is { } total)
            row.Add(Body(total.ToString(CultureInfo.CurrentCulture), palette, secondary: true));
        row.Add(new Icon(Icons.AutoMirrored.Default.KeyboardArrowRight, null)
        {
            Modifier = Modifier.Size(20),
            Tint = palette.SecondaryText,
        });
        return row;
    }

    /// <summary>Two columns on a phone; on a wider window one per button, up to four (as on iOS).</summary>
    private ComposableNode QuickAddGrid(AndroidVisualPalette palette)
    {
        var items = LibraryQuickAddItems;
        var columns = WindowWidthDp >= 600 ? Math.Clamp(items.Count, 2, 4) : 2;
        var grid = new Column(Arrangement.SpacedBy(8))
        {
            Modifier.FillMaxWidth().TestTag("native.library.quickadd"),
            Heading(Localize("Library_QuickAdd"), palette, 17,
                Modifier.Semantics(semantics => semantics.Heading())),
        };
        for (var start = 0; start < items.Count; start += columns)
        {
            var row = new Row(Arrangement.SpacedBy(8)) { Modifier.FillMaxWidth() };
            for (var column = 0; column < columns; column++)
            {
                var index = start + column;
                row.Add(index < items.Count
                    ? QuickAddTile(items[index], palette)
                    : new Box { Modifier.Weight(1f) });
            }
            grid.Add(row);
        }
        return grid;
    }

    private ComposableNode QuickAddTile(LibraryQuickAddItem item, AndroidVisualPalette palette)
    {
        var title = Localize(item.TitleKey);
        var caption = Body(title, palette, modifier: Modifier.FillMaxWidth());
        caption.FontSize = new Sp(AndroidVisualTheme.CaptionSize);
        caption.Align = TextAlign.Center;
        var tile = new Surface
        {
            Modifier = Modifier.Weight(1f).HeightIn(min: 84)
                .Border(1, palette.Border, AndroidVisualTheme.CardRadius)
                .Clip(AndroidVisualTheme.CardRadius)
                .TestTag($"native.library.quickadd.{item.Id}")
                .Clickable(item.Open)
                .Semantics(semantics =>
                {
                    semantics.ContentDescription(title);
                    semantics.Role(SemanticsRole.Button);
                }),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.CardRadius),
            Color = palette.Card,
        };
        tile.Add(new Column(Arrangement.SpacedBy(6), Alignment.Horizontal.CenterHorizontally)
        {
            Modifier.FillMaxWidth().Padding(10),
            new Icon(item.Icon, null) { Modifier = Modifier.Size(22), Tint = palette.Primary },
            caption,
        });
        return tile;
    }

    // ---- Settings tab rows ----

    /// <summary>
    /// The first row of the Settings tab (iOS SettingsView.ProfileRow): the learner's name and
    /// languages; it opens Profile. Before the profile is read it shows "Profile" alone.
    /// </summary>
    private ComposableNode SettingsProfileRow(AndroidVisualPalette palette)
    {
        var profileTitle = Localize("Nav_Profile");
        var summary = _profileSummary;
        var name = summary is { Name.Length: > 0 } known ? known.Name : profileTitle;
        var languages = summary is { } profile
            ? NativeProfileSummaryText.LanguagePair(profile.NativeLanguage, profile.TargetLanguage, Localize)
            : null;
        var spoken = new List<string> { name };
        if (languages is not null)
            spoken.Add(languages);
        if (!string.Equals(name, profileTitle, StringComparison.Ordinal))
            spoken.Add(profileTitle);
        var label = string.Join(", ", spoken);
        var labels = new Column(Arrangement.SpacedBy(2)) { Modifier.Weight(1f), Heading(name, palette, 20) };
        if (languages is not null)
            labels.Add(Body(languages, palette, secondary: true));
        var surface = new Surface
        {
            Modifier = Modifier.FillMaxWidth().TestTag("native.settings.profile")
                .Clip(AndroidVisualTheme.CardRadius)
                .Clickable(OpenProfile)
                .Semantics(semantics =>
                {
                    semantics.ContentDescription(label);
                    semantics.Role(SemanticsRole.Button);
                }),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.CardRadius),
            Color = palette.Card,
        };
        surface.Add(new Row(Arrangement.SpacedBy(12), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth().Padding(AndroidVisualTheme.CardPadding),
            new Icon(Icons.Default.AccountCircle, null) { Modifier = Modifier.Size(40), Tint = palette.Primary },
            labels,
            new Icon(Icons.AutoMirrored.Default.KeyboardArrowRight, null)
            {
                Modifier = Modifier.Size(20),
                Tint = palette.SecondaryText,
            },
        });
        return surface;
    }

    /// <summary>The last row of the Settings tab (was the Dashboard More menu's Sign out), in the danger color.</summary>
    private ComposableNode SettingsSignOut(AndroidVisualPalette palette)
    {
        var title = Localize("Nav_Logout");
#if DEBUG
        if (_preview)
            title = "Exit Debug preview";
#endif
        var button = new OutlinedButton(SignOut, enabled: !Busy.Value)
        {
            Modifier = Modifier.FillMaxWidth().HeightIn(min: AndroidVisualTheme.ControlHeight)
                .TestTag("native.settings.sign-out")
                .Semantics(semantics => semantics.ContentDescription(title)),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
        };
        button.Add(new Text(title) { Color = palette.Danger });
        return button;
    }
}
