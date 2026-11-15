using System.Globalization;
using AndroidX.Compose;
using SentenceStudio.Contracts.Theme;
using SentenceStudio.Native.Features.Settings;

namespace SentenceStudio.Native.Android;

/// <summary>
/// Settings > Appearance (source Settings.razor, iOS AppearanceSettingsView): theme, mode and
/// text size. The Settings pages are in AndroidSliceShell.SettingsPages.Compose.cs.
/// </summary>
internal sealed partial class AndroidSliceShell
{
    private ComposableNode RenderAppearanceSection(AndroidVisualPalette palette)
    {
        var selection = CurrentAppearance;
        var rows = new List<ComposableNode>
        {
            Heading(Localize("Settings_AppearanceSection"), palette, 20,
                Modifier.Semantics(semantics => semantics.Heading())),
            Strong(Localize("Settings_ThemeLabel"), palette),
        };
        var themes = ThemeCatalog.All;
        var grid = new Column(Arrangement.SpacedBy(8))
        {
            Modifier = Modifier.FillMaxWidth().TestTag("native.settings.themes"),
        };
        for (var index = 0; index < themes.Count; index += 2)
        {
            var row = new Row(Arrangement.SpacedBy(8)) { Modifier.FillMaxWidth() };
            row.Add(ThemeSwatch(themes[index], selection, palette));
            if (index + 1 < themes.Count)
                row.Add(ThemeSwatch(themes[index + 1], selection, palette));
            else
                row.Add(new Box { Modifier.Weight(1f) });
            grid.Add(row);
        }
        rows.Add(grid);

        rows.Add(Strong(Localize("Settings_ModeLabel"), palette));
        rows.Add(MoreSegments("native.settings.mode",
            [
                (ThemeMode.System.ToToken(), Localize("Settings_SystemMode")),
                (ThemeMode.Light.ToToken(), Localize("Settings_LightMode")),
                (ThemeMode.Dark.ToToken(), Localize("Settings_DarkMode")),
            ], selection.Mode.ToToken(), token =>
            {
                if (ThemeModeExtensions.TryParse(token, out var mode))
                    SetAppearance(CurrentAppearance.WithMode(mode));
            }, !Busy.Value));

        // While the finger is down the slider and label show the dragged size; the app
        // scale changes once, on release (Captain 2026-11-04, decision A). Applying it per
        // step, as the source's @oninput does, changes the density, and Compose then resets
        // the slider's pointer input, ending the drag after one step.
        // A slider disabled mid-drag (a save made Settings busy) loses the drag without a
        // release, so that size was never applied: show the size the app really uses.
        if (Busy.Value)
            _textSizePreview = null;
        var shownScale = _textSizePreview ?? selection.FontScale;
        var sizeLabel = string.Format(CultureInfo.CurrentCulture,
            Localize("Settings_TextSizeLabel"),
            (int)Math.Round(shownScale * 100));
        rows.Add(Body(sizeLabel, palette,
            modifier: Modifier.TestTag("native.settings.scale.label")));
        // No FillMaxWidth: the Material 3 track already fills the row's width.
        rows.Add(new AndroidReleaseSlider((float)shownScale,
            raw => PreviewTextSize(SettingsInput.SnapFontScale(raw)),
            CommitTextSize,
            new FloatRange((float)AppearanceSelection.MinFontScale,
                (float)AppearanceSelection.MaxFontScale),
            enabled: !Busy.Value,
            // 85% to 150% in 5% steps: 14 values, 12 between the ends.
            steps: 12,
            testTag: "native.settings.text-size",
            contentDescription: sizeLabel));
        rows.Add(new Row(Arrangement.SpaceBetween)
        {
            Modifier.FillMaxWidth(),
            Caption("85%", palette),
            Caption("150%", palette),
        });
        return Card(palette, "native.settings.appearance", rows.ToArray());
    }

    private ComposableNode ThemeSwatch(ThemeDescriptor theme,
        AppearanceSelection selection, AndroidVisualPalette palette)
    {
        var chosen = selection.ThemeId == theme.Id;
        var label = Localize(theme.LocalizationKey);
        var name = chosen
            ? string.Format(CultureInfo.CurrentCulture, Localize("Selected"), label)
            : label;
        var colors = theme.PaletteFor(selection.Mode.ResolveEffective(SystemPrefersDark()));
        var marks = new Row(Arrangement.SpacedBy(6), Alignment.Vertical.CenterVertically)
        {
            Swatch(colors.Primary.Hex),
            Swatch(colors.Accent.Hex),
        };
        if (chosen)
            marks.Add(new Icon(Icons.Default.CheckCircle, name)
            {
                Modifier = Modifier.Size(22),
                Tint = palette.Primary,
            });
        var caption = Caption(label, palette);
        var button = new OutlinedButton(() => SetAppearance(
            CurrentAppearance.WithTheme(theme.Id)), enabled: !Busy.Value)
        {
            Modifier = Modifier.Weight(1f).HeightIn(min: 72)
                .Border(chosen ? 2 : 1, chosen ? palette.Primary : palette.Border,
                    AndroidVisualTheme.ControlRadius)
                .TestTag($"native.settings.theme.{theme.Id}")
                .Semantics(semantics =>
                {
                    semantics.ContentDescription(name);
                    semantics.Selected(chosen);
                }),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
        };
        button.Add(new Column(Arrangement.SpacedBy(6), Alignment.Horizontal.CenterHorizontally)
        {
            Modifier.FillMaxWidth(),
            marks,
            caption,
        });
        return button;
    }

    private static ComposableNode Swatch(string hex) => new Box
    {
        Modifier.Size(22).Background(Color.FromHex(hex), Shape.Circle()),
    };

    private bool SystemPrefersDark() =>
        ((activity.Resources?.Configuration?.UiMode ?? global::Android.Content.Res.UiMode.NightNo)
            & global::Android.Content.Res.UiMode.NightMask)
        == global::Android.Content.Res.UiMode.NightYes;
}
