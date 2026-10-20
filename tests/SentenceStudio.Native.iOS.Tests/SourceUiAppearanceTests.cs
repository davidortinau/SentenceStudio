using SentenceStudio.Abstractions;
using SentenceStudio.Contracts.Theme;
using SentenceStudio.Native.iOS.Styling;

namespace SentenceStudio.Native.iOS.Tests;

public sealed class SourceUiAppearanceTests
{
    [Fact]
    public void NoAppearancePreference_UsesOriginalSeoulPopDarkWithoutWriting()
    {
        var preferences = new MemoryPreferences();

        var appearance = SourceUiAppearance.Load(preferences);

        Assert.Equal(ThemeCatalog.DefaultThemeId, appearance.Selection.ThemeId);
        Assert.Equal(ThemeMode.Dark, appearance.Selection.Mode);
        Assert.Equal(1, appearance.Selection.FontScale);
        Assert.Equal("#F7F8FF", appearance.BodyBackground.Light);
        Assert.Equal("#060913", appearance.BodyBackground.Dark);
        Assert.Equal("#0B0D12", appearance.Text.Light);
        Assert.Equal("#F8FAFF", appearance.Text.Dark);
        Assert.Equal("#1E4DFF", appearance.Primary.Light);
        Assert.Equal("#6B8CFF", appearance.Primary.Dark);
        Assert.Equal("#EEF1FF", appearance.CardSurface.Light);
        Assert.Equal("#121B33", appearance.CardSurface.Dark);
        Assert.Equal("#D6DAF0", appearance.Border.Light);
        Assert.Equal("#263457", appearance.Border.Dark);
        Assert.Equal(6, appearance.BootstrapRadius);
        Assert.Null(appearance.FontPostScriptName(400));
        Assert.Equal(0, preferences.Writes);
    }

    [Fact]
    public void ValidAppearanceToken_WinsOverLegacyAndKeepsSelectedThemePalette()
    {
        var preferences = new MemoryPreferences();
        preferences.Seed("AppAppearance", "v1.forest.light.125");
        preferences.Seed("AppTheme", "seoul-pop");
        preferences.Seed("AppThemeMode", "dark");

        var appearance = SourceUiAppearance.Load(preferences);

        Assert.Equal("forest", appearance.Selection.ThemeId);
        Assert.Equal(ThemeMode.Light, appearance.Selection.Mode);
        Assert.Equal(1.25, appearance.Selection.FontScale);
        Assert.Equal("#F0FDF4", appearance.BodyBackground.Light);
        Assert.Equal("#0F1C13", appearance.BodyBackground.Dark);
        Assert.Equal("#DCFCE7", appearance.CardSurface.Light);
        Assert.Equal("#166534", appearance.CardSurface.Dark);
        Assert.Equal("#059669", appearance.Primary.Light);
        Assert.Equal("#34D399", appearance.Primary.Dark);
        Assert.Equal(0, preferences.Writes);
    }

    [Fact]
    public void SystemChoice_PreservesBothSourceColorPairsAcrossSaveAndReload()
    {
        var preferences = new MemoryPreferences();
        var system = SourceUiAppearance.Save(preferences,
            new AppearanceSelection("forest", ThemeMode.System, 1.25));

        Assert.Equal("v1.forest.system.125",
            preferences.Get("AppAppearance", string.Empty));
        Assert.Equal(ThemeMode.System,
            SourceUiAppearance.Load(preferences).Selection.Mode);
        Assert.Equal("#F0FDF4", system.BodyBackground.Light);
        Assert.Equal("#0F1C13", system.BodyBackground.Dark);
        Assert.Equal("#059669", system.Primary.Light);
        Assert.Equal("#34D399", system.Primary.Dark);
    }

    [Fact]
    public void ExistingLightOverride_SwitchingToSystemFollowsBothOsModesAfterRestart()
    {
        var preferences = new MemoryPreferences();
        preferences.Seed("AppAppearance", "v1.ocean.light.125");
        var previous = SourceUiAppearance.Load(preferences).Selection;

        Assert.Equal(previous.Theme.Light, previous.PaletteFor(systemIsDark: true));

        SourceUiAppearance.Save(preferences, previous.WithMode(ThemeMode.System));
        var restarted = SourceUiAppearance.Load(preferences);

        Assert.Equal("v1.ocean.system.125",
            preferences.Get("AppAppearance", string.Empty));
        Assert.Equal(previous.ThemeId, restarted.Selection.ThemeId);
        Assert.Equal(previous.FontScale, restarted.Selection.FontScale);
        Assert.Equal(previous.Theme.Light, restarted.Selection.PaletteFor(systemIsDark: false));
        Assert.Equal(previous.Theme.Dark, restarted.Selection.PaletteFor(systemIsDark: true));
        Assert.NotEqual(restarted.BodyBackground.Light, restarted.BodyBackground.Dark);
        Assert.Equal(1, preferences.Writes);
    }

