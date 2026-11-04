using SentenceStudio.Contracts.Theme;
using SentenceStudio.Native.Features.Settings;
using SentenceStudio.Native.iOS.Styling;

namespace SentenceStudio.Native.iOS.Features.Settings;

/// <summary>iOS appearance writer for the shared, owner-gated Settings coordinator.</summary>
internal static class SettingsAppearanceSave
{
    internal static SourceUiAppearance? SaveAppearance(
        this SettingsCoordinator settings, AppearanceSelection selection) =>
        settings.SaveAppearance(selection,
            static (preferences, value) => SourceUiAppearance.Save(preferences, value));
}
