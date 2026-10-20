using System.Runtime.CompilerServices;
using SentenceStudio.Native.iOS.Styling;

namespace SentenceStudio.Native.iOS.Tests;

public sealed class NativeSourceStylingContractTests
{
    [Fact]
    public void PhoneInsetsAndTouchSizes_AreTheOriginalCssValues()
    {
        Assert.Equal(12, SourceUiLayout.PhoneGutter);
        Assert.Equal(12, SourceUiLayout.PageGap);
        Assert.Equal(20, SourceUiLayout.SectionGap);
        Assert.Equal(8, SourceUiLayout.ItemGap);
        Assert.Equal(16, SourceUiLayout.CardInset);
        Assert.Equal(16, SourceUiLayout.AuthOuterInset);
        Assert.Equal(24, SourceUiLayout.LargeCardInset);
        Assert.Equal(44, SourceUiLayout.MinimumTouchHeight);
    }

    [Theory]
    [InlineData(402, 12)]
    [InlineData(767, 12)]
    [InlineData(768, 16)]
    public void ResponsiveContentGutter_KeepsTheOriginalPhoneAndTabletValues(
        double width, double expected)
    {
        Assert.Equal(expected, SourceUiLayout.PageGutter(width));
    }

    [Fact]
    public void ScrollContentOwnsVisualGutters_WithoutPaddingOrReplacingNativeViewports()
    {
        var dashboard = Source("Features/Dashboard/DashboardView.cs");
        var dashboardPage = dashboard[dashboard.IndexOf(
                "private static SwiftView DashboardPage(", StringComparison.Ordinal)
            ..dashboard.IndexOf("private static SwiftView ModeButton(",
                StringComparison.Ordinal)];
        Assert.Contains("Views.VStack(items.ToArray())", dashboardPage);
        Assert.Contains("Views.Scroll(content)", dashboardPage);
        Assert.DoesNotContain("Views.Scroll(items.ToArray())", dashboardPage);

        var quizSource = Source("Features/Quiz/QuizView.cs");
        var quiz = quizSource[quizSource.IndexOf(
            "private static SwiftView QuizPage(", StringComparison.Ordinal)..];
        Assert.Contains("Views.VStack(items.ToArray())", quiz);
        Assert.Contains("Views.Scroll(content)", quiz);
        Assert.Contains("Views.BottomInset(page, footer)", quiz);

        var browser = Source("Features/LearningResources/LearningResourcesView.cs");
        var form = Source("Features/LearningResources/LearningResourceView.cs");
        Assert.Contains("Views.Scroll(scrollContent)", browser);
        Assert.Contains("Views.LazyVGrid(1, rows.ToArray())", browser);
        Assert.Contains("return Views.Toolbar(style.Page(page),", browser);
        Assert.Contains("style.Page(Views.Scroll(content)", form);
        Assert.Contains(".WithAccessibilityIdentifier(\"native.resources.form.scroll.content\")",
            form);
        Assert.DoesNotContain("style.GroupedForm(Views.Form(fields.ToArray()))",
            form);

        var settings = Source("Features/Settings/SettingsView.cs");
        Assert.Contains("Views.VStack(sections.ToArray())", settings);
        Assert.Contains("Views.Scroll(content)", settings);

        var scene = Source("App/ApplicationScene.cs");
        var viewport = scene[scene.IndexOf("private void ViewportChanged(",
            StringComparison.Ordinal)..scene.IndexOf("private static SwiftView? Find(",
            StringComparison.Ordinal)];
        Assert.Contains("native.dashboard.scroll.content", viewport);
        Assert.Contains("native.resources.scroll.content", viewport);
        Assert.Contains("native.settings.scroll.content", viewport);
        Assert.DoesNotContain("Find(_dashboardSlot, NativeAccess.Plan)?.WithPadding(",
            viewport);
        Assert.DoesNotContain("Find(_resourcesSlot, \"native.resources.scroll\")?.WithPadding(",
            viewport);
    }

    [Fact]
    public void SignIn_KeepsTheSourceLabeledFieldAndIconOnlyVisibilityStructure()
    {
        var auth = Source("Features/Authentication/SignInView.cs");
        Assert.Contains("Views.Text(text(NativeText.Email))", auth);
        Assert.Contains("Views.Text(text(NativeText.Password))", auth);
        Assert.Contains("Views.Image(showPassword ? \"eye.slash\" : \"eye\")", auth);
        Assert.Contains("style.OutlineButton(", auth);
        Assert.Contains("togglePassword)", auth);
        Assert.Contains(".WithAccessibilityIdentifier(NativeAccess.PasswordVisibility)", auth);
        Assert.Contains(".WithPadding(SourceUiLayout.LargeCardInset)", auth);
        Assert.Contains(".WithPadding(SourceUiLayout.AuthOuterInset)", auth);
        Assert.Contains("SourceTextRole.Brand,", auth);
        Assert.Contains(".WithForegroundColor(style.Appearance.Text.Light,", auth);
        Assert.Contains("text(\"Common_EmailPlaceholder\")", auth);
        Assert.Contains("text(\"Common_PasswordPlaceholder\")", auth);
        Assert.Contains("\"Login_SigningIn\" : NativeText.SignIn", auth);
        Assert.Contains("style.BootstrapLoginButton(", auth);
        Assert.DoesNotContain("style.FullWidthPrimaryButton(", auth);
        Assert.Contains(".WithCenterWhenShort()", auth);
        Assert.Contains("style.TextInput(passwordInput)", auth);
        Assert.DoesNotContain(".WithPadding(20)", auth);
    }

