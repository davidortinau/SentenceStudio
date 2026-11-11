using AppKit;
using Microsoft.SwiftUI;
using SentenceStudio.Contracts.Theme;
using SentenceStudio.Data;
using SentenceStudio.Native;
using SentenceStudio.Native.Features.Lookups;
using SentenceStudio.Native.iOS.Features.Lookups;
using SentenceStudio.Native.iOS.Features.Sam;
using SentenceStudio.Native.iOS.Features.Vocabulary;
using SentenceStudio.Native.iOS.Styling;
using SentenceStudio.Shared.Models;
using Views = Microsoft.SwiftUI.SwiftUI;

// Real Microsoft.SwiftUI facade nodes need the native bridge and the main thread.
NSApplication.Init();

var style = new SourceUiStyle(SourceUiAppearance.FromSelection(AppearanceSelection.Default), 402);
var wideStyle = new SourceUiStyle(SourceUiAppearance.FromSelection(AppearanceSelection.Default), 1000);
var sidebarStyle = new SourceUiStyle(SourceUiAppearance.FromSelection(AppearanceSelection.Default), 1210);
static string Text(string key) => key == "Lookups_AddToVocabularyFor" ? "Add {0} to vocabulary" : key;
var actions = new RecentLookupsActions(Reload: () => { }, Add: (_, _) => { }, DismissNotice: () => { });

var resourceA = new VocabularyLookupDestination("res-a", "Story", "Korean");
var resourceB = new VocabularyLookupDestination("res-b", "Daily words", "Korean");
var withDestinations = Row("lk:1", [resourceA, resourceB]);
var withoutDestinations = Row("lk:2", []);
RecentLookupsState Ready(params RecentLookupRow[] rows) =>
    new(RecentLookupsPhase.Ready, rows, null, null);

