using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Wire;

namespace SentenceStudio.UnitTests.Coach;

/// <summary>
/// What a limitation is allowed to claim, and what an older client sees when it cannot read one.
/// </summary>
/// <remarks>
/// <para>
/// A limitation is a claim about the learner's data made at the exact moment the app is refusing to
/// act on it, which is the worst possible moment for the claim to be loose. "That would remove
/// everything" said about a partial count is the same over-claim the evidence scope exists to
/// prevent, one surface over.
/// </para>
/// <para>
/// The tolerance tests below matter because this whole shape is additive. Every member is new, so
/// every member is one an older client will not read — and the failure mode of a tolerant client is
/// not a crash, it is a confident render of a default. A default that reads as "no consequences"
/// or "no reason given but here is a link" is worse than the missing card.
/// </para>
/// </remarks>
public sealed class CoachLimitationContractTests
{
    private static readonly DateTime AsOf = new(2026, 8, 21, 19, 10, 0, DateTimeKind.Utc);

    // ── Neutral by default ───────────────────────────────────────────────────

    /// <summary>
    /// A default-constructed limitation asserts nothing. Every member's zero is the safe one.
    /// </summary>
    [Fact]
    public void Default_limitation_makes_no_claim()
    {
        var limitation = new CoachLimitationDto();

        limitation.Code.Should().Be(CoachLimitationCode.Unknown);
        limitation.Coverage.Should().Be(CoachEvidenceCoverage.Unknown);
        limitation.AsOfUtc.Should().BeNull();
        limitation.AffectedCount.Should().BeNull();
        limitation.Destination.Should().BeNull();
        limitation.FullScopeSurface.Should().BeNull();

        limitation.Alternatives.Should().BeEmpty(
            "an empty collection rather than null, so a client can enumerate without a guard and "
            + "never renders an option the server did not offer");
        limitation.HintLadder.Should().BeEmpty();
        limitation.ShorterSession.Should().BeNull();
    }

    // ── Old-client tolerance ─────────────────────────────────────────────────

    /// <summary>
    /// A newer server naming a code this build has never heard of.
    /// </summary>
    /// <remarks>
    /// The important half is the second assertion. Degrading to Unknown is only safe because
    /// Unknown renders as a neutral heading with no reason attached; if an unrecognised code
    /// resolved to <c>NotBuilt</c> the learner would be told a feature does not exist on the
    /// strength of a string this build could not parse.
    /// </remarks>
    [Fact]
    public void Unrecognized_limitation_code_degrades_to_unknown()
    {
        const string json = """{"code":"SomeFutureBoundary","coverage":"CompleteOwnedSet"}""";

        var limitation = JsonSerializer.Deserialize<CoachLimitationDto>(json, WireJson.Client);

        limitation.Should().NotBeNull();
        limitation!.Code.Should().Be(CoachLimitationCode.Unknown);
        limitation.Code.Should().NotBe(CoachLimitationCode.NotBuilt);
        limitation.Code.Should().NotBe(CoachLimitationCode.RefusedByDesign);
    }

    [Fact]
    public void Unrecognized_route_degrades_to_unknown()
    {
        const string json = """{"route":"SomeFutureScreen","parameters":[],"sideEffect":"None"}""";

        var destination = JsonSerializer.Deserialize<CoachDestinationDto>(json, WireJson.Client);

        destination!.Route.Should().Be(
            CoachRouteName.Unknown,
            "the client drops an unresolvable route rather than navigating somewhere the server "
            + "never named");
    }

    /// <summary>
    /// The one degradation that must not read as safety.
    /// </summary>
    [Fact]
    public void Unrecognized_side_effect_degrades_to_unknown_not_none()
    {
        const string json = """{"route":"Vocabulary","parameters":[],"sideEffect":"WipesEverything"}""";

        var destination = JsonSerializer.Deserialize<CoachDestinationDto>(json, WireJson.Client);

        destination!.SideEffect.Should().Be(CoachRouteSideEffect.Unknown);
        destination.SideEffect.Should().NotBe(
            CoachRouteSideEffect.None,
            "an unreadable consequence rendered as a read-only screen is the exact inversion this "
            + "field exists to prevent — the case it is for is the screen that is not read-only");
    }

