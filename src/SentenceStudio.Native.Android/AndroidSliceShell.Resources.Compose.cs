using System.Collections.Immutable;
using System.Globalization;
using AndroidX.Compose;
using SentenceStudio.Common;
using SentenceStudio.Native;
using ComposeButton = AndroidX.Compose.Button;

namespace SentenceStudio.Native.Android;

/// <summary>Compose Learning Resources browser (list/grid, search, filters, empty states).</summary>
internal sealed partial class AndroidSliceShell
{
    private enum ResourceMenu
    {
        None, TypeFilter, LanguageFilter, FormMore, FormMediaType, FormLanguage, WordKind
    }

    private ResourceMenu _resourceMenu;
    private string? _resourceMenuWordId;

    private void ToggleResourceMenu(ResourceMenu menu, string? wordId = null) => Publish(() =>
    {
        var open = _resourceMenu == menu && _resourceMenuWordId == wordId;
        _resourceMenu = open ? ResourceMenu.None : menu;
        _resourceMenuWordId = open ? null : wordId;
    });

    private void CloseResourceMenu() => Publish(() =>
    {
        _resourceMenu = ResourceMenu.None;
        _resourceMenuWordId = null;
    });

    private ComposableNode NavigationTabs(AndroidVisualPalette palette)
    {
        var bar = new NavigationBar
        {
            Modifier = Modifier.FillMaxWidth().TestTag("native.tabs"),
        };
        bar.Add(TabItem(NativeDestination.Dashboard, NativeAccess.DashboardTab,
            "Dashboard_Title", Icons.Default.Home, palette));
        bar.Add(TabItem(NativeDestination.Resources, NativeAccess.ResourcesTab,
            "Resources_Title", Icons.Default.Menu, palette));
        return bar;
    }

    private ComposableNode TabItem(NativeDestination destination, string tag,
        string labelKey, AndroidX.Compose.UI.Graphics.Vector.ImageVector icon,
        AndroidVisualPalette palette)
    {
        var label = Localize(labelKey);
        var selected = destination == NativeDestination.Dashboard
            ? Destination is NativeDestination.Dashboard or NativeDestination.TodaysPlan
            : Destination == destination;
        return new NavigationBarItem(selected: selected,
            onClick: () => SelectTopLevel(destination),
            enabled: !Busy.Value && _resources?.IsNavigationBlocked != true,
            alwaysShowLabel: true)
        {
            Modifier = Modifier.TestTag(tag).Semantics(semantics =>
            {
                semantics.ContentDescription(label);
                semantics.Selected(selected);
            }),
            Icon = new Icon(icon, label)
            {
                Modifier = Modifier.Size(24),
                Tint = selected ? palette.Primary : palette.SecondaryText,
            },
            Label = new Text(label),
        };
    }