var cases = new (string Name, Action Test)[]
{
    // Seen on the simulator 2026-11-04: a heading Text among the menu actions made the facade
    // throw "A menu needs one or more Button actions" and the page stayed on Loading.
    ("Recent lookups: a row with destinations builds, its menu holds only resource Buttons", () =>
    {
        using var page = RecentLookupsView.Page(Ready(withDestinations, withoutDestinations), style, Text, actions);
        Assert(page.AccessibilityIdentifier == RecentLookupsAccess.Page);
        Assert(Find(page, RecentLookupsAccess.List) is not null);
        var menu = Find(page, RecentLookupsAccess.Add(withDestinations))
            ?? throw new Exception("The add menu is missing.");
        Assert(menu.Kind == "menu");
        var buttons = menu.Children.Skip(1).ToArray();
        Assert(buttons.Length == 2 && buttons.All(button => button.Kind == "button"));
        Assert(buttons[0].AccessibilityIdentifier == RecentLookupsAccess.Destination(withDestinations, resourceA));
        Assert(buttons[1].AccessibilityIdentifier == RecentLookupsAccess.Destination(withDestinations, resourceB));
        // A row with no resource in its language shows text instead of a menu.
        var none = Find(page, RecentLookupsAccess.Add(withoutDestinations))
            ?? throw new Exception("The no-resource text is missing.");
        Assert(none.Kind != "menu");
        Assert(Find(page, $"{RecentLookupsAccess.Row(withDestinations)}.text") is not null);
        Assert(Find(page, $"{RecentLookupsAccess.Row(withDestinations)}.source") is not null);
    }),
    ("Recent lookups: the destination Buttons alone satisfy the facade Menu", () =>
    {
        var buttons = RecentLookupsView.DestinationButtons(withDestinations, (_, _) => { });
        using var menu = Views.Menu(Views.Text("Add"), buttons);
        Assert(menu.Children.Count == 3);
    }),
    ("Recent lookups: a row being added still builds, with its menu disabled", () =>
    {
        using var page = RecentLookupsView.Page(
            new RecentLookupsState(RecentLookupsPhase.Ready, [withDestinations], withDestinations.Id, null),
            style, Text, actions);
        var menu = Find(page, RecentLookupsAccess.Add(withDestinations))!;
        Assert(menu.Kind == "menu" && !menu.IsEnabled);
    }),
    ("Recent lookups: every other phase and notice builds", () =>
    {
        foreach (var phase in new[] { RecentLookupsPhase.Closed, RecentLookupsPhase.Loading,
                     RecentLookupsPhase.Empty, RecentLookupsPhase.LoadError, RecentLookupsPhase.Blocked })
        {
            using var page = RecentLookupsView.Page(new RecentLookupsState(phase, [], null, null),
                style, Text, actions);
            Assert(Find(page, RecentLookupsAccess.Status) is not null);
            Assert((Find(page, RecentLookupsAccess.Retry) is not null) == (phase == RecentLookupsPhase.LoadError));
        }
        foreach (var notice in new[]
                 {
                     NativeMessage.Success("Lookups_Added", "Story"),
                     NativeMessage.Info("Lookups_AlreadyPresent", "Story"),
                     new NativeMessage(NativeMessageKind.Error, "Lookups_AddFailed")
                 })
        {
            using var page = RecentLookupsView.Page(
                new RecentLookupsState(RecentLookupsPhase.Ready, [withDestinations], null, notice),
                style, Text, actions);
            Assert(Find(page, RecentLookupsAccess.Notice) is not null);
        }
    }),
    // docs/sam-entry.md, driven through the facade's SwiftUI report hooks.
    ("Sam entry: scrolling down 24 points hides it, up 24 brings it back, the ends show it", () =>
    {
        var floating = new SamFloatingEntry();
        using var inset = FloatingPage(out var scroll, out var entry);
        floating.Bind(inset);
        Assert(!entry.IsHidden && entry.HideTransition == SwiftHideTransition.SlideDown);
        scroll.InvokeScrollGeometryChanged(Geometry(-100));
        Assert(!entry.IsHidden);
        scroll.InvokeScrollGeometryChanged(Geometry(-80));
        Assert(!entry.IsHidden);
        scroll.InvokeScrollGeometryChanged(Geometry(-70));
        Assert(entry.IsHidden && !floating.IsShown);
        scroll.InvokeScrollGeometryChanged(Geometry(400));
        Assert(entry.IsHidden);
        scroll.InvokeScrollGeometryChanged(Geometry(380));
        Assert(entry.IsHidden);
        scroll.InvokeScrollGeometryChanged(Geometry(370));
        Assert(!entry.IsHidden);
        scroll.InvokeScrollGeometryChanged(Geometry(600));
        Assert(entry.IsHidden);
        // The end (content 2000 - container 800 - top inset 100 = 1100) always shows it.
        scroll.InvokeScrollGeometryChanged(Geometry(1100));
        Assert(!entry.IsHidden);
        scroll.InvokeScrollGeometryChanged(Geometry(800));
        scroll.InvokeScrollGeometryChanged(Geometry(900));
        Assert(entry.IsHidden);
        scroll.InvokeScrollGeometryChanged(Geometry(-100));
        Assert(!entry.IsHidden);
        // A page that cannot scroll is at both ends.
        scroll.InvokeScrollGeometryChanged(new SwiftScrollGeometry(-100, 300, 800, 100, 80));
        Assert(!entry.IsHidden);
    }),
    ("Sam entry: hidden while the keyboard is up, kept while VoiceOver runs", () =>
    {
        var floating = new SamFloatingEntry();
        using var inset = FloatingPage(out var scroll, out var entry);
        floating.Bind(inset);
        inset.InvokeKeyboardOverlapChanged(0);
        Assert(!entry.IsHidden);
        inset.InvokeKeyboardOverlapChanged(291);
        Assert(entry.IsHidden);
        scroll.InvokeScrollGeometryChanged(Geometry(-100));
        Assert(entry.IsHidden);
        inset.InvokeKeyboardOverlapChanged(0);
        Assert(!entry.IsHidden);

        inset.InvokeAccessibilitySettingsChanged(SwiftAccessibilitySettings.VoiceOver);
        scroll.InvokeScrollGeometryChanged(Geometry(200));
        scroll.InvokeScrollGeometryChanged(Geometry(300));
        Assert(!entry.IsHidden);
        inset.InvokeKeyboardOverlapChanged(291);
        Assert(entry.IsHidden);
        inset.InvokeKeyboardOverlapChanged(0);
        // Reduce Motion alone does not keep it: it changes the motion, not the rule.
        inset.InvokeAccessibilitySettingsChanged(SwiftAccessibilitySettings.ReduceMotion);
        scroll.InvokeScrollGeometryChanged(Geometry(330));
        Assert(entry.IsHidden);
    }),
    ("Sam entry: a page built and discarded leaves the mounted page's button alone", () =>
    {
        var floating = new SamFloatingEntry();
        using var mounted = FloatingPage(out var scroll, out var entry);
        floating.Bind(mounted);
        scroll.InvokeScrollGeometryChanged(Geometry(200));
        scroll.InvokeScrollGeometryChanged(Geometry(260));
        Assert(entry.IsHidden);
        // The Resources incremental update builds a whole page, then disposes it.
        var discarded = FloatingPage(out _, out var discardedEntry);
        floating.Bind(discarded);
        Assert(discardedEntry.IsHidden);
        discarded.Dispose();
        Assert(entry.IsHidden && !floating.IsShown);
        scroll.InvokeScrollGeometryChanged(Geometry(220));
        Assert(!entry.IsHidden);
    }),
    ("Sam entry: only a bottom inset around a scroll can be bound", () =>
    {
        var floating = new SamFloatingEntry();
        using var page = Views.VStack(Views.Text("Page"));
        Throws<ArgumentException>(() => floating.Bind(page));
    }),
    // Review 2026-11-05: a Card overload taking a header action first bound the Progress card's
    // Status row (and the empty History text) as a header action beside the title.
    ("Word editor: Progress, History, memory aids and resources have no header action; their rows stay in the card", () =>
    {
        using var page = VocabularyWordEditView.Page(WordState(history: false), style, key => key, WordActions());
        var terms = Find(page, "native.vocabulary.edit.section.VocabWordEdit_VocabularyTerms.header")
            ?? throw new Exception("The Vocabulary Terms header is missing.");
        Assert(Find(terms, "native.vocabulary.edit.duplicates") is not null);
        foreach (var key in new[] { "VocabWordEdit_LearningProgress", "VocabWordEdit_HistoryTitle",
                     "VocabWordEdit_EncodingMemoryAids", "VocabWordEdit_ResourceAssociations" })
        {
            Assert(Find(page, $"native.vocabulary.edit.section.{key}.header") is null);
            Assert(Find(page, $"native.vocabulary.edit.section.{key}") is not null);
        }
        // Each row sits beside its card's title, not inside a header stack.
        foreach (var row in new[] { "native.vocabulary.edit.progress.status.row",
                     "native.vocabulary.edit.progress.details.row", "native.vocabulary.edit.progress.review.row",
                     "native.vocabulary.edit.history.empty" })
        {
            var parent = Parent(page, row) ?? throw new Exception($"{row} is missing.");
            Assert(parent.AccessibilityIdentifier?.EndsWith(".header", StringComparison.Ordinal) != true);
            Assert(parent.Children.Any(child => child.AccessibilityIdentifier is { } sibling
                && sibling.StartsWith("native.vocabulary.edit.section.", StringComparison.Ordinal)
                && !sibling.EndsWith(".header", StringComparison.Ordinal)));
        }
        using var withHistory = VocabularyWordEditView.Page(WordState(history: true), style, key => key, WordActions());
        Assert(Find(withHistory, "native.vocabulary.edit.section.VocabWordEdit_HistoryTitle.header") is null);
        Assert(Find(withHistory, "native.vocabulary.edit.history.count") is not null);
    }),
    ("Duplicate manager: encoding badges use the shared rule on the page surface, light and dark", () =>
    {
        var appearance = style.Appearance;
        var light = NativeThemeColorTable.For(appearance.Selection.ThemeId, dark: false);
        var dark = NativeThemeColorTable.For(appearance.Selection.ThemeId, dark: true);
        foreach (var (label, tone) in new[] {
                     ("Strong", VocabularyEncodingTone.Strong), ("Good", VocabularyEncodingTone.Good),
                     ("Basic", VocabularyEncodingTone.Basic), ("Unknown", VocabularyEncodingTone.Basic) })
        {
            var shown = VocabularyDuplicatesView.EncodingColors(appearance, label);
            var l = VocabularyEncodingBadge.Colors(tone, light, light.Body);
            var d = VocabularyEncodingBadge.Colors(tone, dark, dark.Body);
            Assert(shown.Fill == new SourceColorPair(l.Fill, d.Fill));
            Assert(shown.Text == new SourceColorPair(l.Text, d.Text));
        }
        var strong = VocabularyDuplicatesView.EncodingColors(appearance, "Strong");
        var good = VocabularyDuplicatesView.EncodingColors(appearance, "Good");
        var basic = VocabularyDuplicatesView.EncodingColors(appearance, "Basic");
        // Seoul Pop sets --bs-secondary to its success green, so on the web Strong and Basic
        // share a fill there and differ by text colour (text-success, text-secondary-ss).
        Assert(strong.Fill != good.Fill && good.Fill != basic.Fill && strong.Text != basic.Text);
        using var panel = VocabularyDuplicatesView.Panel(DuplicatePanel(), style, key => key,
            new VocabularyDuplicateActions(() => { }, () => { }, _ => { }, _ => { }));
        foreach (var id in new[] { "w-strong", "w-good", "w-basic" })
            Assert(Find(panel, $"native.vocabulary.duplicates.word.{id}.encoding") is not null);
    }),
    // One ownership rule (Captain 2026-11-06): every copy in a group is the learner's own, so a
    // blocked group always shows the source's reason and its "Edit before merge" advice.
    ("Duplicate manager: a blocked group shows the source's reason and edit advice", () =>
    {
        var sourceOnly = DuplicatePanel() with
        {
            Groups = [DuplicatePanel().Groups[0] with
                { CanMergeAutomatically = false, MergeBlockedReason = "Language differs" }]
        };
        using var sourcePanel = VocabularyDuplicatesView.Panel(sourceOnly, style, key => key,
            new VocabularyDuplicateActions(() => { }, () => { }, _ => { }, _ => { }));
        Assert(Find(sourcePanel, "native.vocabulary.duplicates.group.0.merge") is null);
        Assert(Find(sourcePanel, "native.vocabulary.duplicates.group.0.reason")?.Text
            == "Language differs");
        Assert(Find(sourcePanel, "native.vocabulary.duplicates.word.w-good.edit") is not null);
        Assert(Find(sourcePanel, "native.vocabulary.duplicates.group.0.blocked")?.Text
            == "NativeVocabulary_DuplicatesEditBeforeMerge");
        Assert(Find(sourcePanel, "native.vocabulary.duplicates.group.0.help")?.Text
            == "NativeVocabulary_DuplicatesMergeUnavailableHelp");
    }),
    // Review 2026-11-05 (third pass): Delete is not offered for a word the server would keep,
    // and the editor says why; an editable word with nothing blocking shows no note.
    ("Word editor: a word the server would keep has Delete disabled and a note saying why", () =>
    {
        var open = WordState(history: false);
        using var free = VocabularyWordEditView.Page(open, style, key => key, WordActions());
        Assert(Find(free, "native.vocabulary.edit.delete.menu")?.IsEnabled == true);
        Assert(Find(free, "native.vocabulary.edit.delete.blocked") is null);

        var answered = open with { Detail = open.Detail! with {
            DeleteBlock = NativeVocabularyDeleteBlock.UsedByAnswers } };
        using var blockedPage = VocabularyWordEditView.Page(answered, style, key => key, WordActions());
        Assert(Find(blockedPage, "native.vocabulary.edit.delete.menu")?.IsEnabled == false);
        Assert(Find(blockedPage, "native.vocabulary.edit.delete.blocked")?.Text
            == "NativeVocabulary_DeleteUsedByAnswers");
        Assert(Find(blockedPage, "native.vocabulary.edit.readonly") is null);
    }),
    // Captain 2026-11-10 (Duotopia): one Save, the toolbar's primary action, as plain text at
    // every width; no Save in the More menu and no Cancel/Save under the last card.
    ("Word editor: one plain toolbar Save at every width, More keeps Find Duplicates and Delete", () =>
    {
        foreach (var width in new[] { style, wideStyle })
        {
            using var page = VocabularyWordEditView.Page(WordState(history: false), width, key => key, WordActions());
            Assert(page.Kind == "toolbar");
            Assert(page.Children.Skip(1).Select(action => action.AccessibilityIdentifier)
                .SequenceEqual(["native.vocabulary.edit.more", "native.vocabulary.edit.save"]));
            var save = Find(page, "native.vocabulary.edit.save")!;
            // A plain toolbar Button with its title, not a source-styled (filled) button.
            Assert(save.Kind == "button" && save.Text == "Common_Save" && save.Children.Count == 0);
            Assert(save.IsEnabled);
            var more = Find(page, "native.vocabulary.edit.more")!;
            Assert(more.Children.Select(item => item.AccessibilityIdentifier).SequenceEqual(
                ["native.vocabulary.edit.duplicates.menu", "native.vocabulary.edit.delete.menu"]));
            foreach (var gone in new[] { "native.vocabulary.edit.actions", "native.vocabulary.edit.cancel",
                         "native.vocabulary.edit.save.menu", "native.vocabulary.edit.save.toolbar" })
                Assert(Find(page, gone) is null);
        }
        var blank = WordState(history: false);
        using var invalid = VocabularyWordEditView.Page(blank with { Draft = blank.Draft! with { NativeTerm = " " } },
            style, key => key, WordActions());
        Assert(Find(invalid, "native.vocabulary.edit.save")?.IsEnabled == false);
    }),
    // Captain 2026-11-10: the resource list was long. Only the word's resources show, each with
    // Remove; Add to Resource lists the others. A smart resource is shown, never removed or offered.
    ("Word editor: resources show only the word's own, each removable, and Add to Resource offers the rest", () =>
    {
        var open = WordState(history: false);
        var detail = open.Detail! with { AvailableResources = [
            new NativeVocabularyResource("res-a", "Story", "Korean", false),
            new NativeVocabularyResource("res-b", "Daily words", "Korean", false),
            new NativeVocabularyResource("res-s", "Struggling", "Korean", true),
            new NativeVocabularyResource("res-t", "Phrases", "Korean", true)] };
        var state = open with { Detail = detail, Draft = open.Draft! with { ResourceIds = ["res-a", "res-s"] } };
        var toggled = new List<(string, bool)>();
        using var page = VocabularyWordEditView.Page(state, style, key => key,
            WordActions(toggleResource: (id, on) => toggled.Add((id, on))));
        Assert(Find(page, "native.vocabulary.edit.resources.count")?.Text == "VocabWordEdit_SelectedCount");
        Assert(Find(page, "native.vocabulary.edit.resource.res-a") is not null);
        Assert(Find(page, "native.vocabulary.edit.resource.res-s") is not null);
        Assert(Find(page, "native.vocabulary.edit.resource.res-b") is null);
        Assert(Find(page, "native.vocabulary.edit.resource.res-s.remove") is null);
        var remove = Find(page, "native.vocabulary.edit.resource.res-a.remove")!;
        Assert(remove.AccessibilityLabel == "VocabWordEdit_RemoveFromResource");
        remove.InvokeAction();
        var add = Find(page, "native.vocabulary.edit.resources.add")!;
        Assert(add.Kind == "menu");
        Assert(add.Children.Skip(1).Select(item => item.AccessibilityIdentifier)
            .SequenceEqual(["native.vocabulary.edit.resources.add.res-b"]));
        Find(page, "native.vocabulary.edit.resources.add.res-b")!.InvokeAction();
        Assert(toggled.SequenceEqual([("res-a", false), ("res-b", true)]));

        using var none = VocabularyWordEditView.Page(open with { Draft = open.Draft! with { ResourceIds = [] } },
            style, key => key, WordActions());
        Assert(Find(none, "native.vocabulary.edit.resources.empty") is not null);
        Assert(Find(none, "native.vocabulary.edit.resource.res-a") is null);
        Assert(Find(none, "native.vocabulary.edit.resources.add.res-a") is not null);
        // Everything already added: no empty menu (a Menu needs a Button).
        using var all = VocabularyWordEditView.Page(open, style, key => key, WordActions());
        Assert(Find(all, "native.vocabulary.edit.resources.add") is null);
    }),
    // Captain 2026-11-10: the source's Reference Sentences were missing from the editor.
    ("Word editor: Reference Sentences have Generate, the add row, suggestions and sentences core first", () =>
    {
        var open = WordState(history: false);
        var sentences = new[] {
            new NativeVocabularyExample("s1", "병을 열어요.", "I open the bottle.", IsCore: true,
                Source: ExampleSentenceSource.UserAuthored, Register: SpeechRegister.InformalPolite,
                DifficultyLevel: 2),
            new NativeVocabularyExample("s2", "병이 깨졌다.", null, IsFlagged: true,
                Source: ExampleSentenceSource.AiGenerated) };
        var examples = new VocabularyExamplesState(sentences,
            [new VocabularyExampleSuggestion("k1", "물 한 병 주세요.", "A bottle of water, please.",
                SpeechRegister.InformalPolite, 1)],
            string.Empty, string.Empty, SpeechRegister.Unspecified, null, string.Empty, string.Empty,
            SpeechRegister.Unspecified, false, false, "RefSentences_Kept", false);
        var state = open with { Detail = open.Detail! with { Examples = sentences }, Examples = examples };
        var sections = new Dictionary<VocabularyEditSection, SwiftView>();
        using var page = VocabularyWordEditView.Page(state, style, key => key,
            WordActions(registerSection: (section, view) => sections[section] = view));
        Assert(sections.Keys.OrderBy(key => key).SequenceEqual(
            [VocabularyEditSection.Examples, VocabularyEditSection.Resources, VocabularyEditSection.EncodingLabel]));
        var header = Find(page, "native.vocabulary.edit.section.RefSentences_Title.header")!;
        Assert(Find(header, "native.vocabulary.edit.examples.generate")?.IsEnabled == true);
        // Add needs a sentence (source); the empty text is not shown beside sentences.
        Assert(Find(page, "native.vocabulary.edit.examples.add")?.IsEnabled == false);
        Assert(Find(page, "native.vocabulary.edit.examples.empty") is null);
        Assert(Find(page, "native.vocabulary.edit.examples.message")?.AccessibilityAnnouncement == "RefSentences_Kept");
        Assert(Find(page, "native.vocabulary.edit.examples.suggestion.k1.keep")?.IsEnabled == true);
        Assert(Find(page, "native.vocabulary.edit.examples.suggestion.k1.badges.level")?.Text == "RefSentences_Level");
        Assert(Find(page, "native.vocabulary.edit.example.s1.badges.core") is not null);
        Assert(Find(page, "native.vocabulary.edit.example.s1.badges.source")?.Text == "RefSentences_SourceUser");
        Assert(Find(page, "native.vocabulary.edit.example.s1.badges.register")?.Text == "Register_InformalPolite");
        Assert(Find(page, "native.vocabulary.edit.example.s2.badges.flagged") is not null);
        Assert(Find(page, "native.vocabulary.edit.example.s2.native") is null);
        var menu = Find(page, "native.vocabulary.edit.example.s1.actions")!;
        Assert(menu.Kind == "menu" && menu.AccessibilityLabel == "RefSentences_Actions");
        Assert(Find(menu, "native.vocabulary.edit.example.s1.core")?.Text == "RefSentences_UnsetCore");
        Assert(Find(page, "native.vocabulary.edit.example.s2.core")?.Text == "RefSentences_SetCore");
        Assert(Find(page, "native.vocabulary.edit.example.s2.flag")?.Text == "RefSentences_Unflag");

        // Editing one sentence shows its fields in place; Save needs a sentence.
        using var editing = VocabularyWordEditView.Page(state with { Examples = examples with {
                EditingId = "s2", EditTarget = " ", EditNative = string.Empty } },
            style, key => key, WordActions());
        Assert(Find(editing, "native.vocabulary.edit.example.s2.editor.save")?.IsEnabled == false);
        Assert(Find(editing, "native.vocabulary.edit.example.s2.actions") is null);
        Assert(Find(editing, "native.vocabulary.edit.example.s1.actions") is not null);

        // The word's Save holds the sentences too; no sentences and no suggestions say so.
        using var empty = VocabularyWordEditView.Page(open with { Saving = true }, style, key => key, WordActions());
        Assert(Find(empty, "native.vocabulary.edit.examples.empty") is not null);
        Assert(Find(empty, "native.vocabulary.edit.examples.generate")?.IsEnabled == false);
        Assert(Find(empty, "native.vocabulary.edit.examples.new.target")?.IsEnabled == false);
    }),
    // Captain 2026-11-09 (Duotopia, wide): the filters sit in a fixed left column, the counts in
    // one row, the results in their own scroll; Filters and the grid/list switch are toolbar
    // buttons, and sorting is one of the filter options.
    ("Vocabulary wide: a fixed filter column, sort first, beside the results", () =>
    {
        using var page = VocabularyListView.Page(ListState(), wideStyle, key => key, ListActions());
        Assert(page.Kind == "toolbar");
        Assert(page.Children.Skip(1).Select(action => action.AccessibilityIdentifier)
            .SequenceEqual(["native.vocabulary.view.toggle", "native.vocabulary.filters.open"]));
        var filters = Find(page, "native.vocabulary.filters.open")!;
        Assert(filters.AccessibilitySelected && filters.AccessibilityLabel == "Vocabulary_Filters");
        Assert(filters.Children[0].SystemImageName == "line.3.horizontal.decrease.circle");
        var toggle = Find(page, "native.vocabulary.view.toggle")!;
        Assert(toggle.AccessibilityLabel == "Vocabulary_ListView"
            && toggle.Children[0].SystemImageName == "list.bullet");
        var layout = Find(page, "native.vocabulary.wide.layout")!;
        Assert(layout.Kind == "hstack" && layout.Children.Count == 3);
        var panel = layout.Children[0];
        Assert(panel.AccessibilityIdentifier == "native.vocabulary.filters.panel" && panel.Kind == "scroll");
        Assert(panel.FixedWidth == 333 && panel.FixedWidth == VocabularyListView.FilterColumnWidth(1000));
        var panelContent = Find(panel, "native.vocabulary.filters.panel.content")!;
        Assert(panelContent.Children[0].AccessibilityIdentifier == "native.vocabulary.filter.field.sort");
        Assert(Find(panelContent, "native.vocabulary.filter.status") is not null);
        Assert(Find(panelContent, "native.vocabulary.filters.panel.clear") is null);
        Assert(layout.Children[1].Kind == "divider");
        var results = layout.Children[2];
        // Only the search stays put; the counts scroll with the cards (review 2026-11-10: a short
        // wide window with the keyboard up still shows results).
        Assert(results.Children.Select(child => child.AccessibilityIdentifier).SequenceEqual([
            "native.vocabulary.search.row", "native.vocabulary.results.scroll"]));
        var resultsContent = Find(results, "native.vocabulary.results.content")!;
        Assert(resultsContent.Children[0].AccessibilityIdentifier == "native.vocabulary.results.header");
        var stats = Find(results, "native.vocabulary.stats")!;
        Assert(stats.Kind == "horizontalGrid" && stats.Children.Count == 1
            && stats.Children[0].Children.Count == 5);
        foreach (var id in new[] { "total", "unknown", "learning", "familiar", "known" })
            Assert(Find(stats, $"native.vocabulary.stats.{id}") is not null);
        Assert(Find(results, "native.vocabulary.chips") is null);
        var cards = Find(Find(results, "native.vocabulary.results.scroll")!, "native.vocabulary.cards")!;
        Assert(cards.GridColumns == 2 && cards.Children.Count == 3);
    }),
    ("Vocabulary wide: a hidden filter column gives the results its width; chips stay in one row", () =>
    {
        using var page = VocabularyListView.Page(ListState(panelHidden: true), wideStyle, key => key, ListActions());
        Assert(Find(page, "native.vocabulary.filters.panel") is null);
        Assert(!Find(page, "native.vocabulary.filters.open")!.AccessibilitySelected);
        Assert(Find(page, "native.vocabulary.cards")!.GridColumns == 3);
        using var listPage = VocabularyListView.Page(ListState(listMode: true), wideStyle, key => key, ListActions());
        Assert(Find(listPage, "native.vocabulary.cards")!.GridColumns == 1);
        var toggle = Find(listPage, "native.vocabulary.view.toggle")!;
        Assert(toggle.AccessibilityLabel == "Vocabulary_GridView"
            && toggle.Children[0].SystemImageName == "square.grid.3x3");
        using var filtered = VocabularyListView.Page(ListState(query: "status:learning"), wideStyle,
            key => key, ListActions());
        var chips = Find(filtered, "native.vocabulary.chips")!;
        Assert(chips.Kind == "horizontalGrid" && Find(chips, "native.vocabulary.chip.status") is not null);
        Assert(Find(filtered, "native.vocabulary.filters.panel.clear") is not null);
        Assert(Find(filtered, "native.vocabulary.filters.open")!.Children[0].SystemImageName
            == "line.3.horizontal.decrease.circle.fill");
    }),
    ("Vocabulary: the filter column is a third of the window and cards fit the results", () =>
    {
        Assert(VocabularyListView.FilterColumnWidth(768) == 280);
        Assert(VocabularyListView.FilterColumnWidth(1000) == 333);
        Assert(VocabularyListView.FilterColumnWidth(1366) == 400);
        Assert(VocabularyListView.CardColumns(402, 1, false, true, wide: false) == 1);
        Assert(VocabularyListView.CardColumns(768, 1, false, true, wide: true) == 2);
        Assert(VocabularyListView.CardColumns(1000, 1, false, true, wide: true) == 2);
        Assert(VocabularyListView.CardColumns(1000, 1, false, false, wide: true) == 3);
        Assert(VocabularyListView.CardColumns(1000, 1.5, false, true, wide: true) == 1);
        Assert(VocabularyListView.CardColumns(1366, 1, true, true, wide: true) == 1);
        // A page narrowed by an iPad sidebar: one column of cards at 740 points, two at 940.
        Assert(VocabularyListView.CardColumns(740, 1, false, true, wide: true) == 1);
        Assert(VocabularyListView.CardColumns(940, 1, false, true, wide: true) == 2);
        // Larger text widens the column, up to half the window.
        Assert(VocabularyListView.FilterColumnWidth(1000, 1.5) == 500);
        Assert(VocabularyListView.FilterColumnWidth(768, 2) == 384);
        Assert(VocabularyListView.FilterColumnWidth(1366, 1.2) == 480);
        Assert(VocabularyListView.FilterColumnWidth(1000, 0.8) == 333);
    }),
    ("Vocabulary wide: while the list loads, the column switch and Filters wait for the words", () =>
    {
        using var page = VocabularyListView.Page(ListState() with { Loading = true }, wideStyle,
            key => key, ListActions());
        Assert(Find(page, "native.vocabulary.wide.layout") is null);
        Assert(Find(page, "native.vocabulary.status") is not null);
        var filters = Find(page, "native.vocabulary.filters.open")!;
        Assert(!filters.IsEnabled && !filters.AccessibilitySelected);
        Assert(!Find(page, "native.vocabulary.view.toggle")!.IsEnabled);
        // A phone's Filters still opens the sheet while loading.
        using var phone = VocabularyListView.Page(ListState() with { Loading = true }, style,
            key => key, ListActions());
        Assert(Find(phone, "native.vocabulary.filters.open")!.IsEnabled);
    }),
    // Seen on the iPad simulator 2026-11-10: with the sidebar open the page is 940 of 1210
    // points, and sizing from the window squeezed three cards beside a 400-point column.
    ("Vocabulary wide: the column and cards follow the page's own width; it changes in place", () =>
    {
        var actions = ListActions() with { PageWidthChanged = _ => { } };
        using var page = VocabularyListView.Page(ListState(), sidebarStyle, key => key, actions);
        // Before the page has reported its width, the window decides.
        Assert(Find(page, "native.vocabulary.filters.panel")!.FixedWidth == 400);
        Assert(Find(page, "native.vocabulary.cards")!.GridColumns == 3);
        var next = VocabularyListView.Page(ListState(), sidebarStyle, key => key, actions, pageWidth: 940);
        Assert(VocabularyListUpdate.TryUpdateInPlace(page, next));
        next.Dispose();
        Assert(Find(page, "native.vocabulary.filters.panel")!.FixedWidth == 313);
        Assert(Find(page, "native.vocabulary.cards")!.GridColumns == 2);
        // A page the sidebar leaves under 768 points takes the phone layout and its sheet.
        using var narrow = VocabularyListView.Page(ListState(), sidebarStyle, key => key, actions, pageWidth: 514);
        Assert(Find(narrow, "native.vocabulary.wide.layout") is null);
        Assert(Find(narrow, "native.vocabulary.scroll.content") is not null);
        Assert(narrow.Children.Skip(1).Select(action => action.AccessibilityIdentifier)
            .SequenceEqual(["native.vocabulary.filters.open"]));
    }),
    ("Vocabulary phone: one column, Filters opens the sheet, and the sheet starts with sort", () =>
    {
        using var page = VocabularyListView.Page(ListState(), style, key => key, ListActions());
        Assert(page.Children.Skip(1).Select(action => action.AccessibilityIdentifier)
            .SequenceEqual(["native.vocabulary.filters.open"]));
        Assert(!Find(page, "native.vocabulary.filters.open")!.AccessibilitySelected);
        Assert(Find(page, "native.vocabulary.wide.layout") is null);
        Assert(Find(page, "native.vocabulary.stats") is null);
        var content = Find(page, "native.vocabulary.scroll.content")!;
        Assert(content.Children[0].AccessibilityIdentifier == "native.vocabulary.search.row");
        Assert(Find(content, "native.vocabulary.cards")!.GridColumns == 1);
        var state = ListState() with { SheetSortField = "updated", SheetSortDescending = false };
        using var sheet = VocabularyListView.PhoneFilterSheet(state, style, key => key,
            new VocabularyFilterActions((_, _) => { }, _ => { }, () => { }, () => { }, () => { }));
        var panel = Find(sheet, "native.vocabulary.filters.sheet.content")!;
        Assert(panel.Children[0].AccessibilityIdentifier == "native.vocabulary.filters.heading");
        Assert(panel.Children[1].AccessibilityIdentifier == "native.vocabulary.filter.field.sort");
        Assert(Find(panel, "native.vocabulary.sort.updated") is not null);
    }),
    ("Vocabulary: typing updates the page in place and keeps the search field", () =>
    {
        using var page = VocabularyListView.Page(ListState(), wideStyle, Counts, ListActions());
        var search = Find(page, VocabularyListUpdate.SearchId)!;
        var panelContent = Find(page, "native.vocabulary.filters.panel.content")!;
        var stats = Find(page, "native.vocabulary.stats")!;
        var header = Find(page, "native.vocabulary.results.header")!;
        var total = Find(page, "native.vocabulary.stats.total")!;
        Assert(total.Text == "3 of 3");
        var next = VocabularyListView.Page(ListState(query: "바다"), wideStyle, Counts, ListActions());
        Assert(VocabularyListUpdate.TryUpdateInPlace(page, next));
        next.Dispose();
        Assert(ReferenceEquals(Find(page, VocabularyListUpdate.SearchId), search));
        Assert(ReferenceEquals(Find(page, "native.vocabulary.filters.panel.content"), panelContent));
        // The counts row stays mounted (its sideways scroll stays) and shows the new counts.
        Assert(ReferenceEquals(Find(page, "native.vocabulary.stats"), stats));
        Assert(ReferenceEquals(Find(page, "native.vocabulary.results.header"), header));
        Assert(ReferenceEquals(Find(page, "native.vocabulary.stats.total"), total));
        Assert(total.Text == "1 of 3");
        Assert(Find(page, "native.vocabulary.search.clear") is not null);
        Assert(Find(page, "native.vocabulary.cards")!.Children.Count == 1);
        Assert(Find(page, "native.vocabulary.filters.panel.clear") is not null);
        Assert(Find(page, "native.vocabulary.filters.open")!.Children[0].SystemImageName
            == "line.3.horizontal.decrease.circle.fill");

        using var phone = VocabularyListView.Page(ListState(), style, key => key, ListActions());
        var phoneSearch = Find(phone, VocabularyListUpdate.SearchId)!;
        var phoneNext = VocabularyListView.Page(ListState(query: "바다"), style, key => key, ListActions());
        Assert(VocabularyListUpdate.TryUpdateInPlace(phone, phoneNext));
        phoneNext.Dispose();
        Assert(ReferenceEquals(Find(phone, VocabularyListUpdate.SearchId), phoneSearch));
        Assert(Find(phone, "native.vocabulary.cards")!.Children.Count == 1);
    }),
    ("Vocabulary: another layout is mounted anew; the grid/list switch changes in place", () =>
    {
        using var page = VocabularyListView.Page(ListState(), wideStyle, key => key, ListActions());
        var shape = VocabularyListUpdate.Outline(page).Shape;
        var hidden = VocabularyListView.Page(ListState(panelHidden: true), wideStyle, key => key, ListActions());
        Assert(!VocabularyListUpdate.TryUpdateInPlace(page, hidden));
        hidden.Dispose();
        var phone = VocabularyListView.Page(ListState(), style, key => key, ListActions());
        Assert(!VocabularyListUpdate.TryUpdateInPlace(page, phone));
        phone.Dispose();
        Assert(VocabularyListUpdate.Outline(page).Shape == shape);
        var list = VocabularyListView.Page(ListState(listMode: true), wideStyle, key => key, ListActions());
        Assert(VocabularyListUpdate.TryUpdateInPlace(page, list));
        list.Dispose();
        var toggle = Find(page, "native.vocabulary.view.toggle")!;
        Assert(toggle.AccessibilityLabel == "Vocabulary_GridView"
            && toggle.Children[0].SystemImageName == "square.grid.3x3");
        Assert(Find(page, "native.vocabulary.cards")!.GridColumns == 1);
    }),
};

