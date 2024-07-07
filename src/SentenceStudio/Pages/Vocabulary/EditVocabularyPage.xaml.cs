using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SentenceStudio.Pages.Vocabulary;

public partial class EditVocabularyPage : ContentPage, INotifyPropertyChanged
{
    EditVocabularyPageModel _vm;
    List<VocabularyWord> _searchResults;

    int _currentSearchIndex = 0;

    string _searchResultsDisplay;
    public string SearchResultsDisplay
    {
        get => _searchResultsDisplay;
        set {
            if (_searchResultsDisplay != value)
            {
                _searchResultsDisplay = value;
                OnPropertyChanged(nameof(SearchResultsDisplay));
            }
        }
    }

    public EditVocabularyPage(EditVocabularyPageModel pageModel)
    {
        InitializeComponent();

        BindingContext = _vm =   pageModel;
    }

    void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _searchResults = _vm.Words.Where(w => w.TargetLanguageTerm.Contains(e.NewTextValue)).ToList();
        UpdateSearchResultsDisplay();

        if (_searchResults.Count > 0)
        {
            ScrollToWord(_searchResults[0]);
        }

    }

    private void UpdateSearchResultsDisplay()
    {
        SearchResultsDisplay = $"{_currentSearchIndex + 1} of {_searchResults.Count}";
    }

    void Find_Clicked(object sender, EventArgs e)
    {
        FindOnPageView.IsVisible = true;
        SearchEntry.Focus();
    }

    void OnDoneClicked(object sender, EventArgs e)
    {
        FindOnPageView.IsVisible = false;
        SearchEntry.Unfocus();
        _searchResults.Clear();
        _currentSearchIndex = 0;
        UpdateSearchResultsDisplay();
    }

    void OnUpClicked(object sender, EventArgs e)
    {
        _currentSearchIndex++;
        if (_currentSearchIndex >= _searchResults.Count)
        {
            _currentSearchIndex = 0;
        }

        ScrollToWord(_searchResults[_currentSearchIndex]);
        UpdateSearchResultsDisplay();
    }

    void OnDownClicked(object sender, EventArgs e)
    {
        _currentSearchIndex--;
        if (_currentSearchIndex < 0)
        {
            _currentSearchIndex = _searchResults.Count - 1;
        }

        ScrollToWord(_searchResults[_currentSearchIndex]);
        UpdateSearchResultsDisplay();
    }

    void ScrollToWord(VocabularyWord word)
    {
        WordsList.ScrollTo(word, position: ScrollToPosition.Center, animate: true);
    }
}