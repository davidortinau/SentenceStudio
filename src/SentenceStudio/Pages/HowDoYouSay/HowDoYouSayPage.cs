using CommunityToolkit.Maui.Alerts;
using CommunityToolkit.Maui.Storage;
using MauiReactor.Shapes;
using Plugin.Maui.Audio;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace SentenceStudio.Pages.HowDoYouSay;

class HowDoYouSayPageState
{
	public string Phrase { get; set; }
	public bool IsBusy { get; set; }
	public ObservableCollection<StreamHistory> StreamHistory { get; set; } = new();
	public float PlaybackPosition { get; set; } = 0f;
	public StreamHistory CurrentPlayingItem { get; set; }
	public string SelectedVoiceId { get; set; } = "jiyoung"; // Default voice
	public bool IsVoiceSelectionVisible { get; set; } = false;
	public Dictionary<string, string> VoiceDisplayNames { get; set; } = new();
	
	public string SelectedVoiceDisplayName => 
		VoiceDisplayNames.ContainsKey(SelectedVoiceId) ? 
		VoiceDisplayNames[SelectedVoiceId] : "Ji-Young";
		
	// Export-related properties
	public bool IsSavingAudio { get; set; } = false;
	public string ExportProgressMessage { get; set; } = string.Empty;
	public StreamHistory ItemToExport { get; set; }
}

partial class HowDoYouSayPage : Component<HowDoYouSayPageState>
{
	[Inject] ElevenLabsSpeechService _speechService;
	[Inject] AudioAnalyzer _audioAnalyzer;
	[Inject] IFileSaver _fileSaver;
	LocalizationManager _localize => LocalizationManager.Instance;
	
	private IAudioPlayer _audioPlayer;
	private System.Timers.Timer _playbackTimer;

	public override VisualNode Render()
	{
		return ContentPage($"{_localize["HowDoYouSay"]}",
			Grid(rows: "Auto,Auto,*", "*",
				RenderInput(),
				WaveformDisplay(),
				RenderHistory(),
				RenderVoiceSelectionBottomSheet()
			)
		).OnAppearing(OnPageAppearing);
	}
	