    [Fact]
    public void NormalSettingsSelection_RoundTripsLightDarkThemeAndScaleWithoutChangingOtherChoices()
    {
        var preferences = new MemoryPreferences();
        var initial = SourceUiAppearance.Load(preferences);

        var light = SourceUiAppearance.Save(preferences,
            initial.Selection.WithMode(ThemeMode.Light));
        Assert.Equal(ThemeMode.Light, light.Selection.Mode);
        Assert.Equal(light.Selection, SourceUiAppearance.Load(preferences).Selection);
        Assert.Equal("#F7F8FF", light.BodyBackground.Light);
        Assert.Equal(1, preferences.Writes);

        var resized = SourceUiAppearance.Save(preferences,
            light.Selection.WithFontScale(1.25));
        Assert.Equal(ThemeMode.Light, resized.Selection.Mode);
        Assert.Equal("seoul-pop", resized.Selection.ThemeId);
        var changedTheme = SourceUiAppearance.Save(preferences,
            resized.Selection.WithTheme("forest"));
        Assert.Equal(ThemeMode.Light, changedTheme.Selection.Mode);
        Assert.Equal(1.25, changedTheme.Selection.FontScale);
        Assert.Equal("#F0FDF4", changedTheme.BodyBackground.Light);

        var dark = SourceUiAppearance.Save(preferences,
            changedTheme.Selection.WithMode(ThemeMode.Dark));
        Assert.Equal("#0F1C13", dark.BodyBackground.Dark);
        Assert.Equal(dark.Selection, SourceUiAppearance.Load(preferences).Selection);
        Assert.Equal(4, preferences.Writes);
    }

    [Fact]
    public void LegacyKeys_NormalizeInvalidPartsWithoutResettingOrSaving()
    {
        var preferences = new MemoryPreferences();
        preferences.Seed("AppAppearance", "invalid");
        preferences.Seed("AppTheme", "ocean");
        preferences.Seed("AppThemeMode", "LIGHT");
        preferences.Seed("AppFontScale", 1.5);

        var appearance = SourceUiAppearance.Load(preferences);

        Assert.Equal("ocean", appearance.Selection.ThemeId);
        Assert.Equal(ThemeMode.Light, appearance.Selection.Mode);
        Assert.Equal(1.5, appearance.Selection.FontScale);
        Assert.Equal("#E0F2FE", appearance.CardSurface.Light);
        Assert.Equal("#155E75", appearance.CardSurface.Dark);
        Assert.Equal(0, preferences.Writes);

        preferences.Seed("AppTheme", "unknown");
        preferences.Seed("AppThemeMode", "invalid");
        preferences.Seed("AppFontScale", 4.0);
        var fallback = SourceUiAppearance.Load(preferences);
        Assert.Equal(AppearanceSelection.Default, fallback.Selection);
        Assert.Equal(0, preferences.Writes);
    }

    [Fact]
    public void IsolatedPreviewAppearance_OverridesStoredSelectionWithoutChangingPreferences()
    {
        var preferences = new MemoryPreferences();
        preferences.Seed("AppAppearance", "v1.seoul-pop.dark.100");

        var appearance = SourceUiAppearance.Load(preferences, "v1.flatly.light.150");

        Assert.Equal("flatly", appearance.Selection.ThemeId);
        Assert.Equal(ThemeMode.Light, appearance.Selection.Mode);
        Assert.Equal(1.5, appearance.Selection.FontScale);
        Assert.Equal("Lato-Regular", appearance.FontPostScriptName(400));
        Assert.Equal("v1.seoul-pop.dark.100",
            preferences.Get("AppAppearance", string.Empty));
        Assert.Equal(0, preferences.Writes);
        Assert.Throws<ArgumentException>(() =>
            SourceUiAppearance.Load(preferences, "v1.nonexistent.light.150"));
        var compact = SourceUiAppearance.Load(preferences, "v1.sketchy.light.85");
        Assert.Equal(0.85, compact.Selection.FontScale);
        Assert.Equal("Neucha", compact.FontPostScriptName(700));
        Assert.Equal(14.45, SourceUiTypography.For(
            SourceTextRole.Body1, 402, compact.Selection.FontScale,
            compact.Selection.ThemeId).Size, 2);
        Assert.Equal(0, preferences.Writes);

        var fixtureRestart = SourceUiAppearance.Load(preferences,
            "v1.flatly.light.150", preferStoredPreviewAppearance: true);
        Assert.Equal("v1.seoul-pop.dark.100",
            fixtureRestart.Selection.ToToken());
        Assert.Equal(0, preferences.Writes);
    }

