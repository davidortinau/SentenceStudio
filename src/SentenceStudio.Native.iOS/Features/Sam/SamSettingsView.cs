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
/// Settings "Learning Coach" and "What Sam remembers" (source Settings.razor coach section +
/// CoachMemoryPanel) as Form Sections (Captain 2026-11-13, the native form pattern). SwiftUI
/// only; confirmations stay inline in the Section they belong to, with the promise that
/// Today's Plan and progress are untouched. A row's buttons are bordered system buttons, each
/// its own target in the row.
/// </summary>
internal static class SamSettingsView
{
    internal static IEnumerable<SwiftView> Sections(SourceUiStyle style, NativeSamState state,
        Func<string, string> text, SamSettingsActions actions)
    {
        yield return Coach(style, state.SafeSettings, text, actions);
        if (Memory(style, state.SafeMemory, text, actions) is { } memory)
            yield return memory;
    }

    private static SwiftView Coach(SourceUiStyle style, NativeSamSettingsState settings,
        Func<string, string> text, SamSettingsActions actions)
    {
        var rows = new List<SwiftView>
        {
            style.Text(Views.Text(text("Coach_SettingsDescription")), SourceTextRole.Caption1,
                SourceTextTone.Secondary),
        };
        if (!settings.IsAvailable)
            rows.Add(style.Text(Views.Text(text("Coach_SettingsUnavailable")), SourceTextRole.Caption1,
                SourceTextTone.Tertiary).WithAccessibilityIdentifier("native.sam.settings.unavailable"));
        else
        {
            var delete = Danger(style, Views.Button(
                    Views.Label(text(settings.Deleting ? "Common_Loading" : "Coach_SettingsDelete"), "trash"),
                    actions.AskDelete))
                .WithAccessibilityIdentifier("native.sam.settings.delete");
            delete.IsEnabled = !settings.Deleting && !settings.ConfirmingDelete;
            rows.Add(delete);
            rows.Add(style.Text(Views.Text(text("Coach_DeleteConfirm")), SourceTextRole.Caption1,
                SourceTextTone.Secondary));
            if (settings.ConfirmingDelete)
            {
                rows.Add(Confirmation(style,
                    text("Coach_DeleteDialogTitle"), text("Coach_DeleteDialogBody"), null,
                    NativeForm.Bordered(style, text("Coach_Cancel"), actions.CancelDelete,
                        "native.sam.settings.delete.cancel"),
                    NativeForm.Destructive(style, text("Coach_DeleteDialogConfirm"), actions.ConfirmDelete,
                        "native.sam.settings.delete.confirm"),
                    "native.sam.settings.delete.dialog"));
            }
        }
        if (settings.NoticeKey is { } notice)
            rows.Add(style.Text(Views.Text(text(notice)), SourceTextRole.Body2,
                    notice == "Coach_SettingsDeleted" ? SourceTextTone.Success : SourceTextTone.Secondary)
                .WithAccessibilityIdentifier("native.sam.settings.notice"));
        return Views.Section(text("Coach_SettingsSection"), rows.ToArray());
    }

