using CoreFoundation;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Foundation;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.SwiftUI;
using SentenceStudio.Abstractions;
using SentenceStudio.Native;
using SentenceStudio.Native.iOS.Styling;
using SentenceStudio.Services;

namespace SentenceStudio.Native.iOS.Features.Vocabulary;

/// <summary>
/// The Vocabulary list and word editor in SwiftUI. The shared <see cref="VocabularyBrowser"/>
/// holds the screen's state and rules (the same as Android's); this adds the SwiftUI views,
/// the Save buttons and resource count it updates in place, and the main-thread hops.
/// </summary>
internal sealed class VocabularyCoordinator : VocabularyBrowser
{
    private readonly List<SwiftView> _saveButtons = [];
    private readonly List<SwiftView> _menuSaveButtons = [];
    private SwiftView? _resourceCount;
    private SourceUiAppearance? _saveAppearance;

    internal VocabularyCoordinator(
        INativeResourceStore store,
        DeviceUserScopeProvider userScope,
        IPreferencesService preferences,
        INativeQuizMedia media,
        ILogger<VocabularyBrowser> logger,
        Func<string, string> text)
        : base(store, userScope, preferences, media, logger, text)
    {
    }

    /// <summary>Opens Recent lookups from the list toolbar (decision 4 B); none hides the button.</summary>
    internal Action? OpenLookups { get; init; }

    internal string WordFormTitle => Text("VocabWordEdit_EditTitle");

    internal void OnListAppeared() => Run(ReloadAsync);

    internal void OnListPopped() => Close();

    internal void OnFormPopped() => CloseWord();

    internal void CancelEdit() => CloseWord();

    /// <summary>The SwiftUI confirmation's Delete.</summary>
    internal void ConfirmDelete() => Run(ConfirmDeleteAsync);

    internal SwiftView RenderList(SourceUiStyle style) =>
        VocabularyListView.Page(ListState, style, Text,
            new VocabularyListActions(
                Search: Search,
                OpenFilters: OpenPhoneFilters,
                ClearFilters: ClearFilters,
                OpenWord: id => Run(() => OpenWordAsync(id)),
                Reload: () => Run(ReloadAsync),
                Sort: SetSort,
                SetListMode: SetListMode,
                ChangeWideFilter: ChangeWideFilter,
                OpenLookups: OpenLookups,
                Duplicates: new VocabularyDuplicateActions(
                    Close: CloseDuplicates,
                    ShowMore: () => Run(ShowMoreDuplicatesAsync),
                    Merge: term => Run(() => MergeDuplicatesAsync(term)),
                    EditRecord: id => Run(() => OpenWordAsync(id)))));

    internal SwiftView RenderFilterSheet(SourceUiStyle style) =>
        VocabularyListView.PhoneFilterSheet(ListState, style, Text,
            new VocabularyFilterActions(
                Change: (type, value) => ChangeFilter(type, value, live: false),
                Apply: ApplyPhoneFilters, Cancel: CancelPhoneFilters,
                Clear: ClearFilters));

    internal SwiftView RenderForm(SourceUiStyle style)
    {
        _saveButtons.Clear();
        _menuSaveButtons.Clear();
        _resourceCount = null;
        _saveAppearance = style.Appearance;
        var form = VocabularyWordEditView.Page(EditState, style, Text,
            new VocabularyEditActions(
                Change: ChangeDraft,
                ToggleResource: ToggleResource,
                Save: () => Run(SaveAsync),
                Cancel: CancelEdit,
                Retry: () => Run(RetryWordAsync),
                PlayAudio: () => Run(PlayAudioAsync),
                FindDuplicates: () => Run(FindDuplicatesAsync),
                RequestDelete: RequestDelete,
                RegisterSave: view => _saveButtons.Add(view),
                RegisterMenuSave: view => _menuSaveButtons.Add(view),
                RegisterResourceCount: view => _resourceCount = view));
        UpdateSaveButtons();
        return form;
    }

    /// <summary>
    /// SwiftUI redraws a whole page from its version, so the editor is not rebuilt for what it
    /// already shows: typed text and resource switches update the Save buttons and the count in
    /// place, and the scene updates the delete confirmation in place (rebuilding the editor
    /// under it would replay its presentation). A sheet draft redraws nothing.
    /// </summary>
    protected override void OnChanged(VocabularyChanges changes)
    {
        if ((changes & (VocabularyChanges.DraftText | VocabularyChanges.DeleteDialog)) != 0)
            UpdateSaveButtons();
        if ((changes & VocabularyChanges.ResourceToggle) != 0 && _resourceCount is not null)
            _resourceCount.Text = string.Format(CultureInfo.CurrentCulture,
                Text("VocabWordEdit_SelectedCount"), DraftResourceCount);
        var list = (changes & VocabularyChanges.List) != 0;
        var form = (changes & VocabularyChanges.Form) != 0;
        var sheet = (changes & VocabularyChanges.Sheet) != 0;
        // The confirmation reads its state when the scene handles Changed.
        if (list || form || sheet || (changes & VocabularyChanges.DeleteDialog) != 0)
            Publish(list, form, sheet);
    }

    protected override void RaiseChanged()
    {
        if (NSThread.IsMain)
            base.RaiseChanged();
        else
            DispatchQueue.MainQueue.DispatchAsync(() =>
            {
                if (!IsDisposed)
                    base.RaiseChanged();
            });
    }

    /// <summary>
    /// Store work on a pool thread, then back on the main thread before the browser touches
    /// its state or a SwiftView.
    /// </summary>
    protected override async Task<T> OffUiAsync<T>(Func<Task<T>> work)
    {
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        try
        {
            result = await Task.Run(work).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ExceptionDispatchInfo.Capture(ex);
        }
        await new MainQueue();
        failure?.Throw();
        return result;
    }

    /// <summary>Resumes the awaiting method on the main queue (inline when already there).</summary>
    private readonly struct MainQueue : INotifyCompletion
    {
        public MainQueue GetAwaiter() => this;
        public bool IsCompleted => NSThread.IsMain;
        public void OnCompleted(Action continuation) =>
            DispatchQueue.MainQueue.DispatchAsync(continuation);
        public void GetResult()
        {
        }
    }

    private void Run(Func<Task> action)
    {
        if (!IsDisposed)
            _ = RunSafelyAsync(action);
    }

    private async Task RunSafelyAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException) when (IsDisposed)
        {
        }
        catch (Exception ex)
        {
            ReportActionFailure(ex);
        }
    }

    private void UpdateSaveButtons()
    {
        if (_saveAppearance is null || IsDisposed)
            return;
        var enabled = CanSaveOpenWord;
        foreach (var button in _saveButtons)
            SourceUiStyle.SetCustomButtonAvailability(button,
                _saveAppearance, SourceButtonRole.Primary, enabled);
        foreach (var item in _menuSaveButtons)
            item.IsEnabled = enabled;
    }
}
