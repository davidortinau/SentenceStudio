using System.Collections.Immutable;
using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Common;
using SentenceStudio.Shared.Models;
using DateFormatter = SentenceStudio.Native.iOS.Platform.NativeResourceDateFormatter;
using Views = Microsoft.SwiftUI.SwiftUI;

using SentenceStudio.Native.iOS.Styling;

namespace SentenceStudio.Native.iOS.Features.LearningResources;

internal static class LearningResourcesView
{
    internal static SwiftView Page(
        NativeResourceListState state,
        string? feedback,
        SourceUiStyle style,
        Func<string, string> text,
        LearningResourcesActions actions,
        Action? openVocabulary)
    {
        if (state.Phase == NativeResourceListPhase.Loading)
            return Views.Toolbar(style.Page(Views.Scroll(
                    Views.VStack(
                            Views.IndeterminateProgress(text("Common_Loading"))
                                .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
                                .WithAccessibilityIdentifier("native.resources.status"))
                        .WithPadding(SourceUiLayout.PageGutter(style.ViewportWidth))
                        .WithFlexibleWidth()
                        .WithAccessibilityIdentifier("native.resources.scroll.content"))
                .WithAccessibilityIdentifier("native.resources.scroll")),
                BrowserHeader(state, style, text, actions.Add, actions.OpenSkills,
                    openVocabulary));

        var items = new List<SwiftView>();
        var types = state.Types.IsDefault ? [] : state.Types;
        var languages = state.Languages.IsDefault ? [] : state.Languages;
        var search = style.TextInput(Views.TextField(
                text("Resources_SearchPlaceholder"), state.SearchText, actions.Search))
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.resources.search");
        search.IsEnabled = state.Phase != NativeResourceListPhase.Blocked
            && !state.StarterBusy;
        items.Add(search);
        var filters = Views.HStack(
            Filter(style, text("NativeResources_TypeFilter"), text("Resources_AllTypes"),
                    types, state.MediaType, actions.FilterType)
                .WithAccessibilityIdentifier(NativeAccess.ResourceType),
            Filter(style, text("NativeResources_LanguageFilter"),
                    text("Resources_AllLanguages"), languages, state.Language,
                    actions.FilterLanguage)
                .WithAccessibilityIdentifier(NativeAccess.ResourceLanguage))
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.resources.filters");
        filters.Spacing = SourceUiLayout.ItemGap;
        var modes = Views.ButtonGroup([
            style.ResourceViewModeButton("square.grid.3x3",
                    text("Resources_GridView"),
                    state.ViewMode == NativeResourceViewMode.Grid,
                    () => actions.SetViewMode(NativeResourceViewMode.Grid),
                    SourceButtonGroupPosition.First)
                .WithAccessibilityIdentifier("native.resources.view.grid"),
            style.ResourceViewModeButton("list.bullet",
                    text("Resources_ListView"),
                    state.ViewMode == NativeResourceViewMode.List,
                    () => actions.SetViewMode(NativeResourceViewMode.List),
                    SourceButtonGroupPosition.Last)
                .WithAccessibilityIdentifier("native.resources.view.list")],
                selectedIndex: state.ViewMode == NativeResourceViewMode.Grid ? 0 : 1)
            .WithButtonGroupLayout(style.Appearance.BootstrapBaseButtonBorderWidth)
            .WithAccessibilityIdentifier("native.resources.view.mode");
        if (style.ViewportWidth < 768)
            modes.WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Trailing);
        // Two flexible halves need room for both 180-point source filter menus.
        var controls = Views.HStack(filters, modes)
            .WithAdaptiveStack(720)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.resources.browser.controls");
        controls.Spacing = SourceUiLayout.PageGap;
        items.Add(controls);

