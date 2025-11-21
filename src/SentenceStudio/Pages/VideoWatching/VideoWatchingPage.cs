using Microsoft.Extensions.Logging;
using SentenceStudio.Pages.Dashboard;
using SentenceStudio.Components;

namespace SentenceStudio.Pages.VideoWatching;

/// <summary>
/// Page component for watching YouTube videos from learning resources.
/// Provides a consumption-focused experience with YouTube's native player controls.
/// </summary>
partial class VideoWatchingPage : Component<VideoWatchingPageState, ActivityProps>
{
    [Inject] VideoWatchingService _videoService;
    [Inject] LearningResourceRepository _resourceRepository;
    [Inject] ILogger<VideoWatchingPage> _logger;
    [Inject] SentenceStudio.Services.Timer.IActivityTimerService _timerService;
    LocalizationManager _localize => LocalizationManager.Instance;

    public override VisualNode Render()
    {
        if (State.IsBusy)
        {
            return ContentPage("Video Watching",
                VStack(
                    ActivityIndicator()
                        .IsRunning(true)
                        .VCenter()
                        .HCenter(),
                    Label("Loading video...")
                        .HCenter()
                        .ThemeKey(MyTheme.Body1)
                )
                .VCenter()
                .HCenter()
            );
        }

        if (!string.IsNullOrEmpty(State.ErrorMessage))
        {
            return ContentPage("Video Watching",
                VStack(
                    Label("⚠️")
                        .FontSize(48)
                        .HCenter(),
                    Label(State.ErrorMessage)
                        .HCenter()
                        .ThemeKey(MyTheme.Body1),
                    Button("Go Back")
                        .OnClicked(GoBack)
                        .HCenter()
                )
                .VCenter()
                .HCenter()
                .Spacing(MyTheme.Size160)
            );
        }

        return ContentPage(State.Resource?.Title ?? "Video Watching",
            Grid(rows: "*", columns: "*",
                RenderVideoPlayer()
            )
        )
        .Set(MauiControls.Shell.TitleViewProperty, Props?.FromTodaysPlan == true ? new ActivityTimerBar() : null)
        .OnAppearing(LoadContentAsync);
    }

    VisualNode RenderVideoPlayer()
    {
        if (string.IsNullOrEmpty(State.EmbedUrl))
        {
            return Label("No video URL available")
                .HCenter()
                .VCenter()
                .ThemeKey(MyTheme.Body1);
        }

        // Use direct URL with UrlWebViewSource for better compatibility with Mac Catalyst
        return WebView()
            .Source(State.EmbedUrl)
            .OnNavigating((sender, args) =>
            {
                _logger.LogDebug("WebView navigating to: {Url}", args.Url);
            })
            .OnNavigated((sender, args) =>
            {
                _logger.LogDebug("WebView navigated to: {Url}, Result: {Result}", args.Url, args.Result);
                SetState(s => s.IsWebViewLoaded = true);
            })
            .GridRow(0);
    }

    async Task GoBack()
    {
        _logger.LogInformation("Navigating back from VideoWatchingPage");
        await MauiControls.Shell.Current.GoToAsync("..");
    }

    protected override void OnMounted()
    {
        base.OnMounted();

        _logger.LogInformation("VideoWatchingPage mounted");

        if (Props?.Resource == null)
        {
            SetState(s => s.ErrorMessage = "No resource provided");
            return;
        }

        // Start activity timer if launched from Today's Plan
        if (Props?.FromTodaysPlan == true)
        {
            _logger.LogInformation("Starting activity timer for VideoWatching, PlanItemId: {PlanItemId}", Props.PlanItemId);
            _timerService.StartSession("VideoWatching", Props.PlanItemId);
        }
    }

    protected override void OnWillUnmount()
    {
        base.OnWillUnmount();

        _logger.LogInformation("VideoWatchingPage unmounting");

        // Pause timer when leaving activity
        if (Props?.FromTodaysPlan == true && _timerService.IsActive)
        {
            _logger.LogInformation("Pausing activity timer");
            _timerService.Pause();
        }
    }

    async Task LoadContentAsync()
    {
        SetState(s => s.IsBusy = true);

        try
        {
            _logger.LogInformation("Loading video content for resource {ResourceId}", Props.Resource.Id);

            // Load complete resource
            var resource = await _videoService.GetResourceWithVideoAsync(Props.Resource.Id);

            if (resource == null)
            {
                SetState(s =>
                {
                    s.ErrorMessage = "Resource not found or does not have a valid video URL";
                    s.IsBusy = false;
                });
                return;
            }

            // Extract video ID and generate embed URL
            var videoId = _videoService.ExtractYouTubeVideoId(resource.MediaUrl);

            if (string.IsNullOrWhiteSpace(videoId))
            {
                SetState(s =>
                {
                    s.ErrorMessage = "Could not extract YouTube video ID from URL";
                    s.IsBusy = false;
                });
                return;
            }

            var embedUrl = _videoService.GetEmbedUrl(videoId);

            _logger.LogInformation("Video loaded successfully - ID: {VideoId}, Title: {Title}",
                videoId, resource.Title);

            SetState(s =>
            {
                s.Resource = resource;
                s.VideoId = videoId;
                s.EmbedUrl = embedUrl;
                s.IsBusy = false;
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading video content");
            SetState(s =>
            {
                s.ErrorMessage = $"Failed to load video: {ex.Message}";
                s.IsBusy = false;
            });
        }
    }
}
