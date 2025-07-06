namespace SentenceStudio.Data;

public static class MigrationHelper
{
    /// <summary>
    /// Migrates vocabulary lists to learning resources
    /// </summary>
    /// <param name="vocabService">The vocabulary service</param>
    /// <param name="resourceRepo">The learning resource repository</param>
    /// <returns>A task representing the asynchronous operation</returns>
    public static async Task MigrateVocabularyListsAsync(VocabularyService vocabService, LearningResourceRepository resourceRepo)
    {
        try
        {
            // Get all old vocabulary lists
            var oldLists = await vocabService.GetListsAsync();
            int migratedCount = 0;
            
            Debug.WriteLine($"Found {oldLists.Count} vocabulary lists to migrate.");
            
            foreach (var list in oldLists)
            {
                // Check if a learning resource already exists for this vocabulary list
                var existingResources = await resourceRepo.GetAllResourcesAsync();
                if (existingResources.Any(r => r.OldVocabularyListID == list.Id))
                {
                    Debug.WriteLine($"Vocabulary list {list.Name} (ID: {list.Id}) already migrated. Skipping...");
                    continue;
                }
                
                // Create a new learning resource
                var resource = new LearningResource
                {
                    Title = list.Name,
                    Description = $"Migrated vocabulary list from {list.CreatedAt:d}",
                    MediaType = "Vocabulary List",
                    OldVocabularyListID = list.Id,
                    CreatedAt = list.CreatedAt,
                    UpdatedAt = DateTime.UtcNow,
                    Language = list.Words?.FirstOrDefault()?.TargetLanguageTerm?.Length > 0 ? 
                        "Unknown" : "Unknown" // You might want to detect language automatically
                };
                
                // Get the vocabulary words from the old list
                var vocabularyList = await vocabService.GetListAsync(list.Id);
                if (vocabularyList?.Words != null)
                {
                    resource.Vocabulary = vocabularyList.Words;
                }
                
                // Save the new resource
                await resourceRepo.SaveResourceAsync(resource);
                migratedCount++;
                
                Debug.WriteLine($"Migrated vocabulary list: {list.Name} with {resource.Vocabulary?.Count ?? 0} words.");
            }
            
            Debug.WriteLine($"Migration completed successfully! Migrated {migratedCount} lists.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Migration failed: {ex.Message}");
            await Application.Current.MainPage.DisplayAlert("Migration Error", $"Failed to migrate vocabulary lists: {ex.Message}", "OK");
        }
    }
}