using CommunityToolkit.Maui.Storage;
using MauiReactor.Shapes;
using Plugin.Maui.Audio;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SentenceStudio.Services.Speech;
using SentenceStudio.Pages.Controls;
using UXDivers.Popups.Maui.Controls;
using UXDivers.Popups.Services;
using SentenceStudio.Services;

namespace SentenceStudio.Pages.HowDoYouSay;

class HowDoYouSayPageState
{
	public string Phrase { get; set; }
	public bool IsBusy { get; set; }
	public ObservableCollection<StreamHistory> StreamHistory { get; set; } = new();
	public float PlaybackPosition { get; set; } = 0f;
	public StreamHistory CurrentPlayingItem { get; set; }
	public string SelectedVoiceId { get; set; } // Initialized from per-language preference
	public List<VoiceInfo> AvailableVoices { get; set; } = new(); // Dynamic voices from API
	public bool IsLoadingVoices { get; set; } = false;
	public string TargetLanguage { get; set; } = "Korean"; // User's primary target language
	public bool IsPlaying { get; set; } = false; // Track if audio is currently playing

	public string SelectedVoiceDisplayName
	{
		get
		{
			if (string.IsNullOrEmpty(SelectedVoiceId))
				return "Select Voice";
			var voice = AvailableVoices.FirstOrDefault(v => v.VoiceId == SelectedVoiceId);
			return voice?.Name ?? "Select Voice";
		}
	}

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
	[Inject] UserActivityRepository _userActivityRepository;
	[Inject] SpeechVoicePreferences _speechVoicePreferences;
	[Inject] IVoiceDiscoveryService _voiceDiscoveryService;
	[Inject] UserProfileRepository _userProfileRepository;
	[Inject] ILogger<HowDoYouSayPage> _logger;
	[Inject] NativeThemeService _themeService;
	LocalizationManager _localize => LocalizationManager.Instance;

	private IAudioPlayer _audioPlayer;
	private System.Timers.Timer _playbackTimer;

	public override VisualNode Render()
	{
		return ContentPage($"{_localize["HowDoYouSay"]}",
			Grid(rows: "Auto,Auto,*", "*",
				RenderInput(),
				RenderHistory()
			)
		).BackgroundColor(BootstrapTheme.Current.GetBackground()).OnAppearing(OnPageAppearing);
	}

	private async Task OnPageAppearing()
	{
		SetState(s => s.IsLoading = true);

		// Get user's primary target language
		var userProfile = await _userProfileRepository.GetAsync();
		var targetLanguage = userProfile?.TargetLanguage ?? "Korean";

		SetState(s => s.TargetLanguage = targetLanguage);

		// Load voices for the target language
		await LoadVoicesForLanguageAsync(targetLanguage);

		// Load history from the repository
		await LoadHistoryAsync();
	}

	private async Task LoadVoicesForLanguageAsync(string language)
	{
		SetState(s => s.IsLoadingVoices = true);

		try
		{
			var voices = await _voiceDiscoveryService.GetVoicesForLanguageAsync(language);

			// Get the saved voice preference for this language
			var savedVoiceId = _speechVoicePreferences.GetVoiceForLanguage(language);

			SetState(s =>
			{
				s.AvailableVoices = voices;
				s.IsLoadingVoices = false;

				// Set selected voice: use saved preference if it exists in the list, otherwise use first available
				if (!string.IsNullOrEmpty(savedVoiceId) && voices.Any(v => v.VoiceId == savedVoiceId))
				{
					s.SelectedVoiceId = savedVoiceId;
				}
				else if (voices.Any())
				{
					s.SelectedVoiceId = voices.First().VoiceId;
				}
			});

			_logger.LogInformation("🎙️ Loaded {Count} voices for {Language}", voices.Count, language);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to load voices for {Language}", language);
			SetState(s => s.IsLoadingVoices = false);
		}
	}

