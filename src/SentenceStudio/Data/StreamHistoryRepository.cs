using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.Data;

public class StreamHistoryRepository
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ISyncService? _syncService;
    private readonly ILogger<StreamHistoryRepository> _logger;

    public StreamHistoryRepository(IServiceProvider serviceProvider, ILogger<StreamHistoryRepository> logger, ISyncService? syncService = null)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _syncService = syncService;
    }

    public async Task<List<StreamHistory>> GetAllStreamHistoryAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.StreamHistories
            .OrderByDescending(h => h.CreatedAt)
            .ToListAsync();
    }

    public async Task<StreamHistory> GetStreamHistoryAsync(int id)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.StreamHistories
            .Where(h => h.Id == id)
            .FirstOrDefaultAsync();
    }

    public async Task<int> SaveStreamHistoryAsync(StreamHistory streamHistory)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        try
        {
            // Set timestamps
            if (streamHistory.CreatedAt == default)
                streamHistory.CreatedAt = DateTime.UtcNow;
                
            streamHistory.UpdatedAt = DateTime.UtcNow;
            
            if (streamHistory.Id != 0)
            {
                db.StreamHistories.Update(streamHistory);
            }
            else
            {
                db.StreamHistories.Add(streamHistory);
            }
            
            int result = await db.SaveChangesAsync();
            
            _syncService?.TriggerSyncAsync().ConfigureAwait(false);
            
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred in SaveStreamHistoryAsync");
            return -1;
        }
    }

    public async Task<int> DeleteStreamHistoryAsync(StreamHistory streamHistory)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        try
        {
            db.StreamHistories.Remove(streamHistory);
            int result = await db.SaveChangesAsync();
            
            _syncService?.TriggerSyncAsync().ConfigureAwait(false);
            
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred in DeleteStreamHistoryAsync");
            return -1;
        }
    }

    public async Task<List<StreamHistory>> SearchStreamHistoryAsync(string query)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.StreamHistories
            .Where(h => h.Phrase.Contains(query))
            .OrderByDescending(h => h.CreatedAt)
            .ToListAsync();
    }
    
    public async Task<List<StreamHistory>> GetStreamHistoryByVoiceAsync(string voiceId)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.StreamHistories
            .Where(h => h.VoiceId == voiceId)
            .OrderByDescending(h => h.CreatedAt)
            .ToListAsync();
    }
    
    public async Task<StreamHistory?> GetStreamHistoryByPhraseAndVoiceAsync(string phrase, string voiceId)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.StreamHistories
            .Where(h => h.Phrase == phrase && h.VoiceId == voiceId)
            .OrderByDescending(h => h.CreatedAt)
            .FirstOrDefaultAsync();
    }
}
