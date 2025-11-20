using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace SentenceStudio.Services;

/// <summary>
/// Service for handling video watching activity, including YouTube URL parsing and embed generation.
/// </summary>
public class VideoWatchingService
{
    private readonly ILogger<VideoWatchingService> _logger;
    private readonly LearningResourceRepository _resourceRepository;

    // YouTube URL patterns to match
    private static readonly Regex[] YouTubePatterns = new[]
    {
        // Standard: https://www.youtube.com/watch?v=VIDEO_ID
        new Regex(@"(?:youtube\.com\/watch\?v=)([a-zA-Z0-9_-]{11})", RegexOptions.Compiled),

        // Short: https://youtu.be/VIDEO_ID
        new Regex(@"(?:youtu\.be\/)([a-zA-Z0-9_-]{11})", RegexOptions.Compiled),

        // Embed: https://www.youtube.com/embed/VIDEO_ID
        new Regex(@"(?:youtube\.com\/embed\/)([a-zA-Z0-9_-]{11})", RegexOptions.Compiled),

        // With timestamp: https://www.youtube.com/watch?v=VIDEO_ID&t=123s
        new Regex(@"(?:youtube\.com\/watch\?.*v=)([a-zA-Z0-9_-]{11})", RegexOptions.Compiled)
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="VideoWatchingService"/> class.
    /// </summary>
    /// <param name="service">The service provider to resolve dependencies.</param>
    public VideoWatchingService(IServiceProvider service)
    {
        _logger = service.GetRequiredService<ILogger<VideoWatchingService>>();
        _resourceRepository = service.GetRequiredService<LearningResourceRepository>();
    }

    /// <summary>
    /// Extracts a YouTube video ID from various URL formats.
    /// </summary>
    /// <param name="url">The YouTube URL to parse.</param>
    /// <returns>The extracted video ID, or null if no valid ID was found.</returns>
    public string? ExtractYouTubeVideoId(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            _logger.LogWarning("ExtractYouTubeVideoId called with empty URL");
            return null;
        }

        _logger.LogDebug("Extracting video ID from URL: {Url}", url);

        // Try each pattern until we find a match
        foreach (var pattern in YouTubePatterns)
        {
            var match = pattern.Match(url);
            if (match.Success && match.Groups.Count > 1)
            {
                var videoId = match.Groups[1].Value;
                _logger.LogInformation("Successfully extracted video ID: {VideoId}", videoId);
                return videoId;
            }
        }

        _logger.LogWarning("Could not extract video ID from URL: {Url}", url);
        return null;
    }

    /// <summary>
    /// Generates a YouTube embed URL for the given video ID.
    /// Uses privacy-enhanced mode (youtube-nocookie.com) and minimal YouTube branding.
    /// No autoplay - user must click play button.
    /// </summary>
    /// <param name="videoId">The YouTube video ID.</param>
    /// <returns>The complete embed URL.</returns>
    public string GetEmbedUrl(string videoId)
    {
        if (string.IsNullOrWhiteSpace(videoId))
        {
            _logger.LogWarning("GetEmbedUrl called with empty video ID");
            return string.Empty;
        }

        // Privacy-enhanced mode, minimal branding, no autoplay
        var embedUrl = $"https://www.youtube-nocookie.com/embed/{videoId}?modestbranding=1&rel=0";

        _logger.LogDebug("Generated embed URL: {EmbedUrl}", embedUrl);
        return embedUrl;
    }

    /// <summary>
    /// Checks if a learning resource has a valid YouTube video URL.
    /// </summary>
    /// <param name="resource">The learning resource to check.</param>
    /// <returns>True if the resource has a valid YouTube URL, false otherwise.</returns>
    public bool HasValidYouTubeUrl(LearningResource? resource)
    {
        if (resource == null || string.IsNullOrWhiteSpace(resource.MediaUrl))
        {
            return false;
        }

        var videoId = ExtractYouTubeVideoId(resource.MediaUrl);
        return !string.IsNullOrWhiteSpace(videoId);
    }

    /// <summary>
    /// Gets a learning resource by ID and validates it has a playable video.
    /// </summary>
    /// <param name="resourceId">The resource ID.</param>
    /// <returns>The resource if found and has valid video URL, null otherwise.</returns>
    public async Task<LearningResource?> GetResourceWithVideoAsync(int resourceId)
    {
        _logger.LogInformation("Loading resource {ResourceId} for video playback", resourceId);

        var resource = await _resourceRepository.GetResourceAsync(resourceId);

        if (resource == null)
        {
            _logger.LogWarning("Resource {ResourceId} not found", resourceId);
            return null;
        }

        if (!HasValidYouTubeUrl(resource))
        {
            _logger.LogWarning("Resource {ResourceId} does not have a valid YouTube URL", resourceId);
            return null;
        }

        _logger.LogInformation("Resource {ResourceId} loaded successfully with video URL: {MediaUrl}",
            resourceId, resource.MediaUrl);

        return resource;
    }
}
