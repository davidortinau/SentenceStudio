using MauiBootstrapTheme.Theming;

namespace SentenceStudio.Helpers;

/// <summary>
/// Extension to fix Picker background on dark themes. The generated Bootstrap theme
/// sets BackgroundProperty = DR("InputBackground") on Entry/Editor implicit styles but
/// NOT on Picker. This extension reads the current InputBackground from the resource
/// dictionary and applies it alongside the form-select class.
/// </summary>
public static class BootstrapPickerExtensions
{
    public static MauiReactor.Picker FormSelect(this MauiReactor.Picker picker)
    {
        Color bg = Colors.Transparent;
        if (Application.Current?.Resources.TryGetValue("InputBackground", out var val) == true && val is Color c)
            bg = c;
        return picker.Class("form-select").BackgroundColor(bg);
    }
}
