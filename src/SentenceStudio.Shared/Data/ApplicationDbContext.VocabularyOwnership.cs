using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Data;

// Words get their owner when they are created (Captain 2026-11-06): every creation path sets
// VocabularyWord.UserProfileId to the learner who creates it, and nothing infers an owner after
// a save. This hook only guards that rule for every save path (repositories, imports, Razor
// pages, native stores, the Coach):
// - a new word with no owner is refused (fail closed, logged), never saved ownerless;
// - a blank owner (NULL or '') on an updated word never overwrites the stored owner (callers
//   often Update() a word object built elsewhere).
public partial class ApplicationDbContext
{
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardVocabularyWordOwners();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        GuardVocabularyWordOwners();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void GuardVocabularyWordOwners()
    {
        if (ChangeTracker.AutoDetectChangesEnabled)
            ChangeTracker.DetectChanges();
        List<string>? ownerless = null;
        foreach (var entry in ChangeTracker.Entries<VocabularyWord>())
        {
            if (!VocabularyWordOwnership.IsOwnerless(entry.Entity.UserProfileId))
                continue;
            if (entry.State == EntityState.Modified)
                entry.Property(word => word.UserProfileId).IsModified = false;
            else if (entry.State == EntityState.Added)
                (ownerless ??= []).Add(entry.Entity.Id);
        }
        if (ownerless is null)
            return;

        OwnershipLogger()?.LogError(
            "Refused saving {Count} new vocabulary words with no owner ({WordIds}): a word must be recorded to the learner who creates it.",
            ownerless.Count, string.Join(", ", ownerless.Take(10)));
        throw new VocabularyWordOwnerRequiredException(ownerless);
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

/// <summary>
/// A save tried to create vocabulary words with no owner. Every creation path must record the
/// learner who creates the word (<see cref="VocabularyWord.UserProfileId"/>); nothing is saved.
/// </summary>
public sealed class VocabularyWordOwnerRequiredException(IReadOnlyList<string> wordIds)
    : InvalidOperationException(
        $"A new vocabulary word must be recorded to the learner who creates it; refused {wordIds.Count} word(s) with no owner.")
{
    /// <summary>The IDs of the refused words.</summary>
    public IReadOnlyList<string> WordIds { get; } = wordIds;
}
