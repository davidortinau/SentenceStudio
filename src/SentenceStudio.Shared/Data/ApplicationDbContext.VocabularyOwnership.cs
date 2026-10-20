using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Data;

// Keeps VocabularyWord.UserProfileId correct for every save path (repositories, imports,
// Razor pages, native stores) without each caller remembering to set it.
//
// Only the server sees every account's links, so only the server infers owners for
// existing words. A SQLite store (native and MAUI apps) holds just its own account's
// links: it stamps only words it creates in the same save, and never a word another
// account may share. Owners for everything else arrive from the server via sync.
public partial class ApplicationDbContext
{
    private const string SqliteProvider = "Microsoft.EntityFrameworkCore.Sqlite";

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        var wordIds = PrepareVocabularyWordOwnership();
        var result = base.SaveChanges(acceptAllChangesOnSuccess);
        if (wordIds.Count > 0)
            StampOwnersAfterSave(() => VocabularyWordOwnership.Stamp(this, wordIds));
        return result;
    }

    public override async Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var wordIds = PrepareVocabularyWordOwnership();
        var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        if (wordIds.Count == 0)
            return result;
        if (Database.CurrentTransaction is not null)
        {
            // Inside the caller's transaction a failure must roll everything back together.
            ApplyStampedOwners(await VocabularyWordOwnership.StampAsync(
                this, wordIds, cancellationToken: cancellationToken));
            return result;
        }
        try
        {
            ApplyStampedOwners(await VocabularyWordOwnership.StampAsync(
                this, wordIds, cancellationToken: cancellationToken));
        }
        catch (Exception ex)
        {
            // The save already committed; an ownerless word fails closed and the next
            // server backfill stamps it. Report the stamp failure without hiding the save.
            OwnershipLogger()?.LogWarning(ex,
                "Saved {Count} vocabulary changes but could not record word owners.", wordIds.Count);
        }
        return result;
    }

    private void StampOwnersAfterSave(Func<VocabularyWordOwnership.Result> stamp)
    {
        if (Database.CurrentTransaction is not null)
        {
            ApplyStampedOwners(stamp());
            return;
        }
        try
        {
            ApplyStampedOwners(stamp());
        }
        catch (Exception ex)
        {
            OwnershipLogger()?.LogWarning(ex, "Saved vocabulary changes but could not record word owners.");
        }
    }

    // A blank owner on an updated word never overwrites the stored owner (callers often
    // Update() a word object built elsewhere). Returns the words whose owner may need
    // stamping: new ownerless words, plus (server only) words gaining a link or progress.
    private HashSet<string> PrepareVocabularyWordOwnership()
    {
        if (ChangeTracker.AutoDetectChangesEnabled)
            ChangeTracker.DetectChanges();
        var wordIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in ChangeTracker.Entries<VocabularyWord>())
        {
            if (entry.Entity.UserProfileId is not null)
                continue;
            if (entry.State == EntityState.Modified)
                entry.Property(word => word.UserProfileId).IsModified = false;
            else if (entry.State == EntityState.Added)
                wordIds.Add(entry.Entity.Id);
        }
        if (Database.ProviderName != SqliteProvider)
        {
            foreach (var entry in ChangeTracker.Entries<ResourceVocabularyMapping>())
                if (entry.State == EntityState.Added)
                    wordIds.Add(entry.Entity.VocabularyWordId);
            foreach (var entry in ChangeTracker.Entries<VocabularyProgress>())
                if (entry.State == EntityState.Added)
                    wordIds.Add(entry.Entity.VocabularyWordId);
        }
        wordIds.Remove(string.Empty);
        return wordIds;
    }

    // Stamping uses ExecuteUpdate, so mirror it onto tracked entities to keep them in step.
    private void ApplyStampedOwners(VocabularyWordOwnership.Result result)
    {
        if (result.Stamped.Count == 0)
            return;
        // Snapshot first: setting values notifies the tracker, which mutates its entries.
        foreach (var entry in ChangeTracker.Entries<VocabularyWord>().ToList())
        {
            if (!result.Stamped.TryGetValue(entry.Entity.Id, out var owner)
                || entry.Entity.UserProfileId is not null)
                continue;
            var property = entry.Property(word => word.UserProfileId);
            property.CurrentValue = owner;
            property.OriginalValue = owner;
            property.IsModified = false;
        }
    }

    private ILogger? OwnershipLogger()
    {
        try
        {
            return this.GetService<ILoggerFactory>().CreateLogger("SentenceStudio.Data.VocabularyWordOwnership");
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
