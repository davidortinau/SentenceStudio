using MauiReactor.Shapes;
using Fonts;
using OpenAI.VectorStores;

namespace SentenceStudio.Resources.Styles;

class ApplicationTheme : Theme
{
    public const string Title1 = nameof(Title1);
    public const string Title3 = nameof(Title3);
    public const string Caption1 = nameof(Caption1);
    public const string Body1 = nameof(Body1);
    public const string InputWrapper = nameof(InputWrapper);

    public static Color Primary { get; } = Color.FromRgba(81, 43, 212, 255); // #512BD4
    public static Color PrimaryDark { get; } = Color.FromRgba(172, 153, 234, 255); // #AC99EA
    public static Color PrimaryDarkText { get; } = Color.FromRgba(36, 36, 36, 255); // #242424
    public static Color Secondary { get; } = Color.FromRgba(223, 216, 247, 255); // #DFD8F7
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

    public static Brush PrimaryBrush { get; } = new SolidColorBrush(Primary);
    public static Brush SecondaryBrush { get; } = new SolidColorBrush(Secondary);
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

    public static double IconSize { get; } = DeviceInfo.Platform == DevicePlatform.WinUI ? 32 : 20;
    public static double IconSizeSmall { get; } = DeviceInfo.Platform == DevicePlatform.WinUI ? 18 : 12;
    public static Thickness LayoutPadding { get; } = DeviceInfo.Platform == DevicePlatform.WinUI ? new Thickness(30) : new Thickness(15);
    public static double LayoutSpacing { get; } = DeviceInfo.Platform == DevicePlatform.WinUI ? 15 : 5;
    public static double ButtonMinimumSize { get; } = DeviceInfo.Platform == DevicePlatform.WinUI ? 60 : 44;

    public static Style ChipStyle { get; } = new Style(typeof(Syncfusion.Maui.Core.SfChipGroup))
    {
        Setters = {
            new Setter { Property = Syncfusion.Maui.Core.SfChipGroup.ChipTextColorProperty, Value = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground }
        }
    };

    public static FontImageSource IconFontDecrease { get; } = new FontImageSource
    {
        Glyph = FluentUI.font_decrease_24_regular,
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = IconSize
    };

    public static FontImageSource IconFontIncrease{ get; } = new FontImageSource
    {
        Glyph = FluentUI.font_increase_24_regular, 
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = IconSize
    };

    public static FontImageSource IconDashboard { get; } = new FontImageSource
    {
        Glyph = FluentUI.diagram_24_regular,
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = IconSize
    };

    public static FontImageSource IconProjects { get; } = new FontImageSource
    {
        Glyph = FluentUI.list_24_regular, 
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = IconSize
    };

    public static FontImageSource IconMeta { get; } = new FontImageSource
    {
        Glyph = FluentUI.info_24_regular, 
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = IconSize
    };

    public static FontImageSource IconRibbon { get; } = new FontImageSource
    {
        Glyph = FluentUI.ribbon_20_regular, 
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = IconSize
    };

    public static FontImageSource IconAdd { get; } = new FontImageSource
    {
        Glyph = FluentUI.add_32_regular, 
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? White : Black,
        Size = IconSize
    };

    public static FontImageSource IconDelete { get; } = new FontImageSource
    {
        Glyph = FluentUI.delete_32_regular, 
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = IconSize
    };

    public static FontImageSource IconClean { get; } = new FontImageSource
    {
        Glyph = FluentUI.broom_32_regular, 
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = Size240
    };

    public static FontImageSource IconLight { get; } = new FontImageSource
    {
        Glyph = FluentUI.weather_sunny_28_regular, 
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = Size240
    };

    public static FontImageSource IconDark { get; } = new FontImageSource
    {
        Glyph = FluentUI.weather_moon_28_regular, 
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = Size240
    };

    public static FontImageSource IconSpeedVerySlow { get; } = new FontImageSource
    {
        Glyph = FluentUI.animal_turtle_24_regular,
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = IconSize
    };

    public static FontImageSource IconSpeedSlow { get; } = new FontImageSource
    {
        Glyph = FluentUI.animal_rabbit_24_regular,
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = IconSize
    };

    public static FontImageSource IconSpeedNormal { get; } = new FontImageSource
    {
        Glyph = FluentUI.animal_cat_24_regular,
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = IconSize
    };

