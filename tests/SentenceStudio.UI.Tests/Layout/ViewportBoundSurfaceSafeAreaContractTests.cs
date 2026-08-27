using System.Text.RegularExpressions;
using FluentAssertions;

namespace SentenceStudio.UI.Tests.Layout;

public class ViewportBoundSurfaceSafeAreaContractTests
{
    private static readonly string Root = RepoRoot();
    private static readonly string UiRoot = Path.Combine(
        Root, "src", "SentenceStudio.UI");
    private static readonly string CssPath = Path.Combine(
        UiRoot, "wwwroot", "css", "app.css");
    private static readonly string Css = File.ReadAllText(CssPath);
    private static readonly string Scene = File.ReadAllText(Path.Combine(
        UiRoot, "Pages", "Scene.razor"));
    private static readonly string Resources = File.ReadAllText(Path.Combine(
        UiRoot, "Pages", "Resources.razor"));
    private static readonly string WhatsNewModal = File.ReadAllText(Path.Combine(
        UiRoot, "Shared", "WhatsNewModal.razor"));

    private static readonly HashSet<string> FixedUtilityClasses =
        new(StringComparer.Ordinal)
        {
            "position-fixed",
            "fixed-top",
            "fixed-bottom"
        };

    private static readonly IReadOnlyDictionary<string, FixedSurfaceDisposition> FixedSurfaceInventory =
        new Dictionary<string, FixedSurfaceDisposition>(StringComparer.Ordinal)
        {
            [".toast-container-ss"] = FixedSurfaceDisposition.OwnsInsets,
            [".scene-gallery-backdrop"] = FixedSurfaceDisposition.EdgeToEdgeBackdrop,
            [".scene-gallery-overlay"] = FixedSurfaceDisposition.OwnsInsets,
            [".sync-overlay"] = FixedSurfaceDisposition.OwnsInsets,
            [".ref-menu-backdrop"] = FixedSurfaceDisposition.EdgeToEdgeBackdrop,
            [".shared-ingest-toast"] = FixedSurfaceDisposition.OwnsInsets,
            [".quiz-fullscreen-overlay"] = FixedSurfaceDisposition.EdgeToEdgeWithSafeChildren,
            [".quiz-fullscreen-close-btn"] = FixedSurfaceDisposition.OwnsInsets,
            [".coach-dialog-backdrop"] = FixedSurfaceDisposition.EdgeToEdgeWithSafeChildren,
            [".sam-fab"] = FixedSurfaceDisposition.OwnsInsets,
            [".sam-panel"] = FixedSurfaceDisposition.OwnsInsets,
            [".sam-backdrop"] = FixedSurfaceDisposition.EdgeToEdgeBackdrop,
            ["razor:Pages/Resources.razor::.resources-starter-overlay[position-fixed]"] =
                FixedSurfaceDisposition.EdgeToEdgeWithSafeChildren
        };

    private static readonly IReadOnlyDictionary<string, SafeAreaEdge[]> OwnsInsetsEdgeCoverage =
        new Dictionary<string, SafeAreaEdge[]>(StringComparer.Ordinal)
        {
            [".toast-container-ss"] = [SafeAreaEdge.Bottom, SafeAreaEdge.Right],
            [".scene-gallery-overlay"] =
                [SafeAreaEdge.Top, SafeAreaEdge.Right, SafeAreaEdge.Bottom, SafeAreaEdge.Left],
            [".sync-overlay"] =
                [SafeAreaEdge.Top, SafeAreaEdge.Right, SafeAreaEdge.Bottom, SafeAreaEdge.Left],
            [".shared-ingest-toast"] =
                [SafeAreaEdge.Left, SafeAreaEdge.Right, SafeAreaEdge.Bottom],
            [".quiz-fullscreen-close-btn"] = [SafeAreaEdge.Top, SafeAreaEdge.Right],
            [".sam-fab"] = [SafeAreaEdge.Bottom, SafeAreaEdge.Right],
            [".sam-panel"] =
                [SafeAreaEdge.Top, SafeAreaEdge.Right, SafeAreaEdge.Bottom, SafeAreaEdge.Left]
        };