    [Fact]
    public void Unrecognized_alternative_and_hint_degrade_to_unknown()
    {
        const string json = """
            {"alternatives":["SomeFutureOption"],"hintLadder":[{"rung":4,"kind":"RevealTheAnswer"}]}
            """;

        var limitation = JsonSerializer.Deserialize<CoachLimitationDto>(json, WireJson.Client);

        limitation!.Alternatives.Should().Equal([CoachAlternativeCode.Unknown]);
        limitation.HintLadder.Should().ContainSingle()
            .Which.Kind.Should().Be(
                CoachHintKind.Unknown,
                "a rung this build cannot name renders as unavailable; falling back to the nearest "
                + "known rung on a ladder whose top is one step from the answer is not tolerance");
    }

    [Fact]
    public void Unknown_members_serialize_by_name_so_an_older_server_is_readable_too()
    {
        var json = JsonSerializer.Serialize(new CoachLimitationDto(), WireJson.Client);

        json.Should().Contain("\"code\":\"Unknown\"");
        json.Should().NotContain("\"code\":0", "the wire is string-valued, not ordinal-valued");
    }

    [Fact]
    public void Absent_optional_members_are_omitted_rather_than_written_null()
    {
        var json = JsonSerializer.Serialize(new CoachLimitationDto(), WireJson.Client);

        json.Should().NotContain("asOfUtc");
        json.Should().NotContain("destination");
        json.Should().NotContain("shorterSession");
    }

    // ── Claim invariants ─────────────────────────────────────────────────────

    /// <summary>
    /// Round-trip fidelity, because every assertion elsewhere assumes the wire preserves the claim.
    /// </summary>
    [Fact]
    public void A_full_limitation_round_trips()
    {
        var original = new CoachLimitationDto
        {
            Code = CoachLimitationCode.ExceedsSafeChangeScope,
            Coverage = CoachEvidenceCoverage.CompleteOwnedSet,
            AsOfUtc = AsOf,
            WindowStartDate = new DateOnly(2026, 8, 15),
            WindowEndDate = new DateOnly(2026, 8, 21),
            AffectedCount = 412,
            Destination = CoachRouteCatalog.Build(
                CoachRouteName.Vocabulary,
                [new CoachRouteParameterDto(CoachRouteParameterKey.ResourceId, "77")]),
            FullScopeSurface = CoachRouteCatalog.Build(CoachRouteName.Settings),
            Alternatives = [CoachAlternativeCode.ExportBeforeRemoving],
            HintLadder = [new CoachHintRungDto(1, CoachHintKind.Category)],
            ShorterSession = new CoachShorterSessionOfferDto(6, 18, PreservesRetrieval: true)
        };

        var restored = JsonSerializer.Deserialize<CoachLimitationDto>(JsonSerializer.Serialize(original, WireJson.Client), WireJson.Client);

        restored.Should().BeEquivalentTo(original);
    }

    /// <summary>
    /// Every code except the fallback must be reachable, or the taxonomy is decorative.
    /// </summary>
    [Fact]
    public void Every_limitation_code_is_distinguishable_on_the_wire()
    {
        var codes = Enum.GetValues<CoachLimitationCode>();

        codes.Should().HaveCountGreaterThan(1);

        var names = codes.Select(code => JsonSerializer.Serialize(new CoachLimitationDto { Code = code }, WireJson.Client))
            .ToArray();

        names.Should().OnlyHaveUniqueItems(
            "two codes that serialize identically would collapse 'not built' into 'won't do it', "
            + "and those two point the learner in opposite directions");
    }

    [Fact]
    public void Coverage_is_a_mirror_of_the_evidence_vocabulary()
    {
        var limitation = new CoachLimitationDto { Coverage = CoachEvidenceCoverage.SingleDay };

        JsonSerializer.Serialize(limitation, WireJson.Client).Should().Contain(
            "\"coverage\":\"SingleDay\"",
            "a limitation states coverage in exactly the terms an evidence item does, so a learner "
            + "comparing the two is comparing like with like");
    }

