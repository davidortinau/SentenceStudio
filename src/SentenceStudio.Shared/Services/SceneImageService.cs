using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.Services;

/// <summary>
/// The learner's Describe a Scene images (web and MAUI). Every read and write is scoped to the
/// active learner (<see cref="ActiveOwner"/>); with no learner it returns nothing and writes
/// nothing. Images saved before they had an owner stay visible only on the device that holds
/// them, never on the multi-tenant server, and never sync (phase 3, D3).
/// </summary>
public class SceneImageService
{
    private readonly IServiceProvider _serviceProvider;
    private ISyncService _syncService;
    private readonly ILogger<SceneImageService> _logger;

    public SceneImageService(IServiceProvider serviceProvider, ILogger<SceneImageService> logger)
    {
        _serviceProvider = serviceProvider;
        _syncService = serviceProvider.GetService<ISyncService>();
        _logger = logger;
    }

    private static IQueryable<SceneImage> Visible(ApplicationDbContext db, string owner) =>
        ActiveOwner.ShowsUnownedRows(db)
            ? db.SceneImages.Where(image => image.UserProfileId == owner || image.UserProfileId == null)
            : db.SceneImages.Where(image => image.UserProfileId == owner);

    public async Task<List<SceneImage>> ListAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        if (!ActiveOwner.TryResolve(scope.ServiceProvider, _logger, nameof(ListAsync), out var owner))
            return [];
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await Visible(db, owner).ToListAsync();
    }

    public async Task<SceneImage?> GetAsync(string url)
    {
        using var scope = _serviceProvider.CreateScope();
        if (!ActiveOwner.TryResolve(scope.ServiceProvider, _logger, nameof(GetAsync), out var owner))
            return null;
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await Visible(db, owner).Where(i => i.Url == url).FirstOrDefaultAsync();
    }

    public async Task<int> SaveAsync(SceneImage item)
    {
        using var scope = _serviceProvider.CreateScope();
        if (!ActiveOwner.TryResolve(scope.ServiceProvider, _logger, nameof(SaveAsync), out var owner))
            return -1;
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        try
        {
            var stored = await db.SceneImages.AsNoTracking()
                .Where(image => image.Id == item.Id)
                .Select(image => new { image.UserProfileId })
                .SingleOrDefaultAsync();
            if (stored is null)
            {
                item.UserProfileId = owner;
                db.SceneImages.Add(item);
            }
            else if (stored.UserProfileId == owner
                || (stored.UserProfileId is null && ActiveOwner.ShowsUnownedRows(db)))
            {
                // The stored owner is kept; a caller cannot move an image to another learner.
                item.UserProfileId = stored.UserProfileId;
                db.SceneImages.Update(item);
            }
            else
            {
                _logger.LogWarning("SceneImageService.SaveAsync refused an image outside the active learner.");
                return -1;
            }

            int result = await db.SaveChangesAsync();

            _syncService?.TriggerSyncAsync().ConfigureAwait(false);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred in SaveAsync");
            return -1;
        }
    }

    public async Task<int> DeleteAsync(SceneImage item)
    {
        using var scope = _serviceProvider.CreateScope();
        if (!ActiveOwner.TryResolve(scope.ServiceProvider, _logger, nameof(DeleteAsync), out var owner))
            return -1;
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        try
        {
            var stored = await Visible(db, owner).SingleOrDefaultAsync(image => image.Id == item.Id);
            if (stored is null)
                return 0;
            db.SceneImages.Remove(stored);
            int result = await db.SaveChangesAsync();

            _syncService?.TriggerSyncAsync().ConfigureAwait(false);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred in DeleteAsync");
            return -1;
        }
    }

    public async Task<SceneImage?> GetRandomAsync()
    {
        var images = await ListAsync();
        return images.Count > 0 ? images[Random.Shared.Next(images.Count)] : null;
    }
}