    [Fact]
    public void EveryExistingTheme_HasItsOwnPairedColors_AndActualSourceFontRoles()
    {
        foreach (var theme in ThemeCatalog.All)
        {
            var appearance = SourceUiAppearance.FromSelection(
                new AppearanceSelection(theme.Id, ThemeMode.Dark, 1));
            Assert.Equal(theme.Light.Primary.Hex, appearance.Primary.Light);
            Assert.Equal(theme.Dark.Primary.Hex, appearance.Primary.Dark);
            Assert.Equal(theme.Light.Surface.Hex, appearance.BodyBackground.Light);
            Assert.Equal(theme.Dark.Surface.Hex, appearance.BodyBackground.Dark);
            Assert.StartsWith("#", appearance.CardSurface.Light);
            Assert.StartsWith("#", appearance.CardSurface.Dark);
        }

        var slate = SourceUiAppearance.FromSelection(
            new AppearanceSelection("slate", ThemeMode.Light, 1));
        Assert.Equal("#272B30", slate.BodyBackground.Light);
        Assert.Equal("#31353A", slate.CardSurface.Dark);
        Assert.Equal("#32383E", slate.PlainCardSurface.Light);
        Assert.Equal("#32383E", slate.PlainCardSurface.Dark);
        Assert.Null(slate.FontPostScriptName(400));
        var sketchy = SourceUiAppearance.FromSelection(
            new AppearanceSelection("sketchy", ThemeMode.Dark, 1));
        Assert.Equal("Neucha", sketchy.FontPostScriptName(700));
        Assert.Equal("CabinSketch-Regular",
            sketchy.FontPostScriptName(500, sourceHeading: true));
        Assert.Equal("CabinSketch-Bold",
            sketchy.FontPostScriptName(700, strongElement: true));
        var flatly = SourceUiAppearance.FromSelection(
            new AppearanceSelection("flatly", ThemeMode.Dark, 1));
        Assert.Equal("Lato-Regular", flatly.FontPostScriptName(400));
        Assert.Equal("Lato-Bold", flatly.FontPostScriptName(600));
        var vapor = SourceUiAppearance.FromSelection(
            new AppearanceSelection("vapor", ThemeMode.Light, 1));
        Assert.Equal("#FFFFFF", vapor.PlainCardText.Light);
        Assert.Equal("#FFFFFF", vapor.PlainCardText.Dark);
        Assert.Equal(vapor.BodyBackground, vapor.PlainCardSurface);
        Assert.Equal(2.4, vapor.BootstrapRadius);
        Assert.Equal(25, sketchy.BootstrapRadius);
        Assert.Equal(6, flatly.BootstrapRadius);
    }

    [Theory]
    [InlineData("seoul-pop", "#12B886", "#21D6A4")]
    [InlineData("ocean", "#06B6D4", "#2DD4BF")]
    [InlineData("forest", "#F59E0B", "#FBBF24")]
    [InlineData("sunset", "#DB2777", "#F472B6")]
    [InlineData("monochrome", "#6B7280", "#9CA3AF")]
    [InlineData("flatly", "#95A5A6", "#95A5A6")]
    [InlineData("vapor", "#EA39B8", "#EA39B8")]
    public void ResourceRowSeparatorsFollowSourceBorderSecondaryForEachTheme(
        string theme, string light, string dark)
    {
        var appearance = SourceUiAppearance.FromSelection(
            new AppearanceSelection(theme, ThemeMode.System, 1));

        Assert.Equal(new SourceColorPair(light, dark), appearance.BorderSecondary);
    }

