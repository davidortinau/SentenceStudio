using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace SentenceStudio.Services;

public class DataExportService
{
    private readonly IServiceProvider _serviceProvider;

    public DataExportService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public async Task<MemoryStream> ExportAllDataAsZipAsync(IProgress<string> progress = null)
    {
        progress?.Report("Preparing data export...");
        
        var zipStream = new MemoryStream();
        
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, true))
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // Export UserProfiles
            progress?.Report("Exporting user profiles...");
            await AddCsvToZip(archive, "UserProfiles.csv", await ExportUserProfilesToCsv(db));

            // Export LearningResources
            progress?.Report("Exporting learning resources...");
            await AddCsvToZip(archive, "LearningResources.csv", await ExportLearningResourcesToCsv(db));

            // Export VocabularyWords
            progress?.Report("Exporting vocabulary words...");
            await AddCsvToZip(archive, "VocabularyWords.csv", await ExportVocabularyWordsToCsv(db));

            // Export VocabularyLists
            progress?.Report("Exporting vocabulary lists...");
            await AddCsvToZip(archive, "VocabularyLists.csv", await ExportVocabularyListsToCsv(db));

            // Export UserActivities
            progress?.Report("Exporting user activities...");
            await AddCsvToZip(archive, "UserActivities.csv", await ExportUserActivitiesToCsv(db));

            // Export StreamHistory
            progress?.Report("Exporting stream history...");
            await AddCsvToZip(archive, "StreamHistory.csv", await ExportStreamHistoryToCsv(db));

            // Export Challenges
            progress?.Report("Exporting challenges...");
            await AddCsvToZip(archive, "Challenges.csv", await ExportChallengesToCsv(db));

            // Export Stories
            progress?.Report("Exporting stories...");
            await AddCsvToZip(archive, "Stories.csv", await ExportStoriesToCsv(db));

            // Export GradeResponses
            progress?.Report("Exporting grade responses...");
            await AddCsvToZip(archive, "GradeResponses.csv", await ExportGradeResponsesToCsv(db));

            // Export SkillProfiles
            progress?.Report("Exporting skill profiles...");
            await AddCsvToZip(archive, "SkillProfiles.csv", await ExportSkillProfilesToCsv(db));

            // Export ResourceVocabularyMappings
            progress?.Report("Exporting resource-vocabulary mappings...");
            await AddCsvToZip(archive, "ResourceVocabularyMappings.csv", await ExportResourceVocabularyMappingsToCsv(db));

            // Add README file
            progress?.Report("Adding documentation...");
            await AddCsvToZip(archive, "README.txt", CreateReadmeContent());
        }

        progress?.Report("Export completed!");
        zipStream.Position = 0;
        return zipStream;
    }

    private async Task AddCsvToZip(ZipArchive archive, string fileName, string csvContent)
    {
        var entry = archive.CreateEntry(fileName);
        using var entryStream = entry.Open();
        using var writer = new StreamWriter(entryStream, Encoding.UTF8);
        await writer.WriteAsync(csvContent);
    }

    private async Task<string> ExportUserProfilesToCsv(ApplicationDbContext db)
    {
        var profiles = await db.UserProfiles.ToListAsync();
        var csv = new StringBuilder();
        csv.AppendLine("Id,Name,Email,NativeLanguage,TargetLanguage,DisplayLanguage,OpenAI_APIKey,CreatedAt");
        
        foreach (var profile in profiles)
        {
            csv.AppendLine($"{profile.Id},{EscapeCsv(profile.Name)},{EscapeCsv(profile.Email)},{EscapeCsv(profile.NativeLanguage)},{EscapeCsv(profile.TargetLanguage)},{EscapeCsv(profile.DisplayLanguage)},{EscapeCsv(profile.OpenAI_APIKey)},{profile.CreatedAt:yyyy-MM-dd HH:mm:ss}");
        }
        
        return csv.ToString();
    }

    private async Task<string> ExportLearningResourcesToCsv(ApplicationDbContext db)
    {
        var resources = await db.LearningResources.ToListAsync();
        var csv = new StringBuilder();
        csv.AppendLine("Id,Title,Description,Language,MediaType,MediaUrl,Tags,Transcript,Translation,CreatedAt,UpdatedAt");
        
        foreach (var resource in resources)
        {
            csv.AppendLine($"{resource.Id},{EscapeCsv(resource.Title)},{EscapeCsv(resource.Description)},{EscapeCsv(resource.Language)},{EscapeCsv(resource.MediaType)},{EscapeCsv(resource.MediaUrl)},{EscapeCsv(resource.Tags)},{EscapeCsv(resource.Transcript)},{EscapeCsv(resource.Translation)},{resource.CreatedAt:yyyy-MM-dd HH:mm:ss},{resource.UpdatedAt:yyyy-MM-dd HH:mm:ss}");
        }
        
        return csv.ToString();
    }

    private async Task<string> ExportVocabularyWordsToCsv(ApplicationDbContext db)
    {
        var words = await db.VocabularyWords.ToListAsync();
        var csv = new StringBuilder();
        csv.AppendLine("Id,NativeLanguageTerm,TargetLanguageTerm,CreatedAt,UpdatedAt");
        
        foreach (var word in words)
        {
            csv.AppendLine($"{word.Id},{EscapeCsv(word.NativeLanguageTerm)},{EscapeCsv(word.TargetLanguageTerm)},{word.CreatedAt:yyyy-MM-dd HH:mm:ss},{word.UpdatedAt:yyyy-MM-dd HH:mm:ss}");
        }
        
        return csv.ToString();
    }

    private async Task<string> ExportVocabularyListsToCsv(ApplicationDbContext db)
    {
        var lists = await db.VocabularyLists.ToListAsync();
        var csv = new StringBuilder();
        csv.AppendLine("Id,Name,CreatedAt,UpdatedAt");
        
        foreach (var list in lists)
        {
            csv.AppendLine($"{list.Id},{EscapeCsv(list.Name)},{list.CreatedAt:yyyy-MM-dd HH:mm:ss},{list.UpdatedAt:yyyy-MM-dd HH:mm:ss}");
        }
        
        return csv.ToString();
    }

    private async Task<string> ExportUserActivitiesToCsv(ApplicationDbContext db)
    {
        var activities = await db.UserActivities.ToListAsync();
        var csv = new StringBuilder();
        csv.AppendLine("Id,Activity,Input,Accuracy,Fluency,CreatedAt,UpdatedAt");
        
        foreach (var activity in activities)
        {
            csv.AppendLine($"{activity.Id},{EscapeCsv(activity.Activity)},{EscapeCsv(activity.Input)},{activity.Accuracy},{activity.Fluency},{activity.CreatedAt:yyyy-MM-dd HH:mm:ss},{activity.UpdatedAt:yyyy-MM-dd HH:mm:ss}");
        }
        
        return csv.ToString();
    }

    private async Task<string> ExportStreamHistoryToCsv(ApplicationDbContext db)
    {
        var streams = await db.StreamHistories.ToListAsync();
        var csv = new StringBuilder();
        csv.AppendLine("Id,Title,Phrase,Source,SourceUrl,AudioFilePath,FileName,VoiceId,Duration,CreatedAt,UpdatedAt");
        
        foreach (var stream in streams)
        {
            csv.AppendLine($"{stream.Id},{EscapeCsv(stream.Title)},{EscapeCsv(stream.Phrase)},{EscapeCsv(stream.Source)},{EscapeCsv(stream.SourceUrl)},{EscapeCsv(stream.AudioFilePath)},{EscapeCsv(stream.FileName)},{EscapeCsv(stream.VoiceId)},{stream.Duration},{stream.CreatedAt:yyyy-MM-dd HH:mm:ss},{stream.UpdatedAt:yyyy-MM-dd HH:mm:ss}");
        }
        
        return csv.ToString();
    }

    private async Task<string> ExportChallengesToCsv(ApplicationDbContext db)
    {
        var challenges = await db.Challenges.ToListAsync();
        var csv = new StringBuilder();
        csv.AppendLine("Id,SentenceText,VocabularyWord,VocabularyWordAsUsed,VocabularyWordGuesses,RecommendedTranslation,CreatedAt,UpdatedAt");
        
        foreach (var challenge in challenges)
        {
            csv.AppendLine($"{challenge.Id},{EscapeCsv(challenge.SentenceText)},{EscapeCsv(challenge.VocabularyWord)},{EscapeCsv(challenge.VocabularyWordAsUsed)},{EscapeCsv(challenge.VocabularyWordGuesses)},{EscapeCsv(challenge.RecommendedTranslation)},{challenge.CreatedAt:yyyy-MM-dd HH:mm:ss},{challenge.UpdatedAt:yyyy-MM-dd HH:mm:ss}");
        }
        
        return csv.ToString();
    }

    private async Task<string> ExportStoriesToCsv(ApplicationDbContext db)
    {
        var stories = await db.Stories.ToListAsync();
        var csv = new StringBuilder();
        csv.AppendLine("Id,Body,ListID,SkillID,CreatedAt,UpdatedAt");
        
        foreach (var story in stories)
        {
            csv.AppendLine($"{story.Id},{EscapeCsv(story.Body)},{story.ListID},{story.SkillID},{story.CreatedAt:yyyy-MM-dd HH:mm:ss},{story.UpdatedAt:yyyy-MM-dd HH:mm:ss}");
        }
        
        return csv.ToString();
    }

    private async Task<string> ExportGradeResponsesToCsv(ApplicationDbContext db)
    {
        var grades = await db.GradeResponses.ToListAsync();
        var csv = new StringBuilder();
        csv.AppendLine("Id,ChallengeID,Accuracy,AccuracyExplanation,Fluency,FluencyExplanation,RecommendedTranslation,CreatedAt");
        
        foreach (var grade in grades)
        {
            csv.AppendLine($"{grade.Id},{grade.ChallengeID},{grade.Accuracy},{EscapeCsv(grade.AccuracyExplanation)},{grade.Fluency},{EscapeCsv(grade.FluencyExplanation)},{EscapeCsv(grade.RecommendedTranslation)},{grade.CreatedAt:yyyy-MM-dd HH:mm:ss}");
        }
        
        return csv.ToString();
    }

    private async Task<string> ExportSkillProfilesToCsv(ApplicationDbContext db)
    {
        var skills = await db.SkillProfiles.ToListAsync();
        var csv = new StringBuilder();
        csv.AppendLine("Id,Title,Description,Language,CreatedAt,UpdatedAt");
        
        foreach (var skill in skills)
        {
            csv.AppendLine($"{skill.Id},{EscapeCsv(skill.Title)},{EscapeCsv(skill.Description)},{EscapeCsv(skill.Language)},{skill.CreatedAt:yyyy-MM-dd HH:mm:ss},{skill.UpdatedAt:yyyy-MM-dd HH:mm:ss}");
        }
        
        return csv.ToString();
    }

    private async Task<string> ExportResourceVocabularyMappingsToCsv(ApplicationDbContext db)
    {
        var mappings = await db.ResourceVocabularyMappings.ToListAsync();
        var csv = new StringBuilder();
        csv.AppendLine("Id,ResourceId,VocabularyWordId");
        
        foreach (var mapping in mappings)
        {
            csv.AppendLine($"{mapping.Id},{mapping.ResourceId},{mapping.VocabularyWordId}");
        }
        
        return csv.ToString();
    }

    private string EscapeCsv(string field)
    {
        if (string.IsNullOrEmpty(field))
            return "";
            
        // Escape quotes and wrap in quotes if needed
        if (field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r'))
        {
            return $"\"{field.Replace("\"", "\"\"")}\"";
        }
        
        return field;
    }

    private string CreateReadmeContent()
    {
        return @"Sentence Studio Data Export
==========================

This export contains all your data from Sentence Studio in CSV format.

Files included:
- UserProfiles.csv: Your user profile information
- LearningResources.csv: Learning materials and resources
- VocabularyWords.csv: All vocabulary words in your collection
- VocabularyLists.csv: Vocabulary list information
- UserActivities.csv: Your learning activity history
- StreamHistory.csv: Audio/media stream history
- Challenges.csv: Challenge data
- Stories.csv: Story content
- GradeResponses.csv: Performance and grading data
- SkillProfiles.csv: Skill tracking information
- ResourceVocabularyMappings.csv: Links between learning resources and vocabulary words

Export Date: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + @"

Note: CSV files can be opened in Excel, Google Sheets, or any text editor.
To import back into another system, refer to the column headers for field mapping.

The ResourceVocabularyMappings.csv file shows which vocabulary words are associated
with which learning resources, using ResourceId and VocabularyWordId foreign keys.

Generated by Sentence Studio
";
    }
}
