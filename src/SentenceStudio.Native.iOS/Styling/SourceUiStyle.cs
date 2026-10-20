using Microsoft.SwiftUI;
using System.Runtime.CompilerServices;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.Styling;

internal enum SourceTextTone
{
    Body, Secondary, Tertiary, Primary, OnPrimary, PlainCard, Success, Danger
}

internal enum SourceButtonRole
{
    Primary, FilledSecondary, OutlineSecondary, BootstrapLoginPrimary,
    BootstrapSuccessOutlineSmall
}

internal sealed class SourceUiStyle(SourceUiAppearance appearance, double viewportWidth)
{
    private sealed class TextRoleTag(
        SourceTextRole role, bool sourceHeading, bool strongElement)
    {
        internal SourceTextRole Role { get; } = role;
        internal bool SourceHeading { get; } = sourceHeading;
        internal bool StrongElement { get; } = strongElement;
    }

    private static readonly ConditionalWeakTable<SwiftView, TextRoleTag> TypedNodes = new();

    internal SourceUiAppearance Appearance => appearance;
    internal double ViewportWidth { get; set; } = viewportWidth;

    internal SwiftView Text(SwiftView view, SourceTextRole role,
        SourceTextTone tone = SourceTextTone.Body,
        bool sourceHeading = false, bool strongElement = false)
    {
        var metrics = SourceUiTypography.For(
            role, ViewportWidth, appearance.Selection.FontScale,
            appearance.Selection.ThemeId);
        var color = tone switch
        {
            SourceTextTone.Secondary => appearance.SecondaryText,
            SourceTextTone.Tertiary => appearance.TertiaryText,
            SourceTextTone.Primary => appearance.Primary,
            SourceTextTone.OnPrimary => appearance.OnPrimary,
            SourceTextTone.PlainCard => appearance.PlainCardText,
            SourceTextTone.Success => appearance.Success,
            SourceTextTone.Danger => appearance.Danger,
            _ => appearance.Text
        };
        TypedNodes.Remove(view);
        TypedNodes.Add(view, new TextRoleTag(role, sourceHeading, strongElement));
        return view.WithTypography(metrics.Size, metrics.Weight, metrics.LineHeight,
                appearance.FontPostScriptName(metrics.Weight,
                    sourceHeading, strongElement))
            .WithForegroundColor(color.Light, color.Dark);
    }

    internal static void RefreshTypography(
        SwiftView view, SourceUiAppearance appearance, double viewportWidth)
    {
        if (TypedNodes.TryGetValue(view, out var tag))
        {
            var metrics = SourceUiTypography.For(
                tag.Role, viewportWidth, appearance.Selection.FontScale,
                appearance.Selection.ThemeId);
            view.WithTypography(metrics.Size, metrics.Weight, metrics.LineHeight,
                appearance.FontPostScriptName(metrics.Weight,
                    tag.SourceHeading, tag.StrongElement));
        }
        foreach (var child in view.Children)
            RefreshTypography(child, appearance, viewportWidth);
    }

    internal SwiftView Page(SwiftView view) =>
        view.WithForegroundColor(appearance.Text.Light, appearance.Text.Dark)
            .WithBackgroundColor(appearance.BodyBackground.Light,
                appearance.BodyBackground.Dark)
            .WithTintColor(appearance.Primary.Light, appearance.Primary.Dark);

    internal SwiftView GroupedForm(SwiftView form) =>
        Page(form.WithNativeListColors(
            appearance.BodyBackground.Light, appearance.BodyBackground.Dark,
            appearance.CardSurface.Light, appearance.CardSurface.Dark));

    internal SwiftView ThemedList(SwiftView list) =>
        Page(list.WithNativeListColors(
            appearance.BodyBackground.Light, appearance.BodyBackground.Dark,
            appearance.ListRowSurface.Light, appearance.ListRowSurface.Dark));

    internal SwiftView Card(SwiftView view, bool elevated = false,
        double? maxWidth = null, double radius = 16)
    {
        var surface = elevated ? appearance.ElevatedSurface : appearance.CardSurface;
        view.WithFlexibleWidth(maxWidth,
                maxWidth is null ? SwiftHorizontalAlignment.Leading
                    : SwiftHorizontalAlignment.Center)
            .WithRoundedSurface(surface.Light, surface.Dark, radius);
        if (elevated)
            view.WithShadow("#000000", "#000000", 15, 0.15, 4);
        return view;
    }

