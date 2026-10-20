using System.Runtime.CompilerServices;
using SentenceStudio.Common;
using SentenceStudio.Native.iOS.Features.LearningResources;

namespace SentenceStudio.Native.iOS.Tests;

public sealed class NativeResourcePickerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RequiredFormPicker_OffersOnlyCatalogValuesAndSendsRealSelection(bool mediaType)
    {
        var catalog = mediaType ? Constants.MediaTypes : Constants.Languages;
        var picker = new ResourceRequiredPicker(null, catalog);

        Assert.Equal(catalog, picker.Options.ToArray());
        Assert.Equal(0, picker.SelectedIndex);
        Assert.All(picker.Options, value => Assert.False(string.IsNullOrWhiteSpace(value)));

        string? selected = null;
        picker.Select(0, value => selected = value);
        Assert.Equal(catalog[0], selected);
        picker.Select(catalog.Length - 1, value => selected = value);
        Assert.Equal(catalog[^1], selected);
    }

    [Fact]
    public void RequiredFormPicker_LeavesUnchangedCatalogValueAlone()
    {
        var picker = new ResourceRequiredPicker("Korean", Constants.Languages);
        Assert.Equal("Korean", picker.Options[picker.SelectedIndex]);

        picker.Select(picker.SelectedIndex, _ =>
            throw new InvalidOperationException("Unchanged selections must not dirty a draft."));

        string? changed = null;
        picker.Select(0, value => changed = value);
        Assert.Equal(Constants.Languages[0], changed);
    }

    [Theory]
    [InlineData("Other")]
    [InlineData("Existing learner language")]
    public void RequiredFormPicker_RetainsReadableOutOfCatalogValueUntilChanged(string existing)
    {
        var picker = new ResourceRequiredPicker(existing, Constants.Languages);
        Assert.Equal(existing, picker.Options[picker.SelectedIndex]);
        Assert.Equal(Constants.Languages.Length + 1, picker.Options.Count);
        Assert.All(picker.Options, value => Assert.False(string.IsNullOrWhiteSpace(value)));

        picker.Select(picker.SelectedIndex, _ =>
            throw new InvalidOperationException("Existing values must survive without rewriting."));

        string? changed = null;
        picker.Select(1, value => changed = value);
        Assert.Equal(Constants.Languages[0], changed);
    }

    [Fact]
    public void RequiredFormSelect_BindsCatalogPickerWhileOptionalFilterMenusKeepAll()
    {
        var view = File.ReadAllText(Path.Combine(HeadPath,
            "Features", "LearningResources", "LearningResourceView.cs"));
        var browser = File.ReadAllText(Path.Combine(HeadPath,
            "Features", "LearningResources", "LearningResourcesView.cs"));
        var fields = File.ReadAllText(Path.Combine(HeadPath,
            "Features", "LearningResources", "LearningResourceFieldsView.cs"));
        var filter = browser[browser.IndexOf("private static SwiftView Filter(",
            StringComparison.Ordinal)..];
        var select = fields[fields.IndexOf("internal static SwiftView Select(",
            StringComparison.Ordinal)..];

        Assert.Contains("new ResourceRequiredPicker(current, options)", select);
        Assert.Contains("next => picker.Select(next, changed)", select);
        Assert.DoesNotContain("next == 0 ? null", select);
        Assert.Contains(".WithPickerStyle(SwiftPickerStyle.Menu)", select);
        Assert.Contains(".WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Leading)",
            select);
        Assert.Contains("value => actions.Change(nameof(LearningResourceViewState.MediaType), value)",
            view);
        Assert.Contains("value => actions.Change(nameof(LearningResourceViewState.Language), value)",
            view);
        Assert.Contains("var values = new[] { all }.Concat(options).ToArray();", filter);
        Assert.Contains("Views.Menu(menuLabel, choices)", filter);
        Assert.Contains("changed(choice == 0 ? null : values[choice])", filter);
        Assert.Contains("menu.Text = values[selectedIndex];", filter);
        Assert.Contains(".WithAccessibilityLabel(label)", filter);
        Assert.Contains(".WithAccessibilityLabel(values[index])", filter);
        Assert.Contains(".WithAccessibilitySelected(index == selectedIndex)", filter);
        Assert.DoesNotContain("Views.Picker(", filter);
        Assert.Contains("types, state.MediaType, actions.FilterType)", browser);
        Assert.Contains("actions.FilterLanguage)", browser);
        var coordinator = File.ReadAllText(Path.Combine(HeadPath,
            "Features", "LearningResources", "LearningResourcesCoordinator.cs"));
        Assert.Contains("_presenter.Filter(type, _presenter.List.Language);",
            coordinator);
        Assert.Contains("_presenter.Filter(_presenter.List.MediaType, language);",
            coordinator);
    }

    [Theory]
    [InlineData("native.resources.edit.media-type", "native.resources.form.media.type",
        "Constants.MediaTypes", "LearningResourceViewState.MediaType")]
    [InlineData("native.resources.edit.language", "native.resources.form.language",
        "Constants.Languages", "LearningResourceViewState.Language")]
    public void ResourceEditSelect_UsesNativeMenuWithLeadingValueAndTrailingChevron(
        string menuId, string fieldId, string catalog, string field)
    {
        var view = File.ReadAllText(Path.Combine(HeadPath,
            "Features", "LearningResources", "LearningResourceView.cs"));
        var fields = File.ReadAllText(Path.Combine(HeadPath,
            "Features", "LearningResources", "LearningResourceFieldsView.cs"));
        var form = view[view.IndexOf("internal static SwiftView Page(", StringComparison.Ordinal)
            ..view.IndexOf("internal static SwiftView WithToolbar(", StringComparison.Ordinal)];
        var select = fields[fields.IndexOf("internal static SwiftView Select(",
            StringComparison.Ordinal)..];
        var edit = select[select.IndexOf("var selectedValue = ", StringComparison.Ordinal)..];

        Assert.Equal(2, form.Split("Select(style, text(creating").Length - 1);
        Assert.Contains($"state.{field[(field.LastIndexOf('.') + 1)..]}, {catalog}", form);
        Assert.Contains($"creating ? null : \"{menuId}\"", form);
        Assert.Contains($"value => actions.Change(nameof({field}), value)", form);
        Assert.True(select.IndexOf("if (readOnly)", StringComparison.Ordinal)
            < select.IndexOf("if (editMenuId is null)", StringComparison.Ordinal));
        Assert.Contains("Views.Text(current ?? string.Empty)", select);
        Assert.Contains("Views.Picker(string.Empty, picker.Options,", select);
        Assert.Contains(".WithPickerStyle(SwiftPickerStyle.Menu)", select);
        Assert.Contains("Views.Text(selectedValue)", edit);
        Assert.Contains("Views.HStack(selectedText, chevron)", edit);
        Assert.Contains("Views.Image(\"chevron.up.chevron.down\")", edit);
        Assert.Contains("style.Input(Views.HStack(", edit);
        Assert.Contains(".WithAccessibilityIdentifier(id)", edit);
        Assert.Contains($"\"{fieldId}\"", form);
        Assert.DoesNotContain(".WithAccessibilityIdentifier(editMenuId)", edit);
        Assert.Contains(".WithAccessibilityLabel(label)", edit);
        Assert.Contains("menu.Text = selectedValue;", edit);
        Assert.Contains("Views.Menu(menuLabel, choices)", edit);
        Assert.Contains("picker.Options[index]", edit);
        Assert.Contains("picker.Select(choice, changed);", edit);
        Assert.Contains("menu!.Text = value;", edit);
        Assert.Contains("var optionLabel = index == picker.SelectedIndex", edit);
        Assert.Contains("Views.Image(\"checkmark\")", edit);
        Assert.DoesNotContain("WithOpacity(", edit);
        Assert.Contains("choices[i].WithAccessibilitySelected(i == choice);", edit);
        Assert.Contains(".WithAccessibilityLabel(value)", edit);
        Assert.DoesNotContain("actions.Save", select);
        Assert.DoesNotContain("Repository", select);
    }

    [Theory]
    [InlineData(-1, 3, (int)ResourceQuickAddKey.ArrowDown, 0)]
    [InlineData(0, 3, (int)ResourceQuickAddKey.ArrowUp, 2)]
    [InlineData(2, 3, (int)ResourceQuickAddKey.ArrowDown, 0)]
    [InlineData(1, 3, (int)ResourceQuickAddKey.ArrowUp, 0)]
    [InlineData(0, 0, (int)ResourceQuickAddKey.ArrowDown, -1)]
    [InlineData(0, 0, (int)ResourceQuickAddKey.ArrowUp, -1)]
    public void QuickAdd_KeyboardHighlightWrapsWithinOwnerSuggestions(
        int current, int count, int key, int expected) =>
        Assert.Equal(expected, ResourceQuickAddNavigation.Move(
            current, count, (ResourceQuickAddKey)key));

    private static string HeadPath => Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(CurrentFile())!, "../../src/SentenceStudio.Native.iOS"));

    private static string CurrentFile([CallerFilePath] string file = "") => file;
}
