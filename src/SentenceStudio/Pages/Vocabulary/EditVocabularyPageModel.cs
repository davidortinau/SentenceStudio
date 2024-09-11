using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.Input;
using SentenceStudio.Models;
using SentenceStudio.Services;
using Sharpnado.Tasks;

namespace SentenceStudio.Pages.Vocabulary;

[QueryProperty(nameof(ListId), "id")]
public partial class EditVocabularyPageModel : ObservableObject
{
    public LocalizationManager Localize => LocalizationManager.Instance;

    private VocabularyService _vocabService;

    [ObservableProperty] private int _listId;

    partial void OnListIdChanged(int value)
    {
        if (value > 0)
        {
            TaskMonitor.Create(LoadList);
        }
    }

    private VocabularyList _vocabList;
    
    [ObservableProperty]
    ObservableCollection<VocabularyWord> _words;

    [ObservableProperty]
    string _vocabListName;
    
    public EditVocabularyPageModel(IServiceProvider service)
    {
        _vocabService = service.GetRequiredService<VocabularyService>();
    }

    private async Task LoadList()
    {
        if (ListId <= 0)
            return; // display an alert that it cannot be found perhaps
        
        _vocabList = await _vocabService.GetListAsync(ListId);
        VocabListName = _vocabList.Name;
        Words = new ObservableCollection<VocabularyWord>(_vocabList.Words);
    }

    [RelayCommand]
    async Task SaveVocab()
    {
        _vocabList.Name = VocabListName;
        _vocabList.Words = Words.ToList();
        
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

    [RelayCommand]
    async Task DeleteVocab(VocabularyWord word)
    {
        Words.Remove(word);
        await _vocabService.DeleteWordAsync(word);
        await _vocabService.DeleteWordFromListAsync(word, _vocabList.ID);
    }

    [RelayCommand]
    async Task AddVocab()
    {
        var word = new VocabularyWord();
        Words.Insert(0, word);
        await _vocabService.SaveWordAsync(word);
        await _vocabService.SaveWordToListAsync(word, _vocabList.ID);
    }
}