    /// <summary>
    /// A shorter session must actually be shorter, and must say whether it kept the retrieval.
    /// </summary>
    [Fact]
    public void Shorter_session_offer_states_both_counts_and_the_retrieval_claim()
    {
        var offer = new CoachShorterSessionOfferDto(6, 18, PreservesRetrieval: true);

        offer.SuggestedItemCount.Should().BeLessThan(
            offer.FullItemCount,
            "an offer the same length as the session is not an offer");
        offer.SuggestedItemCount.Should().BePositive("an offer of nothing is a skip");

        JsonSerializer.Serialize(offer, WireJson.Client).Should().Contain(
            "\"preservesRetrieval\":true",
            "the claim is on the wire so a reviewer reads it rather than trusting the type name");
    }

    [Fact]
    public void Hint_rungs_are_one_based_and_contiguous()
    {
        // The shipped ladder, in shipped order. The enum ordinals are not the rung numbers:
        // FormCue is CoachHintKind = 2 but rung THREE, because it is the only rung that discloses
        // part of the written form, and Cloze is CoachHintKind = 3 but rung TWO, because it gives
        // surrounding context and none of the form. This fixture carried the pre-reorder order for
        // a while and nothing noticed, because the two assertions under it only ever read back the
        // literals typed above them.
        var ladder = new[]
        {
            new CoachHintRungDto(1, CoachHintKind.Category),
            new CoachHintRungDto(2, CoachHintKind.Cloze),
            new CoachHintRungDto(3, CoachHintKind.FormCue)
        };

        ladder.Select(rung => rung.Rung).Should().Equal([1, 2, 3]);
        ladder.Select(rung => rung.Kind).Should().OnlyHaveUniqueItems(
            "a repeated kind is a rung that does not increase support");

        // The one claim here that is about the product rather than about this array: the top of the
        // ladder is the rung that gives away part of the form. Put anything else there and the
        // ladder stops being ordered by how much it discloses.
        ladder.Single(rung => rung.Rung == 3).Kind.Should().Be(
            CoachHintKind.FormCue,
            "the form-disclosing rung is the top of the ladder; the rung above it would be the answer");
        ladder.Single(rung => rung.Kind == CoachHintKind.Cloze).Rung.Should().Be(
            2, "a cloze supplies context and none of the form, so it sits below the form cue");
    }

    // ── Localization parity ──────────────────────────────────────────────────

