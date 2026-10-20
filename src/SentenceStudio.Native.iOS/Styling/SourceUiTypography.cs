namespace SentenceStudio.Native.iOS.Styling;

internal enum SourceTextRole
{
    Brand,
    LoginHeading,
    StatNumber,
    Badge,
    BadgeSmall,
    Display,
    LargeTitle,
    Title1,
    Title2,
    Title3,
    Body1,
    Body1Strong,
    Body1Bold,
    Body2,
    Body2Strong,
    Small,
    SmallStrong,
    ListTitle,
    Caption1,
    Caption2,
    Button,
    DefaultBody,
    FormLabel,
    Input
}

internal readonly record struct SourceTextMetrics(double Size, int Weight, double LineHeight);

internal static class SourceUiTypography
{
    // app.css:437-527,790-805,1043-1071. The last <767.98px .ss-display
    // rule is a fixed 1.5rem and does not include --ss-font-scale.
    internal static SourceTextMetrics For(
        SourceTextRole role, double viewportWidth, double fontScale,
        string? themeId = null)
    {
        var mobile = viewportWidth <= 768;
        var scale = fontScale;
        SourceTextMetrics metrics = role switch
        {
            SourceTextRole.Brand => new(20.8 + Math.Min(viewportWidth, 1200) * 0.006,
                700, 1.2),
            SourceTextRole.LoginHeading =>
                new(mobile ? 21.2 + viewportWidth * 0.009 : 32, 500, 1.2),
            SourceTextRole.StatNumber => new(20.8 + Math.Min(viewportWidth, 1200) * 0.006,
                700, 1.5),
            // The SwiftUI line box at 1.0 clips the streak badge's descenders.
            SourceTextRole.Badge => new(16, 700, 1.2),
            SourceTextRole.BadgeSmall => new(12, 700, 1),
            SourceTextRole.Display => viewportWidth < 767.98
                ? new(24, 700, 1.17)
                : new((mobile ? 42 : 60) * scale, 700, 1.17),
            SourceTextRole.LargeTitle =>
                new((mobile ? 28 : 34) * scale, 700, 1.21),
            SourceTextRole.Title1 =>
                new((mobile ? 24 : 28) * scale, 500, 1.21),
            SourceTextRole.Title2 => new(22 * scale, 600, 1.27),
            SourceTextRole.Title3 => new(20 * scale, 600, 1.25),
            SourceTextRole.Body1 => new(17 * scale, 400, 1.29),
            SourceTextRole.Body1Strong => new(17 * scale, 600, 1.29),
            SourceTextRole.Body1Bold => new(17 * scale, 700, 1.29),
            SourceTextRole.Body2 => new(15 * scale, 400, 1.33),
            SourceTextRole.Body2Strong => new(15 * scale, 600, 1.33),
            SourceTextRole.Small => new(14, 400, 1.5),
            SourceTextRole.SmallStrong => new(14, 600, 1.5),
            SourceTextRole.ListTitle => new(16, 600, 1.5),
            SourceTextRole.Caption1 => new(13 * scale, 400, 1.38),
            SourceTextRole.Caption2 => new(12 * scale, 400, 1.33),
            SourceTextRole.Button => new(14 * scale, 400, 1.5),
            SourceTextRole.DefaultBody => new(16, 400, 1.5),
            SourceTextRole.FormLabel => new(16, 400, 1.5),
            SourceTextRole.Input => new((mobile ? 16 : 14) * scale, 400, 1.5),
            _ => throw new ArgumentOutOfRangeException(nameof(role))
        };
        return themeId == "sketchy"
            && role is (SourceTextRole.Body1 or SourceTextRole.Body2
                or SourceTextRole.Small or SourceTextRole.Caption1
                or SourceTextRole.Caption2 or SourceTextRole.DefaultBody
                or SourceTextRole.FormLabel
                or SourceTextRole.Input)
            ? metrics with { Weight = 700 }
            : metrics;
    }
}
