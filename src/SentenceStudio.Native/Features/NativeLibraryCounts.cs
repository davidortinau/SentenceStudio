using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;

namespace SentenceStudio.Native;

/// <summary>What the Library list's rows count for one owner.</summary>
/// <param name="Words">Words recorded to the owner (the Vocabulary page lists these).</param>
/// <param name="Skills">The owner's skill profiles that are not archived.</param>
/// <param name="Pairs">The owner's minimal pairs whose two words are both theirs.</param>
public sealed record NativeLibraryCountSet(int Words, int Skills, int Pairs);

/// <summary>
/// The Library list's counts (Captain 2026-11-13: every row shows its count), read with one
/// COUNT per section, so the rows have numbers without loading each page. Each count uses
/// the rule its page lists by; an empty owner reads nothing.
/// </summary>
public sealed class NativeLibraryCounts(IServiceScopeFactory scopes)
{
    public async Task<NativeLibraryCountSet?> CountAsync(string owner,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(owner))
            return null;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var words = await db.VocabularyWords.AsNoTracking().OwnedBy(owner)
            .CountAsync(cancellationToken);
        var skills = await db.SkillProfiles.AsNoTracking()
            .CountAsync(skill => skill.UserProfileId == owner && !skill.IsArchived,
                cancellationToken);
        var pairs = await NativeMinimalPairStore.OwnedPairs(db, owner).AsNoTracking()
            .CountAsync(cancellationToken);
        return new(words, skills, pairs);
    }
}
