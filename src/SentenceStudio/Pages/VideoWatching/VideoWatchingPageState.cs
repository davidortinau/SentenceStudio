namespace SentenceStudio.Pages.VideoWatching;

/// <summary>
/// State class for the VideoWatchingPage component.
/// </summary>
class VideoWatchingPageState
{
    /// <summary>
    /// Gets or sets the learning resource being watched.
    /// </summary>
    public LearningResource Resource { get; set; }

    /// <summary>
    /// Gets or sets the YouTube video ID extracted from the resource URL.
    /// </summary>
    public string VideoId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the YouTube embed URL for the WebView.
    /// </summary>
    public string EmbedUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the page is busy loading.
    /// </summary>
    public bool IsBusy { get; set; } = false;

    /// <summary>
    /// Gets or sets the error message to display, if any.
    /// </summary>
    public string ErrorMessage { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the WebView has finished loading.
    /// </summary>
    public bool IsWebViewLoaded { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether the YouTube player is ready.
    /// </summary>
    public bool IsPlayerReady { get; set; } = false;

    /// <summary>
    /// Gets or sets the current player state (unstarted=-1, ended=0, playing=1, paused=2, buffering=3, cued=5).
    /// </summary>
    public int PlayerState { get; set; } = -1;

    /// <summary>
    /// Gets or sets the current playback time in seconds.
    /// </summary>
    public double CurrentTime { get; set; } = 0;

    /// <summary>
    /// Gets or sets the total duration in seconds.
    /// </summary>
    public double Duration { get; set; } = 0;

    /// <summary>
    /// Gets or sets a value indicating whether the player controls are visible.
    /// </summary>
    public bool ShowControls { get; set; } = false;
}
