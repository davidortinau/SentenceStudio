using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Data;

/// <summary>
/// Repository for managing ConversationScenario entities.
/// </summary>
public class ScenarioRepository
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ISyncService? _syncService;
    private readonly ILogger<ScenarioRepository> _logger;

    public ScenarioRepository(IServiceProvider serviceProvider, ILogger<ScenarioRepository> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _syncService = serviceProvider.GetService<ISyncService>();
    }

    /// <summary>
    /// Gets all scenarios ordered by predefined first, then by name.
    /// </summary>
    public async Task<List<ConversationScenario>> GetAllAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        // SQLite can't translate OrderByDescending on boolean, so we use client-side sorting
        var scenarios = await db.ConversationScenarios.ToListAsync();
        return scenarios
            .OrderByDescending(s => s.IsPredefined)
            .ThenBy(s => s.Name)
            .ToList();
    }

    /// <summary>
    /// Gets a scenario by ID.
    /// </summary>
    public async Task<ConversationScenario?> GetByIdAsync(int id)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        return await db.ConversationScenarios.FindAsync(id);
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
    /// Gets user-created scenarios only.
    /// </summary>
    public async Task<List<ConversationScenario>> GetUserScenariosAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        return await db.ConversationScenarios
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
        
        return await db.ConversationScenarios
            .FirstOrDefaultAsync(s => s.IsPredefined && s.Name == "First Meeting");
    }

    /// <summary>
    /// Finds a scenario by name (case-insensitive partial match).
    /// </summary>
    public async Task<ConversationScenario?> FindByNameAsync(string name)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        var lowerName = name.ToLowerInvariant();
        return await db.ConversationScenarios
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
    /// Saves a new scenario or updates an existing one.
    /// </summary>
    public async Task<int> SaveAsync(ConversationScenario scenario)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        scenario.UpdatedAt = DateTime.UtcNow;
        if (scenario.CreatedAt == default)
            scenario.CreatedAt = DateTime.UtcNow;

        try
        {
            if (scenario.Id > 0)
            {
                db.ConversationScenarios.Update(scenario);
            }
            else
            {
                db.ConversationScenarios.Add(scenario);
            }

            await db.SaveChangesAsync();
            _syncService?.TriggerSyncAsync().ConfigureAwait(false);

            _logger.LogInformation("Saved scenario: {Name} (ID: {Id})", scenario.Name, scenario.Id);
            return scenario.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving scenario: {Name}", scenario.Name);
            return -1;
        }
    }

    /// <summary>
    /// Deletes a scenario. Returns false if predefined or not found.
    /// </summary>
    public async Task<bool> DeleteAsync(int id)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        try
        {
            var scenario = await db.ConversationScenarios.FindAsync(id);
            if (scenario == null)
            {
                _logger.LogWarning("Attempted to delete non-existent scenario: {Id}", id);
                return false;
            }

            if (scenario.IsPredefined)
            {
                _logger.LogWarning("Attempted to delete predefined scenario: {Name}", scenario.Name);
                return false;
            }

            // Set ScenarioId to null on any conversations using this scenario
            var conversations = await db.Conversations
                .Where(c => c.ScenarioId == id)
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
