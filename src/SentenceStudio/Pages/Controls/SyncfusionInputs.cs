using MauiReactor;
using MauiReactor.Internals;

namespace SentenceStudio.Pages.Controls.Inputs;

/// <summary>
/// Manual wrapper for SfAutocomplete - can't use Scaffold due to internal base classes in Syncfusion
/// Follows the same pattern as SfComboBox in SyncfusionControls.cs
/// </summary>
public partial class SfAutocomplete : MauiReactor.VisualNode<Syncfusion.Maui.Inputs.SfAutocomplete>
{
    private System.EventHandler<Syncfusion.Maui.Inputs.SelectionChangedEventArgs>? _selectionChangedHandler;

    public SfAutocomplete()
    {
    }

    protected override void OnMount()
    {
        _nativeControl ??= new Syncfusion.Maui.Inputs.SfAutocomplete();
        base.OnMount();

        if (_selectionChangedHandler != null && NativeControl != null)
        {
            ((Syncfusion.Maui.Inputs.SfAutocomplete)NativeControl).SelectionChanged += _selectionChangedHandler;
        }
    }

    protected override void OnUnmount()
    {
        if (_selectionChangedHandler != null && NativeControl != null)
        {
            ((Syncfusion.Maui.Inputs.SfAutocomplete)NativeControl).SelectionChanged -= _selectionChangedHandler;
        }

        base.OnUnmount();
    }

    internal void SetSelectionChangedHandler(System.EventHandler<Syncfusion.Maui.Inputs.SelectionChangedEventArgs>? handler)
    {
        if (_selectionChangedHandler != null && NativeControl != null)
        {
            ((Syncfusion.Maui.Inputs.SfAutocomplete)NativeControl).SelectionChanged -= _selectionChangedHandler;
        }

        _selectionChangedHandler = handler;

        if (_selectionChangedHandler != null && NativeControl != null)
        {
            ((Syncfusion.Maui.Inputs.SfAutocomplete)NativeControl).SelectionChanged += _selectionChangedHandler;
        }
    }
}

// Extension methods for SfAutocomplete properties
public static partial class SfAutocompleteExtensions
{
    public static SfAutocomplete ItemsSource(this SfAutocomplete autocomplete, System.Collections.IEnumerable? itemsSource)
    {
        autocomplete.Set(Syncfusion.Maui.Inputs.DropDownControls.DropDownListBase.ItemsSourceProperty, itemsSource);
        return autocomplete;
    }

    public static SfAutocomplete DisplayMemberPath(this SfAutocomplete autocomplete, string displayMemberPath)
    {
        autocomplete.Set(Syncfusion.Maui.Inputs.DropDownControls.DropDownListBase.DisplayMemberPathProperty, displayMemberPath);
        return autocomplete;
    }

    public static SfAutocomplete TextMemberPath(this SfAutocomplete autocomplete, string textMemberPath)
    {
        autocomplete.Set(Syncfusion.Maui.Inputs.DropDownControls.DropDownListBase.TextMemberPathProperty, textMemberPath);
        return autocomplete;
    }

    public static SfAutocomplete SelectedItem(this SfAutocomplete autocomplete, object? selectedItem)
    {
        autocomplete.Set(Syncfusion.Maui.Inputs.DropDownControls.DropDownListBase.SelectedItemProperty, selectedItem);
        return autocomplete;
    }

    public static SfAutocomplete Text(this SfAutocomplete autocomplete, string text)
    {
        autocomplete.Set(Syncfusion.Maui.Inputs.DropDownControls.DropDownListBase.TextProperty, text);
        return autocomplete;
    }

    public static SfAutocomplete Placeholder(this SfAutocomplete autocomplete, string placeholder)
    {
        autocomplete.Set(Syncfusion.Maui.Inputs.SfAutocomplete.PlaceholderProperty, placeholder);
        return autocomplete;
    }

    public static SfAutocomplete PlaceholderColor(this SfAutocomplete autocomplete, Color color)
    {
        autocomplete.Set(Syncfusion.Maui.Inputs.SfAutocomplete.PlaceholderColorProperty, color);
        return autocomplete;
    }

    public static SfAutocomplete TextSearchMode(this SfAutocomplete autocomplete, Syncfusion.Maui.Inputs.AutocompleteTextSearchMode mode)
    {
        autocomplete.Set(Syncfusion.Maui.Inputs.SfAutocomplete.TextSearchModeProperty, mode);
        return autocomplete;
    }

    public static SfAutocomplete SelectionMode(this SfAutocomplete autocomplete, Syncfusion.Maui.Inputs.AutocompleteSelectionMode mode)
    {
        autocomplete.Set(Syncfusion.Maui.Inputs.SfAutocomplete.SelectionModeProperty, mode);
        return autocomplete;
    }

    public static SfAutocomplete ShowBorder(this SfAutocomplete autocomplete, bool show)
    {
        autocomplete.Set(Syncfusion.Maui.Inputs.SfAutocomplete.ShowBorderProperty, show);
        return autocomplete;
    }

    public static SfAutocomplete BackgroundColor(this SfAutocomplete autocomplete, Color color)
    {
        autocomplete.Set(Syncfusion.Maui.Inputs.SfAutocomplete.BackgroundColorProperty, color);
        return autocomplete;
    }

    public static SfAutocomplete TextColor(this SfAutocomplete autocomplete, Color color)
    {
        autocomplete.Set(Syncfusion.Maui.Inputs.SfAutocomplete.TextColorProperty, color);
        return autocomplete;
    }

    public static SfAutocomplete OnSelectionChanged(this SfAutocomplete autocomplete, EventHandler<Syncfusion.Maui.Inputs.SelectionChangedEventArgs>? handler)
    {
        autocomplete.SetSelectionChangedHandler(handler);
        return autocomplete;
    }

    public static SfAutocomplete DropDownHeight(this SfAutocomplete autocomplete, double height)
    {
        autocomplete.Set(Syncfusion.Maui.Inputs.SfAutocomplete.MaxDropDownHeightProperty, height);
        return autocomplete;
    }

    public static SfAutocomplete HeightRequest(this SfAutocomplete autocomplete, double value)
    {
        autocomplete.Set(Syncfusion.Maui.Inputs.SfAutocomplete.HeightRequestProperty, value);
        return autocomplete;
    }

    public static SfAutocomplete MinimumPrefixCharacters(this SfAutocomplete autocomplete, int count)
    {
        autocomplete.Set(Syncfusion.Maui.Inputs.SfAutocomplete.MinimumPrefixCharactersProperty, count);
        return autocomplete;
    }

    public static SfAutocomplete IsClearButtonVisible(this SfAutocomplete autocomplete, bool visible)
    {
        autocomplete.Set(Syncfusion.Maui.Inputs.SfAutocomplete.IsClearButtonVisibleProperty, visible);
        return autocomplete;
    }

}
