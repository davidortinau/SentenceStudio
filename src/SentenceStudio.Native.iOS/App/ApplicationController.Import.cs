using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SwiftUI;
using SentenceStudio.Data;
using SentenceStudio.Native.Features.Import;
using SentenceStudio.Abstractions;
using SentenceStudio.Native.iOS.Features.Import;
using SentenceStudio.Native.iOS.Features.Vocabulary;
using SentenceStudio.Native.iOS.Platform;
using SentenceStudio.Native.iOS.Styling;
using SentenceStudio.Services;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.App;

/// <summary>Library routes of the import pages (kept here so the Library files stay as they are).</summary>
internal static class ApplicationImportRoutes
{
    internal const string ImportContent = "import-content";
    /// <summary>A result row's word editor (source /vocabulary/edit/{id}), above the results.</summary>
    internal const string ImportWord = "import-word";
}

/// <summary>
/// The Library's import pages (Captain, 2026-11-06: quick-add "including the import
/// features"): Import Content (source /import-content). Each is a browse-level page pushed
/// on the Library stack, so the tab bar stays, as on the other Library pages; it has no
/// Save or Cancel (the source page is left like any other page), and Back waits only while
/// an import is being committed.
/// </summary>
internal sealed partial class ApplicationController
{
    private ImportContentCoordinator? _importContent;
    private NativeToastScheduler? _importContentToasts;
    /// <summary>
    /// The word editor a result row opens: its own Vocabulary coordinator, so a Vocabulary
    /// page open elsewhere keeps its state.
    /// </summary>
    private VocabularyCoordinator? _importWords;

    internal bool IsImportContentOpen => _importContent?.IsOpen == true;

    /// <summary>Changes when the Import Content page must be drawn again.</summary>
    internal long ImportContentVersion => _importContent?.Version ?? -1;

    /// <summary>A commit, or the result word's save or delete, is running: Back and the tabs wait.</summary>
    internal bool IsImportNavigationBlocked => _importContent?.IsNavigationBlocked == true
        || _importWords?.IsNavigationBlocked == true;

    /// <summary>A result row's word editor is open (Save or Cancel decides how it closes).</summary>
    internal bool IsImportWordOpen => _importContent is not null && _importWords?.IsFormOpen == true;

    internal long ImportWordVersion => _importWords?.FormVersion ?? -1;

    internal string ImportWordTitle => _importWords?.WordFormTitle ?? Text("VocabWordEdit_EditTitle");

    internal string? ImportWordDeleteMessage => _importWords?.DeleteMessage;

    internal bool IsImportWordDeletePresented => _importWords?.IsDeleteConfirmationPresented == true;

    internal bool IsImportWordDeletePending => _importWords?.IsDeletePending == true;

    internal SwiftView RenderImportWord(SourceUiStyle style) =>
        _importWords?.RenderForm(style) ?? Views.Text(Text("Common_Loading"));

    internal void CancelImportWordDelete() => _importWords?.CancelDelete();

    internal void ConfirmImportWordDelete() => _importWords?.ConfirmDelete();

    /// <summary>Back (or a swipe) from the word editor discards its draft, as Cancel does.</summary>
    internal void OnImportWordPopped() => _importWords?.OnFormPopped();

    internal string ImportContentTitle => Text("Import_Content_Title");

    /// <summary>
    /// Library quick-add "Import Content": pushes the page above the Library list for the
    /// signed-in owner (an absent or untrusted owner opens nothing).
    /// </summary>
    internal void OpenImportContent()
    {
        if (_destination != NativeDestination.Dashboard || NavigationBlocked
            || IsLibraryPageOpen || IsResourcesFormOpen || IsQuizOpen || _services is null)
            return;
        if (LibraryOwner("Import Content") is not { } owner)
            return;
        var provider = _services.Provider;
        var scope = provider.GetRequiredService<DeviceUserScopeProvider>();
        _libraryErrorKey = null;
        _selectedTab = NativeTabs.Library;
        var coordinator = new ImportContentCoordinator(
            provider.GetRequiredService<INativeContentImport>(), scope,
            provider.GetRequiredService<ILogger<ImportContentCoordinator>>(), Text);
        coordinator.Changed += Publish;
        coordinator.ResourcesWritten += OnImportResourcesWritten;
        _importContent = coordinator;
        _ = OpenImportContentAsync(coordinator, owner);
        Publish();
    }

    /// <summary>
    /// An import wrote the owner's resource and words: the Library's resource list (its count
    /// and the Resources page) reads again now, not only after the next sync. Marking the rows
    /// for sync moves the list's source version, but that mark can fail after the commit (it
    /// is logged), so the write is noted here too.
    /// </summary>
    private void OnImportResourcesWritten()
    {
        _resources?.NoteResourcesWritten();
        ReadResourcesIfStale();
    }

    private async Task OpenImportContentAsync(ImportContentCoordinator coordinator, string owner)
    {
        try
        {
            if (await coordinator.OpenAsync(owner) || !ReferenceEquals(_importContent, coordinator))
                return;
            _logger?.LogWarning("Import Content open was refused for the owner.");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Native iOS Import Content open failed.");
        }
        if (!ReferenceEquals(_importContent, coordinator) || _disposed)
            return;
        ReleaseImportContent();
        _libraryErrorKey = "Import_ProfileNotFound";
        Publish();
    }

