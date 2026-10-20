using System.Collections.Immutable;
using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Styling;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.Features.Dashboard;

internal static class ChooseOwnSelectorView
{
    internal static SwiftView Page(
        SourceUiStyle style, string? selector, NativeChooseOwnState? selection,
        string search, Func<string, string> text, Action<string?> choose,
        Action dismiss)
    {
        if (selector is not { } kind || selection is not { } selected)
            return Views.Text(text("Common_Loading"));
        var isResource = kind == "resources";
        var prefix = isResource
            ? "native.dashboard.chooseown.resource"
            : "native.dashboard.chooseown.skill";
        var options = isResource ? selected.Resources : selected.Skills;
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
            var chosen = isResource
                ? !selected.SelectedResourceIds.IsDefault
                  && selected.SelectedResourceIds.Contains(id)
                : selected.SelectedSkillId == id;
            items.Add(Views.HStack(
                Views.Button(option.Title, () => choose(id))
                    .WithAccessibilityIdentifier(
                        $"{prefix}.{Uri.EscapeDataString(id)}"),
                Views.Image(chosen ? "checkmark" : "circle"))
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
