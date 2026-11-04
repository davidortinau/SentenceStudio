using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Data;

/// <summary>One successful lookup to record. Language is filled in when absent.</summary>
public sealed record VocabularyLookupEntry(
    string Text, string Translation, VocabularyLookupDirection Direction, string Activity,
    string? ResourceId = null, string? Language = null);

public enum VocabularyLookupRecordStatus
{
    Recorded,
    /// <summary>No trusted owner, or it is not the expected one: nothing was written.</summary>
    Refused,
    /// <summary>Empty text or translation: not a lookup.</summary>
    Invalid
}

/// <summary>A lookup and the title of the learner's resource it came from, if it still exists.</summary>
public sealed record VocabularyLookupItem(VocabularyLookup Lookup, string? ResourceTitle);

/// <summary>A resource of the learner's that a lookup can be added to (owned, not smart).</summary>
public sealed record VocabularyLookupDestination(string ResourceId, string Title, string? Language);

public sealed record VocabularyLookupPage(
    IReadOnlyList<VocabularyLookupItem> Items,
    IReadOnlyList<VocabularyLookupDestination> Destinations)
{
    public static VocabularyLookupPage Empty { get; } = new([], []);

    /// <summary>
    /// Where <paramref name="lookup"/> can be added: resources in its language (or with none),
    /// the resource it came from first.
    /// </summary>
    public IReadOnlyList<VocabularyLookupDestination> DestinationsFor(VocabularyLookup lookup) =>
        Destinations
            .Where(destination => VocabularyLookupRepository.LanguagesMatch(
                lookup.Language, destination.Language))
            .OrderBy(destination => destination.ResourceId == lookup.ResourceId ? 0 : 1)
            .ToArray();
}

