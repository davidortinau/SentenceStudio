using AppKit;
using Microsoft.SwiftUI;
using SentenceStudio.Contracts.Theme;
using SentenceStudio.Data;
using SentenceStudio.Native;
using SentenceStudio.Native.Features.Lookups;
using SentenceStudio.Native.iOS.Features.Lookups;
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