    private static readonly string[] CenteredViewportSurfaceInventory =
    [
        ".sync-overlay",
        ".quiz-fullscreen-overlay",
        ".coach-dialog-backdrop",
        "razor:Pages/Resources.razor::.resources-starter-overlay[position-fixed]"
    ];

    [Fact]
    public void Every_local_css_or_razor_fixed_surface_has_a_reviewed_safe_area_disposition()
    {
        var actual = FixedSurfacesInAppCssAndRazor();

        actual.Should().BeEquivalentTo(
           FixedSurfaceInventory.Keys,
           options => options.WithStrictOrdering(),
           "CSS declarations, Razor fixed-position utilities, and inline fixed styles must all be classified");
    }

    [Fact]
    public void Every_inset_owning_surface_declares_the_edges_covered_by_tests()
    {
        var insetOwners = FixedSurfaceInventory
            .Where(item => item.Value == FixedSurfaceDisposition.OwnsInsets)
            .Select(item => item.Key);

        OwnsInsetsEdgeCoverage.Keys.Should().BeEquivalentTo(
            insetOwners,
            "every OwnsInsets classification needs an explicit edge-coverage contract");
        OwnsInsetsEdgeCoverage.Values.Should().OnlyContain(
            edges => edges.Distinct().Count() == edges.Length && edges.Length > 0);
    }

    [Fact]
    public void Scene_gallery_keeps_the_backdrop_edge_to_edge_and_insets_the_interactive_surface()
    {
        var backdrop = CssBlock(".scene-gallery-backdrop");
        backdrop.Should().Contain("inset: 0");
        backdrop.Should().NotContain("safe-area-inset");

        var overlay = CssBlock(".scene-gallery-overlay");
        overlay.Should().Contain(
            "padding: env(safe-area-inset-top, 0px) env(safe-area-inset-right, 0px) env(safe-area-inset-bottom, 0px) env(safe-area-inset-left, 0px)");

        var backdropMarkup = Scene.IndexOf(
            "<div class=\"scene-gallery-backdrop\"", StringComparison.Ordinal);
        var overlayMarkup = Scene.IndexOf(
            "<div class=\"scene-gallery-overlay\">", StringComparison.Ordinal);
        backdropMarkup.Should().BeGreaterThanOrEqualTo(0);
        overlayMarkup.Should().BeGreaterThan(backdropMarkup,
            "the edge-to-edge scrim and inset-owning gallery surface must remain separate siblings");
    }

    [Fact]
    public void Standard_bootstrap_modals_pad_content_inside_each_safe_area_edge()
    {
        var dialog = CssBlock(".modal:not(.coach-modal) > .modal-dialog");
        dialog.Should().Contain(
            "padding: env(safe-area-inset-top, 0px) env(safe-area-inset-right, 0px) env(safe-area-inset-bottom, 0px) env(safe-area-inset-left, 0px)");

        Css.Should().NotMatchRegex(
            @"(?m)^[ \t]*\.modal-backdrop\s*\{",
            "Bootstrap's backdrop must remain a separate edge-to-edge surface");
    }

    [Fact]
    public void Whats_new_modal_uses_the_standard_inset_owning_structure()
    {
        WhatsNewModal.Should().Contain("<div class=\"modal fade show d-block\"");
        WhatsNewModal.Should().Contain(
            "<div class=\"modal-dialog modal-dialog-centered modal-dialog-scrollable\"");
        WhatsNewModal.Should().Contain("<div class=\"modal-content\">");
        WhatsNewModal.Should().NotContain("coach-modal");
    }

    [Fact]
    public void Standard_modal_ownership_does_not_double_pad_the_legacy_coach_modal()
    {
        Css.Should().Contain(".modal:not(.coach-modal) > .modal-dialog");
        Css.Should().NotMatchRegex(
            @"(?m)^[ \t]*\.modal(?:\s|,|\{)[^{]*\{[^}]*safe-area-inset",
            "only a non-Coach dialog descendant may receive the shared insets");

        var coach = CssBlock(".modal.coach-modal .coach-workspace");
        coach.Should().Contain("safe-area-inset-left");
        coach.Should().Contain("safe-area-inset-right");
    }

