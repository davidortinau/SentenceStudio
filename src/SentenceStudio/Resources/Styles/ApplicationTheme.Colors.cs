using MauiReactor.Shapes;

namespace SentenceStudio.Resources.Styles;

partial class MyTheme
{
    // ==========================================
    // SEMANTIC COLORS - Seoul Pop (Obangsaek Modern)
    // ==========================================
    public static Color Success { get; } = Color.FromArgb("#12B886");   // Jade green (녹)
    public static Color SuccessLight { get; } = Color.FromArgb("#E8F9F3");
    public static Color SuccessDark { get; } = Color.FromArgb("#0A6C4E");

    public static Color Warning { get; } = Color.FromArgb("#FFC233");   // Hwang / 황 (golden yellow)
    public static Color Error { get; } = Color.FromArgb("#E13B30");     // Hong / 홍 (Korean red)
    public static Color Info { get; } = Color.FromArgb("#1E4DFF");      // Cheong / 청 (cobalt blue)




    /// <summary>
    /// Light mode: Seoul Pop - Cool neutrals with bold Korean-inspired accents (Obangsaek Modern)
    /// </summary>
    public static class Light
    {
        public static Color Correct { get; } = Success;
        public static Color Incorrect { get; } = Error;
        public static Color Selected { get; } = Color.FromArgb("#1E4DFF"); // Primary highlight (Cheong)

        // Backgrounds & Surfaces (clean, modern with cool blue tint)
        public static Color Background { get; } = Color.FromArgb("#F7F8FF");       // cool "paper" with tiny blue tint
        public static Color Surface { get; } = Color.FromArgb("#FFFFFF");
        public static Color SurfaceVariant { get; } = Color.FromArgb("#EEF1FF");   // very light indigo-tinted gray
        public static Color SurfaceElevated { get; } = Color.FromArgb("#FCFDFF");

        // Brand & Accent (bold, Korean-coded)
        public static Color Primary { get; } = Color.FromArgb("#1E4DFF");          // Cheong / 청 (cobalt blue)
        public static Color OnPrimary { get; } = Color.FromArgb("#FFFFFF");
        public static Color Secondary { get; } = Color.FromArgb("#12B886");        // Jade / 녹 (jade green)

        // CTA Accent (warm dancheong coral)
        public static Color Accent { get; } = Color.FromArgb("#FF6A3D");           // Dancheong coral
        public static Color OnAccent { get; } = Color.FromArgb("#FFFFFF");

        // Typography (ink-based)
        public static Color TextPrimary { get; } = Color.FromArgb("#0B0D12");      // ink
        public static Color TextSecondary { get; } = Color.FromArgb("#394150");
        public static Color TextMuted { get; } = Color.FromArgb("#7A8699");

        // Borders & Dividers
        public static Color Outline { get; } = Color.FromArgb("#D6DAF0");
    }


    /// <summary>
    /// Dark mode: Seoul Pop - Deep indigo/ink with bold Korean-inspired accents
    /// </summary>
    public static class Dark
    {
        // Backgrounds & Surfaces (deep indigo/ink)
        public static Color Background { get; } = Color.FromArgb("#060913");
        public static Color Surface { get; } = Color.FromArgb("#0C1222");
        public static Color SurfaceVariant { get; } = Color.FromArgb("#121B33");
        public static Color SurfaceElevated { get; } = Color.FromArgb("#1A2545");

        // Brand & Accent
        public static Color Primary { get; } = Color.FromArgb("#6B8CFF");          // Lighter cobalt for dark UI
        public static Color OnPrimary { get; } = Color.FromArgb("#081022");
        public static Color Secondary { get; } = Color.FromArgb("#21D6A4");        // Brighter jade for dark UI

        // CTA Accent
        public static Color Accent { get; } = Color.FromArgb("#FF7A4D");           // Brighter coral for dark
        public static Color OnAccent { get; } = Color.FromArgb("#0B0D12");

        // Typography
        public static Color TextPrimary { get; } = Color.FromArgb("#F8FAFF");
        public static Color TextSecondary { get; } = Color.FromArgb("#C6D0E7");
        public static Color TextMuted { get; } = Color.FromArgb("#93A3C7");

        // Borders & Dividers
        public static Color Outline { get; } = Color.FromArgb("#263457");
    }


    // ==========================================
    // THEME-AWARE CONVENIENCE PROPERTIES
    // These automatically return the correct color based on current theme
    // ==========================================

    // Backgrounds & Surfaces
    public static Color Background => IsLightTheme ? Light.Background : Dark.Background;
    public static Color Surface => IsLightTheme ? Light.Surface : Dark.Surface;
    public static Color SurfaceVariant => IsLightTheme ? Light.SurfaceVariant : Dark.SurfaceVariant;
    public static Color SurfaceElevated => IsLightTheme ? Light.SurfaceElevated : Dark.SurfaceElevated;

    // Brand & Accent (renamed to avoid conflict with const string Primary)
    public static Color PrimaryColor => IsLightTheme ? Light.Primary : Dark.Primary;
    public static Color OnPrimary => IsLightTheme ? Light.OnPrimary : Dark.OnPrimary;
    public static Color SecondaryColor => IsLightTheme ? Light.Secondary : Dark.Secondary;

    // Typography
    public static Color TextPrimary => IsLightTheme ? Light.TextPrimary : Dark.TextPrimary;
    public static Color TextSecondary => IsLightTheme ? Light.TextSecondary : Dark.TextSecondary;
    public static Color TextMuted => IsLightTheme ? Light.TextMuted : Dark.TextMuted;

    // Borders & Dividers (renamed to avoid conflict with Border control type)
    public static Color Outline => IsLightTheme ? Light.Outline : Dark.Outline;
    public static Color BorderColor => Outline;
    public static Color BorderLight => IsLightTheme ? Colors.LightGray : Colors.DimGray;