    [Fact]
    public void Onboarding_KeepsAllSixStepsAndSelectedChoicesWithinTheMobileGutter()
    {
        var onboarding = Source("Features/Onboarding/OnboardingView.cs");
        foreach (var step in new[] { "case 0:", "case 1:", "case 2:",
                     "case 3:", "case 4:", "case 5:" })
            Assert.Contains(step, onboarding);
        Assert.Contains("var fullWidth = groupPosition == SourceButtonGroupPosition.None;",
            onboarding);
        Assert.Contains("style.PrimaryButton(label, action, fullWidth: fullWidth,",
            onboarding);
        Assert.Contains("style.SecondaryButton(label, action, fullWidth: fullWidth,",
            onboarding);
        Assert.Contains("style.PrimaryButton(", onboarding);
        Assert.Contains("dots.Spacing = 4;", onboarding);
        Assert.Contains("Views.Circle(8, color.Light, color.Dark)", onboarding);
        Assert.Contains("native.onboarding.step", onboarding);
        Assert.DoesNotContain("Views.Text($\"{step + 1} / 6\")", onboarding);
        Assert.Contains("style.TextLinkButton(", onboarding);
        Assert.Contains(".WithPadding(SourceUiLayout.PageGutter(style.ViewportWidth))",
            onboarding);
        Assert.Contains("registerNext(nextButton)", onboarding);
    }

    [Fact]
    public void Insight_OnlyTheHeaderIsTheButton_StoryRemainsIndependent()
    {
        var feature = Source("Features/Dashboard/PlanInsightView.cs");
        var insightStart = feature.IndexOf("if (state.Narrative is { } narrative)",
            StringComparison.Ordinal);
        var fallback = feature.IndexOf("else if (!string.IsNullOrWhiteSpace(state.Rationale))",
            insightStart, StringComparison.Ordinal);
        Assert.True(insightStart >= 0 && fallback > insightStart);
        var insight = feature[insightStart..fallback];
        Assert.Contains("Views.Button(insightHeader, toggleInsight)", insight);
        Assert.Contains(".WithAccessibilityIdentifier(\"native.plan.insight.toggle\")", insight);
        Assert.True(insight.IndexOf("Views.Button(insightHeader, toggleInsight)",
            StringComparison.Ordinal) < insight.IndexOf("if (insightExpanded)",
            StringComparison.Ordinal));
        Assert.Contains("insight.Add(style.Text(Views.Text(narrative.Story)", insight);
        Assert.Contains("items.Add(style.Card(insightPanel))", insight);
        Assert.DoesNotContain("Views.Button(Views.VStack(insight.ToArray())", insight);
    }

    [Fact]
    public void SourcePhoneDashboard_UsesGridTilesAndPaddedCards()
    {
        var dashboard = Source("Features/Dashboard/DashboardView.cs");
        var chooseOwn = Source("Features/Dashboard/ChooseOwnView.cs");
        var progress = Source("Features/Dashboard/DashboardLearningProgressView.cs");
        var card = Source("Features/Dashboard/PlanCardView.cs");
        var today = Source("Features/Dashboard/TodaysPlanView.cs");
        Assert.Contains("return DashboardPage(items, style)", dashboard);
        Assert.Contains(".WithPadding(SourceUiLayout.PageGutter(style.ViewportWidth))",
            dashboard);
        Assert.Contains("SourceUiLayout.ActivityColumns(style.ViewportWidth)", chooseOwn);
        Assert.Contains("var tiles = Views.LazyVGrid(style.ViewportWidth < 768 ? 2 : 5,",
            progress);
        Assert.Contains(".WithPadding(SourceUiLayout.CardInset)", card);
        Assert.Contains("ActivitySymbol(card.Activity)", card);
        Assert.Contains("native.plan.progress.row", today);
        Assert.Contains("native.plan.progress.track", today);
        Assert.Contains("Math.Max(1, card.EstimatedMinutes), 4, minutes)", card);
        Assert.Contains("native.dashboard.vocab.stat.{name}.label", progress);
    }

