using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Features.Browse;
using SentenceStudio.Native.iOS.Features.Vocabulary;
using SentenceStudio.Native.iOS.Styling;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.App;

internal sealed partial class ApplicationScene
{
    private SwiftView? _vocabularySlot;
    private SwiftView? _vocabularySearch;
    private SwiftView? _vocabularyFilterSlot;
    private SwiftView? _vocabularySheet;
    private SwiftView? _vocabularyWordSlot;
    // The word page inside the slot, and the delete confirmation around it once the word
    // has loaded (source showDeleteConfirm; its message names the word).
    private SwiftView? _vocabularyWordContent;
    private SwiftView? _vocabularyDeleteConfirmation;
    private string? _vocabularyDeleteMessage;
    private long _renderedVocabularyListVersion = -1;
    // The sort and grid/list choice the mounted page shows: another one mounts a new page, so
    // its results start at the top instead of keeping an offset into different cards.
    private string? _renderedVocabularyArrangement;
    private long _renderedVocabularyFormVersion = -1;
    private long _renderedVocabularySheetVersion = -1;

    /// <summary>
    /// System Back changed the path: each Vocabulary page that left it closes. The word editor can
    /// sit above the duplicate page (its Edit), so Back from it leaves the duplicate page open.
    /// </summary>
    private void OnVocabularyRoutesChanged(IReadOnlyList<string> routes)
    {
        if (_vocabularySlot is not null && !routes.Contains(ApplicationFormRoutes.Vocabulary))
        {
            if (_lookupsSlot is not null)
            {
                DropRecentLookupsReferences();
                _app.OnRecentLookupsPopped();
            }
            DropVocabularyReferences();
            _app.OnVocabularyListPopped();
            return;
        }
        if (_vocabularyWordSlot is not null && !routes.Contains(ApplicationFormRoutes.VocabularyWordForm))
        {
            DropVocabularyWordReferences();
            _app.OnVocabularyWordPopped();
        }
        if (_lookupsSlot is not null && !routes.Contains(ApplicationFormRoutes.RecentLookups))
        {
            DropRecentLookupsReferences();
            _app.OnRecentLookupsPopped();
        }
        if (_duplicatesSlot is not null && !routes.Contains(ApplicationFormRoutes.VocabularyDuplicates))
        {
            DropDuplicatesReferences();
            _app.OnVocabularyDuplicatesPopped();
        }
    }

    // The duplicate page (Captain 2026-11-14: "The cramped space in the split view doesn't
    // work"): pushed above the Vocabulary list, so the Library split view shows it in the detail
    // column beside the list (with Full screen), and a phone shows it full width.
    private SwiftView? _duplicatesSlot;
    private VocabularyDuplicatePanel? _renderedDuplicates;
    private SourceUiStyle? _renderedDuplicatesStyle;

    private void UpdateVocabularyDuplicates(ISceneNavigation stack, bool appearanceChanged)
    {
        var panel = _app.VocabularyDuplicates;
        if (panel is not null && _duplicatesSlot is null)
        {
            // It waits for the editor to leave (Find Duplicates closes it first).
            if (stack.NavigationPath.Count != 1 || _vocabularyWordSlot is not null
                || _lookupsSlot is not null)
                return;
            _duplicatesSlot = Views.VStack(_app.RenderVocabularyDuplicates(_style))
                .WithNavigationTitle(_app.VocabularyDuplicatesTitle)
                .WithInlineNavigationTitle();
            RecordDuplicates(panel);
            stack.Push(ApplicationFormRoutes.VocabularyDuplicates, _duplicatesSlot);
        }
        else if (panel is null && _duplicatesSlot is not null)
        {
            // Closed without a Back (a word opened from the list beside it): leave the page too.
            // Under an editor (its Edit), or while Back is blocked, the reference stays, so the
            // page leaves once it is on top again rather than stay on the stack, dead.
            var path = stack.NavigationPath;
            if (path.Contains(ApplicationFormRoutes.VocabularyDuplicates)
                && (path[^1] != ApplicationFormRoutes.VocabularyDuplicates || !stack.CanNavigateBack))
                return;
            var onTop = path.Count > 0 && path[^1] == ApplicationFormRoutes.VocabularyDuplicates;
            // Clear first: the route callback must not report this pop as a person's Back.
            DropDuplicatesReferences();
            if (onTop)
                stack.Pop();
        }
        else if (panel is not null && _duplicatesSlot is not null
            && (appearanceChanged || !ReferenceEquals(panel, _renderedDuplicates)
                || !ReferenceEquals(_style, _renderedDuplicatesStyle)))
        {
            // A merge or Skip swaps the Sections into the mounted Form, so the page keeps its
            // scroll position; a new appearance or style draws it whole.
            var next = _app.RenderVocabularyDuplicates(_style);
            var reshaped = appearanceChanged || !ReferenceEquals(_style, _renderedDuplicatesStyle);
            if (reshaped || !TrySwapFormSections(_duplicatesSlot, next, VocabularyDuplicatesView.FormId))
                Replace(_duplicatesSlot, next);
            _duplicatesSlot.WithNavigationTitle(_app.VocabularyDuplicatesTitle);
            RecordDuplicates(panel);
        }
    }

