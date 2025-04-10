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
	public string SelectedVoiceId { get; set; } = Voices.JiYoung; // Default voice
	public bool IsVoiceSelectionVisible { get; set; } = false;
	public Dictionary<string, string> VoiceDisplayNames { get; set; } = new();
	public bool IsPlaying { get; set; } = false; // Track if audio is currently playing
	
	public string SelectedVoiceDisplayName => 
		VoiceDisplayNames.ContainsKey(SelectedVoiceId) ? 
		VoiceDisplayNames[SelectedVoiceId] : "Ji-Young";
		
	// Export-related properties
	public bool IsSavingAudio { get; set; } = false;
	public string ExportProgressMessage { get; set; } = string.Empty;
	public StreamHistory ItemToExport { get; set; }
	
	// Is loading from repository
	public bool IsLoading { get; set; } = true;
}

partial class HowDoYouSayPage : Component<HowDoYouSayPageState>
{
	[Inject] ElevenLabsSpeechService _speechService;
	[Inject] AudioAnalyzer _audioAnalyzer;
	[Inject] IFileSaver _fileSaver;
	[Inject] StreamHistoryRepository _streamHistoryRepository;
	LocalizationManager _localize => LocalizationManager.Instance;
	
	private IAudioPlayer _audioPlayer;
	private System.Timers.Timer _playbackTimer;

	public override VisualNode Render()
	{
		return ContentPage($"{_localize["HowDoYouSay"]}",
			Grid(rows: "Auto,Auto,*", "*",
				RenderInput(),
				RenderHistory(),
				RenderVoiceSelectionBottomSheet()
			)
		).OnAppearing(OnPageAppearing);
	}
	
	private async Task OnPageAppearing()
	{
		// Initialize voice display names from the service
		SetState(s => {
			s.VoiceDisplayNames = _speechService.VoiceDisplayNames;
			s.IsLoading = true;
		});
		
		// Load history from the repository
		await LoadHistoryAsync();
	}
	
