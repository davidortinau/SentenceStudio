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

    private void OnVocabularyRoutesChanged(int count)
    {
        if (count == 0 && _vocabularySlot is not null)
        {
            if (_lookupsSlot is not null)
            {
                DropRecentLookupsReferences();
                _app.OnRecentLookupsPopped();
            }
            DropVocabularyReferences();
            _app.OnVocabularyListPopped();
        }
        else if (count == 1 && _vocabularyWordSlot is not null)
        {
            DropVocabularyWordReferences();
            _app.OnVocabularyWordPopped();
        }
        else if (count == 1 && _lookupsSlot is not null)
        {
            DropRecentLookupsReferences();
            _app.OnRecentLookupsPopped();
        }
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

        if (_app.IsVocabularyWordOpen && _vocabularyWordSlot is null
            && stack.NavigationPath.Count == 1)
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
            _vocabularyDeleteConfirmation.IsPresented = _app.IsVocabularyDeletePresented;
            _vocabularyDeleteConfirmation.DeletePending = _app.IsVocabularyDeletePending;
        }

        UpdateRecentLookups(stack, appearanceChanged);
    }

    private void UpdateVocabularyList(bool replaceAll = false)
    {
        if (_vocabularySlot is null)
            return;
        var next = _app.RenderVocabularyList(_style);
        var current = _vocabularySlot.Children.Single();
        var oldSearch = Find(current, BrowserPageUpdate.Vocabulary.SearchId);
        if (replaceAll || oldSearch is null || !BrowserPageUpdate.Vocabulary.TryUpdateInPlace(current, next))
        {
            // Another layout (phone or wide, the filter column shown or hidden, loading or
            // loaded): a new page, with focus handed to its search field.
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
            : Views.ResourceDeleteConfirmation(content,
                    _app.Localize("VocabWordEdit_ConfirmDelete"), message,
                    _app.Localize("Common_Close"),
                    _app.Localize("Common_Cancel"),
                    _app.Localize("Common_Delete"),
                    _app.CancelVocabularyDelete,
                    _app.ConfirmVocabularyDelete)
                .WithTransparentDeleteBackdrop()
                .WithAccessibilityIdentifier("native.vocabulary.edit.delete");

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
        _vocabularySlot = null;
        _vocabularySearch = null;
        _vocabularyFilterSlot = null;
        _vocabularySheet = null;
        _renderedVocabularyListVersion = -1;
        _renderedVocabularyArrangement = null;
        _renderedVocabularySheetVersion = -1;
    }
}
