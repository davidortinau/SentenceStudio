using System.Linq;
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

    public Task SeedAsync(string languageCode, CancellationToken ct = default)
        => SeedCoreAsync(languageCode, static () => { }, ct);

    /// <summary>Seeds on the caller's locked startup context, checking its session between awaits.</summary>
    public Task SeedAsync(string languageCode, Action assertSession, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(assertSession);
        return SeedCoreAsync(languageCode, assertSession, ct);
    }

    private async Task SeedCoreAsync(string languageCode, Action assertSession, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        assertSession();
        // Prefer embedded resource (works regardless of CWD or deployment layout).
        // Fall back to filesystem path for tests/dev where the file is added directly.
        string? jsonContent = null;
        
        // Embedded resource name is "<RootNamespace>.<LinkBase>.<file>".
        // SentenceStudio.Shared.csproj uses RootNamespace=SentenceStudio.Shared and LinkBase=Numbers,
        // so the actual embedded resource is "SentenceStudio.Shared.Numbers.{lang}.json".
        // Match by suffix to be resilient to namespace changes.
        var assembly = typeof(NumberContentSeeder).Assembly;
        var suffix = $".Numbers.{languageCode}.json";
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            ?? $"Numbers.{languageCode}.json";

        await using (var stream = assembly.GetManifestResourceStream(resourceName))
        {
            if (stream != null)
            {
                using var reader = new StreamReader(stream);
                jsonContent = await reader.ReadToEndAsync(ct);
                assertSession();
            }
        }

        if (jsonContent == null)
        {
            var contentFilePath = Path.Combine("lib", "content", "numbers", $"{languageCode}.json");
            if (!File.Exists(contentFilePath))
            {
                _logger.LogWarning(
                    "Number content seed not found. Tried embedded resource '{ResourceName}' (available: {Available}) and file '{FilePath}'.",
                    resourceName,
                    string.Join(", ", assembly.GetManifestResourceNames().Where(n => n.Contains("Numbers", StringComparison.OrdinalIgnoreCase))),
                    contentFilePath);
                return;
            }
            jsonContent = await File.ReadAllTextAsync(contentFilePath, ct);
            assertSession();
        }

        assertSession();
        // FIXED: Use source-generated JsonSerializerContext for AOT safety (iOS Release trimming)
        var seedData = JsonSerializer.Deserialize(jsonContent, NumberContentSeedJsonContext.Default.NumberContentSeed);

        if (seedData == null)
        {
            _logger.LogWarning("Failed to deserialize number content seed for language {LanguageCode}", languageCode);
            return;
        }

        int contextsInserted = 0, contextsUpdated = 0;
        int subModesInserted = 0, subModesUpdated = 0;
        int countersInserted = 0, countersUpdated = 0;

        // Seed contexts
        foreach (var contextDto in seedData.Contexts)
        {
            ct.ThrowIfCancellationRequested();
            assertSession();
            var existing = await _db.NumberContexts
                .FirstOrDefaultAsync(c => c.Code == contextDto.Code, ct);
            assertSession();

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
            ct.ThrowIfCancellationRequested();
            assertSession();
            var existing = await _db.NumberSubModes
                .FirstOrDefaultAsync(sm => sm.Code == subModeDto.Code, ct);
            assertSession();

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
            ct.ThrowIfCancellationRequested();
            assertSession();
            var existing = await _db.NumberCounters
                .FirstOrDefaultAsync(c => c.LanguageCode == languageCode && c.Counter == counterDto.Counter, ct);
            assertSession();

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

        assertSession();
        await _db.SaveChangesAsync(ct);
        assertSession();

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
