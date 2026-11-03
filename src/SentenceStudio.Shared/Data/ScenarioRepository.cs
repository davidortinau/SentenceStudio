using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Data;

/// <summary>
/// Conversation scenarios. Built-in scenarios have the same fixed ID on every client
/// (<see cref="SyncIds.BuiltInScenario"/>) and are visible to every learner; custom scenarios
/// belong to the learner who created them (phase 3 of docs/sync-all-tables-plan.md). Every read
/// and write resolves the active learner (<see cref="ActiveOwner"/>): with none, reads return
/// built-ins only and nothing is written. Custom scenarios saved before they had an owner stay
/// visible only on the device that holds them, never on the multi-tenant server (D3).
/// </summary>
public class ScenarioRepository
{
    public const string DefaultScenarioName = "First Meeting";

    private readonly IServiceProvider _serviceProvider;
    private readonly ISyncService? _syncService;
    private readonly ILogger<ScenarioRepository> _logger;

    public ScenarioRepository(IServiceProvider serviceProvider, ILogger<ScenarioRepository> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _syncService = serviceProvider.GetService<ISyncService>();
    }

    private static IQueryable<ConversationScenario> Visible(ApplicationDbContext db, string? owner)
    {
        if (string.IsNullOrEmpty(owner))
            return db.ConversationScenarios.Where(s => s.IsPredefined);
        return ActiveOwner.ShowsUnownedRows(db)
            ? db.ConversationScenarios.Where(s => s.IsPredefined
                || s.UserProfileId == owner || s.UserProfileId == null)
            : db.ConversationScenarios.Where(s => s.IsPredefined || s.UserProfileId == owner);
    }

