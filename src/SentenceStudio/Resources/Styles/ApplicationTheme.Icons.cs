using Fonts;

namespace SentenceStudio.Resources.Styles;

partial class MyTheme
{
    public static FontImageSource IconFontDecrease { get; } = new FontImageSource
    {
        Glyph = FluentUI.font_decrease_24_regular,
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = IconSize
    };

    public static FontImageSource IconFontIncrease { get; } = new FontImageSource
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

    public static FontImageSource IconFilter { get; } = new FontImageSource
    {
        Glyph = FluentUI.filter_24_regular,
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
        Glyph = FluentUI.checkmark_16_regular,
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = IconSize
    };

    public static FontImageSource IconCancel { get; } = new FontImageSource
    {
        Glyph = FluentUI.error_circle_16_regular,
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = IconSize
    };

    // Close/dismiss icon used for dialogs and bottom sheets
    public static FontImageSource IconClose { get; } = new FontImageSource
    {
        Glyph = FluentUI.dismiss_24_regular,
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = IconSize
    };

    public static FontImageSource IconImageExport { get; } = new FontImageSource
    {
        Glyph = FluentUI.arrow_export_up_24_regular,
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = IconSize
    };

    public static FontImageSource IconSwitch { get; } = new FontImageSource
    {
        Glyph = FluentUI.camera_switch_24_regular,
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

    public static FontImageSource IconGlobe { get; } = new FontImageSource
    {
        Glyph = FluentUI.globe_24_regular,
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = IconSize
    };

    public static ImageSource GetIconForMediaType(string mediaType)
    {
        return mediaType switch
        {
            "Video" => IconVideo,
            "Podcast" => IconPodcast,
            "Image" => IconImage,
            "Vocabulary List" => IconVocabList,
            "Article" => IconArticle,
            _ => IconArticle
        };
    }
}
