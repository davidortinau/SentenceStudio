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

    internal bool IsVocabularyOpen => _vocabulary?.IsOpen == true;
    internal bool IsVocabularyWordOpen => _vocabulary?.IsFormOpen == true;
    internal bool IsVocabularyFilterPresented =>
        _vocabulary?.IsPhoneFilterPresented == true;
    internal long VocabularyListVersion => _vocabulary?.ListVersion ?? -1;
    internal long VocabularyFormVersion => _vocabulary?.FormVersion ?? -1;
    internal long VocabularySheetVersion => _vocabulary?.SheetVersion ?? -1;
    internal bool VocabularyListMode => _vocabulary?.ListMode == true;
    internal string VocabularySearchText =>
        _vocabulary?.SearchText ?? string.Empty;
    internal string VocabularyWordTitle => _vocabulary?.WordFormTitle
        ?? Text("VocabWordEdit_EditTitle");
    internal bool IsVocabularyDeletePresented =>
        _vocabulary?.IsDeleteConfirmationPresented == true;
    internal bool IsVocabularyDeletePending => _vocabulary?.IsDeletePending == true;
    internal string? VocabularyDeleteMessage => _vocabulary?.DeleteMessage;

    internal void CancelVocabularyDelete() => _vocabulary?.CancelDelete();

    internal void ConfirmVocabularyDelete() => _vocabulary?.ConfirmDelete();

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
            provider.GetRequiredService<ILogger<VocabularyBrowser>>(),
            Text)
        {
            OpenLookups = OpenRecentLookups
        };
        _vocabulary.Changed += Publish;
    }

    internal void OpenVocabulary()
    {
        if (_destination != NativeDestination.Dashboard || NavigationBlocked
            || IsVocabularyOpen || IsQuizOpen || IsResourcesFormOpen || _services is null
            || VocabularyOwner() is not { } expectedOwner)
            return;
        EnsureVocabulary();
        RunLibrary(async () =>
        {
            if (!await _vocabulary!.OpenAsync(expectedOwner))
                _logger?.LogWarning(
                    "Vocabulary navigation refused a changed owner.");
        });
    }

    /// <summary>
    /// Find Duplicates in a word editor hosted outside the Vocabulary page (Import Content
    /// results): as the source navigates to the Vocabulary page with its duplicate manager
    /// focused on the word, the Library returns to its list and opens Vocabulary with that
    /// focused panel. Back from there returns to the Library list.
    /// </summary>
    private void OpenDuplicatesOnVocabularyPage(string term, string wordId) =>
        RunLibrary(async () =>
        {
            // The editor that asked finishes first: returning to the list releases it.
            await Task.Yield();
            if (_destination != NativeDestination.Dashboard || IsQuizOpen || _services is null
                || VocabularyOwner() is not { } owner)
                return;
            ResetLibraryToList();
            EnsureVocabulary();
            if (!await _vocabulary!.OpenAsync(owner))
            {
                _logger?.LogWarning("Vocabulary navigation refused a changed owner.");
                return;
            }
            await _vocabulary.OpenDuplicatesAsync(term, wordId);
        });

    /// <summary>The signed-in owner when the device scope agrees; null (logged) otherwise.</summary>
    private string? VocabularyOwner()
    {
        var scope = _services!.Provider.GetRequiredService<DeviceUserScopeProvider>();
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
            return null;
        }
        return expectedOwner;
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
        ReleaseRecentLookups();
        _vocabulary?.Dispose();
        _vocabulary = null;
    }
}