    [Fact]
    public void PlainCards_ListRows_AndAlertsFollowTheirOwnSourceCssRules()
    {
        var seoul = SourceUiAppearance.FromSelection(AppearanceSelection.Default);
        Assert.Equal(seoul.BodyBackground, seoul.PlainCardSurface);
        Assert.Equal(seoul.BodyBackground, seoul.ListRowSurface);
        Assert.NotEqual(seoul.CardSurface, seoul.PlainCardSurface);
        Assert.Equal(new SourceColorPair("#F8D7DA", "#2C0B0E"),
            seoul.DangerAlertFill);
        Assert.Equal(new SourceColorPair("#58151C", "#EA868F"),
            seoul.DangerAlertText);
        Assert.Equal(new SourceColorPair("#D1E7DD", "#051B11"),
            seoul.SuccessAlertFill);
        Assert.Equal(new SourceColorPair("#A3CFBB", "#0F5132"),
            seoul.SuccessAlertBorder);

        var flatly = SourceUiAppearance.FromSelection(
            AppearanceSelection.Default.WithTheme("flatly"));
        Assert.Equal(new SourceColorPair("#D1F2EB", "#05261F"),
            flatly.SuccessAlertFill);
        Assert.Equal(new SourceColorPair("#FADBD8", "#2E0F0C"),
            flatly.DangerAlertFill);
    }

    // Pinned iOS host selects Bootstrap 5.3.3 or themes/*.min.css. Their
    // .alert-info rules use the three --bs-info-*-subtle/emphasis variables.
    [Theory]
    [InlineData("seoul-pop", "#CFF4FC", "#032830",
        "#055160", "#6EDFF6", "#9EEAF9", "#087990")]
    [InlineData("ocean", "#CFF4FC", "#032830",
        "#055160", "#6EDFF6", "#9EEAF9", "#087990")]
    [InlineData("forest", "#CFF4FC", "#032830",
        "#055160", "#6EDFF6", "#9EEAF9", "#087990")]
    [InlineData("sunset", "#CFF4FC", "#032830",
        "#055160", "#6EDFF6", "#9EEAF9", "#087990")]
    [InlineData("monochrome", "#CFF4FC", "#032830",
        "#055160", "#6EDFF6", "#9EEAF9", "#087990")]
    [InlineData("flatly", "#D6EAF8", "#0A1E2C",
        "#153D58", "#85C1E9", "#AED6F1", "#1F5B83")]
    [InlineData("sketchy", "#D1ECF1", "#052025",
        "#09414A", "#74C7D4", "#A2DAE3", "#0E616E")]
    [InlineData("slate", "#DEF2F8", "#12262C",
        "#244D59", "#9DD9EB", "#BDE6F2", "#377385")]
    [InlineData("vapor", "#D1ECFD", "#052031",
        "#0B4162", "#76C7FA", "#A4DAFB", "#106194")]
    [InlineData("brite", "#D3F6FB", "#072A2F",
        "#0E545F", "#7AE4F4", "#A7EDF8", "#147E8E")]
    public void InfoAlerts_UseExactPinnedIosStylesheetTokens(
        string theme, string lightFill, string darkFill,
        string lightText, string darkText,
        string lightBorder, string darkBorder)
    {
        var appearance = SourceUiAppearance.FromSelection(
            AppearanceSelection.Default.WithTheme(theme));
        Assert.Equal(new SourceColorPair(lightFill, darkFill),
            appearance.InfoAlertFill);
        Assert.Equal(new SourceColorPair(lightText, darkText),
            appearance.InfoAlertText);
        Assert.Equal(new SourceColorPair(lightBorder, darkBorder),
            appearance.InfoAlertBorder);
    }

