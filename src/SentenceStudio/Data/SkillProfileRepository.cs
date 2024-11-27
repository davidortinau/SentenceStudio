using System.Diagnostics;
using SentenceStudio.Models;
using SQLite;
using SentenceStudio.Common;
using Microsoft.Data.Sqlite;
using Foundation;

namespace SentenceStudio.Data;

public class SkillProfileRepository
{
    private bool _hasBeenInitialized = false;

    public SkillProfileRepository()
    {        
    }

    async Task Init()
    {
        if (_hasBeenInitialized)
			return;

		await using var connection = new SqliteConnection(Constants.SQLDatabasePath);
		await connection.OpenAsync();

		try
		{
			var createTableCmd = connection.CreateCommand();
			createTableCmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS SkillProfile (
                    ID INTEGER PRIMARY KEY AUTOINCREMENT,
                    Title TEXT NOT NULL,
                    Description TEXT NOT NULL,
                    Language TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL DEFAULT (datetime('now')),
                    UpdatedAt TEXT NOT NULL DEFAULT (datetime('now'))
                );";
			await createTableCmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"{ex.Message}");
            await Shell.Current.DisplayAlert("Error", ex.Message, "Fix it");
        }

        _hasBeenInitialized = true;
    }

    public async Task<List<SkillProfile>> ListAsync()
    {
        await Init();
		await using var connection = new SqliteConnection(Constants.SQLDatabasePath);
		await connection.OpenAsync();

		var selectCmd = connection.CreateCommand();
		selectCmd.CommandText = "SELECT * FROM SkillProfile";
		var list = new List<SkillProfile>();

		await using var reader = await selectCmd.ExecuteReaderAsync();
		while (await reader.ReadAsync())
		{
			list.Add(reader.ToSkillProfile());
		}

		return list;
    }

    public async Task<List<SkillProfile>> GetSkillsByLanguageAsync(string language)
    {
        await Init();
        await using var connection = new SqliteConnection(Constants.SQLDatabasePath);
		await connection.OpenAsync();

		var selectCmd = connection.CreateCommand();
		selectCmd.CommandText = "SELECT * FROM SkillProfile WHERE Language = @language";
        selectCmd.Parameters.AddWithValue("@language", language);

        var list = new List<SkillProfile>();
        
        await using var reader = await selectCmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(reader.ToSkillProfile());
        }

        return list;
    }

    public async Task<int> SaveAsync(SkillProfile item)
    {
        await Init();
		await using var connection = new SqliteConnection(Constants.SQLDatabasePath);
		await connection.OpenAsync();

		var saveCmd = connection.CreateCommand();
		if (item.ID == 0)
		{
			saveCmd.CommandText = @"
                INSERT INTO SkillProfile (Title, Description, Language)
                VALUES (@Title, @Description, @Language);
                SELECT last_insert_rowid();";
		}
		else
		{
			saveCmd.CommandText = @"
                UPDATE SkillProfile
                SET Title = @Title, Description = @Description, Language = @Language
                WHERE ID = @ID";
			saveCmd.Parameters.AddWithValue("@ID", item.ID);
		}

		saveCmd.Parameters.AddWithValue("@Title", item.Title);
		saveCmd.Parameters.AddWithValue("@Description", item.Description);
		saveCmd.Parameters.AddWithValue("@Language", item.Language);
		// saveCmd.Parameters.AddWithValue("@CreatedAt", item.CreatedAt);
        // saveCmd.Parameters.AddWithValue("@UpdatedAt", item.UpdatedAt);

		object result = 0;
        try{
            result = await saveCmd.ExecuteScalarAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"{ex.Message}");
        }

        if (item.ID == 0)
        {
            item.ID = Convert.ToInt32(result);
        }
		

		return item.ID;
    }
    

    public async Task<int> DeleteAsync(SkillProfile item)
    {
        await Init();
		await using var connection = new SqliteConnection(Constants.SQLDatabasePath);
		await connection.OpenAsync();

		var deleteCmd = connection.CreateCommand();
		deleteCmd.CommandText = "DELETE FROM SkillProfile WHERE ID = @ID";
		deleteCmd.Parameters.AddWithValue("@ID", item.ID);

		return await deleteCmd.ExecuteNonQueryAsync();
    }

    internal async Task<SkillProfile> GetSkillProfileAsync(int skillID)
    {
        await Init();
		await using var connection = new SqliteConnection(Constants.SQLDatabasePath);
		await connection.OpenAsync();

		var selectCmd = connection.CreateCommand();
		selectCmd.CommandText = "SELECT * FROM SkillProfile WHERE ID = @id";
		selectCmd.Parameters.AddWithValue("@id", skillID);

		await using var reader = await selectCmd.ExecuteReaderAsync();
		if (await reader.ReadAsync())
		{
			return reader.ToSkillProfile();
		}

		return null;
    }
}