    public static Color AccentColor => IsLightTheme ? Light.Accent : Dark.Accent;
    public static Color OnAccent => IsLightTheme ? Light.OnAccent : Dark.OnAccent;
    public static Brush AccentBrush => new SolidColorBrush(AccentColor);


    // ==========================================
    // CHART & VISUALIZATION COLORS
    // ==========================================
    public static Color ChartKnown => Success;
    public static Color ChartReview => Warning;
    public static Color ChartLearning => Info;
    public static Color ChartNew => IsLightTheme ? Gray400 : Gray500;

    // Progress/Proficiency colors (for progress bars, skill levels)
    public static Color ProficiencyHigh => Success;
    public static Color ProficiencyMedium => Warning;
    public static Color ProficiencyLow => Color.FromArgb("#FF8C00"); // DarkOrange
    public static Color ProficiencyVeryLow => Error;

    // Audio waveform colors
    public static Color WaveformDefault => IsLightTheme ? Colors.DarkBlue.WithAlpha(0.6f) : Colors.SkyBlue.WithAlpha(0.6f);
    public static Color WaveformPlayed => IsLightTheme ? Colors.Orange : Colors.OrangeRed;

    // ==========================================
    // BRUSHES (theme-aware)
    // ==========================================
    public static Brush PrimaryBrush => new SolidColorBrush(PrimaryColor);
    public static Brush SurfaceBrush => new SolidColorBrush(Surface);
    public static Brush SurfaceVariantBrush => new SolidColorBrush(SurfaceVariant);
    public static Brush SurfaceElevatedBrush => new SolidColorBrush(SurfaceElevated);
    public static Brush BackgroundBrush => new SolidColorBrush(Background);

    // ==========================================
    // LEGACY COLORS (kept for backward compatibility during migration)
    // These will be gradually replaced with semantic colors above
    // ==========================================

    // Gray scale (neutral colors)
    public static Color Gray100 { get; } = Color.FromRgba(225, 225, 225, 255); // #E1E1E1
    public static Color Gray200 { get; } = Color.FromRgba(200, 200, 200, 255); // #C8C8C8
    public static Color Gray300 { get; } = Color.FromRgba(172, 172, 172, 255); // #ACACAC
    public static Color Gray400 { get; } = Color.FromRgba(145, 145, 145, 255); // #919191
    public static Color Gray500 { get; } = Color.FromRgba(110, 110, 110, 255); // #6E6E6E
    public static Color Gray600 { get; } = Color.FromRgba(64, 64, 64, 255); // #404040
    public static Color Gray900 { get; } = Color.FromRgba(33, 33, 33, 255); // #212121
    public static Color Gray950 { get; } = Color.FromRgba(20, 20, 20, 255); // #141414

    // Basic colors
    public static Color White { get; } = Colors.White;
    public static Color Black { get; } = Colors.Black;
    public static Color OffBlack { get; } = Color.FromRgba(31, 31, 31, 255); // #1F1F1F
    public static Color OffWhite { get; } = Color.FromRgba(241, 241, 241, 255); // #F1F1F1

    // Legacy named colors (mapped to new semantic colors)
    public static Color DarkBackground => Dark.Background;
    public static Color LightBackground => Light.Background;
    public static Color LightOnDarkBackground => Dark.TextPrimary;
    public static Color DarkOnLightBackground => Light.TextPrimary;
    public static Color LightSecondaryBackground => Light.SurfaceVariant;
    public static Color DarkSecondaryBackground => Dark.SurfaceVariant;

    // Legacy accent colors (mapped to Primary)
    public static Color HighlightDarkest => PrimaryColor;
    public static Color HighlightMedium => IsLightTheme ? Light.SurfaceVariant : Dark.SurfaceVariant;
    public static Color HighlightLightest => IsLightTheme ? Light.SurfaceElevated : Dark.SurfaceElevated;

    // Legacy purple-based colors (deprecated - mapped to coffee palette)
    public static Color PrimaryDark => Dark.Primary;
    public static Color PrimaryDarkText { get; } = Color.FromRgba(36, 36, 36, 255); // #242424
    public static Color SecondaryDarkText => Dark.TextSecondary;
    public static Color Tertiary => PrimaryColor;
    public static Color Magenta { get; } = Color.FromRgba(214, 0, 170, 255); // #D600AA (kept for accent)
    public static Color MidnightBlue { get; } = Color.FromRgba(25, 6, 73, 255); // #190649 (kept for specific uses)

    // Legacy brushes
    public static Brush SecondaryBrush => SurfaceVariantBrush;
    public static Brush TertiaryBrush => PrimaryBrush;
    public static Brush WhiteBrush { get; } = new SolidColorBrush(White);
    public static Brush BlackBrush { get; } = new SolidColorBrush(Black);
    public static Brush Gray100Brush { get; } = new SolidColorBrush(Gray100);
    public static Brush Gray200Brush { get; } = new SolidColorBrush(Gray200);
    public static Brush Gray300Brush { get; } = new SolidColorBrush(Gray300);
    public static Brush Gray400Brush { get; } = new SolidColorBrush(Gray400);
    public static Brush Gray500Brush { get; } = new SolidColorBrush(Gray500);
    public static Brush Gray600Brush { get; } = new SolidColorBrush(Gray600);
    public static Brush Gray900Brush { get; } = new SolidColorBrush(Gray900);
    public static Brush Gray950Brush { get; } = new SolidColorBrush(Gray950);
    public static Brush LightCardBackgroundBrush => new SolidColorBrush(Light.SurfaceVariant);
    public static Brush DarkCardBackgroundBrush => new SolidColorBrush(Dark.SurfaceVariant);
}