    // Pinned bootstrap.css .btn-primary/.btn-outline-success, themes/*.min.css,
    // and app.css:722-725 (dark outline) / :1054-1063 (phone small padding).
    [Theory]
    [InlineData("seoul-pop", "#0D6EFD", "#0D6EFD", "#FFFFFF",
        "#0D6EFD", "#FFFFFF", "#198754", "#198754", 4)]
    [InlineData("ocean", "#0D6EFD", "#0D6EFD", "#FFFFFF",
        "#0D6EFD", "#FFFFFF", "#198754", "#198754", 4)]
    [InlineData("forest", "#0D6EFD", "#0D6EFD", "#FFFFFF",
        "#0D6EFD", "#FFFFFF", "#198754", "#198754", 4)]
    [InlineData("sunset", "#0D6EFD", "#0D6EFD", "#FFFFFF",
        "#0D6EFD", "#FFFFFF", "#198754", "#198754", 4)]
    [InlineData("monochrome", "#0D6EFD", "#0D6EFD", "#FFFFFF",
        "#0D6EFD", "#FFFFFF", "#198754", "#198754", 4)]
    [InlineData("flatly", "#2C3E50", "#2C3E50", "#FFFFFF",
        "#2C3E50", "#FFFFFF", "#18BC9C", "#18BC9C", 4)]
    [InlineData("sketchy", "#333333", "#F8F9FA", "#FFFFFF",
        "#333333", "#000000", "#28A745", "#28A745", 15)]
    [InlineData("vapor", "#6F42C1", "#6F42C1", "#FFFFFF",
        "#6F42C1", "#FFFFFF", "#3CF281", "#FFFFFF", 0.8)]
    [InlineData("slate", "#3A3F44", "#3A3F44", "#FFFFFF",
        "#3A3F44", "#FFFFFF", "#62C462", "#62C462", 4)]
    [InlineData("brite", "#A2E436", "#A2E436", "#000000",
        "#000000", "#000000", "#000000", "#000000", 4)]
    public void SourceBootstrapButtonClasses_UseSelectedStylesheetNotThemeRoot(
        string theme, string primaryLight, string primaryDark,
        string foregroundLight, string borderDark, string foregroundDark,
        string successLight, string successForegroundLight, double smallRadius)
    {
        var appearance = SourceUiAppearance.FromSelection(
            AppearanceSelection.Default.WithTheme(theme));

        Assert.Equal(new SourceColorPair(primaryLight, primaryDark),
            appearance.BootstrapPrimaryFill);
        Assert.Equal(new SourceColorPair(
                theme == "brite" ? "#000000" : primaryLight, borderDark),
            appearance.BootstrapPrimaryBorder);
        Assert.Equal(theme == "brite"
                ? appearance.BootstrapPrimaryFill : appearance.BootstrapPrimaryBorder,
            appearance.BootstrapPrimaryDisabledBorder);
        Assert.Equal(theme is "brite" or "sketchy" ? 2 : 1,
            appearance.BootstrapPrimaryBorderWidth);
        Assert.Equal(theme is "brite" or "sketchy" ? 2 : 1,
            appearance.BootstrapBaseButtonBorderWidth);
        Assert.Equal(theme == "brite"
                ? new SourceColorPair("#000000", "#000000") : appearance.Border,
            appearance.BootstrapButtonBorder);
        Assert.Equal(theme == "brite"
                ? new SourceColorPair("#FFFFFF", "#FFFFFF") : null,
            appearance.BootstrapOutlineSecondaryFill);
        Assert.Equal(new SourceColorPair(foregroundLight, foregroundDark),
            appearance.BootstrapPrimaryForeground);
        Assert.Equal(new SourceColorPair(successLight, "#12B886"),
            appearance.BootstrapSuccessOutlineBorder);
        Assert.Equal(new SourceColorPair(successForegroundLight, "#12B886"),
            appearance.BootstrapSuccessOutlineForeground);
        Assert.Equal(theme == "brite"
                ? new SourceColorPair("#68D391", "#68D391") : null,
            appearance.BootstrapSuccessOutlineFill);
        Assert.Equal(smallRadius, appearance.BootstrapSmallButtonRadius);
        Assert.Equal(16, appearance.BootstrapSmallButtonHorizontalPadding(402));
        Assert.Equal(theme == "brite" ? 12 : 8,
            appearance.BootstrapSmallButtonHorizontalPadding(834));
    }

    [Fact]
    public void DefaultSignInAndQuizOutline_AreNotGenericSourceButtonColors()
    {
        var appearance = SourceUiAppearance.FromSelection(AppearanceSelection.Default);
        Assert.Equal("#6B8CFF", appearance.Primary.Dark);
        Assert.Equal("#081022", appearance.OnPrimary.Dark);
        Assert.Equal("#0D6EFD", appearance.BootstrapPrimaryFill.Dark);
        Assert.Equal("#FFFFFF", appearance.BootstrapPrimaryForeground.Dark);
        Assert.Equal("#12B886", appearance.BootstrapSuccessOutlineBorder.Dark);
        Assert.Null(appearance.BootstrapSuccessOutlineFill);
    }