    [Theory]
    [InlineData(0, 28, 346)]
    [InlineData(62, 62, 312)]
    public void Legacy_coach_tablet_landscape_dialog_stays_inside_874_by_402_safe_areas(
        double safeAreaTop,
        double expectedTop,
        double expectedHeight)
    {
        var tabletBand = CssMediaBlock(
            "@media (min-width: 768px) and (max-width: 991.98px)");
        var coachTokens = Regex.Match(
            tabletBand,
            @"(?ms)\.modal\.coach-modal\s*,\s*\.coach-page\s*\{(?<body>[^}]*)\}");

        coachTokens.Success.Should().BeTrue(
            "the tablet band must explicitly size the legacy Coach modal");
        coachTokens.Groups["body"].Value.Should().Contain(
            "--coach-width: min(94vw, calc(100vw - env(safe-area-inset-left, 0px) - env(safe-area-inset-right, 0px)))");
        coachTokens.Groups["body"].Value.Should().Contain("--coach-height: 92dvh");
        tabletBand.Should().Contain(
            "--coach-height: min(92dvh, calc(100dvh - var(--coach-outer-top) - var(--coach-outer-bottom)))");

        const double viewportWidth = 874;
        const double viewportHeight = 402;
        const double safeAreaLeft = 62;
        const double safeAreaRight = 62;
        const double safeAreaBottom = 20;
        const double bootstrapDesktopMargin = 28;

        var dialogWidth = Math.Min(
            viewportWidth * 0.94,
            viewportWidth - safeAreaLeft - safeAreaRight);
        var dialogLeft = (viewportWidth - dialogWidth) / 2;
        var dialogRight = dialogLeft + dialogWidth;
        var dialogTop = Math.Max(bootstrapDesktopMargin, safeAreaTop);
        var dialogBottomGap = Math.Max(bootstrapDesktopMargin, safeAreaBottom);
        var dialogHeight = Math.Min(
            viewportHeight * 0.92,
            viewportHeight - dialogTop - dialogBottomGap);
        var dialogBottom = dialogTop + dialogHeight;

        dialogLeft.Should().BeGreaterThanOrEqualTo(safeAreaLeft);
        dialogRight.Should().BeLessThanOrEqualTo(viewportWidth - safeAreaRight);
        dialogTop.Should().Be(expectedTop);
        dialogHeight.Should().Be(expectedHeight);
        dialogTop.Should().BeGreaterThanOrEqualTo(safeAreaTop);
        dialogBottom.Should().BeLessThanOrEqualTo(viewportHeight - safeAreaBottom);
    }