    /// <summary>
    /// Every string this feature renders exists in both languages.
    /// </summary>
    /// <remarks>
    /// A missing Korean key does not fail: it falls back to English, silently, on the one surface
    /// whose job is telling a learner the truth about what the app will not do. The public-
    /// publication disclosure is the worst one to lose that way.
    /// </remarks>
    [Fact]
    public void Every_limitation_string_exists_in_english_and_korean()
    {
        var english = LoadKeys("AppResources.resx");
        var korean = LoadKeys("AppResources.ko.resx");

        var prefixes = new[]
        {
            "Coach_Limitation_", "Coach_Route_", "Coach_RouteEffect_", "Coach_Alternative_", "Coach_Hint_"
        };

        var scoped = english
            .Where(key => prefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal)))
            .ToArray();

        scoped.Should().HaveCountGreaterThan(
            30,
            "the parity check is vacuous if it finds no keys; the feature ships a closed vocabulary "
            + "of codes and one string per member");

        var missing = scoped.Where(key => !korean.Contains(key)).ToArray();

        missing.Should().BeEmpty(
            "a missing Korean key falls back to English silently. Missing: {0}",
            string.Join(", ", missing));
    }

    /// <summary>
    /// Every closed member this feature can emit has a string behind it.
    /// </summary>
    [Fact]
    public void Every_closed_member_has_a_localized_string()
    {
        var english = LoadKeys("AppResources.resx");

        foreach (var route in Enum.GetValues<CoachRouteName>().Where(r => r != CoachRouteName.Unknown))
        {
            english.Should().Contain($"Coach_Route_{route}", "route {0} must render as something", route);
        }

        foreach (var effect in Enum.GetValues<CoachRouteSideEffect>())
        {
            english.Should().Contain($"Coach_RouteEffect_{effect}", "effect {0} must disclose", effect);
        }

        foreach (var hint in Enum.GetValues<CoachHintKind>())
        {
            english.Should().Contain($"Coach_Hint_{hint}", "rung {0} must render", hint);
        }

        foreach (var alternative in Enum.GetValues<CoachAlternativeCode>()
                     .Where(code => code != CoachAlternativeCode.Unknown))
        {
            english.Should().Contain($"Coach_Alternative_{alternative}", "alternative {0} must render", alternative);
        }
    }

    /// <summary>
    /// The unknown route has no label at all, in either language.
    /// </summary>
    /// <remarks>
    /// Not an empty string — the repository forbids blank resource values, and rightly: a blank
    /// value is a key somebody meant to fill. The absence is the design. A placeholder like
    /// "Unknown screen" would occupy the space a destination goes in and read as one, sending the
    /// learner looking for a link this build cannot produce.
    /// </remarks>
    [Fact]
    public void Unknown_route_has_no_label_in_either_language()
    {
        foreach (var file in new[] { "AppResources.resx", "AppResources.ko.resx" })
        {
            LoadKeys(file).Should().NotContain("Coach_Route_Unknown");
        }
    }

    private static HashSet<string> LoadKeys(string fileName) =>
        XDocument.Load(ResourcePath(fileName))
            .Root!
            .Elements("data")
            .Select(element => element.Attribute("name")!.Value)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Walks up from the test binary to the repository root, which is where the resx files live
    /// relative to source rather than to output.
    /// </summary>
    private static string ResourcePath(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null &&
               !Directory.Exists(Path.Combine(directory.FullName, "src", "SentenceStudio.Shared")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the repository root must be reachable from the test binary");

        return Path.Combine(
            directory!.FullName, "src", "SentenceStudio.Shared", "Resources", "Strings", fileName);
    }

    // ================================================================ R5 regression: AnswerShapeInvalid wire format

    /// <summary>
    /// R5-6a: AnswerShapeInvalid round-trips through client wire JSON.
    /// </summary>
    [Fact]
    public void AnswerShapeInvalid_round_trips_on_the_client_wire()
    {
        var original = new CoachLimitationDto
        {
            Code = CoachLimitationCode.AnswerShapeInvalid,
            Coverage = CoachEvidenceCoverage.Unknown,
            AsOfUtc = AsOf
        };

        var json = JsonSerializer.Serialize(original, WireJson.Client);
        var restored = JsonSerializer.Deserialize<CoachLimitationDto>(json, WireJson.Client);

        json.Should().Contain("\"code\":\"AnswerShapeInvalid\"",
            "the code serializes as its enum name, not a numeric ordinal");
        json.Should().NotContain("\"code\":7",
            "the wire is string-valued, never ordinal-valued");

        restored.Should().NotBeNull();
        restored!.Code.Should().Be(CoachLimitationCode.AnswerShapeInvalid);
        restored.Coverage.Should().Be(CoachEvidenceCoverage.Unknown);
        restored.AsOfUtc.Should().Be(AsOf);
    }

    /// <summary>
    /// R5-6b: All prior CoachLimitationCode ordinals remain unchanged. If a new code is added
    /// between existing members, this test breaks — ordinal stability is a wire contract.
    /// </summary>
    [Fact]
    public void CoachLimitationCode_ordinals_are_stable()
    {
        // These ordinals are a wire contract. Changing them would break older clients that
        // compare by numeric value (even though we use string serialization, the enum backing
        // value is the source of truth for switch statements and DB storage).
        ((int)CoachLimitationCode.Unknown).Should().Be(0);
        ((int)CoachLimitationCode.NotBuilt).Should().Be(1);
        ((int)CoachLimitationCode.AvailableOnAnotherSurface).Should().Be(2);
        ((int)CoachLimitationCode.RefusedByDesign).Should().Be(3);
        ((int)CoachLimitationCode.WouldRemoveLearningValue).Should().Be(4);
        ((int)CoachLimitationCode.ExceedsSafeChangeScope).Should().Be(5);
        ((int)CoachLimitationCode.UnverifiedClaimWithheld).Should().Be(6);
        ((int)CoachLimitationCode.AnswerShapeInvalid).Should().Be(7);
    }

    /// <summary>
    /// R5-6c: The total number of CoachLimitationCode members matches the expected count,
    /// so an accidental addition without updating this test is caught.
    /// </summary>
    [Fact]
    public void CoachLimitationCode_member_count_is_known()
    {
        Enum.GetValues<CoachLimitationCode>().Should().HaveCount(8,
            "Unknown(0) through AnswerShapeInvalid(7) = 8 members; update this test if a new code is added");
    }
}