    [Fact]
    public void ResourceBrowse_KeepsOneScrollHostWithLazyCardsOrContiguousRows()
    {
        var browser = Source("Features/LearningResources/LearningResourcesView.cs");
        var row = Source("Features/LearningResources/LearningResourceRowView.cs");
        var start = browser.IndexOf("internal static SwiftView Page(", StringComparison.Ordinal);
        var end = browser.IndexOf("private static SwiftView Filter(", start,
            StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        browser = browser[start..end];
        Assert.Contains("SourceUiLayout.ResourceColumns(style.ViewportWidth)", browser);
        Assert.Contains("items.Add(ResourceListGroup(resources, text, actions.Open, style))",
            browser);
        Assert.Contains("Views.LazyVGrid(1, rows.ToArray())", browser);
        Assert.Contains("if (rows.Count != 0)\n                rows.Add(Views.Divider());",
            browser);
        Assert.Contains("group.Spacing = 0;", browser);
        Assert.Contains("style.BorderedCard(group, radius: style.Appearance.BootstrapRadius)",
            browser);
        Assert.Contains("Views.Scroll(scrollContent)", browser);
        Assert.Contains("return Views.Toolbar(style.Page(page),", browser);
        Assert.Contains("label.WithPadding(8, SourceUiLayout.CardInset,",
            row);
        Assert.Contains("scrollContent.Spacing = SourceUiLayout.PageGap;", browser);
        Assert.Contains("filters.Spacing = SourceUiLayout.ItemGap;", browser);
        Assert.Contains("controls.Spacing = SourceUiLayout.PageGap;", browser);
        var filterPair = browser[browser.IndexOf("var filters = Views.HStack(",
                StringComparison.Ordinal)
            ..browser.IndexOf("var modes = Views.ButtonGroup(",
                StringComparison.Ordinal)];
        Assert.DoesNotContain(".WithAdaptiveStack(", filterPair);
        Assert.Contains("var controls = Views.HStack(filters, modes)", browser);
        Assert.Contains(".WithAdaptiveStack(720)", browser);
        Assert.Contains("if (style.ViewportWidth < 768)\n            modes.WithFlexibleWidth(",
            browser);
        Assert.DoesNotContain("Views.List(", browser);
        Assert.DoesNotContain("style.BorderedCard(label", row);
        Assert.Contains("style.ResourceViewModeButton(", browser);
        Assert.Contains("Views.Toolbar(", browser);
        Assert.Contains("native.resources.search", browser);
        Assert.DoesNotContain("Views.Picker(text(\"Resources_GridView\")", browser);
        Assert.DoesNotContain("Views.Scroll(\n                    Views.LazyVGrid(", browser);
        Assert.DoesNotContain("return Views.VStack(items.ToArray())", browser);
    }

    [Fact]
    public void ResourceBrowserFilters_AllocateHeightFromWrappingTextInsideEachMenuLabel()
    {
        var browser = Source("Features/LearningResources/LearningResourcesView.cs");
        var filter = browser[browser.IndexOf("private static SwiftView Filter(",
            StringComparison.Ordinal)..];

        Assert.Contains("var selectedText = Views.Text(values[selectedIndex])\n"
            + "            .WithFlexibleWidth();", filter);
        Assert.Contains("var menuLabel = style.Input(Views.HStack(selectedText, chevron)\n"
            + "            .WithFlexibleWidth());", filter);
        Assert.Contains("menuLabel.Spacing = SourceUiLayout.ItemGap;", filter);
        Assert.Contains("var menu = Views.Menu(menuLabel, choices)", filter);
        Assert.Contains("Views.Image(\"checkmark\")\n"
            + "                            .WithOpacity(index == selectedIndex ? 1 : 0)",
            filter);
        Assert.DoesNotContain("style.Input(Views.Menu(", filter);
        Assert.DoesNotContain("Views.Picker(", filter);
        Assert.DoesNotContain(".WithLineLimit(", filter);
        Assert.DoesNotContain(".WithMinimumHeight(", filter);
        Assert.DoesNotContain(".WithAdaptiveStack(", filter);
    }

    [Fact]
    public void ResourceForm_UsesSharedCards_WithoutMovingTheBottomSaveIntoVocab()
    {
        var form = Source("Features/LearningResources/LearningResourceView.cs");
        Assert.Contains("internal static SwiftView Page(", form);
        Assert.Contains("fields.Add(CardSection(style, \"native.resources.form.basic.card\", basic));", form);
        Assert.Contains("fields.Add(CardSection(style, \"native.resources.form.media.card\", media));", form);
        Assert.Contains("fields.Add(CardSection(style, \"native.resources.form.transcript.card\", [", form);
        Assert.Contains("fields.Add(CardSection(style, \"native.resources.form.translation.card\", [", form);
        Assert.Contains("inputs.RegisterVocabularySection(vocabularyCard);", form);
        Assert.Contains("fields.Add(vocabularyCard);", form);
        Assert.Contains(".WithPadding(SourceUiLayout.LargeCardInset)", form);
        Assert.Contains(".WithPadding(SourceUiLayout.PageGutter(style.ViewportWidth))", form);
        Assert.Contains("content.Spacing = SourceUiLayout.FormGap;", form);
        Assert.Contains("card.Spacing = SourceUiLayout.FormGap;", form);
        Assert.Contains("return style.Card(card);", form);
        Assert.DoesNotContain("Views.Form(", form);
        Assert.DoesNotContain("Views.Section(", form);
        Assert.True(form.IndexOf("fields.Add(vocabularyCard);",
                StringComparison.Ordinal)
            < form.IndexOf("inputs.RegisterSave(save)", StringComparison.Ordinal));
        var sharedStyle = Source("Styling/SourceUiStyle.cs");
        Assert.Contains("double radius = 16)", sharedStyle);
        Assert.Contains("appearance.ElevatedSurface : appearance.CardSurface;", sharedStyle);
        Assert.Contains(".WithRoundedSurface(surface.Light, surface.Dark, radius);",
            sharedStyle);
        foreach (var field in new[]
            {
                "Title", "Description", "MediaType", "Language",
                "MediaUrl", "Tags", "Transcript", "Translation"
            })
            Assert.Contains($"nameof(LearningResourceViewState.{field})", form);
    }

    [Fact]
    public void ResponsiveStyle_UpdatesGridFontsAndBreakpointCompositionWithFocusRestoration()
    {
        var scene = Source("App/ApplicationScene.cs");
        var viewport = scene[scene.IndexOf("private void ViewportChanged(",
            StringComparison.Ordinal)
            ..scene.IndexOf("private static SwiftView? Find(",
                StringComparison.Ordinal)];
        Assert.Contains(".OnViewportChanged(ViewportChanged)", scene);
        Assert.Contains("SourceUiStyle.RefreshTypography(_root, _app.Appearance, width)",
            viewport);
        Assert.Contains("resourceGrid.GridColumns = SourceUiLayout.ResourceColumns(width)",
            viewport);
        Assert.Contains("activityGrid.GridColumns = SourceUiLayout.ActivityColumns(width)",
            viewport);
        Assert.Contains("if (crossedDesktopBreakpoint)", viewport);
        Assert.Contains("UpdateResources(replaceAll: true);", viewport);
        Assert.Contains("var wasSearching = _resourcesSearch?.FocusRequested == true;",
            viewport);
        Assert.Contains("FindFocusedInput(_formSlot)?.AccessibilityIdentifier",
            viewport);
        Assert.Contains("Replace(_formSlot, ResourceFormContent());",
            viewport);
        Assert.Contains("input.FocusRequested = true;", viewport);
        Assert.DoesNotContain("Publish(", viewport);
        Assert.DoesNotContain("tablet: false", scene);
        Assert.Contains("WithPreferredColorScheme(PreferredSchemeFor(app.Appearance.Selection.Mode))",
            scene);
        Assert.Contains(".WithInlineNavigationTitle()", scene);
        var resourceTitle = scene[scene.IndexOf("_resourcesSlot = Views.VStack(",
            StringComparison.Ordinal)
            ..scene.IndexOf("_resourcesStack = Views.NavigationStack(",
                StringComparison.Ordinal)];
        Assert.Contains(".WithNavigationTitle(localize(\"Resources_Title\"))",
            resourceTitle);
        Assert.Contains(".WithInlineNavigationTitle()", resourceTitle);
        Assert.DoesNotContain("Views.Searchable(_resourcesSlot", scene);
        Assert.Contains("private void UpdateResources(bool replaceAll = false)", scene);
        Assert.Contains("var search = content.Children[0];", scene);
        Assert.Contains("content.Add(child);", scene);

        var style = Source("Styling/SourceUiStyle.cs");
        Assert.Contains("WithTypography(metrics.Size, metrics.Weight, metrics.LineHeight,",
            style);
        Assert.Contains("appearance.FontPostScriptName(metrics.Weight,", style);
        Assert.Contains("WithForegroundColor(color.Light, color.Dark)", style);
        Assert.Contains("WithProgressTrackColor(", style);
        Assert.Contains("Page(form.WithNativeListColors(", style);
        Assert.Contains("Page(list.WithNativeListColors(", style);
        Assert.Contains("Input(field.WithPlaceholderColor(", style);
        Assert.Contains("Views.Button(label, action)", style);
        Assert.Contains(".WithButtonAppearance(fill?.Light, fill?.Dark,", style);
    }

    [Fact]
    public void SourceButtons_KeepNativeHitAndDisabledVisuals_WithARealTransparentOutline()
    {
        var style = Source("Styling/SourceUiStyle.cs");
        var button = style[style.IndexOf("private SwiftView SourceButton(",
            StringComparison.Ordinal)..style.IndexOf("internal SwiftView Input(",
            StringComparison.Ordinal)];
        Assert.Contains("SourceButtonRole.FilledSecondary => appearance.CardSurface",
            button);
        Assert.Contains("SourceButtonRole.Primary => appearance.Primary", button);
        Assert.Contains("SourceButtonRole.BootstrapLoginPrimary => appearance.BootstrapPrimaryFill",
            button);
        Assert.Contains("appearance.BootstrapSuccessOutlineFill,", button);
        Assert.Contains("appearance.BootstrapSuccessOutlineBorder,", button);
        Assert.Contains("appearance.BootstrapSuccessOutlineForeground,", button);
        Assert.Contains("appearance.BootstrapSmallButtonRadius", button);
        Assert.Contains("_ => null", button);
        Assert.Contains("Views.Button(label, action)", button);
        Assert.Contains(".WithButtonAppearance(fill?.Light, fill?.Dark,", button);
        Assert.Contains("border.Light, border.Dark, radius, 0.65)", button);
        Assert.Contains("var borderWidth = role == SourceButtonRole.BootstrapSuccessOutlineSmall",
            button);
        Assert.Contains("appearance.BootstrapBaseButtonBorderWidth", button);
        Assert.Contains("button.WithButtonBorderWidth(borderWidth);", button);
        Assert.Contains("if (role is SourceButtonRole.OutlineSecondary", button);
        Assert.Contains("or SourceButtonRole.BootstrapLoginPrimary", button);
        Assert.Contains("or SourceButtonRole.BootstrapSuccessOutlineSmall", button);
        Assert.Contains("WithSketchyBootstrapCorners(button);", button);
        Assert.Contains("button.WithButtonEllipticalCornerRadii(", style);
        Assert.Contains("255, 25, 25, 225, 225, 25, 25, 255", style);
        Assert.Equal(2, style.Split("WithButtonEllipticalCornerRadii(",
            StringSplitOptions.None).Length - 1);
        Assert.Contains("internal SwiftView QuizPromptIconButton(", style);
        Assert.Equal(5, style.Split("WithButtonBorderWidth(",
            StringSplitOptions.None).Length - 1);
        Assert.Contains(".WithMinimumHeight(SourceUiLayout.MinimumTouchHeight)",
            button);
        Assert.DoesNotContain(".WithRoundedSurface(", button);
        Assert.DoesNotContain("background ?? appearance.BodyBackground", button);
        Assert.Contains("verticalPadding: 4,", style);
        Assert.Contains("appearance.BootstrapSmallButtonHorizontalPadding(ViewportWidth)",
            style);
        Assert.Contains("appearance.Selection.ThemeId != \"sketchy\"", style);
        Assert.Contains("horizontalPadding: 48", style);
        Assert.Contains("var paddingX = horizontalPadding", style);

        var project = Source("SentenceStudio.Native.iOS.csproj");
        Assert.Contains(
            "<PackageVersion Include=\"Microsoft.SwiftUI\" Version=\"0.1.0-preview.25\" />",
            project);
        Assert.Contains("SentenceStudio.Native.SourceAtBuildSha", project);
    }

    [Fact]
    public void MappedResourceModeAndQuizChoice_UseSourceClassShapeWithoutChangingOtherThemes()
    {
        var style = Source("Styling/SourceUiStyle.cs");
        var mode = style[style.IndexOf("internal SwiftView ResourceViewModeButton(",
            StringComparison.Ordinal)..style.IndexOf("internal SwiftView FullWidthPrimaryButton(",
            StringComparison.Ordinal)];
        Assert.Contains(".WithButtonAppearance(fill?.Light, fill?.Dark,", mode);
        Assert.Contains("border.Light, border.Dark, appearance.BootstrapSmallButtonRadius)",
            mode);
        Assert.Contains("WithButtonBorderWidth(appearance.BootstrapBaseButtonBorderWidth);",
            mode);
        Assert.Contains("WithGroupCorners(button, SourceButtonGroupClass.Small, groupPosition);",
            mode);
        Assert.Contains("button.Text = name;", mode);

        var feature = Source("Features/Quiz/QuizQuestionView.cs");
        var quizChoice = feature[feature.IndexOf("private static SwiftView QuizChoice(",
            StringComparison.Ordinal)..];
        Assert.Contains("correct ? style.Appearance.Success", quizChoice);
        Assert.Contains("incorrect ? style.Appearance.Danger", quizChoice);
        Assert.Contains("style.Appearance.BootstrapButtonBorder", quizChoice);
        Assert.Contains("if (!sourceClass)", quizChoice);
        Assert.Contains("button.WithButtonAppearance(fill?.Light, fill?.Dark,", quizChoice);
        Assert.Contains("style.Appearance.BootstrapNeutralDisabledBorder", quizChoice);
        Assert.Contains("WithButtonBorderWidth(style.Appearance.BootstrapBaseButtonBorderWidth);",
            quizChoice);
        Assert.Contains("style.WithSketchyBootstrapCorners(button);", quizChoice);
        Assert.Contains("button.IsEnabled = !answered && !busy;", quizChoice);
    }

    [Fact]
    public void BriteHardShadow_StaysOnSourceFilledButtonsOnly()
    {
        var style = Source("Styling/SourceUiStyle.cs");
        var shadow = style[style.IndexOf("private SwiftView WithBriteHardShadow(",
            StringComparison.Ordinal)..style.IndexOf("private SwiftView SourceButton(",
            StringComparison.Ordinal)];
        Assert.Contains("appearance.Selection.ThemeId == \"brite\"", shadow);
        Assert.Contains("button.WithShadow(\"#000000\", \"#000000\", 0, 1, 3)",
            shadow);
        Assert.Contains(".WithButtonShadowOffsetX(3)", shadow);

        var sourceButton = style[style.IndexOf("private SwiftView SourceButton(",
            StringComparison.Ordinal)..style.IndexOf("internal SwiftView Input(",
            StringComparison.Ordinal)];
        var selected = sourceButton[sourceButton.IndexOf(
            "if (role is SourceButtonRole.Primary", StringComparison.Ordinal)..];
        Assert.Contains("or SourceButtonRole.FilledSecondary", selected);
        Assert.Contains("or SourceButtonRole.BootstrapLoginPrimary", selected);
        Assert.Contains("WithBriteHardShadow(button);", selected);
        Assert.DoesNotContain("SourceButtonRole.OutlineSecondary", selected);
        Assert.DoesNotContain("SourceButtonRole.BootstrapSuccessOutlineSmall", selected);
        Assert.True(sourceButton.IndexOf(".WithButtonAppearance(", StringComparison.Ordinal)
            < sourceButton.LastIndexOf("WithBriteHardShadow(button);",
                StringComparison.Ordinal));

        var mode = style[style.IndexOf("internal SwiftView ResourceViewModeButton(",
            StringComparison.Ordinal)..style.IndexOf("internal SwiftView FullWidthPrimaryButton(",
            StringComparison.Ordinal)];
        Assert.Contains("if (selected)\n            WithBriteHardShadow(button);", mode);
        Assert.Contains("else if (appearance.Selection.ThemeId == \"brite\")", mode);
        Assert.Contains("button.WithShadow(\"#000000\", \"#000000\", 0, 0)", mode);
        Assert.Contains(".WithAccessibilitySelected(selected);", mode);
        Assert.True(mode.IndexOf(".WithButtonAppearance(", StringComparison.Ordinal)
            < mode.IndexOf("WithBriteHardShadow(button);", StringComparison.Ordinal));

        var feature = Source("Features/Quiz/QuizQuestionView.cs");
        var choice = feature[feature.IndexOf("private static SwiftView QuizChoice(",
            StringComparison.Ordinal)..];
        Assert.DoesNotContain("WithBriteHardShadow", choice);
    }

    [Fact]
    public void SourceLink_KeepsOneCenteredButtonAndHonorsSketchyTextDecoration()
    {
        var style = Source("Styling/SourceUiStyle.cs");
        var link = style[style.IndexOf("internal SwiftView TextLinkButton(",
            StringComparison.Ordinal)..style.IndexOf("internal SwiftView ResourceViewModeButton(",
            StringComparison.Ordinal)];
        Assert.Contains("appearance.Selection.ThemeId != \"sketchy\"", link);
        Assert.Contains("label.Spacing = 4;", link);
        Assert.Contains(".WithPadding(6, 16, 6, 16)", link);
        Assert.Contains(".WithMinimumHeight(SourceUiLayout.MinimumTouchHeight)", link);
        Assert.Contains(".WithAccessibilityIdentifier(accessibilityIdentifier)", link);
        Assert.Contains("if (appearance.Selection.ThemeId == \"brite\")", link);
        Assert.Contains("button.WithButtonAppearance(null, null,", link);
        Assert.Contains("border.Light, border.Dark, appearance.BootstrapSmallButtonRadius)",
            link);
        Assert.Contains("WithBriteHardShadow(button);", link);
        Assert.Contains("else if (appearance.Selection.ThemeId == \"sketchy\")", link);
        Assert.Contains("button.WithButtonAppearance(null, null, null, null,", link);
        Assert.Contains("appearance.BootstrapSmallButtonRadius)", link);
        Assert.Contains(".WithButtonBorderWidth(appearance.BootstrapBaseButtonBorderWidth);",
            link[link.IndexOf("else if (appearance.Selection.ThemeId == \"sketchy\")",
                StringComparison.Ordinal)..]);
        Assert.Contains("WithSketchyBootstrapCorners(button);", link);
        Assert.True(link.IndexOf("button.WithButtonAppearance(null, null, null, null,",
            StringComparison.Ordinal) < link.IndexOf("WithSketchyBootstrapCorners(button)",
                StringComparison.Ordinal));
        Assert.Contains("Views.HStack(button).WithFlexibleWidth(", link);
        Assert.Contains(": button.WithFlexibleWidth();", link);
        Assert.Equal(1, link.Split("Views.Button(label, action)",
            StringSplitOptions.None).Length - 1);
        var onboarding = Source("Features/Onboarding/OnboardingView.cs");
        Assert.Contains("\"native.onboarding.signout\");", onboarding);
        Assert.Contains("logout.IsEnabled = !busy;", onboarding);
        Assert.Contains("\"arrow.left.square\", signOut", onboarding);
    }

    [Fact]
    public void LoginAndCorrectionOverride_SelectTheirOwnSourceClassesOnly()
    {
        var auth = Source("Features/Authentication/SignInView.cs");
        var feedback = Source("Features/Quiz/QuizFeedbackView.cs");

        Assert.Equal(1, auth.Split("style.BootstrapLoginButton(",
            StringSplitOptions.None).Length - 1);
        Assert.Contains("style.FullWidthPrimaryButton(",
            Source("Features/LearningResources/LearningResourceView.cs"));
        Assert.Contains("if (state.RequiresCorrection)", feedback);
        Assert.DoesNotContain("style.PrimaryButton(", feedback);
        Assert.Contains("style.QuizPrimaryButton(", feedback);
        Assert.Contains("style.SuccessOutlineSmallButton(", feedback);
        Assert.Contains("Views.Image(\"checkmark\")", feedback);
        Assert.Contains(".WithAccessibilityLabel(text(NativeText.QuizOverride))",
            feedback);
        Assert.Equal(1, feedback.Split("\"native.quiz.override\"",
            StringSplitOptions.None).Length - 1);
        Assert.Contains("text(NativeText.QuizNext), \"chevron.right\", next", feedback);
        Assert.DoesNotContain("style.SecondaryButton(", feedback);
        Assert.DoesNotContain("style.SuccessOutlineSmallButton(", auth);
        Assert.Contains("signIn, canSubmit)", auth);
        Assert.Contains("SetBootstrapLoginAvailability(button, appearance, enabled)",
            Source("Styling/SourceUiStyle.cs"));
        Assert.Contains("SetBootstrapLoginAvailability(submit, Appearance,",
            Source("App/ApplicationController.cs"));
        Assert.Contains("SourceButtonRole.Primary => appearance.Primary",
            Source("Styling/SourceUiStyle.cs"));
    }

    [Fact]
    public void ResourceToolbar_UpdatesActionsWithoutTreatingItAsAnAddRemoveContainer()
    {
        var scene = Source("App/ApplicationScene.cs");
        var update = scene[scene.IndexOf("private void UpdateResources(bool replaceAll = false)",
            StringComparison.Ordinal)..scene.IndexOf("private static bool SameToolbarActions(",
            StringComparison.Ordinal)];
        Assert.Contains("!SameToolbarActions(current, next)", update);
        Assert.Contains("for (var actionIndex = 1; actionIndex < current.Children.Count;",
            update);
        Assert.Contains("child.IsEnabled = nextChild.IsEnabled;", update);
        Assert.Contains("child.Text = nextChild.Text;", update);
        Assert.DoesNotContain("current.RemoveAt(", update);
        Assert.DoesNotContain("current.Add(", update);
        Assert.Contains("content.Add(child);", update);
    }

    [Fact]
    public void RejectedSourceClassCallers_UseTheirSpecificButtonStateAndGeometry()
    {
        var style = Source("Styling/SourceUiStyle.cs");
        var feature = Source("Features/Dashboard/DashboardView.cs");
        var question = Source("Features/Quiz/QuizQuestionView.cs");
        var feedback = Source("Features/Quiz/QuizFeedbackView.cs");
        var onboarding = Source("Features/Onboarding/OnboardingView.cs");
        var resources = Source("Features/LearningResources/LearningResourcesView.cs");
        var controller = Source("App/ApplicationController.cs");

        Assert.Contains("horizontalPadding: 48", style);
        Assert.Contains("Views.HStack(submit)", question);
        Assert.Contains("Views.HStack(nextButton)", feedback);
        Assert.Contains("correcting ? submitCorrection : submitText", question);
        Assert.Contains("\"chevron.right\", next", feedback);
        Assert.Contains("!string.IsNullOrWhiteSpace(value)", question);
        Assert.Contains("SourceButtonGroupPosition.First", resources);
        Assert.Contains("SourceButtonGroupPosition.Last", resources);
        Assert.Contains("Views.ButtonGroup([", resources);
        Assert.Contains("selectedIndex: state.ViewMode == NativeResourceViewMode.Grid ? 0 : 1)",
            resources);
        Assert.Contains("SourceButtonGroupPosition.First", feature);
        Assert.Contains("SourceButtonGroupPosition.Last", feature);
        Assert.Contains("Views.ButtonGroup([", feature);
        Assert.Contains("selectedIndex: state.Mode == NativeDashboardMode.TodaysPlan ? 0 : 1)",
            feature);
        Assert.Contains("GroupPosition(index,", onboarding);
        Assert.Contains("Views.ButtonGroup(buttons, wraps: true, selectedIndex: selectedIndex)",
            onboarding);
        Assert.Contains("group.SelectedIndex = selectedIndex;", onboarding);
        Assert.Contains("SelectPreference(minutesGroup!, style.Appearance, index)", onboarding);
        Assert.Contains("SelectPreference(levelGroup!, style.Appearance, index)", onboarding);
        Assert.DoesNotContain("buttons.Chunk(3)", onboarding);
        Assert.Contains("else if (_onboardingStep != 4)", controller);
        Assert.Contains("neutralDisabled && sourceClass ? null", question);
        Assert.Contains("SetCustomButtonAvailability(nextButton, style.Appearance,",
            onboarding);
        Assert.Contains("SetCustomButtonAvailability(starter, style.Appearance,",
            resources);
        Assert.Contains("SetCustomButtonAvailability(", controller);
        Assert.Contains("button.Children[0].WithForegroundColor(", style);
    }

    [Fact]
    public void StarterLoading_IsAnIsolatedPresenterStateProjectedThroughTheRealView()
    {
        var resources = Source("Features/LearningResources/LearningResourcesCoordinator.cs");
        var view = Source("Features/LearningResources/LearningResourcesView.cs");
        var preview = Source("App/ApplicationController.Quiz.cs");
        var fixture = Source("Platform/NativePreviewFixture.cs");
        Assert.Contains("or \"starter-loading\"", fixture);
        Assert.Contains("#if DEBUG\n    internal async Task BeginControlledStarterPreviewAsync(",
            resources);
        Assert.Contains("_presenter.LoadListAsync(cancellationToken)", resources);
        Assert.Contains("NativeResourceListPhase.NoMatches", resources);
        Assert.Contains("state = state with { StarterBusy = true };", resources);
        Assert.Contains("LearningResourcesView.Page(state, _listFeedback, style, _text,",
            resources);
        Assert.Contains("SourceUiStyle.SetCustomButtonAvailability(starter, style.Appearance,",
            view);
        Assert.Contains("!state.StarterBusy", view);
        Assert.Contains("if (fixture.Surface == \"starter-loading\")", preview);
        Assert.DoesNotContain("CreateStarterAsync()", preview[
            preview.IndexOf("if (fixture.Surface == \"starter-loading\")",
                StringComparison.Ordinal)
            ..preview.IndexOf("if (fixture.Surface == \"dashboard\")",
                StringComparison.Ordinal)]);
    }

    [Fact]
    public void OriginalButtonGroups_UseThemeStrokeOverlap_OnlyDashboardExpandsChildren()
    {
        var dashboardSource = Source("Features/Dashboard/DashboardView.cs");
        var dashboard = dashboardSource[..dashboardSource.IndexOf(
            "internal static SwiftView WithToolbar(", StringComparison.Ordinal)];
        var resourcesSource = Source("Features/LearningResources/LearningResourcesView.cs");
        var resources = resourcesSource[resourcesSource.IndexOf(
            "var modes = Views.ButtonGroup([", StringComparison.Ordinal)..
            resourcesSource.IndexOf("items.Add(controls);", StringComparison.Ordinal)];
        var onboardingSource = Source("Features/Onboarding/OnboardingView.cs");
        var preferences = onboardingSource[onboardingSource.IndexOf(
            "private static SwiftView PreferenceGroup(", StringComparison.Ordinal)..
            onboardingSource.IndexOf("private static void SelectPreference(",
                StringComparison.Ordinal)];

        Assert.Contains(".WithButtonGroupLayout(style.Appearance.BootstrapBaseButtonBorderWidth,",
            dashboard);
        Assert.Contains("fillAvailableWidth: true", dashboard);
        Assert.Contains(".WithButtonGroupLayout(style.Appearance.BootstrapBaseButtonBorderWidth)",
            resources);
        Assert.DoesNotContain("fillAvailableWidth: true", resources);
        Assert.Contains("Views.ButtonGroup(buttons, wraps: true, selectedIndex: selectedIndex)",
            preferences);
        Assert.Contains(".WithButtonGroupLayout(appearance.BootstrapBaseButtonBorderWidth)",
            preferences);
        Assert.DoesNotContain("fillAvailableWidth: true", preferences);
    }

    private static string Source(string relative) =>
        File.ReadAllText(Path.Combine(HeadPath, relative));

    private static string HeadPath => Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(CurrentFile())!, "../../src/SentenceStudio.Native.iOS"));

    private static string CurrentFile([CallerFilePath] string path = "") => path;
}