    private void RecordDuplicates(VocabularyDuplicatePanel panel)
    {
        _renderedDuplicates = panel;
        _renderedDuplicatesStyle = _style;
    }

    private void DropDuplicatesReferences()
    {
        _duplicatesSlot = null;
        _renderedDuplicates = null;
        _renderedDuplicatesStyle = null;
    }

    // Recent lookups (decision 4 B): one page pushed above the Vocabulary list.
    private SwiftView? _lookupsSlot;
    private int _renderedLookupsVersion = -1;

    private void UpdateRecentLookups(ISceneNavigation stack, bool appearanceChanged)
    {
        if (_app.IsRecentLookupsOpen && _lookupsSlot is null
            && stack.NavigationPath.Count == 1 && _vocabularyWordSlot is null)
        {
            // A browse list: the tab bar stays.
            _lookupsSlot = Views.VStack(_app.RenderRecentLookups(_style))
                .WithNavigationTitle(_app.Localize("Lookups_Title"))
                .WithInlineNavigationTitle();
            _renderedLookupsVersion = _app.RecentLookupsViewVersion;
            stack.Push(ApplicationFormRoutes.RecentLookups,
                _lookupsSlot);
        }
        else if (_lookupsSlot is not null
            && (appearanceChanged || _renderedLookupsVersion != _app.RecentLookupsViewVersion))
        {
            Replace(_lookupsSlot, _app.RenderRecentLookups(_style));
            _renderedLookupsVersion = _app.RecentLookupsViewVersion;
        }
    }

    private void DropRecentLookupsReferences()
    {
        _lookupsSlot = null;
        _renderedLookupsVersion = -1;
    }