    public static FontImageSource IconPlay { get; } = new FontImageSource
    {
        Glyph = FluentUI.play_32_regular, 
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = Size280
    };

    public static FontImageSource IconPause { get; } = new FontImageSource
    {
        Glyph = FluentUI.pause_32_regular, 
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = Size280
    };

    public static FontImageSource IconStop { get; } = new FontImageSource
    {
        Glyph = FluentUI.record_stop_32_regular, 
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = Size280
    };

    public static FontImageSource IconRewind { get; } = new FontImageSource
    {
        Glyph = FluentUI.skip_back_10_32_regular, 
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = Size280
    };
    
    public static FontImageSource IconPreviousSm { get; } = new FontImageSource
    {
        Glyph = FluentUI.previous_32_regular, 
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = Size280
    };

    public static FontImageSource IconNextSm { get; } = new FontImageSource
    {
        Glyph = FluentUI.next_32_regular, 
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = Size280
    };

    public static FontImageSource IconPlaySm { get; } = new FontImageSource
    {
        Glyph = FluentUI.play_20_regular,
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = Size200
    };

    public static FontImageSource IconPauseSm { get; } = new FontImageSource
    {
        Glyph = FluentUI.pause_20_regular, 
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = Size200
    };

    public static FontImageSource IconStopSm { get; } = new FontImageSource
    {
        Glyph = FluentUI.stop_20_regular, 
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = Size200
    };

    public static FontImageSource IconRewindSm { get; } = new FontImageSource
    {
        Glyph = FluentUI.previous_20_regular, 
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = Size200
    };

    public static FontImageSource IconNext { get; } = new FontImageSource
    {
        Glyph = FluentUI.next_48_regular, 
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = IconSize
    };

    public static FontImageSource IconPrevious { get; } = new FontImageSource
    {
        Glyph = FluentUI.previous_48_regular, 
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = IconSize
    };

    public static FontImageSource IconKeyboard { get; } = new FontImageSource
    {
        Glyph = FluentUI.keyboard_24_regular, 
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = IconSize
    };

    public static FontImageSource IconMultiSelect { get; } = new FontImageSource
    {
        Glyph = FluentUI.multiselect_ltr_24_regular, 
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = IconSize
    };

    public static FontImageSource IconDictionary { get; } = new FontImageSource
    {
        Glyph = FluentUI.book_24_regular, 
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = IconSize
    };

    public static FontImageSource IconSave { get; } = new FontImageSource
    {
        Glyph = FluentUI.save_24_regular, 
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = IconSize
    };

    public static FontImageSource IconShare { get; } = new FontImageSource
    {
        Glyph = FluentUI.share_24_regular, 
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = IconSize
    };

    public static FontImageSource IconCopy { get; } = new FontImageSource
    {
        Glyph = FluentUI.copy_24_regular, 
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = IconSize
    };

    public static FontImageSource IconInfo { get; } = new FontImageSource
    {
        Glyph = FluentUI.info_24_regular, 
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = IconSize
    };

    public static FontImageSource IconCircleCheckmark { get; } = new FontImageSource
    {
        Glyph = FluentUI.checkmark_circle_16_regular, 
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = IconSize
    };

    public static FontImageSource IconCancel { get; } = new FontImageSource
    {
        Glyph = FluentUI.calendar_cancel_16_regular, 
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = IconSize
    };

    public static FontImageSource IconImageExport { get; } = new FontImageSource
    {
        Glyph = FluentUI.arrow_export_ltr_24_regular, 
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = IconSize
    };

    public static FontImageSource IconSwitch { get; } = new FontImageSource
    {
        Glyph = FluentUI.arrow_turn_down_right_20_regular, 
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = IconSize
    };

    public static FontImageSource IconTranslate { get; } = new FontImageSource
    {
        Glyph = FluentUI.translate_24_regular, 
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = IconSize
    };

    public static FontImageSource IconErase { get; } = new FontImageSource
    {
        Glyph = FluentUI.eraser_24_regular, 
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = IconSize
    };

    public static FontImageSource IconCheckbox { get; } = new FontImageSource
    {
        Glyph = FluentUI.checkbox_2_24_regular, 
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = IconSize
    };
    public static FontImageSource IconCheckboxSelected { get; } = new FontImageSource
    {
        Glyph = FluentUI.checkbox_checked_24_regular, 
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = IconSize
    };

