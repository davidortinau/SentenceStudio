using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Contracts.Theme;
using Views = Microsoft.SwiftUI.SwiftUI;

using SentenceStudio.Native.iOS.Styling;

namespace SentenceStudio.Native.iOS.Features.Settings;

internal static class AppearanceSettingsView
{
    internal static SwiftView Section(
        SourceUiStyle style, AppearanceSelection selection, Func<string, string> text,
        Action<string> selectTheme, Action<ThemeMode> selectMode,
        Action<double> setFontScale)
    {
        var swatches = Views.LazyVGrid(2,
            ThemeCatalog.All.Select(theme =>
            {
                var chosen = selection.ThemeId == theme.Id;
                var label = text(theme.LocalizationKey);
                var content = Views.VStack(
                    Views.HStack(
                        Views.Circle(24, theme.Light.Primary.Hex, theme.Dark.Primary.Hex),
                        Views.Circle(24, theme.Light.Accent.Hex, theme.Dark.Accent.Hex),
                        Views.Image(chosen ? "checkmark.circle.fill" : "circle"))
                        .WithFlexibleWidth(
                            alignment: SwiftHorizontalAlignment.Center),
                    style.Text(Views.Text(label), SourceTextRole.Caption1)
                        .WithFlexibleWidth(
                            alignment: SwiftHorizontalAlignment.Center))
                    .WithPadding(8)
                    .WithMinimumHeight(SourceUiLayout.MinimumTouchHeight);
                content.Spacing = SourceUiLayout.ItemGap;
                var button = Views.Button(content, () => selectTheme(theme.Id))
                    .WithButtonStyle(SwiftButtonStyle.Plain)
                    .WithAccessibilityLabel(chosen
                        ? string.Format(CultureInfo.CurrentCulture,
                            text("Selected"), label)
                        : label)
                    .WithAccessibilityIdentifier($"native.settings.theme.{theme.Id}");
                return style.BorderedCard(button,
                    radius: style.Appearance.BootstrapRadius);
            }).ToArray());
        swatches.Spacing = SourceUiLayout.ItemGap;

        var mode = Views.Picker(text("Settings_ModeLabel"),
                [
                    new SwiftPickerOption("system", text("Settings_SystemMode")),
                    new SwiftPickerOption("light", text("Settings_LightMode")),
                    new SwiftPickerOption("dark", text("Settings_DarkMode")),
                ],
                selection.Mode.ToToken(),
                value =>
                {
                    if (ThemeModeExtensions.TryParse(value, out var selected))
                        selectMode(selected);
                })
            .WithPickerStyle(SwiftPickerStyle.Segmented)
            .WithAccessibilityIdentifier("native.settings.mode");

        SwiftView? scaleSlider = null;
        scaleSlider = Views.Slider(string.Format(CultureInfo.CurrentCulture,
                    text("Settings_TextSizeLabel"),
                    (int)Math.Round(selection.FontScale * 100)),
                AppearanceSelection.MinFontScale,
                AppearanceSelection.MaxFontScale,
                selection.FontScale,
                raw =>
                {
                    var scale = SettingsInput.SnapFontScale(raw);
                    scaleSlider!.Value = scale;
                    scaleSlider.AccessibilityLabel = string.Format(
                        CultureInfo.CurrentCulture,
                        text("Settings_TextSizeLabel"),
                        (int)Math.Round(scale * 100));
                    setFontScale(scale);
                })
            .WithAccessibilityIdentifier("native.settings.text-size");

        var appearance = Views.VStack(
            style.Text(Views.Text(text("Settings_AppearanceSection")),
                SourceTextRole.Title3, sourceHeading: true),
            style.Text(Views.Text(text("Settings_ThemeLabel")),
                SourceTextRole.Body2Strong),
            swatches,
            mode,
            style.Text(Views.Text(string.Format(CultureInfo.CurrentCulture,
                    text("Settings_TextSizeLabel"),
                    (int)Math.Round(selection.FontScale * 100))),
                SourceTextRole.Body2)
                .WithAccessibilityIdentifier("native.settings.scale.label"),
            scaleSlider,
            Views.HStack(
                style.Text(Views.Text("85%"), SourceTextRole.Caption1),
                style.Text(Views.Text("150%"), SourceTextRole.Caption1)
                    .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Trailing)))
            .WithPadding(SourceUiLayout.CardInset);
        appearance.Spacing = SourceUiLayout.PageGap;

        return style.Card(appearance);
    }
}