    private void UpdateVocabulary(bool appearanceChanged)
    {
        if (!_app.IsVocabularyOpen)
            return;
        // The Library list's Vocabulary row pushes the list; the word editor sits above it.
        var stack = _libraryStack;
        if (stack is null)
            return;
        if (_vocabularySlot is null && stack.NavigationPath.Count == 0)
        {
            _vocabularySlot = Views.VStack(_app.RenderVocabularyList(_style))
                .WithNavigationTitle(_app.Localize("Vocabulary_Title"))
                .WithNavigationSubtitle(_app.VocabularySubtitle)
                .WithInlineNavigationTitle();
            _vocabularySearch = Find(_vocabularySlot, BrowserPageUpdate.Vocabulary.SearchId);
            _vocabularyFilterSlot = Views.VStack(
                _app.RenderVocabularyFilters(_style));
            // A browse page: the tab bar stays (the filters sheet covers it while open).
            _vocabularySheet = Views.Sheet(_vocabularySlot,
                    _vocabularyFilterSlot, _app.CancelVocabularyFilters)
                .WithSheetDetentFraction(0.85)
                .WithAccessibilityIdentifier("native.vocabulary.filters.host");
            _renderedVocabularyListVersion = _app.VocabularyListVersion;
            _renderedVocabularyArrangement = _app.VocabularyArrangement;
            _renderedVocabularySheetVersion = _app.VocabularySheetVersion;
            stack.Push(ApplicationFormRoutes.Vocabulary,
                _vocabularySheet);
        }
        if (_vocabularySlot is null || _vocabularySheet is null
            || _vocabularyFilterSlot is null)
            return;
        if (appearanceChanged
            || _renderedVocabularyListVersion != _app.VocabularyListVersion)
        {
            var arrangement = _app.VocabularyArrangement;
            UpdateVocabularyList(appearanceChanged
                || arrangement != _renderedVocabularyArrangement);
            _renderedVocabularyListVersion = _app.VocabularyListVersion;
            _renderedVocabularyArrangement = arrangement;
        }
        if (appearanceChanged
            || _renderedVocabularySheetVersion != _app.VocabularySheetVersion)
        {
            Replace(_vocabularyFilterSlot, _app.RenderVocabularyFilters(_style));
            _renderedVocabularySheetVersion = _app.VocabularySheetVersion;
        }
        _vocabularySheet.IsPresented = _app.IsVocabularyFilterPresented;

        // A word opened from the list beside the duplicate page (the split view): the page
        // gives the detail column to the word, as another list row would. A word the page's own
        // Edit opened goes above the page instead, and Back returns to it (Captain 2026-11-14).
        if (_app.IsVocabularyWordOpen && _duplicatesSlot is not null && _vocabularyWordSlot is null
            && !_app.IsVocabularyWordFromDuplicates)
            _app.CloseVocabularyDuplicates();
        UpdateVocabularyDuplicates(stack, appearanceChanged);

        // Above the duplicate page only for a word its Edit opened, while the page is still open;
        // otherwise the editor waits for that page to leave (its close can wait while Back is
        // blocked).
        var path = stack.NavigationPath;
        if (_app.IsVocabularyWordOpen && _vocabularyWordSlot is null
            && (path.Count == 1 || _duplicatesSlot is not null && path.Count == 2
                && path[^1] == ApplicationFormRoutes.VocabularyDuplicates
                && _app.IsVocabularyWordFromDuplicates && _app.VocabularyDuplicates is not null))
        {
            _vocabularyWordContent = Views.VStack(_app.RenderVocabularyWord(_style));
            _vocabularyDeleteMessage = _app.VocabularyDeleteMessage;
            _vocabularyDeleteConfirmation = VocabularyDeleteConfirmation(
                _vocabularyWordContent, _vocabularyDeleteMessage);
            _vocabularyWordSlot = Views.VStack(
                    _vocabularyDeleteConfirmation ?? _vocabularyWordContent)
                .WithNavigationTitle(_app.VocabularyWordTitle)
                .WithInlineNavigationTitle()
                .WithTabBarHidden();
            _renderedVocabularyFormVersion = _app.VocabularyFormVersion;
            stack.Push(ApplicationFormRoutes.VocabularyWordForm,
                WithSamToolbar(stack, ApplicationFormRoutes.VocabularyWordForm, _vocabularyWordSlot));
        }
        else if (ApplicationFormRoutes.ShouldPopVocabularyWordForm(
            _app.IsVocabularyWordOpen, _vocabularyWordSlot is not null,
            stack.CanNavigateBack, stack.NavigationPath))
        {
            stack.Pop();
            DropVocabularyWordReferences();
        }
        else if (_vocabularyWordSlot is not null
            && (appearanceChanged
                || _renderedVocabularyFormVersion != _app.VocabularyFormVersion))
            UpdateVocabularyWord();
        if (_vocabularyDeleteConfirmation is not null)
        {
            var asking = _app.IsVocabularyDeletePresented && !_app.IsVocabularyDeletePending;
            if (_vocabularyDeleteConfirmation.IsPresented != asking)
                _vocabularyDeleteConfirmation.IsPresented = asking;
        }

        // Find Duplicates in the editor: the editor has left in this update, so the page can open.
        UpdateVocabularyDuplicates(stack, appearanceChanged);
        UpdateRecentLookups(stack, appearanceChanged);
    }

