using System.Globalization;
using AndroidX.Compose;
using SentenceStudio.Contracts.LearnerMemory;
using SentenceStudio.Native;

namespace SentenceStudio.Native.Android;

/// <summary>
/// Settings "Learning Coach" card and "What Sam remembers" card (source Settings.razor
/// coach section + CoachMemoryPanel), matching the iOS SamSettingsView. Compose only;
/// confirmations render inline in the card they belong to.
/// </summary>
internal sealed partial class AndroidSliceShell
{
    private ComposableNode? RenderSamSettings(AndroidVisualPalette palette)
    {
        if (_preview || _sam is null || !HasDashboardOwner(Auth.UserId))
            return null;
        var state = SamState;
        var column = new Column(Arrangement.SpacedBy(12))
        {
            Modifier.FillMaxWidth().TestTag("native.sam.settings"),
            SamCoachSettings(state.SafeSettings, palette),
        };
        if (SamMemoryPanel(state.SafeMemory, palette) is { } memory)
            column.Add(memory);
        return column;
    }

    private ComposableNode SamCoachSettings(NativeSamSettingsState settings, AndroidVisualPalette palette)
    {
        var rows = new List<ComposableNode>
        {
            Heading(Localize("Coach_SettingsSection"), palette, 20,
                Modifier.Semantics(semantics => semantics.Heading())),
            Caption(Localize("Coach_SettingsDescription"), palette),
        };
        if (!settings.IsAvailable)
            rows.Add(Caption(Localize("Coach_SettingsUnavailable"), palette, "native.sam.settings.unavailable"));
        else
        {
            rows.Add(Action(Localize(settings.Deleting ? "Common_Loading" : "Coach_SettingsDelete"),
                "native.sam.settings.delete", AskDeleteSamHistory,
                !settings.Deleting && !settings.ConfirmingDelete, primary: false));
            rows.Add(Caption(Localize("Coach_DeleteConfirm"), palette));
            if (settings.ConfirmingDelete)
                rows.Add(SamCard(palette, "native.sam.settings.delete.dialog", SamCardStyle.Learner,
                [
                    Strong(Localize("Coach_DeleteDialogTitle"), palette, heading: true),
                    Body(Localize("Coach_DeleteDialogBody"), palette),
                    SamButtonRow([
                        SamButton(Localize("Coach_Cancel"), "native.sam.settings.delete.cancel",
                            CancelDeleteSamHistory, !settings.Deleting, AndroidSamButtonStyle.Secondary, palette),
                        SamButton(Localize("Coach_DeleteDialogConfirm"), "native.sam.settings.delete.confirm",
                            ConfirmDeleteSamHistory, !settings.Deleting, AndroidSamButtonStyle.Danger, palette),
                    ]),
                ], spacing: 8));
        }
        if (settings.NoticeKey is { } notice)
        {
            var text = Body(Localize(notice), palette, secondary: notice != "Coach_SettingsDeleted",
                modifier: Modifier.TestTag("native.sam.settings.notice"));
            if (notice == "Coach_SettingsDeleted")
                text.Color = palette.Success;
            rows.Add(text);
        }
        return Card(palette, "native.sam.settings.coach", rows.ToArray());
    }