    [Fact]
    public void SuccessOutlineStroke_UsesSelectedCssWidthWithoutScreenScale()
    {
        foreach (var theme in ThemeCatalog.All)
        {
            var light = SourceUiAppearance.FromSelection(
                new AppearanceSelection(theme.Id, ThemeMode.Light, 1));
            var dark = SourceUiAppearance.FromSelection(
                new AppearanceSelection(theme.Id, ThemeMode.Dark, 1));
            var expected = theme.Id is "vapor" or "brite" or "sketchy" ? 2d : 1d;

            Assert.Equal(expected, light.BootstrapSuccessOutlineBorderWidth);
            Assert.Equal(expected, dark.BootstrapSuccessOutlineBorderWidth);
        }
    }

    [Fact]
    public void SketchySuccessOutlineKeepsSourceColorsAndTwoPointStroke()
    {
        var appearance = SourceUiAppearance.FromSelection(
            new AppearanceSelection("sketchy", ThemeMode.Light, 1));

        Assert.Equal(new SourceColorPair("#28A745", "#12B886"),
            appearance.BootstrapSuccessOutlineBorder);
        Assert.Equal(appearance.BootstrapSuccessOutlineBorder,
            appearance.BootstrapSuccessOutlineForeground);
        Assert.Null(appearance.BootstrapSuccessOutlineFill);
        Assert.Equal(2, appearance.BootstrapSuccessOutlineBorderWidth);
        Assert.Equal(2, appearance.BootstrapPrimaryBorderWidth);
        Assert.Equal(appearance.Border, appearance.BootstrapButtonBorder);
        Assert.Null(appearance.BootstrapOutlineSecondaryFill);
        Assert.Equal(15, appearance.BootstrapSmallButtonRadius);
    }

    [Fact]
    public void BriteDisabledSignInRetainsItsClassBorderInsteadOfLateEnabledOverride()
    {
        var appearance = SourceUiAppearance.FromSelection(
            new AppearanceSelection("brite", ThemeMode.Light, 1));

        Assert.Equal(new SourceColorPair("#000000", "#000000"),
            appearance.BootstrapPrimaryBorder);
        Assert.Equal(new SourceColorPair("#A2E436", "#A2E436"),
            appearance.BootstrapPrimaryDisabledBorder);
        Assert.Equal(2, appearance.BootstrapPrimaryBorderWidth);
        Assert.Equal(new SourceColorPair("#000000", "#12B886"),
            appearance.BootstrapSuccessOutlineBorder);
        Assert.Equal(new SourceColorPair("#68D391", "#68D391"),
            appearance.BootstrapSuccessOutlineFill);
        Assert.Equal(new SourceColorPair("#000000", "#12B886"),
            appearance.BootstrapSuccessOutlineForeground);
        Assert.Equal(2, appearance.BootstrapSuccessOutlineBorderWidth);
        Assert.Equal(new SourceColorPair("#000000", "#000000"),
            appearance.BootstrapButtonBorder);
        Assert.Equal(new SourceColorPair("#FFFFFF", "#FFFFFF"),
            appearance.BootstrapOutlineSecondaryFill);
    }

    [Fact]
    public void DisabledCustomButtons_UseTheWebKitComputedInheritedInkAndClearFill()
    {
        foreach (var theme in new[] { "brite", "sketchy" })
        {
            var appearance = SourceUiAppearance.FromSelection(
                new AppearanceSelection(theme, ThemeMode.Light, 1));
            Assert.Equal(new SourceColorPair("#212529", "#DEE2E6"),
                appearance.Text);
            Assert.NotEqual(appearance.Primary, appearance.Text);
        }
        Assert.Equal(new SourceColorPair("#FFFFFF", "#FFFFFF"),
            SourceUiAppearance.FromSelection(
                AppearanceSelection.Default.WithTheme("brite"))
                .BootstrapNeutralDisabledBorder);
        Assert.Equal(new SourceColorPair("#555555", "#555555"),
            SourceUiAppearance.FromSelection(
                AppearanceSelection.Default.WithTheme("sketchy"))
                .BootstrapNeutralDisabledBorder);
    }

