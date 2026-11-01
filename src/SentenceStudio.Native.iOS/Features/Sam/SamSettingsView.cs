using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Contracts.LearnerMemory;
using SentenceStudio.Native.iOS.Styling;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.Features.Sam;

internal sealed record SamSettingsActions(
    Action AskDelete,
    Action CancelDelete,
    Action ConfirmDelete,
    Action<NativeSamMemoryTab> SelectTab,
    Action<string> Approve,
    Action<string> Reject,
    Action<string> Forget,
    Action<string> BeginEdit,
    Action<string> EditChanged,
    Action SaveEdit,
    Action CancelEdit,
    Action AskForgetAll,
    Action CancelForgetAll,
    Action ConfirmForgetAll);

/// <summary>
/// Settings "Learning Coach" card and "What Sam remembers" card (source Settings.razor
/// coach section + CoachMemoryPanel). SwiftUI only; confirmations render inline in the
/// card they belong to, with the promise that Today's Plan and progress are untouched.
/// </summary>
internal static class SamSettingsView
{
    internal static SwiftView Sections(SourceUiStyle style, NativeSamState state,
        Func<string, string> text, SamSettingsActions actions)
    {
        var sections = new List<SwiftView> { Coach(style, state.SafeSettings, text, actions) };
        if (Memory(style, state.SafeMemory, text, actions) is { } memory)
            sections.Add(memory);
        var stack = Views.VStack(sections.ToArray()).WithFlexibleWidth();
        stack.Spacing = SourceUiLayout.SectionGap;
        return stack.WithAccessibilityIdentifier("native.sam.settings");
    }

    private static SwiftView Coach(SourceUiStyle style, NativeSamSettingsState settings,
        Func<string, string> text, SamSettingsActions actions)
    {
        var rows = new List<SwiftView>
        {
            style.Text(Views.Text(text("Coach_SettingsSection")), SourceTextRole.Title3, sourceHeading: true),
            style.Text(Views.Text(text("Coach_SettingsDescription")), SourceTextRole.Caption1,
                SourceTextTone.Secondary),
        };
        if (!settings.IsAvailable)
            rows.Add(style.Text(Views.Text(text("Coach_SettingsUnavailable")), SourceTextRole.Caption1,
                SourceTextTone.Tertiary).WithAccessibilityIdentifier("native.sam.settings.unavailable"));
        else
        {
            var delete = style.SecondaryButton(text(settings.Deleting ? "Common_Loading" : "Coach_SettingsDelete"),
                    actions.AskDelete, fullWidth: true)
                .WithAccessibilityIdentifier("native.sam.settings.delete");
            delete.IsEnabled = !settings.Deleting && !settings.ConfirmingDelete;
            rows.Add(delete);
            rows.Add(style.Text(Views.Text(text("Coach_DeleteConfirm")), SourceTextRole.Caption1,
                SourceTextTone.Secondary));
            if (settings.ConfirmingDelete)
            {
                var confirm = Views.VStack(
                        style.Text(Views.Text(text("Coach_DeleteDialogTitle")), SourceTextRole.Body1Strong,
                            sourceHeading: true),
                        style.Text(Views.Text(text("Coach_DeleteDialogBody")), SourceTextRole.Body2),
                        Row(style.SecondaryButton(text("Coach_Cancel"), actions.CancelDelete)
                                .WithAccessibilityIdentifier("native.sam.settings.delete.cancel"),
                            SamView.DangerButton(style, text("Coach_DeleteDialogConfirm"), actions.ConfirmDelete)
                                .WithAccessibilityIdentifier("native.sam.settings.delete.confirm")))
                    .WithFlexibleWidth();
                confirm.Spacing = 8;
                rows.Add(style.BorderedCard(confirm.WithPadding(12), radius: 10)
                    .WithAccessibilityIdentifier("native.sam.settings.delete.dialog"));
            }
        }
        if (settings.NoticeKey is { } notice)
            rows.Add(style.Text(Views.Text(text(notice)), SourceTextRole.Body2,
                    notice == "Coach_SettingsDeleted" ? SourceTextTone.Success : SourceTextTone.Secondary)
                .WithAccessibilityIdentifier("native.sam.settings.notice"));
        return Card(style, rows, "native.sam.settings.coach");
    }

