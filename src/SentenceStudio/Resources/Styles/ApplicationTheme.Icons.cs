using Fonts;

namespace SentenceStudio.Resources.Styles;

partial class MyTheme
{
    public static FontImageSource IconGrammarCheck { get; } = new FontImageSource
    {
        Glyph = FluentUI.text_grammar_wand_20_regular,
        FontFamily = FluentUI.FontFamily,
        Color = Warning,
        Size = Size160
    };

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

    // Settings icon for preferences and configuration
    public static FontImageSource IconSettings { get; } = new FontImageSource
    {
        Glyph = FluentUI.settings_24_regular,
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

    // Plan item icons
    public static FontImageSource IconCheckmarkCircleFilled { get; } = new FontImageSource
    {
        Glyph = FluentUI.checkmark_circle_24_regular,
        FontFamily = FluentUI.FontFamily,
        Color = CheckboxColor,
        Size = Size280
    };

    public static FontImageSource IconChart { get; } = new FontImageSource
    {
        FontFamily = FluentUI.FontFamily,
        Glyph = FluentUI.chart_multiple_20_regular,
        Color = MyTheme.HighlightDarkest
    };

    public static FontImageSource IconSelectAll { get; } = new FontImageSource
    {
        FontFamily = FluentUI.FontFamily,
        Glyph = FluentUI.select_all_on_20_regular,
        Color = MyTheme.HighlightDarkest
    };

    public static FontImageSource IconDismiss { get; } = new FontImageSource
    {
        FontFamily = FluentUI.FontFamily,
        Glyph = FluentUI.dismiss_20_regular,
        Color = MyTheme.HighlightDarkest
    };

    public static FontImageSource IconCleanup { get; } = new FontImageSource
    {
        FontFamily = FluentUI.FontFamily,
        Glyph = FluentUI.broom_20_regular,
        Color = MyTheme.HighlightDarkest
    };

    public static FontImageSource IconCheckmarkCircleFilledCorrect { get; } = new FontImageSource
    {
        Glyph = FluentUI.checkmark_circle_24_regular,
        FontFamily = FluentUI.FontFamily,
        Color = MyTheme.Light.Correct,
        Size = Size280
    };

    public static FontImageSource IconCircle { get; } = new FontImageSource
    {
        Glyph = FluentUI.circle_24_regular,
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? Gray400 : Gray300,
        Size = Size280
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

    // Filter icons for search/filter UI
    public static FontImageSource IconTag { get; } = new FontImageSource
    {
        Glyph = FluentUI.tag_20_regular,
        FontFamily = FluentUI.FontFamily,
        Color = Gray600,
        Size = Size200
    };

    public static FontImageSource IconResource { get; } = new FontImageSource
    {
        Glyph = FluentUI.book_20_regular,
        FontFamily = FluentUI.FontFamily,
        Color = Gray600,
        Size = Size200
    };

    public static FontImageSource IconLemma { get; } = new FontImageSource
    {
        Glyph = FluentUI.text_word_count_20_regular,
        FontFamily = FluentUI.FontFamily,
        Color = Gray600,
        Size = Size200
    };

    public static FontImageSource IconStatusFilter { get; } = new FontImageSource
    {
        Glyph = FluentUI.status_20_regular,
        FontFamily = FluentUI.FontFamily,
        Color = Gray600,
        Size = Size200
    };

    // Conversation scenario icons
    public static FontImageSource IconChat { get; } = new FontImageSource
    {
        Glyph = FluentUI.chat_20_regular,
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = Size200
    };

    public static FontImageSource IconChatSmall { get; } = new FontImageSource
    {
        Glyph = FluentUI.chat_16_regular,
        FontFamily = FluentUI.FontFamily,
        Color = Gray400,
        Size = Size160
    };

    public static FontImageSource IconRepeat { get; } = new FontImageSource
    {
        Glyph = FluentUI.arrow_repeat_all_20_regular,
        FontFamily = FluentUI.FontFamily,
        Color = IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground,
        Size = Size200
    };

    public static FontImageSource IconRepeatSmall { get; } = new FontImageSource
    {
        Glyph = FluentUI.arrow_repeat_1_16_regular,
        FontFamily = FluentUI.FontFamily,
        Color = Gray400,
        Size = Size160
    };

    public static FontImageSource IconPerson { get; } = new FontImageSource
    {
        Glyph = FluentUI.person_20_regular,
        FontFamily = FluentUI.FontFamily,
        Color = Gray400,
        Size = Size160
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
