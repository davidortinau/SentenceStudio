using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Features.Browse;
using SentenceStudio.Native.iOS.Styling;
using SentenceStudio.Shared.Models;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.Features.Skills;

/// <summary>
/// The Skills list's actions. Each must not depend on the state it was built with (the page
/// updates in place and a menu keeps its first handlers).
/// </summary>
internal sealed record SkillsListActions(
    Action Add, Action<string> Edit, Action Retry, Action<bool> ShowArchived,
    Action<string> ToggleArchived, Action<string> RequestDelete, Action ConfirmDelete,
    Action CancelDelete, Action<string>? Search = null);

internal static class SkillsView
{
    internal static SwiftView Page(
        SkillsListState state, SourceUiStyle style, Func<string, string> text,
        SkillsListActions actions, string? pendingDeleteTitle = null)
    {
        var items = new List<SwiftView>();
        if (state.FeedbackKey is { } feedback)
            items.Add(style.Alert(Views.Text(SkillProfileFormView.Error(feedback, text)),
                    success: !state.FeedbackIsError)
                .WithAccessibilityIdentifier("native.skills.feedback"));

        switch (state.Phase)
        {
            case SkillsListPhase.Loading:
                items.Add(Views.IndeterminateProgress(text("Common_Loading"))
                    .WithAccessibilityIdentifier("native.skills.loading"));
                break;
            case SkillsListPhase.Blocked:
            case SkillsListPhase.LoadError:
                items.Add(style.Alert(Views.Text(SkillProfileFormView.Error(
                            state.ErrorKey, text)), success: false)
                    .WithAccessibilityIdentifier("native.skills.error"));
                if (state.Phase == SkillsListPhase.LoadError)
                    items.Add(style.SecondaryButton(text(NativeText.Retry), actions.Retry)
                        .WithAccessibilityIdentifier("native.skills.retry"));
                break;
            case SkillsListPhase.Empty when state.Archived:
                items.Add(EmptyText(style, text("Skills_NoArchived")));
                break;
            case SkillsListPhase.Empty:
                var empty = Views.VStack(
                        style.Text(Views.Text(text("Skills_NoProfilesYet")),
                            SourceTextRole.Body1, SourceTextTone.Secondary),
                        style.PrimaryButton(text("Skills_AddFirstProfile"), actions.Add)
                            .WithAccessibilityIdentifier("native.skills.empty.add"))
                    .WithPadding(48)
                    .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
                    .WithAccessibilityIdentifier("native.skills.empty");
                empty.Spacing = SourceUiLayout.LargeCardInset;
                items.Add(empty);
                break;
            case SkillsListPhase.Populated when state.Profiles.Count == 0:
                items.Add(EmptyText(style, text("Common_NoResults")));
                break;
            case SkillsListPhase.Populated:
                // Captain 2026-11-12: Library lists are native Lists, icons kept, with swipe
                // actions (Delete, and Archive or Restore for skills).
                items.AddRange(state.Profiles.Select(profile =>
                    Row(profile, state, style, text, actions)));
                break;
        }

        // Captain 2026-11-13: the system search field, as the other Library lists have. One
        // outline in every state (a native List under the field), so the page updates in place
        // and the field keeps its focus while typing.
        SwiftView body = BrowserPage.SingleColumn("native.skills", items, style.ViewportWidth,
            new BrowserPage.PageSearch(text("Common_Search"), state.Search,
                value => actions.Search?.Invoke(value), [], _ => { }),
            style);
        body = DeleteConfirmation(body, pendingDeleteTitle, text, actions);
        var ready = state.Phase is SkillsListPhase.Empty or SkillsListPhase.Populated;
        // Captain 2026-11-10: the same plain Add (+) as every browse page, at every width (the
        // one action the source More menu held).
        var add = BrowserPage.AddButton("native.skills", text("Skills_AddSkillProfile"),
            // New skills are active, so Add belongs to Skill Profiles, not the archive.
            actions.Add, ready && !state.Busy && !state.Archived);
        return Views.Toolbar(
                style.Page(body).WithAccessibilityIdentifier("native.skills.screen"),
                ShowMenu(state, text, actions, ready), add);
    }

    private static SwiftView EmptyText(SourceUiStyle style, string message) =>
        style.Text(Views.Text(message), SourceTextRole.Body1, SourceTextTone.Secondary)
            .WithPadding(48)
            .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
            .WithAccessibilityIdentifier("native.skills.empty");

