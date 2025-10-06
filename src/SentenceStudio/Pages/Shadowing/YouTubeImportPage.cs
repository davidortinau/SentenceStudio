using MauiReactor.Compatibility;

namespace SentenceStudio.Pages.YouTube;

class YouTubeImportState
{
    public string VideoUrl { get; set; } = string.Empty;
    public double StartTimeSeconds { get; set; } = 0;
    public double DurationSeconds { get; set; } = 5;
    public bool IsImporting { get; set; } = false;
    public string ErrorMessage { get; set; } = string.Empty;
    public StreamHistory ImportedClip { get; set; }

    // Transcript-related properties
    public List<TranscriptTrack> AvailableTranscripts { get; set; } = new();
    public TranscriptTrack SelectedTranscript { get; set; }
    public string TranscriptText { get; set; } = string.Empty;
    public bool IsLoadingTranscripts { get; set; } = false;
    public bool ShowTranscriptPicker { get; set; } = false;
    public string TranscriptMessage { get; set; } = string.Empty;
    public bool IsSavingResource { get; set; } = false;
    public int? SavedResourceId { get; set; } = null;
}

partial class YouTubeImportPage : Component<YouTubeImportState>
{
    [Inject] YouTubeImportService _youtubeImportService;
    [Inject] UserProfileRepository _userProfileRepository;
    [Inject] LearningResourceRepository _learningResourceRepository;

    public override VisualNode Render()
    {
        return ContentPage("Import from YouTube",
            ToolbarItem("Clear")
                .OnClicked(ClearPage),

            ScrollView(
                VStack(spacing: 20,
                Label("Import audio clip from YouTube video")
                    .ThemeKey("Title"),

                Entry()
                    .Placeholder("YouTube URL")
                    .Text(State.VideoUrl)
                    .OnTextChanged((url) => SetState(s => s.VideoUrl = url))
                    .Margin(0, 10),

                Grid(rows: "Auto", columns: "*,*",
                    VStack(spacing: 5,
                        Label("Start time (seconds)"),
                        Entry()
                            .Keyboard(Keyboard.Numeric)
                            .Text(State.StartTimeSeconds.ToString())
                            .OnTextChanged((val) =>
                            {
                                if (double.TryParse(val, out double seconds))
                                    SetState(s => s.StartTimeSeconds = seconds);
                            })
                    ).GridColumn(0),

                    VStack(spacing: 5,
                        Label("Duration (seconds)"),
                        Entry()
                            .Keyboard(Keyboard.Numeric)
                            .Text(State.DurationSeconds.ToString())
                            .OnTextChanged((val) =>
                            {
                                if (double.TryParse(val, out double seconds))
                                    SetState(s => s.DurationSeconds = seconds);
                            })
                    ).GridColumn(1)
                ),

                HStack(spacing: 10,
                    Button("Import Clip")
                        .OnClicked(ImportClipAsync)
                        .IsEnabled(!State.IsImporting && !string.IsNullOrEmpty(State.VideoUrl))
                        .HStart(),

                    Button("Fetch Transcripts")
                        .OnClicked(FetchTranscriptsAsync)
                        .IsEnabled(!State.IsLoadingTranscripts && !string.IsNullOrEmpty(State.VideoUrl))
                        .HEnd()
                ),

                Label(State.ErrorMessage)
                    .IsVisible(!string.IsNullOrEmpty(State.ErrorMessage)),

                Label(State.TranscriptMessage)
                    .IsVisible(!string.IsNullOrEmpty(State.TranscriptMessage)),

                VStack(spacing: 10,
                    Label("Preview")
                        .ThemeKey("Subtitle")
                        .HStart(),

                    new WaveformView()
                        .WaveColor(Theme.IsLightTheme ? Colors.DarkBlue.WithAlpha(0.6f) : Colors.SkyBlue.WithAlpha(0.6f))
                        .PlayedColor(Theme.IsLightTheme ? Colors.Orange : Colors.OrangeRed)
                        .Amplitude(Constants.Amplitude)
                        .ShowTimeScale(true)
                        .WaveformData(State.ImportedClip?.WaveformData)
                        .AudioDuration(State.ImportedClip?.Duration ?? 0)
                ),
                // .IsVisible(State.ImportedClip != null),

                Button("Use This Clip")
                    .IsEnabled(State.ImportedClip != null)
                    .OnClicked(AddClipToLibrary),

                // Transcript picker (when multiple transcripts available)
                VStack(spacing: 10,
                    Label("Select Transcript Language")
                        .ThemeKey("Subtitle")
                        .HStart(),

                    Picker()
                        .ItemsSource(State.AvailableTranscripts.Select(t => t.LanguageName).ToList())
                        .SelectedIndex(State.AvailableTranscripts.IndexOf(State.SelectedTranscript))
                        .OnSelectedIndexChanged(async (index) =>
                        {
                            if (index >= 0 && index < State.AvailableTranscripts.Count)
                            {
                                var selected = State.AvailableTranscripts[index];
                                SetState(s => s.SelectedTranscript = selected);
                                await LoadTranscriptAsync(selected);
                            }
                        }),

                    Label($"Language: {State.SelectedTranscript?.LanguageName ?? "None"}")
                        .IsVisible(State.SelectedTranscript != null),

                    Label(State.SelectedTranscript?.IsAutoGenerated == true ? "⚠️ Auto-generated" : "✓ Manual")
                        .IsVisible(State.SelectedTranscript != null)
                        .FontSize(12)
                )
                .IsVisible(State.ShowTranscriptPicker),

                // Transcript display
                VStack(spacing: 10,
                    Label("Transcript")
                        .ThemeKey("Subtitle")
                        .HStart(),

                    Border(
                        ScrollView(
                            Label(State.TranscriptText)
                                .Padding(10)
                        )
                        .HeightRequest(200)
                    )
                    .Stroke(Theme.IsLightTheme ? MyTheme.DarkOnLightBackground : MyTheme.LightOnDarkBackground)
                    .StrokeThickness(1)
                )
                .IsVisible(!string.IsNullOrEmpty(State.TranscriptText)),

                // Save transcript button
                Button("Save as Learning Resource")
                    .OnClicked(SaveTranscriptAsResource)
                    .IsEnabled(!State.IsSavingResource && !string.IsNullOrEmpty(State.TranscriptText))
                    .IsVisible(!string.IsNullOrEmpty(State.TranscriptText) && State.SavedResourceId == null)
            )
            .Padding(20)
            )
        );
    }

