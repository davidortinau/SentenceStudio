using System.Collections.Immutable;
using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Styling;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.Features.Dashboard;

internal static class ChooseOwnSelectorView
{
    internal static SwiftView Page(
        SourceUiStyle style, string? selector, NativeChooseOwnState? selection,
        ImmutableArray<NativeChooseOwnOption> resourceOrder,
        string search, Func<string, string> text, Action<string?> choose,
        Action dismiss)
    {
        if (selector is not { } kind || selection is not { } selected)
            return Views.Text(text("Common_Loading"));
        var isResource = kind == "resources";
        var prefix = isResource
            ? "native.dashboard.chooseown.resource"
            : "native.dashboard.chooseown.skill";
        var options = isResource ? resourceOrder : selected.Skills;
        var visible = (options.IsDefault
                ? ImmutableArray<NativeChooseOwnOption>.Empty
                : options)
            .Where(option => option.Title.Contains(
                search, StringComparison.CurrentCultureIgnoreCase))
            .ToArray();
        var items = new List<SwiftView>();
        if (!isResource)
            items.Add(Views.Button(text("Common_Clear"), () => choose(null))
                .WithAccessibilityIdentifier($"{prefix}.clear"));
        foreach (var option in visible)
        {
            var id = option.Id;
            if (isResource)
            {
                // Issue 244: several can be chosen, so each row is a system switch at its
                // trailing edge, not a drawn mark after the title. choose flips the
                // resource, so only a switch that differs from the selection is passed on.
                var chosen = !selected.SelectedResourceIds.IsDefault
                    && selected.SelectedResourceIds.Contains(id);
                items.Add(NativeForm.Switch(style, option.Title, chosen,
                    isOn =>
                    {
                        if (isOn != chosen)
                            choose(id);
                    }, $"{prefix}.{Uri.EscapeDataString(id)}"));
                continue;
            }
            items.Add(Views.HStack(
                Views.Button(option.Title, () => choose(id))
                    .WithAccessibilityIdentifier(
                        $"{prefix}.{Uri.EscapeDataString(id)}"),
                Views.Image(selected.SelectedSkillId == id ? "checkmark" : "circle"))
                .WithAccessibilityIdentifier(
                    $"{prefix}.{Uri.EscapeDataString(id)}.row"));
        }
        var content = style.ThemedList(Views.List(items.ToArray()))
            .WithAccessibilityIdentifier($"{prefix}.screen")
            .WithNavigationTitle(text(isResource
                ? "Dashboard_LearningResourcesLabel" : "Dashboard_SkillProfileLabel"))
            .WithInlineNavigationTitle();
        return Views.Toolbar(content,
            Views.Button(text("Common_Done"), dismiss)
                .WithAccessibilityIdentifier($"{prefix}.done"));
    }
}
