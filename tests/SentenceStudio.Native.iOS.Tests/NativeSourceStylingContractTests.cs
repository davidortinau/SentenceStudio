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
        // Captain 2026-11-10: the Resources page is the shared browse layout; its scroll content
        // owns the gutter (BrowserPage.SingleColumn), as on Vocabulary.
        Assert.Contains("return BrowserPage.SingleColumn(Prefix, children, width, new BrowserPage.PageSearch(", browser);
        Assert.Contains("return BrowserPage.WithToolbar(Prefix, page, style, actions.PageWidthChanged,",
            browser);
        var browse = Source("Features/Browse/BrowserPage.cs");
        Assert.Contains(".WithPadding(SourceUiLayout.PageGutter(style.ViewportWidth))", browse);
        Assert.Contains("return Views.Scroll(content)\n            .WithScrollDismissesKeyboard(SwiftScrollDismissesKeyboard.Interactively)",
            browse);
        // Captain 2026-11-12, "I want the native way": the narrow page's search is the system
        // search field, its filters as tokens in the field.
        Assert.Contains(": Views.Searchable(content, search.Placeholder, search.Text,", browse);
        Assert.Contains(": NativeList($\"{prefix}.scroll.content\", children, list);", browse);
        Assert.Contains("node.SearchTokens = nextNode.SearchTokens;", Source("Features/Browse/BrowserPageUpdate.cs"));
        Assert.DoesNotContain("ComposerCapsule", browse);
        // Captain 2026-11-13: native Resource form. The Resource form is a grouped system Form
        // (its own native viewport and insets), not a padded ScrollView; dragging its rows puts
        // the keyboard away.
        Assert.Contains("style.GroupedForm(Views.Form(sections.ToArray())", form);
        Assert.Contains(".WithScrollDismissesKeyboard(SwiftScrollDismissesKeyboard.Interactively)",
            form);
        Assert.Contains(".WithAccessibilityIdentifier(\"native.resources.form.scroll\")", form);
        Assert.DoesNotContain("Views.Scroll(", form);
        Assert.DoesNotContain("PageGutter", form);

        // Captain 2026-11-13 (the native form pattern): Settings is a grouped Form; the
        // system sets its insets, so the viewport pass no longer pads it.
        var settings = Source("Features/Settings/SettingsView.cs");
        Assert.Contains("NativeForm.Page(style, sections, \"native.settings.form\"", settings);
        Assert.DoesNotContain("Views.Scroll(", settings);

        var scene = Source("App/ApplicationScene.cs");
        var viewport = scene[scene.IndexOf("private void ViewportChanged(",
            StringComparison.Ordinal)..scene.IndexOf("private static SwiftView? Find(",
            StringComparison.Ordinal)];
        Assert.Contains("native.dashboard.scroll.content", viewport);
        Assert.Contains("native.resources.scroll.content", viewport);
        Assert.DoesNotContain("native.settings.scroll.content", viewport);
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
        Assert.Contains("SourceUiLayout.ActivityColumns(style.ViewportWidth, style.TextScale)", chooseOwn);
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
        // Grid cards follow the page's own width and the filter column; the list keeps the
        // source's contiguous bordered rows.
        Assert.Contains("GridColumns(width, style.TextScale, !state.FilterPanelHidden)", browser);
        Assert.Contains("return effective < 600 ? 1 : effective < 1000 ? 2 : 3;", browser);
        Assert.Contains("return SourceUiLayout.ResourceColumns(width);", browser);
        // Captain 2026-11-12: list mode is a native List of rows (icons kept) with swipe actions:
        // Delete (not smart resources, confirmed) and Study; the grid stays a lazy grid.
        Assert.Contains("children.AddRange(resources.Select(item => ListRow(item, text, actions, style, width)));", browser);
        Assert.Contains("return Views.SwipeRow(row, trailing, leading)", browser);
        Assert.Contains(".WithButtonRole(SwiftButtonRole.Destructive)", browser);
        Assert.Contains("AsNativeList(state) ? style : null);", browser);
        Assert.Contains("asList: AsNativeList(state));", browser);
        Assert.Contains("return BrowserPage.SingleColumn(Prefix, children, width, new BrowserPage.PageSearch(", browser);
        Assert.Contains("return BrowserPage.Wide(Prefix, style,", browser);
        Assert.Contains("label.WithPadding(8, SourceUiLayout.CardInset,", row);
        Assert.DoesNotContain("Views.List(", browser);
        Assert.DoesNotContain("style.BorderedCard(label", row);
        Assert.Contains("native.resources.search", browser);
        // The wide results scroll on their own beside the filter column; the search stays put.
        var browse = Source("Features/Browse/BrowserPage.cs");
        Assert.Contains("Views.Scroll(content).WithAccessibilityIdentifier($\"{prefix}.results.scroll\")",
            browse);
        Assert.Contains(".WithFixedWidth(FilterColumnWidth(width, style.TextScale))", browse);
    }

    [Fact]
    public void ResourceBrowserFilters_UseTheVocabularyFilterMenus()
    {
        // Captain 2026-11-10: the Resources filters are the Vocabulary page's labelled menus.
        var browser = Source("Features/LearningResources/LearningResourcesView.cs");
        Assert.Contains("BrowserPage.FilterMenu(Prefix, \"type\"", browser);
        Assert.Contains("BrowserPage.FilterMenu(Prefix, \"language\"", browser);
        Assert.DoesNotContain("Views.Menu(", browser);
        var browse = Source("Features/Browse/BrowserPage.cs");
        var filter = browse[browse.IndexOf("internal static SwiftView FilterMenu(",
            StringComparison.Ordinal)..];
        Assert.Contains("var picker = style.Input(Views.Picker(", filter);
        Assert.Contains(".WithPickerStyle(SwiftPickerStyle.Menu)", filter);
        Assert.Contains("style.Text(Views.Text(label), SourceTextRole.Body2, SourceTextTone.Secondary)",
            filter);
    }

    /// <summary>
    /// Captain 2026-11-13: native Resource form. The form's groups are system Form Sections
    /// (Basic Info, each long text, Media Content on create, the vocabulary) instead of the
    /// custom cards; Save stays in the toolbar and never moves into the vocabulary.
    /// </summary>
    [Fact]
    public void ResourceForm_UsesNativeSections_WithoutMovingSaveIntoVocab()
    {
        var form = Source("Features/LearningResources/LearningResourceView.cs");
        var fields = Source("Features/LearningResources/LearningResourceFieldsView.cs");
        var vocabulary = Source("Features/LearningResources/LearningResourceVocabularyView.cs");
        Assert.Contains("internal static SwiftView Page(", form);
        Assert.Contains("sections.Add(Views.Section(text(creating\n                    ? \"ResourceAdd_BasicInfo\" : \"ResourceEdit_BasicInfo\"), basic.ToArray()));", form);
        Assert.Contains("sections.Add(Views.Section(text(\"ResourceAdd_MediaContent\"),", form);
        Assert.Contains("sections.Add(LearningResourceFieldsView.Area(style, text(\"ResourceEdit_Transcript\"),", form);
        Assert.Contains("sections.Add(LearningResourceFieldsView.Area(style, text(\"ResourceEdit_Translation\"),", form);
        Assert.Contains("Views.Section(label, readOnly", fields);
        Assert.Contains("inputs.RegisterVocabularySection(section);", vocabulary);
        Assert.Contains("sections.Add(section);", vocabulary);
        Assert.DoesNotContain("CardSection(", form);
        Assert.DoesNotContain("style.Card(", form);
        // A Section carries no automation ID: SwiftUI would give it to every row.
        Assert.DoesNotContain("Views.Section(string.Empty, notices.ToArray())\n                .WithAccessibilityIdentifier", form);
        Assert.DoesNotContain(".WithAccessibilityIdentifier(\"native.resources.form.basic.card\")", form);
        var toolbar = form[form.IndexOf("internal static SwiftView WithToolbar(", StringComparison.Ordinal)..];
        Assert.Contains("Views.Button(text(\"Common_Save\"), actions.Save)", toolbar);
        Assert.DoesNotContain("Common_Save", vocabulary);
        var sharedStyle = Source("Styling/SourceUiStyle.cs");
        Assert.Contains("internal SwiftView GroupedForm(SwiftView form)", sharedStyle);
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
        Assert.Contains("resourceGrid.GridColumns = LearningResourcesView.GridColumns(",
            viewport);
        Assert.Contains("resourcePanel.WithFixedWidth(BrowserPage.FilterColumnWidth(", viewport);
        Assert.Contains("activityGrid.GridColumns = SourceUiLayout.ActivityColumns(width, _style.TextScale)",
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
        // The Library root carries the inline title; Resources is its first page.
        var library = Source("App/ApplicationScene.Library.cs");
        var libraryTitle = library[library.IndexOf("_libraryRoot = _libraryListSlot",
            StringComparison.Ordinal)
            ..library.IndexOf("_librarySplit = new LibrarySplitNavigation(",
                StringComparison.Ordinal)];
        Assert.Contains(".WithNavigationTitle(_app.Localize(\"Nav_Library\"))", libraryTitle);
        Assert.Contains(".WithInlineNavigationTitle()", libraryTitle);
        Assert.DoesNotContain("Views.Searchable(_resourcesSlot", scene);
        Assert.Contains("private void UpdateResources(bool replaceAll = false)", scene);
        Assert.Contains("BrowserPageUpdate.Resources.TryUpdateInPlace(current, next)", scene);

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
            "<PackageVersion Include=\"Microsoft.SwiftUI\" Version=\"0.1.0-preview.43\" />",
            project);
        Assert.Contains("SentenceStudio.Native.SourceAtBuildSha", project);
    }

    [Fact]
    public void PagesWithAMultilineEditor_LetADragPutTheKeyboardAway()
    {
        // A TextEditor's Return adds a line, so without this nothing but the page's own action
        // closes the keyboard, and it covers the tab bar (found on Import Content, 2026-11-06).
        const string dismiss =
            ".WithScrollDismissesKeyboard(SwiftScrollDismissesKeyboard.Interactively)";
        foreach (var page in new[]
        {
            "Features/Import/ImportContentView.cs",
            "Features/LearningResources/LearningResourceView.cs",
            "Features/Quiz/QuizView.cs",
        })
            Assert.Contains(dismiss, Source(page));
        // The native form pattern (Captain 2026-11-13): NativeForm.Page carries it for every form.
        Assert.Contains(dismiss, Source("Styling/NativeForm.cs"));
        foreach (var page in new[]
        {
            "Features/Skills/SkillProfileFormView.cs",
            "Features/Vocabulary/VocabularyWordEditView.cs",
        })
            Assert.Contains("NativeForm.Page(", Source(page));
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
        // Captain 2026-11-10: the resource form's Save is the toolbar's plain Save only (the
        // full-width Save Resource under the add form is gone, as on every editor).
        var resourceForm = Source("Features/LearningResources/LearningResourceView.cs");
        Assert.DoesNotContain("style.FullWidthPrimaryButton(", resourceForm);
        Assert.DoesNotContain("native.resources.add.save.bottom", resourceForm);
        Assert.Contains("var save = Views.Button(text(\"Common_Save\"), actions.Save)", resourceForm);
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
            StringComparison.Ordinal)..scene.IndexOf("private void UpdateResourceFilters(",
            StringComparison.Ordinal)];
        Assert.Contains("BrowserPageUpdate.Resources.TryUpdateInPlace(current, next)", update);
        Assert.DoesNotContain("current.RemoveAt(", update);
        Assert.DoesNotContain("current.Add(", update);
        // A toolbar's buttons keep their nodes and take the new symbol, label and state.
        var updater = Source("Features/Browse/BrowserPageUpdate.cs");
        Assert.Contains("for (var index = 1; index < current.Children.Count; index++)", updater);
        Assert.Contains("CopyButtonState(current.Children[index], next.Children[index]);", updater);
        Assert.Contains("child.IsEnabled = nextChild.IsEnabled;", updater);
        Assert.Contains("child.Text = nextChild.Text;", updater);
        Assert.Contains("region.Add(child);", updater);
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
        // Captain 2026-11-10: the grid/list switch is a toolbar icon, as on Vocabulary.
        Assert.DoesNotContain("Views.ButtonGroup([", resources);
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
        // Captain 2026-11-10: Resources has no button group (its grid/list switch is a toolbar icon).
        Assert.DoesNotContain("Views.ButtonGroup(",
            Source("Features/LearningResources/LearningResourcesView.cs"));
        var onboardingSource = Source("Features/Onboarding/OnboardingView.cs");
        var preferences = onboardingSource[onboardingSource.IndexOf(
            "private static SwiftView PreferenceGroup(", StringComparison.Ordinal)..
            onboardingSource.IndexOf("private static void SelectPreference(",
                StringComparison.Ordinal)];

        Assert.Contains(".WithButtonGroupLayout(style.Appearance.BootstrapBaseButtonBorderWidth,",
            dashboard);
        Assert.Contains("fillAvailableWidth: true", dashboard);
        Assert.Contains("Views.ButtonGroup(buttons, wraps: true, selectedIndex: selectedIndex)",
            preferences);
        Assert.Contains(".WithButtonGroupLayout(appearance.BootstrapBaseButtonBorderWidth)",
            preferences);
        Assert.DoesNotContain("fillAvailableWidth: true", preferences);
    }

    [Fact]
    public void VocabularyMatchingBoard_FillsHeightAndKeepsHiddenSlots()
    {
        var screen = Source("Features/Activities/VocabularyMatchingScreen.cs");
        // Ready board is bounded (no Scroll) so equal flexible rows fill the screen.
        Assert.Contains("state.Phase == NativePhase.Ready\n            ? Views.VStack(content.WithFlexibleHeight())",
            screen);
        Assert.DoesNotContain("Views.LazyVGrid(", screen);
        var grid = screen[screen.IndexOf("private SwiftView Grid(", StringComparison.Ordinal)..];
        Assert.Contains("var row = Views.HStack(cells.ToArray())\n                .WithFlexibleWidth()\n                .WithFlexibleHeight();",
            grid);
        // Hidden native tiles and short-row fillers take a full, empty tile slot.
        Assert.Contains("Views.VStack().WithFlexibleWidth().WithFlexibleHeight();", screen);
        Assert.Contains("if (!tile.IsVisible && !tile.IsMatched)\n            return Placeholder();", screen);
        Assert.Contains(": Placeholder());", grid);
        // Matched tiles stay in place at the source's 0.3 opacity.
        Assert.Contains(".WithOpacity(tile.IsMatched ? 0.3 : 1);", screen);
    }

    private static string Source(string relative) =>
        File.ReadAllText(Path.Combine(HeadPath, relative));

    private static string HeadPath => Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(CurrentFile())!, "../../src/SentenceStudio.Native.iOS"));

    private static string CurrentFile([CallerFilePath] string path = "") => path;
}
