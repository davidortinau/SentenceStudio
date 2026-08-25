using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Coach.Tools.SamTools;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Capabilities;

/// <summary>Raised when the read-capability metadata table does not match the frozen registry.</summary>
public sealed class CoachReadCapabilityMetadataException : InvalidOperationException
{
    public CoachReadCapabilityMetadataException(string message) : base(message) { }
}

/// <summary>
/// Asserts at startup that <see cref="CoachReadCapabilityMetadataTable"/> is a faithful description
/// of the reads the frozen registry actually offers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this runs at startup rather than in a test.</b> This table is the source Sam draws on when
/// it tells a learner what it can and cannot look up — "I can show you at most twenty tag
/// categories", "I cannot filter that by date". A row that has drifted from its tool does not
/// produce a wrong number in a log; it produces a confident false statement to a learner. The
/// earlier version of this table claimed <c>GetVocabularyDueSummary</c> had no ceiling while the
/// tool rejected anything above twenty, and nothing in the running system objected. A comment in
/// the table claimed this validator already existed. It did not.
/// </para>
/// <para>
/// <b>What it asserts, and what it deliberately does not.</b> Three rules, none of which invent a
/// value:
/// </para>
/// <list type="number">
/// <item>
/// <b>Bidirectional completeness.</b> Every registration whose effect class is
/// <see cref="CoachCapabilityEffectClass.Read"/> has a row, and every row names a registration that
/// is a read. A read added without metadata is the drift that produces silence about a capability;
/// a row left behind by a deleted read is the drift that produces a claim about a capability that
/// no longer exists.
/// </item>
/// <item>
/// <b>Bound fidelity.</b> Each of the five bounded reads declares its clamp as exactly one
/// constant, and the row must carry that constant's value. <see cref="DeclaredCeilings"/> maps the
/// registration to the constant — by reference, never by a copied number — so the tool remains the
/// single source and this validator is the thing that notices when a row stops agreeing with it.
/// </item>
/// <item>
/// <b>Range/ceiling agreement.</b> <see cref="CoachReadRangeSupport.ResultLimit"/> and a non-null
/// <c>MaxPageSize</c> must appear together. Claiming a caller bound while declaring no ceiling is
/// exactly the shape of the defect this file exists because of; declaring a ceiling on a read that
/// takes no caller bound is the same inconsistency mirrored.
/// </item>
/// </list>
/// <para>
/// It does <b>not</b> check coverage, order sets, filters or date support against the tools. Those
/// are read out of each tool's scope block by hand and there is no constant to cite; asserting them
/// here would mean writing a second copy of the expected value into the validator, which is the
/// duplication this design removes rather than adds.
/// </para>
/// </remarks>
public static class CoachReadCapabilityMetadataValidator
{
    /// <summary>
    /// The bounded reads, mapped to the constant each one declares.
    /// </summary>
    /// <remarks>
    /// Every value here is a reference to the tool's own constant. There is exactly one literal per
    /// ceiling in the codebase and it lives on the tool. Changing a tool's clamp changes this map,
    /// the metadata row, and the tool's own validation together, because all three read the same
    /// member.
    /// </remarks>
    public static IReadOnlyDictionary<string, int> DeclaredCeilings { get; } =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [CoachToolNames.GetResourceCatalog] = ResourceCatalogTool.MaxResults,
            [CoachToolNames.GetVocabularyDueSummary] = VocabularyDueSummaryTool.MaxTagCount,
            [CoachToolNames.ListUserVocabularies] = VocabularySearchTool.MaxResults,
            [CoachToolNames.GetSkillList] = SkillListTool.MaxResults,
            [CoachToolNames.GetLearningResourceList] = LearningResourceListTool.MaxResults
        };

    /// <summary>
    /// Validates the shipped table against <paramref name="registry"/>.
    /// </summary>
    /// <returns>The population examined, so a caller can assert non-vacuity.</returns>
    public static int Validate(ICoachToolRegistry registry) =>
        Validate(registry, CoachReadCapabilityMetadataTable.All);

    /// <summary>
    /// Validates <paramref name="table"/> against <paramref name="registry"/>.
    /// </summary>
    /// <remarks>
    /// The table is a parameter so a fixture can prove a doctored row stops startup. Production
    /// resolves the shipped table through the single-argument overload.
    /// </remarks>
    /// <returns>The population examined, so a caller can assert non-vacuity.</returns>
    public static int Validate(
        ICoachToolRegistry registry,
        IReadOnlyDictionary<string, CoachReadCapabilityMetadata> table)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(table);

        var reads = registry.All
            .Where(r => r.EffectClass == CoachCapabilityEffectClass.Read)
            .ToList();

        var examined = 0;

        foreach (var read in reads)
        {
            if (!table.TryGetValue(read.Name, out var metadata))
            {
                throw new CoachReadCapabilityMetadataException(
                    $"Read '{read.Name}' is registered but has no row in the read-capability "
                    + "metadata table. Sam would have nothing truthful to say about what that read "
                    + "covers, orders or bounds. Add the row, citing the tool it came from.");
            }

            ValidateRow(read.Name, metadata);
            examined++;
        }

        var registeredReadNames = reads.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var declaredName in table.Keys)
        {
            if (!registeredReadNames.Contains(declaredName))
            {
                throw new CoachReadCapabilityMetadataException(
                    $"The read-capability metadata table declares a row for '{declaredName}', which "
                    + "is not a registered read. A row that outlives its tool is a capability claim "
                    + "about something that no longer exists.");
            }
        }

        if (examined == 0)
        {
            // A validator that passes over zero rows is indistinguishable from one that was never
            // wired up, and the second is the failure that actually happens.
            throw new CoachReadCapabilityMetadataException(
                "The read-capability metadata validator examined zero reads. Everything it would "
                + "otherwise assert proves nothing.");
        }

        return examined;
    }

    /// <summary>The three value rules for one row.</summary>
    public static void ValidateRow(string toolName, CoachReadCapabilityMetadata metadata)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(metadata);

        if (metadata.MaxPageSize is { } size && size <= 0)
        {
            // Checked first so a nonsensical number is named as nonsense, whichever other rule
            // would also have caught it.
            throw new CoachReadCapabilityMetadataException(
                $"Read '{toolName}' declares a ceiling of {size}. A non-positive page size describes "
                + "a read that can never return anything.");
        }

        if (DeclaredCeilings.TryGetValue(toolName, out var declared))
        {
            if (metadata.MaxPageSize != declared)
            {
                throw new CoachReadCapabilityMetadataException(
                    $"Read '{toolName}' declares a ceiling of {declared} on the tool, but its "
                    + $"metadata row says {Describe(metadata.MaxPageSize)}. The row must cite the "
                    + "tool's constant, not restate the number, so the two cannot disagree.");
            }
        }
        else if (metadata.MaxPageSize is not null)
        {
            throw new CoachReadCapabilityMetadataException(
                $"Read '{toolName}' has a metadata ceiling of {metadata.MaxPageSize} but declares no "
                + "constant of its own to cite. A page size that exists only in this table is a "
                + "number nothing enforces.");
        }

        var claimsCallerBound = metadata.RangeSupport == CoachReadRangeSupport.ResultLimit;
        var hasCeiling = metadata.MaxPageSize is not null;

        if (claimsCallerBound != hasCeiling)
        {
            throw new CoachReadCapabilityMetadataException(
                $"Read '{toolName}' declares RangeSupport={metadata.RangeSupport} with "
                + $"MaxPageSize={Describe(metadata.MaxPageSize)}. A read that takes a caller bound "
                + "must state the ceiling it clamps to, and a read that takes no bound must not "
                + "state one.");
        }
    }

    private static string Describe(int? value) => value?.ToString() ?? "null";
}