	private async Task LoadHistoryAsync()
	{
		try
		{
			// Get all history from the repository
			var history = await _streamHistoryRepository.GetAllStreamHistoryAsync();
			
			// Create a new ObservableCollection with the loaded items
			SetState(s => {
				s.StreamHistory = new ObservableCollection<StreamHistory>(history);
				s.IsLoading = false;
			});
			
			Debug.WriteLine($"Loaded {history.Count} history items");
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"Error loading history: {ex.Message}");
			SetState(s => s.IsLoading = false);
		}
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
			).Spacing(ApplicationTheme.Size240).HEnd()
		)
		.Padding(ApplicationTheme.Size240);
		
	
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
		Grid("*", "Auto,*,Auto,Auto",
			Button()
				.Background(Colors.Transparent)
				.OnClicked(() =>
				{
					// If this is the current item, toggle playback
					if (State.CurrentPlayingItem == item)
					{
						TogglePlayback();
					}
					// Otherwise play this new item
					else
					{
						PlayAudio(item);
					}
				})
				.ImageSource(GetPlayButtonIcon(item))
				.TextColor(Colors.Black)
				.GridColumn(0)
				.VCenter(),
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
				.HEnd(),
			Button()
				.Background(Colors.Transparent)
				.OnClicked(() => DeleteHistoryItem(item))
				.ImageSource(SegoeFluentIcons.Delete.ToImageSource())
				.TextColor(Colors.Red)
				.GridColumn(3)
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
				VoiceId = State.SelectedVoiceId, // Store the voice ID with the history item
				CreatedAt = DateTime.UtcNow,
				UpdatedAt = DateTime.UtcNow,
				Duration = await _audioAnalyzer.GetDurationAsync(stream)
			};
			
			// First save to repository to get an ID
			await _streamHistoryRepository.SaveStreamHistoryAsync(historyItem);
			
			// Now use the ID for the filename
			string fileName = $"phrase_{historyItem.ID}.mp3";
			historyItem.FileName = fileName;
			string audioFilePath = System.IO.Path.Combine(FileSystem.AppDataDirectory, fileName);
			
			// Save the audio stream to a file
			using (var fileStream = File.Create(audioFilePath))
			{
				stream.Position = 0;
				await stream.CopyToAsync(fileStream);
			}
			
			// Reset stream position
			stream.Position = 0;
			
			// Store the file path in the history item
			historyItem.AudioFilePath = audioFilePath;
			
			// Update the history item with the file path
			await _streamHistoryRepository.SaveStreamHistoryAsync(historyItem);
			
			SetState(s =>
			{
				s.StreamHistory.Insert(0, historyItem);
				s.Phrase = string.Empty;
				s.IsBusy = false;
				s.CurrentPlayingItem = historyItem;
			});
		}
		catch (Exception ex)
		{
			Debug.WriteLine(ex.Message);
			SetState(s => s.IsBusy = false);
		}
	}

	async Task PlayAudio(StreamHistory item)
	{
		try
		{
			// Stop any currently playing audio
			StopPlayback();
			
			Stream audioStream = null;
			
			// Check if we have a local file to use
			if (!string.IsNullOrEmpty(item.AudioFilePath) && File.Exists(item.AudioFilePath))
			{
				Debug.WriteLine($"Using cached audio file: {item.AudioFilePath}");
				audioStream = File.OpenRead(item.AudioFilePath);
				item.Stream = audioStream;
			}
			// If no local file or stream exists, fetch from the service
			else if (item.Stream == null)
			{
				Debug.WriteLine($"Fetching audio from service for: {item.Phrase}");
				audioStream = await _speechService.TextToSpeechAsync(item.Phrase, item.VoiceId);
				item.Stream = audioStream;
				
				// Save the stream to disk for future use if we have an ID
				if (item.ID > 0)
				{
					string fileName = $"phrase_{item.ID}.mp3";
					string audioFilePath = System.IO.Path.Combine(FileSystem.AppDataDirectory, fileName);
					
					// Save the audio stream to a file
					using (var fileStream = File.Create(audioFilePath))
					{
						audioStream.Position = 0;
						await audioStream.CopyToAsync(fileStream);
					}
					
					// Reset stream position
					audioStream.Position = 0;
					
					// Update the file path
					item.AudioFilePath = audioFilePath;
					item.FileName = fileName;
					
					// Update in repository
					await _streamHistoryRepository.SaveStreamHistoryAsync(item);
				}
			}
			// Otherwise use the existing stream
			else
			{
				audioStream = item.Stream;
				audioStream.Position = 0;
			}
			
			// Create and play audio
			_audioPlayer = AudioManager.Current.CreatePlayer(audioStream);
			_audioPlayer.PlaybackEnded += OnPlaybackEnded;
			_audioPlayer.Play();
			
			// Set as the current playing item
			SetState(s => 
			{ 
				s.CurrentPlayingItem = item;
				s.PlaybackPosition = 0f;
			});
			
			// Start the playback timer to update position
			StartPlaybackTimer();
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"Error playing audio: {ex.Message}");
			await App.Current.MainPage.DisplayAlert("Error", $"Failed to play audio: {ex.Message}", "OK");
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

			SetState(s => s.PlaybackPosition = position);
			
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
	async Task SaveAudioAsMp3(StreamHistory item)
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

	/// <summary>
	/// Deletes a history item from the list and the repository.
	/// </summary>
	async Task DeleteHistoryItem(StreamHistory item)
	{
		bool confirm = await App.Current.MainPage.DisplayAlert(
			"Confirm Deletion",
			$"Are ye sure ye want to delete this phrase: \"{item.Phrase}\"?",
			"Aye", "Nay");
			
		if (!confirm) return;
		
		try
		{
			// Stop playback if this is the item being played
			if (State.CurrentPlayingItem == item)
			{
				StopPlayback();
				SetState(s => s.CurrentPlayingItem = null);
			}
			
			// Delete from repository
			await _streamHistoryRepository.DeleteStreamHistoryAsync(item);
			
			// Delete audio file from disk if it exists
			if (!string.IsNullOrEmpty(item.AudioFilePath) && File.Exists(item.AudioFilePath))
			{
				try
				{
					File.Delete(item.AudioFilePath);
				}
				catch (Exception ex)
				{
					Debug.WriteLine($"Error deleting audio file: {ex.Message}");
				}
			}
			
			// Remove from UI list
			SetState(s => s.StreamHistory.Remove(item));
			
			// Show toast notification
			await Toast.Make("Phrase deleted successfully!").Show();
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"Error deleting history item: {ex.Message}");
			await App.Current.MainPage.DisplayAlert("Error", $"Failed to delete phrase: {ex.Message}", "OK");
		}
	}
	
	/// <summary>
	/// Pauses the current audio playback
	/// </summary>
	private void PausePlayback()
	{
		if (_audioPlayer != null && _audioPlayer.IsPlaying)
		{
			_audioPlayer.Pause();
			
			// Stop the timer but don't reset it
			if (_playbackTimer != null)
			{
				_playbackTimer.Stop();
			}
			
			// Update the playing state
			SetState(s => s.IsPlaying = false);
		}
	}
	
	/// <summary>
	/// Resumes playback from the current position
	/// </summary>
	private void ResumePlayback()
	{
		if (_audioPlayer != null && !_audioPlayer.IsPlaying)
		{
			_audioPlayer.Play();
			
			// Restart the timer
			if (_playbackTimer != null)
			{
				_playbackTimer.Start();
			}
			
			// Update the playing state
			SetState(s => s.IsPlaying = true);
		}
	}
	
	/// <summary>
	/// Toggles between play and pause
	/// </summary>
	private void TogglePlayback()
	{
		if (_audioPlayer == null) return;
		
		if (_audioPlayer.IsPlaying)
		{
			PausePlayback();
		}
		else
		{
			ResumePlayback();
		}
	}

	/// <summary>
	/// Returns the appropriate icon for the play/pause button based on current state
	/// </summary>
	private ImageSource GetPlayButtonIcon(StreamHistory item)
	{
		// If this is the current playing item and audio is playing, show pause
		if (State.CurrentPlayingItem == item && _audioPlayer != null && _audioPlayer.IsPlaying)
		{
			return ApplicationTheme.IconPause;
		}
		// Otherwise show play
		else
		{
			return ApplicationTheme.IconPlay;
		}
	}
}