    private ComposableNode RenderResources(AndroidVisualPalette palette)
    {
        if (EnsureResources() is not { } resources)
            return new Column
            {
                Modifier.FillMaxSize().TestTag("native.resources.screen"),
                StatusAlert(Localize("NativeResources_LoadError"), palette, error: true,
                    automationId: "native.resources.status"),
            };
        if (resources.IsFormOpen)
            return RenderResourceForm(resources, palette);

        var state = resources.ListState;
        var enabled = state.Phase != NativeResourceListPhase.Blocked
            && !state.StarterBusy && !Busy.Value;
        var add = new IconButton(resources.OpenCreate, enabled: enabled)
        {
            Modifier = Modifier.TestTag("native.resources.add")
                .Semantics(semantics => semantics.ContentDescription(
                    Localize("Resources_AddResource"))),
        };
        add.Add(new Icon(Icons.Default.Add, Localize("Resources_AddResource"))
        {
            Modifier = Modifier.Size(24),
            Tint = palette.Text,
        });
        var page = new Column
        {
            Modifier.FillMaxSize().TestTag("native.resources.screen"),
            new TopAppBar
            {
                Modifier = Modifier.FillMaxWidth().TestTag("native.resources.appbar"),
                Title = Heading(Localize("Resources_Title"), palette, 20,
                    Modifier.Semantics(semantics => semantics.Heading())),
                Actions = add,
            },
        };

        var header = new Column(Arrangement.SpacedBy(10))
        {
            Modifier.FillMaxWidth()
                .Padding(horizontal: AndroidVisualTheme.PagePadding, vertical: 8),
        };
        if (state.Phase == NativeResourceListPhase.Loading)
        {
            header.Add(new CircularProgressIndicator
            {
                Modifier = Modifier.Size(28).TestTag("native.resources.status")
            });
            header.Add(Body(Localize("Common_Loading"), palette, secondary: true));
            page.Add(header);
            return page;
        }
        header.Add(new OutlinedTextField(state.SearchText, resources.Search,
            enabled: enabled, singleLine: true)
        {
            Modifier = Modifier.FillMaxWidth().TestTag(NativeAccess.ResourceSearch)
                .Semantics(semantics => semantics.ContentDescription(
                    Localize("Resources_SearchPlaceholder"))),
            Placeholder = new Text(Localize("Resources_SearchPlaceholder")),
            LeadingIcon = new Icon(Icons.Default.Search,
                Localize("Resources_SearchPlaceholder")) { Modifier = Modifier.Size(20) },
        });
        var types = state.Types.IsDefault ? [] : state.Types;
        var languages = state.Languages.IsDefault ? [] : state.Languages;
        header.Add(new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth().TestTag("native.resources.filters"),
            FilterMenu(ResourceMenu.TypeFilter, NativeAccess.ResourceType,
                Localize("NativeResources_TypeFilter"), Localize("Resources_AllTypes"),
                types, state.MediaType, resources.FilterType, enabled, palette),
            FilterMenu(ResourceMenu.LanguageFilter, NativeAccess.ResourceLanguage,
                Localize("NativeResources_LanguageFilter"),
                Localize("Resources_AllLanguages"), languages, state.Language,
                resources.FilterLanguage, enabled, palette),
        });
        var modes = new SingleChoiceSegmentedButtonRow
        {
            Modifier = Modifier.FillMaxWidth().TestTag("native.resources.view.mode"),
        };
        modes.Add(ViewModeButton(resources, NativeResourceViewMode.Grid,
            "Resources_GridView", "native.resources.view.grid", state, enabled));
        modes.Add(ViewModeButton(resources, NativeResourceViewMode.List,
            "Resources_ListView", "native.resources.view.list", state, enabled));
        header.Add(modes);
        var syncStatus = NativeResourceSyncStatus.Describe(
            state.Sync, state.SyncErrorKey, Localize);
        if (!string.IsNullOrEmpty(syncStatus))
            header.Add(Body(syncStatus, palette, secondary: true,
                modifier: Modifier.TestTag("native.resources.sync.status")));
        if (!string.IsNullOrWhiteSpace(resources.ListFeedback))
            header.Add(StatusAlert(resources.ListFeedback, palette,
                automationId: "native.resources.feedback"));
        if (state.StarterBusy)
            header.Add(new Row(Arrangement.SpacedBy(10), Alignment.Vertical.CenterVertically)
            {
                Modifier.FillMaxWidth().TestTag("native.resources.starter.progress"),
                new CircularProgressIndicator { Modifier = Modifier.Size(20) },
                Body(Localize("Resources_CreatingStarter"), palette, secondary: true),
            });
        if (state.ErrorKey == "NativeResources_LoadError"
            || state.Phase is NativeResourceListPhase.LoadError
                or NativeResourceListPhase.Blocked)
        {
            header.Add(StatusAlert(Localize(state.ErrorKey ?? "NativeResources_LoadError"),
                palette, error: true, automationId: "native.resources.status"));
            if (state.Phase != NativeResourceListPhase.Blocked)
                header.Add(Action(Localize(NativeText.Retry), "native.resources.retry",
                    resources.ReloadList, !Busy.Value, primary: false));
        }
        page.Add(header);
        if (state.Phase is NativeResourceListPhase.LoadError or NativeResourceListPhase.Blocked)
            return page;

        var items = state.Items.IsDefault ? ImmutableArray<NativeResourceItem>.Empty : state.Items;
        if (state.Phase is NativeResourceListPhase.Empty or NativeResourceListPhase.NoMatches)
        {
            var message = Body(Localize("Resources_NoResourcesFound"), palette, secondary: true,
                modifier: Modifier.FillMaxWidth().TestTag("native.resources.status"));
            message.Align = TextAlign.Center;
            page.Add(new Column(Arrangement.SpacedBy(16),
                Alignment.Horizontal.CenterHorizontally)
            {
                Modifier.FillMaxWidth().Padding(32).TestTag("native.resources.empty"),
                message,
                Action(Localize("Resources_AddFirstResource"), "native.resources.empty.add",
                    resources.OpenCreate, enabled),
                Action(Localize("Resources_CreateStarter"), "native.resources.empty.starter",
                    resources.CreateStarter, enabled, primary: false),
            });
            return page;
        }

