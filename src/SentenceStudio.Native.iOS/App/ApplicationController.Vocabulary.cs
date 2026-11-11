using CoreFoundation;
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
    internal bool VocabularyFilterPanelShown => _vocabulary?.FilterPanelHidden == false;
    internal string VocabularyArrangement => _vocabulary?.Arrangement ?? string.Empty;
    internal double? VocabularyPageWidth => _vocabulary?.PageWidth;

    /// <summary>The window changed size: the Vocabulary page is drawn for it until it reports its own width.</summary>
    internal void ForgetVocabularyPageWidth() => _vocabulary?.ForgetPageWidth();
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
            || LibraryOwner("Vocabulary") is not { } expectedOwner)
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
    /// focused on the word, the Library opens Vocabulary with that focused panel and returns the
    /// import page to the list. Back from there returns to the Library list.
    /// </summary>
    /// <remarks>
    /// The editor and the results stay until the Vocabulary page has opened. False (the editor
    /// says it could not open) when the handoff cannot start now: another Library action is
    /// running, or the owner is not trusted. The handoff itself runs after the editor's own
    /// operation has returned (posted to the main queue), because on success it releases that
    /// editor.
    /// </remarks>
    private bool OpenDuplicatesOnVocabularyPage(string term, string wordId)
    {
        if (!CanHandOverDuplicates() || LibraryOwner("Find Duplicates") is null)
            return false;
        DispatchQueue.MainQueue.DispatchAsync(() =>
        {
            if (!CanHandOverDuplicates() || LibraryOwner("Find Duplicates") is not { } owner)
            {
                _importWords?.ShowEditError(VocabularyBrowser.FindDuplicatesFailedKey);
                return;
            }
            RunLibrary(() => HandOverDuplicatesAsync(owner, term, wordId));
        });
        return true;
    }

    /// <summary>The import word editor is open and nothing else runs: a handoff can start.</summary>
    private bool CanHandOverDuplicates() =>
        !_disposed && !_busy && _destination == NativeDestination.Dashboard && !IsQuizOpen
        && _services is not null && IsImportWordOpen;

    private async Task HandOverDuplicatesAsync(string owner, string term, string wordId)
    {
        var editor = _importWords;
        EnsureVocabulary();
        var vocabulary = _vocabulary!;
        var wasOpen = vocabulary.IsOpen;
        var opened = false;
        try
        {
            // The scene pushes the Vocabulary page only on an empty Library path, so opening it
            // here leaves the import page and its editor on screen until they are released below.
            opened = wasOpen || await vocabulary.OpenAsync(owner);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Find Duplicates could not open the Vocabulary page.");
        }
        if (!opened || !ReferenceEquals(_importWords, editor) || editor is not { IsFormOpen: true })
        {
            // Nothing moves: a page left half open would be pushed once the import page closes.
            if (!wasOpen && vocabulary.IsOpen)
                vocabulary.OnListPopped();
            _logger?.LogWarning("Find Duplicates kept the import word editor: the Vocabulary page did not open.");
            editor?.ShowEditError(VocabularyBrowser.FindDuplicatesFailedKey);
            return;
        }
        // The Vocabulary page is ready: the import page and its editor leave (the draft is
        // discarded, as the source navigates away) and the page opens on the focused panel.
        ResetLibraryToList(keepVocabulary: true);
        await vocabulary.OpenDuplicatesAsync(term, wordId);
    }

    /// <summary>
    /// The signed-in owner when the device scope agrees, for a Library page (Vocabulary, Import
    /// Content, an imported word, Find Duplicates); null (logged with <paramref name="page"/>)
    /// otherwise. One check, so the pages cannot drift apart.
    /// </summary>
    private string? LibraryOwner(string page)
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
                "{Page} navigation refused an absent or untrusted owner.", page);
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
