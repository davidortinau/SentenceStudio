using Microsoft.EntityFrameworkCore;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Coach.Operations;

/// <summary>
/// Proves the learner owns a row before any write touches it.
/// </summary>
/// <remarks>
/// <para>
/// Several vocabulary repository methods take no user identifier and perform no ownership check
/// of their own — <c>SaveWordAsync</c>, <c>UpdateVocabularyWordAsync</c>, and
/// <c>DeleteVocabularyWordAsync</c> all act on whatever id they are handed. That is safe for the
/// app, where the caller is the person whose device it is, and unsafe here, where the id can come
/// from a model. Every write handler resolves ownership through this type first.
/// </para>
/// <para>
/// Vocabulary ownership is transitive: a word has no owner column, and belongs to a learner only
/// through a mapping onto one of that learner's resources. This type deliberately requires that
/// mapping to exist. The app's own <c>IsVocabularyWordAvailableToUserAsync</c> also treats a word
/// with no mappings at all as available to everybody, which is reasonable for a local library and
/// wrong for a tool the model can aim: an unmapped word is a word some other learner may be about
/// to map, and letting Sam rewrite it would be a cross-tenant edit that looks like a local one.
/// </para>
/// </remarks>
public sealed class CoachWriteOwnership
{
    private readonly ApplicationDbContext _db;

    public CoachWriteOwnership(ApplicationDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>Loads a resource the learner owns, or null.</summary>
    public Task<LearningResource?> FindResourceAsync(
        string userProfileId, string resourceId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userProfileId) || string.IsNullOrWhiteSpace(resourceId))
        {
            return Task.FromResult<LearningResource?>(null);
        }

        return _db.LearningResources
            .FirstOrDefaultAsync(
                r => r.Id == resourceId && r.UserProfileId == userProfileId, cancellationToken);
    }

    /// <summary>Loads a skill the learner owns, or null.</summary>
    /// <param name="userProfileId">The owner, from the authenticated principal.</param>
    /// <param name="skillId">The skill to load.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <param name="includeArchived">
    /// Whether a skill the learner archived can be returned. False by default, so an archived
    /// skill answers exactly like one that does not exist: an archived skill is one the learner
    /// put away, and a write tool that could still reach it would be editing something they
    /// believe is gone. The one caller that passes true is the undo that restores an archive,
    /// which by definition looks for a row in that state.
    /// </param>
    public Task<SkillProfile?> FindSkillAsync(
        string userProfileId,
        string skillId,
        CancellationToken cancellationToken,
        bool includeArchived = false)
    {
        if (string.IsNullOrWhiteSpace(userProfileId) || string.IsNullOrWhiteSpace(skillId))
        {
            return Task.FromResult<SkillProfile?>(null);
        }

        return _db.SkillProfiles
            .FirstOrDefaultAsync(
                s => s.Id == skillId
                     && s.UserProfileId == userProfileId
                     && (includeArchived || !s.IsArchived),
                cancellationToken);
    }

    /// <summary>
    /// Loads a vocabulary word the learner owns through at least one of their own resources.
    /// </summary>
    public async Task<VocabularyWord?> FindOwnedWordAsync(
        string userProfileId, string wordId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userProfileId) || string.IsNullOrWhiteSpace(wordId))
        {
            return null;
        }

        var owned = await _db.ResourceVocabularyMappings
            .AnyAsync(
                m => m.VocabularyWordId == wordId
                     && _db.LearningResources.Any(
                         r => r.Id == m.ResourceId && r.UserProfileId == userProfileId),
                cancellationToken)
            .ConfigureAwait(false);

        if (!owned)
        {
            return null;
        }

        return await _db.VocabularyWords
            .FirstOrDefaultAsync(w => w.Id == wordId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>True when the learner's resource already carries this word.</summary>
    public Task<bool> IsWordLinkedAsync(
        string resourceId, string wordId, CancellationToken cancellationToken) =>
        _db.ResourceVocabularyMappings
            .AnyAsync(m => m.ResourceId == resourceId && m.VocabularyWordId == wordId, cancellationToken);

    /// <summary>
    /// Finds a word the learner already has with the same target term, so a repeated "add this
    /// word" lands on the existing entry instead of creating a near-duplicate.
    /// </summary>
    public async Task<VocabularyWord?> FindOwnedWordByTermAsync(
        string userProfileId, string targetTerm, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userProfileId) || string.IsNullOrWhiteSpace(targetTerm))
        {
            return null;
        }

        return await _db.VocabularyWords
            .Where(w => w.TargetLanguageTerm == targetTerm)
            .Where(w => _db.ResourceVocabularyMappings.Any(
                m => m.VocabularyWordId == w.Id
                     && _db.LearningResources.Any(
                         r => r.Id == m.ResourceId && r.UserProfileId == userProfileId)))
            .OrderBy(w => w.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Loads the learner's own profile row, or null.</summary>
    public Task<UserProfile?> FindProfileAsync(string userProfileId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userProfileId))
        {
            return Task.FromResult<UserProfile?>(null);
        }

        return _db.UserProfiles.FirstOrDefaultAsync(p => p.Id == userProfileId, cancellationToken);
    }

    /// <summary>Counts how many of the learner's resources carry a word, for removal previews.</summary>
    public Task<int> CountOwnedLinksAsync(
        string userProfileId, string wordId, CancellationToken cancellationToken) =>
        _db.ResourceVocabularyMappings
            .CountAsync(
                m => m.VocabularyWordId == wordId
                     && _db.LearningResources.Any(
                         r => r.Id == m.ResourceId && r.UserProfileId == userProfileId),
                cancellationToken);

    /// <summary>Counts every mapping onto a word, including other learners'.</summary>
    /// <remarks>
    /// A word can be mapped by more than one learner. Deleting the row outright would remove it
    /// from under them, so removal unlinks instead whenever this count exceeds the learner's own.
    /// </remarks>
    public Task<int> CountAllLinksAsync(string wordId, CancellationToken cancellationToken) =>
        _db.ResourceVocabularyMappings.CountAsync(m => m.VocabularyWordId == wordId, cancellationToken);

    /// <summary>The learner's own resources that carry a word.</summary>
    public async Task<IReadOnlyList<string>> OwnedResourceIdsForWordAsync(
        string userProfileId, string wordId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userProfileId) || string.IsNullOrWhiteSpace(wordId))
        {
            return Array.Empty<string>();
        }

        return await _db.ResourceVocabularyMappings
            .Where(m => m.VocabularyWordId == wordId)
            .Where(m => _db.LearningResources.Any(
                r => r.Id == m.ResourceId && r.UserProfileId == userProfileId))
            .Select(m => m.ResourceId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
