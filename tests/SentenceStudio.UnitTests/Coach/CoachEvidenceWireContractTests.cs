using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Wire;

namespace SentenceStudio.UnitTests.Coach;

/// <summary>
/// The wire half of the evidence scope: the four mirrored enums and the additive DTO members.
/// </summary>
/// <remarks>
/// <para>
/// These fields exist so a learner can tell a total from a sample. That only works if a client too
/// old to read them keeps working, if a client that meets a value it cannot name says nothing
/// rather than guessing, and if the timestamp is stable enough that two identical facts render
/// identically. Each of those is one assertion here.
/// </para>
/// <para>
/// The mirror itself — that these four enums still agree with the server's <c>CoachScope*</c>
/// vocabulary — is asserted on the server side, where both halves are visible. Contracts cannot
/// reference Api, so this file can only prove the wire shape is well formed, not that it is a
/// faithful copy.
/// </para>
/// </remarks>
public class CoachEvidenceWireContractTests
{
    private static readonly Type[] EvidenceScopeEnums =
    [
        typeof(CoachEvidenceCoverage),
        typeof(CoachEvidenceOrder),
        typeof(CoachDefinitionCode),
        typeof(CoachWithheldReason),
        // Added with the W3 localization revision: the client localizes each value's label from
        // this code, so it is a member of the same family and inherits the same zero-is-Unknown
        // rule. Omitting it from the census is how a fifth enum ships unguarded.
        typeof(CoachEvidenceValueCode)
    ];