	private async Task LoadHistoryAsync()
	{
		try
		{
			// Get all history from the repository
			var history = await _streamHistoryRepository.GetAllStreamHistoryAsync();

			// Create a new ObservableCollection with the loaded items
			SetState(s =>
			{
				s.StreamHistory = new ObservableCollection<StreamHistory>(history);
				s.IsLoading = false;
			});

			_logger.LogDebug("HowDoYouSayPage: Loaded {Count} history items", history.Count);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "HowDoYouSayPage: Error loading history");
			SetState(s => s.IsLoading = false);
		}
	}

	VisualNode RenderInput() =>
		VStack(spacing: 24,
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
			.Stroke(BootstrapTheme.Current.GetOutline())
			.StrokeThickness(1),
			HStack(
				Button("Submit")
					.Background(new SolidColorBrush(BootstrapTheme.Current.Primary))
					.TextColor(Colors.White)
					.HFill()
					.OnClicked(Submit),
				Button(State.SelectedVoiceDisplayName)
					.Background(new SolidColorBrush(Colors.Transparent))
					.TextColor(BootstrapTheme.Current.GetOnBackground())
					.BorderColor(BootstrapTheme.Current.GetOutline())
					.BorderWidth(1)
					.HEnd()
					.OnClicked(ShowVoiceSelectionPopup)
			).Spacing(24).HEnd()
		)
		.Padding(24);


	VisualNode RenderHistory() =>

		CollectionView()
			.Header(Label("History")
				.H4()
				.Padding(24))
			.ItemsSource(State.StreamHistory, RenderHistoryItem)
			.Margin(24)
			.GridRow(2);

	VisualNode RenderHistoryItem(StreamHistory item)
	{
		var theme = BootstrapTheme.Current;
		return Grid(rows: "*", columns: "Auto,*,Auto,Auto",
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
				.TextColor(theme.GetOnBackground())
				.GridColumn(0)
				.VStart(),
			Label(item.Phrase)
				.FontSize(24)
				.LineBreakMode(LineBreakMode.WordWrap)
				.GridColumn(1),
			Button()
				.Background(Colors.Transparent)
				.OnClicked(() => SaveAudioAsMp3(item))
				.ImageSource(BootstrapIcons.Create(BootstrapIcons.Save, theme.GetOnBackground(), 20))
				.TextColor(theme.GetOnBackground())
				.GridColumn(2)
				.HEnd(),
			Button()
				.Background(Colors.Transparent)
				.OnClicked(() => DeleteHistoryItem(item))
				.ImageSource(BootstrapIcons.Create(BootstrapIcons.Trash, theme.Danger, 20))
				.GridColumn(3)
				.HEnd()
		);
	}

	async Task Submit()
	{
		if (string.IsNullOrWhiteSpace(State.Phrase)) return;

		SetState(s => s.IsBusy = true);

		try
		{
			_logger.LogInformation("HowDoYouSayPage: Submitting phrase '{Phrase}' with voice {VoiceId}", State.Phrase, State.SelectedVoiceId);

			var stream = await _speechService.TextToSpeechAsync(
				State.Phrase,
				State.SelectedVoiceId); // Use the selected voice ID

			// Check for null or empty stream
			if (stream == null || stream == Stream.Null || stream.Length == 0)
			{
				_logger.LogWarning("HowDoYouSayPage: TTS returned empty stream for phrase: {Phrase} with voice: {VoiceId}", State.Phrase, State.SelectedVoiceId);
				await IPopupService.Current.PushAsync(new SimpleActionPopup
				{
					Title = "Error",
					Text = "Failed to generate audio. The voice may not be available. Please try a different voice.",
					ActionButtonText = "OK",
					ShowSecondaryActionButton = false
				});
				SetState(s => s.IsBusy = false);
				return;
			}

			// Create new StreamHistory item
			var historyItem = new StreamHistory
			{
				Phrase = State.Phrase,
				Stream = stream,
				VoiceId = State.SelectedVoiceId, // Store the voice Id with the history item
				CreatedAt = DateTime.UtcNow,
				UpdatedAt = DateTime.UtcNow
			};

			// First save to repository to get an ID
			await _streamHistoryRepository.SaveStreamHistoryAsync(historyItem);

			// Now use the Id for the filename
			string fileName = $"phrase_{historyItem.Id}.mp3";
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

			// Track user activity
			await _userActivityRepository.SaveAsync(new UserActivity
			{
				Activity = SentenceStudio.Shared.Models.Activity.HowDoYouSay.ToString(),
				Input = State.Phrase,
				Accuracy = 100, // Default to 100 for successful speech generation
				CreatedAt = DateTime.Now,
				UpdatedAt = DateTime.Now
			});

			PlayAudio(historyItem);

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
			_logger.LogError(ex, "HowDoYouSayPage: Error in Submit");
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
				_logger.LogDebug("HowDoYouSayPage: Using cached audio file: {FilePath}", item.AudioFilePath);
				audioStream = File.OpenRead(item.AudioFilePath);
				item.Stream = audioStream;
			}
			// If no local file or stream exists, fetch from the service
			else if (item.Stream == null)
			{
				_logger.LogDebug("HowDoYouSayPage: Fetching audio from service for: {Phrase}", item.Phrase);
				audioStream = await _speechService.TextToSpeechAsync(item.Phrase, item.VoiceId);
				item.Stream = audioStream;

				// Save the stream to disk for future use if we have an ID
				if (item.Id > 0)
				{
					string fileName = $"phrase_{item.Id}.mp3";
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

			// Check for null or empty stream
			if (audioStream == null || audioStream == Stream.Null || audioStream.Length == 0)
			{
				_logger.LogWarning("HowDoYouSayPage: Audio stream is null or empty for phrase: {Phrase}", item.Phrase);
				await IPopupService.Current.PushAsync(new SimpleActionPopup
				{
					Title = "Error",
					Text = "Failed to generate audio. Please check the selected voice.",
					ActionButtonText = "OK",
					ShowSecondaryActionButton = false
				});
				return;
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
			_logger.LogError(ex, "HowDoYouSayPage: Error playing audio");
			await IPopupService.Current.PushAsync(new SimpleActionPopup
			{
				Title = "Error",
				Text = $"Failed to play audio: {ex.Message}",
				ActionButtonText = "OK",
				ShowSecondaryActionButton = false
			});
		}
	}

	private void OnPlaybackEnded(object sender, EventArgs e)
	{
		StopPlayback();

		// Reset position to start and update playing state
		SetState(s =>
		{
			s.PlaybackPosition = 0f;
			s.IsPlaying = false;
		});
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
			// Don't dispose the player immediately - it can cause crashes
			// _audioPlayer.Dispose();
			// _audioPlayer = null;
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

	protected override void OnMounted()
	{
        _themeService.ThemeChanged += OnThemeChanged;
	    base.OnMounted();
	}

	protected override void OnWillUnmount()
	{
		StopPlayback();
        _themeService.ThemeChanged -= OnThemeChanged;
		base.OnWillUnmount();
	}

    private void OnThemeChanged(object? sender, ThemeChangedEventArgs e) => Invalidate();

	/// <summary>
	/// Shows the voice selection popup using UXDivers OptionSheetPopup.
	/// </summary>
	async void ShowVoiceSelectionPopup()
	{
		if (State.IsLoadingVoices)
		{
			_logger.LogDebug("HowDoYouSayPage: Voices still loading, cannot show popup");
			return;
		}

		if (!State.AvailableVoices.Any())
		{
			_logger.LogWarning("HowDoYouSayPage: No voices available for {Language}", State.TargetLanguage);
			var noVoicesToast = new UXDivers.Popups.Maui.Controls.Toast { Title = $"No voices available for {State.TargetLanguage}" };
			await IPopupService.Current.PushAsync(noVoicesToast);
			_ = Task.Delay(2500).ContinueWith(async _ =>
			{
				try { await IPopupService.Current.PopAsync(noVoicesToast); } catch { }
			});
			return;
		}

		try
		{
			await VoiceSelectionPopup.ShowAsync(
				$"{State.TargetLanguage} Voices",
				State.AvailableVoices,
				State.SelectedVoiceId,
				SelectVoice
			);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "HowDoYouSayPage: Error showing voice selection popup");
		}
	}

	/// <summary>
	/// Handles voice selection and saves to per-language preferences.
	/// </summary>
	private void SelectVoice(string voiceId)
	{
		// Save to per-language preference
		_speechVoicePreferences.SetVoiceForLanguage(State.TargetLanguage, voiceId);

		// Update the selected voice
		SetState(s => s.SelectedVoiceId = voiceId);

		_logger.LogInformation("🎙️ Selected voice {VoiceId} for {Language}", voiceId, State.TargetLanguage);
	}

	/// <summary>
	/// Saves the selected audio to an MP3 file using the FileSaver service.
	/// </summary>
	async Task SaveAudioAsMp3(StreamHistory item)
	{
		_logger.LogDebug("HowDoYouSayPage: Saving audio for: {Phrase}", item.Phrase);
		// if (item?.Stream == null) 
		// {
		// 	await Application.Current.MainPage.DisplayAlert("Error", "No audio available to save", "OK");
		// 	return;
		// }

		try
		{
			SetState(s =>
			{
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
				var savedToast = new UXDivers.Popups.Maui.Controls.Toast { Title = "Audio saved successfully!" };
				await IPopupService.Current.PushAsync(savedToast);
				_ = Task.Delay(2500).ContinueWith(async _ =>
				{
					try { await IPopupService.Current.PopAsync(savedToast); } catch { }
				});
				// await Application.Current.MainPage.DisplayAlert("Success", $"Audio saved to: {fileSaverResult.FilePath}", "OK");
			}
			else
			{
				// Show error if save was canceled or failed
				if (!string.IsNullOrEmpty(fileSaverResult.Exception?.Message))
				{
						await IPopupService.Current.PushAsync(new SimpleActionPopup
					{
						Title = "Error",
						Text = $"Failed to save audio: {fileSaverResult.Exception.Message}",
						ActionButtonText = "OK",
						ShowSecondaryActionButton = false
					});
				}
			}

			SetState(s => s.IsSavingAudio = false);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "HowDoYouSayPage: Error saving audio");
			SetState(s => s.IsSavingAudio = false);

			await IPopupService.Current.PushAsync(new SimpleActionPopup
			{
				Title = "Error",
				Text = $"Failed to save audio: {ex.Message}",
				ActionButtonText = "OK",
				ShowSecondaryActionButton = false
			});
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
		var tcs = new TaskCompletionSource<bool>();
		var confirmPopup = new SimpleActionPopup
		{
			Title = "Confirm Deletion",
			Text = $"Are ye sure ye want to delete this phrase: \"{item.Phrase}\"?",
			ActionButtonText = "Aye",
			SecondaryActionButtonText = "Nay",
			CloseWhenBackgroundIsClicked = false,
			ActionButtonCommand = new Command(async () =>
			{
				tcs.TrySetResult(true);
				await IPopupService.Current.PopAsync();
			}),
			SecondaryActionButtonCommand = new Command(async () =>
			{
				tcs.TrySetResult(false);
				await IPopupService.Current.PopAsync();
			})
		};
		await IPopupService.Current.PushAsync(confirmPopup);
		bool confirm = await tcs.Task;

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
					_logger.LogError(ex, "HowDoYouSayPage: Error deleting audio file");
				}
			}

			// Remove from UI list
			SetState(s => s.StreamHistory.Remove(item));

			// Show toast notification
			var deletedToast = new UXDivers.Popups.Maui.Controls.Toast { Title = "Phrase deleted successfully!" };
			await IPopupService.Current.PushAsync(deletedToast);
			_ = Task.Delay(2500).ContinueWith(async _ =>
			{
				try { await IPopupService.Current.PopAsync(deletedToast); } catch { }
			});
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "HowDoYouSayPage: Error deleting history item");
			await IPopupService.Current.PushAsync(new SimpleActionPopup
			{
				Title = "Error",
				Text = $"Failed to delete phrase: {ex.Message}",
				ActionButtonText = "OK",
				ShowSecondaryActionButton = false
			});
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
			_audioPlayer.PlaybackEnded += OnPlaybackEnded;
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
		var theme = BootstrapTheme.Current;
		// If this is the current playing item and audio is playing, show pause
		if (State.CurrentPlayingItem == item && _audioPlayer != null && _audioPlayer.IsPlaying)
		{
			return BootstrapIcons.Create(BootstrapIcons.PauseFill, theme.GetOnBackground(), 20);
		}
		// Otherwise show play
		else
		{
			return BootstrapIcons.Create(BootstrapIcons.PlayCircleFill, theme.GetOnBackground(), 20);
		}
	}
}

