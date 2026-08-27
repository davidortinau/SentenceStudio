using System.Reflection;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Reports;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Reports;

/// <summary>
/// The shape of the report table is its privacy boundary.
/// </summary>
/// <remarks>
/// <para>
/// The same technique <c>CoachOpportunityShapeTests</c> and <c>CoachWriteAuditShapeTests</c> use,
/// and for the same reason: a reviewer should be able to establish that no learner text can reach
/// this table by reading the type declaration, rather than by trusting that a redaction routine
/// was applied correctly at every call site. These fail the build if a payload-shaped or
/// free-text-shaped member appears.
/// </para>
/// <para>
/// The learner-facing contracts are covered too. The request has one member and no room for a
/// second, and that is the property that makes "nothing a learner typed is ever re-sent in order
/// to report it" checkable rather than merely stated.
/// </para>
/// </remarks>
public class CoachResponseReportShapeTests
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
        "payload", "text", "content", "prompt", "answer", "transcript", "term", "word",
        "phrase", "email", "secret", "token", "argument", "arg", "note", "comment",
        "detail", "description", "summary", "title", "body", "raw", "value"
    ];

    /// <summary>
    /// Members whose names contain a forbidden fragment but are provably pointers, codes, or
    /// closed enums.
    /// </summary>
    /// <remarks>
    /// Listed by exact name so that adding <c>ResponseText</c> would still fail.
    /// </remarks>
    private static readonly HashSet<string> AllowedMembers = new(StringComparer.Ordinal)
    {
        nameof(CoachResponseReport.CoachMessageId),
        nameof(CoachResponseReport.CoachMessageSequence),
        nameof(CoachResponseReport.RequestMessageId),
        nameof(CoachResponseReport.RequestMessageSequence),
        nameof(CoachResponseReport.ResponseKind),
        nameof(CoachResponseReport.StopReason),
        nameof(CoachResponseReport.Reason),
        nameof(CoachResponseReport.TurnErrorCode),
        nameof(CoachResponseReport.WriteFailureCode),
        nameof(CoachResponseReport.InvokedToolNames)
    };

    [Fact]
    public void TheEntityCarriesNoPayloadOrFreeTextMember()
    {
        Offenders(typeof(CoachResponseReport)).Should().BeEmpty(
            "the report table holds identifiers, enum ordinals, closed-vocabulary codes, counts, " +
            "and timestamps only — a member named for content is how a learner's sentence reaches " +
            "a product backlog");
    }

    [Fact]
    public void TheRequestContractCannotCarryAnythingButAReason()
    {
        var members = typeof(CoachResponseReportRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToList();

        members.Should().ContainSingle().Which.Should().Be(nameof(CoachResponseReportRequest.Reason),
            "a call site that wanted to attach a learner's phrase must have nowhere to put it");
    }

    [Fact]
    public void TheResponseContractNamesNoLedgerIdentity()
    {
        var members = typeof(CoachResponseReportResponse)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToList();

        members.Should().NotContain("OpportunityId");
        members.Should().NotContain("Fingerprint");
        members.Should().NotContain("Status",
            "the operator's review lifecycle is a reviewer's business, not the reporting learner's");
    }

    [Fact]
    public void EveryStoredEnumKeepsItsOrdinal()
    {
        // Stored with HasConversion<int>(), so member order is a persistence contract: inserting a
        // value into the middle silently re-labels every row already written.
        ((int)CoachResponseReportReason.DidNotAnswer).Should().Be(0);
        ((int)CoachResponseReportReason.IncorrectOrMisleading).Should().Be(1);
        ((int)CoachResponseReportReason.ExpectedAppAction).Should().Be(2);
        ((int)CoachResponseReportReason.Confusing).Should().Be(3);
        ((int)CoachResponseReportReason.Other).Should().Be(4);

        Enum.GetValues<CoachResponseReportReason>().Should().HaveCount(5,
            "members may only be appended");

        ((int)CoachResponseReportState.Recorded).Should().Be(0);
        ((int)CoachResponseReportState.AlreadyReported).Should().Be(1);
    }

    [Fact]
    public void EveryReasonMapsToItsOwnCapabilityCode()
    {
        var codes = Enum.GetValues<CoachResponseReportReason>()
            .Select(SentenceStudio.Api.Coach.Opportunities.CoachOpportunityCapabilityCodes.ForReportReason)
            .ToList();

        codes.Should().OnlyHaveUniqueItems(
            "the capability code is a fingerprint input, so one code per reason is what makes the " +
            "rollup answer 'how many learners reported responses as incorrect' rather than one " +
            "undifferentiated total");

        codes.Should().OnlyContain(
            code => SentenceStudio.Api.Coach.Opportunities.CoachOpportunityCapabilityCodes.IsKnown(code),
            "the recorder drops a code outside the closed set, so an unregistered one would silently lose the row");
    }

    [Fact]
    public void EveryColumnIsBoundedAndNoneIsUnlimitedText()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();

        var entity = db.Model.FindEntityType(typeof(CoachResponseReport))!;

        foreach (var property in entity.GetProperties()
                     .Where(p => p.ClrType == typeof(string) || p.ClrType == typeof(string)))
        {
            property.GetMaxLength().Should().NotBeNull(
                $"'{property.Name}' is a string column and an unbounded one is a free-text column wearing a code's name");
        }
    }

    [Fact]
    public void TheUniquenessKeyIsRootedInTheOwner()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();

        var entity = db.Model.FindEntityType(typeof(CoachResponseReport))!;

        var unique = entity.GetIndexes().Where(index => index.IsUnique).ToList();

        unique.Should().ContainSingle().Which.Properties.Select(p => p.Name)
            .Should().Equal(
                nameof(CoachResponseReport.UserProfileId),
                nameof(CoachResponseReport.CoachMessageId));
    }

    private static List<string> Offenders(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(member => !AllowedMembers.Contains(member.Name))
            .Where(member => ForbiddenNameFragments.Any(fragment =>
                member.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            .Select(member => $"{type.Name}.{member.Name}")
            .ToList();
}