    [Theory]
    [InlineData(0, 16, 386, 80.4, 321.6)]
    [InlineData(62, 78, 324, 80.4, 321.6)]
    public void Sam_landscape_states_stay_inside_874_by_402_safe_areas(
        double safeAreaTop,
        double expectedCompactTop,
        double expectedCompactHeight,
        double expectedExpandedTop,
        double expectedExpandedHeight)
    {
        const double viewportWidth = 874;
        const double viewportHeight = 402;
        const double safeAreaLeft = 62;
        const double safeAreaRight = 62;
        const double safeAreaBottom = 20;
        const double compactWidth = 360;
        const double expandedWidth = 520;
        const double compactRequestedHeight = 420;
        const double rem = 16;
        const double expandedVisualTop = 60;

        var compactMaxHeight = viewportHeight - rem - safeAreaTop;
        var compactHeight = Math.Min(compactRequestedHeight, compactMaxHeight);
        var compactTop = viewportHeight - compactHeight;

        var expandedRequestedHeight = viewportHeight * 0.8;
        var expandedTopReservation = Math.Max(
            expandedVisualTop,
            rem + safeAreaTop);
        var expandedMaxHeight = viewportHeight - expandedTopReservation;
        var expandedHeight = Math.Min(expandedRequestedHeight, expandedMaxHeight);
        var expandedTop = viewportHeight - expandedHeight;

        compactTop.Should().Be(expectedCompactTop);
        compactHeight.Should().Be(expectedCompactHeight);
        expandedTop.Should().BeApproximately(expectedExpandedTop, 0.001);
        expandedHeight.Should().BeApproximately(expectedExpandedHeight, 0.001);

        var compactContentLeft = viewportWidth - compactWidth + safeAreaLeft;
        var expandedContentLeft = viewportWidth - expandedWidth + safeAreaLeft;
        var contentRight = viewportWidth - safeAreaRight;
        var contentBottom = viewportHeight - safeAreaBottom;

        compactTop.Should().BeGreaterThanOrEqualTo(safeAreaTop);
        expandedTop.Should().BeGreaterThanOrEqualTo(safeAreaTop);
        compactContentLeft.Should().BeGreaterThanOrEqualTo(safeAreaLeft);
        expandedContentLeft.Should().BeGreaterThanOrEqualTo(safeAreaLeft);
        contentRight.Should().Be(viewportWidth - safeAreaRight);
        contentBottom.Should().Be(viewportHeight - safeAreaBottom);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(62, 62)]
    public void Fullscreen_sam_keeps_its_outer_frame_edge_to_edge_and_content_inside_874_by_402_safe_areas(
        double safeAreaTop,
        double expectedHeaderTop)
    {
        const double viewportWidth = 874;
        const double viewportHeight = 402;
        const double safeAreaLeft = 62;
        const double safeAreaRight = 62;
        const double safeAreaBottom = 20;

        var outer = new
        {
            Top = 0d,
            Right = viewportWidth,
            Bottom = viewportHeight,
            Left = 0d
        };
        var content = new
        {
            HeaderTop = safeAreaTop,
            Right = viewportWidth - safeAreaRight,
            Bottom = viewportHeight - safeAreaBottom,
            Left = safeAreaLeft
        };

        outer.Top.Should().Be(0);
        outer.Right.Should().Be(viewportWidth);
        outer.Bottom.Should().Be(viewportHeight);
        outer.Left.Should().Be(0);
        content.HeaderTop.Should().Be(expectedHeaderTop);
        content.Left.Should().BeGreaterThanOrEqualTo(safeAreaLeft);
        content.Right.Should().BeLessThanOrEqualTo(viewportWidth - safeAreaRight);
        content.Bottom.Should().BeLessThanOrEqualTo(viewportHeight - safeAreaBottom);
    }

    [Fact]
    public void Custom_coach_dialog_keeps_its_scrim_edge_to_edge_and_centers_inside_its_usable_rectangle()
    {
        var backdrop = CssBlock(".coach-dialog-backdrop");
        backdrop.Should().Contain("inset: 0");
        backdrop.Should().Contain("box-sizing: border-box");
        backdrop.Should().Contain("calc(1rem + env(safe-area-inset-top, 0px))");
        backdrop.Should().Contain("calc(1rem + env(safe-area-inset-right, 0px))");
        backdrop.Should().Contain("calc(1rem + env(safe-area-inset-bottom, 0px))");
        backdrop.Should().Contain("calc(1rem + env(safe-area-inset-left, 0px))");

        var dialog = CssBlock(".coach-dialog-backdrop > .coach-dialog");
        dialog.Should().Contain("width: min(28rem, 100%)");
        dialog.Should().Contain("max-height: 100%");
        dialog.Should().NotContain("safe-area-inset",
            "the fixed containing overlay, not its centered child, owns asymmetric insets");

        Css.Should().NotMatchRegex(
            @"(?m)^[ \t]*\.coach-dialog\s*\{",
            "confirmation-dialog sizing must not leak onto the legacy Bootstrap .modal-dialog.coach-dialog wrapper");
    }

    [Fact]
    public void Resources_starter_overlay_keeps_its_scrim_edge_to_edge_and_insets_status_content()
    {
        Resources.Should().Contain(
            "class=\"resources-starter-overlay position-fixed top-0 start-0 w-100 h-100 d-flex align-items-center justify-content-center\"");

        var overlay = CssBlock(".resources-starter-overlay");
        overlay.Should().Contain("box-sizing: border-box");
        overlay.Should().Contain(
            "padding: env(safe-area-inset-top, 0px) env(safe-area-inset-right, 0px) env(safe-area-inset-bottom, 0px) env(safe-area-inset-left, 0px)");
    }

