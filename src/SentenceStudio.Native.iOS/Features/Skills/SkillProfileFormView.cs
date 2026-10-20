using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Platform;
using SentenceStudio.Native.iOS.Styling;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.Features.Skills;

internal sealed record SkillProfileFormActions(
    Action<string> TitleChanged, Action<string> DescriptionChanged,
    Action Save, Action Cancel, Action Retry, Action RequestDelete);

internal static class SkillProfileFormView
{
    internal static SwiftView Page(SkillProfileFormState state,
        SourceUiStyle style, Func<string, string> text,
        SkillProfileFormActions actions)
    {
        var fields = new List<SwiftView>();
        var busy = state.Phase is SkillFormPhase.Saving or SkillFormPhase.Deleting;
        if (state.Phase is SkillFormPhase.Loading or SkillFormPhase.Saving
            or SkillFormPhase.Deleting)
            fields.Add(Views.IndeterminateProgress(text(state.Phase switch
                {
                    SkillFormPhase.Loading => "Common_Loading",
                    SkillFormPhase.Deleting => "Common_Delete",
                    _ => "Common_Save"
                }))
                .WithAccessibilityIdentifier("native.skills.form.progress"));
        if (state.ErrorKey is { } error)
            fields.Add(style.Alert(Views.Text(Error(error, text)), success: false)
                .WithAccessibilityIdentifier("native.skills.form.error"));

        var editable = state.Phase is SkillFormPhase.Creating
            or SkillFormPhase.Editing or SkillFormPhase.SaveError
            or SkillFormPhase.DeleteError or SkillFormPhase.Saving
            or SkillFormPhase.Deleting;
        if (editable)
        {
            var title = style.Input(Views.TextField(
                    state.Creating ? text("SkillAdd_TitlePlaceholder") : string.Empty,
                    state.Title, actions.TitleChanged))
                .WithAccessibilityLabel(text("SkillAdd_TitleLabel"))
                .WithAccessibilityIdentifier("native.skills.form.title");
            title.IsEnabled = !busy;
            var description = Views.TextEditor(state.Description,
                    actions.DescriptionChanged)
                .WithTextEditorScrollBackgroundHidden();
            if (state.Creating)
                description.WithTextEditorPlaceholder(
                        text("SkillAdd_DescriptionPlaceholder"))
                    .WithPlaceholderColor(style.Appearance.TertiaryText.Light,
                        style.Appearance.TertiaryText.Dark);
            var editor = style.Input(description)
                .WithMinimumHeight(DescriptionHeight(style))
                .WithAccessibilityLabel(text("SkillAdd_DescriptionLabel"))
                .WithAccessibilityIdentifier("native.skills.form.description");
            editor.IsEnabled = !busy;
            fields.Add(Card(style, "native.skills.form.fields",
            [
                Views.VStack(
                    style.Text(Views.Text(text("SkillAdd_TitleLabel")),
                        SourceTextRole.Body2, SourceTextTone.Secondary),
                    title),
                Views.VStack(
                    style.Text(Views.Text(text("SkillAdd_DescriptionLabel")),
                        SourceTextRole.Body2, SourceTextTone.Secondary),
                    editor)
            ]));

            if (!state.Creating && state.CreatedAt is { } created
                && state.UpdatedAt is { } updated)
            {
                var dates = Views.HStack(
                        Date(style, text("SkillEdit_Created"),
                            NativeResourceDateFormatter.DetailCreatedDate(
                                created, isSmartResource: false)),
                        Date(style, text("SkillEdit_Updated"),
                            NativeResourceDateFormatter.DetailUpdatedDate(updated)))
                    .WithAdaptiveStack(680)
                    .WithFlexibleWidth();
                dates.Spacing = SourceUiLayout.ItemGap;
                fields.Add(Card(style, "native.skills.form.details",
                [
                    style.Text(Views.Text(text("SkillEdit_DetailsTitle")),
                        SourceTextRole.Title3, sourceHeading: true),
                    dates
                ]));
            }

            if (state.Creating)
                fields.Add(style.PrimaryButton(
                        text("SkillAdd_SaveSkillProfile"), actions.Save,
                        fullWidth: true)
                    .WithAccessibilityIdentifier("native.skills.form.save.bottom"));
        }
        else if (state.Phase == SkillFormPhase.LoadError)
            fields.Add(style.SecondaryButton(text(NativeText.Retry), actions.Retry)
                .WithAccessibilityIdentifier("native.skills.form.retry"));

        var cancel = style.SecondaryButton(text("Common_Cancel"), actions.Cancel)
            .WithAccessibilityIdentifier("native.skills.form.cancel");
        cancel.IsEnabled = !busy;
        fields.Add(cancel);

        var content = Views.VStack(fields.ToArray())
            .WithPadding(SourceUiLayout.PageGutter(style.ViewportWidth))
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.skills.form.scroll.content");
        content.Spacing = SourceUiLayout.FormGap;
        var page = style.Page(Views.Scroll(content)
                .WithAccessibilityIdentifier("native.skills.form.scroll"))
            .WithAccessibilityIdentifier(state.Creating
                ? "native.skills.add.screen" : "native.skills.edit.screen");
        if (!editable)
            return page;

        var menuSave = Views.Button(text("Common_Save"), actions.Save)
            .WithAccessibilityIdentifier("native.skills.form.save.menu");
        menuSave.IsEnabled = !busy;
        var menuActions = new List<SwiftView> { menuSave };
        if (!state.Creating)
        {
            var delete = Views.Button(text("Common_Delete"), actions.RequestDelete)
                .WithAccessibilityIdentifier("native.skills.edit.delete.request");
            delete.IsEnabled = !busy;
            menuActions.Add(delete);
        }
        var more = Views.Menu(text("More"), menuActions.ToArray())
            .WithAccessibilityIdentifier("native.skills.form.more");
        more.IsEnabled = !busy;
        if (style.ViewportWidth < 768)
            return Views.Toolbar(page, more);
        var primary = style.PrimaryButton(text("Common_Save"), actions.Save)
            .WithAccessibilityIdentifier("native.skills.form.save.primary");
        primary.IsEnabled = !busy;
        return Views.Toolbar(page, primary, more);
    }

    internal static string Error(string? errorKey, Func<string, string> text)
    {
        var key = errorKey ?? NativeText.UnexpectedError;
        var message = text(key);
        return key is "SkillAdd_SaveFailed" or "SkillEdit_LoadFailed"
            or "SkillEdit_SaveFailed" or "SkillEdit_DeleteFailed"
            ? string.Format(CultureInfo.CurrentCulture, message,
                text(NativeText.UnexpectedError))
            : message;
    }

    private static SwiftView Date(
        SourceUiStyle style, string name, string value) =>
        Views.HStack(
                style.Text(Views.Text(name), SourceTextRole.Body2Strong),
                style.Text(Views.Text(value), SourceTextRole.Body2))
            .WithAccessibilityLabel($"{name}: {value}");

    private static SwiftView Card(
        SourceUiStyle style, string id, IEnumerable<SwiftView> children)
    {
        var card = Views.VStack(children.ToArray())
            .WithPadding(SourceUiLayout.LargeCardInset)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier(id);
        card.Spacing = SourceUiLayout.FormGap;
        return style.Card(card);
    }

    private static double DescriptionHeight(SourceUiStyle style)
    {
        var font = SourceUiTypography.For(SourceTextRole.Input,
            style.ViewportWidth, style.Appearance.Selection.FontScale,
            style.Appearance.Selection.ThemeId);
        return 6 * font.Size * font.LineHeight + 2 * SourceUiLayout.ItemGap;
    }
}
