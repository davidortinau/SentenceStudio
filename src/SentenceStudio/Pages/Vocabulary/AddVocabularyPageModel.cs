using CommunityToolkit.Mvvm.Input;
using LukeMauiFilePicker;
using SentenceStudio.Models;
using SentenceStudio.Services;

namespace SentenceStudio.Pages.Vocabulary;

public partial class AddVocabularyPageModel : ObservableObject
{
    [ObservableProperty]
    string _vocabList;

    [ObservableProperty]
    string _vocabListName;
    
    readonly IFilePickerService picker;
    
    static readonly Dictionary<DevicePlatform, IEnumerable<string>> FileType = new()
    {
        {  DevicePlatform.Android, new[] { "text/*" } } ,
        { DevicePlatform.iOS, new[] { "public.json", "public.plain-text" } },
        { DevicePlatform.MacCatalyst, new[] { "public.json", "public.plain-text" } },
        { DevicePlatform.WinUI, new[] { ".txt", ".json" } }
    };

    public AddVocabularyPageModel(IFilePickerService picker, IServiceProvider service)
    {
        this.picker = picker;
        _vocabService = service.GetRequiredService<VocabularyService>();
    }

    private VocabularyService _vocabService;

    [RelayCommand]
    async Task SaveVocab()
    {
        // persist here
        VocabularyList list = new VocabularyList();
        list.Name = _vocabListName;
        list.Terms = ParseTerms(_vocabList);
        
        
       var listId = await _vocabService.SaveListAsync(list); // this saves the terms
        
        await Shell.Current.GoToAsync("..?refresh=true");
    }

    private List<Term> ParseTerms(string vocabList)
    {
        List<Term> terms = new List<Term>();
        string[] lines = vocabList.Split('\n');
        foreach (var line in lines)
        {
            string[] parts = line.Split(',');
            if (parts.Length == 2)
            {
                terms.Add(new Term
                {
                    TargetLanguageTerm  = parts[0], 
                    NativeLanguageTerm = parts[1]
                });
            }
        }
        return terms;
    }

    [RelayCommand]
    async Task ChooseFile()
    {
        /*FileResult? file = await FilePicker.PickAsync(new PickOptions
        {
            PickerTitle = "Pick a file",
            FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
            {
                { DevicePlatform.iOS, new[] { "public.text" } },
                { DevicePlatform.Android, new[] { "text/plain" } },
                { DevicePlatform.WinUI, new[] { ".txt",".csv" } },
                { DevicePlatform.MacCatalyst, new[] { "public.text" } }
            })
        });*/
        
        var file = await picker.PickFileAsync("Select a file", FileType);

        if (file != null)
        {
            var stream = await file.OpenReadAsync();
            using (var reader = new StreamReader(stream))
            {
                VocabList = await reader.ReadToEndAsync();
            }
        }
    }
}