    [Fact]
    public void Shared_ingest_toast_owns_both_inline_insets_and_the_bottom_inset()
    {
        var block = CssBlock(".shared-ingest-toast");

        block.Should().Contain("left: calc(0.75rem + env(safe-area-inset-left, 0px))");
        block.Should().Contain("right: calc(0.75rem + env(safe-area-inset-right, 0px))");
        block.Should().Contain("bottom: calc(1rem + env(safe-area-inset-bottom, 0px))");
        block.Should().Contain("margin-inline: auto");
    }

    [Fact]
    public void Sync_overlay_owns_every_edge_it_touches()
    {
        CssBlock(".sync-overlay").Should().Contain(
            "padding: env(safe-area-inset-top, 0px) env(safe-area-inset-right, 0px) env(safe-area-inset-bottom, 0px) env(safe-area-inset-left, 0px)");
    }

    [Fact]
    public void Quiz_close_button_owns_the_top_and_right_edges_it_touches()
    {
        var block = CssBlock(".quiz-fullscreen-close-btn");
        block.Should().Contain("top: calc(env(safe-area-inset-top, 0px) + 1rem)");
        block.Should().Contain("right: calc(env(safe-area-inset-right, 0px) + 1rem)");
    }

    [Fact]
    public void Sam_fab_owns_the_bottom_and_right_edges_it_touches()
    {
        var block = CssBlock(".sam-fab");
        block.Should().Contain(
            "bottom: calc(1.5rem + env(safe-area-inset-bottom, 0px))");
        block.Should().Contain(
            "right: calc(1.5rem + env(safe-area-inset-right, 0px))");
    }

    [Fact]
    public void Edge_to_edge_layers_are_non_content_backdrops_or_have_safe_children()
    {
        FixedSurfaceInventory
            .Where(item => item.Value == FixedSurfaceDisposition.EdgeToEdgeBackdrop)
            .Select(item => item.Key)
            .Should().BeEquivalentTo(
                ".scene-gallery-backdrop",
                ".ref-menu-backdrop",
                ".sam-backdrop");

        var quizOverlay = CssBlock(".quiz-fullscreen-overlay");
        quizOverlay.Should().Contain("inset: 0");
        quizOverlay.Should().Contain("box-sizing: border-box");
        quizOverlay.Should().Contain("calc(1rem + env(safe-area-inset-top, 0px))");
        quizOverlay.Should().Contain("calc(1rem + env(safe-area-inset-right, 0px))");
        quizOverlay.Should().Contain("calc(1rem + env(safe-area-inset-bottom, 0px))");
        quizOverlay.Should().Contain("calc(1rem + env(safe-area-inset-left, 0px))");

        var quizImage = CssBlock(".quiz-fullscreen-image");
        quizImage.Should().Contain("width: 100%");
        quizImage.Should().Contain("height: 100%");
        quizImage.Should().NotContain("safe-area-inset",
            "the image must be centered in the overlay's already-inset usable rectangle");
    }

    [Fact]
    public void Every_full_viewport_centering_container_has_a_computed_geometry_contract()
    {
        FullViewportCenteringContainers().Should().BeEquivalentTo(
            CenteredViewportSurfaceInventory,
            options => options.WithStrictOrdering(),
            "new fixed flex/grid centering containers need an asymmetric-inset geometry case");
    }

    [Theory]
    [InlineData(874, 402, 62, 62, 20, 62)]
    [InlineData(874, 402, 0, 62, 20, 62)]
    [InlineData(402, 874, 62, 0, 34, 0)]
    public void Every_centered_child_stays_inside_the_usable_viewport(
        double viewportWidth,
        double viewportHeight,
        double safeTop,
        double safeRight,
        double safeBottom,
        double safeLeft)
    {
        var viewport = new Viewport(viewportWidth, viewportHeight);
        var insets = new Insets(safeTop, safeRight, safeBottom, safeLeft);

        foreach (var surface in CenteredViewportSurfaceInventory)
        {
            var child = surface switch
            {
                ".quiz-fullscreen-overlay" =>
                    CenteredChild(
                        viewport,
                        insets,
                        gutter: 16,
                        requestedWidth: double.MaxValue,
                        requestedHeight: double.MaxValue),
                ".coach-dialog-backdrop" =>
                    CenteredChild(
                        viewport,
                        insets,
                        gutter: 16,
                        requestedWidth: 448,
                        requestedHeight: double.MaxValue),
                ".sync-overlay" =>
                    CenteredChild(viewport, insets, gutter: 0, requestedWidth: 320, requestedHeight: 120),
                "razor:Pages/Resources.razor::.resources-starter-overlay[position-fixed]" =>
                    CenteredChild(viewport, insets, gutter: 0, requestedWidth: 320, requestedHeight: 120),
                _ => throw new InvalidOperationException($"No geometry contract for {surface}")
            };

            child.Left.Should().BeGreaterThanOrEqualTo(
                safeLeft, $"{surface} must start at or after the usable left edge");
            child.Right.Should().BeLessThanOrEqualTo(
                viewportWidth - safeRight, $"{surface} must end at or before the usable right edge");
            child.Top.Should().BeGreaterThanOrEqualTo(
                safeTop, $"{surface} must start at or after the usable top edge");
            child.Bottom.Should().BeLessThanOrEqualTo(
                viewportHeight - safeBottom, $"{surface} must end at or before the usable bottom edge");
        }
    }