	private void OnPageAppearing()
	{
		// Initialize voice display names from the service
		SetState(s => s.VoiceDisplayNames = _speechService.VoiceDisplayNames);
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
			HStack(
				Button("Submit")
					.HorizontalOptions(LayoutOptions.Fill)
					.OnClicked(Submit),
				Button(State.SelectedVoiceDisplayName)
					.ThemeKey("Secondary")
					.HEnd()
					.OnClicked(ShowVoiceSelection)
			).Spacing(ApplicationTheme.Size240)
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
				new WaveformView()
					.WaveColor(Theme.IsLightTheme ? Colors.DarkBlue.WithAlpha(0.6f) : Colors.SkyBlue.WithAlpha(0.6f))
					.PlayedColor(Theme.IsLightTheme ? Colors.Orange : Colors.OrangeRed)
					.Amplitude(0.8f)
					.PlaybackPosition(State.PlaybackPosition)
					.Height(80)
					.StreamHistoryItem(State.CurrentPlayingItem) // Use the real waveform data
					.AudioDuration(_audioPlayer?.Duration ?? 0) // Use actual audio duration
					.PixelsPerSecond(120) // 120 pixels per second gives good detail
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
		Grid("*", "Auto,*,Auto",
			Button()
				.Background(Colors.Transparent)
				.OnClicked(() => PlayAudio(item))
				.ImageSource(SegoeFluentIcons.Play.ToImageSource())
				.TextColor(Colors.Black)
				.GridColumn(0),
			Label(item.Phrase)
				.FontSize(24)
				.LineBreakMode(LineBreakMode.TailTruncation)
				.GridColumn(1),
			Button()
				.Background(Colors.Transparent)
				.OnClicked(() => SaveAudioAsMp3(item))
				.ImageSource(SegoeFluentIcons.Save.ToImageSource())
				.TextColor(Colors.Black)
				.GridColumn(2)
				.HEnd()
		);

	async Task Submit()
	{
		if (string.IsNullOrWhiteSpace(State.Phrase)) return;

		SetState(s => s.IsBusy = true);

		try
		{
			var stream = await _speechService.TextToSpeechAsync(
				State.Phrase, 
				State.SelectedVoiceId); // Use the selected voice ID
			
			// Create new StreamHistory item
			var historyItem = new StreamHistory { 
				Phrase = State.Phrase, 
				Stream = stream,
				VoiceId = State.SelectedVoiceId // Store the voice ID with the history item
			};
			
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
	
	/// <summary>
	/// Renders the voice selection bottom sheet.
	/// </summary>
	private VisualNode RenderVoiceSelectionBottomSheet() =>
		new SfBottomSheet(
				Grid("*", "*",
					ScrollView(
						VStack(
							Label("Korean Voices")
								.FontAttributes(FontAttributes.Bold)
								.FontSize(18)
								.TextColor(Theme.IsLightTheme ? ApplicationTheme.DarkOnLightBackground : ApplicationTheme.LightOnDarkBackground)
								.HCenter()
								.Margin(0, 0, 0, 10),
							CreateVoiceOption("yuna", "Yuna", "Female - Young, cheerful"),
							CreateVoiceOption("jiyoung", "Ji-Young", "Female - Warm, clear"),
							CreateVoiceOption("jina", "Jina", "Female - Mid-aged, news broadcaster"),
							CreateVoiceOption("jennie", "Jennie", "Female - Youthful, professional"),
							CreateVoiceOption("hyunbin", "Hyun-Bin", "Male - Cool, professional"),
							CreateVoiceOption("dohyeon", "Do-Hyeon", "Male - Older, mature"),
							CreateVoiceOption("yohankoo", "Yohan Koo", "Male - Confident, authoritative")
						)
						.Spacing(15)
						.Padding(20, 10)
					)
				)

		)
			.GridRowSpan(4)
			.IsOpen(State.IsVoiceSelectionVisible);

	/// <summary>
	/// Creates a voice option item for the bottom sheet.
	/// </summary>
	private VisualNode CreateVoiceOption(string voiceId, string displayName, string description) =>
		Grid("*", "Auto,*",
			RadioButton()
				.IsChecked(State.SelectedVoiceId == voiceId)
				.GroupName("VoiceOptions")
				.OnCheckedChanged((sender, args) =>
				{
					if (args.Value)
					{
						SelectVoice(voiceId);
					}
				})
				.GridColumn(0),
			VStack(spacing: 0,
				Label(displayName)
					.FontAttributes(FontAttributes.Bold)
					.FontSize(16),
				Label(description)
					.FontSize(14)
					.TextColor(Colors.Gray)
			)
			.HStart()
			.GridColumn(1)
		)
		.OnTapped(() => SelectVoice(voiceId))
		;
	
	/// <summary>
	/// Handles voice selection.
	/// </summary>
	private void SelectVoice(string voiceId)
	{
		// Update the selected voice
		SetState(s => {
			s.SelectedVoiceId = voiceId;
			
			// Close the bottom sheet after selection
			s.IsVoiceSelectionVisible = false;
		});
		
		Debug.WriteLine($"Selected voice: {voiceId}");
	}
	
	/// <summary>
	/// Shows the voice selection bottom sheet.
	/// </summary>
	private void ShowVoiceSelection()
	{
		SetState(s => s.IsVoiceSelectionVisible = true);
	}

	/// <summary>
	/// Saves the selected audio to an MP3 file using the FileSaver service.
	/// </summary>
	async void SaveAudioAsMp3(StreamHistory item)
	{
		Debug.WriteLine($"Saving audio for: {item.Phrase}");
		// if (item?.Stream == null) 
		// {
		// 	await App.Current.MainPage.DisplayAlert("Error", "No audio available to save", "OK");
		// 	return;
		// }

		try
		{
			SetState(s => {
				s.IsSavingAudio = true;
				s.ItemToExport = item;
			});

		// 	// Create a unique filename based on text and timestamp
			string safeFilename = MakeSafeFileName(item.Phrase);
			string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
			string fileName = $"{safeFilename}_{timestamp}.mp3";

		// 	// Clone the stream to a memory stream to avoid position issues
			MemoryStream memoryStream = new MemoryStream();
			item.Stream.Position = 0;
			await item.Stream.CopyToAsync(memoryStream);
			memoryStream.Position = 0;

		// 	// Reset original stream position
			item.Stream.Position = 0;

		// 	// Use the FileSaver to save the audio
			var fileSaverResult = await _fileSaver.SaveAsync(fileName, memoryStream, new CancellationToken());

			// 	// Check if the save was successful
			if (fileSaverResult.IsSuccessful)
			{
				// Show success message
				await Toast.Make("Audio saved successfully!").Show();
				// await App.Current.MainPage.DisplayAlert("Success", $"Audio saved to: {fileSaverResult.FilePath}", "OK");
			}
			else
			{
				// Show error if save was canceled or failed
				if (!string.IsNullOrEmpty(fileSaverResult.Exception?.Message))
				{
					await App.Current.MainPage.DisplayAlert("Error",
						$"Failed to save audio: {fileSaverResult.Exception.Message}", "OK");
				}
			}

			SetState(s => s.IsSavingAudio = false);
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"Error saving audio: {ex.Message}");
			SetState(s => s.IsSavingAudio = false);

			await App.Current.MainPage.DisplayAlert("Error", $"Failed to save audio: {ex.Message}", "OK");
		}
	}
	
	/// <summary>
	/// Creates a safe filename from a text string by removing invalid characters.
	/// </summary>
	private string MakeSafeFileName(string text)
	{
		if (string.IsNullOrEmpty(text))
			return "audio";
			
		// Replace invalid filename characters with underscores
		string invalidChars = new string(System.IO.Path.GetInvalidFileNameChars());
		string invalidRegStr = string.Format(@"[{0}]", Regex.Escape(invalidChars));
		string safe = Regex.Replace(text, invalidRegStr, "_");
		
		// Trim to reasonable length
		if (safe.Length > 50)
			safe = safe.Substring(0, 50);
			
		return safe;
	}
}