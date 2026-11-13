using CoreFoundation;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Foundation;
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
    /// <summary>The drawn editor's cards and label that change in place (VocabularyWordEditView.Section).</summary>
    private readonly Dictionary<VocabularyEditSection, SwiftView> _sections = [];
    /// <summary>The drawn Reference Sentences buttons whose availability follows typing.</summary>
    private readonly Dictionary<string, SwiftView> _exampleButtons = new(StringComparer.Ordinal);
    private SourceUiStyle? _formStyle;
    private VocabularyEditActions? _formActions;
    /// <summary>Cards waiting to be drawn again on the next main-queue turn (see <see cref="ScheduleRefill"/>).</summary>
    private readonly HashSet<VocabularyEditSection> _pendingRefills = [];

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

    /// <summary>Source: Add Word for a new word, Edit Word otherwise.</summary>
    internal string WordFormTitle =>
        Text(IsCreatingWord ? "VocabWordEdit_AddTitle" : "VocabWordEdit_EditTitle");

    /// <summary>The list toolbar's Add (+) and the empty page's Add your first word.</summary>
    internal void AddWord() => Run(OpenNewWordAsync);

    internal void OnListAppeared() => Run(ReloadAsync);

    internal void OnListPopped() => Close();

    internal void OnFormPopped() => CloseWord();

    internal void CancelEdit() => CloseWord();

    /// <summary>The SwiftUI confirmation's Delete.</summary>
    internal void ConfirmDelete() => Run(ConfirmDeleteAsync);

    /// <summary>The Vocabulary page's own width, once it has reported one (an iPad sidebar takes part of the window).</summary>
    internal double? PageWidth { get; private set; }

    /// <summary>The width the page was last drawn for (its own, or the window's before it reported).</summary>
    private double? _drawnPageWidth;

    /// <summary>
    /// The window changed size: the reported width no longer holds, so the page is drawn for the
    /// window until it reports again (scene ViewportChanged).
    /// </summary>
    internal void ForgetPageWidth() => PageWidth = null;

    internal SwiftView RenderList(SourceUiStyle style)
    {
        _drawnPageWidth = PageWidth ?? style.ViewportWidth;
        return VocabularyListView.Page(ListState, style, Text,
            new VocabularyListActions(
                Search: Search,
                OpenFilters: OpenPhoneFilters,
                ToggleFilterPanel: ToggleFilterPanel,
                ClearFilters: ClearFilters,
                OpenWord: id => Run(() => OpenWordAsync(id)),
                Reload: () => Run(ReloadAsync),
                Sort: SetSort,
                // Read at tap time: the scene keeps a mounted toolbar button.
                ToggleListMode: () => SetListMode(!ListMode),
                ChangeWideFilter: ChangeWideFilter,
                OpenLookups: OpenLookups,
                Duplicates: new VocabularyDuplicateActions(
                    Close: CloseDuplicates,
                    ShowMore: () => Run(ShowMoreDuplicatesAsync),
                    Merge: term => Run(() => MergeDuplicatesAsync(term)),
                    EditRecord: id => Run(() => OpenWordAsync(id))),
                PageWidthChanged: OnPageWidthChanged,
                AddWord: AddWord,
                SearchField: SearchField,
                RemoveChip: RemoveChip),
            PageWidth);
    }

    /// <summary>
    /// The page reported its width (when it appears and as it changes). The phone or wide layout,
    /// the filter column and the card columns follow it, so the list redraws when the layout
    /// flips or the width moved 8 points or more from the one drawn (not on every frame of a
    /// window drag). Becoming wide closes the phone filter sheet, as crossing the window
    /// breakpoint does.
    /// </summary>
    private void OnPageWidthChanged(double width)
    {
        if (IsDisposed || !double.IsFinite(width) || width <= 0)
            return;
        PageWidth = width;
        var wide = VocabularyListView.IsWide(width);
        if (_drawnPageWidth is { } drawn && Math.Abs(drawn - width) < 8
            && VocabularyListView.IsWide(drawn) == wide)
            return;
        if (wide && IsPhoneFilterPresented)
            CancelPhoneFilters();
        Publish(list: true, form: false, sheet: false);
    }

    internal SwiftView RenderFilterSheet(SourceUiStyle style) =>
        VocabularyListView.PhoneFilterSheet(ListState, style, Text,
            new VocabularyFilterActions(
                Change: (type, value) => ChangeFilter(type, value, live: false),
                Sort: SetSheetSort,
                Apply: ApplyPhoneFilters, Cancel: CancelPhoneFilters,
                Clear: ClearFilters));

    internal SwiftView RenderForm(SourceUiStyle style)
    {
        _saveButtons.Clear();
        _sections.Clear();
        _exampleButtons.Clear();
        _formStyle = style;
        _formActions = new VocabularyEditActions(
            Change: ChangeDraft,
            ToggleResource: ToggleResource,
            Save: () => Run(SaveAsync),
            Cancel: CancelEdit,
            Retry: () => Run(RetryWordAsync),
            PlayAudio: () => Run(PlayAudioAsync),
            FindDuplicates: () => Run(FindDuplicatesAsync),
            RequestDelete: RequestDelete,
            RegisterSave: view => _saveButtons.Add(view),
            Examples: new VocabularyExampleActions(
                ChangeNew: ChangeNewExample,
                SetNewRegister: SetNewExampleRegister,
                Add: () => Run(AddExampleAsync),
                Generate: () => Run(GenerateExamplesAsync),
                ChangeSuggestion: ChangeSuggestion,
                Keep: key => Run(() => KeepSuggestionAsync(key)),
                Discard: DiscardSuggestion,
                StartEdit: StartEditExample,
                ChangeEdit: ChangeEditedExample,
                SetEditRegister: SetEditedExampleRegister,
                SaveEdit: () => Run(SaveEditedExampleAsync),
                CancelEdit: CancelEditExample,
                ToggleCore: id => Run(() => ToggleExampleCoreAsync(id)),
                ToggleFlag: id => Run(() => ToggleExampleFlagAsync(id)),
                Delete: id => Run(() => DeleteExampleAsync(id)),
                RegisterButton: (key, view) => _exampleButtons[key] = view),
            RegisterSection: (section, view) => _sections[section] = view);
        var form = VocabularyWordEditView.Page(EditState, style, Text, _formActions);
        UpdateSaveButtons();
        return form;
    }

    /// <summary>
    /// SwiftUI redraws a whole page from its version (and the page starts again at the top), so
    /// the editor is not rebuilt for what it already shows: typed text updates the Save and
    /// sentence buttons in place, a resource change or a sentence change refills only its card,
    /// and the scene updates the delete confirmation in place (rebuilding the editor under it
    /// would replay its presentation); a delete in progress refills both cards, so their
    /// controls show they wait. A sheet draft redraws nothing.
    /// </summary>
    protected override void OnChanged(VocabularyChanges changes)
    {
        if ((changes & (VocabularyChanges.DraftText | VocabularyChanges.DeleteDialog)) != 0)
            UpdateSaveButtons();
        var form = (changes & VocabularyChanges.Form) != 0;
        if (!form)
        {
            if ((changes & (VocabularyChanges.ResourceToggle | VocabularyChanges.DeleteDialog)) != 0)
                ScheduleRefill(VocabularyEditSection.Resources);
            if ((changes & (VocabularyChanges.Examples | VocabularyChanges.DeleteDialog)) != 0)
                ScheduleRefill(VocabularyEditSection.Examples);
            if ((changes & VocabularyChanges.ExampleText) != 0)
                UpdateExampleButtons();
        }
        var list = (changes & VocabularyChanges.List) != 0;
        var sheet = (changes & VocabularyChanges.Sheet) != 0;
        // The confirmation reads its state when the scene handles Changed.
        if (list || form || sheet || (changes & VocabularyChanges.DeleteDialog) != 0)
            Publish(list, form, sheet);
    }

    /// <summary>
    /// Refills a card on the next main-queue turn, not inside the tap that changed it: the
    /// card's own Button or Menu (Remove, a menu item, Discard) is still handling that tap, and
    /// disposing it then would pull its handler and close its menu mid-dispatch. Several
    /// changes in one turn draw the card once. A card the editor no longer shows redraws the
    /// whole page instead.
    /// </summary>
    private void ScheduleRefill(VocabularyEditSection section)
    {
        if (IsDisposed || !_pendingRefills.Add(section) || _pendingRefills.Count > 1)
            return;
        DispatchQueue.MainQueue.DispatchAsync(() =>
        {
            if (IsDisposed)
                return;
            var sections = _pendingRefills.ToArray();
            _pendingRefills.Clear();
            if (!IsFormOpen)
                return;
            var whole = false;
            foreach (var pending in sections)
                whole |= !RefillSection(pending);
            if (whole)
                Publish(list: false, form: true, sheet: false);
        });
    }

    /// <summary>
    /// Draws one card again in its slot, keeping the page and its scroll position. False when
    /// the editor is not drawn with that card (the page then redraws whole).
    /// </summary>
    private bool RefillSection(VocabularyEditSection section)
    {
        var state = EditState;
        if (IsDisposed || _formStyle is not { } style || _formActions is not { } actions
            || state.Detail is not { } detail || state.Draft is null
            || !_sections.TryGetValue(section, out var slot) || slot.Children.Count != 1)
            return false;
        SwiftView card;
        if (section == VocabularyEditSection.Examples)
        {
            _exampleButtons.Clear();
            card = VocabularyWordEditView.ExamplesCard(state, style, Text, actions.Examples);
            // The strength label counts the owner's sentences.
            if (_sections.TryGetValue(VocabularyEditSection.EncodingLabel, out var encoding))
                encoding.Text = VocabularyWordEditView.EncodingText(detail.Word.EncodingLabel, Text);
        }
        else
            card = VocabularyWordEditView.ResourceCard(detail, state, style, Text, actions);
        var previous = slot.Children[0];
        slot.RemoveAt(0);
        previous.Dispose();
        slot.Add(card);
        return true;
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

    /// <summary>The toolbar's plain Save follows the one Save rule as the learner types.</summary>
    private void UpdateSaveButtons()
    {
        if (IsDisposed)
            return;
        var enabled = CanSaveOpenWord;
        foreach (var button in _saveButtons)
            button.IsEnabled = enabled;
    }

    /// <summary>Add, an edit's Save and each Keep need a sentence (source), checked as the learner types.</summary>
    private void UpdateExampleButtons()
    {
        if (IsDisposed || _formStyle is not { } style || EditState is not
            { Examples: { } examples } state)
            return;
        var locked = state.Saving || state.Deleting || state.HandingOff;
        void Set(string key, bool enabled)
        {
            if (_exampleButtons.TryGetValue(key, out var button))
                SourceUiStyle.SetCustomButtonAvailability(button, style.Appearance,
                    SourceButtonRole.Primary, enabled);
        }
        Set(VocabularyWordEditView.AddExampleButton, !locked && examples.CanAdd);
        Set(VocabularyWordEditView.SaveEditedExampleButton, !locked && examples.CanSaveEdit);
        foreach (var suggestion in examples.Suggestions)
            Set(VocabularyWordEditView.KeepSuggestionButton(suggestion.Key),
                !locked && VocabularyExamplesState.CanKeep(examples.Writing, suggestion));
    }
}
