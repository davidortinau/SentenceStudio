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
    ("Word editor: only Vocabulary Terms has a header action; Progress and History keep their rows", () =>
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
    ("Duplicate manager: a group with another learner's copy shows why it cannot merge", () =>
    {
        var blocked = DuplicatePanel() with
        {
            Groups = [DuplicatePanel().Groups[0] with { CanMergeAutomatically = false, HasAnotherOwnersCopy = true }]
        };
        using var panel = VocabularyDuplicatesView.Panel(blocked, style, key => key,
            new VocabularyDuplicateActions(() => { }, () => { }, _ => { }, _ => { }));
        Assert(Find(panel, "native.vocabulary.duplicates.group.0.merge") is null);
        var reason = Find(panel, "native.vocabulary.duplicates.group.0.reason")
            ?? throw new Exception("The reason is missing.");
        Assert(reason.Text == "NativeVocabulary_DuplicatesAnotherOwner");
        Assert(Find(panel, "native.vocabulary.duplicates.word.w-good.edit") is not null);
        // Review 2026-11-05 (third pass): editing cannot unblock it, so no "Edit before merge"
        // badge and no "Edit record" help.
        Assert(Find(panel, "native.vocabulary.duplicates.group.0.blocked")?.Text
            == "NativeVocabulary_DuplicatesCannotMerge");
        Assert(Find(panel, "native.vocabulary.duplicates.group.0.help") is null);

        // Blocked for both reasons: both show. Blocked by the source only: its advice stays.
        var both = blocked with
        {
            Groups = [blocked.Groups[0] with { MergeBlockedReason = "Language differs" }]
        };
        using var bothPanel = VocabularyDuplicatesView.Panel(both, style, key => key,
            new VocabularyDuplicateActions(() => { }, () => { }, _ => { }, _ => { }));
        Assert(Find(bothPanel, "native.vocabulary.duplicates.group.0.reason")?.Text
            == "NativeVocabulary_DuplicatesAnotherOwner\nLanguage differs");
        var sourceOnly = both with
        {
            Groups = [both.Groups[0] with { HasAnotherOwnersCopy = false }]
        };
        using var sourcePanel = VocabularyDuplicatesView.Panel(sourceOnly, style, key => key,
            new VocabularyDuplicateActions(() => { }, () => { }, _ => { }, _ => { }));
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
        [], [], false, false, NativeVocabularyDeleteBlock.None);
    return new VocabularyBrowserEdit(false, false, detail, draft, null, false, null);
}

static VocabularyEditActions WordActions() => new(
    Change: (_, _) => { }, ToggleResource: (_, _) => { }, Save: () => { }, Cancel: () => { },
    Retry: () => { }, PlayAudio: () => { }, RegisterSave: _ => { }, RegisterMenuSave: _ => { },
    RegisterResourceCount: _ => { }, FindDuplicates: () => { }, RequestDelete: () => { });

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
