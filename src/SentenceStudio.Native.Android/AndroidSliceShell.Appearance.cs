using AndroidX.Compose;
using SentenceStudio.Contracts.Theme;

namespace SentenceStudio.Native.Android;

internal sealed partial class AndroidSliceShell
{
    private ComposableNode RenderAppearanceSettings(AndroidVisualPalette palette)
    {
        var modes = new Column(Arrangement.SpacedBy(10))
        {
            Modifier = Modifier.FillMaxWidth().TestTag("native.settings.mode")
        };
        modes.Add(Heading(Localize("Settings_AppearanceSection"), palette, 20));
        modes.Add(Body(Localize("Settings_ModeLabel"), palette));

        foreach (var mode in new[] { ThemeMode.System, ThemeMode.Light, ThemeMode.Dark })
        {
            var label = Localize(mode switch
            {
                ThemeMode.System => "Settings_SystemMode",
                ThemeMode.Light => "Settings_LightMode",
                ThemeMode.Dark => "Settings_DarkMode",
                _ => throw new ArgumentOutOfRangeException(nameof(mode))
            });
            var selected = CurrentAppearance.Mode == mode;
            var option = new OutlinedButton(
                () => SelectAppearanceMode(mode), enabled: !Busy.Value)
            {
                Modifier = Modifier.FillMaxWidth()
                    .HeightIn(min: AndroidVisualTheme.ControlHeight)
                    .TestTag($"native.settings.mode.{mode.ToToken()}")
                    .Semantics(semantics =>
                    {
                        semantics.ContentDescription(label);
                        semantics.Selected(selected);
                    }),
                Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
            };
            var row = new Row(Arrangement.SpaceBetween,
                Alignment.Vertical.CenterVertically)
            {
                Modifier.FillMaxWidth(),
                Body(label, palette),
            };
            if (selected)
                row.Add(new Icon(Icons.Default.Check, label)
                {
                    Modifier = Modifier.Size(24),
                    Tint = palette.Primary,
                });
            option.Add(row);
            modes.Add(option);
        }

        var viewport = new Column(Arrangement.SpacedBy(12))
        {
            Modifier = Modifier.FillMaxSize()
                .VerticalScroll(_settingsScroll)
                .Padding(AndroidVisualTheme.PagePadding)
                .TestTag("native.settings.screen"),
        };
        viewport.Add(Card(palette, "native.settings.appearance", modes));
        if (!string.IsNullOrWhiteSpace(Error.Value))
            viewport.Add(StatusAlert(Error.Value, palette, error: true,
                automationId: "native.settings.error"));

        return new Column
        {
            Modifier.FillMaxSize(),
            ScreenHeader(Localize("Settings_Title"), palette, back: true,
                backAutomationId: "native.settings.back"),
            viewport,
        };
    }
}
