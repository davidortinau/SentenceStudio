using AppKit;
using Microsoft.SwiftUI;
using SentenceStudio.Contracts.Theme;
using SentenceStudio.Data;
using SentenceStudio.Native;
using SentenceStudio.Native.Features.Lookups;
using SentenceStudio.Native.iOS.Features.Lookups;
using SentenceStudio.Native.iOS.Features.Sam;
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
