using ReactorTheme.Styles;

namespace SentenceStudio.Resources.Styles;

partial class MyTheme : ApplicationTheme
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
    public new const string Secondary = nameof(Secondary);
    public const string Primary = nameof(Primary);
    public const string Danger = nameof(Danger);

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
            new Setter { Property = Syncfusion.Maui.Core.SfChipGroup.ChipTextColorProperty, Value = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground }
        }
    };

    // Today's Plan colors
    public static Color CardBackground => IsLightTheme ? Color.FromArgb("#FFFFFF") : Color.FromArgb("#1E1E1E");
    public static Color CardBorder => IsLightTheme ? Color.FromArgb("#E0E0E0") : Color.FromArgb("#3A3A3A");
    public static Color ItemBackground => IsLightTheme ? Color.FromArgb("#F5F5F5") : Color.FromArgb("#2A2A2A");
    public static Color ItemBorder => IsLightTheme ? Color.FromArgb("#D0D0D0") : Color.FromArgb("#404040");
    public static Color CompletedItemBackground => IsLightTheme ? Color.FromArgb("#E8F5E9") : Color.FromArgb("#1A3A1A");
    public static Color CompletedItemBorder => IsLightTheme ? Color.FromArgb("#A5D6A7") : Color.FromArgb("#2E5A2E");
    public static Color PriorityHighColor => Color.FromArgb("#EF5350");
    public static Color PriorityMediumColor => Color.FromArgb("#FF9800");
    public static Color PriorityLowColor => Color.FromArgb("#9E9E9E");
    public static Color BadgeBackground => IsLightTheme ? Color.FromArgb("#E0E0E0") : Color.FromArgb("#424242");
    public static Color BadgeText => IsLightTheme ? Color.FromArgb("#424242") : Color.FromArgb("#FFFFFF");
    public static Color StreakBadgeBackground => Color.FromArgb("#FF6F00");
    public static Color ProgressBarFill => Color.FromArgb("#66BB6A");
    public static Color PrimaryButtonBackground => Color.FromArgb("#1976D2");
    public static Color PrimaryButtonText => Colors.White;
    public static Color SecondaryButtonBackground => IsLightTheme ? Color.FromArgb("#F5F5F5") : Color.FromArgb("#424242");
    public static Color SecondaryButtonText => IsLightTheme ? Color.FromArgb("#424242") : Color.FromArgb("#FFFFFF");
    public static Color TertiaryButtonBackground => Colors.Transparent;
    public static Color TertiaryButtonText => Color.FromArgb("#1976D2");
    public static Color CheckboxColor => Color.FromArgb("#1976D2");
    public static Color PrimaryText => IsLightTheme ? Color.FromArgb("#212121") : Color.FromArgb("#FFFFFF");
    public static Color SecondaryText => IsLightTheme ? Color.FromArgb("#757575") : Color.FromArgb("#B0B0B0");
    public static Color AccentText => Color.FromArgb("#1976D2");

    protected override void OnApply()
    {
        ApplyStyles();
    }

    // Partial method implemented in ApplicationTheme.Styles.cs
    partial void ApplyStyles();
}