var failures = 0;
foreach (var (name, test) in cases)
{
    try
    {
        test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}");
    }
}
Console.WriteLine($"{cases.Length - failures} passed, {failures} failed.");
return failures == 0 ? 0 : 1;

static RecentLookupRow Row(string id, IReadOnlyList<VocabularyLookupDestination> destinations) =>
    new(id, RecentLookupsAccess.Key(id), "바다", "sea", VocabularyLookupActivity.Reading, "Story",
        new DateTime(2026, 11, 4, 15, 30, 0, DateTimeKind.Utc), destinations);

static VocabularyBrowserList ListState(string query = "", bool listMode = false,
    bool panelHidden = false)
{
    var created = new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);
    var resource = new NativeVocabularyResource("res-a", "Story", "Korean", false);
    NativeVocabularyEntry Word(string id, string target, LearningStatus status, int day) =>
        new(id, target, $"native {id}", LexicalUnitType.Word, null, null, "Korean",
            created.AddDays(day), created.AddDays(day), "Basic", status,
            status == LearningStatus.Known, [resource]);
    NativeVocabularyEntry[] words = [
        Word("w1", "병", LearningStatus.Learning, 0),
        Word("w2", "바다", LearningStatus.Unknown, 1),
        Word("w3", "하늘", LearningStatus.Known, 2)];
    var filters = new VocabularyFilterState(query, _ => { });
    return new VocabularyBrowserList(false, false, null,
        new NativeVocabularyCatalog(true, words, [resource]),
        filters.Filter(words, "added", true), filters, "added", true, listMode, null, false,
        FilterPanelHidden: panelHidden);
}