/// <summary>
/// Converter to display voice name and gender
/// </summary>
class VoiceDisplayConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
	{
		if (value is VoiceInfo voice)
		{
			return $"{voice.Name} ({voice.Gender})";
		}
		return value?.ToString() ?? "";
	}

	public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
	{
		throw new NotImplementedException();
	}
}

/// <summary>
/// Converter to show checkmark for selected voice
/// </summary>
class VoiceSelectedConverter : IValueConverter
{
	private readonly string _selectedVoiceId;

	public VoiceSelectedConverter(string selectedVoiceId)
	{
		_selectedVoiceId = selectedVoiceId;
	}

	public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
	{
		if (value is string voiceId)
		{
			return voiceId == _selectedVoiceId;
		}
		return false;
	}

	public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
	{
		throw new NotImplementedException();
	}
}

/// <summary>
/// Converter to display voice with checkmark for selected item
/// </summary>
class VoiceDisplayWithCheckConverter : IValueConverter
{
	private readonly string _selectedVoiceId;

	public VoiceDisplayWithCheckConverter(string selectedVoiceId)
	{
		_selectedVoiceId = selectedVoiceId;
	}

	public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
	{
		if (value is VoiceInfo voice)
		{
			var check = voice.VoiceId == _selectedVoiceId ? "✓ " : "   ";
			return $"{check}{voice.Name} ({voice.Gender})";
		}
		return value?.ToString() ?? "";
	}

	public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
	{
		throw new NotImplementedException();
	}
}