    public static FontImageSource IconSearch { get; } = new FontImageSource
    {
        Glyph = FluentUI.search_48_regular, 
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = IconSize
    };

    public static FontImageSource IconMore { get; } = new FontImageSource
    {
        Glyph = FluentUI.more_horizontal_48_regular, 
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = IconSize
    };

    public static FontImageSource IconFileExplorer { get; } = new FontImageSource
    {
        Glyph = FluentUI.folder_search_24_regular, 
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = IconSize
    };

    public static FontImageSource IconEdit { get; } = new FontImageSource
    {
        Glyph = FluentUI.edit_48_regular, 
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = IconSize
    };

    public static FontImageSource IconStatus { get; } = new FontImageSource
    {
        Glyph = FluentUI.status_48_regular, 
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = IconSize
    };

    public static FontImageSource IconVideo { get; } = new FontImageSource
    {
        Glyph = FluentUI.video_32_regular, 
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = IconSize
    };

    public static FontImageSource IconPodcast { get; } = new FontImageSource
    {
        Glyph = FluentUI.mic_48_regular, 
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = IconSize
    };

    public static FontImageSource IconImage { get; } = new FontImageSource
    {
        Glyph = FluentUI.image_48_regular, 
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = IconSize
    };

    public static FontImageSource IconVocabList { get; } = new FontImageSource
    {
        Glyph = FluentUI.list_28_regular, 
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = IconSize
    };

    public static FontImageSource IconArticle { get; } = new FontImageSource
    {
        Glyph = FluentUI.document_100_24_regular, 
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = IconSize
    };

