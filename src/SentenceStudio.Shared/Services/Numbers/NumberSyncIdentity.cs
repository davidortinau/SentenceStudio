using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models.Numbers;

namespace SentenceStudio.Services.Numbers;

/// <summary>
/// Number Drill rows get the same ID on every device and on the server, so CoreSync can merge
/// them (Captain 2026-11-02, docs/sync-all-tables-plan.md D2 and D5):
/// <list type="bullet">
/// <item>Catalog rows (contexts, sub-modes, counters) are app content. Their ID comes from their
/// code, so every store seeds identical rows. (Attempts and mastery store the counter word itself
/// in <c>CounterId</c>, e.g. "개", not a counter row ID, so they never need re-pointing.)</item>
/// <item>A learner has one mastery row per drill bucket. Its ID comes from the learner and the
/// bucket, so two devices practicing the same bucket update one row instead of colliding.</item>
/// </list>
/// IDs are RFC 4122 version 5 (SHA-1, name-based) GUIDs in the "D" format the tables already use.
/// </summary>
public static class NumberSyncIdentity
{
    // Fixed namespace for every Number Drill name. Never change it: stored IDs depend on it.
    private static readonly Guid Namespace = new("5b0a7f3e-2c1d-4e8a-9f6b-3d2c1b0a9e8f");

    public static string ContextId(string code) => NameGuid($"number-context:{code}");

    public static string SubModeId(string code) => NameGuid($"number-submode:{code}");

    public static string CounterId(string languageCode, string counter) =>
        NameGuid($"number-counter:{languageCode}:{counter}");

    public static string MasteryId(
        string userProfileId, string languageCode, string contextCode,
        string? counterId, NumberSystem system, string bucket)
    {
        if (string.IsNullOrWhiteSpace(userProfileId))
            throw new ArgumentException("A mastery row needs its owner.", nameof(userProfileId));
        return NameGuid(string.Join('\u001f', "number-mastery", userProfileId, languageCode,
            contextCode, counterId ?? string.Empty, system.ToString(), bucket));
    }

    public static string MasteryId(NumberMasteryProgress row) => MasteryId(
        row.UserProfileId, row.LanguageCode, row.ContextCode, row.CounterId, row.System, row.Bucket);

    /// <summary>
    /// Moves existing rows created with random IDs to their fixed IDs, in one transaction:
    /// catalog rows first, then mastery rows to their bucket ID. Idempotent: rows already on their fixed
    /// ID are not written. Nothing is deleted. A mastery row whose fixed ID is already taken
    /// (a duplicate bucket) keeps its ID and is reported, so it stays on this store only.
    /// Run before the store's first sync after this change and before a drill can write.
    /// </summary>
    public static async Task<NumberIdentityReport> NormalizeAsync(
        ApplicationDbContext db, ILogger logger, Action? assertSession = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        assertSession ??= static () => { };
        var ownsTransaction = db.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        assertSession();

        var contexts = await db.NumberContexts.AsNoTracking()
            .Select(row => new { row.Id, row.Code }).ToListAsync(cancellationToken);
        var contextsMoved = 0;
        foreach (var row in contexts)
        {
            var target = ContextId(row.Code);
            if (row.Id == target || contexts.Any(other => other.Id == target))
                continue;
            await ExecuteAsync(db, "UPDATE \"NumberContext\" SET \"Id\" = {0} WHERE \"Id\" = {1}",
                cancellationToken, target, row.Id);
            contextsMoved++;
        }

        var subModes = await db.NumberSubModes.AsNoTracking()
            .Select(row => new { row.Id, row.Code }).ToListAsync(cancellationToken);
        var subModesMoved = 0;
        foreach (var row in subModes)
        {
            var target = SubModeId(row.Code);
            if (row.Id == target || subModes.Any(other => other.Id == target))
                continue;
            await ExecuteAsync(db, "UPDATE \"NumberSubMode\" SET \"Id\" = {0} WHERE \"Id\" = {1}",
                cancellationToken, target, row.Id);
            subModesMoved++;
        }

        var counters = await db.NumberCounters.AsNoTracking()
            .Select(row => new { row.Id, row.LanguageCode, row.Counter })
            .ToListAsync(cancellationToken);
        var countersMoved = 0;
        foreach (var row in counters)
        {
            var target = CounterId(row.LanguageCode, row.Counter);
            if (row.Id == target || counters.Any(other => other.Id == target))
                continue;
            await ExecuteAsync(db, "UPDATE \"NumberCounter\" SET \"Id\" = {0} WHERE \"Id\" = {1}",
                cancellationToken, target, row.Id);
            countersMoved++;
        }
        assertSession();

        var mastery = await db.NumberMasteryProgresses.AsNoTracking().ToListAsync(cancellationToken);
        var taken = mastery.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
        var masteryMoved = 0;
        var masteryKept = 0;
        foreach (var row in mastery.Where(row => !string.IsNullOrWhiteSpace(row.UserProfileId)))
        {
            var target = MasteryId(row);
            if (row.Id == target)
                continue;
            if (taken.Contains(target))
            {
                masteryKept++;
                continue;
            }
            await ExecuteAsync(db,
                "UPDATE \"NumberMasteryProgress\" SET \"Id\" = {0} WHERE \"Id\" = {1}",
                cancellationToken, target, row.Id);
            taken.Remove(row.Id);
            taken.Add(target);
            masteryMoved++;
        }
        assertSession();

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        db.ChangeTracker.Clear();

        var report = new NumberIdentityReport(
            contextsMoved, subModesMoved, countersMoved, masteryMoved, masteryKept);
        if (report.Changed || masteryKept > 0)
            logger.LogInformation(
                "Number Drill IDs normalized: {Contexts} contexts, {SubModes} sub-modes, " +
                "{Counters} counters, {Mastery} mastery rows moved; {Kept} duplicate mastery rows kept local",
                contextsMoved, subModesMoved, countersMoved, masteryMoved, masteryKept);
        return report;
    }

    private static Task<int> ExecuteAsync(
        ApplicationDbContext db, string sql, CancellationToken cancellationToken,
        params object[] parameters) =>
        db.Database.ExecuteSqlRawAsync(sql, parameters, cancellationToken);

    private static string NameGuid(string name)
    {
        Span<byte> namespaceBytes = stackalloc byte[16];
        Namespace.TryWriteBytes(namespaceBytes, bigEndian: true, out _);
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var input = new byte[16 + nameBytes.Length];
        namespaceBytes.CopyTo(input);
        nameBytes.CopyTo(input, 16);
        Span<byte> hash = stackalloc byte[20];
        SHA1.HashData(input, hash);
        var bytes = hash[..16].ToArray();
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true).ToString();
    }
}

public sealed record NumberIdentityReport(
    int ContextsMoved, int SubModesMoved, int CountersMoved, int MasteryMoved, int MasteryKept)
{
    public bool Changed => ContextsMoved + SubModesMoved + CountersMoved + MasteryMoved > 0;
}