        var syncStatus = NativeResourceSyncStatus.Describe(
            state.Sync, state.SyncErrorKey, text);
        if (!string.IsNullOrEmpty(syncStatus))
            items.Add(style.Text(Views.Text(syncStatus),
                    SourceTextRole.Caption1, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier("native.resources.sync.status"));
        if (!string.IsNullOrWhiteSpace(feedback))
            items.Add(style.Text(Views.Text(feedback),
                    SourceTextRole.Body2, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier("native.resources.feedback"));
        if (state.ErrorKey == "NativeResources_LoadError"
            || state.Phase is NativeResourceListPhase.LoadError
                or NativeResourceListPhase.Blocked)
        {
            items.Add(style.Text(Views.Text(
                    text(state.ErrorKey ?? "NativeResources_LoadError")),
                    SourceTextRole.Body1, SourceTextTone.Danger)
                .WithAccessibilityIdentifier("native.resources.status"));
            if (state.Phase != NativeResourceListPhase.Blocked)
                items.Add(Views.Button(text(NativeText.Retry), actions.Reload)
                    .WithAccessibilityIdentifier("native.resources.retry"));
        }
        if (state.Phase is not (NativeResourceListPhase.LoadError
            or NativeResourceListPhase.Blocked))
        {
            var resources = state.Items.IsDefault
                ? ImmutableArray<NativeResourceItem>.Empty : state.Items;
            if (state.Phase is NativeResourceListPhase.Empty
                or NativeResourceListPhase.NoMatches)
            {
                var message = style.Text(Views.Text(
                        text("Resources_NoResourcesFound")),
                        SourceTextRole.Body1, SourceTextTone.Secondary)
                    .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
                    .WithAccessibilityIdentifier("native.resources.status");
                var starter = style.SecondaryButton(
                        text("Resources_CreateStarter"), actions.CreateStarter)
                    .WithAccessibilityIdentifier("native.resources.empty.starter");
                SourceUiStyle.SetCustomButtonAvailability(starter, style.Appearance,
                    SourceButtonRole.FilledSecondary, !state.StarterBusy);
                var emptyActions = Views.HStack(
                        style.PrimaryButton(text("Resources_AddFirstResource"),
                                actions.Add)
                            .WithAccessibilityIdentifier("native.resources.empty.add"),
                        starter)
                    .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
                    .WithAccessibilityIdentifier("native.resources.empty.actions");
                emptyActions.Spacing = SourceUiLayout.ItemGap;
                var empty = Views.VStack(message, emptyActions)
                    .WithPadding(48)
                    .WithFlexibleWidth()
                    .WithAccessibilityIdentifier("native.resources.empty");
                empty.Spacing = SourceUiLayout.LargeCardInset;
                items.Add(empty);
            }
            else
            {
                items.Add(style.Text(Views.Text(string.Format(CultureInfo.CurrentCulture,
                        text("Resources_ResourcesCount"), resources.Length)),
                        SourceTextRole.Small, SourceTextTone.Secondary)
                    .WithAccessibilityIdentifier("native.resources.count"));
                if (state.ViewMode == NativeResourceViewMode.Grid)
                    items.Add(Views.LazyVGrid(
                            SourceUiLayout.ResourceColumns(style.ViewportWidth),
                            resources.Select(item => LearningResourceRowView.Row(item, text, actions.Open,
                                style, list: false)).ToArray())
                        .WithAccessibilityIdentifier("native.resources.grid"));
                else
                    items.Add(ResourceListGroup(resources, text, actions.Open, style));
            }
        }
        if (state.StarterBusy)
            items.Add(style.Text(Views.Text(text("Resources_CreatingStarter")),
                    SourceTextRole.Body2, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier("native.resources.starter.progress"));
        var scrollContent = Views.VStack(items.ToArray())
            .WithPadding(SourceUiLayout.PageGutter(style.ViewportWidth))
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.resources.scroll.content");
        scrollContent.Spacing = SourceUiLayout.PageGap;
        var page = Views.Scroll(scrollContent)
            .WithAccessibilityIdentifier("native.resources.scroll");
        page.IsEnabled = !state.StarterBusy;
        return Views.Toolbar(style.Page(page),
            BrowserHeader(state, style, text, actions.Add, actions.OpenSkills,
                openVocabulary));
    }

    private static SwiftView ResourceListGroup(
        ImmutableArray<NativeResourceItem> resources, Func<string, string> text,
        Action<string> open, SourceUiStyle style)
    {
        var rows = new List<SwiftView>();
        foreach (var resource in resources)
        {
            if (rows.Count != 0)
                rows.Add(Views.Divider());
            rows.Add(LearningResourceRowView.Row(resource, text, open, style, list: true));
        }
        var group = Views.LazyVGrid(1, rows.ToArray())
            .WithAccessibilityIdentifier("native.resources.list");
        group.Spacing = 0;
        return style.BorderedCard(group, radius: style.Appearance.BootstrapRadius);
    }

    private static SwiftView[] BrowserHeader(
        NativeResourceListState state, SourceUiStyle style,
        Func<string, string> text, Action addResource, Action? openSkills,
        Action? openVocabulary)
    {
        var enabled = state.Phase != NativeResourceListPhase.Blocked
            && !state.StarterBusy;
        var add = Views.Button(text("Resources_AddResource"), addResource)
            .WithAccessibilityIdentifier("native.resources.add");
        add.IsEnabled = enabled;
        var menuActions = new List<SwiftView> { add };
        if (openSkills is not null)
            menuActions.Add(Views.Button(text("Nav_Skills"), openSkills)
                .WithAccessibilityIdentifier("native.resources.skills"));
        if (openVocabulary is not null)
            menuActions.Add(Views.Button(text("Nav_Vocabulary"), openVocabulary)
                .WithAccessibilityIdentifier("native.resources.vocabulary"));
        var more = Views.Menu(text("More"), menuActions.ToArray())
            .WithAccessibilityIdentifier("native.resources.more");
        more.IsEnabled = enabled;
        if (style.ViewportWidth < 768)
            return [more];
        var primary = style.PrimaryButton(text("Resources_AddResource"), addResource)
            .WithAccessibilityIdentifier("native.resources.add.primary");
        SourceUiStyle.SetCustomButtonAvailability(primary, style.Appearance,
            SourceButtonRole.Primary, enabled);
        return [primary, more];
    }

    private static SwiftView Filter(
        SourceUiStyle style, string label, string all,
        ImmutableArray<string> options, string? selected,
        Action<string?> changed)
    {
        var values = new[] { all }.Concat(options).ToArray();
        var selectedIndex = selected is null
            ? 0 : Array.IndexOf(values, selected);
        if (selectedIndex < 0)
            selectedIndex = 0;
        var selectedText = Views.Text(values[selectedIndex])
            .WithFlexibleWidth();
        var chevron = Views.Image("chevron.up.chevron.down")
            .WithForegroundColor(style.Appearance.SecondaryText.Light,
                style.Appearance.SecondaryText.Dark);
        var menuLabel = style.Input(Views.HStack(selectedText, chevron)
            .WithFlexibleWidth());
        menuLabel.Spacing = SourceUiLayout.ItemGap;

        var choices = new SwiftView[values.Length];
        for (var index = 0; index < choices.Length; index++)
        {
            var choice = index;
            choices[index] = Views.Button(
                    Views.HStack(Views.Text(values[index]).WithFlexibleWidth(),
                        Views.Image("checkmark")
                            .WithOpacity(index == selectedIndex ? 1 : 0)),
                    () => changed(choice == 0 ? null : values[choice]))
                .WithAccessibilityLabel(values[index])
                .WithAccessibilitySelected(index == selectedIndex);
            choices[index].Text = values[index];
        }

        var menu = Views.Menu(menuLabel, choices)
            .WithTintColor(style.Appearance.Primary.Light,
                style.Appearance.Primary.Dark)
            .WithAccessibilityLabel(label)
            .WithFlexibleWidth();
        menu.Text = values[selectedIndex];
        return menu;
    }

}
