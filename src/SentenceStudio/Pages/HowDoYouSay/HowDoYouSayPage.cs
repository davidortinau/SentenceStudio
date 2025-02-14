using CommunityToolkit.Maui.Core;
using CommunityToolkit.Maui.Views;
using MauiReactor;
using MauiReactor.Shapes;
using Plugin.Maui.Audio;
using SentenceStudio.Models;
using SentenceStudio.Pages.Controls;
using SentenceStudio.Resources.Styles;
using SentenceStudio.Services;
using System.Collections.ObjectModel;
using MauiIcons.Core;

namespace SentenceStudio.Pages.HowDoYouSay;

class HowDoYouSayPageState
{
	public string Phrase { get; set; }
	public bool IsBusy { get; set; }
	public ObservableCollection<StreamHistory> StreamHistory { get; set; } = new();
}

partial class HowDoYouSayPage : Component<HowDoYouSayPageState>
{
	[Inject] AiService _aiService;
	LocalizationManager _localize => LocalizationManager.Instance;

	public override VisualNode Render()
	{
		return ContentPage($"{_localize["HowDoYouSay"]}",
			Grid(rows: "Auto,*", "*",
				RenderInput(),
				RenderHistory()
			)
		);
	}

	private VisualNode RenderInput()
	{
		return VStack(spacing: ApplicationTheme.Size240,
			ActivityIndicator()
				.IsVisible(State.IsBusy)
				.IsRunning(State.IsBusy),
			Border(
				Editor()
					.Placeholder("Enter a word or phrase")
					.FontSize(32)
					.MinimumHeightRequest(200)
					.MaximumHeightRequest(500)
					.AutoSize(EditorAutoSizeOption.TextChanges)
					.Text(State.Phrase)
					.OnTextChanged((s, e) => SetState(s => s.Phrase = e.NewTextValue))
			)
			.StrokeShape(new RoundRectangle().CornerRadius(8))
			.Stroke(ApplicationTheme.Gray300),
			Button("Submit")
				.OnClicked(Submit)
		)
		.Padding(ApplicationTheme.Size240);
	}

	private VisualNode RenderHistory()
	{
		return ScrollView(
			VStack(
				State.StreamHistory.Select(item => RenderHistoryItem(item)).ToArray()
			)
			.Spacing(ApplicationTheme.Size240)
			.Padding(ApplicationTheme.Size240)
		)
		.GridRow(1);
	}

	private VisualNode RenderHistoryItem(StreamHistory item)
	{
		return HStack(spacing: ApplicationTheme.Size120,
			Button()
				.Background(Colors.Transparent)
				.OnClicked(() => PlayAudio(item))
				.ImageSource(SegoeFluentIcons.Play.ToFontImageSource())
				.TextColor(Colors.Black),
			Label(item.Phrase)
				.FontSize(24)
		);
	}

	private async Task Submit()
	{
		if (string.IsNullOrWhiteSpace(State.Phrase)) return;

		SetState(s => s.IsBusy = true);

		try
		{
			var stream = await _aiService.TextToSpeechAsync(State.Phrase, "Nova");
			
			SetState(s =>
			{
				s.StreamHistory.Insert(0, new StreamHistory { Phrase = s.Phrase, Stream = stream });
				s.Phrase = string.Empty;
				s.IsBusy = false;
			});
		}
		catch (Exception ex)
		{
			Debug.WriteLine(ex.Message);
			SetState(s => s.IsBusy = false);
		}
	}

	private void PlayAudio(StreamHistory item)
	{
		try
		{
			var player = AudioManager.Current.CreatePlayer(item.Stream);
			player.PlaybackEnded += (s, e) => item.Stream.Position = 0;
			player.Play();
		}
		catch (Exception ex)
		{
			Debug.WriteLine(ex.Message);
		}
	}
}