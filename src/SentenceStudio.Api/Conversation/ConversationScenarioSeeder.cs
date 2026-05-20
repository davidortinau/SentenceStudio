using Microsoft.EntityFrameworkCore;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Conversation;

/// <summary>
/// Seeds predefined <see cref="ConversationScenario"/> rows into the API database on startup.
/// Idempotent — re-running upserts existing rows (matched by Name + IsPredefined=true) and
/// inserts any that are missing. Mirrors the MAUI-side seed in
/// <c>ScenarioService.SeedPredefinedScenariosAsync</c>; both consume the same shared seed list
/// (<see cref="ConversationScenarioSeedData.GetPredefinedScenarios"/>).
/// </summary>
public class ConversationScenarioSeeder
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<ConversationScenarioSeeder> _logger;

    public ConversationScenarioSeeder(ApplicationDbContext db, ILogger<ConversationScenarioSeeder> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken ct = default)
    {
        // Guard against concurrent replicas seeding simultaneously (ACA scale-out, parallel restarts).
        // pg_advisory_xact_lock blocks until released at transaction commit; serializes seeders across
        // every connection in the cluster. The arbitrary int key (4242_4242) is unique to this seeder.
        // No-op on SQLite (tests) — that path is single-threaded by construction.
        var isPostgres = _db.Database.IsNpgsql();
        if (isPostgres)
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(42424242);", ct);
            await SeedCoreAsync(ct);
            await tx.CommitAsync(ct);
            return;
        }

        await SeedCoreAsync(ct);
    }

    private async Task SeedCoreAsync(CancellationToken ct)
    {
        var seedData = ConversationScenarioSeedData.GetPredefinedScenarios();

        // Load existing predefined rows once; match on Name (case-sensitive — seed names are canonical English).
        var existingPredefined = await _db.ConversationScenarios
            .Where(s => s.IsPredefined)
            .ToListAsync(ct);

        int inserted = 0;
        int updated = 0;

        foreach (var seed in seedData)
        {
            var existing = existingPredefined.FirstOrDefault(s => s.Name == seed.Name);
            if (existing is null)
            {
                seed.CreatedAt = DateTime.UtcNow;
                seed.UpdatedAt = DateTime.UtcNow;
                _db.ConversationScenarios.Add(seed);
                inserted++;
                continue;
            }

            bool changed =
                existing.NameKorean != seed.NameKorean ||
                existing.PersonaName != seed.PersonaName ||
                existing.PersonaDescription != seed.PersonaDescription ||
                existing.SituationDescription != seed.SituationDescription ||
                existing.ConversationType != seed.ConversationType ||
                existing.QuestionBank != seed.QuestionBank;

            if (changed)
            {
                existing.NameKorean = seed.NameKorean;
                existing.PersonaName = seed.PersonaName;
                existing.PersonaDescription = seed.PersonaDescription;
                existing.SituationDescription = seed.SituationDescription;
                existing.ConversationType = seed.ConversationType;
                existing.QuestionBank = seed.QuestionBank;
                existing.UpdatedAt = DateTime.UtcNow;
                updated++;
            }
        }

        if (inserted == 0 && updated == 0)
        {
            _logger.LogDebug("ConversationScenario seed: no changes ({Count} predefined rows already current)", seedData.Count);
            return;
        }

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "ConversationScenario seed: inserted {Inserted}, updated {Updated} predefined rows",
            inserted,
            updated);
    }
}
