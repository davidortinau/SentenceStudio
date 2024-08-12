using System.Diagnostics;
using SentenceStudio.Models;
using SQLite;
using SentenceStudio.Common;

namespace SentenceStudio.Data;

public class SkillProfileRepository
{
    private SQLiteAsyncConnection Database;

    public SkillProfileRepository()
    {
        
    }

    async Task Init()
    {
        if (Database is not null)
            return;

        Database = new SQLiteAsyncConnection(Constants.DatabasePath, Constants.Flags);

        CreateTableResult result;
        
        try
        {
            result = await Database.CreateTableAsync<SkillProfile>();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"{ex.Message}");
            await Shell.Current.DisplayAlert("Error", ex.Message, "Fix it");
        }
    }

    public async Task<List<SkillProfile>> ListAsync()
    {
        await Init();
        return await Database.Table<SkillProfile>().ToListAsync();
    }

    public async Task<List<SkillProfile>> GetSkillsByLanguageAsync(string language)
    {
        await Init();
        return await Database.Table<SkillProfile>().Where(i => i.Language == language).ToListAsync();
    }

    public async Task<int> SaveAsync(SkillProfile item)
    {
        await Init();
        int result = -1;
        if (item.ID != 0)
        {
            try
            {
                result = await Database.UpdateAsync(item);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"{ex.Message}");
            }
        }
        else
        {
            try
            {
                result = await Database.InsertAsync(item);
            }
            catch (Exception ex)
            {
                await Shell.Current.DisplayAlert("Error", ex.Message, "Fix it");
            }
        }

        return result;
    }
    

    public async Task<int> DeleteAsync(SkillProfile item)
    {
        await Init();
        return await Database.DeleteAsync(item);
    }

    internal async Task<SkillProfile> GetSkillProfileAsync(int skillID)
    {
        await Init();
        return await Database.Table<SkillProfile>().Where(i => i.ID == skillID).FirstOrDefaultAsync();
    }
}