    [Fact]
    public void SketchyDarkLogin_ChangesOnlyItsDisabledFillToken()
    {
        var sketchy = SourceUiAppearance.FromSelection(
            AppearanceSelection.Default.WithTheme("sketchy"));
        Assert.Equal(new SourceColorPair("#333333", "#F8F9FA"),
            sketchy.BootstrapPrimaryFill);
        Assert.Equal(new SourceColorPair("#333333", "#AAAAAA"),
            sketchy.BootstrapPrimaryDisabledFill);
        Assert.Equal(new SourceColorPair("#333333", "#333333"),
            sketchy.BootstrapPrimaryDisabledBorder);
        var brite = SourceUiAppearance.FromSelection(
            AppearanceSelection.Default.WithTheme("brite"));
        Assert.Equal(brite.BootstrapPrimaryFill, brite.BootstrapPrimaryDisabledFill);
    }

    [Theory]
    [InlineData(402, 16)]
    [InlineData(767.98, 16)]
    [InlineData(768, 14)]
    [InlineData(1024, 14)]
    public void CustomButtonPadding_FollowsTheSourceMobileCascade(
        double viewportWidth, double expected) =>
        Assert.Equal(expected,
            SourceUiAppearance.BootstrapCustomButtonHorizontalPadding(viewportWidth));

    [Fact]
    public void GroupCorners_RetainOnlyRealExteriorAxesForEachSourceClass()
    {
        var brite = SourceUiAppearance.FromSelection(
            AppearanceSelection.Default.WithTheme("brite"));
        Assert.Equal(new SourceButtonCornerRadii(6, 6, 0, 0, 0, 0, 6, 6),
            brite.GroupCorners(SourceButtonGroupClass.Custom,
                SourceButtonGroupPosition.First));
        Assert.Equal(new SourceButtonCornerRadii(0, 0, 4, 4, 4, 4, 0, 0),
            brite.GroupCorners(SourceButtonGroupClass.Small,
                SourceButtonGroupPosition.Last));
        var sketchy = SourceUiAppearance.FromSelection(
            AppearanceSelection.Default.WithTheme("sketchy"));
        Assert.Equal(new SourceButtonCornerRadii(25, 25, 0, 0, 0, 0, 25, 25),
            sketchy.GroupCorners(SourceButtonGroupClass.Custom,
                SourceButtonGroupPosition.First));
        Assert.Equal(new SourceButtonCornerRadii(0, 0, 25, 225, 225, 25, 0, 0),
            sketchy.GroupCorners(SourceButtonGroupClass.Outline,
                SourceButtonGroupPosition.Last));
        Assert.Equal(new SourceButtonCornerRadii(255, 25, 0, 0, 0, 0, 25, 255),
            sketchy.GroupCorners(SourceButtonGroupClass.Small,
                SourceButtonGroupPosition.First));
        Assert.Equal(default(SourceButtonCornerRadii),
            sketchy.GroupCorners(SourceButtonGroupClass.Custom,
                SourceButtonGroupPosition.Middle));
    }

    [Fact]
    public void SketchyInheritsNeuchaBodyWeightButKeepsSourceButtonAndHeadingWeights()
    {
        Assert.Equal(new SourceTextMetrics(17, 700, 1.29),
            SourceUiTypography.For(SourceTextRole.Body1, 402, 1, "sketchy"));
        Assert.Equal(new SourceTextMetrics(16, 700, 1.5),
            SourceUiTypography.For(SourceTextRole.Input, 402, 1, "sketchy"));
        Assert.Equal(new SourceTextMetrics(14, 400, 1.5),
            SourceUiTypography.For(SourceTextRole.Button, 402, 1, "sketchy"));
        Assert.Equal(new SourceTextMetrics(22, 600, 1.27),
            SourceUiTypography.For(SourceTextRole.Title2, 402, 1, "sketchy"));
        Assert.Equal(new SourceTextMetrics(16, 600, 1.5),
            SourceUiTypography.For(SourceTextRole.ListTitle, 402, 1, "sketchy"));
    }

