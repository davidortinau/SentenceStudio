using Fonts;
using LukeMauiFilePicker;

namespace SentenceStudio.Pages.Vocabulary;

class AddVocabularyPageState
{
    public string VocabListName { get; set; } = string.Empty;
    public string VocabList { get; set; } = string.Empty;
    public string Delimiter { get; set; } = "comma";
}

partial class AddVocabularyPage : Component<AddVocabularyPageState>
{
    [Inject] VocabularyService _vocabService;
    [Inject] IFilePickerService _picker;
    LocalizationManager _localize => LocalizationManager.Instance;

    static readonly Dictionary<DevicePlatform, IEnumerable<string>> FileType = new()
    {
        { DevicePlatform.Android, new[] { "text/*" } },
        { DevicePlatform.iOS, new[] { "public.json", "public.plain-text" } },
        { DevicePlatform.MacCatalyst, new[] { "public.json", "public.plain-text" } },
        { DevicePlatform.WinUI, new[] { ".txt", ".json" } }
    };

    public override VisualNode Render()
    {
        return ContentPage($"{_localize["AddVocabularyList"]}",
			
			VScrollView(
				VStack(
					new SfTextInputLayout{
						Entry()
							.Text(State.VocabListName)
							.OnTextChanged(text => SetState(s => s.VocabListName = text))
						}
						.ContainerType(Syncfusion.Maui.Toolkit.TextInputLayout.ContainerType.Filled)
						.ContainerBackground(Colors.White)
						.Hint($"{_localize["ListName"]}"),

					new SfTextInputLayout{
						Editor()
							.Text(State.VocabList)
							.OnTextChanged(text => SetState(s => s.VocabList = text))
							.MinimumHeightRequest(400)
							.MaximumHeightRequest(600)
						}
						.ContainerType(Syncfusion.Maui.Toolkit.TextInputLayout.ContainerType.Filled)
						.ContainerBackground(Colors.White)
						.Hint($"{_localize["Vocabulary"]}"),

					Button()
						.ImageSource(SegoeFluentIcons.FileExplorer.ToImageSource())
						.Background(Colors.Transparent)
						.HEnd()
						.OnClicked(ChooseFile)
					,


					HStack(
						RadioButton()
							.Content("Comma").Value("comma")
							.IsChecked(State.Delimiter == "comma")
							.OnCheckedChanged(e =>
								{ if (e.Value) SetState(s => s.Delimiter = "comma"); }),
						RadioButton()
							.Content("Tab").Value("tab")
							.IsChecked(State.Delimiter == "tab")
							.OnCheckedChanged(e =>
								{ if (e.Value) SetState(s => s.Delimiter = "tab"); })
					)
					.Spacing((Double)Application.Current.Resources["size320"]),

					Button($"{_localize["Save"]}")
						.OnClicked(SaveVocab)
						.HorizontalOptions(DeviceInfo.Idiom == DeviceIdiom.Desktop ?
							LayoutOptions.Start : LayoutOptions.Fill)
						.WidthRequest(DeviceInfo.Idiom == DeviceIdiom.Desktop ? 300 : -1)
				)
				.HorizontalOptions(LayoutOptions.Fill)
				.Spacing((Double)Application.Current.Resources["size320"])
				.Margin(24)	
			)
			// ToolbarItem().IconImageSource(SegoeFluentIcons.FileExplorer.ToImageSource()).OnClicked(() => ChooseFile())
		);
    }

    private async Task ChooseFile()
    {
        var file = await _picker.PickFileAsync("Select a file", FileType);

        if (file != null)
        {
            using var stream = await file.OpenReadAsync();
            using var reader = new StreamReader(stream);
            var content = await reader.ReadToEndAsync();
            SetState(s => s.VocabList = content);
        }
    }

    private async Task SaveVocab()
    {
        var list = new VocabularyList
        {
            Name = State.VocabListName,
            Words = VocabularyWord.ParseVocabularyWords(State.VocabList, State.Delimiter)
        };

        await _vocabService.SaveListAsync(list);
        await MauiControls.Shell.Current.GoToAsync("..");
    }
}