    private static SwiftView? Memory(SourceUiStyle style, NativeSamMemoryState memory,
        Func<string, string> text, SamSettingsActions actions)
    {
        // The whole surface disappears when the feature is off or the data is not this learner's.
        if (memory.Availability != NativeSamMemoryAvailability.Available)
            return null;
        var explainer = Views.VStack(
                style.Text(Views.Text(text("Coach_MemoryExplainer")), SourceTextRole.Body2,
                    SourceTextTone.Secondary),
                style.Text(Views.Text(text("Coach_MemoryVsSettings")), SourceTextRole.Caption1,
                    SourceTextTone.Secondary))
            .WithFlexibleWidth();
        explainer.Spacing = 4;
        var rows = new List<SwiftView> { explainer };
        // Two tabs (source role=tablist) as the system segmented control.
        var tabs = Views.Picker(text("Coach_MemoryTitle"),
                [
                    new SwiftPickerOption(nameof(NativeSamMemoryTab.Active), string.Format(
                        CultureInfo.CurrentCulture, text("Coach_MemoryTabActive"),
                        memory.SafeActive.Length)),
                    new SwiftPickerOption(nameof(NativeSamMemoryTab.Candidates), string.Format(
                        CultureInfo.CurrentCulture, text("Coach_MemoryTabCandidates"),
                        memory.SafeCandidates.Length)),
                ],
                memory.Tab.ToString(),
                value =>
                {
                    if (Enum.TryParse<NativeSamMemoryTab>(value, out var tab))
                        actions.SelectTab(tab);
                })
            .WithPickerStyle(SwiftPickerStyle.Segmented)
            .WithAccessibilityIdentifier("native.sam.memory.tabs");
        rows.Add(tabs);
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
                var forgetAll = Danger(style, Views.Button(
                        Views.Label(text("Coach_MemoryForgetAll"), "trash"), actions.AskForgetAll))
                    .WithAccessibilityIdentifier("native.sam.memory.forgetall");
                forgetAll.IsEnabled = !memory.Busy;
                rows.Add(forgetAll);
                rows.Add(style.Text(Views.Text(text("Coach_MemoryForgetAllImpact")), SourceTextRole.Caption1,
                    SourceTextTone.Secondary));
                if (memory.ConfirmingForgetAll)
                    rows.Add(Confirmation(style,
                        text("Coach_MemoryForgetAllTitle"), text("Coach_MemoryForgetAllBody"),
                        text("Coach_MemoryForgetAllImpact"),
                        // Source order: Forget all, then Cancel.
                        NativeForm.Destructive(style, text("Coach_MemoryForgetAllConfirm"), actions.ConfirmForgetAll,
                            "native.sam.memory.forgetall.confirm"),
                        NativeForm.Bordered(style, text("Coach_Cancel"), actions.CancelForgetAll,
                            "native.sam.memory.forgetall.cancel"),
                        "native.sam.memory.forgetall.dialog"));
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
        return Views.Section(text("Coach_MemoryTitle"), rows.ToArray());
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
        SwiftView Enable(SwiftView button)
        {
            button.IsEnabled = !memory.Busy;
            return button;
        }
        if (memory.EditingId == fact.Id)
        {
            // A Form row field: the prompt names it.
            rows.Add(Views.TextField(text("Coach_MemoryEditLabel"), memory.EditText, actions.EditChanged)
                .WithAccessibilityLabel(text("Coach_MemoryEditLabel"))
                .WithAccessibilityIdentifier(prefix + ".editfield"));
            var save = NativeForm.Prominent(style, text("Coach_MemorySaveEdited"), actions.SaveEdit, $"{prefix}.saveedited");
            save.IsEnabled = !memory.Busy && !string.IsNullOrWhiteSpace(memory.EditText);
            rows.Add(Row(save, Enable(NativeForm.Bordered(style, text("Coach_Cancel"), actions.CancelEdit,
                $"{prefix}.canceledit"))));
        }
        else
        {
            var buttons = new List<SwiftView>();
            var id = fact.Id;
            if (isCandidate)
                buttons.Add(Enable(NativeForm.Prominent(style, text("Coach_MemorySave"), () => actions.Approve(id),
                    $"{prefix}.save")));
            if (fact.Kind == CoachMemoryKind.PersistentStudyGoal)
                buttons.Add(Enable(NativeForm.Bordered(style, text("Coach_MemoryEdit"), () => actions.BeginEdit(id),
                    $"{prefix}.edit")));
            buttons.Add(Enable(NativeForm.Bordered(style, text(isCandidate ? "Coach_MemoryNotNow" : "Coach_MemoryForget"),
                () => (isCandidate ? actions.Reject : actions.Forget)(id),
                isCandidate ? $"{prefix}.notnow" : $"{prefix}.forget")));
            rows.Add(Row(buttons.ToArray()));
        }
        var stack = Views.VStack(rows.ToArray()).WithFlexibleWidth();
        stack.Spacing = 6;
        return stack.WithAccessibilityIdentifier(prefix);
    }

    /// <summary>An inline confirmation row: its title, body (and impact), then its two buttons in source order.</summary>
    private static SwiftView Confirmation(SourceUiStyle style, string title, string body,
        string? impact, SwiftView first, SwiftView second, string id)
    {
        var lines = new List<SwiftView>
        {
            style.Text(Views.Text(title), SourceTextRole.Body1Strong, sourceHeading: true),
            style.Text(Views.Text(body), SourceTextRole.Body2),
        };
        if (impact is not null)
            lines.Add(style.Text(Views.Text(impact), SourceTextRole.Caption1, SourceTextTone.Secondary));
        lines.Add(Row(first, second));
        var stack = Views.VStack(lines.ToArray()).WithFlexibleWidth();
        stack.Spacing = 8;
        return stack.WithAccessibilityIdentifier(id);
    }

    private static SwiftView Danger(SourceUiStyle style, SwiftView button) =>
        button.WithButtonRole(SwiftButtonRole.Destructive)
            .WithTintColor(style.Appearance.Danger.Light, style.Appearance.Danger.Dark)
            .WithForegroundColor(style.Appearance.Danger.Light, style.Appearance.Danger.Dark);

    private static SwiftView Row(params SwiftView[] buttons)
    {
        var row = Views.HStack(buttons);
        row.Spacing = 8;
        return row;
    }
}
