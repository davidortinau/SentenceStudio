using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SentenceStudio.Pages.Vocabulary;

public class EditVocabularyPage : ContentPage, INotifyPropertyChanged
{
    EditVocabularyPageModel _vm;
    List<VocabularyWord> _searchResults;
    CollectionView WordsList;
    ContentView FindOnPageView;
    Entry SearchEntry;
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
        BindingContext = _vm =   pageModel;

        Build();
    }

    public void Build()
    {
        this.Bind(Page.TitleProperty, nameof(EditVocabularyPageModel.VocabListName));
        this.HideSoftInputOnTapped = true;

        this.ToolbarItems.Add(new ToolbarItem { Text = "Find" }.OnClicked(Find_Clicked));
        this.ToolbarItems.Add(
            new ToolbarItem { Text = "Add" }
                .BindCommand(nameof(EditVocabularyPageModel.AddVocabCommand))
        );
        this.ToolbarItems.Add(
            new ToolbarItem { Text = "Save" }
                .BindCommand(nameof(EditVocabularyPageModel.SaveVocabCommand))
        );
        this.ToolbarItems.Add(
            new ToolbarItem { Text = "Delete" }
                .BindCommand(nameof(EditVocabularyPageModel.DeleteCommand))
        );

        Content = new Grid
        {
            Children =
            {
                new CollectionView
                    {                    
                        Header = WordsListHeader()
                    }
                    .Assign(out WordsList)
                    .Bind(CollectionView.ItemsSourceProperty, nameof(_vm.Words))
                    .ItemTemplate(WorksListItemTemplate()),

                CreateBottomSheet()
            }
        };
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

    private VerticalStackLayout WordsListHeader()
    {
        return new VerticalStackLayout
        {
            Spacing = (double)Application.Current.Resources["size120"], 
            Padding = (double)Application.Current.Resources["size240"],
            Children =
            {
                new VerticalStackLayout
                {
                    Spacing = (double)Application.Current.Resources["size120"],
                    Children =
                    {
                        new Label { }
                            .Start()
                            .Bind(Label.TextProperty, "Localize[ListName]"),
                        new Border
                            {
                                Style = (Style)Application.Current.Resources["InputWrapper"],
                                Content = new Entry()
                                            .Bind(Entry.TextProperty, nameof(EditVocabularyPageModel.VocabListName), BindingMode.TwoWay)
                            }
                    }
                },
                new VerticalStackLayout
                {
                    Spacing = (double)Application.Current.Resources["size120"],
                    Children =
                    {
                        new Label { }
                            .Start()
                            .Bind(Label.TextProperty, "Localize[Vocabulary]"),
                        new Grid
                            {
                                ColumnDefinitions = Columns.Define(Star,Star),
                                Padding = (double)Application.Current.Resources["size240"],
                                ColumnSpacing = (double)Application.Current.Resources["size240"],
                                Children =
                                {
                                    new Label { }
                                        .Bind(Label.TextProperty, "Localize[Term]"),
                                    new Label { }
                                        .Bind(Label.TextProperty, "Localize[Translation]")
                                        .Column(1)
                                }
                            }
                    }
                }
            }
        };
    }

    private DataTemplate WorksListItemTemplate()
    {
        return new DataTemplate(() =>                    
                new Grid
                {
                    Padding = (double)Application.Current.Resources["size240"],
                    ColumnDefinitions = Columns.Define(Star,Star,Auto),
                    ColumnSpacing = (double)Application.Current.Resources["size240"],
                    Children = {
                        new Border
                            {
                                Style = (Style)Application.Current.Resources["InputWrapper"],
                                Content = new Entry().Bind(Entry.TextProperty, "TargetLanguageTerm")
                            },
                        new Border
                            {
                                Style = (Style)Application.Current.Resources["InputWrapper"],
                                Content = new Entry().Bind(Entry.TextProperty, "NativeLanguageTerm")
                            }
                            .Column(1),
                        new Button
                            {
                                BackgroundColor = Colors.Transparent,
                            }
                            .Column(2)
                            .Center()
                            .Icon(SegoeFluentIcons.Delete)
                            .IconSize(24)
                            .BindCommand(nameof(EditVocabularyPageModel.DeleteVocabCommand), source: _vm, parameterPath: ".")
                            .AppThemeColorBinding(Button.TextColorProperty,
                                (Color)Application.Current.Resources["DarkOnLightBackground"],
                                (Color)Application.Current.Resources["LightOnDarkBackground"]
                            )

                    }
                }
            );
    }

    ContentView CreateBottomSheet()
    {
        return new ContentView
        {
            IsVisible = false,
            Content = new Grid
            {
                Padding = new Thickness(4, 8),
                
                ColumnDefinitions = Columns.Define(Auto,Star,Auto),
                ColumnSpacing = 4,
                Children =
                {
                    new Button
                        {
                            BackgroundColor = Colors.Transparent
                        }
                        .Bind(Button.TextProperty, "Localize[Done]")
                        .OnClicked(OnDoneClicked)
                        .AppThemeColorBinding(Button.TextColorProperty,
                            (Color)Application.Current.Resources["DarkOnLightBackground"],
                            (Color)Application.Current.Resources["LightOnDarkBackground"]
                        )
                        .Column(0),

                    new Border
                    {
                        Content = new Grid
                        {
                            ColumnDefinitions = Columns.Define(Star,Auto),
                            ColumnSpacing = 4,
                            Children =
                            {
                                new Entry
                                    {
                                        Placeholder = "Search...",
                                        IsSpellCheckEnabled = false,
                                        IsTextPredictionEnabled = false,
                                        ReturnType = ReturnType.Search
                                    }
                                    .OnTextChanged(OnSearchTextChanged)
                                    .Assign(out SearchEntry),

                                new Label
                                    {
                                        FontSize = 10,
                                        Margin = new Thickness(0, 0, 8, 0)
                                    }
                                    .Column(1)
                                    .End()
                                    .CenterVertical()
                                    .TextEnd()
                                    .Bind(Label.TextProperty, nameof(EditVocabularyPage.SearchResultsDisplay), source: this)
                            }
                        }
                    }.Column(1),

                    new HorizontalStackLayout
                        {
                            Spacing = 0,
                            Children =
                            {
                                new Button
                                    {
                                        BackgroundColor = Colors.Transparent
                                    }
                                    .Icon(SegoeFluentIcons.ChevronUp)
                                    .IconSize(24)
                                    .AppThemeColorBinding(Button.TextColorProperty,
                                        (Color)Application.Current.Resources["LightOnDarkBackground"],
                                        (Color)Application.Current.Resources["DarkOnLightBackground"]
                                    )
                                    .CenterVertical()
                                    .Size(40, 40)
                                    .OnClicked(OnDownClicked),

                                new Button
                                    {
                                        BackgroundColor = Colors.Transparent
                                    }
                                    .Icon(SegoeFluentIcons.ChevronDown)
                                    .IconSize(24)
                                    .AppThemeColorBinding(Button.TextColorProperty,
                                        (Color)Application.Current.Resources["LightOnDarkBackground"],
                                        (Color)Application.Current.Resources["DarkOnLightBackground"]
                                    )
                                    .CenterVertical()
                                    .Size(40, 40)
                                    .OnClicked(OnUpClicked)
                            }
                        }
                        .End()
                        .Column(2)
                }
            }
            .AppThemeColorBinding(BackgroundColorProperty,
                (Color)Application.Current.Resources["Gray100"],
                (Color)Application.Current.Resources["Gray900"]
            )
        }
        .Bottom()
        .Assign(out FindOnPageView);
    }
}