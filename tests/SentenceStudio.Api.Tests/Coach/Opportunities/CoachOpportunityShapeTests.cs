using System.Reflection;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Coach.Opportunities;
using SentenceStudio.Api.Coach.Persistence;

namespace SentenceStudio.Api.Tests.Coach.Opportunities;

/// <summary>
/// The shape of the opportunity ledger is its privacy boundary.
/// </summary>
/// <remarks>
/// <para>
/// The same technique <c>CoachWriteAuditShapeTests</c> uses, and for the same reason: a reviewer
/// should be able to establish that no learner text can reach this table by reading the type
/// declaration, rather than by trusting that a redaction routine was applied correctly at every
/// call site. These tests fail the build if a payload-shaped or free-text-shaped member appears
/// on either the entity or the signal.
/// </para>
/// <para>
/// The signal matters as much as the entity. A call site that wanted to log a learner's phrase
/// has to have somewhere to put it, and the point of the design is that it does not.
/// </para>
/// </remarks>
public class CoachOpportunityShapeTests
{
    /// <summary>
    /// Substrings that name a place learner content could live.
    /// </summary>
    /// <remarks>
    /// Deliberately broad. A false positive costs somebody a rename; a false negative costs a
    /// learner's sentence appearing in a product backlog.
    /// </remarks>
    private static readonly string[] ForbiddenNameFragments =
    [
        "payload", "text", "content", "message" /* only as a value, never a pointer — see below */,
        "prompt", "response", "answer", "transcript", "term", "word", "phrase",
        "email", "secret", "token", "argument", "arg", "note", "comment",
        "detail", "description", "summary", "title", "reason", "body", "raw", "value"
    ];

    /// <summary>
    /// Members whose names contain a forbidden fragment but are provably pointers or codes.
    /// </summary>
    /// <remarks>
    /// Every entry here is an opaque identifier, a sequence number, or a closed-enum ordinal.
    /// They are listed by exact name so that adding <c>EvidenceMessageText</c> would still fail.
    /// </remarks>
    private static readonly HashSet<string> AllowedPointerMembers = new(StringComparer.Ordinal)
    {
        nameof(CoachOpportunity.EvidenceMessageId),
        nameof(CoachOpportunity.EvidenceMessageSequence),
        nameof(CoachOpportunity.EvidenceOfferMessageId),
        nameof(CoachOpportunity.EvidenceOfferMessageSequence),
        nameof(CoachOpportunity.ReviewerNoteCode),
        nameof(CoachOpportunity.StopReason),
        nameof(CoachOpportunityEvidencePointer.MessageId),
        nameof(CoachOpportunityEvidencePointer.MessageSequence),
        nameof(CoachOpportunityEvidencePointer.OfferMessageId),
        nameof(CoachOpportunityEvidencePointer.OfferMessageSequence),

        // A computed bool over the two pointers above. Carries one bit — "is there anything to
        // resolve" — and cannot hold a character of learner content.
        nameof(CoachOpportunityEvidencePointer.HasMessagePointer)
    };

    [Fact]
    public void TheEntityCarriesNoPayloadOrFreeTextMember()
    {
        var offenders = Offenders(typeof(CoachOpportunity));

        offenders.Should().BeEmpty(
            "the opportunity ledger holds identifiers, enum ordinals, closed-vocabulary codes, " +
            "counts, and timestamps only — a member named for content is how a learner's sentence " +
            "reaches a product backlog");
    }

    [Fact]
    public void TheSignalCarriesNoPayloadOrFreeTextMember()
    {
        Offenders(typeof(CoachOpportunitySignal)).Should().BeEmpty(
            "the signal is the boundary: a call site that wanted to attach learner text must have " +
            "nowhere to put it");
    }

    [Fact]
    public void TheEvidencePointerCarriesNoText()
    {
        Offenders(typeof(CoachOpportunityEvidencePointer)).Should().BeEmpty(
            "evidence is a pointer into the encrypted message ledger, never a copy of it");
    }

    [Fact]
    public void EveryEntityMemberIsABoundedPrimitive()
    {
        var allowed = new[]
        {
            typeof(string), typeof(int), typeof(int?), typeof(long), typeof(long?),
            typeof(DateTime), typeof(DateTime?), typeof(DateOnly), typeof(bool)
        };

        foreach (var property in typeof(CoachOpportunity).GetProperties(
                     BindingFlags.Public | BindingFlags.Instance))
        {
            var type = property.PropertyType;
            var isEnum = type.IsEnum
                         || (Nullable.GetUnderlyingType(type)?.IsEnum ?? false);

            (isEnum || allowed.Contains(type)).Should().BeTrue(
                $"{property.Name} is typed {type.Name}; a complex or collection member on this " +
                "entity would be a payload column by another name");
        }
    }

    [Fact]
    public async Task TheMappedTableHasNoPayloadShapedColumn()
    {
        using var harness = new CoachOpportunityHarness();
        await using var db = harness.NewContext();

        var entity = db.Model.FindEntityType(typeof(CoachOpportunity));
        entity.Should().NotBeNull();

        var columns = entity!.GetProperties().Select(p => p.GetColumnName()).ToList();

        columns.Should().NotBeEmpty();

        foreach (var column in columns)
        {
            var offending = ForbiddenNameFragments
                .Where(fragment => column.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (offending.Count == 0 || AllowedPointerMembers.Contains(column))
            {
                continue;
            }

            Assert.Fail(
                $"Column '{column}' names content ({string.Join(", ", offending)}). The " +
                "opportunity ledger has no payload column, protected or otherwise.");
        }
    }

    [Fact]
    public async Task EveryTextColumnIsBounded()
    {
        using var harness = new CoachOpportunityHarness();
        await using var db = harness.NewContext();

        var entity = db.Model.FindEntityType(typeof(CoachOpportunity))!;

        foreach (var property in entity.GetProperties()
                     .Where(p => p.ClrType == typeof(string)))
        {
            property.GetMaxLength().Should().NotBeNull(
                $"{property.Name} is a string column with no length bound; an unbounded text " +
                "column is a free-text column regardless of what it is named");
        }
    }

    private static List<string> Offenders(Type type)
    {
        var offenders = new List<string>();

        foreach (var member in type
                     .GetMembers(BindingFlags.Public | BindingFlags.Instance)
                     .Where(m => m is PropertyInfo or FieldInfo))
        {
            if (AllowedPointerMembers.Contains(member.Name))
            {
                continue;
            }

            // Compiler-generated record members are not part of the declared shape.
            if (member.Name.StartsWith("<", StringComparison.Ordinal)
                || member.Name is "EqualityContract")
            {
                continue;
            }

            foreach (var fragment in ForbiddenNameFragments)
            {
                if (member.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                {
                    offenders.Add($"{type.Name}.{member.Name} (matched '{fragment}')");
                    break;
                }
            }
        }

        return offenders;
    }
}
