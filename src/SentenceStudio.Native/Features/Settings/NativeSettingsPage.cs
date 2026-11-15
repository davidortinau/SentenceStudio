namespace SentenceStudio.Native.Features.Settings;

/// <summary>
/// The pages of the Settings tab (Captain 2026-11-15, the iOS Settings pattern): the sidebar
/// lists them and the chosen one fills the detail column. Profile is chosen first beside the
/// list; a compact width shows the list until a page is chosen.
/// </summary>
internal enum NativeSettingsPage
{
    Profile,
    Appearance,
    VoiceQuiz,
    LearningMode,
    Coach,
    About,
}