    /// <summary>
    /// Show: the learner's skill profiles or the archived ones, a checkmark on the one shown
    /// (the collection lives in the list's menu; the symbol is filled while the archive shows).
    /// </summary>
    private static SwiftView ShowMenu(SkillsListState state, Func<string, string> text,
        SkillsListActions actions, bool ready)
    {
        var picker = Views.Picker(text("Skills_Show"),
                [text("Skills_ShowActive"), text("Skills_ShowArchived")],
                state.Archived ? 1 : 0, index => actions.ShowArchived(index == 1))
            .WithAccessibilityIdentifier("native.skills.show");
        return BrowserPage.FilterMenu("native.skills", text("Skills_Show"), state.Archived,
            (ready || state.Archived) && !state.Busy, [picker]);
    }

    /// <summary>
    /// A swipe Delete asks first (a native confirmation dialog naming the skill). The dialog is
    /// always there, shown while a delete waits, so the page updates in place.
    /// </summary>
    private static SwiftView DeleteConfirmation(SwiftView page, string? pendingTitle,
        Func<string, string> text, SkillsListActions actions)
    {
        var dialog = Views.ConfirmationDialog(page,
                string.Format(CultureInfo.CurrentCulture,
                    text("Skills_DeleteConfirm"), pendingTitle ?? string.Empty),
                actions.CancelDelete,
                Views.Button(text("Common_Delete"), actions.ConfirmDelete)
                    .WithButtonRole(SwiftButtonRole.Destructive)
                    .WithAccessibilityIdentifier("native.skills.delete.confirm"),
                Views.Button(text("Common_Cancel"), actions.CancelDelete)
                    .WithButtonRole(SwiftButtonRole.Cancel)
                    .WithAccessibilityIdentifier("native.skills.delete.cancel"))
            .WithAccessibilityIdentifier("native.skills.delete.dialog");
        dialog.IsPresented = pendingTitle is not null;
        return dialog;
    }

    /// <summary>
    /// A native List row: the skill icon, title and description. Swipe left for Delete (asks
    /// first) and Archive, or Restore in the archive. No action runs on a full swipe.
    /// </summary>
    private static SwiftView Row(SkillProfile profile, SkillsListState state,
        SourceUiStyle style, Func<string, string> text, SkillsListActions actions)
    {
        var title = profile.Title ?? string.Empty;
        var lines = new List<SwiftView>
        {
            style.Text(Views.Text(title), SourceTextRole.ListTitle)
                .WithLineLimit(1)
                .WithFlexibleWidth()
                .WithAccessibilityIdentifier($"native.skills.item.{profile.Id}.title")
        };
        if (!string.IsNullOrEmpty(profile.Description))
            lines.Add(style.Text(Views.Text(profile.Description),
                    SourceTextRole.Small, SourceTextTone.Secondary)
                .WithLineLimit(1)
                .WithFlexibleWidth()
                .WithAccessibilityIdentifier(
                    $"native.skills.item.{profile.Id}.description"));
        var terms = Views.VStack(lines.ToArray()).WithFlexibleWidth();
        terms.Spacing = 2;
        var icon = Views.Image(state.Archived ? "archivebox" : "target")
            .WithForegroundColor(style.Appearance.Primary.Light, style.Appearance.Primary.Dark);
        var label = Views.HStack(icon, terms).WithFlexibleWidth();
        label.Spacing = SourceUiLayout.ItemGap;
        var row = Views.Button(label, () => actions.Edit(profile.Id))
            .WithButtonStyle(SwiftButtonStyle.Plain)
            .WithFlexibleWidth()
            .WithMinimumHeight(SourceUiLayout.MinimumTouchHeight)
            .WithAccessibilityLabel(string.IsNullOrEmpty(profile.Description) ? title
                : $"{title}, {profile.Description}")
            .WithAccessibilityIdentifier($"native.skills.item.{profile.Id}");
        var trailing = new[] {
            // Red, not destructive: a destructive swipe action removes its row at once, and
            // this one asks first.
            Views.Button(Views.Label(text("Common_Delete"), "trash"),
                    () => actions.RequestDelete(profile.Id))
                .WithTintColor(style.Appearance.Danger.Light, style.Appearance.Danger.Dark)
                .WithAccessibilityIdentifier($"native.skills.item.{profile.Id}.delete"),
            state.Archived
                ? Views.Button(Views.Label(text("Skills_Restore"), "arrow.uturn.backward"),
                        () => actions.ToggleArchived(profile.Id))
                    .WithTintColor(style.Appearance.Primary.Light, style.Appearance.Primary.Dark)
                    .WithAccessibilityIdentifier($"native.skills.item.{profile.Id}.restore")
                : Views.Button(Views.Label(text("Skills_Archive"), "archivebox"),
                        () => actions.ToggleArchived(profile.Id))
                    .WithTintColor(style.Appearance.Accent.Light, style.Appearance.Accent.Dark)
                    .WithAccessibilityIdentifier($"native.skills.item.{profile.Id}.archive")
        };
        return Views.SwipeRow(row, trailing)
            .WithAccessibilityIdentifier($"native.skills.item.{profile.Id}.swipe");
    }
}