    /// <summary>Changes only when the preview rows change; other redraws keep the drawn rows.</summary>
    internal long ImportContentRowsVersion => _importContent?.RowsVersion ?? -1;

    /// <summary>Typed or previewed content not imported yet (another tab's entry shows the page).</summary>
    internal bool HasImportContentWork => _importContent?.HasUnsavedWork == true;

    internal SwiftView RenderImportContent(SourceUiStyle style, ImportContentDrawn drawn,
        bool reuseRows) =>
        _importContent is { IsOpen: true } import
            ? ImportContentView.Page(import, style, Text,
                new ImportContentActions(ViewImportedResource, OpenImportedWord), drawn, reuseRows)
            : Views.Text(Text("Common_Loading"));

    /// <summary>What the overlay shows: the banner ids and the current toast (redrawn when it changes).</summary>
    internal string ImportContentOverlayKey
    {
        get
        {
            if (_importContent is not { IsOpen: true } import)
                return string.Empty;
            var toast = ImportContentToast;
            var banners = NativeMessages.Banners(import.Messages);
            return $"{string.Join(',', banners.Select(message => message.Id))}|{toast?.Id ?? 0}";
        }
    }

    internal SwiftView? RenderImportContentOverlay(SourceUiStyle style) =>
        _importContent is { IsOpen: true } import
            ? ImportContentView.Messages(import.Messages, ImportContentToast, style, Text,
                import.DismissMessages, DismissImportContentToast)
            : null;

    /// <summary>The newest success message, shown once as a toast (as on the other Library pages).</summary>
    private NativeMessage? ImportContentToast
    {
        get
        {
            if (_importContent is not { } import)
                return null;
            var toasts = _importContentToasts ??= CreateImportContentToasts();
            toasts.Offer(NativeMessages.LatestToast(import.Messages));
            return toasts.Current;
        }
    }

    private void DismissImportContentToast()
    {
        if (_importContentToasts?.Dismiss() == true)
            Publish();
    }

    private NativeToastScheduler CreateImportContentToasts()
    {
        var toasts = new NativeToastScheduler();
        toasts.Changed += Publish;
        return toasts;
    }

    /// <summary>Source ViewResource: the imported resource's detail, above the results.</summary>
    private void ViewImportedResource(string resourceId)
    {
        if (_destination != NativeDestination.Dashboard || NavigationBlocked
            || IsResourcesFormOpen || !IsImportContentOpen || string.IsNullOrWhiteSpace(resourceId))
            return;
        EnsureResources();
        if (_resources is null)
            return;
        _resources.Open(resourceId);
        Publish();
    }

    /// <summary>
    /// Source NavigateToVocabDetail: the imported word's editor above the results (Back
    /// returns to them, as the source re-hydrates its results). The owner's vocabulary is
    /// read first, as on the Vocabulary page, so the editor applies the same rules.
    /// </summary>
    private void OpenImportedWord(string wordId)
    {
        if (_destination != NativeDestination.Dashboard || NavigationBlocked || _services is null
            || !IsImportContentOpen || IsImportWordOpen || IsResourcesFormOpen
            || string.IsNullOrWhiteSpace(wordId))
            return;
        if (LibraryOwner("Imported word") is not { } owner)
            return;
        var provider = _services.Provider;
        var scope = provider.GetRequiredService<DeviceUserScopeProvider>();
        if (_importWords is null)
        {
            _importWords = new VocabularyCoordinator(
                provider.GetRequiredService<INativeResourceStore>(), scope,
                provider.GetRequiredService<IPreferencesService>(),
                provider.GetRequiredService<INativeQuizMedia>(),
                provider.GetRequiredService<ILogger<VocabularyBrowser>>(), Text)
            {
                // Source: Find Duplicates navigates to the Vocabulary page's manager.
                FindDuplicatesElsewhere = OpenDuplicatesOnVocabularyPage
            };
            _importWords.Changed += Publish;
        }
        _ = OpenImportedWordAsync(_importWords, owner, wordId);
    }

    private async Task OpenImportedWordAsync(VocabularyCoordinator words, string owner, string wordId)
    {
        try
        {
            // Read again each time: the import may have added words since the last read.
            if (words.IsOpen)
                await words.ReloadAsync();
            else if (!await words.OpenAsync(owner))
            {
                _logger?.LogWarning("An imported word's editor was refused for the owner.");
                return;
            }
            if (ReferenceEquals(_importWords, words))
                await words.OpenWordAsync(wordId);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "An imported word's editor could not open.");
        }
        if (ReferenceEquals(_importWords, words) && !_disposed)
            Publish();
    }

    /// <summary>Back from the Import Content page to the Library list.</summary>
    internal void OnImportContentPopped()
    {
        if (_importContent is null)
            return;
        ReleaseImportContent();
        Publish();
    }

    private void ReleaseImportContent()
    {
        _importContentToasts?.Reset();
        if (_importWords is { } words)
        {
            _importWords = null;
            words.Changed -= Publish;
            words.Dispose();
        }
        if (_importContent is not { } import)
            return;
        _importContent = null;
        import.Changed -= Publish;
        import.ResourcesWritten -= OnImportResourcesWritten;
        import.Dispose();
    }
}