/// <summary>
/// The learner's lookup history (Captain 2026-11-04, decision 4 B). Every read and write acts for
/// the active learner from the trusted scope (<see cref="ActiveOwner"/>) and only when that is the
/// owner the caller expects; otherwise it is refused and logged, never widened. Shared by the web,
/// MAUI and native clients.
/// </summary>
public sealed class VocabularyLookupRepository
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<VocabularyLookupRepository> _logger;
    private readonly TimeProvider _clock;
    private readonly ISyncService? _syncService;

    public VocabularyLookupRepository(
        IServiceProvider serviceProvider, ILogger<VocabularyLookupRepository> logger,
        ISyncService? syncService = null, TimeProvider? clock = null)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _syncService = syncService;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Records a successful lookup: a repeat of the same text (same learner, language and
    /// direction) updates its row with the latest translation, activity, resource and time.
    /// A resource that is not the learner's is not stored. A database failure is thrown, so the
    /// caller logs it while the learner still sees the translation.
    /// </summary>
    public async Task<VocabularyLookupRecordStatus> RecordAsync(
        string expectedOwner, VocabularyLookupEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var text = entry.Text?.Trim() ?? string.Empty;
        var translation = entry.Translation?.Trim() ?? string.Empty;
        if (SyncIds.NormalizeLookupText(text).Length == 0 || translation.Length == 0
            || string.IsNullOrWhiteSpace(entry.Activity))
            return VocabularyLookupRecordStatus.Invalid;

        using var scope = _serviceProvider.CreateScope();
        if (!TryOwner(scope.ServiceProvider, expectedOwner, nameof(RecordAsync), out var owner))
            return VocabularyLookupRecordStatus.Refused;
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        string? resourceId = null;
        string? resourceLanguage = null;
        if (!string.IsNullOrWhiteSpace(entry.ResourceId))
        {
            var resource = await db.LearningResources.AsNoTracking()
                .Where(row => row.Id == entry.ResourceId && row.UserProfileId == owner)
                .Select(row => new { row.Id, row.Language })
                .FirstOrDefaultAsync(cancellationToken);
            if (resource is null)
                _logger.LogWarning("A lookup named a resource that is not the learner's; it is recorded without one.");
            resourceId = resource?.Id;
            resourceLanguage = resource?.Language;
        }
        var language = FirstText(entry.Language, resourceLanguage)
            ?? await db.UserProfiles.AsNoTracking()
                .Where(profile => profile.Id == owner)
                .Select(profile => profile.TargetLanguage)
                .FirstOrDefaultAsync(cancellationToken)
            ?? string.Empty;
        language = language.Trim();

        var id = SyncIds.VocabularyLookup(owner, language, entry.Direction, text);
        var now = _clock.GetUtcNow().UtcDateTime;
        for (var attempt = 0; ; attempt++)
        {
            var existing = await db.VocabularyLookups
                .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);
            if (existing is not null && existing.UserProfileId != owner)
            {
                _logger.LogWarning("A lookup row ID belongs to another learner; nothing was written.");
                return VocabularyLookupRecordStatus.Refused;
            }
            if (existing is null)
                db.VocabularyLookups.Add(new VocabularyLookup
                {
                    Id = id, UserProfileId = owner, Text = text, Translation = translation,
                    Language = language, Direction = entry.Direction, Activity = entry.Activity,
                    ResourceId = resourceId, FirstLookedUpAt = now, LastLookedUpAt = now
                });
            else
            {
                existing.Text = text;
                existing.Translation = translation;
                existing.Activity = entry.Activity;
                existing.ResourceId = resourceId;
                // A clock that went back never moves the last lookup earlier.
                if (now > existing.LastLookedUpAt)
                    existing.LastLookedUpAt = now;
                if (now < existing.FirstLookedUpAt)
                    existing.FirstLookedUpAt = now;
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                break;
            }
            catch (DbUpdateException) when (attempt == 0 && existing is null)
            {
                // Another write inserted the same lookup first (two tabs, a sync download):
                // update that row instead. Any other failure is thrown.
                db.ChangeTracker.Clear();
                if (!await db.VocabularyLookups.AsNoTracking()
                        .AnyAsync(row => row.Id == id, cancellationToken))
                    throw;
            }
        }

        _syncService?.TriggerSyncAsync().ConfigureAwait(false);
        return VocabularyLookupRecordStatus.Recorded;
    }

    /// <summary>The learner's lookups, newest first, and the resources they can be added to.</summary>
    public async Task<VocabularyLookupPage> GetRecentAsync(
        string expectedOwner, int limit = DefaultLimit, CancellationToken cancellationToken = default)
    {
        using var scope = _serviceProvider.CreateScope();
        if (!TryOwner(scope.ServiceProvider, expectedOwner, nameof(GetRecentAsync), out var owner))
            return VocabularyLookupPage.Empty;
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var take = Math.Clamp(limit, 1, MaxLimit);
        var lookups = await db.VocabularyLookups.AsNoTracking()
            .Where(row => row.UserProfileId == owner)
            .OrderByDescending(row => row.LastLookedUpAt)
            .ThenBy(row => row.Id)
            .Take(take)
            .ToListAsync(cancellationToken);
        var resources = await db.LearningResources.AsNoTracking()
            .Where(row => row.UserProfileId == owner)
            .OrderByDescending(row => row.UpdatedAt)
            .ThenBy(row => row.Title)
            .Select(row => new { row.Id, row.Title, row.Language, row.IsSmartResource })
            .ToListAsync(cancellationToken);
        var titles = resources.ToDictionary(row => row.Id, row => row.Title, StringComparer.Ordinal);
        return new VocabularyLookupPage(
            lookups.Select(lookup => new VocabularyLookupItem(lookup,
                lookup.ResourceId is { } resource && titles.TryGetValue(resource, out var title)
                    ? title : null)).ToArray(),
            resources.Where(row => !row.IsSmartResource)
                .Select(row => new VocabularyLookupDestination(
                    row.Id, row.Title ?? string.Empty, row.Language))
                .ToArray());
    }

    /// <summary>
    /// "Add to vocabulary": creates the word or links the learner's existing one to the resource
    /// through <see cref="LearningResourceRepository.CreateAndAttachOwnedVocabularyAsync"/>
    /// (a word the resource already has is <see cref="ResourceVocabularyWriteStatus.AlreadyPresent"/>).
    /// The lookup row is kept.
    /// </summary>
    public async Task<ResourceVocabularyWriteStatus> AddToVocabularyAsync(
        string expectedOwner, string lookupId, string resourceId,
        CancellationToken cancellationToken = default)
    {
        using var scope = _serviceProvider.CreateScope();
        if (!TryOwner(scope.ServiceProvider, expectedOwner, nameof(AddToVocabularyAsync), out var owner))
            return ResourceVocabularyWriteStatus.NotFoundOrForbidden;
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var lookup = await db.VocabularyLookups.AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == lookupId && row.UserProfileId == owner,
                cancellationToken);
        var resource = await db.LearningResources.AsNoTracking()
            .Where(row => row.Id == resourceId && row.UserProfileId == owner)
            .Select(row => new { row.Language })
            .FirstOrDefaultAsync(cancellationToken);
        if (lookup is null || resource is null)
        {
            _logger.LogWarning("Add to vocabulary refused a lookup or resource that is not the learner's.");
            return ResourceVocabularyWriteStatus.NotFoundOrForbidden;
        }
        if (!LanguagesMatch(lookup.Language, resource.Language))
            return ResourceVocabularyWriteStatus.Invalid;

        var word = new VocabularyWord
        {
            TargetLanguageTerm = lookup.TargetTerm,
            NativeLanguageTerm = lookup.NativeTerm
        };
        return await scope.ServiceProvider.GetRequiredService<LearningResourceRepository>()
            .CreateAndAttachOwnedVocabularyAsync(resourceId, word,
                string.IsNullOrWhiteSpace(resource.Language) ? null! : resource.Language,
                owner, cancellationToken);
    }

    /// <summary>A resource with no language accepts any lookup; otherwise the languages match.</summary>
    internal static bool LanguagesMatch(string? lookupLanguage, string? resourceLanguage) =>
        string.IsNullOrWhiteSpace(resourceLanguage) || string.IsNullOrWhiteSpace(lookupLanguage)
        || string.Equals(lookupLanguage.Trim(), resourceLanguage.Trim(),
            StringComparison.OrdinalIgnoreCase);

    private bool TryOwner(
        IServiceProvider services, string expectedOwner, string operation, out string owner)
    {
        owner = string.Empty;
        if (!ActiveOwner.TryResolve(services, _logger, $"VocabularyLookupRepository.{operation}",
                out var active))
            return false;
        if (string.IsNullOrWhiteSpace(expectedOwner)
            || !string.Equals(active, expectedOwner, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "VocabularyLookupRepository.{Operation} refused: the caller's learner is not the active one.",
                operation);
            return false;
        }
        owner = active;
        return true;
    }

    private static string? FirstText(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