    internal SwiftView BorderedCard(SwiftView view, double? maxWidth = null,
        double radius = 16) =>
        view.WithFlexibleWidth(maxWidth,
                maxWidth is null ? SwiftHorizontalAlignment.Leading
                    : SwiftHorizontalAlignment.Center)
            .WithRoundedSurface(appearance.ListRowSurface.Light,
                appearance.ListRowSurface.Dark, radius,
                appearance.Border.Light, appearance.Border.Dark, 1);

    internal SwiftView Alert(SwiftView text, bool success, bool compact = false)
    {
        var fill = success
            ? appearance.SuccessAlertFill : appearance.DangerAlertFill;
        var foreground = success
            ? appearance.SuccessAlertText : appearance.DangerAlertText;
        var border = success
            ? appearance.SuccessAlertBorder : appearance.DangerAlertBorder;
        var verticalPadding = compact ? 8 : 16;
        return text.WithForegroundColor(foreground.Light, foreground.Dark)
            .WithPadding(verticalPadding, 16, verticalPadding, 16)
            .WithFlexibleWidth()
            .WithRoundedSurface(fill.Light, fill.Dark, appearance.BootstrapRadius,
                border.Light, border.Dark, 1);
    }

    internal SwiftView InfoAlert(SwiftView content, bool compact = false)
    {
        var fill = appearance.InfoAlertFill;
        var foreground = appearance.InfoAlertText;
        var border = appearance.InfoAlertBorder;
        var verticalPadding = compact ? 8 : 16;
        return content.WithForegroundColor(foreground.Light, foreground.Dark)
            .WithPadding(verticalPadding, 16, verticalPadding, 16)
            .WithFlexibleWidth()
            .WithRoundedSurface(fill.Light, fill.Dark, appearance.BootstrapRadius,
                border.Light, border.Dark, 1);
    }

    internal SwiftView PrimaryButton(string title, Action action,
        bool fullWidth = false,
        SourceButtonGroupPosition groupPosition = SourceButtonGroupPosition.None) =>
        SourceButton(Views.Text(title), action, SourceButtonRole.Primary,
            fullWidth: fullWidth, groupPosition: groupPosition);

    internal SwiftView SecondaryButton(string title, Action action,
        bool fullWidth = false,
        SourceButtonGroupPosition groupPosition = SourceButtonGroupPosition.None) =>
        SourceButton(Views.Text(title), action, SourceButtonRole.FilledSecondary,
            fullWidth: fullWidth, groupPosition: groupPosition);

    internal SwiftView OutlineButton(SwiftView label, Action action,
        bool fullWidth = false,
        SourceButtonGroupPosition groupPosition = SourceButtonGroupPosition.None) =>
        SourceButton(label, action, SourceButtonRole.OutlineSecondary,
            fullWidth: fullWidth, verticalPadding: 6, horizontalPadding: 16,
            groupPosition: groupPosition);

    internal SwiftView QuizPrimaryButton(string title, string symbol, Action action)
    {
        var label = Views.HStack(Views.Text(title), Views.Image(symbol));
        label.Spacing = 4;
        return SourceButton(label, action, SourceButtonRole.Primary,
            horizontalPadding: 48);
    }

    internal SwiftView QuizPromptIconButton(
        string symbol, Action action, string name, string accessibilityIdentifier)
    {
        var fill = appearance.BootstrapOutlineSecondaryFill;
        var border = appearance.BootstrapButtonBorder;
        var foreground = appearance.SecondaryText;
        return Views.Button(Views.Image(symbol)
                .WithFixedSymbolSize(
                    SourceUiLayout.QuizPromptSymbolBaseSize * appearance.Selection.FontScale)
                .WithForegroundColor(foreground.Light, foreground.Dark), action)
            .WithButtonAppearance(fill?.Light, fill?.Dark,
                border.Light, border.Dark, SourceUiLayout.QuizPromptButtonDiameter / 2)
            .WithButtonBorderWidth(appearance.BootstrapBaseButtonBorderWidth)
            .WithButtonCircle(SourceUiLayout.QuizPromptButtonDiameter)
            .WithAccessibilityLabel(name)
            .WithAccessibilityIdentifier(accessibilityIdentifier);
    }

