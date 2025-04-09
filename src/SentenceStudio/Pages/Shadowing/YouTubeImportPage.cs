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
}

partial class YouTubeImportPage : Component<YouTubeImportState>
{
    [Inject] YouTubeImportService _youtubeImportService;
    
    public override VisualNode Render()
    {
        return ContentPage("Import from YouTube",
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
                            .OnTextChanged((val) => {
                                if (double.TryParse(val, out double seconds))
                                    SetState(s => s.StartTimeSeconds = seconds);
                            })
                    ).GridColumn(0),
                    
                    VStack(spacing: 5,
                        Label("Duration (seconds)"),
                        Entry()
                            .Keyboard(Keyboard.Numeric)
                            .Text(State.DurationSeconds.ToString())
                            .OnTextChanged((val) => {
                                if (double.TryParse(val, out double seconds))
                                    SetState(s => s.DurationSeconds = seconds);
                            })
                    ).GridColumn(1)
                ),
                
                Button("Import Clip")
                    .OnClicked(ImportClipAsync)
                    .IsEnabled(!State.IsImporting && !string.IsNullOrEmpty(State.VideoUrl)),
                
                Label(State.ErrorMessage)
                    .TextColor(Colors.Red)
                    .IsVisible(!string.IsNullOrEmpty(State.ErrorMessage)),
                
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
                    .OnClicked(AddClipToLibrary)
            )
            .Padding(20)
            .VCenter()
        );
    }
    
    async void ImportClipAsync()
    {
        try
        {
            SetState(s => {
                s.IsImporting = true;
                s.ErrorMessage = string.Empty;
                s.ImportedClip = null;
            });
            
            var importedClip = await _youtubeImportService.ExtractAudioClipAsync(
                State.VideoUrl,
                State.StartTimeSeconds,
                State.DurationSeconds
            );
            
            SetState(s => {
                s.ImportedClip = importedClip;
                s.IsImporting = false;
            });
        }
        catch (Exception ex)
        {
            SetState(s => {
                s.ErrorMessage = $"Import failed: {ex.Message}";
                s.IsImporting = false;
            });
        }
    }
    
    async void AddClipToLibrary()
    {
        // Add code to save this clip to your app's library
        // This would depend on how you're managing audio in your app
    }
}