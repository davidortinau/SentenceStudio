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

    [ObservableProperty]
    string _delimiter = "comma";
    
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
        VocabularyList list = new VocabularyList
        {
            Name = VocabListName,
            Terms = ParseTerms(VocabList)
        };


        var listId = await _vocabService.SaveListAsync(list); // this saves the terms
        
        await Shell.Current.GoToAsync("..?refresh=true");
    }

    private List<Term> ParseTerms(string vocabList)
    {
        string delimiter = Delimiter == "tab" ? "\t" : ",";

        List<Term> terms = new List<Term>();
        string[] lines = vocabList.Split('\n');
        foreach (var line in lines)
        {
            string[] parts = line.Split(delimiter);
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