// The total count's format, so a test can read the counts it shows.
static string Counts(string key) => key == "Vocabulary_StatsTotal" ? "{0} of {1}" : key;

static VocabularyListActions ListActions() => new(
    Search: _ => { }, OpenFilters: () => { }, ToggleFilterPanel: () => { },
    ClearFilters: () => { }, OpenWord: _ => { }, Reload: () => { }, Sort: _ => { },
    ToggleListMode: () => { }, ChangeWideFilter: (_, _) => { });

static VocabularyBrowserEdit WordState(bool history)
{
    var created = new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);
    var resource = new NativeVocabularyResource("res-a", "Story", "Korean", false);
    var draft = new NativeVocabularyEditDraft("word-1", created, "병", "bottle", null, "Korean",
        null, null, null, LexicalUnitType.Word, ["res-a"]);
    var entry = new NativeVocabularyEntry("word-1", "병", "bottle", LexicalUnitType.Word, null, null,
        "Korean", created, created, "Basic", LearningStatus.Learning, false, [resource]);
    var detail = new NativeVocabularyDetail(entry, draft, [resource],
        new NativeVocabularyProgress(LearningStatus.Learning, false, 0.4f, 1.5f, 1, created.AddDays(3), 4),
        history ? [new NativeVocabularyHistory("h-1", "VocabularyQuiz", created, true, false, "Story")] : [],
        [], [], false, NativeVocabularyDeleteBlock.None);
    return new VocabularyBrowserEdit(false, false, detail, draft, null, false, null);
}