    internal SwiftView TextLinkButton(string title, string symbol, Action action,
        string accessibilityIdentifier)
    {
        var sourceClass = appearance.Selection.ThemeId is "brite" or "sketchy";
        var metrics = SourceUiTypography.For(SourceTextRole.Button,
            ViewportWidth, appearance.Selection.FontScale, appearance.Selection.ThemeId);
        var label = Views.HStack(
                Views.Image(symbol),
                Views.Text(title).WithUnderline(
                    appearance.Selection.ThemeId != "sketchy"))
            .WithTypography(metrics.Size, metrics.Weight, metrics.LineHeight,
                appearance.FontPostScriptName(metrics.Weight))
            .WithForegroundColor(appearance.TertiaryText.Light,
                appearance.TertiaryText.Dark)
            .WithPadding(6, 16, 6, 16)
            .WithMinimumHeight(SourceUiLayout.MinimumTouchHeight);
        if (!sourceClass)
            label.WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center);
        label.Spacing = 4;
        var button = Views.Button(label, action)
            .WithButtonStyle(SwiftButtonStyle.Plain)
            .WithMinimumHeight(SourceUiLayout.MinimumTouchHeight)
            .WithAccessibilityIdentifier(accessibilityIdentifier);
        if (appearance.Selection.ThemeId == "brite")
        {
            var border = appearance.BootstrapButtonBorder;
            button.WithButtonAppearance(null, null,
                    border.Light, border.Dark, appearance.BootstrapSmallButtonRadius)
                .WithButtonBorderWidth(appearance.BootstrapBaseButtonBorderWidth);
            WithBriteHardShadow(button);
        }
        else if (appearance.Selection.ThemeId == "sketchy")
        {
            button.WithButtonAppearance(null, null, null, null,
                    appearance.BootstrapSmallButtonRadius)
                .WithButtonBorderWidth(appearance.BootstrapBaseButtonBorderWidth);
            WithSketchyBootstrapCorners(button);
        }
        return sourceClass
            ? Views.HStack(button).WithFlexibleWidth(
                alignment: SwiftHorizontalAlignment.Center)
            : button.WithFlexibleWidth();
    }

    internal SwiftView ResourceViewModeButton(
        string symbol, string name, bool selected, Action action,
        SourceButtonGroupPosition groupPosition)
    {
        var label = Views.Image(symbol)
            .WithPadding(4,
                appearance.BootstrapSmallButtonHorizontalPadding(ViewportWidth),
                4, appearance.BootstrapSmallButtonHorizontalPadding(ViewportWidth))
            .WithMinimumHeight(SourceUiLayout.MinimumTouchHeight);
        var button = Views.Button(label, action)
            .WithButtonStyle(SwiftButtonStyle.Plain)
            .WithMinimumHeight(SourceUiLayout.MinimumTouchHeight)
            .WithAccessibilityLabel(name);
        button.Text = name;
        return UpdateResourceViewModeButton(button, selected, groupPosition);
    }

    internal SwiftView UpdateResourceViewModeButton(
        SwiftView button, bool selected, SourceButtonGroupPosition groupPosition)
    {
        SourceColorPair? fill = selected ? appearance.BootstrapSecondary
            : appearance.BootstrapOutlineSecondaryFill;
        var color = selected ? appearance.OnBootstrapSecondary : appearance.SecondaryText;
        var border = selected && appearance.Selection.ThemeId != "brite"
            ? appearance.BootstrapSecondary : appearance.BootstrapButtonBorder;
        button.Children[0].WithForegroundColor(color.Light, color.Dark);
        button.WithButtonAppearance(fill?.Light, fill?.Dark,
                border.Light, border.Dark, appearance.BootstrapSmallButtonRadius)
            .WithAccessibilitySelected(selected);
        if (appearance.BootstrapBaseButtonBorderWidth != 1)
            button.WithButtonBorderWidth(appearance.BootstrapBaseButtonBorderWidth);
        if (groupPosition == SourceButtonGroupPosition.None)
            WithSketchyBootstrapCorners(button);
        else
            WithGroupCorners(button, SourceButtonGroupClass.Small, groupPosition);
        if (selected)
            WithBriteHardShadow(button);
        else if (appearance.Selection.ThemeId == "brite")
            button.WithShadow("#000000", "#000000", 0, 0)
                .WithButtonShadowOffsetX(0);
        return button;
    }

    internal SwiftView FullWidthPrimaryButton(string label, Action action)
        => SourceButton(Views.Text(label), action, SourceButtonRole.Primary,
            fullWidth: true, verticalPadding: 6, horizontalPadding: 16);

    internal SwiftView BootstrapLoginButton(string label, Action action, bool enabled)
    {
        var button = SourceButton(Views.Text(label), action,
            SourceButtonRole.BootstrapLoginPrimary,
            fullWidth: true, verticalPadding: 6, horizontalPadding: 16);
        SetBootstrapLoginAvailability(button, appearance, enabled);
        return button;
    }

    internal static void SetBootstrapLoginAvailability(
        SwiftView button, SourceUiAppearance appearance, bool enabled)
    {
        button.IsEnabled = enabled;
        if (appearance.Selection.ThemeId is not ("brite" or "sketchy"))
            return;

        var fill = enabled
            ? appearance.BootstrapPrimaryFill : appearance.BootstrapPrimaryDisabledFill;
        var border = enabled
            ? appearance.BootstrapPrimaryBorder : appearance.BootstrapPrimaryDisabledBorder;
        button.WithButtonAppearance(fill.Light, fill.Dark,
            border.Light, border.Dark, appearance.BootstrapRadius, 0.65);
    }

    internal static void SetCustomButtonAvailability(
        SwiftView button, SourceUiAppearance appearance,
        SourceButtonRole role, bool enabled,
        SourceButtonGroupPosition groupPosition = SourceButtonGroupPosition.None)
    {
        if (role is not (SourceButtonRole.Primary or SourceButtonRole.FilledSecondary))
            throw new ArgumentOutOfRangeException(nameof(role));

        button.IsEnabled = enabled;
        // The source .btn-group .btn-ss-primary important declarations win over
        // .btn:disabled; ungrouped primary and every secondary do not have that override.
        var keepsGroupPrimary = !enabled && role == SourceButtonRole.Primary
            && groupPosition != SourceButtonGroupPosition.None;
        var usesEnabledColors = enabled || keepsGroupPrimary;
        // Ungrouped disabled custom buttons compute clear fill and currentColor
        // stroke/ink because the source never declares their disabled CSS variables.
        var fill = usesEnabledColors
            ? role == SourceButtonRole.Primary
                ? appearance.Primary : appearance.CardSurface
            : (SourceColorPair?)null;
        var border = usesEnabledColors
            ? role == SourceButtonRole.Primary
                ? appearance.Primary : appearance.BootstrapButtonBorder
            : appearance.Text;
        var foreground = usesEnabledColors
            ? role == SourceButtonRole.Primary
                ? appearance.OnPrimary : appearance.Text
            : appearance.Text;
        button.Children[0].WithForegroundColor(foreground.Light, foreground.Dark);
        button.WithButtonAppearance(fill?.Light, fill?.Dark,
            border.Light, border.Dark, appearance.BootstrapRadius, 0.65);
    }

    internal SwiftView SuccessOutlineSmallButton(SwiftView label, Action action)
        => SourceButton(label, action, SourceButtonRole.BootstrapSuccessOutlineSmall,
            verticalPadding: 4,
            horizontalPadding: appearance.BootstrapSmallButtonHorizontalPadding(ViewportWidth));

    internal SwiftView WithSketchyBootstrapCorners(SwiftView button)
    {
        if (appearance.Selection.ThemeId == "sketchy")
            button.WithButtonEllipticalCornerRadii(
                255, 25, 25, 225, 225, 25, 25, 255);
        return button;
    }

    private SwiftView WithGroupCorners(SwiftView button,
        SourceButtonGroupClass buttonClass, SourceButtonGroupPosition position)
    {
        var corners = appearance.GroupCorners(buttonClass, position);
        return button.WithButtonEllipticalCornerRadii(
            corners.TopLeftX, corners.TopLeftY, corners.TopRightX, corners.TopRightY,
            corners.BottomRightX, corners.BottomRightY,
            corners.BottomLeftX, corners.BottomLeftY);
    }

    private SwiftView WithBriteHardShadow(SwiftView button)
    {
        if (appearance.Selection.ThemeId == "brite")
            button.WithShadow("#000000", "#000000", 0, 1, 3)
                .WithButtonShadowOffsetX(3);
        return button;
    }

    private SwiftView SourceButton(SwiftView label, Action action,
        SourceButtonRole role, bool fullWidth = false, double verticalPadding = 10,
        double? horizontalPadding = null,
        SourceButtonGroupPosition groupPosition = SourceButtonGroupPosition.None)
    {
        SourceColorPair? fill = role switch
        {
            SourceButtonRole.Primary => appearance.Primary,
            SourceButtonRole.FilledSecondary => appearance.CardSurface,
            SourceButtonRole.BootstrapLoginPrimary => appearance.BootstrapPrimaryFill,
            SourceButtonRole.OutlineSecondary => appearance.BootstrapOutlineSecondaryFill,
            SourceButtonRole.BootstrapSuccessOutlineSmall =>
                appearance.BootstrapSuccessOutlineFill,
            _ => null
        };
        var border = role switch
        {
            SourceButtonRole.Primary => appearance.Primary,
            SourceButtonRole.BootstrapLoginPrimary => appearance.BootstrapPrimaryBorder,
            SourceButtonRole.BootstrapSuccessOutlineSmall =>
                appearance.BootstrapSuccessOutlineBorder,
            SourceButtonRole.FilledSecondary or SourceButtonRole.OutlineSecondary =>
                appearance.BootstrapButtonBorder,
            _ => appearance.Border
        };
        var foreground = role switch
        {
            SourceButtonRole.Primary => appearance.OnPrimary,
            SourceButtonRole.OutlineSecondary => appearance.SecondaryText,
            SourceButtonRole.BootstrapLoginPrimary =>
                appearance.BootstrapPrimaryForeground,
            SourceButtonRole.BootstrapSuccessOutlineSmall =>
                appearance.BootstrapSuccessOutlineForeground,
            _ => appearance.Text
        };
        var radius = role == SourceButtonRole.BootstrapSuccessOutlineSmall
            ? appearance.BootstrapSmallButtonRadius : appearance.BootstrapRadius;
        var metrics = SourceUiTypography.For(SourceTextRole.Button,
            ViewportWidth, appearance.Selection.FontScale, appearance.Selection.ThemeId);
        var paddingX = horizontalPadding
            ?? SourceUiAppearance.BootstrapCustomButtonHorizontalPadding(ViewportWidth);
        label.WithTypography(metrics.Size, metrics.Weight, metrics.LineHeight,
                appearance.FontPostScriptName(metrics.Weight))
            .WithForegroundColor(foreground.Light, foreground.Dark)
            .WithPadding(verticalPadding, paddingX, verticalPadding, paddingX)
            .WithMinimumHeight(SourceUiLayout.MinimumTouchHeight);
        if (fullWidth)
            label.WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center);
        var button = Views.Button(label, action)
            .WithButtonStyle(SwiftButtonStyle.Plain)
            .WithButtonAppearance(fill?.Light, fill?.Dark,
                border.Light, border.Dark, radius, 0.65)
            .WithMinimumHeight(SourceUiLayout.MinimumTouchHeight);
        var borderWidth = role == SourceButtonRole.BootstrapSuccessOutlineSmall
            ? appearance.BootstrapSuccessOutlineBorderWidth
            : appearance.BootstrapBaseButtonBorderWidth;
        if (borderWidth != 1)
            button.WithButtonBorderWidth(borderWidth);
        if (role is SourceButtonRole.OutlineSecondary
            or SourceButtonRole.BootstrapLoginPrimary
            or SourceButtonRole.BootstrapSuccessOutlineSmall)
            WithSketchyBootstrapCorners(button);
        if (groupPosition != SourceButtonGroupPosition.None)
            WithGroupCorners(button,
                role == SourceButtonRole.OutlineSecondary
                    ? SourceButtonGroupClass.Outline : SourceButtonGroupClass.Custom,
                groupPosition);
        if (role is SourceButtonRole.Primary
            or SourceButtonRole.FilledSecondary
            or SourceButtonRole.BootstrapLoginPrimary)
            WithBriteHardShadow(button);
        return fullWidth ? button.WithFlexibleWidth() : button;
    }

    internal SwiftView Input(SwiftView field)
    {
        var metrics = SourceUiTypography.For(
            SourceTextRole.Input, ViewportWidth, appearance.Selection.FontScale,
            appearance.Selection.ThemeId);
        TypedNodes.Remove(field);
        TypedNodes.Add(field, new TextRoleTag(SourceTextRole.Input,
            sourceHeading: false, strongElement: false));
        return field.WithTypography(metrics.Size, metrics.Weight, metrics.LineHeight,
                appearance.FontPostScriptName(metrics.Weight))
            .WithForegroundColor(appearance.Text.Light, appearance.Text.Dark)
            .WithPadding(8, 12, 8, 12)
            .WithRoundedSurface(appearance.BodyBackground.Light,
                appearance.BodyBackground.Dark, appearance.BootstrapRadius,
                appearance.Border.Light, appearance.Border.Dark, 1)
            .WithMinimumHeight(SourceUiLayout.MinimumTouchHeight);
    }

    internal SwiftView TextInput(SwiftView field) =>
        Input(field.WithPlaceholderColor(
            appearance.TertiaryText.Light, appearance.TertiaryText.Dark));

    internal SwiftView Progress(SwiftView progress, bool completion)
    {
        var tint = completion ? appearance.Success : appearance.Primary;
        return progress.WithTintColor(tint.Light, tint.Dark)
            .WithProgressTrackColor(appearance.ProgressTrack.Light,
                appearance.ProgressTrack.Dark)
            .WithFlexibleWidth();
    }
}
