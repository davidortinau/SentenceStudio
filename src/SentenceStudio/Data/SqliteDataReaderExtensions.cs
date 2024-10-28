using Microsoft.Data.Sqlite;

namespace SentenceStudio.Data;

public static class SqliteDataReaderExtensions
{
    public static SkillProfile ToSkillProfile(this SqliteDataReader reader)
    {
        SkillProfile p = null;
        try{
            p = new SkillProfile
            {
                ID = reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
                Title = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                Description = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                Language = reader.IsDBNull(3) ? string.Empty : reader.GetString(3)
            };        
        
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"{ex.Message}");
            // await Shell.Current.DisplayAlert("Error", ex.Message, "Fix it");
        }
        return p;
    }
}