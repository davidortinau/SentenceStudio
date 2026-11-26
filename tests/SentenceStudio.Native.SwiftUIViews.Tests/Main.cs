using System.Runtime.InteropServices;
using AppKit;
using CoreGraphics;
using Microsoft.SwiftUI;
using SentenceStudio.Contracts.Theme;
using SentenceStudio.Data;
using SentenceStudio.Native;
using SentenceStudio.Native.Features.Import;
using SentenceStudio.Native.Features.Lookups;
using SentenceStudio.Native.iOS.Features.Activities;
using SentenceStudio.Native.iOS.Features.Browse;
using SentenceStudio.Native.iOS.Features.Import;
using SentenceStudio.Native.iOS.Features.LearningResources;
using SentenceStudio.Native.iOS.Features.Lookups;
using SentenceStudio.Native.iOS.Features.Quiz;
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
    ("Quiz typed answer: the field's hint is its only instruction (issue 261)", () =>
    {
        var items = new List<SwiftView>();
        QuizQuestionView.Add(items,
            new NativeQuizState(NativePhase.Ready, NativeDestination.VocabularyQuiz,
                Prompt: new NativeQuizPrompt("w-1", "Prompt", "Korean", null, null,
                    IsTargetLanguagePrompt: true, ShowWrittenPrompt: true,
                    CanPlayPromptAudio: false, ShowPhoto: false, TargetLanguageEngaged: true),
                Mode: "Text", CanSubmit: true, TurnId: "t-1"),
            answer: string.Empty, correctionCompleted: false, busy: false, style, Text,
            answerChanged: _ => { }, answerFocusChanged: _ => { }, answerShouldFocus: false,
            selectChoice: _ => { }, playPrompt: () => { }, playChoiceAudio: _ => { },
            submitText: () => { }, submitCorrection: () => { }, openDetails: () => { },
            toggleSentenceHints: () => { }, photoRendered: _ => { },
            photoFailed: (_, _) => { }, openPhoto: () => { }, togglePhotoVisible: () => { });
        using var page = Views.VStack(items.ToArray());
        // The field says what to do; nothing above it says so a second time.
        Assert(!AllText(page).Contains("VocabQuiz_TypeTranslation"));
        var answerIndex = items.FindIndex(item => Find(item, NativeAccess.QuizAnswer) is not null);
        Assert(answerIndex > 0 && items[answerIndex].Kind == "textField");
        // What sits right above the field is the prompt's controls, not a line of text.
        Assert(items[answerIndex - 1].AccessibilityIdentifier == "native.quiz.prompt.actions");
        Assert(Find(page, "native.quiz.submit") is not null);
    }),
    ("Quiz footer: its rows leave while the keyboard is up and return after (issue 254)", () =>
    {
        var footer = Views.VStack(Views.Divider(), Views.Text("1 / 10"));
        using var inset = Views.BottomInset(
            Views.Scroll(Views.VStack(Views.Text("Question"))), footer);
        bool? reported = null;
        QuizKeyboardFooter.Bind(inset, keyboardVisible: false, visible => reported = visible);
        Assert(footer.Children.Count == 2);
        inset.InvokeKeyboardOverlapChanged(291);
        Assert(reported == true);
        Assert(footer.Children.Count == 0);
        inset.InvokeKeyboardOverlapChanged(240);
        Assert(footer.Children.Count == 0);
        inset.InvokeKeyboardOverlapChanged(0);
        Assert(reported == false);
        Assert(footer.Children.Count == 2);
        Assert(footer.Children[0].Kind == "divider" && footer.Children[1].Text == "1 / 10");
    }),
    ("Quiz footer: a page built while the keyboard is up opens without its rows", () =>
    {
        var footer = Views.VStack(Views.Divider(), Views.Text("1 / 10"));
        using var inset = Views.BottomInset(
            Views.Scroll(Views.VStack(Views.Text("Question"))), footer);
        QuizKeyboardFooter.Bind(inset, keyboardVisible: true, _ => { });
        Assert(footer.Children.Count == 0);
        inset.InvokeKeyboardOverlapChanged(0);
        Assert(footer.Children.Count == 2);
        Throws<ArgumentException>(() => QuizKeyboardFooter.Bind(
            Views.BottomInset(Views.Scroll(Views.Text("Question")), Views.Text("1 / 10")),
            false, _ => { }));
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
    // Captain 2026-11-13 (the native form pattern): each card is a Form Section headed with its
    // title; Find Duplicates is the terms Section's last row, and every other row sits in its own
    // Section, not in a header.
    ("Word editor: each card is a titled Section; Find Duplicates is the terms' last row", () =>
    {
        using var page = VocabularyWordEditView.Page(WordState(history: false), style, key => key, WordActions());
        var terms = Parent(page, "native.vocabulary.edit.duplicates")
            ?? throw new Exception("Find Duplicates is missing.");
        Assert(terms.Kind == "section" && terms.Text == "VocabWordEdit_VocabularyTerms");
        Assert(terms.Children[^1].AccessibilityIdentifier == "native.vocabulary.edit.duplicates");
        foreach (var (row, title) in new[] {
                     ("native.vocabulary.edit.progress.status.row", "VocabWordEdit_LearningProgress"),
                     ("native.vocabulary.edit.progress.review.row", "VocabWordEdit_LearningProgress"),
                     ("native.vocabulary.edit.history.empty", "VocabWordEdit_HistoryTitle"),
                     ("native.vocabulary.edit.lemma", "VocabWordEdit_EncodingMemoryAids"),
                     ("native.vocabulary.edit.resources.count", "VocabWordEdit_ResourceAssociations") })
        {
            var section = Parent(page, row) ?? throw new Exception($"{row} is missing.");
            Assert(section.Kind == "section" && section.Text == title);
        }
        using var withHistory = VocabularyWordEditView.Page(WordState(history: true), style, key => key, WordActions());
        Assert(Parent(withHistory, "native.vocabulary.edit.history.count")?.Text == "VocabWordEdit_HistoryTitle");
    }),
    // Captain 2026-11-14: one Merge for every safe group; only the groups that would lose
    // something are listed (with Merge and Skip), and the ones that cannot merge say why.
    ("Duplicate page: Merge all, review groups with Merge and Skip, blocked groups with the reason", () =>
    {
        var panel = DuplicatePanel();
        using var page = VocabularyDuplicatesView.Page(panel, style, key => key,
            new VocabularyDuplicateActions(() => { }, () => { }, _ => { }, _ => { }));
        // The scene swaps the Sections of this Form in place (TrySwapFormSections finds it by ID).
        Assert(page is { Kind: "form", AccessibilityIdentifier: VocabularyDuplicatesView.FormId });
        Assert(Find(page, "native.vocabulary.duplicates.mergeall")?.Text == "Vocabulary_DuplicatesMergeAll");
        Assert(Find(page, "native.vocabulary.duplicates.summary")?.Text == "Vocabulary_DuplicatesSafeSummary");
        var review = VocabularyDuplicatesView.GroupId(panel.Groups[0]);
        // The group is a Section titled by its term; the Section has no ID (SwiftUI would give it
        // to every row in it), so its rows keep their own.
        var section = SectionTitled(page, "병")!;
        Assert(string.IsNullOrEmpty(section.AccessibilityIdentifier));
        Assert(Find(section, $"{review}.differences")?.Text == "Vocabulary_DuplicateDifferenceMeaning");
        Assert(Find(page, $"{review}.merge") is { IsEnabled: true });
        Assert(Find(page, $"{review}.skip") is { IsEnabled: true });
        // The keeper is listed first and marked in words, not color alone.
        var copies = section.Children
            .Where(child => child.AccessibilityIdentifier?.StartsWith("native.vocabulary.duplicates.word.") == true)
            .Select(child => child.AccessibilityIdentifier).ToArray();
        Assert(copies.SequenceEqual(new[] {
            "native.vocabulary.duplicates.word.w-good", "native.vocabulary.duplicates.word.w-strong" }));
        Assert(AllText(Find(page, "native.vocabulary.duplicates.word.w-good.keeper")!).Contains("Vocabulary_DuplicatesKeep"));
        Assert(Find(page, "native.vocabulary.duplicates.word.w-strong.keeper") is null);
        var blocked = VocabularyDuplicatesView.GroupId(panel.Groups[1]);
        Assert(Find(page, $"{blocked}.reason")?.Text == "Vocabulary_DuplicatesOthersShared");
        // With a copy to keep, Merge still moves the learner's history; with none it is not offered.
        Assert(Find(page, $"{blocked}.merge") is { IsEnabled: true });
        using var noKeeper = VocabularyDuplicatesView.Page(panel with { Groups = [panel.Groups[1] with {
                RecommendedKeeperId = "", MergeBlockedReason = "Vocabulary_DuplicatesAllShared" }] },
            style, key => key, new VocabularyDuplicateActions(() => { }, () => { }, _ => { }, _ => { }));
        Assert(Find(noKeeper, $"{blocked}.reason")?.Text == "Vocabulary_DuplicatesAllShared");
        Assert(Find(noKeeper, $"{blocked}.merge") is null);
        // A safe group loaded with the others is merged by Merge all, not listed.
        Assert(SectionTitled(page, "물") is null);
        Assert(Find(page, $"{VocabularyDuplicatesView.GroupId(panel.Groups[2])}.term") is null);
    }),
    // Captain 2026-11-14: "merge some matching duplicates but not all" and "go directly to a
    // vocabulary word so that I can modify the entry".
    ("Duplicate page: three copies are each a choice, the keeper follows it, and Edit opens a copy", () =>
    {
        static NativeVocabularyDuplicateWord Copy(string id, string meaning) =>
            new(id, "약", meaning, "Korean", LexicalUnitType.Word, 1, 1, "Basic", null, false, false);
        var group = new NativeVocabularyDuplicateGroup("약", "약",
            [Copy("y-1", "approximately"), Copy("y-2", "medicine"), Copy("y-3", "drug")],
            false, null, "y-1", VocabularyDuplicateRisk.Review, [VocabularyDuplicateDifference.Meaning]);
        var edited = new List<string>();
        var toggled = new List<(string, string)>();
        var kept = new List<(string, string)>();
        var actions = new VocabularyDuplicateActions(() => { }, () => { }, _ => { }, _ => { },
            (term, id) => toggled.Add((term, id)), id => edited.Add(id), (term, id) => kept.Add((term, id)));
        var panel = new VocabularyDuplicatePanel(false, "Duplicates", "Found", null, null, null,
            [group], 1, 2, [], ReviewGroupCount: 1);
        var id = VocabularyDuplicatesView.GroupId(group);
        using var page = VocabularyDuplicatesView.Page(panel, style, key => key, actions);
        Assert(Find(page, $"{id}.hint")?.Text == "Vocabulary_DuplicatesChooseHint");
        var choose = Find(page, "native.vocabulary.duplicates.word.y-3.choose")!;
        Assert(choose.AccessibilitySelected && choose.Kind == "button");
        // Identical copies read the same, so the label also says which one.
        Assert(choose.AccessibilityLabel!.Contains("Vocabulary_DuplicatesCopyPosition"));
        Assert(Find(page, "native.vocabulary.duplicates.word.y-2.edit")!.AccessibilityLabel!
            .Contains("Vocabulary_DuplicatesCopyPosition"));
        choose.InvokeAction();
        Assert(toggled.SequenceEqual(new[] { ("약", "y-3") }));
        Find(page, "native.vocabulary.duplicates.word.y-2.edit")!.InvokeAction();
        Assert(edited.SequenceEqual(new[] { "y-2" }));
        Assert(Find(page, "native.vocabulary.duplicates.word.y-1.keeper") is not null);
        // Keep This on every copy but the keeper (Captain 2026-11-14, 빠르다).
        Assert(Find(page, "native.vocabulary.duplicates.word.y-1.makekeeper") is null);
        Find(page, "native.vocabulary.duplicates.word.y-3.makekeeper")!.InvokeAction();
        Assert(kept.SequenceEqual(new[] { ("약", "y-3") }));
        using var chosen = VocabularyDuplicatesView.Page(panel with {
                ChosenKeepers = new Dictionary<string, string> { ["약"] = "y-3" } },
            style, key => key, actions);
        Assert(Find(chosen, "native.vocabulary.duplicates.word.y-3.keeper") is not null);
        Assert(Find(chosen, "native.vocabulary.duplicates.word.y-1.makekeeper") is not null);

        // The keeper left out: the first chosen copy keeps; one copy chosen cannot merge.
        using var without = VocabularyDuplicatesView.Page(panel with { Excluded = new HashSet<string> { "y-1" } },
            style, key => key, actions);
        Assert(!Find(without, "native.vocabulary.duplicates.word.y-1.choose")!.AccessibilitySelected);
        Assert(Find(without, "native.vocabulary.duplicates.word.y-2.keeper") is not null);
        Assert(Find(without, "native.vocabulary.duplicates.word.y-1.keeper") is null);
        Assert(Find(without, $"{id}.merge") is { IsEnabled: true });
        using var single = VocabularyDuplicatesView.Page(panel with { Excluded = new HashSet<string> { "y-1", "y-3" } },
            style, key => key, actions);
        Assert(Find(single, $"{id}.merge") is { IsEnabled: false });

        // Two copies offer no choice, but each can still be edited.
        using var pair = VocabularyDuplicatesView.Page(DuplicatePanel(), style, key => key, actions);
        Assert(Find(pair, "native.vocabulary.duplicates.word.w-good.choose") is null);
        Assert(Find(pair, "native.vocabulary.duplicates.word.w-good.edit") is not null);
    }),
    // docs/duplicate-merge-ai-plan.md: Resolve with AI, its report with Undo, the AI's suggestion
    // on a group left for the learner, and Keep Apart.
    ("Duplicate page: Resolve with AI, the AI report with Undo, a suggestion and Keep Apart", () =>
    {
        var resolved = 0;
        var undone = new List<string>();
        var apart = new List<string>();
        var actions = new VocabularyDuplicateActions(() => { }, () => { }, _ => { }, _ => { },
            ResolveWithAi: () => resolved++, UndoDecision: undone.Add, KeepApart: apart.Add);
        var panel = DuplicatePanel();
        using var page = VocabularyDuplicatesView.Page(panel, style, key => key, actions);
        // Merge all is the prominent button, so Resolve with AI is bordered beside it.
        Assert(Find(page, "native.vocabulary.duplicates.resolveai")?.Text == "Vocabulary_DuplicatesResolveWithAi");
        Find(page, "native.vocabulary.duplicates.resolveai")!.InvokeAction();
        Assert(resolved == 1);
        var review = VocabularyDuplicatesView.GroupId(panel.Groups[0]);
        Find(page, $"{review}.keepapart")!.InvokeAction();
        Assert(apart.SequenceEqual(new[] { "병" }));
        Assert(SectionTitled(page, "Vocabulary_DuplicatesAiReport") is null);

        var merged = new NativeVocabularyDuplicateDecision("dd:1", VocabularyDuplicateDecisionKind.Merged, "소리",
            ["sound; noise"], VocabularyDuplicateDecider.Ai, "same meaning");
        var apartDecision = new NativeVocabularyDuplicateDecision("dd:2", VocabularyDuplicateDecisionKind.KeptApart, "달",
            ["month", "moon (in the sky)"], VocabularyDuplicateDecider.Learner, null);
        using var report = VocabularyDuplicatesView.Page(panel with {
                Decisions = [merged, apartDecision],
                AiSuggestions = new Dictionary<string, NativeVocabularyDuplicateAiSuggestion> {
                    ["병"] = new("병", true, SentenceStudio.Services.VocabularyDuplicateAiVerdict.SameWord, ["bottle"], "same") } },
            style, key => key, actions);
        Assert(SectionTitled(report, "Vocabulary_DuplicatesAiReport") is not null);
        var first = VocabularyDuplicatesView.DecisionId(merged);
        Assert(first == "native.vocabulary.duplicates.decision.dd%3A1");
        Assert(Find(report, $"{first}.text")?.Text == "Vocabulary_DuplicatesAiMerged");
        Assert(Find(report, $"{first}.reason")?.Text == "same meaning");
        var undo = Find(report, $"{first}.undo")!;
        Assert(undo.Kind == "button" && undo.AccessibilityLabel!.Contains("소리"));
        undo.InvokeAction();
        Assert(undone.SequenceEqual(new[] { "dd:1" }));
        // The learner's own Keep Apart is listed with its Undo, and has no reason.
        var second = VocabularyDuplicatesView.DecisionId(apartDecision);
        Assert(Find(report, $"{second}.text")?.Text == "Vocabulary_DuplicatesAiKeptApart");
        Assert(Find(report, $"{second}.reason") is null);
        Assert(Find(report, $"{second}.undo") is not null);
        Assert(AllText(Find(report, $"{review}.suggestion")!).Contains("Vocabulary_DuplicatesAiSuggestsMerge"));

        // A copy left out of a three-copy group: Keep Apart waits until every copy is back in.
        static NativeVocabularyDuplicateWord Yak(string id, string meaning) =>
            new(id, "약", meaning, "Korean", LexicalUnitType.Word, 1, 1, "Basic", null, false, false);
        var three = new NativeVocabularyDuplicateGroup("약", "약", [Yak("y-1", "approximately"), Yak("y-2", "medicine"),
            Yak("y-3", "drug")], false, null, "y-1", VocabularyDuplicateRisk.Review, [VocabularyDuplicateDifference.Meaning]);
        var threePanel = new VocabularyDuplicatePanel(false, "Duplicates", "Found", null, null, null, [three], 1, 2, [],
            ReviewGroupCount: 1);
        var threeId = VocabularyDuplicatesView.GroupId(three);
        using var allIn = VocabularyDuplicatesView.Page(threePanel, style, key => key, actions);
        Assert(Find(allIn, $"{threeId}.keepapart") is { IsEnabled: true });
        using var oneOut = VocabularyDuplicatesView.Page(threePanel with { Excluded = new HashSet<string> { "y-3" } },
            style, key => key, actions);
        Assert(Find(oneOut, $"{threeId}.keepapart") is { IsEnabled: false });

        // Nothing to review, or a page focused on one word: no Resolve with AI.
        using var focused = VocabularyDuplicatesView.Page(panel with { FocusLabel = "병" }, style, key => key, actions);
        Assert(Find(focused, "native.vocabulary.duplicates.resolveai") is null);
        using var running = VocabularyDuplicatesView.Page(panel with { Running = true }, style, key => key, actions);
        Assert(Find(running, "native.vocabulary.duplicates.resolveai") is null);
        Assert(Find(running, $"{review}.keepapart") is { IsEnabled: false });
    }),
    ("Duplicate page: while a merge runs it shows progress and its buttons wait", () =>
    {
        var running = DuplicatePanel() with { Running = true, Status = "Vocabulary_DuplicatesMerging" };
        using var page = VocabularyDuplicatesView.Page(running, style, key => key,
            new VocabularyDuplicateActions(() => { }, () => { }, _ => { }, _ => { }));
        Assert(Find(page, "native.vocabulary.duplicates.mergeall") is null);
        var review = VocabularyDuplicatesView.GroupId(running.Groups[0]);
        Assert(Find(page, $"{review}.merge") is { IsEnabled: false });
        Assert(Find(page, $"{review}.skip") is { IsEnabled: false });
        // Skipped, the group leaves the page.
        using var skipped = VocabularyDuplicatesView.Page(DuplicatePanel() with
            { Skipped = new HashSet<string> { "병" } }, style, key => key,
            new VocabularyDuplicateActions(() => { }, () => { }, _ => { }, _ => { }));
        Assert(Find(skipped, $"{review}.skip") is null);
        Assert(Find(skipped, "native.vocabulary.duplicates.review.note") is null);
    }),
    // Review 2026-11-05 (third pass): Delete is not offered for a word the server would keep,
    // and the editor says why; an editable word with nothing blocking shows no note.
    ("Word editor: a word the server would keep has Delete disabled and a note saying why", () =>
    {
        var open = WordState(history: false);
        using var free = VocabularyWordEditView.Page(open, style, key => key, WordActions());
        Assert(Find(free, "native.vocabulary.edit.delete.request")?.IsEnabled == true);
        Assert(Find(free, "native.vocabulary.edit.delete.blocked") is null);

        var answered = open with { Detail = open.Detail! with {
            DeleteBlock = NativeVocabularyDeleteBlock.UsedByAnswers } };
        using var blockedPage = VocabularyWordEditView.Page(answered, style, key => key, WordActions());
        Assert(Find(blockedPage, "native.vocabulary.edit.delete.request")?.IsEnabled == false);
        Assert(Find(blockedPage, "native.vocabulary.edit.delete.blocked")?.Text
            == "NativeVocabulary_DeleteUsedByAnswers");
        Assert(Find(blockedPage, "native.vocabulary.edit.readonly") is null);
    }),
    // Captain 2026-11-10 (Duotopia): one Save, the toolbar's primary action, as plain text at
    // every width; no Cancel/Save under the last card. Captain 2026-11-13: Delete is a trash
    // icon beside Save and Find Duplicates a row, so there is no More menu.
    ("Word editor: one plain toolbar Save at every width, beside the Delete icon", () =>
    {
        foreach (var width in new[] { style, wideStyle })
        {
            using var page = VocabularyWordEditView.Page(WordState(history: false), width, key => key, WordActions());
            Assert(page.Kind == "toolbar");
            Assert(page.Children.Skip(1).Select(action => action.AccessibilityIdentifier)
                .SequenceEqual(["native.vocabulary.edit.delete.request", "native.vocabulary.edit.save"]));
            var save = Find(page, "native.vocabulary.edit.save")!;
            // A plain toolbar Button with its title, not a source-styled (filled) button.
            Assert(save.Kind == "button" && save.Text == "Common_Save" && save.Children.Count == 0);
            Assert(save.IsEnabled);
            var delete = Find(page, "native.vocabulary.edit.delete.request")!;
            Assert(delete.Children[0].Text == "Common_Delete" && delete.Children[0].SystemImageName == "trash");
            Assert(Find(page, "native.vocabulary.edit.duplicates") is not null);
            foreach (var gone in new[] { "native.vocabulary.edit.actions", "native.vocabulary.edit.cancel",
                         "native.vocabulary.edit.more", "native.vocabulary.edit.save.menu",
                         "native.vocabulary.edit.save.toolbar" })
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
        Assert(Parent(page, "native.vocabulary.edit.examples.generate")?.Text == "RefSentences_Title");
        Assert(Find(page, "native.vocabulary.edit.examples.generate")?.IsEnabled == true);
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
    // Issue 287: the actions menu was fixed at 44 points because a menu's label filled the width
    // it was offered. Since bridge 0.1.0-preview.68 the menu asks for its label's own width; the
    // label is the symbol, and the padding around it is what makes the target.
    ("Word editor: a sentence's actions menu keeps its label's own width, not a fixed one", () =>
    {
        var open = WordState(history: false);
        var sentences = new[] { new NativeVocabularyExample("s1", "병을 열어요.", "I open the bottle.") };
        var examples = new VocabularyExamplesState(sentences, [],
            string.Empty, string.Empty, SpeechRegister.Unspecified, null, string.Empty, string.Empty,
            SpeechRegister.Unspecified, false, false, null, false);
        using var page = VocabularyWordEditView.Page(
            open with { Detail = open.Detail! with { Examples = sentences }, Examples = examples },
            style, key => key, WordActions());
        var menu = Find(page, "native.vocabulary.edit.example.s1.actions")!;
        Assert(menu.Kind == "menu" && menu.AccessibilityLabel == "RefSentences_Actions");
        Assert(menu.MenuLabelFitsContent);
        Assert(menu.FixedWidth is null);
        var label = menu.Children[0];
        Assert(label.Kind == "image" && label.SystemImageName == "ellipsis.circle");
        // The label sets no width of its own either: it is as wide as the symbol and its padding.
        Assert(label.FixedWidth is null);
        // The menu's actions are still there after the label, the destructive one last.
        Assert(menu.Children.Skip(1).Where(action => action.Kind == "button")
            .Select(action => action.AccessibilityIdentifier).SequenceEqual([
                "native.vocabulary.edit.example.s1.core", "native.vocabulary.edit.example.s1.edit",
                "native.vocabulary.edit.example.s1.flag", "native.vocabulary.edit.example.s1.delete"]));
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
        Assert(filters.AccessibilitySelected && filters.Children[0].Text == "Vocabulary_Filters");
        Assert(filters.Children[0].SystemImageName == "line.3.horizontal.decrease.circle");
        var toggle = Find(page, "native.vocabulary.view.toggle")!;
        Assert(toggle.Children[0].Text == "Vocabulary_ListView"
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
        foreach (var id in new[] { "total", "unknown", "learning", "known" })
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
        // List mode is a native List of rows (Captain 2026-11-12), the header its first row.
        Assert(Find(listPage, "native.vocabulary.results.content")!.Kind == "list");
        Assert(Find(listPage, "native.vocabulary.cards") is null);
        var toggle = Find(listPage, "native.vocabulary.view.toggle")!;
        Assert(toggle.Children[0].Text == "Vocabulary_GridView"
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
        Assert(BrowserPageUpdate.Vocabulary.TryUpdateInPlace(page, next));
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
        // Filters is a native menu on a phone; search is the system field in the bottom bar.
        Assert(Find(page, "native.vocabulary.filters.open")!.Kind == "menu");
        Assert(Find(page, "native.vocabulary.wide.layout") is null);
        Assert(Find(page, "native.vocabulary.stats") is null);
        Assert(Find(page, "native.vocabulary.search")!.Kind == "searchable");
        var content = Find(page, "native.vocabulary.scroll.content")!;
        Assert(content.Kind == "list" && content.Children.Count == 3);
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
        var search = Find(page, BrowserPageUpdate.Vocabulary.SearchId)!;
        var panelContent = Find(page, "native.vocabulary.filters.panel.content")!;
        var stats = Find(page, "native.vocabulary.stats")!;
        var header = Find(page, "native.vocabulary.results.header")!;
        var total = Find(page, "native.vocabulary.stats.total")!;
        Assert(total.Text == "3 of 3");
        var next = VocabularyListView.Page(ListState(query: "바다"), wideStyle, Counts, ListActions());
        Assert(BrowserPageUpdate.Vocabulary.TryUpdateInPlace(page, next));
        next.Dispose();
        Assert(ReferenceEquals(Find(page, BrowserPageUpdate.Vocabulary.SearchId), search));
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
        var phoneSearch = Find(phone, BrowserPageUpdate.Vocabulary.SearchId)!;
        var phoneNext = VocabularyListView.Page(ListState(query: "바다"), style, key => key, ListActions());
        Assert(BrowserPageUpdate.Vocabulary.TryUpdateInPlace(phone, phoneNext));
        phoneNext.Dispose();
        Assert(ReferenceEquals(Find(phone, BrowserPageUpdate.Vocabulary.SearchId), phoneSearch));
        Assert(Find(phone, "native.vocabulary.scroll.content")!.Children.Count == 1);
    }),
    // Duotopia 2026-11-13: one Korean jamo matches no words; the List gave way to a plain
    // column, the page under the field was mounted anew and the keyboard closed on every key.
    ("Vocabulary: a search that matches no words keeps the List and updates in place", () =>
    {
        using var phone = VocabularyListView.Page(ListState(), style, key => key, ListActions());
        var search = Find(phone, BrowserPageUpdate.Vocabulary.SearchId)!;
        var none = VocabularyListView.Page(ListState(query: "ㅂ"), style, key => key, ListActions());
        Assert(BrowserPageUpdate.Vocabulary.TryUpdateInPlace(phone, none));
        none.Dispose();
        Assert(ReferenceEquals(Find(phone, BrowserPageUpdate.Vocabulary.SearchId), search));
        Assert(Find(phone, "native.vocabulary.status") is not null);
        var back = VocabularyListView.Page(ListState(query: "바다"), style, key => key, ListActions());
        Assert(BrowserPageUpdate.Vocabulary.TryUpdateInPlace(phone, back));
        back.Dispose();
        Assert(ReferenceEquals(Find(phone, BrowserPageUpdate.Vocabulary.SearchId), search));
        Assert(Find(phone, "native.vocabulary.status") is null);

        // A reload while typing (a sync, the word editor saving) keeps the List too.
        var loading = VocabularyListView.Page(ListState(query: "바다") with { Loading = true }, style,
            key => key, ListActions());
        Assert(BrowserPageUpdate.Vocabulary.TryUpdateInPlace(phone, loading));
        loading.Dispose();
        Assert(ReferenceEquals(Find(phone, BrowserPageUpdate.Vocabulary.SearchId), search));
        Assert(Find(phone, "native.vocabulary.status") is not null);

        using var wide = VocabularyListView.Page(ListState(listMode: true), wideStyle, key => key, ListActions());
        var wideSearch = Find(wide, BrowserPageUpdate.Vocabulary.SearchId)!;
        var wideNone = VocabularyListView.Page(ListState(query: "ㅂ", listMode: true), wideStyle,
            key => key, ListActions());
        Assert(BrowserPageUpdate.Vocabulary.TryUpdateInPlace(wide, wideNone));
        wideNone.Dispose();
        Assert(ReferenceEquals(Find(wide, BrowserPageUpdate.Vocabulary.SearchId), wideSearch));
        Assert(Find(wide, "native.vocabulary.status") is not null);
    }),
    ("Vocabulary: another layout is mounted anew, as is the switch between the grid and the List", () =>
    {
        using var page = VocabularyListView.Page(ListState(), wideStyle, key => key, ListActions());
        var shape = BrowserPageUpdate.Vocabulary.Outline(page).Shape;
        var hidden = VocabularyListView.Page(ListState(panelHidden: true), wideStyle, key => key, ListActions());
        Assert(!BrowserPageUpdate.Vocabulary.TryUpdateInPlace(page, hidden));
        hidden.Dispose();
        var phone = VocabularyListView.Page(ListState(), style, key => key, ListActions());
        Assert(!BrowserPageUpdate.Vocabulary.TryUpdateInPlace(page, phone));
        phone.Dispose();
        Assert(BrowserPageUpdate.Vocabulary.Outline(page).Shape == shape);
        // The grid and the native List (Captain 2026-11-12) are different outlines.
        using var list = VocabularyListView.Page(ListState(listMode: true), wideStyle, key => key, ListActions());
        Assert(!BrowserPageUpdate.Vocabulary.TryUpdateInPlace(page, list));
        var toggle = Find(list, "native.vocabulary.view.toggle")!;
        Assert(toggle.Children[0].Text == "Vocabulary_GridView"
            && toggle.Children[0].SystemImageName == "square.grid.3x3");
    }),
    // Captain 2026-11-10: every Library browse page has the same plain Add (+), last in its
    // toolbar; the Vocabulary one opens the source's add-word form.
    ("Vocabulary: the toolbar ends with the plain Add (+); an empty page offers its first word", () =>
    {
        var added = 0;
        var actions = ListActions() with { AddWord = () => added++ };
        using var wide = VocabularyListView.Page(ListState(), wideStyle, key => key, actions);
        Assert(wide.Children.Skip(1).Select(action => action.AccessibilityIdentifier).SequenceEqual([
            "native.vocabulary.view.toggle", "native.vocabulary.filters.open", "native.vocabulary.add"]));
        var add = Find(wide, "native.vocabulary.add")!;
        Assert(add.Kind == "button" && add.IsEnabled && add.Children[0].Text == "Vocabulary_Add"
            && add.Children[0].SystemImageName == "plus");
        using var phone = VocabularyListView.Page(ListState(), style, key => key, actions);
        Assert(phone.Children.Skip(1).Select(action => action.AccessibilityIdentifier).SequenceEqual([
            "native.vocabulary.filters.open", "native.vocabulary.add"]));
        var none = ListState() with { Catalog = new NativeVocabularyCatalog(true, [], []), FilteredWords = [] };
        using var empty = VocabularyListView.Page(none, style, key => key, actions);
        Assert(Find(empty, "native.vocabulary.empty.add") is not null);
        using var blocked = VocabularyListView.Page(ListState() with { Blocked = true }, style, key => key, actions);
        Assert(!Find(blocked, "native.vocabulary.add")!.IsEnabled);
    }),
    ("Vocabulary new word: one Add, no More menu, sentences, progress or history; every resource to tick", () =>
    {
        var open = WordState(history: true);
        var created = new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);
        var story = new NativeVocabularyResource("res-a", "Story", "Korean", false);
        var smart = new NativeVocabularyResource("res-s", "Struggling", "Korean", true);
        var draft = new NativeVocabularyEditDraft(string.Empty, created, string.Empty, string.Empty, null,
            "Korean", null, null, null, LexicalUnitType.Word, []);
        var state = open with { Creating = true, Draft = draft,
            Detail = open.Detail! with { Word = open.Detail.Word with { Id = string.Empty },
                AvailableResources = [story, smart], Progress = null, History = [] } };
        var toggled = new List<(string, bool)>();
        using var page = VocabularyWordEditView.Page(state, style, key => key,
            WordActions(toggleResource: (id, on) => toggled.Add((id, on))));
        Assert(page.Children.Skip(1).Select(action => action.AccessibilityIdentifier)
            .SequenceEqual(["native.vocabulary.edit.save"]));
        var save = Find(page, "native.vocabulary.edit.save")!;
        Assert(save.Text == "Common_Add" && !save.IsEnabled);
        foreach (var id in new[] { "native.vocabulary.edit.more", "native.vocabulary.edit.duplicates",
                     "native.vocabulary.edit.encoding", "native.vocabulary.edit.slot.Examples",
                     "native.vocabulary.edit.progress.status.row",
                     "native.vocabulary.edit.history.empty", "native.vocabulary.edit.delete.request",
                     "native.vocabulary.edit.resources.add" })
            Assert(Find(page, id) is null);
        var row = Find(page, "native.vocabulary.edit.resource.res-a")!;
        Assert(row.Kind == "toggle" && row.IsEnabled);
        Assert(!Find(page, "native.vocabulary.edit.resource.res-s")!.IsEnabled);
    }),
    ("Resources phone: one column, the shared toolbar icons and no button group", () =>
    {
        using var page = LearningResourcesView.Page(Resources(), null, style, key => key, ResourceActions());
        Assert(page.Kind == "toolbar");
        Assert(page.Children.Skip(1).Select(action => action.AccessibilityIdentifier).SequenceEqual([
            "native.resources.view.toggle", "native.resources.filters.open", "native.resources.add"]));
        var add = Find(page, "native.resources.add")!;
        Assert(add.Children[0].Text == "Resources_AddResource" && add.Children[0].SystemImageName == "plus");
        Assert(Find(page, "native.resources.view.toggle")!.Children[0].SystemImageName == "list.bullet");
        // The system search field wraps the page (bottom toolbar); the count is the title's
        // subtitle, not a row.
        Assert(Find(page, "native.resources.search")!.Kind == "searchable");
        Assert(Find(page, "native.resources.count") is null);
        Assert(Find(page, "native.resources.grid")!.GridColumns == 1);
        Assert(Find(page, "native.resources.view.mode") is null);
        Assert(Find(page, "native.resources.filters.panel") is null);
        using var list = LearningResourcesView.Page(Resources() with { ViewMode = NativeResourceViewMode.List },
            null, style, key => key, ResourceActions());
        // List mode is a native List of swipe rows.
        var listContent = Find(list, "native.resources.scroll.content")!;
        Assert(listContent.Kind == "list" && listContent.Children.Any(row => row.Kind == "swipeRow"));
        Assert(Find(list, "native.resources.view.toggle")!.Children[0].SystemImageName == "square.grid.3x3");
    }),
    ("Resources wide: the Vocabulary filter column, sort first, beside the results", () =>
    {
        using var page = LearningResourcesView.Page(Resources(), null, wideStyle, key => key, ResourceActions());
        var layout = Find(page, "native.resources.wide.layout")!;
        Assert(layout.Kind == "hstack" && layout.Children.Count == 3);
        var panel = layout.Children[0];
        Assert(panel.AccessibilityIdentifier == "native.resources.filters.panel" && panel.FixedWidth == 333);
        var fields = Find(panel, "native.resources.filters.panel.content")!.Children
            .Select(child => child.AccessibilityIdentifier).ToArray();
        Assert(fields.SequenceEqual(["native.resources.filter.field.sort",
            "native.resources.filter.field.type", "native.resources.filter.field.language"]));
        Assert(Find(panel, NativeAccess.ResourceType) is not null && Find(panel, NativeAccess.ResourceLanguage) is not null);
        Assert(AllText(Find(panel, "native.resources.sort.updated")!).Contains('↓'));
        Assert(Find(page, "native.resources.filters.open")!.AccessibilitySelected);
        var results = layout.Children[2];
        Assert(results.Children.Select(child => child.AccessibilityIdentifier).SequenceEqual([
            "native.resources.search.row", "native.resources.results.scroll"]));
        Assert(Find(results, "native.resources.results.header") is null);
        Assert(Find(results, "native.resources.grid")!.GridColumns == 2);
        using var filtered = LearningResourcesView.Page(Resources() with { MediaType = "Video", FilterPanelHidden = true },
            null, wideStyle, key => key, ResourceActions());
        Assert(Find(filtered, "native.resources.filters.panel") is null);
        // Resource cards keep their titles: two across in 1000 points, three from about 1030.
        Assert(Find(filtered, "native.resources.grid")!.GridColumns == 2);
        Assert(LearningResourcesView.GridColumns(1210, 1, filterPanelShown: false) == 3);
        Assert(LearningResourcesView.GridColumns(1210, 1, filterPanelShown: true) == 2);
        Assert(LearningResourcesView.GridColumns(820, 1, filterPanelShown: true) == 1);
        Assert(LearningResourcesView.GridColumns(402, 1, filterPanelShown: true) == 1);
        Assert(Find(filtered, "native.resources.search.clear") is not null);
        Assert(Find(filtered, "native.resources.filters.open")!.Children[0].SystemImageName
            == "line.3.horizontal.decrease.circle.fill");
    }),
    ("Resources: typing and filtering update the mounted page; the sheet holds sort, type and language", () =>
    {
        using var page = LearningResourcesView.Page(Resources(), null, style, key => key, ResourceActions());
        var search = Find(page, BrowserPageUpdate.Resources.SearchId)!;
        var next = LearningResourcesView.Page(Resources(searchText: "lesson"), null, style, key => key, ResourceActions());
        Assert(BrowserPageUpdate.Resources.TryUpdateInPlace(page, next));
        next.Dispose();
        Assert(ReferenceEquals(Find(page, BrowserPageUpdate.Resources.SearchId), search));
        Assert(search.Kind == "searchable");
        // No matches: the count leaves and the empty page shows, still in place.
        var none = LearningResourcesView.Page(Resources() with { Phase = NativeResourceListPhase.NoMatches, Items = [] },
            null, style, key => key, ResourceActions());
        Assert(BrowserPageUpdate.Resources.TryUpdateInPlace(page, none));
        none.Dispose();
        Assert(ReferenceEquals(Find(page, BrowserPageUpdate.Resources.SearchId), search));
        Assert(Find(page, "native.resources.count") is null && Find(page, "native.resources.empty.add") is not null);
        // List mode keeps its List with no matches, the source's empty page one row; wide too,
        // whatever its first row was (an unconfirmed sync shows no status row above the results).
        foreach (var (pageStyle, sync) in new[] { (style, NativeResourceSyncPhase.Confirmed),
                     (wideStyle, NativeResourceSyncPhase.Confirmed), (wideStyle, NativeResourceSyncPhase.Unconfirmed) })
        {
            var listMode = Resources() with { ViewMode = NativeResourceViewMode.List, Sync = sync };
            using var list = LearningResourcesView.Page(listMode, null, pageStyle, key => key, ResourceActions());
            var listSearch = Find(list, BrowserPageUpdate.Resources.SearchId)!;
            var listNone = LearningResourcesView.Page(
                listMode with { Phase = NativeResourceListPhase.NoMatches, Items = [] },
                null, pageStyle, key => key, ResourceActions());
            Assert(BrowserPageUpdate.Resources.TryUpdateInPlace(list, listNone));
            listNone.Dispose();
            Assert(ReferenceEquals(Find(list, BrowserPageUpdate.Resources.SearchId), listSearch));
            Assert(Find(list, "native.resources.empty.actions") is not null);
            Assert(Find(list, "native.resources.row.r1.swipe") is null);
            var back = LearningResourcesView.Page(listMode, null, pageStyle, key => key, ResourceActions());
            Assert(BrowserPageUpdate.Resources.TryUpdateInPlace(list, back));
            back.Dispose();
            Assert(Find(list, "native.resources.empty") is null);
            Assert(Find(list, "native.resources.row.r1.swipe") is not null
                && Find(list, "native.resources.row.r2.swipe") is not null);
        }
        // A starter resource in progress holds the page and its toolbar still, in place.
        var busy = LearningResourcesView.Page(Resources() with { StarterBusy = true }, null, style, key => key, ResourceActions());
        Assert(BrowserPageUpdate.Resources.TryUpdateInPlace(page, busy));
        busy.Dispose();
        // The page's root (the system search field over the list) is disabled, so all of it is.
        Assert(!search.IsEnabled);
        Assert(!Find(page, "native.resources.add")!.IsEnabled && !Find(page, "native.resources.filters.open")!.IsEnabled);

        var sheetState = Resources() with { Sheet = new NativeResourceFilterDraft("Video", null, NativeResourceSort.Added, false) };
        using var sheet = LearningResourcesView.FilterSheet(sheetState, style, key => key,
            new LearningResourceFilterActions(_ => { }, _ => { }, _ => { }, () => { }, () => { }, () => { }));
        Assert(AllText(Find(sheet, "native.resources.sort.added")!).Contains('↑'));
        Assert(Find(sheet, NativeAccess.ResourceType) is not null);
        foreach (var id in new[] { "native.resources.filters.clear", "native.resources.filters.cancel",
                     "native.resources.filters.apply", "native.resources.filters.close" })
            Assert(Find(sheet, id) is not null);
    }),
    // Issue #234 (Duotopia, 951 points wide): only the stats card was centered; the heading,
    // the line under it and Play Again sat at the page's leading edge. The tree read as
    // centered (a centered frame on the stack), so this one is drawn in a real SwiftUI host.
    // The Captain then asked (pull request 238) for the heading to stay at the leading edge,
    // with only the content under it centered, and for its trophy to go: it read as an emoji.
    ("Vocabulary Matching: the completion line, stats and Play Again are centered under a leading heading with no symbol", () =>
    {
        static string English(string key) => key switch
        {
            NativeMatchingText.Congratulations => "Congratulations!",
            NativeMatchingText.AllPairsMatched => "All pairs matched!",
            NativeMatchingText.Matched => "Matched",
            NativeMatchingText.Misses => "Misses",
            NativeMatchingText.PlayAgain => "Play Again",
            _ => key
        };
        foreach (var width in new[] { 402d, 951d })
        {
            using var view = VocabularyMatchingCompleteView.Render(
                new NativeMatchingState(NativePhase.Summary, MatchedPairs: 6, TotalPairs: 6),
                new SourceUiStyle(SourceUiAppearance.FromSelection(AppearanceSelection.Default), width),
                English, () => { });
            Assert(Find(view, NativeMatchingAccess.PlayAgain) is { Kind: "button" });
            Assert(view.Children[0] is { Kind: "text", Text: "Congratulations!" });
            var rows = PaintedRows(view, width, 400);
            var drawn = $"At {width} points the rows are painted at {string.Join(", ", rows.Select(row => $"{row.Left:0.#}-{row.Right:0.#}"))}.";
            if (rows.Count != 4)
                throw new Exception($"Expected the heading, the line, the card and the button. {drawn}");
            if (rows[0].Left > 4 || (rows[0].Left + rows[0].Right) / 2 > width / 2 - 4)
                throw new Exception($"The heading is not at the leading edge. {drawn}");
            if (rows.Skip(1).Any(row => Math.Abs((row.Left + row.Right) / 2 - width / 2) > 4))
                throw new Exception($"A row under the heading is off the center ({width / 2:0.#}). {drawn}");
        }
    }),
    // Issue 260 (Duotopia, 951 points wide): beside "I was correct", Submit was drawn narrower
    // than its label and checkmark need, so the label wrapped a letter or two to a line, and its
    // checkmark trailed while its neighbour's led. The tree cannot show a squeezed button, so
    // the row is drawn in a real SwiftUI host. The squeeze was the bridge's (an empty frame
    // around every node, mended in 0.1.0-preview.67), so the row keeps the two widths by
    // itself: neither button is fixed-size, and both can wrap again at very large text.
    ("Quiz correction: Submit keeps its own width beside I was correct, and both checkmarks lead (issue 260)", () =>
    {
        foreach (var width in new[] { 402d, 951d })
        {
            var quizStyle = new SourceUiStyle(SourceUiAppearance.FromSelection(AppearanceSelection.Default), width);
            SwiftView SubmitRow(NativeQuizState state, string submitId, SwiftView? beside)
            {
                var items = new List<SwiftView>();
                QuizQuestionView.Add(items, state, "", false, false, quizStyle, QuizEnglish,
                    _ => { }, _ => { }, false, _ => { }, () => { }, _ => { }, () => { }, () => { },
                    () => { }, () => { }, _ => { }, (_, _) => { }, () => { }, () => { }, null, beside);
                return items.Single(item => Find(item, submitId) is not null);
            }
            double PaintedWidth(SwiftView view)
            {
                var rows = PaintedRows(view, width, 300);
                if (rows.Count != 1)
                    throw new Exception($"Expected one painted row, found {rows.Count}.");
                return rows[0].Right - rows[0].Left;
            }
            // Submit as it is before an answer, with its row to itself.
            using var alone = SubmitRow(QuizCorrection() with
            {
                Phase = NativePhase.Ready, RequiresCorrection = false, CanSubmit = true,
                AnswerWasCorrect = null, SelectedAnswer = null, CorrectAnswerAfterGrading = null
            }, "native.quiz.submit", null);
            using var wasCorrect = QuizFeedbackView.Override(quizStyle, QuizEnglish, () => { });
            using var pair = SubmitRow(QuizCorrection(), "native.quiz.correction",
                QuizFeedbackView.Override(quizStyle, QuizEnglish, () => { }));
            Assert(Find(pair, "native.quiz.override") is { Kind: "button" });
            foreach (var id in new[] { "native.quiz.correction", "native.quiz.override" })
                if (Find(pair, id)!.FixedHorizontalSize)
                    throw new Exception($"{id} is fixed-size, so it cannot wrap at very large text; the row keeps its width without that.");
            var apart = PaintedWidth(alone) + SourceUiLayout.ItemGap + PaintedWidth(wasCorrect);
            var together = PaintedWidth(pair);
            if (Math.Abs(together - apart) > 2)
                throw new Exception($"At {width} points Submit and I was correct are painted {together:0.#} points wide together; each at its own width, with the gap, they take {apart:0.#}.");
            foreach (var (view, id) in new[] { (alone, "native.quiz.submit"),
                         (pair, "native.quiz.correction"), (pair, "native.quiz.override") })
            {
                var label = Find(view, id)!.Children[0];
                if (label.Children.Count != 2 || label.Children[0].Kind != "image" || label.Children[1].Kind != "text")
                    throw new Exception($"{id}: the checkmark does not lead the title ({string.Join(", ", label.Children.Select(child => child.Kind))}).");
            }
        }
    }),
    // Issue 279: the correction row at the app's largest text size, at a phone's widths, in
    // English and in Korean (the app's own strings). Each button is first drawn with room for
    // everything, which gives the width and height it needs. Where the two and the gap fit the
    // width, they are painted in one row at those widths; where they do not, "I was correct" is
    // painted under Submit, each as wide and as high as it is by itself, so neither label wraps.
    // (Squeezed into one row at 330 points, the row was one block of paint with wrapped labels.)
    ("Quiz correction: at the largest text size Submit and I was correct stack on a phone when they do not fit one row, in English and Korean (issue 279)", () =>
    {
        const double room = 1200;
        var wrong = new List<string>();
        var (stacked, sideBySide) = (0, 0);
        foreach (var language in new[] { "en", "ko" })
        foreach (var scale in new[] { AppearanceSelection.DefaultFontScale, AppearanceSelection.MaxFontScale })
        foreach (var width in new[] { 330d, 402d, 951d })
        {
            var culture = System.Globalization.CultureInfo.GetCultureInfo(language);
            string Words(string key) => SentenceStudio.LocalizationManager.Instance.GetString(key, culture);
            var title = Words(NativeText.QuizOverride);
            if (title == NativeText.QuizOverride || (language == "ko") != title.Any(letter => letter >= '가'))
                throw new Exception($"The app's {language} strings were not read: the button beside Submit is \"{title}\".");
            var look = new SourceUiStyle(SourceUiAppearance.FromSelection(
                AppearanceSelection.Default.WithFontScale(scale)), width);
            SwiftView SubmitRow(SwiftView? besideSubmit)
            {
                var items = new List<SwiftView>();
                QuizQuestionView.Add(items, QuizCorrection(), "", false, false, look, Words,
                    _ => { }, _ => { }, false, _ => { }, () => { }, _ => { }, () => { }, () => { },
                    () => { }, () => { }, _ => { }, (_, _) => { }, () => { }, () => { }, null, besideSubmit);
                return items.Single(item => Find(item, "native.quiz.correction") is not null);
            }
            double OwnWidth(SwiftView view)
            {
                var blocks = PaintedRows(view, room, 400);
                if (blocks.Count != 1)
                    throw new Exception($"A button drawn by itself with room is painted in {blocks.Count} blocks.");
                return blocks[0].Right - blocks[0].Left;
            }
            using var submitAlone = SubmitRow(null);
            using var submitAgain = SubmitRow(null);
            using var overrideAlone = QuizFeedbackView.Override(look, Words, () => { });
            using var overrideAgain = QuizFeedbackView.Override(look, Words, () => { });
            // A view is drawn once: the height comes from a second copy.
            var submit = (Width: OwnWidth(submitAlone), Height: PaintedHeight(submitAgain, room, 400));
            var beside = (Width: OwnWidth(overrideAlone), Height: PaintedHeight(overrideAgain, room, 400));
            var oneRow = submit.Width + SourceUiLayout.ItemGap + beside.Width;
            using var pair = SubmitRow(QuizFeedbackView.Override(look, Words, () => { }));
            using var pairAgain = SubmitRow(QuizFeedbackView.Override(look, Words, () => { }));
            var rows = PaintedRows(pair, width, 600);
            var high = PaintedHeight(pairAgain, width, 600);
            var at = $"{language}, text size {scale:0.##}, {width} points";
            var drawn = $"painted {string.Join(" and ", rows.Select(row => $"{row.Right - row.Left:0.#} wide"))}, {high:0.#} high; Submit needs {submit.Width:0.#} by {submit.Height:0.#}, \"{title}\" {beside.Width:0.#} by {beside.Height:0.#}, one row {oneRow:0.#}";
            if (oneRow <= width)
            {
                sideBySide++;
                if (rows.Count != 1 || Math.Abs(rows[0].Right - rows[0].Left - oneRow) > 2
                    || high - Math.Max(submit.Height, beside.Height) > 2)
                    wrong.Add($"{at}: the two fit one row and are not in it ({drawn})");
                continue;
            }
            stacked++;
            if (submit.Width > width || beside.Width > width)
                throw new Exception($"{at}: a button does not fit the width by itself ({drawn}); this case does not cover that.");
            if (rows.Count != 2
                || Math.Abs(rows[0].Right - rows[0].Left - submit.Width) > 2
                || Math.Abs(rows[1].Right - rows[1].Left - beside.Width) > 2
                || Math.Abs(high - (submit.Height + SourceUiLayout.ItemGap + beside.Height)) > 2)
                wrong.Add($"{at}: the two do not fit one row and are not one under the other at their own sizes ({drawn})");
            else if (rows.Any(row => Math.Abs((row.Left + row.Right) / 2 - width / 2) > 2))
                wrong.Add($"{at}: a stacked button is off the center ({drawn})");
        }
        if (wrong.Count > 0)
            throw new Exception($"{wrong.Count} drawings are wrong: {string.Join("; ", wrong)}.");
        if (stacked == 0 || sideBySide == 0)
            throw new Exception($"The case must draw both: {stacked} drawings stacked and {sideBySide} side by side.");
    }),
    // Issue 260, the Captain's follow-up: the YouTube pages' badges, Retry and Select All New
    // keep one line beside a title that truncates. The rows are put together here as ImportRow,
    // VideoRow and the count row put them together, from the views' own badges and buttons, and
    // drawn 330 points wide (an iPhone's Form row): a row whose badge or button wrapped would be
    // taller. Issue 260 also took fixed size off the badges and Retry, and this case refused it;
    // issue 277 drew the wider badges, which wrap without it, and put it back (the next case).
    // Select All New is still not fixed-size.
    ("YouTube rows: the badges, Retry and Select All New keep one line beside a long title (issue 260)", () =>
    {
        const double width = 330;
        const string longTitle = "A very long video title that cannot fit on one line of an iPhone, so it has to truncate somewhere on the way to its end";
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        var phone = new SourceUiStyle(SourceUiAppearance.FromSelection(AppearanceSelection.Default), width);
        SwiftView StatusBadge() => (SwiftView)typeof(YouTubeImportView).GetMethod("StatusBadge", flags)!
            .Invoke(null, [VideoImportStatus.Failed, phone, (Func<string, string>)(_ => "Failed"), "status"])!;
        SwiftView VideoBadge() => (SwiftView)typeof(YouTubeChannelView).GetMethod("Badge", flags)!
            .Invoke(null, [phone, "xmark.circle", "Failed", phone.Appearance.Danger, new SourceColorPair("#FFFFFF", "#FFFFFF"), "failed"])!;
        SwiftView Retry()
        {
            var label = Views.HStack(Views.Image("arrow.clockwise"), Views.Text("Retry"));
            label.Spacing = 4;
            return YouTubeImportView.OutlineWarningButton(label, () => { }, phone);
        }
        SwiftView SelectAll() => NativeForm.Bordered(phone, "Select All New", () => { }, "select-all") is { FixedHorizontalSize: false } button
            ? button : throw new Exception("Select All New is fixed-size; beside one caption it keeps its width without that.");
        SwiftView Caption(string value) => phone.Text(Views.Text(value), SourceTextRole.Caption1, SourceTextTone.Secondary);
        SwiftView ImportRow(string title)
        {
            var open = Views.Button(phone.Text(Views.Text(title), SourceTextRole.Body1Strong).WithLineLimit(1), () => { })
                .WithButtonStyle(SwiftButtonStyle.Plain).WithFlexibleWidth();
            var line = Views.HStack(StatusBadge(), open, Caption("11/25/2026"), Retry()).WithFlexibleWidth();
            line.Spacing = SourceUiLayout.ItemGap + 4;
            return line;
        }
        SwiftView VideoRow(string title)
        {
            var row = Views.HStack(Views.Toggle(title, false, _ => { }).WithLabelsHidden(),
                phone.Text(Views.Text(title), SourceTextRole.Body2).WithLineLimit(1).WithFlexibleWidth(),
                VideoBadge(), Retry()).WithFlexibleWidth();
            row.Spacing = SourceUiLayout.ItemGap + 4;
            return row;
        }
        foreach (var (what, row) in new (string, Func<string, SwiftView>)[] { ("The import's row", ImportRow), ("The channel's video row", VideoRow) })
        {
            using var brief = row("Short");
            using var full = row(longTitle);
            var (one, tall) = (PaintedHeight(brief, width, 120), PaintedHeight(full, width, 120));
            if (Math.Abs(tall - one) > 1)
                throw new Exception($"{what} is {tall:0.#} points high with a long title and {one:0.#} with a short one: something beside the title wrapped.");
        }
        using var alone = SelectAll();
        var countRow = Views.HStack(Caption("128 videos found").WithFlexibleWidth(), SelectAll());
        countRow.Spacing = SourceUiLayout.ItemGap;
        using (countRow)
        {
            var (button, counted) = (PaintedHeight(alone, width, 120), PaintedHeight(countRow, width, 120));
            if (Math.Abs(counted - button) > 1)
                throw new Exception($"The video count row is {counted:0.#} points high and Select All New alone is {button:0.#}: the button wrapped.");
        }
    }),
    // Issue 277: the case above draws rows it puts together itself, 330 points wide, in English,
    // at the default text size, and only "Failed" and "Retry", which fit in an equal share of
    // the row. This one draws the pages' own rows (History's and Discovery's, from a state made
    // here): each row alone, and all of them inside the grouped Form they sit in, at an iPhone's
    // and an iPad's width, in English and in Korean (the app's own strings), at the default and
    // at the largest app text size, with a title too long for any of them. Each is drawn again
    // with room for everything on one line: a row whose badge, date or button wrapped is taller
    // than that. (Without fixed size a stuck import's row was 164 points high at 330, not 40.)
    ("YouTube rows: at iPad width, in Korean and at the largest app text size the pages' own rows keep one line, alone and in a Form (issue 277)", () =>
    {
        const double room = 2000;
        const string title = "한국어 브이로그 아이폰 한 줄에는 도저히 들어가지 않는 아주 긴 영상 제목이라서 끝까지 가기 전에 어딘가에서 잘려야 하는데 아이패드에서도 한 줄에 다 들어가지 않을 만큼 길게 이어지는 제목입니다 and it goes on in English as well, far past the end of the row, so that it is cut short even with room for the rest of the row";
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        var created = new DateTime(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);
        // The rows take their coordinator only for what a tap does; nothing is tapped here.
        using var import = new YouTubeImportCoordinator(null!, null!,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<YouTubeImportCoordinator>.Instance);
        using var channel = new YouTubeChannelCoordinator(null!, null!,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<YouTubeChannelCoordinator>.Instance);
        var opens = new YouTubeImportActions(_ => { }, null, null);
        // A failed manual import, a completed one from a channel, and one stuck while it runs
        // (its badge has a spinner, and it has Retry).
        var history = new YouTubeImportState(true, false, YouTubeImportTab.History, "",
            false, false, false, false, [], 0, "", null, null, false, [], false,
            [
                new("1", title, true, null, VideoImportStatus.Failed, null, created, null, false),
                new("2", title, false, title, VideoImportStatus.Completed, null, created, "res-1", false),
                new("3", title, false, null, VideoImportStatus.GeneratingVocabulary, null, created, null, true),
            ], 3, null, []);
        // A failed video (badge and Retry), an imported one, one being imported and a new one.
        var videos = new YouTubeChannelState(true, false, false, false, false, false, null,
            "https://www.youtube.com/@channel", "Korean", 24, true, true, true, null, false,
            [
                new("a", title, "https://youtu.be/a", VideoImportStatus.Failed),
                new("b", title, "https://youtu.be/b", VideoImportStatus.Completed),
                new("c", title, "https://youtu.be/c", VideoImportStatus.CleaningTranscript),
                new("d", title, "https://youtu.be/d", null),
            ], System.Collections.Immutable.ImmutableHashSet<string>.Empty, false, false, true, null, false, null, []);
        var wrapped = new List<string>();
        foreach (var language in new[] { "en", "ko" })
        foreach (var scale in new[] { AppearanceSelection.DefaultFontScale, AppearanceSelection.MaxFontScale })
        foreach (var width in new[] { 330d, 402d, 951d })
        {
            var culture = System.Globalization.CultureInfo.GetCultureInfo(language);
            string Words(string key) => SentenceStudio.LocalizationManager.Instance.GetString(key, culture);
            if (Words("Import_Retry") == "Import_Retry" || (language == "ko") != Words("Import_Retry").Any(letter => letter >= '가'))
                throw new Exception($"The app's {language} strings were not read: Retry is \"{Words("Import_Retry")}\".");
            var look = new SourceUiStyle(SourceUiAppearance.FromSelection(
                AppearanceSelection.Default.WithFontScale(scale)), width);
            SwiftView ImportRow(int index) =>
                (SwiftView)typeof(YouTubeImportView).GetMethod("ImportRow", flags)!
                    .Invoke(null, [import, history, history.DisplayedImports[index], look, (Func<string, string>)Words, opens])!;
            SwiftView VideoRow(int index) =>
                (SwiftView)typeof(YouTubeChannelView).GetMethod("VideoRow", flags)!
                    .Invoke(null, [channel, videos, videos.Videos[index], index, look, (Func<string, string>)Words])!;
            SwiftView HistoryForm() => NativeForm.Page(look,
                (IEnumerable<SwiftView>)typeof(YouTubeImportView).GetMethod("History", flags)!
                    .Invoke(null, [import, history, look, (Func<string, string>)Words, opens])!, "form", "page");
            SwiftView DiscoveryForm() => NativeForm.Page(look,
                [(SwiftView)typeof(YouTubeChannelView).GetMethod("Discovery", flags)!
                    .Invoke(null, [channel, videos, look, (Func<string, string>)Words])!], "form", "page");
            void OneLine(string what, Func<SwiftView> build, Func<SwiftView, double, double> height)
            {
                using var drawn = build();
                using var withRoom = build();
                var (high, oneLine) = (height(drawn, width), height(withRoom, room));
                // Taller only: with room more of the title shows, and its letters can reach lower.
                if (high - oneLine > 2)
                    wrapped.Add($"{what} ({language}, text size {scale:0.##}, {width} points) is {high:0.#} points high, and {oneLine:0.#} with room for one line");
            }
            for (var index = 0; index < history.DisplayedImports.Length; index++)
            {
                var at = index;
                OneLine($"The import row {history.DisplayedImports[at].Status}", () => ImportRow(at),
                    (row, wide) => PaintedHeight(row, wide, 300));
            }
            for (var index = 0; index < videos.Videos.Length; index++)
            {
                var at = index;
                OneLine($"The channel's video row {videos.Videos[at].ImportStatus?.ToString() ?? "New"}", () => VideoRow(at),
                    (row, wide) => PaintedHeight(row, wide, 300));
            }
            OneLine("The import history in its Form", HistoryForm, (form, wide) => HeightOffCanvas(form, wide, 700));
            OneLine("The channel's videos in their Form", DiscoveryForm, (form, wide) => HeightOffCanvas(form, wide, 700));
        }
        if (wrapped.Count > 0)
            throw new Exception($"Something beside the title wrapped in {wrapped.Count} drawings: {string.Join("; ", wrapped)}.");
    }),
    // Issue 292: since issue 277 the import row's badge, date and Retry keep their own width, and
    // an item that keeps its width cannot give way. The cases above measure height only. This
    // one draws the pages' own import rows at the width of an iPhone's Form row (the page less
    // its 16-point margins and the row's 20-point insets: 303 of 375, 330 of 402, 368 of 440),
    // in English and Korean, at the default and the largest app text size, inside a frame of
    // that width in a host with 100 points to spare on each side: paint outside the frame is a
    // row running past its edges. Each row is drawn a second time with a blank title, and the
    // columns that differ are the title's: it has to show.
    ("YouTube import row: on an iPhone at the largest app text size nothing is painted past the row and the title shows (issue 292)", () =>
    {
        const double margin = 100;
        const string title = "A very long video title that cannot fit on one line of an iPhone, so it has to truncate somewhere on the way to its end";
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        var created = new DateTime(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);
        using var import = new YouTubeImportCoordinator(null!, null!,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<YouTubeImportCoordinator>.Instance);
        var opens = new YouTubeImportActions(_ => { }, null, null);
        YouTubeImportState History(string name) => new(true, false, YouTubeImportTab.History, "",
            false, false, false, false, [], 0, "", null, null, false, [], false,
            [
                new("1", name, true, null, VideoImportStatus.Failed, null, created, null, false),
                new("2", name, false, name, VideoImportStatus.Completed, null, created, "res-1", false),
                new("3", name, false, null, VideoImportStatus.GeneratingVocabulary, null, created, null, true),
            ], 3, null, []);
        var (titled, blank) = (History(title), History(" "));
        var faults = new List<string>();
        var drawn = new List<string>();
        foreach (var language in new[] { "en", "ko" })
        foreach (var scale in new[] { AppearanceSelection.DefaultFontScale, AppearanceSelection.MaxFontScale })
        foreach (var page in new[] { 375d, 402d, 440d })
        {
            var width = page - 72;
            var culture = System.Globalization.CultureInfo.GetCultureInfo(language);
            string Words(string key) => SentenceStudio.LocalizationManager.Instance.GetString(key, culture);
            var look = new SourceUiStyle(SourceUiAppearance.FromSelection(
                AppearanceSelection.Default.WithFontScale(scale)), page);
            bool[][] Draw(YouTubeImportState state, int index)
            {
                var row = (SwiftView)typeof(YouTubeImportView).GetMethod("ImportRow", flags)!
                    .Invoke(null, [import, state, state.DisplayedImports[index], look, (Func<string, string>)Words, opens])!;
                using var framed = Views.VStack(row).WithFixedWidth(width);
                return PaintedMask(framed, width + 2 * margin, 160);
            }
            for (var index = 0; index < titled.DisplayedImports.Length; index++)
            {
                var (with, without) = (Draw(titled, index), Draw(blank, index));
                var perPoint = with[0].Length / (width + 2 * margin);
                var columns = Enumerable.Range(0, with[0].Length).ToArray();
                var painted = columns.Where(x => with.Any(line => line[x])).ToArray();
                var (left, right) = (painted[0] / perPoint - margin, (painted[^1] + 1) / perPoint - margin);
                var titleWidth = columns.Count(x => Enumerable.Range(0, with.Length).Any(y => with[y][x] != without[y][x])) / perPoint;
                var lines = with.Count(line => line.Any(pixel => pixel)) / perPoint;
                var what = $"The import row {titled.DisplayedImports[index].Status} ({language}, text size {scale:0.##}, a {page}-point page, a {width}-point row)";
                drawn.Add($"{what}: paint {left:0.#} to {right:0.#}, title {titleWidth:0.#}, {lines:0.#} high");
                if (left < -1 || right > width + 1)
                    faults.Add($"{what} is painted from {left:0.#} to {right:0.#}, past its {width} points");
                // Three letters and the ellipsis of the title at this text size.
                else if (titleWidth < 40 * scale)
                    faults.Add($"{what} shows {titleWidth:0.#} points of its title, under {40 * scale:0.#}");
            }
        }
        if (Environment.GetEnvironmentVariable("ISSUE_292_DRAWN") is not null)
            Console.WriteLine(string.Join(Environment.NewLine, drawn));
        if (faults.Count > 0)
            throw new Exception($"{faults.Count} of {drawn.Count} rows: {string.Join("; ", faults)}.");
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

static NativeResourceListState Resources(string searchText = "")
{
    var day = new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);
    NativeResourceItem Item(string id, string title, string type) =>
        new(id, title, null, null, type, "Korean", false, day, day);
    return new NativeResourceListState(NativeResourceListPhase.Populated,
        [Item("r1", "Lesson one", "Video"), Item("r2", "Story", "Vocabulary List")],
        ["Video", "Vocabulary List"], ["Korean"], SearchText: searchText,
        Sync: NativeResourceSyncPhase.Confirmed);
}

static LearningResourcesActions ResourceActions() => new(
    Search: _ => { }, FilterType: _ => { }, FilterLanguage: _ => { }, ToggleViewMode: () => { },
    Open: _ => { }, Add: () => { }, CreateStarter: () => { }, Reload: () => { }, Sort: _ => { },
    OpenFilters: () => { }, ToggleFilterPanel: () => { }, ClearFilters: () => { },
    ClearSearchAndFilters: () => { });

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
    static NativeVocabularyDuplicateWord Word(string id, string meaning, string label) =>
        new(id, "병", meaning, "Korean", LexicalUnitType.Word, 1, 1, label, null, false, false);
    var review = new NativeVocabularyDuplicateGroup("병", "병",
        [Word("w-strong", "bottle", "Strong"), Word("w-good", "illness", "Good")],
        false, null, "w-good", VocabularyDuplicateRisk.Review, [VocabularyDuplicateDifference.Meaning]);
    var blocked = new NativeVocabularyDuplicateGroup("사과", "사과",
        [Word("w-a", "apple", "Basic"), Word("w-b", "apple", "Basic")],
        false, "Vocabulary_DuplicatesOthersShared", "w-a", VocabularyDuplicateRisk.Blocked);
    var safe = new NativeVocabularyDuplicateGroup("물", "물",
        [Word("w-c", "water", "Basic"), Word("w-d", "water", "Basic")], true, null, "w-c");
    return new VocabularyDuplicatePanel(false, "Duplicates", "Found", null, null, null,
        [review, blocked, safe], 3, 3, [], SafeGroupCount: 1, SafeCopyCount: 1,
        ReviewGroupCount: 1, BlockedGroupCount: 1);
}

static SwiftView? SectionTitled(SwiftView node, string title)
{
    if (node.Kind == "section" && node.Text == title)
        return node;
    foreach (var child in node.Children)
        if (SectionTitled(child, title) is { } match)
            return match;
    return null;
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

static string AllText(SwiftView node) =>
    (node.Text ?? string.Empty) + string.Concat(node.Children.Select(AllText));

static SwiftView? Find(SwiftView node, string id)
{
    if (node.AccessibilityIdentifier == id)
        return node;
    foreach (var child in node.Children)
        if (Find(child, id) is { } match)
            return match;
    return null;
}

// Draws a view in a real SwiftUI host and returns, for each run of pixel rows with anything
// painted on it, the left and right edge of the paint in points. The host has no background
// of its own, so a painted pixel is one that is not transparent.
static List<(double Left, double Right)> PaintedRows(SwiftView root, double width, double height)
{
    var host = SwiftUIHost.CreateView(root);
    try
    {
        host.Frame = new CGRect(0, 0, width, height);
        host.LayoutSubtreeIfNeeded();
        var bitmap = host.BitmapImageRepForCachingDisplayInRect(host.Bounds)
            ?? throw new Exception("The host has no bitmap to draw in.");
        host.CacheDisplay(host.Bounds, bitmap);
        if (!bitmap.HasAlpha || bitmap.IsPlanar || bitmap.BitsPerSample != 8)
            throw new Exception("The host's bitmap is not 8-bit meshed pixels with alpha, so paint cannot be told from background.");
        var scale = bitmap.PixelsWide / width;
        var samples = (int)bitmap.BitsPerPixel / 8;
        var alpha = bitmap.BitmapFormat.HasFlag(NSBitmapFormat.AlphaFirst) ? 0 : (int)bitmap.SamplesPerPixel - 1;
        var rows = new List<(double Left, double Right)>();
        (int Left, int Right)? run = null;
        void EndRun()
        {
            if (run is { } ended)
                rows.Add((ended.Left / scale, (ended.Right + 1) / scale));
            run = null;
        }
        for (var y = 0; y < bitmap.PixelsHigh; y++)
        {
            (int Left, int Right)? painted = null;
            for (var x = 0; x < bitmap.PixelsWide; x++)
                if (Marshal.ReadByte(bitmap.BitmapData, y * (int)bitmap.BytesPerRow + x * samples + alpha) != 0)
                    painted = (painted?.Left ?? x, x);
            if (painted is { } row)
                run = (Math.Min(run?.Left ?? row.Left, row.Left), Math.Max(run?.Right ?? row.Right, row.Right));
            else
                EndRun();
        }
        EndRun();
        if (rows.Count == 0)
            throw new Exception("The host painted nothing: this is the drawing, not the layout.");
        return rows;
    }
    finally
    {
        SwiftUIHost.DetachView(root, host);
    }
}

// Draws a view in a real SwiftUI host and returns, for each pixel row, which of its pixels
// are painted (not transparent: the host has no background of its own).
static bool[][] PaintedMask(SwiftView root, double width, double height)
{
    var host = SwiftUIHost.CreateView(root);
    try
    {
        host.Frame = new CGRect(0, 0, width, height);
        host.LayoutSubtreeIfNeeded();
        var bitmap = host.BitmapImageRepForCachingDisplayInRect(host.Bounds)
            ?? throw new Exception("The host has no bitmap to draw in.");
        host.CacheDisplay(host.Bounds, bitmap);
        if (!bitmap.HasAlpha || bitmap.IsPlanar || bitmap.BitsPerSample != 8)
            throw new Exception("The host's bitmap is not 8-bit meshed pixels with alpha, so paint cannot be told from background.");
        var samples = (int)bitmap.BitsPerPixel / 8;
        var alpha = bitmap.BitmapFormat.HasFlag(NSBitmapFormat.AlphaFirst) ? 0 : (int)bitmap.SamplesPerPixel - 1;
        // Read once: each of these is a call into the bitmap, and there is one read per pixel.
        var (data, stride, wide) = (bitmap.BitmapData, (int)bitmap.BytesPerRow, (int)bitmap.PixelsWide);
        var mask = new bool[(int)bitmap.PixelsHigh][];
        for (var y = 0; y < mask.Length; y++)
        {
            mask[y] = new bool[wide];
            for (var x = 0; x < wide; x++)
                mask[y][x] = Marshal.ReadByte(data, y * stride + x * samples + alpha) != 0;
        }
        if (!mask.Any(line => line.Any(pixel => pixel)))
            throw new Exception("The host painted nothing: this is the drawing, not the layout.");
        return mask;
    }
    finally
    {
        SwiftUIHost.DetachView(root, host);
    }
}

// The height in points of everything a view paints in a real SwiftUI host of the given size.
static double PaintedHeight(SwiftView root, double width, double height)
{
    var host = SwiftUIHost.CreateView(root);
    try
    {
        host.Frame = new CGRect(0, 0, width, height);
        host.LayoutSubtreeIfNeeded();
        var bitmap = host.BitmapImageRepForCachingDisplayInRect(host.Bounds)
            ?? throw new Exception("The host has no bitmap to draw in.");
        host.CacheDisplay(host.Bounds, bitmap);
        if (!bitmap.HasAlpha || bitmap.IsPlanar || bitmap.BitsPerSample != 8)
            throw new Exception("The host's bitmap is not 8-bit meshed pixels with alpha, so paint cannot be told from background.");
        var (data, stride, wide, high) = (bitmap.BitmapData, (int)bitmap.BytesPerRow, (int)bitmap.PixelsWide, (int)bitmap.PixelsHigh);
        var samples = (int)bitmap.BitsPerPixel / 8;
        var alpha = bitmap.BitmapFormat.HasFlag(NSBitmapFormat.AlphaFirst) ? 0 : (int)bitmap.SamplesPerPixel - 1;
        int top = -1, bottom = -1;
        for (var y = 0; y < high; y++)
            for (var x = 0; x < wide; x++)
                if (Marshal.ReadByte(data, y * stride + x * samples + alpha) != 0)
                {
                    top = top < 0 ? y : top;
                    bottom = y;
                    break;
                }
        if (top < 0)
            throw new Exception("The host painted nothing: this is the drawing, not the layout.");
        return (bottom + 1 - top) / (high / height);
    }
    finally
    {
        SwiftUIHost.DetachView(root, host);
    }
}

// The height in points of everything in a view that is not its canvas: for a page that paints
// its own background (a grouped Form), where every pixel is painted. The canvas color is the
// top left pixel's.
static double HeightOffCanvas(SwiftView root, double width, double height)
{
    var host = SwiftUIHost.CreateView(root);
    try
    {
        host.Frame = new CGRect(0, 0, width, height);
        host.LayoutSubtreeIfNeeded();
        var bitmap = host.BitmapImageRepForCachingDisplayInRect(host.Bounds)
            ?? throw new Exception("The host has no bitmap to draw in.");
        host.CacheDisplay(host.Bounds, bitmap);
        if (bitmap.IsPlanar || bitmap.BitsPerSample != 8)
            throw new Exception("The host's bitmap is not 8-bit meshed pixels, so the canvas cannot be told from what is on it.");
        var (data, stride, wide, high) = (bitmap.BitmapData, (int)bitmap.BytesPerRow, (int)bitmap.PixelsWide, (int)bitmap.PixelsHigh);
        var samples = (int)bitmap.BitsPerPixel / 8;
        bool OnCanvas(int x, int y)
        {
            for (var sample = 0; sample < samples; sample++)
                if (Math.Abs(Marshal.ReadByte(data, y * stride + x * samples + sample) - Marshal.ReadByte(data, sample)) > 8)
                    return false;
            return true;
        }
        int top = -1, bottom = -1;
        for (var y = 0; y < high; y++)
            for (var x = 0; x < wide; x++)
                if (!OnCanvas(x, y))
                {
                    top = top < 0 ? y : top;
                    bottom = y;
                    break;
                }
        if (top < 0)
            throw new Exception("The host painted nothing but its canvas: this is the drawing, not the layout.");
        if (bottom == high - 1)
            throw new Exception($"The page is taller than the {height} points it was drawn in, so its height is not known.");
        return (bottom + 1 - top) / (high / height);
    }
    finally
    {
        SwiftUIHost.DetachView(root, host);
    }
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

static NativeQuizState QuizCorrection() =>
    new(NativePhase.Feedback, NativeDestination.VocabularyQuiz,
        new NativeQuizPrompt("w-1", "오른쪽 하고 비슷하게 들리네요", "Korean", null, null,
            IsTargetLanguagePrompt: true, ShowWrittenPrompt: true, CanPlayPromptAudio: true,
            ShowPhoto: false, TargetLanguageEngaged: true),
        Mode: "Text", CorrectAnswerAfterGrading: "sounds similar to the right side",
        RequiresCorrection: true, CurrentTurn: 3, TurnsInRound: 10, TotalTurns: 10,
        TurnId: "t-1", SelectedAnswer: "sounds right", AnswerWasCorrect: false);

static string QuizEnglish(string key) => key switch
{
    "VocabQuiz_SubmitAnswer" => "Submit",
    NativeText.QuizOverride => "I was correct",
    NativeText.CorrectionRequired => "Type the correct answer to continue",
    "VocabQuiz_TypeTranslation" => "Type the translation",
    "NativeVocabQuiz_IncorrectFeedback" => "The answer is: {0}",
    _ => key
};