static VocabularyEditActions WordActions(
    Action<string, bool>? toggleResource = null,
    Action<VocabularyEditSection, SwiftView>? registerSection = null) => new(
    Change: (_, _) => { }, ToggleResource: toggleResource ?? ((_, _) => { }), Save: () => { },
    Cancel: () => { }, Retry: () => { }, PlayAudio: () => { }, RegisterSave: _ => { },
    FindDuplicates: () => { }, RequestDelete: () => { },
    Examples: new VocabularyExampleActions(
        ChangeNew: (_, _) => { }, SetNewRegister: _ => { }, Add: () => { }, Generate: () => { },
        ChangeSuggestion: (_, _, _) => { }, Keep: _ => { }, Discard: _ => { },
        StartEdit: _ => { }, ChangeEdit: (_, _) => { }, SetEditRegister: _ => { },
        SaveEdit: () => { }, CancelEdit: () => { }, ToggleCore: _ => { }, ToggleFlag: _ => { },
        Delete: _ => { }),
    RegisterSection: registerSection);

static VocabularyDuplicatePanel DuplicatePanel()
{
    static NativeVocabularyDuplicateWord Word(string id, string label) =>
        new(id, "병", "bottle", "Korean", LexicalUnitType.Word, 1, 1, label, null, false, false);
    var group = new NativeVocabularyDuplicateGroup("병", "병",
        [Word("w-strong", "Strong"), Word("w-good", "Good"), Word("w-basic", "Basic")],
        true, null, "w-strong");
    return new VocabularyDuplicatePanel(false, "Duplicates", "Found", null, null, null,
        [group], 1, 2, []);
}

