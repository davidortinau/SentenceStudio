using MauiReactor.Shapes;
using Plugin.Maui.Audio;
using System.Collections.ObjectModel;

namespace SentenceStudio.Pages.HowDoYouSay;

class HowDoYouSayPageState
{
	public string Phrase { get; set; }
	public bool IsBusy { get; set; }
	public ObservableCollection<StreamHistory> StreamHistory { get; set; } = new();
	public float PlaybackPosition { get; set; } = 0f;
	public StreamHistory CurrentPlayingItem { get; set; }
}

partial class HowDoYouSayPage : Component<HowDoYouSayPageState>
{
	[Inject] AiService _aiService;
	[Inject] AudioAnalyzer _audioAnalyzer;
	LocalizationManager _localize => LocalizationManager.Instance;
	
	private IAudioPlayer _audioPlayer;
	private System.Timers.Timer _playbackTimer;

	public override VisualNode Render()
	{
		return ContentPage($"{_localize["HowDoYouSay"]}",
			Grid(rows: "Auto,Auto,*", "*",
				RenderInput(),
				WaveformDisplay(),
				RenderHistory()
			)
		);
	}

	VisualNode RenderInput() =>
		VStack(spacing: ApplicationTheme.Size240,
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
		
	private VisualNode WaveformDisplay() =>
		VStack(
			Label("Audio Waveform")
				.FontSize(16)
				.TextColor(Colors.Gray)
				.HCenter()
				.IsVisible(State.CurrentPlayingItem != null),
			Border(
				new Waveform()
					.WaveColor(Theme.IsLightTheme ? Colors.DarkBlue.WithAlpha(0.6f) : Colors.SkyBlue.WithAlpha(0.6f))
					.PlayedColor(Theme.IsLightTheme ? Colors.Orange : Colors.OrangeRed)
					.Amplitude(0.8f)
					.PlaybackPosition(State.PlaybackPosition)
					.Height(80)
					.AutoGenerateWaveform(false) // Don't auto-generate random data
					.StreamHistoryItem(State.CurrentPlayingItem) // Use the real waveform data
					.AudioDuration(_audioPlayer?.Duration ?? 0) // Use actual audio duration
					.PixelsPerSecond(120) // 120 pixels per second gives good detail
					.UseScrollView(true) // Enable horizontal scrolling
			)
			.StrokeShape(new RoundRectangle().CornerRadius(8))
			.StrokeThickness(1)
			.Stroke(Theme.IsLightTheme ? Colors.LightGray : Colors.DimGray)
			.HeightRequest(100)
			.IsVisible(State.CurrentPlayingItem != null)
		)
		.Margin(20, 0)
		.HStart() // Align the VStack to the left/start
		.GridRow(1);

	VisualNode RenderHistory() =>
		ScrollView(
			VStack(
				State.StreamHistory.Select(item => RenderHistoryItem(item)).ToArray()
			)
			.Spacing(ApplicationTheme.Size240)
			.Padding(ApplicationTheme.Size240)
		)
		.GridRow(2);

	VisualNode RenderHistoryItem(StreamHistory item) =>
		HStack(spacing: ApplicationTheme.Size120,
			Button()
				.Background(Colors.Transparent)
				.OnClicked(() => PlayAudio(item))
				.ImageSource(SegoeFluentIcons.Play.ToFontImageSource())
				.TextColor(Colors.Black),
			Label(item.Phrase)
				.FontSize(24)
		);

	async Task Submit()
	{
		if (string.IsNullOrWhiteSpace(State.Phrase)) return;

		SetState(s => s.IsBusy = true);

		try
		{
			var stream = await _aiService.TextToSpeechAsync(State.Phrase, "Nova");
			
			// Create new StreamHistory item
			var historyItem = new StreamHistory { Phrase = State.Phrase, Stream = stream };
			
			// Analyze the audio stream to extract waveform data
			historyItem.WaveformData = await _audioAnalyzer.AnalyzeAudioStreamAsync(stream);
			
			SetState(s =>
			{
				s.StreamHistory.Insert(0, historyItem);
				s.Phrase = string.Empty;
				s.IsBusy = false;
				
				// Set as current playing item so we can see the waveform immediately
				s.CurrentPlayingItem = historyItem;
			});
		}
		catch (Exception ex)
		{
			Debug.WriteLine(ex.Message);
			SetState(s => s.IsBusy = false);
		}
	}

	async void PlayAudio(StreamHistory item)
	{
		try
		{
			// Stop any currently playing audio
			StopPlayback();
			
			// Reset stream position
			item.Stream.Position = 0;
			
			// Create and play audio
			_audioPlayer = AudioManager.Current.CreatePlayer(item.Stream);
			_audioPlayer.PlaybackEnded += OnPlaybackEnded;
			_audioPlayer.Play();
			
			// Set as the current playing item
			SetState(s => 
			{ 
				s.CurrentPlayingItem = item;
				s.PlaybackPosition = 0f;
			});
			
			// If the waveform hasn't been analyzed yet, analyze it now
			if (!item.IsWaveformAnalyzed)
			{
				var waveformData = await _audioAnalyzer.AnalyzeAudioStreamAsync(item.Stream);
				SetState(s => 
				{
					// Find the item and update its waveform data
					var historyItem = s.StreamHistory.FirstOrDefault(h => h == item);
					if (historyItem != null)
					{
						historyItem.WaveformData = waveformData;
					}
				});
			}
			
			// Start the playback timer to update position
			StartPlaybackTimer();
		}
		catch (Exception ex)
		{
			Debug.WriteLine(ex.Message);
		}
	}
	
	private void OnPlaybackEnded(object sender, EventArgs e)
	{
		StopPlayback();
		
		// Reset position to start
		SetState(s => s.PlaybackPosition = 0f);
	}
	
	private void StopPlayback()
	{
		// Stop any existing player
		if (_audioPlayer != null)
		{
			_audioPlayer.PlaybackEnded -= OnPlaybackEnded;
			
			if (_audioPlayer.IsPlaying)
			{
				_audioPlayer.Stop();
			}
			_audioPlayer.Dispose();
			_audioPlayer = null;
		}
		
		// Stop playback timer
		if (_playbackTimer != null)
		{
			_playbackTimer.Stop();
			_playbackTimer.Elapsed -= OnPlaybackTimerElapsed;
			_playbackTimer.Dispose();
			_playbackTimer = null;
		}
	}
	
	private void StartPlaybackTimer()
	{
		// Create timer to update playback position
		_playbackTimer = new System.Timers.Timer(50); // Update every 50ms
		_playbackTimer.Elapsed += OnPlaybackTimerElapsed;
		_playbackTimer.Start();
	}
	
	private void OnPlaybackTimerElapsed(object sender, System.Timers.ElapsedEventArgs e)
	{
		if (_audioPlayer != null && _audioPlayer.IsPlaying && _audioPlayer.Duration > 0)
		{
			// Calculate position ratio (0-1)
			float position = (float)(_audioPlayer.CurrentPosition / _audioPlayer.Duration);
			
			// Update UI on main thread
			MainThread.BeginInvokeOnMainThread(() =>
			{
				// First update the waveform directly for smooth animation
				// var waveformComponent = FindByType<Waveform>();
				// if (waveformComponent != null)
				// {
				// 	// Use the direct update method for smoother animation
				// 	waveformComponent.UpdatePlaybackPosition(position);
				// }
				
				// Also update state but less frequently to avoid excessive re-renders
				// Only update state if position changed by at least 1%
				// if (Math.Abs(State.PlaybackPosition - position) >= 0.01f)
				// {
					SetState(s => s.PlaybackPosition = position);
				// }
			});
		}
	}
	
	// Clean up resources when component is removed
	protected override void OnWillUnmount()
	{
		StopPlayback();
		base.OnWillUnmount();
	}
}