    private static JsonSerializerOptions TolerantClient()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new TolerantWireEnumConverterFactory());
        return options;
    }

    // ── The enums ────────────────────────────────────────────────────────────

    [Fact]
    public void The_five_evidence_scope_enums_all_reserve_zero_for_unknown()
    {
        EvidenceScopeEnums.Should().HaveCount(5, "the census must not silently shrink");

        foreach (var type in EvidenceScopeEnums)
        {
            Enum.GetName(type, 0).Should().Be(
                "Unknown",
                "{0} must reserve zero for the value a client could not read; every other member "
                + "makes a specific claim about the learner's data, and a claim is worse than a gap",
                type.Name);
        }
    }

    [Fact]
    public void Every_evidence_scope_enum_declares_its_unknown_value_fallback()
    {
        foreach (var type in EvidenceScopeEnums)
        {
            var descriptor = WireEnumFallback.TryDescribe(type);

            descriptor.Should().NotBeNull("{0} crosses to a client and must declare a fallback", type.Name);
            descriptor!.MemberName.Should().Be("Unknown", "{0}", type.Name);
            descriptor.Kind.Should().Be(WireEnumFallbackKind.SafeZero, "{0}", type.Name);
            descriptor.Rationale.Length.Should().BeGreaterThan(
                60, "{0}'s rationale has to say why, not just that", type.Name);
        }
    }

    [Fact]
    public void Every_evidence_scope_enum_serializes_as_words()
    {
        foreach (var type in EvidenceScopeEnums)
        {
            type.GetCustomAttribute<JsonConverterAttribute>()
                .Should().NotBeNull("{0} must go on the wire as a name, not an ordinal", type.Name);
        }
    }

    [Fact]
    public void The_ordinals_are_pinned()
    {
        Pinned<CoachEvidenceCoverage>(new()
        {
            ["Unknown"] = 0, ["CompleteOwnedSet"] = 1, ["PageOfOwnedSet"] = 2, ["WindowBounded"] = 3,
            ["SingleItem"] = 4, ["SingleDay"] = 5, ["SettingsSnapshot"] = 6,
            ["DerivedProjection"] = 7, ["CompleteAggregateWithBreakdown"] = 8
        });

        Pinned<CoachEvidenceOrder>(new()
        {
            ["Unknown"] = 0, ["NotApplicable"] = 1, ["Unordered"] = 2, ["LastUsedAscending"] = 3,
            ["UpdatedDescending"] = 4, ["MasteryDescending"] = 5, ["MinutesDescending"] = 6,
            ["PriorityAscending"] = 7, ["FrequencyDescending"] = 8, ["BandLabelAscending"] = 9
        });

        Pinned<CoachDefinitionCode>(new()
        {
            ["Unknown"] = 0, ["OwnedResourceCatalog"] = 1, ["OwnedResourceList"] = 2,
            ["OwnedResourceDetail"] = 3, ["ActiveSkillList"] = 4, ["ActiveSkillDetail"] = 5,
            ["TrackedVocabularyDueSummary"] = 6, ["UndueVocabularySearch"] = 7,
            ["TrackedVocabularyDetail"] = 8, ["LearnerSettingsSnapshot"] = 9,
            ["LearnerOverviewSummary"] = 10, ["PlanDaySummary"] = 11,
            ["PracticeWindowBalance"] = 12, ["DeterministicPlanPreview"] = 13
        });

        // The one enum whose ordinals deliberately differ from the server's. None must not sit at
        // zero, because zero is what an unreadable value lands on and "nothing was withheld" is a
        // claim, not an absence of one.
        Pinned<CoachWithheldReason>(new()
        {
            ["Unknown"] = 0, ["None"] = 1, ["DueReviewEmbargo"] = 2, ["ResultLimit"] = 3,
            ["ArchivedExcluded"] = 4, ["BelowMinimumEvidence"] = 5
        });
    }

    [Fact]
    public void An_unreadable_scope_value_collapses_to_unknown_rather_than_throwing()
    {
        var payload = """
            {
              "kind": "PracticeBalance",
              "label": "Practice balance",
              "summary": "Mostly reading.",
              "windowStartDate": "2026-08-01",
              "windowEndDate": "2026-08-14",
              "coverage": "SomeCoverageFromNextYear",
              "order": "SomeOrderFromNextYear",
              "definitionCode": "SomeDefinitionFromNextYear",
              "withheldReason": "SomeReasonFromNextYear",
              "withheldCount": 4
            }
            """;

        var dto = JsonSerializer.Deserialize<CoachEvidenceDto>(payload, TolerantClient());

        dto.Should().NotBeNull();
        dto!.Coverage.Should().Be(CoachEvidenceCoverage.Unknown);
        dto.Order.Should().Be(CoachEvidenceOrder.Unknown);
        dto.DefinitionCode.Should().Be(CoachDefinitionCode.Unknown);
        dto.WithheldReason.Should().Be(CoachWithheldReason.Unknown);

        dto.WithheldCount.Should().Be(
            4, "the count is the disclosure and survives even when the explanation does not");
        dto.Label.Should().Be("Practice balance", "one unreadable enum must not cost the whole item");
    }

    // ── The DTO ──────────────────────────────────────────────────────────────

    [Fact]
    public void A_payload_from_before_the_scope_fields_still_reads()
    {
        var payload = """
            {
              "kind": "VocabularyDue",
              "label": "Vocabulary due",
              "summary": "Fourteen words are waiting.",
              "windowStartDate": "2026-08-01",
              "windowEndDate": "2026-08-14",
              "values": [ { "label": "Due now", "value": 14, "unit": "Count" } ]
            }
            """;

        var dto = JsonSerializer.Deserialize<CoachEvidenceDto>(payload, TolerantClient())!;

        dto.Values.Should().ContainSingle();
        dto.Coverage.Should().BeNull("an omitted field means the server did not say, not Unknown");
        dto.Order.Should().BeNull();
        dto.DefinitionCode.Should().BeNull();
        dto.WithheldReason.Should().BeNull();
        dto.AsOfUtc.Should().BeNull();
        dto.MatchedCount.Should().BeNull();
        dto.ReturnedCount.Should().BeNull();
        dto.WithheldCount.Should().BeNull();
    }

    [Fact]
    public void The_scope_fields_are_all_optional_on_the_shape()
    {
        string[] additive =
        [
            nameof(CoachEvidenceDto.Coverage), nameof(CoachEvidenceDto.Order),
            nameof(CoachEvidenceDto.DefinitionCode), nameof(CoachEvidenceDto.WithheldReason),
            nameof(CoachEvidenceDto.AsOfUtc), nameof(CoachEvidenceDto.MatchedCount),
            nameof(CoachEvidenceDto.ReturnedCount), nameof(CoachEvidenceDto.WithheldCount)
        ];

        additive.Should().HaveCount(8, "the census must not silently shrink");

        foreach (var name in additive)
        {
            var property = typeof(CoachEvidenceDto).GetProperty(name);

            property.Should().NotBeNull(name);
            Nullable.GetUnderlyingType(property!.PropertyType).Should().NotBeNull(
                "{0} must be nullable so a server that cannot supply it and a client that cannot "
                + "read it both behave as they did before", name);

            property.GetCustomAttribute<System.Runtime.CompilerServices.RequiredMemberAttribute>()
                .Should().BeNull("{0} must not be required — that would break every existing caller", name);
        }
    }

    [Fact]
    public void The_scope_fields_carry_no_channel_for_learner_content()
    {
        // The embargo, checked on the shape rather than trusted. Evidence discloses that words were
        // withheld and why; a string member here is the one way a term, a gloss, an example, or an
        // expected answer could ride along with that disclosure.
        var additive = typeof(CoachEvidenceDto)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name is not (nameof(CoachEvidenceDto.Kind)
                or nameof(CoachEvidenceDto.Label)
                or nameof(CoachEvidenceDto.Summary)
                or nameof(CoachEvidenceDto.WindowStartDate)
                or nameof(CoachEvidenceDto.WindowEndDate)
                or nameof(CoachEvidenceDto.Values)))
            .ToList();

        additive.Should().HaveCount(8);

        foreach (var property in additive)
        {
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

            (type.IsEnum || type == typeof(int) || type == typeof(DateTime)).Should().BeTrue(
                "{0} is a {1}; a scope field may only be a closed enum, a count, or a timestamp",
                property.Name,
                type.Name);
        }
    }

    // ── The timestamp ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(9_999_999)]
    [InlineData(4_821_593)]
    public void AsOfUtc_is_truncated_to_the_whole_second_on_the_way_in(long extraTicks)
    {
        var whole = new DateTime(2026, 8, 21, 22, 14, 7, DateTimeKind.Utc);

        var dto = Sample(asOfUtc: whole.AddTicks(extraTicks));

        dto.AsOfUtc.Should().Be(whole, "rounding up would place the claim after the data it came from");
        dto.AsOfUtc!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void AsOfUtc_normalization_is_idempotent_and_kind_pinning()
    {
        var whole = new DateTime(2026, 8, 21, 22, 14, 7, DateTimeKind.Utc);

        CoachEvidenceInstant.Normalize(whole).Should().Be(whole);
        CoachEvidenceInstant.Normalize(CoachEvidenceInstant.Normalize(whole.AddTicks(123)))
            .Should().Be(whole);

        var unspecified = DateTime.SpecifyKind(whole, DateTimeKind.Unspecified);
        CoachEvidenceInstant.Normalize(unspecified).Should().Be(whole);
        CoachEvidenceInstant.Normalize(unspecified).Kind.Should().Be(DateTimeKind.Utc);

        var local = whole.ToLocalTime();
        CoachEvidenceInstant.Normalize(local).Should().Be(whole, "the instant is preserved, the clock is not");
    }

    [Fact]
    public void A_sub_second_instant_from_the_wire_is_normalized_on_read()
    {
        var payload = """
            {
              "kind": "PracticeBalance",
              "label": "Practice balance",
              "summary": "Mostly reading.",
              "windowStartDate": "2026-08-01",
              "windowEndDate": "2026-08-14",
              "asOfUtc": "2026-08-21T22:14:07.4821593Z"
            }
            """;

        var dto = JsonSerializer.Deserialize<CoachEvidenceDto>(payload, TolerantClient())!;

        dto.AsOfUtc.Should().Be(new DateTime(2026, 8, 21, 22, 14, 7, DateTimeKind.Utc),
            "the init accessor is the guard, so a newer server cannot put sub-second noise on screen");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static CoachEvidenceDto Sample(DateTime? asOfUtc = null) => new()
    {
        Kind = CoachEvidenceKind.PracticeBalance,
        Label = "Practice balance",
        Summary = "Mostly reading.",
        WindowStartDate = new DateOnly(2026, 8, 1),
        WindowEndDate = new DateOnly(2026, 8, 14),
        AsOfUtc = asOfUtc
    };

    private static void Pinned<TEnum>(Dictionary<string, int> expected) where TEnum : struct, Enum
    {
        var actual = Enum.GetValues<TEnum>().ToDictionary(v => v.ToString(), v => Convert.ToInt32(v));

        actual.Should().BeEquivalentTo(
            expected,
            "{0} is a wire vocabulary an older client reads by name and a newer one by ordinal; "
            + "renumbering it changes what an already-sent payload means",
            typeof(TEnum).Name);
    }
}