    [Fact]
    public void Typography_UsesTheLaterUnscaledPhoneDisplayWinner_AndScalesOtherRulesOnce()
    {
        Assert.Equal(new SourceTextMetrics(24, 700, 1.17),
            SourceUiTypography.For(SourceTextRole.Display, 402, 1.5));
        Assert.Equal(new SourceTextMetrics(63, 700, 1.17),
            SourceUiTypography.For(SourceTextRole.Display, 768, 1.5));
        Assert.Equal(new SourceTextMetrics(90, 700, 1.17),
            SourceUiTypography.For(SourceTextRole.Display, 820, 1.5));
        Assert.Equal(new SourceTextMetrics(36, 500, 1.21),
            SourceUiTypography.For(SourceTextRole.Title1, 402, 1.5));
        Assert.Equal(new SourceTextMetrics(25.5, 400, 1.29),
            SourceUiTypography.For(SourceTextRole.Body1, 402, 1.5));
        Assert.Equal(new SourceTextMetrics(24, 400, 1.5),
            SourceUiTypography.For(SourceTextRole.Input, 402, 1.5));
        Assert.Equal(new SourceTextMetrics(23.212, 700, 1.2),
            SourceUiTypography.For(SourceTextRole.Brand, 402, 1.5));
        Assert.Equal(new SourceTextMetrics(23.212, 700, 1.5),
            SourceUiTypography.For(SourceTextRole.StatNumber, 402, 1.5));
    }

    [Theory]
    [InlineData(402, 0.85, 23.212)]
    [InlineData(402, 1, 23.212)]
    [InlineData(402, 1.5, 23.212)]
    [InlineData(834, 1.5, 25.804)]
    [InlineData(1200, 1, 28)]
    [InlineData(1376, 1.5, 28)]
    public void BootstrapFluidFs3_DoesNotInheritTheAppFontScale(
        double width, double scale, double expected)
    {
        Assert.Equal(expected, SourceUiTypography.For(
            SourceTextRole.Brand, width, scale).Size, 3);
        Assert.Equal(expected, SourceUiTypography.For(
            SourceTextRole.StatNumber, width, scale).Size, 3);
        Assert.Equal(1.5, SourceUiTypography.For(
            SourceTextRole.StatNumber, width, scale).LineHeight);
    }

    [Theory]
    [InlineData(0.85)]
    [InlineData(1)]
    [InlineData(1.5)]
    public void BootstrapButtonAndInputKeepTheirEffectiveLineHeight(double scale)
    {
        Assert.Equal(new SourceTextMetrics(14 * scale, 400, 1.5),
            SourceUiTypography.For(SourceTextRole.Button, 402, scale));
        Assert.Equal(new SourceTextMetrics(16 * scale, 400, 1.5),
            SourceUiTypography.For(SourceTextRole.Input, 402, scale));
        Assert.Equal(1.2, SourceUiTypography.For(
            SourceTextRole.Badge, 402, scale).LineHeight);
        Assert.Equal(1, SourceUiTypography.For(
            SourceTextRole.BadgeSmall, 402, scale).LineHeight);
        Assert.Equal(24, SourceUiTypography.For(
            SourceTextRole.Display, 402, scale).Size);
    }

    [Theory]
    [InlineData(402, 1, 2)]
    [InlineData(767, 1, 2)]
    [InlineData(768, 2, 3)]
    [InlineData(820, 2, 3)]
    [InlineData(992, 3, 4)]
    public void Breakpoints_MatchOriginalResourceAndActivityGrid(
        double width, int resources, int activities)
    {
        Assert.Equal(resources, SourceUiLayout.ResourceColumns(width));
        Assert.Equal(activities, SourceUiLayout.ActivityColumns(width));
    }

    [Theory]
    [InlineData(402, false)]
    [InlineData(767, false)]
    [InlineData(768, true)]
    [InlineData(820, true)]
    [InlineData(992, true)]
    public void ListRows_HideMetadataOnPhonesAndShowItAtSourceMediumBreakpoint(
        double width, bool expected)
    {
        Assert.Equal(expected, SourceUiLayout.ShowResourceListMetadata(width));
    }

    private sealed class MemoryPreferences : IPreferencesService
    {
        private readonly Dictionary<string, object> _values =
            new(StringComparer.Ordinal);
        internal int Writes { get; private set; }

        internal void Seed<T>(string key, T value) where T : notnull =>
            _values[key] = value;

        public T Get<T>(string key, T defaultValue) =>
            _values.TryGetValue(key, out var value) && value is T typed
                ? typed : defaultValue;

        public void Set<T>(string key, T value)
        {
            Writes++;
            _values[key] = value!;
        }

        public void Remove(string key)
        {
            Writes++;
            _values.Remove(key);
        }

        public void Clear() => throw new NotSupportedException();
    }
}