    async Task ImportClipAsync()
    {
        try
        {
            SetState(s =>
            {
                s.IsImporting = true;
                s.ErrorMessage = string.Empty;
                s.ImportedClip = null;
            });

            var importedClip = await _youtubeImportService.ExtractAudioClipAsync(
                State.VideoUrl,
                State.StartTimeSeconds,
                State.DurationSeconds
            );

            SetState(s =>
            {
                s.ImportedClip = importedClip;
                s.IsImporting = false;
            });
        }
        catch (Exception ex)
        {
            SetState(s =>
            {
                s.ErrorMessage = $"Import failed: {ex.Message}";
                s.IsImporting = false;
            });
        }
    }

    async Task AddClipToLibrary()
    {
        // Add code to save this clip to your app's library
        // This would depend on how you're managing audio in your app
    }

    async Task FetchTranscriptsAsync()
    {
        try
        {
            SetState(s =>
            {
                s.IsLoadingTranscripts = true;
                s.TranscriptMessage = "Fetching available transcripts...";
                s.ErrorMessage = string.Empty;
            });

            var tracks = await _youtubeImportService.GetAvailableTranscriptsAsync(State.VideoUrl);

            if (tracks.Count == 0)
            {
                SetState(s =>
                {
                    s.TranscriptMessage = "No transcripts available for this video.";
                    s.IsLoadingTranscripts = false;
                });
                return;
            }

            // Get user's language preferences
            var profile = await _userProfileRepository.GetAsync();
            var targetLangCode = GetLanguageCode(profile?.TargetLanguage ?? "Korean");
            var nativeLangCode = GetLanguageCode(profile?.NativeLanguage ?? "English");

            // Try to find preferred transcript
            var preferredTrack = tracks.FirstOrDefault(t => t.LanguageCode.StartsWith(targetLangCode, StringComparison.OrdinalIgnoreCase))
                              ?? tracks.FirstOrDefault(t => t.LanguageCode.StartsWith(nativeLangCode, StringComparison.OrdinalIgnoreCase))
                              ?? tracks.FirstOrDefault();

            SetState(s =>
            {
                s.AvailableTranscripts = tracks;
                s.SelectedTranscript = preferredTrack;
                s.IsLoadingTranscripts = false;
            });

            if (tracks.Count == 1)
            {
                // Auto-load if only one transcript
                SetState(s => s.TranscriptMessage = $"Found transcript in {preferredTrack.LanguageName}. Loading...");
                await LoadTranscriptAsync(preferredTrack);
            }
            else
            {
                // Show picker for multiple transcripts
                SetState(s =>
                {
                    s.ShowTranscriptPicker = true;
                    s.TranscriptMessage = $"Found {tracks.Count} transcripts. Select your preferred language below.";
                });

                // Auto-load the preferred transcript
                await LoadTranscriptAsync(preferredTrack);
            }
        }
        catch (Exception ex)
        {
            SetState(s =>
            {
                s.ErrorMessage = $"Failed to fetch transcripts: {ex.Message}";
                s.TranscriptMessage = string.Empty;
                s.IsLoadingTranscripts = false;
            });
        }
    }