    private static SwiftView? Memory(SourceUiStyle style, NativeSamMemoryState memory,
        Func<string, string> text, SamSettingsActions actions)
    {
        // The whole surface disappears when the feature is off or the data is not this learner's.
        if (memory.Availability != NativeSamMemoryAvailability.Available)
            return null;
        var rows = new List<SwiftView>
        {
            style.Text(Views.Text(text("Coach_MemoryTitle")), SourceTextRole.Title3, sourceHeading: true),
            style.Text(Views.Text(text("Coach_MemoryExplainer")), SourceTextRole.Body2, SourceTextTone.Secondary),
            style.Text(Views.Text(text("Coach_MemoryVsSettings")), SourceTextRole.Caption1, SourceTextTone.Secondary),
        };
        // Two tabs (source role=tablist): the selected one is the filled button.
        SwiftView Tab(NativeSamMemoryTab tab, string key, int count, string id)
        {
            var title = string.Format(CultureInfo.CurrentCulture, text(key), count);
            var selected = memory.Tab == tab;
            return (selected ? style.PrimaryButton(title, () => actions.SelectTab(tab))
                    : style.SecondaryButton(title, () => actions.SelectTab(tab)))
                .WithAccessibilityLabel(title)
                .WithAccessibilitySelected(selected)
                .WithAccessibilityIdentifier(id);
        }
        rows.Add(Row(
            Tab(NativeSamMemoryTab.Active, "Coach_MemoryTabActive", memory.SafeActive.Length,
                "native.sam.memory.tab.active"),
            Tab(NativeSamMemoryTab.Candidates, "Coach_MemoryTabCandidates", memory.SafeCandidates.Length,
                "native.sam.memory.tab.candidates")));
        if (memory.NoticeKey is { } notice)
            rows.Add(style.Text(Views.Text(text(notice)), SourceTextRole.Caption1, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier("native.sam.memory.panel.notice"));
        if (memory.Tab == NativeSamMemoryTab.Active)
        {
            if (memory.IsPaused)
                rows.Add(style.Text(Views.Text(text("Coach_MemoryPaused")), SourceTextRole.Body2,
                    SourceTextTone.Secondary).WithAccessibilityIdentifier("native.sam.memory.paused"));
            else
            {
                for (var index = 0; index < memory.SafeActive.Length; index++)
                    rows.Add(Fact(style, memory, memory.SafeActive[index], index, isCandidate: false, text, actions));
                var forgetAll = SamView.DangerButton(style, text("Coach_MemoryForgetAll"), actions.AskForgetAll)
                    .WithAccessibilityIdentifier("native.sam.memory.forgetall");
                forgetAll.IsEnabled = !memory.Busy;
                rows.Add(forgetAll);
                rows.Add(style.Text(Views.Text(text("Coach_MemoryForgetAllImpact")), SourceTextRole.Caption1,
                    SourceTextTone.Secondary));
                if (memory.ConfirmingForgetAll)
                {
                    var confirm = Views.VStack(
                            style.Text(Views.Text(text("Coach_MemoryForgetAllTitle")), SourceTextRole.Body1Strong,
                                sourceHeading: true),
                            style.Text(Views.Text(text("Coach_MemoryForgetAllBody")), SourceTextRole.Body2),
                            style.Text(Views.Text(text("Coach_MemoryForgetAllImpact")), SourceTextRole.Caption1,
                                SourceTextTone.Secondary),
                            Row(SamView.DangerButton(style, text("Coach_MemoryForgetAllConfirm"), actions.ConfirmForgetAll)
                                    .WithAccessibilityIdentifier("native.sam.memory.forgetall.confirm"),
                                style.SecondaryButton(text("Coach_Cancel"), actions.CancelForgetAll)
                                    .WithAccessibilityIdentifier("native.sam.memory.forgetall.cancel")))
                        .WithFlexibleWidth();
                    confirm.Spacing = 8;
                    rows.Add(style.BorderedCard(confirm.WithPadding(12), radius: 10)
                        .WithAccessibilityIdentifier("native.sam.memory.forgetall.dialog"));
                }
            }
        }
        else if (memory.SafeCandidates.IsEmpty)
            rows.Add(style.Text(Views.Text(text("Coach_MemoryNoCandidates")), SourceTextRole.Body2,
                SourceTextTone.Secondary).WithAccessibilityIdentifier("native.sam.memory.nocandidates"));
        else
            for (var index = 0; index < memory.SafeCandidates.Length; index++)
                rows.Add(Fact(style, memory, memory.SafeCandidates[index], index, isCandidate: true, text, actions));
        if (memory.Busy)
            rows.Add(Views.IndeterminateProgress(text("Common_Loading")));
        return Card(style, rows, "native.sam.memory.panel");
    }

    private static SwiftView Fact(SourceUiStyle style, NativeSamMemoryState memory, CoachMemoryFactDto fact,
        int index, bool isCandidate, Func<string, string> text, SamSettingsActions actions)
    {
        var prefix = isCandidate ? $"native.sam.memory.candidate.{index}" : $"native.sam.memory.active.{index}";
        string Format(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime()
            .ToString("d", CultureInfo.CurrentCulture);
        var meta = $"{text("Coach_MemoryScopeLabel")}: {SamView.MemoryScope(fact, text)} · "
            + $"{text("Coach_MemoryProvenanceLabel")}: "
            + text(fact.Provenance == CoachMemoryProvenance.UserConfirmed
                ? "Coach_MemoryProvenanceConfirmed" : "Coach_MemoryProvenanceStated")
            + $" · {text("Coach_MemorySavedAtLabel")}: {Format(fact.ConfirmedAtUtc ?? fact.CreatedAtUtc)}"
            + (fact.LastUsedAtUtc is { } used ? $" · {text("Coach_MemoryLastUsedLabel")}: {Format(used)}" : "");
        var rows = new List<SwiftView>
        {
            style.Text(Views.Text(fact.DisplayText), SourceTextRole.Body2).WithAccessibilityIdentifier(prefix + ".value"),
            style.Text(Views.Text(meta), SourceTextRole.Caption1, SourceTextTone.Secondary),
        };
        SwiftView Enable(SwiftView button, string suffix)
        {
            button.IsEnabled = !memory.Busy;
            return button.WithAccessibilityIdentifier($"{prefix}.{suffix}");
        }
        if (memory.EditingId == fact.Id)
        {
            rows.Add(style.TextInput(Views.TextField(text("Coach_MemoryEditLabel"), memory.EditText, actions.EditChanged)
                .WithAccessibilityLabel(text("Coach_MemoryEditLabel"))
                .WithAccessibilityIdentifier(prefix + ".editfield")));
            var save = Enable(style.PrimaryButton(text("Coach_MemorySaveEdited"), actions.SaveEdit), "saveedited");
            save.IsEnabled = !memory.Busy && !string.IsNullOrWhiteSpace(memory.EditText);
            rows.Add(Row(save, Enable(style.SecondaryButton(text("Coach_Cancel"), actions.CancelEdit), "canceledit")));
        }
        else
        {
            var buttons = new List<SwiftView>();
            var id = fact.Id;
            if (isCandidate)
                buttons.Add(Enable(style.PrimaryButton(text("Coach_MemorySave"), () => actions.Approve(id)), "save"));
            if (fact.Kind == CoachMemoryKind.PersistentStudyGoal)
                buttons.Add(Enable(style.SecondaryButton(text("Coach_MemoryEdit"), () => actions.BeginEdit(id)), "edit"));
            buttons.Add(Enable(style.SecondaryButton(text(isCandidate ? "Coach_MemoryNotNow" : "Coach_MemoryForget"),
                () => (isCandidate ? actions.Reject : actions.Forget)(id)), isCandidate ? "notnow" : "forget"));
            rows.Add(Row(buttons.ToArray()));
        }
        var stack = Views.VStack(rows.ToArray()).WithFlexibleWidth();
        stack.Spacing = 4;
        return style.BorderedCard(stack.WithPadding(10), radius: 10).WithAccessibilityIdentifier(prefix);
    }

    private static SwiftView Row(params SwiftView[] buttons)
    {
        var row = Views.HStack(buttons);
        row.Spacing = 8;
        return row;
    }

    private static SwiftView Card(SourceUiStyle style, List<SwiftView> rows, string id)
    {
        var stack = Views.VStack(rows.ToArray()).WithFlexibleWidth();
        stack.Spacing = 8;
        return style.Card(stack.WithPadding(16)).WithAccessibilityIdentifier(id);
    }
}
