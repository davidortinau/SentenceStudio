using Foundation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Features.Settings;
using SentenceStudio.Native.iOS.Styling;
using SentenceStudio.Services;

namespace SentenceStudio.Native.iOS.App;

/// <summary>
/// Settings > About (source Settings.razor About card): the app's version and What's New, the
/// release notes for this version (or the newest) in a sheet.
/// </summary>
internal sealed partial class ApplicationController
{
    private NativeReleaseNotes? _whatsNew;
    private bool _whatsNewPresented;
    private bool _whatsNewLoading;
    private string? _whatsNewNoticeKey;

    /// <summary>
    /// The bundle's version (the product version, src/SentenceStudio.Version.props); empty when
    /// the bundle has none, and then What's New shows the newest notes.
    /// </summary>
    internal static string AppVersion =>
        NSBundle.MainBundle.ObjectForInfoDictionary("CFBundleShortVersionString")?.ToString()
            ?? string.Empty;

    internal bool IsWhatsNewPresented => _whatsNewPresented;

    /// <summary>The notes the sheet shows once read (they do not change while the app runs).</summary>
    internal NativeReleaseNotes? WhatsNewNotes => _whatsNew;

    /// <summary>Changes when the About page must be drawn again (the sheet has its own checks).</summary>
    internal int AboutVersion => _aboutVersion;
    private int _aboutVersion;

    internal SwiftView RenderAbout(SourceUiStyle style) =>
        SettingsView.About(style, Text, AppVersion, _whatsNewLoading, _whatsNewNoticeKey,
            OpenWhatsNew);

    internal SwiftView RenderWhatsNew(SourceUiStyle style) =>
        SettingsView.WhatsNew(style, Text, _whatsNew);

    /// <summary>What's New: reads the notes once, then presents them; says so when there are none.</summary>
    internal void OpenWhatsNew()
    {
        if (_whatsNewLoading || _services is null)
            return;
        if (_whatsNew is not null)
        {
            _whatsNewPresented = true;
            Publish();
            return;
        }
        _whatsNewLoading = true;
        _whatsNewNoticeKey = null;
        _aboutVersion++;
        Publish();
        _ = LoadWhatsNewAsync(_services.Provider);
    }

    private async Task LoadWhatsNewAsync(IServiceProvider provider)
    {
        try
        {
            _whatsNew = await NativeReleaseNotes.ForAppVersionAsync(
                provider.GetRequiredService<ReleaseNotesService>(), AppVersion);
            if (_whatsNew is null)
                _whatsNewNoticeKey = "Settings_NoReleaseNotes";
            // Presented only if About is still in front (the person may have moved on).
            else if (_selectedTab == NativeTabs.Settings && _settingsPage == NativeSettingsPage.About)
                _whatsNewPresented = true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "The release notes could not be read.");
            _whatsNewNoticeKey = "NativeSettings_ReleaseNotesFailed";
        }
        finally
        {
            _whatsNewLoading = false;
            _aboutVersion++;
        }
        if (!_disposed)
            Publish();
    }

    /// <summary>The sheet closed (its button, or the person swiping it away).</summary>
    internal void DismissWhatsNew()
    {
        if (!_whatsNewPresented)
            return;
        _whatsNewPresented = false;
        Publish();
    }
}
