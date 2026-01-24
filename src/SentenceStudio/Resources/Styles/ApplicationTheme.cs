
namespace SentenceStudio.Resources.Styles;

partial class MyTheme : Theme
{
    // Label theme styles
    public const string Title1 = nameof(Title1);
    public const string Title2 = nameof(Title2);
    public const string Title3 = nameof(Title3);
    public const string LargeTitle = nameof(LargeTitle);
    public const string Display = nameof(Display);
    public const string Headline = nameof(Headline);
    public const string SubHeadline = nameof(SubHeadline);
    public const string Body1 = nameof(Body1);
    public const string Body1Strong = nameof(Body1Strong);
    public const string Body2 = nameof(Body2);
    public const string Body2Strong = nameof(Body2Strong);
    public const string Caption1 = nameof(Caption1);
    public const string Caption1Strong = nameof(Caption1Strong);
    public const string Caption2 = nameof(Caption2);
    public const string Title = nameof(Title);
    public const string Subtitle = nameof(Subtitle);

    // Button theme styles
    public const string Secondary = nameof(Secondary);
    public const string PrimaryButton = nameof(PrimaryButton);
    public const string Danger = nameof(Danger);
    public const string Cta = nameof(Cta);  // Call-to-action buttons using AccentColor

    // Border theme styles
    public const string CardStyle = nameof(CardStyle);
    public const string InputWrapper = nameof(InputWrapper);

    // Layout theme styles
    public const string Surface1 = nameof(Surface1);

    // BoxView theme styles
    public const string ShimmerCustomViewStyle = nameof(ShimmerCustomViewStyle);

    // Size constants
    public static double SizeNone { get; } = 0;
    public static double Size20 { get; } = 2;
    public static double Size40 { get; } = 4;
    public static double Size60 { get; } = 6;
    public static double Size80 { get; } = 8;
    public static double Size100 { get; } = 10;
    public static double Size120 { get; } = 12;
    public static double Size160 { get; } = 16;
    public static double Size200 { get; } = 20;
    public static double Size240 { get; } = 24;
    public static double Size280 { get; } = 28;
    public static double Size320 { get; } = 32;
    public static double Size360 { get; } = 36;
    public static double Size400 { get; } = 40;
    public static double Size480 { get; } = 48;
    public static double Size520 { get; } = 52;
    public static double Size560 { get; } = 56;

    // Layout constants
    public static double IconSize { get; } = DeviceInfo.Idiom == DeviceIdiom.Desktop ? Size240 : Size200;
    public static double IconSizeSmall { get; } = DeviceInfo.Idiom == DeviceIdiom.Desktop ? Size160 : Size120;
    public static Thickness LayoutPadding { get; } = DeviceInfo.Idiom == DeviceIdiom.Desktop ? new Thickness(Size160) : new Thickness(Size120);
    public static double LayoutSpacing { get; } = DeviceInfo.Idiom == DeviceIdiom.Desktop ? Size160 : Size120;
    public static double ButtonMinimumSize { get; } = DeviceInfo.Idiom == DeviceIdiom.Desktop ? Size160 : Size120;

    // Content spacing constants
    public static double CardPadding { get; } = DeviceInfo.Idiom == DeviceIdiom.Desktop ? Size120 : Size80;
    public static double CardMargin { get; } = DeviceInfo.Idiom == DeviceIdiom.Desktop ? Size120 : Size80;
    public static double SectionSpacing { get; } = DeviceInfo.Idiom == DeviceIdiom.Desktop ? Size240 : Size200;
    public static double ComponentSpacing { get; } = Size80;
    public static double MicroSpacing { get; } = Size40;
    public static double GridSpacing { get; } = DeviceInfo.Idiom == DeviceIdiom.Desktop ? Size120 : Size80;

    // Special styles
    public static Style ChipStyle { get; } = new Style(typeof(Syncfusion.Maui.Core.SfChipGroup))
    {
        Setters = {
            new Setter { Property = Syncfusion.Maui.Core.SfChipGroup.ChipTextColorProperty, Value = TextPrimary }
        }
    };

    // Today's Plan colors - using semantic colors from the theme
    public static Color CardBackground => Surface;
    public static Color CardBorder => BorderColor;
    public static Color ItemBackground => SurfaceVariant;
    public static Color ItemBorder => BorderColor;
    public static Color CompletedItemBackground => IsLightTheme ? SuccessLight : SuccessDark;
    public static Color CompletedItemBorder => Success;
    public static Color PriorityHighColor => Error;
    public static Color PriorityMediumColor => Warning;
    public static Color PriorityLowColor => TextSecondary;
    public static Color BadgeBackground => SurfaceVariant;
    public static Color BadgeText => TextPrimary;
    public static Color StreakBadgeBackground => Warning;
    public static Color ProgressBarFill => Success;
    public static Color PrimaryButtonBackground => PrimaryColor;
    public static Color PrimaryButtonText => OnPrimary;
    public static Color SecondaryButtonBackground => SurfaceVariant;
    public static Color SecondaryButtonText => TextPrimary;
    public static Color TertiaryButtonBackground => Colors.Transparent;
    public static Color TertiaryButtonText => PrimaryColor;
    public static Color CheckboxColor => PrimaryColor;
    public static Color PrimaryText => TextPrimary;
    public static Color SecondaryText => TextSecondary;
    public static Color AccentText => PrimaryColor;

    protected override void OnApply()
    {
        ApplyStyles();
    }

    // Partial method implemented in ApplicationTheme.Styles.cs
    partial void ApplyStyles();
}
