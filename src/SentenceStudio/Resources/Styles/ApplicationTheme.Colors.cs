using MauiReactor.Shapes;

namespace SentenceStudio.Resources.Styles;

partial class MyTheme
{
    // public static Color Primary { get; } = Color.FromRgba(81, 43, 212, 255); // #512BD4
    public static Color PrimaryDark { get; } = Color.FromRgba(172, 153, 234, 255); // #AC99EA
    public static Color PrimaryDarkText { get; } = Color.FromRgba(36, 36, 36, 255); // #242424
    // public static Color Secondary { get; } = Color.FromRgba(223, 216, 247, 255); // #DFD8F7
    public static Color SecondaryDarkText { get; } = Color.FromRgba(152, 128, 229, 255); // #9880E5
    public static Color Tertiary { get; } = Color.FromRgba(43, 11, 152, 255); // #2B0B98

    // Status colors for vocabulary quiz and other feedback
    public static Color Success { get; } = Color.FromRgba(34, 197, 94, 255); // #22C55E
    public static Color Error { get; } = Color.FromRgba(239, 68, 68, 255); // #EF4444
    public static Color Warning { get; } = Color.FromRgba(245, 158, 11, 255); // #F59E0B

    public static Color White { get; } = Colors.White; // #FFFFFF
    public static Color Black { get; } = Colors.Black; // #000000
    public static Color Magenta { get; } = Color.FromRgba(214, 0, 170, 255); // #D600AA
    public static Color MidnightBlue { get; } = Color.FromRgba(25, 6, 73, 255); // #190649
    public static Color OffBlack { get; } = Color.FromRgba(31, 31, 31, 255); // #1F1F1F
    public static Color OffWhite { get; } = Color.FromRgba(241, 241, 241, 255); // #F1F1F1

    public static Color Gray100 { get; } = Color.FromRgba(225, 225, 225, 255); // #E1E1E1
    public static Color Gray200 { get; } = Color.FromRgba(200, 200, 200, 255); // #C8C8C8
    public static Color Gray300 { get; } = Color.FromRgba(172, 172, 172, 255); // #ACACAC
    public static Color Gray400 { get; } = Color.FromRgba(145, 145, 145, 255); // #919191
    public static Color Gray500 { get; } = Color.FromRgba(110, 110, 110, 255); // #6E6E6E
    public static Color Gray600 { get; } = Color.FromRgba(64, 64, 64, 255); // #404040
    public static Color Gray900 { get; } = Color.FromRgba(33, 33, 33, 255); // #212121
    public static Color Gray950 { get; } = Color.FromRgba(20, 20, 20, 255); // #141414

    public static Color DarkBackground { get; } = Color.FromRgba(23, 23, 26, 255); // #17171a
    public static Color LightOnDarkBackground { get; } = Color.FromRgba(195, 195, 195, 255); // #C3C3C3
    public static Color LightBackground { get; } = Color.FromRgba(242, 242, 242, 255); // #F2F2F2
    public static Color DarkOnLightBackground { get; } = Color.FromRgba(13, 13, 13, 255); // #0D0D0D
    public static Color LightSecondaryBackground { get; } = Color.FromRgba(224, 224, 224, 255); // #E0E0E0
    public static Color DarkSecondaryBackground { get; } = Color.FromRgba(34, 34, 40, 255); // #222228

    public static Brush PrimaryBrush { get; } = new SolidColorBrush(HighlightDarkest);
    public static Brush SecondaryBrush { get; } = new SolidColorBrush(HighlightMedium);
    public static Brush TertiaryBrush { get; } = new SolidColorBrush(Tertiary);
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
    public static Brush LightCardBackgroundBrush { get; } = new SolidColorBrush(LightSecondaryBackground);
    public static Brush DarkCardBackgroundBrush { get; } = new SolidColorBrush(DarkSecondaryBackground);
}
