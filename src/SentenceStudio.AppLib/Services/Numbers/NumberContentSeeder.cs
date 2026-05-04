using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models.Numbers;

namespace SentenceStudio.Services.Numbers;

public class NumberContentSeeder
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<NumberContentSeeder> _logger;

    public NumberContentSeeder(ApplicationDbContext db, ILogger<NumberContentSeeder> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task SeedAsync(string languageCode, CancellationToken ct = default)
    {
        var contentFilePath = Path.Combine("lib", "content", "numbers", $"{languageCode}.json");
        
        if (!File.Exists(contentFilePath))
        {
            _logger.LogWarning("Number content seed file not found: {FilePath}", contentFilePath);
            return;
        }

        var jsonContent = await File.ReadAllTextAsync(contentFilePath, ct);
        var seedData = JsonSerializer.Deserialize<NumberContentSeed>(jsonContent, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (seedData == null)
        {
            _logger.LogWarning("Failed to deserialize number content seed file: {FilePath}", contentFilePath);
            return;
        }

        int contextsInserted = 0, contextsUpdated = 0;
        int subModesInserted = 0, subModesUpdated = 0;
        int countersInserted = 0, countersUpdated = 0;

        // Seed contexts
        foreach (var contextDto in seedData.Contexts)
        {
            var existing = await _db.NumberContexts
                .FirstOrDefaultAsync(c => c.Code == contextDto.Code, ct);

            if (existing == null)
            {
                var context = new NumberContext
                {
                    Id = Guid.NewGuid().ToString(),
                    Code = contextDto.Code,
                    DisplayName = contextDto.DisplayName,
                    Icon = contextDto.Icon,
                    DefaultSystem = Enum.Parse<NumberSystem>(contextDto.DefaultSystem),
                    SortOrder = contextDto.SortOrder,
                    IsActive = contextDto.IsActive
                };
                _db.NumberContexts.Add(context);
                contextsInserted++;
            }
            else
            {
                existing.DisplayName = contextDto.DisplayName;
                existing.Icon = contextDto.Icon;
                existing.DefaultSystem = Enum.Parse<NumberSystem>(contextDto.DefaultSystem);
                existing.SortOrder = contextDto.SortOrder;
                existing.IsActive = contextDto.IsActive;
                contextsUpdated++;
            }
        }

        // Seed sub-modes
        foreach (var subModeDto in seedData.SubModes)
        {
            var existing = await _db.NumberSubModes
                .FirstOrDefaultAsync(sm => sm.Code == subModeDto.Code, ct);

            if (existing == null)
            {
                var subMode = new NumberSubMode
                {
                    Id = Guid.NewGuid().ToString(),
                    Code = subModeDto.Code,
                    DisplayName = subModeDto.DisplayName,
                    Phase = subModeDto.Phase,
                    IsActive = subModeDto.IsActive
                };
                _db.NumberSubModes.Add(subMode);
                subModesInserted++;
            }
            else
            {
                existing.DisplayName = subModeDto.DisplayName;
                existing.Phase = subModeDto.Phase;
                existing.IsActive = subModeDto.IsActive;
                subModesUpdated++;
            }
        }

        // Seed counters
        foreach (var counterDto in seedData.Counters)
        {
            var existing = await _db.NumberCounters
                .FirstOrDefaultAsync(c => c.LanguageCode == languageCode && c.Counter == counterDto.Counter, ct);

            if (existing == null)
            {
                var counter = new NumberCounter
                {
                    Id = Guid.NewGuid().ToString(),
                    LanguageCode = languageCode,
                    Counter = counterDto.Counter,
                    Romanization = counterDto.Romanization,
                    MeaningEn = counterDto.MeaningEn,
                    System = Enum.Parse<NumberSystem>(counterDto.System),
                    Notes = counterDto.Notes
                };
                _db.NumberCounters.Add(counter);
                countersInserted++;
            }
            else
            {
                existing.Romanization = counterDto.Romanization;
                existing.MeaningEn = counterDto.MeaningEn;
                existing.System = Enum.Parse<NumberSystem>(counterDto.System);
                existing.Notes = counterDto.Notes;
                countersUpdated++;
            }
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Number content seeding complete for {LanguageCode}: " +
            "Contexts ({ContextsInserted} inserted, {ContextsUpdated} updated), " +
            "SubModes ({SubModesInserted} inserted, {SubModesUpdated} updated), " +
            "Counters ({CountersInserted} inserted, {CountersUpdated} updated)",
            languageCode,
            contextsInserted, contextsUpdated,
            subModesInserted, subModesUpdated,
            countersInserted, countersUpdated
        );
    }
}

// DTOs for JSON deserialization
internal record NumberContentSeed(
    string LanguageCode,
    int Version,
    List<NumberContextDto> Contexts,
    List<NumberSubModeDto> SubModes,
    List<NumberCounterDto> Counters
);

internal record NumberContextDto(
    string Code,
    string DisplayName,
    string Icon,
    string DefaultSystem,
    int SortOrder,
    bool IsActive
);

internal record NumberSubModeDto(
    string Code,
    string DisplayName,
    int Phase,
    bool IsActive
);

internal record NumberCounterDto(
    string Counter,
    string Romanization,
    string MeaningEn,
    string System,
    string Notes
);
