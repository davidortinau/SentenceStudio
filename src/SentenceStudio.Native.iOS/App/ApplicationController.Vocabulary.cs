using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SwiftUI;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Native.iOS.Features.Vocabulary;
using SentenceStudio.Native.iOS.Platform;
using SentenceStudio.Native.iOS.Styling;
using SentenceStudio.Services;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.App;

internal sealed partial class ApplicationController
{
    private VocabularyCoordinator? _vocabulary;
    private int _vocabularyOriginTab;

    internal bool IsVocabularyOpen => _vocabulary?.IsOpen == true;
    internal bool IsVocabularyWordOpen => _vocabulary?.IsFormOpen == true;
    internal bool IsVocabularyFilterPresented =>
        _vocabulary?.IsPhoneFilterPresented == true;
    internal int VocabularyOriginTab => _vocabularyOriginTab;
    internal long VocabularyListVersion => _vocabulary?.ListVersion ?? -1;
    internal long VocabularyFormVersion => _vocabulary?.FormVersion ?? -1;
    internal long VocabularySheetVersion => _vocabulary?.SheetVersion ?? -1;
    internal bool VocabularyListMode => _vocabulary?.ListMode == true;
    internal string VocabularySearchText =>
        _vocabulary?.SearchText ?? string.Empty;
    internal string VocabularyWordTitle => _vocabulary?.WordFormTitle
        ?? Text("VocabWordEdit_EditTitle");

    private void EnsureVocabulary()
    {
        if (_vocabulary is not null || _services is null)
            return;
        var provider = _services.Provider;
        _vocabulary = new VocabularyCoordinator(
            provider.GetRequiredService<INativeResourceStore>(),
            provider.GetRequiredService<DeviceUserScopeProvider>(),
            provider.GetRequiredService<IPreferencesService>(),
            provider.GetRequiredService<INativeQuizMedia>(),
            provider.GetRequiredService<ILogger<VocabularyCoordinator>>(),
            Text);
        _vocabulary.Changed += Publish;
    }

    internal void OpenVocabulary()
    {
        if (_destination != NativeDestination.Dashboard || NavigationBlocked
            || IsVocabularyOpen || IsSkillsOpen || _settingsOpen || IsQuizOpen
            || IsResourcesFormOpen || _services is null)
            return;
        var scope = _services.Provider.GetRequiredService<DeviceUserScopeProvider>();
        var expectedOwner = _authState is {
            Phase: NativePhase.Ready,
            Destination: NativeDestination.Dashboard,
            UserId: { Length: > 0 } authenticated }
            ? authenticated : null;
#if DEBUG
        if (expectedOwner is null && _preview)
            expectedOwner = NativePreviewFixture.FromAssembly()?.Owner;
#endif
        if (string.IsNullOrWhiteSpace(expectedOwner)
            || !scope.TryGetUserProfileId(out var current)
            || current != expectedOwner)
        {
            _logger?.LogWarning(
                "Vocabulary navigation refused an absent or untrusted owner.");
            return;
        }
        EnsureVocabulary();
        _vocabularyOriginTab = _selectedTab;
        Run(async () =>
        {
            if (!await _vocabulary!.OpenAsync(expectedOwner))
                _logger?.LogWarning(
                    "Vocabulary navigation refused a changed owner.");
        });
    }

    internal SwiftView RenderVocabularyList(SourceUiStyle style) =>
        _vocabulary?.RenderList(style)
        ?? Views.Text(Text("Common_Loading"));

    internal SwiftView RenderVocabularyFilters(SourceUiStyle style) =>
        _vocabulary?.RenderFilterSheet(style)
        ?? Views.Text(Text("Common_Loading"));

    internal SwiftView RenderVocabularyWord(SourceUiStyle style) =>
        _vocabulary?.RenderForm(style)
        ?? Views.Text(Text("Common_Loading"));

    internal void CancelVocabularyFilters() =>
        _vocabulary?.CancelPhoneFilters();

    internal void OnVocabularyListPopped() =>
        _vocabulary?.OnListPopped();

    internal void OnVocabularyWordPopped() =>
        _vocabulary?.OnFormPopped();

    private void ReleaseVocabulary()
    {
        _vocabulary?.Dispose();
        _vocabulary = null;
    }
}
