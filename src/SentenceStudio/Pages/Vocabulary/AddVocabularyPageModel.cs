using LukeMauiFilePicker;

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
        //await Shell.Current.DisplayAlert("Oof", "Nope", "Okay");

        // persist here
        VocabularyList list = new VocabularyList
        {
            Name = VocabListName,
            Words = VocabularyWord.ParseVocabularyWords(VocabList, Delimiter)
        };


        var listId = await _vocabService.SaveListAsync(list); 
        
        await Shell.Current.GoToAsync("..?refresh=true");
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
