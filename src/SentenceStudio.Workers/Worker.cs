using SentenceStudio.Services;
using SentenceStudio.Shared.Models;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Data;

namespace SentenceStudio.Workers;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly TimeSpan _checkInterval = TimeSpan.FromMinutes(30);

    public Worker(ILogger<Worker> logger, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("YouTube Channel Monitor Worker starting...");

        // Wait a bit before first check to let services initialize
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckChannelsAsync(stoppingToken);
                await CleanupStaleImportsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in worker loop");
            }

            // Wait for next check interval
            await Task.Delay(_checkInterval, stoppingToken);
        }

        _logger.LogInformation("YouTube Channel Monitor Worker stopped.");
    }

    private async Task CheckChannelsAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var channelService = scope.ServiceProvider.GetRequiredService<ChannelMonitorService>();
        var pipelineService = scope.ServiceProvider.GetRequiredService<VideoImportPipelineService>();

        var channels = await channelService.GetChannelsDueForCheckAsync();
        _logger.LogInformation("Found {Count} channels due for check", channels.Count);

        foreach (var channel in channels)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
                _logger.LogInformation("Checking channel: {Name} ({Handle})", channel.ChannelName, channel.ChannelHandle);

                // Get recent videos
                var videos = await channelService.GetRecentVideosAsync(channel, maxResults: 20);
                _logger.LogDebug("Found {Count} recent videos for channel {Name}", videos.Count, channel.ChannelName);

                var newImportCount = 0;

                foreach (var (videoId, title, url) in videos)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    // Check if already imported
                    if (await channelService.IsVideoAlreadyImportedAsync(videoId, channel.UserProfileId!))
                    {
                        _logger.LogDebug("Video {Title} already imported, skipping", title);
                        continue;
                    }

                    // Create and run import
                    var import = new VideoImport
                    {
                        UserProfileId = channel.UserProfileId,
                        MonitoredChannelId = channel.Id,
                        VideoId = videoId,
                        VideoTitle = title,
                        VideoUrl = url,
                        Language = channel.Language,
                        CreatedAt = DateTime.UtcNow
                    };

                    _logger.LogInformation("Starting import for new video: {Title}", title);

                    // Run pipeline in background (non-blocking)
                    _ = Task.Run(async () =>
                    {
                        using var pipelineScope = _serviceProvider.CreateScope();
                        var pipeline = pipelineScope.ServiceProvider.GetRequiredService<VideoImportPipelineService>();
                        try
                        {
                            await pipeline.RunPipelineAsync(import);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Pipeline failed for video {Title}", title);
                        }
                    }, cancellationToken);

                    newImportCount++;

                    // Rate limiting to avoid YouTube throttling
                    await Task.Delay(500, cancellationToken);
                }

                // Mark channel as checked
                await channelService.MarkCheckedAsync(channel.Id);
                _logger.LogInformation("Channel {Name} check complete. {NewCount} new videos queued for import.", 
                    channel.ChannelName, newImportCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking channel {Name}", channel.ChannelName);
            }
        }
    }

    private async Task CleanupStaleImportsAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var staleThreshold = DateTime.UtcNow.AddMinutes(-30);

        // Find imports stuck in processing for more than 30 minutes
        var staleImports = await db.VideoImports
            .Where(vi => vi.Status != VideoImportStatus.Completed 
                      && vi.Status != VideoImportStatus.Failed
                      && vi.CreatedAt < staleThreshold)
            .ToListAsync(cancellationToken);

        if (staleImports.Any())
        {
            _logger.LogWarning("Found {Count} stale imports, marking as failed", staleImports.Count);

            foreach (var import in staleImports)
            {
                import.Status = VideoImportStatus.Failed;
                import.ErrorMessage = "Import timed out after 30 minutes";
            }

            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