static SwiftView? Parent(SwiftView node, string id)
{
    foreach (var child in node.Children)
    {
        if (child.AccessibilityIdentifier == id)
            return node;
        if (Parent(child, id) is { } match)
            return match;
    }
    return null;
}

static SwiftView? Find(SwiftView node, string id)
{
    if (node.AccessibilityIdentifier == id)
        return node;
    foreach (var child in node.Children)
        if (Find(child, id) is { } match)
            return match;
    return null;
}

static void Assert(bool condition)
{
    if (!condition)
        throw new Exception("Assertion failed.");
}

static void Throws<T>(Action action) where T : Exception
{
    try
    {
        action();
    }
    catch (T)
    {
        return;
    }
    throw new Exception($"Expected {typeof(T).Name}.");
}

// A Dashboard-shaped page: 2000 points of content, 800 points visible inside 100 points of
// top inset and the floating button's 80-point bottom inset (SwiftUI containerSize).
static SwiftScrollGeometry Geometry(double offset) => new(offset, 2000, 800, 100, 80);

static SwiftView FloatingPage(out SwiftView scroll, out SwiftView entry)
{
    scroll = Views.Scroll(Views.VStack(Views.Text("Card")));
    entry = Views.HStack(Views.Button("Ask", () => { }));
    return Views.BottomInset(scroll, entry);
}
