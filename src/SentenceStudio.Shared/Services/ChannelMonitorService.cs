using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;
using YoutubeExplode;
using YoutubeExplode.Channels;

namespace SentenceStudio.Services;

/// <summary>
/// CRUD + channel discovery for MonitoredChannels.
/// Uses YoutubeExplode to resolve handles and list channel videos.
/// </summary>
public class ChannelMonitorService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ChannelMonitorService> _logger;
    private readonly YoutubeClient _youtube = new();

    public ChannelMonitorService(
        IServiceProvider serviceProvider,
        ILogger<ChannelMonitorService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    // ────────────────────────── CRUD ──────────────────────────

    public async Task<List<MonitoredChannel>> GetAllAsync(string userProfileId)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.MonitoredChannels
            .Where(c => c.UserProfileId == userProfileId)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync();
    }

    public async Task<MonitoredChannel?> GetByIdAsync(string id)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.MonitoredChannels.FindAsync(id);
    }

    public async Task<MonitoredChannel> AddAsync(MonitoredChannel channel)
    {
        // Resolve channel metadata from YouTube if we have a URL
        if (!string.IsNullOrWhiteSpace(channel.ChannelUrl))
        {
            await ResolveChannelMetadataAsync(channel);
        }

        channel.CreatedAt = DateTime.UtcNow;
        channel.UpdatedAt = DateTime.UtcNow;

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.MonitoredChannels.Add(channel);
        await db.SaveChangesAsync();

        _logger.LogInformation("Added monitored channel {Name} ({Handle})", channel.ChannelName, channel.ChannelHandle);
        return channel;
    }

    public async Task UpdateAsync(MonitoredChannel channel)
    {
        channel.UpdatedAt = DateTime.UtcNow;

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.MonitoredChannels.Update(channel);
        await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(string id)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var channel = await db.MonitoredChannels.FindAsync(id);
        if (channel != null)
        {
            db.MonitoredChannels.Remove(channel);
            await db.SaveChangesAsync();
            _logger.LogInformation("Deleted monitored channel {Id}", id);
        }
    }

    // ────────────────────── Channel Discovery ──────────────────────

    /// <summary>
    /// Resolve a channel URL/handle into full metadata via YoutubeExplode.
    /// </summary>
    public async Task ResolveChannelMetadataAsync(MonitoredChannel channel)
    {
        try
        {
            var url = channel.ChannelUrl!.Trim();
            Channel ytChannel;

            if (url.Contains("/@") || url.StartsWith("@"))
            {
                // Handle-based URL — extract bare handle (no @ or /)
                var handle = url.Contains("/@")
                    ? url.Substring(url.IndexOf("/@") + 2)  // skip the "/@"
                    : url.TrimStart('@');
                // Strip any trailing path segments (e.g. /@handle/videos)
                var slashIdx = handle.IndexOf('/');
                if (slashIdx > 0) handle = handle[..slashIdx];
                ytChannel = await _youtube.Channels.GetByHandleAsync(handle);
            }
            else if (url.Contains("/channel/"))
            {
                var channelId = ChannelId.Parse(url);
                ytChannel = await _youtube.Channels.GetAsync(channelId);
            }
            else
            {
                // Try as a slug or custom URL
                ytChannel = await _youtube.Channels.GetBySlugAsync(url.Split('/').Last());
            }

            channel.ChannelName = ytChannel.Title;
            channel.YouTubeChannelId = ytChannel.Id.Value;
            channel.ChannelUrl = ytChannel.Url;

            _logger.LogInformation("Resolved channel: {Name} ({Id})", ytChannel.Title, ytChannel.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resolve channel metadata for {Url}", channel.ChannelUrl);
        }
    }

    /// <summary>
    /// Lists recent video IDs from a channel. Returns (videoId, title, url) tuples.
    /// </summary>
    public async Task<List<(string VideoId, string Title, string Url)>> GetRecentVideosAsync(
        MonitoredChannel channel,
        int maxResults = 20)
    {
        var results = new List<(string, string, string)>();

        try
        {
            var channelId = new ChannelId(channel.YouTubeChannelId
                ?? throw new InvalidOperationException("Channel has no YouTube ID"));

            await foreach (var video in _youtube.Channels.GetUploadsAsync(channelId))
            {
                // Skip Shorts (≤60s duration) — they're unsupported (no transcripts)
                if (video.Duration.HasValue && video.Duration.Value.TotalSeconds <= 60)
                {
                    _logger.LogDebug("Skipping Short: {Title} ({Duration}s)", video.Title, video.Duration.Value.TotalSeconds);
                    continue;
                }

                results.Add((video.Id.Value, video.Title, video.Url));

                if (results.Count >= maxResults)
                    break;

                // Small delay to avoid hammering YouTube
                await Task.Delay(200);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list videos for channel {Name}", channel.ChannelName);
        }

        return results;
    }

    /// <summary>
    /// Returns channels that are active and due for a check.
    /// </summary>
    public async Task<List<MonitoredChannel>> GetChannelsDueForCheckAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var now = DateTime.UtcNow;

        return await db.MonitoredChannels
            .Where(c => c.IsActive)
            .Where(c => c.LastCheckedAt == null ||
                        c.LastCheckedAt.Value.AddHours(c.CheckIntervalHours) <= now)
            .ToListAsync();
    }

    /// <summary>
    /// Marks a channel as just checked.
    /// </summary>
    public async Task MarkCheckedAsync(string channelId)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var channel = await db.MonitoredChannels.FindAsync(channelId);
        if (channel != null)
        {
            channel.LastCheckedAt = DateTime.UtcNow;
            channel.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Checks if a video has already been imported for this user.
    /// </summary>
    public async Task<bool> IsVideoAlreadyImportedAsync(string videoId, string userProfileId)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.VideoImports
            .AnyAsync(vi => vi.VideoId == videoId && vi.UserProfileId == userProfileId);
    }
}
