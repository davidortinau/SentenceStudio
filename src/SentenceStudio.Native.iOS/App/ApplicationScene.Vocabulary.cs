using Microsoft.SwiftUI;
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
    private long _renderedVocabularyListVersion = -1;
    private long _renderedVocabularyFormVersion = -1;
    private long _renderedVocabularySheetVersion = -1;

    private void OnVocabularyRoutesChanged(int count)
    {
        if (count == 0 && _vocabularySlot is not null)
        {
            DropVocabularyReferences();
            _app.OnVocabularyListPopped();
        }
        else if (count == 1 && _vocabularyWordSlot is not null)
        {
            DropVocabularyWordReferences();
            _app.OnVocabularyWordPopped();
        }
    }

    private void UpdateVocabulary(bool appearanceChanged)
    {
        if (!_app.IsVocabularyOpen)
            return;
        var stack = _app.VocabularyOriginTab == 0
            ? _dashboardStack : _resourcesStack;
        if (stack is null)
            return;
        if (_vocabularySlot is null && stack.NavigationPath.Count == 0)
        {
            _vocabularySlot = Views.VStack(_app.RenderVocabularyList(_style))
                .WithNavigationTitle(_app.Localize("Vocabulary_Title"))
                .WithInlineNavigationTitle();
            _vocabularySearch = Find(_vocabularySlot, "native.vocabulary.search");
            _vocabularyFilterSlot = Views.VStack(
                _app.RenderVocabularyFilters(_style));
            _vocabularySheet = Views.Sheet(_vocabularySlot,
                    _vocabularyFilterSlot, _app.CancelVocabularyFilters)
                .WithSheetDetentFraction(0.85)
                .WithTabBarHidden()
                .WithAccessibilityIdentifier("native.vocabulary.filters.host");
            _renderedVocabularyListVersion = _app.VocabularyListVersion;
            _renderedVocabularySheetVersion = _app.VocabularySheetVersion;
            stack.Push(ApplicationFormRoutes.Vocabulary, _vocabularySheet);
        }
        if (_vocabularySlot is null || _vocabularySheet is null
            || _vocabularyFilterSlot is null)
            return;
        if (appearanceChanged
            || _renderedVocabularyListVersion != _app.VocabularyListVersion)
        {
            UpdateVocabularyList(appearanceChanged);
            _renderedVocabularyListVersion = _app.VocabularyListVersion;
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
            _vocabularyWordSlot = Views.VStack(
                    _app.RenderVocabularyWord(_style))
                .WithNavigationTitle(_app.VocabularyWordTitle)
                .WithInlineNavigationTitle()
                .WithTabBarHidden();
            _renderedVocabularyFormVersion = _app.VocabularyFormVersion;
            stack.Push(ApplicationFormRoutes.VocabularyWordForm,
                _vocabularyWordSlot);
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
    }

    private void UpdateVocabularyList(bool replaceAll = false)
    {
        if (_vocabularySlot is null)
            return;
        var next = _app.RenderVocabularyList(_style);
        var current = _vocabularySlot.Children.Single();
        var oldSearch = Find(current, "native.vocabulary.search");
        if (replaceAll || current.Kind != "toolbar" || next.Kind != "toolbar"
            || current.Children.Count == 0 || next.Children.Count == 0
            || !SameToolbarActions(current, next)
            || current.Children[0].Kind != "scroll"
            || next.Children[0].Kind != "scroll"
            || current.Children[0].Children.Count != 1
            || next.Children[0].Children.Count != 1)
        {
            var focused = oldSearch?.FocusRequested == true;
            Replace(_vocabularySlot, next);
            _vocabularySearch = Find(_vocabularySlot, "native.vocabulary.search");
            if (focused && _vocabularySearch?.IsEnabled == true)
                _vocabularySearch.FocusRequested = true;
            return;
        }

        var content = current.Children[0].Children[0];
        var nextContent = next.Children[0].Children[0];
        var searchRow = content.Children.FirstOrDefault();
        var nextSearchRow = nextContent.Children.FirstOrDefault();
        if (content.Kind != "vstack" || nextContent.Kind != "vstack"
            || searchRow?.Kind != "hstack" || nextSearchRow?.Kind != "hstack"
            || searchRow.Children.Count == 0 || nextSearchRow.Children.Count == 0
            || searchRow.Children[0].Kind != "textField"
            || nextSearchRow.Children[0].Kind != "textField"
            || searchRow.Children[0].AccessibilityIdentifier
                != "native.vocabulary.search")
        {
            var focused = oldSearch?.FocusRequested == true;
            Replace(_vocabularySlot, next);
            _vocabularySearch = Find(_vocabularySlot, "native.vocabulary.search");
            if (focused && _vocabularySearch?.IsEnabled == true)
                _vocabularySearch.FocusRequested = true;
            return;
        }

        var search = searchRow.Children[0];
        if (!string.Equals(search.Text, _app.VocabularySearchText,
                StringComparison.Ordinal))
            search.Text = _app.VocabularySearchText;
        while (searchRow.Children.Count > 1)
        {
            var old = searchRow.Children[1];
            searchRow.RemoveAt(1);
            old.Dispose();
        }
        while (nextSearchRow.Children.Count > 1)
        {
            var child = nextSearchRow.Children[1];
            nextSearchRow.RemoveAt(1);
            searchRow.Add(child);
        }
        while (content.Children.Count > 1)
        {
            var old = content.Children[1];
            content.RemoveAt(1);
            old.Dispose();
        }
        while (nextContent.Children.Count > 1)
        {
            var child = nextContent.Children[1];
            nextContent.RemoveAt(1);
            content.Add(child);
        }
        for (var actionIndex = 1; actionIndex < current.Children.Count;
            actionIndex++)
        {
            var action = current.Children[actionIndex];
            var nextAction = next.Children[actionIndex];
            if (action.IsEnabled != nextAction.IsEnabled)
                action.IsEnabled = nextAction.IsEnabled;
            if (action.Text != nextAction.Text)
                action.Text = nextAction.Text;
            for (var index = 0; index < action.Children.Count; index++)
            {
                var child = action.Children[index];
                var nextChild = nextAction.Children[index];
                if (child.IsEnabled != nextChild.IsEnabled)
                    child.IsEnabled = nextChild.IsEnabled;
                if (child.Text != nextChild.Text)
                    child.Text = nextChild.Text;
            }
        }
        _vocabularySearch = search;
        next.Dispose();
    }

    private void UpdateVocabularyWord()
    {
        if (_vocabularyWordSlot is null)
            return;
        var focusedId = FindFocusedInput(_vocabularyWordSlot)
            ?.AccessibilityIdentifier;
        Replace(_vocabularyWordSlot, _app.RenderVocabularyWord(_style));
        _renderedVocabularyFormVersion = _app.VocabularyFormVersion;
        if (!string.IsNullOrEmpty(focusedId)
            && Find(_vocabularyWordSlot, focusedId) is { IsEnabled: true } input)
            input.FocusRequested = true;
    }

    private void DropVocabularyWordReferences()
    {
        _vocabularyWordSlot = null;
        _renderedVocabularyFormVersion = -1;
    }

    private void DropVocabularyReferences()
    {
        DropVocabularyWordReferences();
        _vocabularySlot = null;
        _vocabularySearch = null;
        _vocabularyFilterSlot = null;
        _vocabularySheet = null;
        _renderedVocabularyListVersion = -1;
        _renderedVocabularySheetVersion = -1;
    }
}
