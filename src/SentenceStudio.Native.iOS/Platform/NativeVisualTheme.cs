using SentenceStudio.Contracts.Theme;
using UIKit;

namespace SentenceStudio.Native.iOS.Platform;

internal static class NativeVisualTheme
{
    internal const int PagePadding = 16;
    internal const int CardPadding = 16;
    internal const int CardRadius = 16;
    internal const int ControlRadius = 10;
    internal const int ControlHeight = 48;
    internal const int QuizPromptSize = 42;

    internal static readonly UIColor Primary = Dynamic(dark =>
        ThemeCatalog.Default.PaletteFor(dark ? ThemeMode.Dark : ThemeMode.Light).Primary);

    internal static readonly UIColor OnPrimary = Dynamic(dark =>
        SrgbColor.Parse(dark ? "#081022" : "#FFFFFF"));

    internal static readonly UIColor Background = Dynamic(dark =>
        ThemeCatalog.Default.PaletteFor(dark ? ThemeMode.Dark : ThemeMode.Light).Surface);

    internal static readonly UIColor Header = Dynamic(dark =>
        SrgbColor.Parse(dark ? "#0C1222" : "#FFFFFF"));

    internal static readonly UIColor Text = Dynamic(dark =>
        SrgbColor.Parse(dark ? "#F8FAFF" : "#0B0D12"));

    internal static readonly UIColor SecondaryText = Dynamic(dark =>
        SrgbColor.Parse(dark ? "#C6D0E7" : "#394150"));

    internal static readonly UIColor Card = Dynamic(dark =>
        SrgbColor.Parse(dark ? "#121B33" : "#EEF1FF"));

    internal static readonly UIColor Elevated = Dynamic(dark =>
        SrgbColor.Parse(dark ? "#1A2545" : "#FCFDFF"));

    internal static readonly UIColor Border = Dynamic(dark =>
        SrgbColor.Parse(dark ? "#263457" : "#D6DAF0"));

    internal static readonly UIColor Success = Dynamic(_ =>
        SrgbColor.Parse("#12B886"));

    internal static readonly UIColor WarningText = Dynamic(dark =>
        SrgbColor.Parse(dark ? "#FFD77A" : "#855900"));

    internal static readonly UIColor KnownText = Dynamic(dark =>
        SrgbColor.Parse(dark ? "#63DCAA" : "#136F52"));

    internal static readonly UIColor Danger = Dynamic(_ =>
        SrgbColor.Parse("#E13B30"));

    internal static readonly UIColor DangerSurface = Dynamic(dark =>
        SrgbColor.Parse(dark ? "#401C27" : "#FFF0EF"));

    private static UIColor Dynamic(Func<bool, SrgbColor> color) =>
        UIColor.FromDynamicProvider(traits =>
        {
            var rgb = color(traits.UserInterfaceStyle == UIUserInterfaceStyle.Dark);
            return UIColor.FromRGB(rgb.R, rgb.G, rgb.B);
        });
}
