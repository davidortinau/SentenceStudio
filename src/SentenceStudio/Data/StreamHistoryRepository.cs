using System.Diagnostics;
using SentenceStudio.Common;
using SentenceStudio.Shared.Models;
using SQLite;

namespace SentenceStudio.Data;

public class StreamHistoryRepository
{
    private SQLiteAsyncConnection Database;

    public StreamHistoryRepository()
    {
    }

    async Task Init()
    {
        if (Database is not null)
            return;

        Database = new SQLiteAsyncConnection(Constants.DatabasePath, Constants.Flags);

        try
        {
            await Database.CreateTableAsync<StreamHistory>();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"{ex.Message}");
            await App.Current.Windows[0].Page.DisplayAlert("Error", ex.Message, "Fix it");
        }
    }

    public async Task<List<StreamHistory>> GetAllStreamHistoryAsync()
    {
        await Init();
        return await Database.Table<StreamHistory>()
            .OrderByDescending(h => h.CreatedAt)
            .ToListAsync();
    }

    public async Task<StreamHistory> GetStreamHistoryAsync(int id)
    {
        await Init();
        return await Database.Table<StreamHistory>()
            .Where(h => h.ID == id)
            .FirstOrDefaultAsync();
    }

    public async Task<int> SaveStreamHistoryAsync(StreamHistory streamHistory)
    {
        await Init();
        
        // Set timestamps
        if (streamHistory.CreatedAt == default)
            streamHistory.CreatedAt = DateTime.UtcNow;
            
        streamHistory.UpdatedAt = DateTime.UtcNow;
        
        if (streamHistory.ID != 0)
        {
            return await Database.UpdateAsync(streamHistory);
        }
        else
        {
            return await Database.InsertAsync(streamHistory);
        }
    }

    public async Task<int> DeleteStreamHistoryAsync(StreamHistory streamHistory)
    {
        await Init();
        return await Database.DeleteAsync(streamHistory);
    }
    
    public async Task<List<StreamHistory>> SearchStreamHistoryAsync(string query)
    {
        await Init();
        return await Database.Table<StreamHistory>()
            .Where(h => h.Phrase.Contains(query))
            .OrderByDescending(h => h.CreatedAt)
            .ToListAsync();
    }
    
    public async Task<List<StreamHistory>> GetStreamHistoryByVoiceAsync(string voiceId)
    {
        await Init();
        return await Database.Table<StreamHistory>()
            .Where(h => h.VoiceId == voiceId)
            .OrderByDescending(h => h.CreatedAt)
            .ToListAsync();
    }
}
