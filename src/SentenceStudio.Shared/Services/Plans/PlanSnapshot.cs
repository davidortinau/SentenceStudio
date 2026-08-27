using System.Security.Cryptography;
using System.Text;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Services.Plans;

/// <summary>
/// One item in a normalized plan snapshot. Carries only the fields that define
/// plan identity and progress — no localized text, no learner content, no
/// answer material.
/// </summary>
public sealed record PlanSnapshotItem
{
    public required string PlanItemId { get; init; }
    public required string ActivityType { get; init; }
    public string? ResourceId { get; init; }
    public string? SkillId { get; init; }
    public required int Priority { get; init; }
    public required int EstimatedMinutes { get; init; }
    public int MinutesSpent { get; init; }
    public bool IsCompleted { get; init; }

    /// <summary>
    /// True when the learner has neither completed nor started this item, which
    /// is the only class of item a coach revision may replace.
    /// </summary>
    public bool IsUntouched => !IsCompleted && MinutesSpent <= 0;
}

/// <summary>
/// A normalized, order-stable snapshot of one user-local day's plan, plus the
/// deterministic version and hash derived from it.
/// </summary>
/// <remarks>
/// <para>
/// The version is content-derived rather than a stored counter, so no schema
/// change is required and two hosts observing the same rows always agree. Any
/// change to item membership, order, estimates, logged minutes, or completion
/// state produces a new version — which is exactly the staleness signal the
/// Learning Coach needs before it writes.
/// </para>
/// <para>
/// The API Coach persistence lane maps this type onto
/// <c>CoachPlanStateDto</c> / <c>CoachPlanRevisionInput</c>. Shared does not
/// reference those Coach types, so the plan domain stays independent of the
/// coach feature.
/// </para>
/// </remarks>
public sealed record PlanSnapshot
{
    /// <summary>Snapshot format tag. Bump when the canonical form changes.</summary>
    public const string FormatVersion = "1";

    private const char FieldSeparator = '\u001f';
    private const char RecordSeparator = '\u001e';

    public required DateOnly PlanDate { get; init; }

    /// <summary>Items in plan order (Priority, then PlanItemId ordinal).</summary>
    public required IReadOnlyList<PlanSnapshotItem> Items { get; init; }

    /// <summary>Lower-case hex SHA-256 of the canonical form.</summary>
    public required string Hash { get; init; }

    /// <summary>Opaque version token clients echo back. Format: <c>v{format}:{hash}</c>.</summary>
    public required string Version { get; init; }

    public int TotalEstimatedMinutes => Items.Sum(i => i.EstimatedMinutes);

    public int TotalMinutesSpent => Items.Sum(i => i.MinutesSpent);

    public int CompletedItemCount => Items.Count(i => i.IsCompleted);

    public int InProgressItemCount => Items.Count(i => !i.IsCompleted && i.MinutesSpent > 0);

    public int UntouchedItemCount => Items.Count(i => i.IsUntouched);

    /// <summary>Builds a snapshot from persisted completion rows.</summary>
    public static PlanSnapshot FromCompletions(DateOnly planDate, IEnumerable<DailyPlanCompletion> completions)
    {
        ArgumentNullException.ThrowIfNull(completions);

        var items = completions
            .Select(c => new PlanSnapshotItem
            {
                PlanItemId = c.PlanItemId,
                ActivityType = c.ActivityType,
                ResourceId = c.ResourceId,
                SkillId = c.SkillId,
                Priority = c.Priority,
                EstimatedMinutes = c.EstimatedMinutes,
                MinutesSpent = c.MinutesSpent,
                IsCompleted = c.IsCompleted
            })
            .ToList();

        return FromItems(planDate, items);
    }

    /// <summary>Builds a snapshot from already-projected items, normalizing order.</summary>
    public static PlanSnapshot FromItems(DateOnly planDate, IEnumerable<PlanSnapshotItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var ordered = items
            .OrderBy(i => i.Priority)
            .ThenBy(i => i.PlanItemId, StringComparer.Ordinal)
            .ToList();

        var hash = ComputeHash(planDate, ordered);

        return new PlanSnapshot
        {
            PlanDate = planDate,
            Items = ordered,
            Hash = hash,
            Version = $"v{FormatVersion}:{hash}"
        };
    }

    /// <summary>An empty snapshot for a date with no plan rows.</summary>
    public static PlanSnapshot Empty(DateOnly planDate) => FromItems(planDate, Array.Empty<PlanSnapshotItem>());

    /// <summary>
    /// Ordinal version comparison. A null or blank expected version means the
    /// caller is not asserting a version and is always considered current.
    /// </summary>
    public bool MatchesVersion(string? expectedVersion) =>
        string.IsNullOrWhiteSpace(expectedVersion) ||
        string.Equals(expectedVersion, Version, StringComparison.Ordinal);

    private static string ComputeHash(DateOnly planDate, IReadOnlyList<PlanSnapshotItem> orderedItems)
    {
        var builder = new StringBuilder();
        builder.Append('v').Append(FormatVersion).Append(FieldSeparator);
        builder.Append(planDate.ToString("yyyy-MM-dd")).Append(RecordSeparator);

        foreach (var item in orderedItems)
        {
            builder.Append(item.PlanItemId).Append(FieldSeparator);
            builder.Append(item.ActivityType).Append(FieldSeparator);
            builder.Append(item.ResourceId ?? string.Empty).Append(FieldSeparator);
            builder.Append(item.SkillId ?? string.Empty).Append(FieldSeparator);
            builder.Append(item.Priority).Append(FieldSeparator);
            builder.Append(item.EstimatedMinutes).Append(FieldSeparator);
            builder.Append(item.MinutesSpent).Append(FieldSeparator);
            builder.Append(item.IsCompleted ? '1' : '0').Append(RecordSeparator);
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(bytes);
    }
}
