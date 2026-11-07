using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Native.Features.Import;
using SentenceStudio.Native.Features.Vocabulary;
using SentenceStudio.Services;

namespace SentenceStudio.Native.Android;

/// <summary>
/// Import Content (source /import-content, NavMenu "Import Content") as a More screen over the
/// Dashboard tab, on the shared owner-gated <see cref="ImportContentCoordinator"/> that iOS
/// uses. A result row opens the word editor above the results (its own VocabularyBrowser);
/// that editor's Find Duplicates hands off to the Vocabulary screen's duplicate manager, and
/// View resource opens the resource editor, whose close returns to the results (iOS
/// departures 34-35). Back waits while an import is being committed.
/// </summary>
internal sealed partial class AndroidSliceShell
{
    private ImportContentCoordinator? _importContent;
    private NativeToastScheduler? _importToasts;
    /// <summary>The word editor a result row opens (its own browser, apart from the Vocabulary screen).</summary>
    private VocabularyBrowser? _importWords;
    /// <summary>The menu open on the Import Content screen (a select's key), or null.</summary>
    private string? _importMenu;
    private bool _importWordOpening;

    /// <summary>The Dashboard tab in either mode (Choose My Own or Today's Plan), where More screens open.</summary>
    private bool IsDashboardTab => Destination is NativeDestination.Dashboard or NativeDestination.TodaysPlan;

    private bool IsImportWordOpen => _importContent is not null && _importWords?.IsFormOpen == true;

    private void OpenImportContent() => OpenMoreScreen(MoreScreen.ImportContent);

    private void OpenImportContentCoordinator(IServiceProvider provider, DeviceUserScopeProvider scope,
        string? owner)
    {
        if (owner is null)
            return;
        var coordinator = new ImportContentCoordinator(
            provider.GetRequiredService<INativeContentImport>(), scope,
            provider.GetRequiredService<ILogger<ImportContentCoordinator>>(), Localize);
        coordinator.Changed += OnMoreChanged;
        coordinator.ResourcesWritten += OnImportResourcesWritten;
        _importContent = coordinator;
        // The coordinator resumes on the caller's context: start it on the UI thread.
        RunMoreOnUi(() => OpenImportContentAsync(coordinator, owner));
    }

    private async Task OpenImportContentAsync(ImportContentCoordinator coordinator, string owner)
    {
        try
        {
            if (await coordinator.OpenAsync(owner) || !ReferenceEquals(_importContent, coordinator))
                return;
            global::Android.Util.Log.Warn("SentenceStudio.Native", "Import Content open was refused for the owner.");
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("SentenceStudio.Native", $"Import Content open failed: {ex}");
        }
        // Refused or failed: the screen says the profile was not found (no spinner left running).
        if (ReferenceEquals(_importContent, coordinator))
            ReleaseImportContent();
        OnMoreChanged();
    }

    /// <summary>
    /// An import wrote the owner's resource and words: the Resources list and the Dashboard's
    /// Choose My Own catalog read again now, not only after the next sync.
    /// </summary>
    private void OnImportResourcesWritten() => Publish(() =>
    {
        _resources?.ReloadList();
        ReloadDashboardAfterResources();
    });

    /// <summary>A coordinator call from a tap (UI thread); failures show as the shell's error.</summary>
    private void ImportCommand(Func<ImportContentCoordinator, Task> command)
    {
        if (_importContent is { } import)
            _ = GuardMoreAsync(() => command(import));
    }

    /// <summary>A bound input changed the coordinator without a redraw: draw now so the field shows it.</summary>
    private void ImportEdit(Action<ImportContentCoordinator> edit)
    {
        if (_importContent is not { } import)
            return;
        edit(import);
        RenderMoreNow();
    }

    private void ToggleImportMenu(string key) => Publish(() =>
        _importMenu = _importMenu == key ? null : key);

    private void CloseImportMenu() => Publish(() => _importMenu = null);

    /// <summary>Back on Import Content, innermost first: menu, the word editor's dialog, the word editor.</summary>
    private bool HandleImportContentBack()
    {
        if (_importMenu is not null)
        {
            CloseImportMenu();
            return true;
        }
        if (_importContent?.IsNavigationBlocked == true)
            return true;
        if (_importWords is { } words && IsImportWordOpen)
        {
            if (words.IsNavigationBlocked)
                return true;
            if (words.IsDeleteConfirmationPresented)
                words.CancelDelete();
            else
                words.CloseWord();
            OnMoreChanged();
            return true;
        }
        return false;
    }

    /// <summary>Source ViewResource: the imported resource's editor; closing it returns to the results.</summary>
    private void ViewImportedResource(string resourceId) => Publish(() =>
    {
        if (!IsDashboardTab || Busy.Value || _more != MoreScreen.ImportContent
            || string.IsNullOrWhiteSpace(resourceId)
            || EnsureResources() is not { } resources || resources.IsFormOpen)
            return;
        _resourceFormFromDashboard = true;
        _resourceFormReturn = Destination;
        Destination = NativeDestination.Resources;
        resources.OnBrowserAppeared();
        resources.Open(resourceId);
    });