    protected override void OnApply()
    {
        SfTextInputLayoutStyles.Default = _ => _
            .ContainerType(Syncfusion.Maui.Toolkit.TextInputLayout.ContainerType.Filled)
            .OutlineCornerRadius(0)
            .ContainerBackground(IsLightTheme ? LightSecondaryBackground : DarkSecondaryBackground);

        ActivityIndicatorStyles.Default = _ =>
            _.Color(IsLightTheme ? Primary : White);

        IndicatorViewStyles.Default = _ => _
            .IndicatorColor(IsLightTheme ? Gray200 : Gray500)
            .SelectedIndicatorColor(IsLightTheme ? Gray950 : Gray100);

        BorderStyles.Default = _ => _
            .Stroke(IsLightTheme ? Gray200 : Gray500)
            .Background(IsLightTheme ? LightSecondaryBackground : DarkSecondaryBackground)
            .StrokeShape(new RoundRectangle().CornerRadius(20))
            .StrokeThickness(0)
            .Padding(DeviceInfo.Idiom == DeviceIdiom.Desktop ? 20 : 15);


        BoxViewStyles.Default = _ => _
            .BackgroundColor(IsLightTheme ? Gray950 : Gray200);

        ButtonStyles.Default = _ => _
            .TextColor(IsLightTheme ? White : PrimaryDarkText)
            .BackgroundColor(IsLightTheme ? Primary : PrimaryDark)
            .FontFamily("OpenSansRegular")
            .FontSize(14)
            .BorderWidth(0)
            .CornerRadius(8)
            .Padding(14, 10)
            .MinimumHeightRequest(44)
            .MinimumWidthRequest(44)
            .VisualState("CommonStates", "Disable", MauiControls.Button.TextColorProperty, IsLightTheme ? Gray950 : Gray200)
            .VisualState("CommonStates", "Disable", MauiControls.Button.BackgroundColorProperty, IsLightTheme ? Gray200 : Gray600);

        ButtonStyles.Themes["Secondary"] = _ => _
            .TextColor(IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground)
            .BackgroundColor(IsLightTheme ? LightSecondaryBackground : DarkSecondaryBackground)
            .FontFamily("OpenSansRegular")
            .FontSize(14)
            .BorderWidth(0)
            .CornerRadius(8)
            .Padding(14, 10)
            .MinimumHeightRequest(44)
            .MinimumWidthRequest(44)
            .VisualState("CommonStates", "Disable", MauiControls.Button.TextColorProperty, IsLightTheme ? Gray200 : Gray500)
            .VisualState("CommonStates", "Disable", MauiControls.Button.BackgroundColorProperty, IsLightTheme ? Gray300 : Gray600);

        CheckBoxStyles.Default = _ => _
            .Color(IsLightTheme ? Primary : White)
            .MinimumHeightRequest(44)
            .MinimumWidthRequest(44)
            .VisualState("CommonStates", "Disable", MauiControls.CheckBox.ColorProperty, IsLightTheme ? Gray300 : Gray600);

        DatePickerStyles.Default = _ => _
            .TextColor(IsLightTheme ? Gray900 : White)
            .BackgroundColor(Colors.Transparent)
            .FontFamily("OpenSansRegular")
            .FontSize(14)
            .MinimumHeightRequest(44)
            .MinimumWidthRequest(44)
            .VisualState("CommonStates", "Disable", MauiControls.DatePicker.TextColorProperty, IsLightTheme ? Gray200 : Gray500);

        EditorStyles.Default = _ => _
            .TextColor(IsLightTheme ? Black : White)
            .BackgroundColor(Colors.Transparent)
            .FontFamily("OpenSansRegular")
            .FontSize(14)
            .PlaceholderColor(IsLightTheme ? Gray200 : Gray500)
            .MinimumHeightRequest(44)
            .MinimumWidthRequest(44)
            .VisualState("CommonStates", "Disable", MauiControls.Editor.TextColorProperty, IsLightTheme ? Gray300 : Gray600);


        EntryStyles.Default = _ => _
            .TextColor(IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground)
            .BackgroundColor(Colors.Transparent)
            .FontFamily("OpenSansRegular")
            .FontSize(DeviceInfo.Current.Idiom == DeviceIdiom.Desktop ? 24 : 18)
            .PlaceholderColor(IsLightTheme ? Gray200 : Gray500)
            .MinimumHeightRequest(44)
            .MinimumWidthRequest(44)
            .VisualState("CommonStates", "Disable", MauiControls.Entry.TextColorProperty, IsLightTheme ? Gray300 : Gray600);


        FrameStyles.Default = _ => _
            .HasShadow(false)
            .BorderColor(IsLightTheme ? Gray200 : Gray950)
            .CornerRadius(8)
            .BackgroundColor(IsLightTheme ? White : Black);

        ImageButtonStyles.Default = _ => _
            .Opacity(1)
            .BorderColor(Colors.Transparent)
            .BorderWidth(0)
            .CornerRadius(0)
            .MinimumHeightRequest(44)
            .MinimumWidthRequest(44)
            .VisualState("CommonStates", "Disable", MauiControls.ImageButton.OpacityProperty, 0.5);

        LabelStyles.Default = _ => _
            .TextColor(IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground)
            .BackgroundColor(Colors.Transparent)
            .FontFamily("OpenSansRegular")
            .FontSize(17)
            .LineHeight(1.29)
            .VisualState("CommonStates", "Disable", MauiControls.Label.TextColorProperty, IsLightTheme ? Gray300 : Gray600);

        LabelStyles.Themes["Headline"] = _ => _
            .TextColor(IsLightTheme ? MidnightBlue : White)
            .FontSize(32)
            .HorizontalOptions(LayoutOptions.Center)
            .HorizontalTextAlignment(TextAlignment.Center);

        LabelStyles.Themes["SubHeadline"] = _ => _
            .TextColor(IsLightTheme ? MidnightBlue : White)
            .FontSize(24)
            .HorizontalOptions(LayoutOptions.Center)
            .HorizontalTextAlignment(TextAlignment.Center);

        LabelStyles.Themes["Caption2"] = _ => _
            .FontSize(12)
            .LineHeight(1.33);

        LabelStyles.Themes["Caption1"] = _ => _
            .FontSize(13)
            .LineHeight(1.38);

        LabelStyles.Themes["Caption1Strong"] = _ => _
            .FontSize(13)
            .LineHeight(1.38)
            .FontFamily(DeviceInfo.Platform == DevicePlatform.WinUI ? "SegoeSemibold" : DeviceInfo.Platform == DevicePlatform.iOS || DeviceInfo.Platform == DevicePlatform.MacCatalyst ? ".SFUI-SemiBold" : "")
            .FontAttributes(DeviceInfo.Platform == DevicePlatform.Android ? FontAttributes.Bold : FontAttributes.None);

        LabelStyles.Themes["Body2"] = _ => _
            .FontSize(15)
            .LineHeight(1.33);

        LabelStyles.Themes["Body2Strong"] = _ => _
            .FontSize(15)
            .LineHeight(1.33)
            .FontFamily(DeviceInfo.Platform == DevicePlatform.WinUI ? "SegoeSemibold" : DeviceInfo.Platform == DevicePlatform.iOS || DeviceInfo.Platform == DevicePlatform.MacCatalyst ? ".SFUI-SemiBold" : "")
            .FontAttributes(DeviceInfo.Platform == DevicePlatform.Android ? FontAttributes.Bold : FontAttributes.None);

        LabelStyles.Themes["Body1"] = _ => _
            .FontSize(17)
            .LineHeight(1.29);

        LabelStyles.Themes["Body1Strong"] = _ => _
            .FontSize(17)
            .LineHeight(1.29)
            .FontFamily(DeviceInfo.Platform == DevicePlatform.WinUI ? "SegoeSemibold" : DeviceInfo.Platform == DevicePlatform.iOS || DeviceInfo.Platform == DevicePlatform.MacCatalyst ? ".SFUI-SemiBold" : "")
            .FontAttributes(DeviceInfo.Platform == DevicePlatform.Android ? FontAttributes.Bold : FontAttributes.None);

        LabelStyles.Themes["Title3"] = _ => _
            .FontSize(20)
            .LineHeight(1.25)
            .FontFamily(DeviceInfo.Platform == DevicePlatform.WinUI ? "SegoeSemibold" : DeviceInfo.Platform == DevicePlatform.iOS || DeviceInfo.Platform == DevicePlatform.MacCatalyst ? ".SFUI-SemiBold" : "")
            .FontAttributes(DeviceInfo.Platform == DevicePlatform.Android ? FontAttributes.Bold : FontAttributes.None);

        LabelStyles.Themes["Title2"] = _ => _
            .FontSize(22)
            .LineHeight(1.27)
            .FontFamily(DeviceInfo.Platform == DevicePlatform.WinUI ? "SegoeSemibold" : DeviceInfo.Platform == DevicePlatform.iOS || DeviceInfo.Platform == DevicePlatform.MacCatalyst ? ".SFUI-SemiBold" : "")
            .FontAttributes(DeviceInfo.Platform == DevicePlatform.Android ? FontAttributes.Bold : FontAttributes.None);

        LabelStyles.Themes[Title1] = _ => _
            .FontSize(28)
            .LineHeight(1.21)
            .FontAttributes(DeviceInfo.Platform == DevicePlatform.WinUI ? FontAttributes.None : FontAttributes.Bold);

        LabelStyles.Themes["LargeTitle"] = _ => _
            .FontSize(34)
            .LineHeight(1.21)
            .FontAttributes(DeviceInfo.Platform == DevicePlatform.WinUI ? FontAttributes.None : FontAttributes.Bold);

        LabelStyles.Themes["Display"] = _ => _
            .FontSize(60)
            .LineHeight(1.17)
            .FontAttributes(DeviceInfo.Platform == DevicePlatform.WinUI ? FontAttributes.None : FontAttributes.Bold);

        BorderStyles.Themes["Default"] = _ => _
            .StrokeShape(new RoundRectangle().CornerRadius(20))
            .Background(IsLightTheme ? LightSecondaryBackground : DarkSecondaryBackground)
            .StrokeThickness(0)
            .Padding(DeviceInfo.Idiom == DeviceIdiom.Desktop ? 20 : 15);

        BorderStyles.Themes["CardStyle"] = _ => _
            .StrokeShape(new RoundRectangle().CornerRadius(20))
            .Background(IsLightTheme ? LightSecondaryBackground : DarkSecondaryBackground)
            .StrokeThickness(0)
            .Padding(DeviceInfo.Idiom == DeviceIdiom.Desktop ? 20 : 15);

        BorderStyles.Themes[InputWrapper] = _ => _
            .StrokeShape(new RoundRectangle().CornerRadius(20))
            .Background(IsLightTheme ? LightSecondaryBackground : DarkSecondaryBackground)
            .StrokeThickness(0)
            .Padding(DeviceInfo.Idiom == DeviceIdiom.Desktop ? 20 : 15);

        BoxViewStyles.Themes["ShimmerCustomViewStyle"] = _ => _
            .BackgroundColor(Colors.Gray)
            .HorizontalOptions(LayoutOptions.Fill)
            .VerticalOptions(LayoutOptions.Center);

        ImageButtonStyles.Default = _ => _
            .BackgroundColor(Colors.Transparent);

        ListViewStyles.Default = _ => _
            .SeparatorColor(IsLightTheme ? Gray200 : Gray500)
            .RefreshControlColor(IsLightTheme ? Gray900 : Gray200);

        PickerStyles.Default = _ => _
            .TextColor(IsLightTheme ? Gray900 : White)
            .TitleColor(IsLightTheme ? Gray900 : Gray200)
            .BackgroundColor(Colors.Transparent)
            .FontFamily("OpenSansRegular")
            .FontSize(DeviceIdiom.Desktop == DeviceInfo.Idiom ? 24 : 18)
            .MinimumHeightRequest(44)
            .MinimumWidthRequest(44)
            .VisualState("CommonStates", "Disable", MauiControls.Picker.TextColorProperty, IsLightTheme ? Gray300 : Gray600)
            .VisualState("CommonStates", "Disable", MauiControls.Picker.TitleColorProperty, IsLightTheme ? Gray300 : Gray600);

        ProgressBarStyles.Default = _ => _
            .ProgressColor(IsLightTheme ? Primary : White)
            .VisualState("CommonStates", "Disable", MauiControls.ProgressBar.ProgressColorProperty, IsLightTheme ? Gray300 : Gray600);

        RadioButtonStyles.Default = _ => _
            .BackgroundColor(Colors.Transparent)
            .TextColor(IsLightTheme ? Black : White)
            .FontFamily("OpenSansRegular")
            .FontSize(DeviceIdiom.Desktop == DeviceInfo.Idiom ? 24 : 18)
            .MinimumHeightRequest(44)
            .MinimumWidthRequest(44)
            .VisualState("CommonStates", "Disable", MauiControls.RadioButton.TextColorProperty, IsLightTheme ? Gray300 : Gray600);

        RefreshViewStyles.Default = _ => _
            .RefreshColor(IsLightTheme ? Gray900 : Gray200);

        SearchBarStyles.Default = _ => _
            .TextColor(IsLightTheme ? Gray900 : White)
            .PlaceholderColor(Gray500)
            .CancelButtonColor(Gray500)
            .BackgroundColor(Colors.Transparent)
            .FontFamily("OpenSansRegular")
            .FontSize(14)
            .MinimumHeightRequest(44)
            .MinimumWidthRequest(44)
            .VisualState("CommonStates", "Disable", MauiControls.SearchBar.TextColorProperty, IsLightTheme ? Gray300 : Gray600)
            .VisualState("CommonStates", "Disable", MauiControls.SearchBar.PlaceholderColorProperty, IsLightTheme ? Gray300 : Gray600);

        //SearchHandlerStyles.Default = _ => _
        //    .TextColor(IsLightTheme ? Gray900 : White)
        //    .PlaceholderColor(Gray500)
        //    .BackgroundColor(Colors.Transparent)
        //    .FontFamily("OpenSansRegular")
        //    .FontSize(14)
        //    .VisualState("CommonStates", "Disable", MauiControls.SearchHandler.TextColorProperty, IsLightTheme ? Gray300 : Gray600)
        //    .VisualState("CommonStates", "Disable", MauiControls.SearchHandler.PlaceholderColorProperty, IsLightTheme ? Gray300 : Gray600);

        ShadowStyles.Default = _ => _
            .Radius(15)
            .Opacity(0.5f)
            .Brush(IsLightTheme ? White : White)
            .Offset(new Point(10, 10));

        SliderStyles.Default = _ => _
            .MinimumTrackColor(IsLightTheme ? Primary : White)
            .MaximumTrackColor(IsLightTheme ? Gray200 : Gray600)
            .ThumbColor(IsLightTheme ? Primary : White)
            .VisualState("CommonStates", "Disable", MauiControls.Slider.MinimumTrackColorProperty, IsLightTheme ? Gray300 : Gray600)
            .VisualState("CommonStates", "Disable", MauiControls.Slider.MaximumTrackColorProperty, IsLightTheme ? Gray300 : Gray600)
            .VisualState("CommonStates", "Disable", MauiControls.Slider.ThumbColorProperty, IsLightTheme ? Gray300 : Gray600);

        SwipeItemStyles.Default = _ => _
            .BackgroundColor(IsLightTheme ? White : Black);

        SwitchStyles.Default = _ => _
            .OnColor(IsLightTheme ? Primary : White)
            .ThumbColor(White)
            .VisualState("CommonStates", "Disable", MauiControls.Switch.OnColorProperty, IsLightTheme ? Gray300 : Gray600)
            .VisualState("CommonStates", "Disable", MauiControls.Switch.ThumbColorProperty, IsLightTheme ? Gray300 : Gray600)
            .VisualState("CommonStates", "On", MauiControls.Switch.OnColorProperty, IsLightTheme ? Secondary : Gray200)
            .VisualState("CommonStates", "On", MauiControls.Switch.ThumbColorProperty, IsLightTheme ? Primary : White)
            .VisualState("CommonStates", "Off", MauiControls.Switch.ThumbColorProperty, IsLightTheme ? Gray400 : Gray500);


        TimePickerStyles.Default = _ => _
            .TextColor(IsLightTheme ? Gray900 : White)
            .BackgroundColor(Colors.Transparent)
            .FontFamily("OpenSansRegular")
            .FontSize(14)
            .MinimumHeightRequest(44)
            .MinimumWidthRequest(44)
            .VisualState("CommonStates", "Disable", MauiControls.TimePicker.TextColorProperty, IsLightTheme ? Gray300 : Gray600);

        TitleBarStyles.Default = _ => _
            .MinimumHeightRequest(32)
            .VisualState("TitleActiveStates", "TitleBarTitleActive", MauiControls.TitleBar.BackgroundColorProperty, Colors.Transparent)
            .VisualState("TitleActiveStates", "TitleBarTitleActive", MauiControls.TitleBar.ForegroundColorProperty, IsLightTheme ? Black : White)
            .VisualState("TitleActiveStates", "TitleBarTitleInactive", MauiControls.TitleBar.BackgroundColorProperty, IsLightTheme ? White : Black)
            .VisualState("TitleActiveStates", "TitleBarTitleInactive", MauiControls.TitleBar.ForegroundColorProperty, IsLightTheme ? Gray400 : Gray500);

        PageStyles.Default = _ => _
            .Padding(0)
            .BackgroundColor(IsLightTheme ? LightBackground : DarkBackground);

        ShellStyles.Default = _ => _
            .Set(MauiControls.Shell.BackgroundColorProperty, IsLightTheme ? LightBackground : DarkBackground)
            .Set(MauiControls.Shell.ForegroundColorProperty, IsLightTheme ? Black : SecondaryDarkText)
            .Set(MauiControls.Shell.TitleColorProperty, IsLightTheme ? Black : SecondaryDarkText)
            .Set(MauiControls.Shell.DisabledColorProperty, IsLightTheme ? Gray200 : Gray950)
            .Set(MauiControls.Shell.UnselectedColorProperty, IsLightTheme ? Gray200 : Gray200)
            .Set(MauiControls.Shell.NavBarHasShadowProperty, false)
            .Set(MauiControls.Shell.TabBarBackgroundColorProperty, IsLightTheme ? LightBackground : DarkBackground)
            .Set(MauiControls.Shell.TabBarForegroundColorProperty, IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground)
            .Set(MauiControls.Shell.TabBarTitleColorProperty, IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground)
            .Set(MauiControls.Shell.TabBarUnselectedColorProperty, IsLightTheme ? Gray900 : Gray200);

        NavigationPageStyles.Default = _ => _
            .Set(MauiControls.NavigationPage.BarBackgroundColorProperty, IsLightTheme ? White : OffBlack)
            .Set(MauiControls.NavigationPage.BarTextColorProperty, IsLightTheme ? Gray200 : White)
            .Set(MauiControls.NavigationPage.IconColorProperty, IsLightTheme ? Gray200 : White);

        TabbedPageStyles.Default = _ => _
            .Set(MauiControls.TabbedPage.BarBackgroundColorProperty, IsLightTheme ? White : Gray950)
            .Set(MauiControls.TabbedPage.BarTextColorProperty, IsLightTheme ? Magenta : White)
            .Set(MauiControls.TabbedPage.UnselectedTabColorProperty, IsLightTheme ? Gray200 : Gray950)
            .Set(MauiControls.TabbedPage.SelectedTabColorProperty, IsLightTheme ? Gray950 : Gray200);


    }
    public static ImageSource GetIconForMediaType(string mediaType)
    {
        return mediaType switch
        {
            "Video" =>IconVideo,
            "Podcast" => IconPodcast,
            "Image" => IconImage,
            "Vocabulary List" => IconVocabList,
            "Article" => IconArticle,
            _ => IconArticle
        };
    }
}