    private ComposableNode? SamMemoryPanel(NativeSamMemoryState memory, AndroidVisualPalette palette)
    {
        // The whole surface disappears when the feature is off or the data is not this learner's.
        if (memory.Availability != NativeSamMemoryAvailability.Available)
            return null;
        var rows = new List<ComposableNode>
        {
            Heading(Localize("Coach_MemoryTitle"), palette, 20,
                Modifier.Semantics(semantics => semantics.Heading())),
            Body(Localize("Coach_MemoryExplainer"), palette, secondary: true),
            Caption(Localize("Coach_MemoryVsSettings"), palette),
        };
        Tab MemoryTab(NativeSamMemoryTab tab, string key, int count, string id)
        {
            var title = string.Format(CultureInfo.CurrentCulture, Localize(key), count);
            // Material Tab publishes Role.Tab + selected state (source role=tablist) as one node.
            return new Tab(memory.Tab == tab, () => SelectSamMemoryTab(tab), true)
            {
                Modifier = Modifier.TestTag(id),
                Text = new Text(title),
            };
        }
        rows.Add(new TabRow(memory.Tab == NativeSamMemoryTab.Active ? 0 : 1)
        {
            Modifier.FillMaxWidth(),
            MemoryTab(NativeSamMemoryTab.Active, "Coach_MemoryTabActive", memory.SafeActive.Length,
                "native.sam.memory.tab.active"),
            MemoryTab(NativeSamMemoryTab.Candidates, "Coach_MemoryTabCandidates", memory.SafeCandidates.Length,
                "native.sam.memory.tab.candidates"),
        });
        if (memory.NoticeKey is { } notice)
            rows.Add(Caption(Localize(notice), palette, "native.sam.memory.panel.notice"));
        if (memory.Tab == NativeSamMemoryTab.Active)
        {
            if (memory.IsPaused)
                rows.Add(Body(Localize("Coach_MemoryPaused"), palette, secondary: true,
                    modifier: Modifier.TestTag("native.sam.memory.paused")));
            else
            {
                for (var index = 0; index < memory.SafeActive.Length; index++)
                    rows.Add(SamMemoryFact(memory, memory.SafeActive[index], index, isCandidate: false, palette));
                rows.Add(Action(Localize("Coach_MemoryForgetAll"), "native.sam.memory.forgetall",
                    AskForgetAllSamMemories, !memory.Busy, primary: false));
                rows.Add(Caption(Localize("Coach_MemoryForgetAllImpact"), palette));
                if (memory.ConfirmingForgetAll)
                    rows.Add(SamCard(palette, "native.sam.memory.forgetall.dialog", SamCardStyle.Learner,
                    [
                        Strong(Localize("Coach_MemoryForgetAllTitle"), palette, heading: true),
                        Body(Localize("Coach_MemoryForgetAllBody"), palette),
                        Caption(Localize("Coach_MemoryForgetAllImpact"), palette),
                        SamButtonRow([
                            SamButton(Localize("Coach_MemoryForgetAllConfirm"), "native.sam.memory.forgetall.confirm",
                                ConfirmForgetAllSamMemories, !memory.Busy, AndroidSamButtonStyle.Danger, palette),
                            SamButton(Localize("Coach_Cancel"), "native.sam.memory.forgetall.cancel",
                                CancelForgetAllSamMemories, !memory.Busy, AndroidSamButtonStyle.Secondary, palette),
                        ]),
                    ], spacing: 8));
            }
        }
        else if (memory.SafeCandidates.IsEmpty)
            rows.Add(Body(Localize("Coach_MemoryNoCandidates"), palette, secondary: true,
                modifier: Modifier.TestTag("native.sam.memory.nocandidates")));
        else
            for (var index = 0; index < memory.SafeCandidates.Length; index++)
                rows.Add(SamMemoryFact(memory, memory.SafeCandidates[index], index, isCandidate: true, palette));
        if (memory.Busy)
            rows.Add(SamProgress(Localize("Common_Loading"), palette, "native.sam.memory.busy"));
        return Card(palette, "native.sam.memory.panel", rows.ToArray());
    }

    private ComposableNode SamMemoryFact(NativeSamMemoryState memory, CoachMemoryFactDto fact, int index,
        bool isCandidate, AndroidVisualPalette palette)
    {
        var prefix = isCandidate ? $"native.sam.memory.candidate.{index}" : $"native.sam.memory.active.{index}";
        var rows = new List<ComposableNode>
        {
            Body(fact.DisplayText, palette, modifier: Modifier.TestTag(prefix + ".value")),
            Caption(AndroidSamCards.MemoryMeta(fact, Localize), palette),
        };
        var busy = memory.Busy;
        var id = fact.Id;
        if (memory.EditingId == id)
            rows.Add(SamEditor(_samMemoryEdit, Localize("Coach_MemoryEditLabel"), prefix + ".editfield",
                text => _sam?.SetMemoryEditText(text), typed => SamButtonRow([
                    SamButton(Localize("Coach_MemorySaveEdited"), prefix + ".saveedited", SaveSamMemoryEdit,
                        AndroidSamCards.CanSaveEdit(typed, busy), AndroidSamButtonStyle.Primary, palette),
                    SamButton(Localize("Coach_Cancel"), prefix + ".canceledit", CancelSamMemoryEdit, !busy,
                        AndroidSamButtonStyle.Secondary, palette),
                ])));
        else
        {
            var buttons = new List<ComposableNode>();
            if (isCandidate)
                buttons.Add(SamButton(Localize("Coach_MemorySave"), prefix + ".save", () => ApproveSamMemory(id),
                    !busy, AndroidSamButtonStyle.Primary, palette));
            if (AndroidSamCards.IsEditable(fact))
                buttons.Add(SamButton(Localize("Coach_MemoryEdit"), prefix + ".edit", () => BeginSamMemoryEdit(id),
                    !busy, AndroidSamButtonStyle.Secondary, palette));
            buttons.Add(isCandidate
                ? SamButton(Localize("Coach_MemoryNotNow"), prefix + ".notnow", () => RejectSamMemory(id), !busy,
                    AndroidSamButtonStyle.Secondary, palette)
                : SamButton(Localize("Coach_MemoryForget"), prefix + ".forget", () => ForgetSamMemory(id), !busy,
                    AndroidSamButtonStyle.Secondary, palette));
            rows.Add(SamButtonRow(buttons));
        }
        return SamCard(palette, prefix, SamCardStyle.Learner, rows, spacing: 4);
    }
}
