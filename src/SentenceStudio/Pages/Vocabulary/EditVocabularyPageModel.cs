using System.Diagnostics;
using CommunityToolkit.Mvvm.Input;
using SentenceStudio.Models;
using SentenceStudio.Services;
using Sharpnado.Tasks;

namespace SentenceStudio.Pages.Vocabulary;

[QueryProperty(nameof(ListId), "id")]
public partial class EditVocabularyPageModel : ObservableObject
{
    private VocabularyService _vocabService;

    [ObservableProperty] private int _listId;

    partial void OnListIdChanged(int id)
    {
        if (id > 0)
        {
            TaskMonitor.Create(LoadList);
        }
    }

    private VocabularyList _vocabList;
    
    [ObservableProperty]
    List<Term> _terms;

    [ObservableProperty]
    string _vocabListName;
    
    public EditVocabularyPageModel(IServiceProvider service)
    {
        _vocabService = service.GetRequiredService<VocabularyService>();
    }

    private async Task LoadList()
    {
        if (_listId <= 0)
            return; // display an alert that it cannot be found perhaps
        
        _vocabList = await _vocabService.GetListAsync(_listId);
        VocabListName = _vocabList.Name;
        Terms = _vocabList.Terms;
    }

    [RelayCommand]
    async Task SaveVocab()
    {
        _vocabList.Name = _vocabListName;
        _vocabList.Terms = _terms;
        
       var listId = await _vocabService.SaveListAsync(_vocabList);
        
        await Shell.Current.GoToAsync("..?refresh=true");
    }

    [RelayCommand]
    async Task Delete()
    {
        var result = await _vocabService.DeleteListAsync(_vocabList);
        try
        {
            await Shell.Current.GoToAsync($"..?refresh=true");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"{ex.Message}");
        }
    }
}