        var count = Body(string.Format(CultureInfo.CurrentCulture,
            Localize("Resources_ResourcesCount"), items.Length), palette, secondary: true,
            modifier: Modifier.Padding(horizontal: AndroidVisualTheme.PagePadding)
                .TestTag("native.resources.count"));
        count.FontSize = new Sp(AndroidVisualTheme.CaptionSize);
        page.Add(count);
        if (state.ViewMode == NativeResourceViewMode.Grid)
            page.Add(new LazyVerticalGrid<NativeResourceItem>(GridCells.Adaptive(300), items,
                item => ResourceCard(item, resources, palette, list: false, enabled))
            {
                Key = item => item.Id,
                Modifier = Modifier.FillMaxWidth().Weight(1f)
                    .Padding(horizontal: AndroidVisualTheme.PagePadding)
                    .TestTag("native.resources.grid"),
                VerticalArrangement = Arrangement.SpacedBy(10),
                HorizontalArrangement = Arrangement.SpacedBy(10),
            });
        else
        {
            var listSurface = new Surface
            {
                Modifier = Modifier.FillMaxWidth().Weight(1f)
                    .Padding(horizontal: AndroidVisualTheme.PagePadding, vertical: 4)
                    .Border(1, palette.Border, AndroidVisualTheme.ControlRadius),
                Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
                Color = palette.Elevated,
            };
            listSurface.Add(new LazyColumn<NativeResourceItem>(items,
                item => ResourceCard(item, resources, palette, list: true, enabled))
            {
                Key = item => item.Id,
                Modifier = Modifier.FillMaxSize().TestTag("native.resources.list"),
            });
            page.Add(listSurface);
        }
        return page;
    }

    private ComposableNode ViewModeButton(AndroidResourcesCoordinator resources,
        NativeResourceViewMode mode, string labelKey, string tag,
        NativeResourceListState state, bool enabled)
    {
        var label = Localize(labelKey);
        var selected = state.ViewMode == mode;
        var button = new SegmentedButton(selected: selected,
            onClick: () =>
            {
                if (enabled)
                    resources.SetViewMode(mode);
            })
        {
            Modifier = Modifier.Weight(1f).TestTag(tag).Semantics(semantics =>
            {
                semantics.ContentDescription(label);
                semantics.Selected(selected);
            }),
        };
        button.Add(new Text(label));
        return button;
    }

    private ComposableNode FilterMenu(ResourceMenu menu, string tag, string label,
        string all, ImmutableArray<string> options, string? selected,
        Action<string?> changed, bool enabled, AndroidVisualPalette palette)
    {
        var values = new[] { all }.Concat(options).ToArray();
        var selectedIndex = selected is null ? 0 : Array.IndexOf(values, selected);
        if (selectedIndex < 0)
            selectedIndex = 0;
        var anchor = new OutlinedButton(() => ToggleResourceMenu(menu), enabled: enabled)
        {
            Modifier = Modifier.FillMaxWidth().HeightIn(min: AndroidVisualTheme.ControlHeight)
                .TestTag(tag)
                .Semantics(semantics =>
                {
                    semantics.ContentDescription($"{label}: {values[selectedIndex]}");
                    semantics.StateDescription(values[selectedIndex]);
                }),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
        };
        anchor.Add(new Row(Arrangement.SpaceBetween, Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth(),
            new Text(values[selectedIndex]) { MaxLines = 1, Color = palette.Text },
            new Icon(Icons.Default.ArrowDropDown, label) { Modifier = Modifier.Size(20) },
        });
        var dropdown = new DropdownMenu(expanded: _resourceMenu == menu,
            onDismissRequest: CloseResourceMenu)
        {
            Modifier = Modifier.TestTag($"{tag}.menu"),
        };
        for (var index = 0; index < values.Length; index++)
        {
            var choice = index;
            var isSelected = index == selectedIndex;
            dropdown.Add(new DropdownMenuItem(text: new Text(values[index]), onClick: () =>
            {
                CloseResourceMenu();
                changed(choice == 0 ? null : values[choice]);
            })
            {
                Modifier = Modifier.TestTag($"{tag}.option.{index}").Semantics(semantics =>
                {
                    semantics.ContentDescription(values[choice]);
                    semantics.Selected(isSelected);
                }),
                // The source select marks exactly one option.
                TrailingIcon = isSelected
                    ? new Icon(Icons.Default.Check, values[choice]) { Modifier = Modifier.Size(20) }
                    : null,
            });
        }
        return new Box
        {
            Modifier.Weight(1f),
            anchor,
            dropdown,
        };
    }

    private ComposableNode ResourceCard(NativeResourceItem resource,
        AndroidResourcesCoordinator resources, AndroidVisualPalette palette,
        bool list, bool enabled)
    {
        var title = string.IsNullOrWhiteSpace(resource.Title)
            ? Localize("NativeResources_Untitled") : resource.Title;
        var media = string.Join(" · ", new[] { resource.MediaType, resource.Language }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        var date = AndroidResourceDates.ListDate(resource.CreatedAt, resource.IsSmartResource);
        var metadata = string.Join(" · ", new[] { media, date }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        var icon = resource.IsSmartResource ? Icons.Default.Star : resource.MediaType switch
        {
            "Video" => Icons.Default.PlayArrow,
            "Podcast" => Icons.Default.Call,
            "Image" => Icons.Default.Face,
            "Vocabulary List" => Icons.Default.Menu,
            "Article" => Icons.Default.Edit,
            _ => Icons.Default.Info,
        };
        var name = Heading(title, palette, list ? 16 : 17,
            Modifier.TestTag($"{NativeAccess.ResourceRow}.{resource.Id}.title"));
        name.MaxLines = 1;
        var mediaText = Body(media, palette, secondary: true,
            modifier: Modifier.TestTag($"{NativeAccess.ResourceRow}.{resource.Id}.metadata"));
        mediaText.FontSize = new Sp(AndroidVisualTheme.CaptionSize);
        var time = Body(date, palette, secondary: true,
            modifier: Modifier.TestTag($"{NativeAccess.ResourceRow}.{resource.Id}.created"));
        time.FontSize = new Sp(AndroidVisualTheme.CaptionSize);
        var row = new Row(Arrangement.SpacedBy(12), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth()
                .HeightIn(min: list ? AndroidVisualTheme.ControlHeight : 76)
                .Padding(horizontal: AndroidVisualTheme.CardPadding, vertical: list ? 8 : 12),
            new Icon(icon, resource.MediaType ?? title)
            {
                Modifier = Modifier.Size(24),
                Tint = palette.Primary,
            },
            new Column(Arrangement.SpacedBy(2)) { Modifier.Weight(1f), name, mediaText },
            time,
        };
        var modifier = Modifier.FillMaxWidth()
            .TestTag($"{NativeAccess.ResourceRow}.{resource.Id}");
        modifier = enabled
            ? modifier.Clickable(() => resources.Open(resource.Id)).Semantics(semantics =>
            {
                semantics.ContentDescription($"{title}, {metadata}");
                semantics.Role(SemanticsRole.Button);
            })
            : modifier.Semantics(semantics =>
                semantics.ContentDescription($"{title}, {metadata}"));
        if (list)
            return new Column
            {
                modifier,
                row,
                new HorizontalDivider { Color = palette.Border },
            };
        var card = new Surface
        {
            Modifier = modifier,
            Shape = Shape.RoundedCorners(AndroidVisualTheme.CardRadius),
            Color = palette.Card,
        };
        card.Add(row);
        return card;
    }
}

internal static class AndroidResourceDates
{
    internal static string ListDate(DateTime createdAt, bool isSmartResource) =>
        Format(createdAt, "d", isSmartResource);

    internal static string DetailCreatedDate(DateTime createdAt, bool isSmartResource) =>
        Format(createdAt, "g", isSmartResource);

    internal static string DetailUpdatedDate(DateTime updatedAt) =>
        Format(updatedAt, "g", false);

    // SmartResourceService writes CreatedAt with local time while saves write UTC
    // and SQLite drops Kind, so smart CreatedAt stays local (same rule as iOS).
    private static string Format(DateTime timestamp, string format, bool isSmartCreatedAt)
    {
        if (timestamp == default)
            return string.Empty;
        var local = timestamp.Kind switch
        {
            DateTimeKind.Local => timestamp,
            DateTimeKind.Unspecified when isSmartCreatedAt => timestamp,
            _ => TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(timestamp, DateTimeKind.Utc), TimeZoneInfo.Local),
        };
        return local.ToString(format, CultureInfo.CurrentUICulture);
    }
}