    private static IReadOnlyList<string> FullViewportCenteringContainers()
    {
        var surfaces = new List<string>();
        var source = Regex.Replace(Css, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        foreach (Match match in Regex.Matches(source, @"(?ms)([^{}]+)\{([^{}]*)\}"))
        {
            var selector = Regex.Replace(match.Groups[1].Value.Trim(), @"\s+", " ");
            var body = match.Groups[2].Value;
            var isFixed = Regex.IsMatch(body, @"\bposition\s*:\s*fixed\s*;");
            var fillsViewport =
                Regex.IsMatch(body, @"\binset\s*:\s*0\s*;") ||
                (Regex.IsMatch(body, @"\bwidth\s*:\s*100vw\s*;") &&
                 Regex.IsMatch(body, @"\bheight\s*:\s*100vh\s*;"));
            var centers =
                Regex.IsMatch(body, @"\bdisplay\s*:\s*(?:flex|grid)\s*;") &&
                Regex.IsMatch(body, @"\balign-items\s*:\s*center\s*;") &&
                Regex.IsMatch(body, @"\bjustify-content\s*:\s*center\s*;");

            if (isFixed && fillsViewport && centers)
                surfaces.Add(selector);
        }

        var resourcesClasses = Regex.Match(
            Resources,
            @"class=""(?<classes>[^""]*\bresources-starter-overlay\b[^""]*)""")
            .Groups["classes"].Value
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (resourcesClasses.Contains("position-fixed") &&
            resourcesClasses.Contains("top-0") &&
            resourcesClasses.Contains("start-0") &&
            resourcesClasses.Contains("w-100") &&
            resourcesClasses.Contains("h-100") &&
            resourcesClasses.Contains("d-flex") &&
            resourcesClasses.Contains("align-items-center") &&
            resourcesClasses.Contains("justify-content-center"))
        {
            surfaces.Add(
                "razor:Pages/Resources.razor::.resources-starter-overlay[position-fixed]");
        }

        return surfaces;
    }

    private static Box CenteredChild(
        Viewport viewport,
        Insets insets,
        double gutter,
        double requestedWidth,
        double requestedHeight)
    {
        var contentLeft = insets.Left + gutter;
        var contentTop = insets.Top + gutter;
        var contentWidth = Math.Max(
            0,
            viewport.Width - insets.Left - insets.Right - (2 * gutter));
        var contentHeight = Math.Max(
            0,
            viewport.Height - insets.Top - insets.Bottom - (2 * gutter));
        var childWidth = Math.Min(requestedWidth, contentWidth);
        var childHeight = Math.Min(requestedHeight, contentHeight);

        return new Box(
            Left: contentLeft + ((contentWidth - childWidth) / 2),
            Top: contentTop + ((contentHeight - childHeight) / 2),
            Width: childWidth,
            Height: childHeight);
    }

    private static IReadOnlyList<string> FixedSurfacesInAppCssAndRazor()
    {
        var cssFiles = Directory
            .EnumerateFiles(
                UiRoot,
                "*.razor.css",
                SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Prepend(CssPath);

        var surfaces = new List<string>();
        foreach (var path in cssFiles)
        {
            var source = Regex.Replace(
                File.ReadAllText(path),
                @"/\*.*?\*/",
                string.Empty,
                RegexOptions.Singleline);

            surfaces.AddRange(
                Regex.Matches(
                        source,
                        @"(?ms)([^{}]+)\{([^{}]*)\bposition\s*:\s*fixed\s*;")
                    .Select(match => Regex.Replace(
                        match.Groups[1].Value.Trim(),
                        @"\s+",
                        " ")));
        }

        foreach (var path in Directory
                     .EnumerateFiles(UiRoot, "*.razor", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            var source = Regex.Replace(
                File.ReadAllText(path),
                @"@\*.*?\*@|<!--.*?-->",
                string.Empty,
                RegexOptions.Singleline);

            foreach (Match tag in Regex.Matches(
                        source,
                        @"<(?<tag>[a-zA-Z][\w:-]*)\b(?<attributes>(?:[^>""']|""[^""]*""|'[^']*')*)>"))
            {
                var attributes = tag.Groups["attributes"].Value;
                var classValue = AttributeValue(attributes, "class");
                var classes = classValue
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                var utilityMarkers = classes
                    .Where(FixedUtilityClasses.Contains)
                    .ToArray();
                var styleValue = AttributeValue(attributes, "style");
                var hasInlineFixedPosition = Regex.IsMatch(
                    styleValue,
                    @"(?:^|;)\s*position\s*:\s*fixed\s*(?:;|$)",
                    RegexOptions.IgnoreCase);

                if (utilityMarkers.Length == 0 && !hasInlineFixedPosition)
                    continue;

                var identifier = classes.Length > 0
                    ? $".{classes[0]}"
                    : $"<{tag.Groups["tag"].Value}>";
                var positionSources = utilityMarkers
                    .Concat(hasInlineFixedPosition ? ["inline-position-fixed"] : [])
                    .ToArray();
                var relativePath = Path
                    .GetRelativePath(UiRoot, path)
                    .Replace(Path.DirectorySeparatorChar, '/');

                surfaces.Add(
                    $"razor:{relativePath}::{identifier}[{string.Join('+', positionSources)}]");
            }
        }

        return surfaces;
    }

    private static string AttributeValue(string attributes, string name)
    {
        var match = Regex.Match(
            attributes,
            $@"\b{Regex.Escape(name)}\s*=\s*(?:""(?<double>[^""]*)""|'(?<single>[^']*)')",
            RegexOptions.IgnoreCase);

        return match.Groups["double"].Success
            ? match.Groups["double"].Value
            : match.Groups["single"].Value;
    }

    private static string CssMediaBlock(string mediaQuery)
    {
        var start = Css.IndexOf(mediaQuery, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"the stylesheet must define {mediaQuery}");

        var openingBrace = Css.IndexOf('{', start + mediaQuery.Length);
        openingBrace.Should().BeGreaterThan(start);
        return BalancedBlockContents(Css, openingBrace);
    }

    private static string BalancedBlockContents(string source, int openingBrace)
    {
        var depth = 0;
        for (var index = openingBrace; index < source.Length; index++)
        {
            switch (source[index])
            {
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                        return source[(openingBrace + 1)..index];
                    break;
            }
        }

        throw new InvalidOperationException("Unbalanced CSS block");
    }

    private static string CssBlock(string selector) =>
        CssBlocks(selector).First();

    private static IReadOnlyList<string> CssBlocks(string selector)
    {
        var pattern = @"(?m)^[ \t]*" + Regex.Escape(selector) + @"\s*\{([^}]*)\}";
        var matches = Regex.Matches(Css, pattern)
            .Select(match => match.Groups[1].Value)
            .ToArray();

        matches.Should().NotBeEmpty($"the stylesheet must define {selector}");
        return matches;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    private enum FixedSurfaceDisposition
    {
        OwnsInsets,
        EdgeToEdgeBackdrop,
        EdgeToEdgeWithSafeChildren
    }

    private enum SafeAreaEdge
    {
        Top,
        Right,
        Bottom,
        Left
    }

    private sealed record Viewport(double Width, double Height);

    private sealed record Insets(double Top, double Right, double Bottom, double Left);

    private sealed record Box(double Left, double Top, double Width, double Height)
    {
        public double Right => Left + Width;
        public double Bottom => Top + Height;
    }
}