    /// <summary>
    /// Source NavigateToVocabDetail: the imported word's editor above the results. The owner's
    /// vocabulary is read first (again each time: the import may have added words).
    /// </summary>
    private void OpenImportedWord(string wordId)
    {
        if (_more != MoreScreen.ImportContent || _importContent is not { IsOpen: true }
            || IsImportWordOpen || _importWordOpening || string.IsNullOrWhiteSpace(wordId) || _scope is null)
            return;
        if (MoreOwner() is not { } owner)
        {
            global::Android.Util.Log.Warn("SentenceStudio.Native",
                "An imported word's editor refused an absent or untrusted owner.");
            return;
        }
        if (_importWords is null)
        {
            var provider = _scope.ServiceProvider;
            _importWords = new VocabularyBrowser(
                provider.GetRequiredService<INativeResourceStore>(),
                provider.GetRequiredService<DeviceUserScopeProvider>(),
                provider.GetRequiredService<IPreferencesService>(),
                provider.GetRequiredService<INativeQuizMedia>(),
                provider.GetRequiredService<ILogger<VocabularyBrowser>>(),
                Localize)
            {
                // Source: Find Duplicates navigates to the Vocabulary page's manager.
                FindDuplicatesElsewhere = OpenDuplicatesOnVocabularyScreen,
            };
            _importWords.Changed += OnMoreChanged;
        }
        var words = _importWords;
        // One open at a time: a second row tap while this one reads is ignored.
        _importWordOpening = true;
        RunMoreOnUi(async () =>
        {
            try
            {
                if (words.IsOpen)
                    await words.ReloadAsync();
                else if (!await words.OpenAsync(owner))
                {
                    global::Android.Util.Log.Warn("SentenceStudio.Native",
                        "An imported word's editor was refused for the owner.");
                    return;
                }
                if (ReferenceEquals(_importWords, words))
                    await words.OpenWordAsync(wordId);
            }
            finally
            {
                if (ReferenceEquals(_importWords, words))
                    _importWordOpening = false;
            }
        });
    }

    /// <summary>The import word editor is open and nothing else runs: a handoff can start.</summary>
    private bool CanHandOverDuplicates() =>
        !_disposed && !Busy.Value && IsDashboardTab
        && _more == MoreScreen.ImportContent && IsImportWordOpen && _scope is not null;

    /// <summary>
    /// The import word editor's Find Duplicates: opens the Vocabulary screen's duplicate manager
    /// on the term and word. The editor waits until the Vocabulary screen is ready, then the
    /// import screen and its editor close (Back returns to the Dashboard, as on iOS where it
    /// returns to the Library list). A refused open keeps the editor and says so.
    /// </summary>
    private bool OpenDuplicatesOnVocabularyScreen(string term, string wordId)
    {
        if (!CanHandOverDuplicates() || MoreOwner() is null)
            return false;
        Post(() => _ = GuardMoreAsync(() => HandOverDuplicatesAsync(term, wordId)));
        return true;
    }

    private async Task HandOverDuplicatesAsync(string term, string wordId)
    {
        var editor = _importWords;
        if (!CanHandOverDuplicates() || MoreOwner() is not { } owner || _scope is null)
        {
            editor?.ShowEditError(VocabularyBrowser.FindDuplicatesFailedKey);
            return;
        }
        var provider = _scope.ServiceProvider;
        var vocabulary = new VocabularyBrowser(
            provider.GetRequiredService<INativeResourceStore>(),
            provider.GetRequiredService<DeviceUserScopeProvider>(),
            provider.GetRequiredService<IPreferencesService>(),
            provider.GetRequiredService<INativeQuizMedia>(),
            provider.GetRequiredService<ILogger<VocabularyBrowser>>(),
            Localize);
        var opened = false;
        try
        {
            opened = await vocabulary.OpenAsync(owner);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("SentenceStudio.Native",
                $"Find Duplicates could not open the Vocabulary screen: {ex}");
        }
        if (!opened || !ReferenceEquals(_importWords, editor) || editor is not { IsFormOpen: true }
            || _more != MoreScreen.ImportContent)
        {
            vocabulary.Dispose();
            global::Android.Util.Log.Warn("SentenceStudio.Native",
                "Find Duplicates kept the import word editor: the Vocabulary screen did not open.");
            editor?.ShowEditError(VocabularyBrowser.FindDuplicatesFailedKey);
            return;
        }
        // The Vocabulary screen is ready: the import screen and its editor leave (the draft is
        // discarded, as the source navigates away) and the screen opens on the focused panel.
        ReleaseImportContent();
        vocabulary.Changed += OnMoreChanged;
        _vocabulary = vocabulary;
        _more = MoreScreen.Vocabulary;
        OnMoreChanged();
        await vocabulary.OpenDuplicatesAsync(term, wordId);
    }

    /// <summary>The newest success message, shown once as a snackbar (source toasts).</summary>
    private NativeMessage? ImportToastCandidate() =>
        _more == MoreScreen.ImportContent && _importContent is { IsOpen: true } import && !IsImportWordOpen
            ? NativeMessages.LatestToast(import.Messages)
            : null;

    private NativeToastScheduler ImportToasts
    {
        get
        {
            if (_importToasts is null)
            {
                _importToasts = new NativeToastScheduler();
                _importToasts.Changed += () => Publish(() => { });
            }
            return _importToasts;
        }
    }

    private void ReleaseImportContent()
    {
        _importMenu = null;
        _importWordOpening = false;
        _importToasts?.Reset();
        if (_importWords is { } words)
        {
            _importWords = null;
            words.Changed -= OnMoreChanged;
            words.Dispose();
        }
        if (_importContent is not { } import)
            return;
        _importContent = null;
        import.Changed -= OnMoreChanged;
        import.ResourcesWritten -= OnImportResourcesWritten;
        import.Dispose();
    }
}
