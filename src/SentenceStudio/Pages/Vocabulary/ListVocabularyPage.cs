using CustomLayouts;
using MauiReactor.Shapes;
using ReactorCustomLayouts;

namespace SentenceStudio.Pages.Vocabulary;

class ListVocabularyPageState
{
    public List<VocabularyList> VocabLists { get; set; } = [];
}

partial class ListVocabularyPage : Component<ListVocabularyPageState>
{
	[Inject] VocabularyService _vocabService;
	LocalizationManager _localize => LocalizationManager.Instance;

	public override VisualNode Render()
	{
		return ContentPage($"{_localize["VocabularyList"]}",
			VScrollView(
				VStack(
					Label()
						.Style((Style)Application.Current.Resources["Title1"])
						.HStart()
						.Text($"{_localize["VocabularyList"]}")
						.IsVisible(DeviceInfo.Platform != DevicePlatform.WinUI),

					new HWrap()
					{
						State.VocabLists.Select(list =>
							Border(
								Grid(
									Label()
										.VerticalOptions(LayoutOptions.Center)
										.HorizontalOptions(LayoutOptions.Center)
										.Text($"{list.Name} ({list.Words?.Count ?? 0})")
								)
								.WidthRequest(300)
								.HeightRequest(120)
								.OnTapped(() => ViewList(list.ID))
							)
							.StrokeShape(new Rectangle())
							.StrokeThickness(1)
						)
					}
					.Spacing((Double)Application.Current.Resources["size320"]),

					Border(
						Grid(
							Label($"{_localize["Add"]}")
								.Center()
						)
						.WidthRequest(300)
						.HeightRequest(60)
						.OnTapped(AddVocabulary)
					)
					.StrokeShape(new Rectangle())
					.StrokeThickness(1)
					.HStart()
				)
				.Padding((Double)Application.Current.Resources["size160"])
				.Spacing(ApplicationTheme.Size240)
			)
		).OnAppearing(LoadVocabLists);
	}

	private async Task LoadVocabLists()
	{
		var lists = await _vocabService.GetAllListsWithWordsAsync();
		SetState(s => s.VocabLists = lists);
	}

	private async Task AddVocabulary()
	{
		await MauiControls.Shell.Current.GoToAsync(nameof(AddVocabularyPage));
	}

	private async Task ViewList(int listID)
	{
		await MauiControls.Shell.Current.GoToAsync<VocabProps>(
			nameof(EditVocabularyPage),
			props => props.ListID = listID);
	}
}

class VocabProps
{
    public int ListID { get; set; }
}