    private (ApplicationDbContext Db, string? Owner) Open(IServiceScope scope, string operation)
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return ActiveOwner.TryResolve(scope.ServiceProvider, _logger, operation, out var owner)
            ? (db, owner)
            : (db, null);
    }

    /// <summary>
    /// Gets all scenarios ordered by predefined first, then by name.
    /// </summary>
    public async Task<List<ConversationScenario>> GetAllAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var (db, owner) = Open(scope, nameof(GetAllAsync));

        // SQLite can't translate OrderByDescending on boolean, so we use client-side sorting
        var scenarios = await Visible(db, owner).ToListAsync();
        return scenarios
            .OrderByDescending(s => s.IsPredefined)
            .ThenBy(s => s.Name)
            .ToList();
    }

    /// <summary>
    /// Gets a scenario by ID; null when it is unknown or another learner's.
    /// </summary>
    public async Task<ConversationScenario?> GetByIdAsync(string id)
    {
        using var scope = _serviceProvider.CreateScope();
        var (db, owner) = Open(scope, nameof(GetByIdAsync));
        return await Visible(db, owner).FirstOrDefaultAsync(s => s.Id == id);
    }

    /// <summary>
    /// Gets predefined scenarios only.
    /// </summary>
    public async Task<List<ConversationScenario>> GetPredefinedAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.ConversationScenarios
            .Where(s => s.IsPredefined)
            .OrderBy(s => s.Name)
            .ToListAsync();
    }

    /// <summary>
    /// Gets the active learner's own scenarios only.
    /// </summary>
    public async Task<List<ConversationScenario>> GetUserScenariosAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var (db, owner) = Open(scope, nameof(GetUserScenariosAsync));
        if (owner is null)
            return [];

        return await Visible(db, owner)
            .Where(s => !s.IsPredefined)
            .OrderBy(s => s.Name)
            .ToListAsync();
    }

    /// <summary>
    /// Gets the default scenario (First Meeting).
    /// </summary>
    public async Task<ConversationScenario?> GetDefaultAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var id = SyncIds.BuiltInScenario(DefaultScenarioName);

        return await db.ConversationScenarios
            .FirstOrDefaultAsync(s => s.IsPredefined && s.Id == id);
    }

    /// <summary>
    /// Finds a scenario the learner can see by name (case-insensitive partial match).
    /// </summary>
    public async Task<ConversationScenario?> FindByNameAsync(string name)
    {
        using var scope = _serviceProvider.CreateScope();
        var (db, owner) = Open(scope, nameof(FindByNameAsync));

        var lowerName = name.ToLowerInvariant();
        return await Visible(db, owner)
            .FirstOrDefaultAsync(s => s.Name.ToLower().Contains(lowerName) ||
                                     (s.NameKorean != null && s.NameKorean.Contains(name)));
    }

    /// <summary>
    /// Checks if predefined scenarios exist.
    /// </summary>
    public async Task<bool> HasPredefinedScenariosAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.ConversationScenarios.AnyAsync(s => s.IsPredefined);
    }

    /// <summary>
    /// Adds the built-in scenarios this store lacks, under their fixed IDs. Built-ins belong to
    /// nobody and are never changed here once stored.
    /// </summary>
    public async Task<int> AddMissingBuiltInsAsync(IReadOnlyList<ConversationScenario> builtIns)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var ids = builtIns.Select(s => s.Id).ToArray();
        var stored = await db.ConversationScenarios.AsNoTracking()
            .Where(s => ids.Contains(s.Id)).Select(s => s.Id).ToListAsync();
        var missing = builtIns.Where(s => !stored.Contains(s.Id)).ToList();
        if (missing.Count == 0)
            return 0;
        var now = DateTime.UtcNow;
        foreach (var scenario in missing)
        {
            if (!scenario.IsPredefined || !SyncIds.IsBuiltInScenario(scenario.Id))
                throw new ArgumentException("A built-in scenario needs its fixed ID.", nameof(builtIns));
            scenario.UserProfileId = null;
            scenario.CreatedAt = scenario.CreatedAt == default ? now : scenario.CreatedAt;
            scenario.UpdatedAt = now;
        }
        db.ConversationScenarios.AddRange(missing);
        await db.SaveChangesAsync();
        return missing.Count;
    }

    /// <summary>
    /// Saves the active learner's new scenario or updates one of theirs. Returns the scenario's
    /// ID, or null when it was refused or failed. Built-ins are not saved here.
    /// </summary>
    public async Task<string?> SaveAsync(ConversationScenario scenario)
    {
        using var scope = _serviceProvider.CreateScope();
        var (db, owner) = Open(scope, nameof(SaveAsync));
        if (owner is null || scenario.IsPredefined || SyncIds.IsBuiltInScenario(scenario.Id))
        {
            _logger.LogWarning("ScenarioRepository.SaveAsync refused: no active learner or a built-in scenario.");
            return null;
        }

        scenario.UpdatedAt = DateTime.UtcNow;
        if (scenario.CreatedAt == default)
            scenario.CreatedAt = DateTime.UtcNow;

        try
        {
            var stored = await db.ConversationScenarios.AsNoTracking()
                .Where(s => s.Id == scenario.Id)
                .Select(s => new { s.UserProfileId, s.IsPredefined })
                .SingleOrDefaultAsync();
            if (stored is null)
            {
                scenario.UserProfileId = owner;
                db.ConversationScenarios.Add(scenario);
            }
            else if (!stored.IsPredefined && (stored.UserProfileId == owner
                || (stored.UserProfileId is null && ActiveOwner.ShowsUnownedRows(db))))
            {
                // The stored owner is kept; a caller cannot move a scenario to another learner.
                scenario.UserProfileId = stored.UserProfileId;
                db.ConversationScenarios.Update(scenario);
            }
            else
            {
                _logger.LogWarning("ScenarioRepository.SaveAsync refused a scenario outside the active learner.");
                return null;
            }

            await db.SaveChangesAsync();
            _syncService?.TriggerSyncAsync().ConfigureAwait(false);

            _logger.LogInformation("Saved scenario: {Name} (ID: {Id})", scenario.Name, scenario.Id);
            return scenario.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving scenario: {Name}", scenario.Name);
            return null;
        }
    }

    /// <summary>
    /// Deletes one of the active learner's scenarios. Returns false if predefined, not found or
    /// another learner's.
    /// </summary>
    public async Task<bool> DeleteAsync(string id)
    {
        using var scope = _serviceProvider.CreateScope();
        var (db, owner) = Open(scope, nameof(DeleteAsync));
        if (owner is null)
            return false;

        try
        {
            var scenario = await Visible(db, owner).FirstOrDefaultAsync(s => s.Id == id);
            if (scenario == null)
            {
                _logger.LogWarning("Attempted to delete a scenario that is unknown or not the learner's: {Id}", id);
                return false;
            }

            if (scenario.IsPredefined)
            {
                _logger.LogWarning("Attempted to delete predefined scenario: {Name}", scenario.Name);
                return false;
            }

            // The learner's conversations that used this scenario keep no reference to it.
            var conversations = await db.Conversations
                .Where(c => c.ScenarioId == id && c.UserProfileId == owner)
                .ToListAsync();
            foreach (var conv in conversations)
            {
                conv.ScenarioId = null;
            }

            db.ConversationScenarios.Remove(scenario);
            await db.SaveChangesAsync();
            _syncService?.TriggerSyncAsync().ConfigureAwait(false);

            _logger.LogInformation("Deleted scenario: {Name} (ID: {Id})", scenario.Name, id);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting scenario: {Id}", id);
            return false;
        }
    }
}