    private void UpdateVocabularyList(bool replaceAll = false)
    {
        if (_vocabularySlot is null)
            return;
        var next = _app.RenderVocabularyList(_style);
        // After the render: the subtitle reads the state the list was drawn with.
        _vocabularySlot.WithNavigationSubtitle(_app.VocabularySubtitle);
        var current = _vocabularySlot.Children.Single();
        var oldSearch = Find(current, BrowserPageUpdate.Vocabulary.SearchId);
        if (replaceAll || oldSearch is null || !BrowserPageUpdate.Vocabulary.TryUpdateInPlace(current, next))
        {
            // Another layout (phone or wide, the filter column shown or hidden, the wide page
            // loading, the duplicate manager opened): a new page, with focus handed to its search
            // field. One column keeps its List through loading, errors and no matches.
            var focused = oldSearch?.FocusRequested == true;
            Replace(_vocabularySlot, next);
            _vocabularySearch = Find(_vocabularySlot, BrowserPageUpdate.Vocabulary.SearchId);
            if (focused && _vocabularySearch?.IsEnabled == true)
                _vocabularySearch.FocusRequested = true;
            return;
        }
        // The fresh page's field holds the text for its layout: the whole query on the wide
        // page, the words beside the filter tokens in the system search field.
        var text = Find(next, BrowserPageUpdate.Vocabulary.SearchId)?.Text
            ?? _app.VocabularySearchText;
        if (!string.Equals(oldSearch.Text, text, StringComparison.Ordinal))
            oldSearch.Text = text;
        _vocabularySearch = oldSearch;
        next.Dispose();
    }

    private void UpdateVocabularyWord()
    {
        if (_vocabularyWordSlot is null || _vocabularyWordContent is null)
            return;
        var focusedId = FindFocusedInput(_vocabularyWordSlot)
            ?.AccessibilityIdentifier;
        var message = _app.VocabularyDeleteMessage;
        if (message is not null && message != _vocabularyDeleteMessage)
        {
            // The word loaded (or another one did): the confirmation is rebuilt around a
            // new page, once, while it is not presented.
            var content = Views.VStack(_app.RenderVocabularyWord(_style));
            var confirmation = VocabularyDeleteConfirmation(content, message)!;
            Replace(_vocabularyWordSlot, confirmation);
            _vocabularyWordContent = content;
            _vocabularyDeleteConfirmation = confirmation;
            _vocabularyDeleteMessage = message;
        }
        else
            Replace(_vocabularyWordContent, _app.RenderVocabularyWord(_style));
        _renderedVocabularyFormVersion = _app.VocabularyFormVersion;
        if (!string.IsNullOrEmpty(focusedId)
            && Find(_vocabularyWordSlot, focusedId) is { IsEnabled: true } input)
            input.FocusRequested = true;
    }

    private SwiftView? VocabularyDeleteConfirmation(SwiftView content, string? message) =>
        message is null ? null
            : SystemDeleteDialog(content, message, _app.CancelVocabularyDelete,
                _app.ConfirmVocabularyDelete,
                () => _app.IsVocabularyDeletePresented && !_app.IsVocabularyDeletePending,
                "native.vocabulary.edit.delete");

    private void DropVocabularyWordReferences()
    {
        _vocabularyWordSlot = null;
        _vocabularyWordContent = null;
        _vocabularyDeleteConfirmation = null;
        _vocabularyDeleteMessage = null;
        _renderedVocabularyFormVersion = -1;
    }

    private void DropVocabularyReferences()
    {
        DropVocabularyWordReferences();
        DropRecentLookupsReferences();
        DropDuplicatesReferences();
        _vocabularySlot = null;
        _vocabularySearch = null;
        _vocabularyFilterSlot = null;
        _vocabularySheet = null;
        _renderedVocabularyListVersion = -1;
        _renderedVocabularyArrangement = null;
        _renderedVocabularySheetVersion = -1;
    }
}