    async Task LoadTranscriptAsync(TranscriptTrack track)
    {
        try
        {
            SetState(s => s.TranscriptMessage = $"Loading {track.LanguageName} transcript...");

            var transcriptText = await _youtubeImportService.DownloadTranscriptTextAsync(track);

            SetState(s =>
            {
                s.TranscriptText = transcriptText;
                s.TranscriptMessage = $"Transcript loaded in {track.LanguageName}" +
                    (track.IsAutoGenerated ? " (auto-generated)" : " (manual)");
            });
        }
        catch (Exception ex)
        {
            SetState(s =>
            {
                s.ErrorMessage = $"Failed to load transcript: {ex.Message}";
                s.TranscriptMessage = string.Empty;
            });
        }
    }

    string GetLanguageCode(string language) => language switch
    {
        "Korean" => "ko",
        "English" => "en",
        "Spanish" => "es",
        "Japanese" => "ja",
        "Chinese" => "zh",
        "French" => "fr",
        "German" => "de",
        "Italian" => "it",
        "Portuguese" => "pt",
        "Russian" => "ru",
        _ => "en"
    };

    async Task SaveTranscriptAsResource()
    {
        try
        {
            SetState(s => s.IsSavingResource = true);

            // Get video metadata
            var videoId = YoutubeExplode.Videos.VideoId.Parse(State.VideoUrl);
            var video = await _youtubeImportService.GetVideoMetadataAsync(videoId.ToString());

            // Get user profile for language
            var profile = await _userProfileRepository.GetAsync();
            var language = State.SelectedTranscript?.LanguageName ?? profile?.TargetLanguage ?? "Korean";

            // Create learning resource
            var resource = new LearningResource
            {
                Title = video.Title ?? "YouTube Video",
                Description = video.Description ?? $"Imported from YouTube: {State.VideoUrl}",
                Language = language,
                MediaType = "Video",
                MediaUrl = State.VideoUrl,
                Transcript = State.TranscriptText,
                Tags = "youtube,video,transcript",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            // Save the resource - this updates the resource.Id property
            await _learningResourceRepository.SaveResourceAsync(resource);

            // Now resource.Id contains the correct ID from the database
            var resourceId = resource.Id;

            SetState(s =>
            {
                s.IsSavingResource = false;
                s.SavedResourceId = resourceId;
            });

            // Ask user if they want to view the resource
            var viewResource = await Application.Current.MainPage.DisplayAlert(
                "Success",
                "Transcript saved as learning resource! Would you like to view it now?",
                "Yes",
                "No");

            if (viewResource)
            {
                await MauiControls.Shell.Current.GoToAsync<LearningResources.ResourceProps>(
                    nameof(LearningResources.EditLearningResourcePage),
                    props => props.ResourceID = resourceId);
            }
        }
        catch (Exception ex)
        {
            SetState(s =>
            {
                s.IsSavingResource = false;
                s.ErrorMessage = $"Failed to save resource: {ex.Message}";
            });
        }
    }

    void ClearPage()
    {
        SetState(s =>
        {
            s.VideoUrl = string.Empty;
            s.StartTimeSeconds = 0;
            s.DurationSeconds = 5;
            s.IsImporting = false;
            s.ErrorMessage = string.Empty;
            s.ImportedClip = null;
            s.AvailableTranscripts = new();
            s.SelectedTranscript = null;
            s.TranscriptText = string.Empty;
            s.IsLoadingTranscripts = false;
            s.ShowTranscriptPicker = false;
            s.TranscriptMessage = string.Empty;
            s.IsSavingResource = false;
            s.SavedResourceId = null;
        });
    }
}