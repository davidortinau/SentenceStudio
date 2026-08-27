using System.Text.Json;
using FluentAssertions;
using SentenceStudio.Api.Coach.Application.History;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.History;

/// <summary>
/// A build that predates W9, reading a row this build wrote.
/// </summary>
/// <remarks>
/// <para>
/// <b>The scenario.</b> A rolling deployment. Replica A has W9 and writes a v3 payload with a
/// <c>grounding</c> section. Replica B is one release behind and reads it. What B does with that
/// row is the entire justification for not bumping the schema version, and it is a claim no test
/// against the current parser can make — the current parser knows about grounding.
/// </para>
/// <para>
/// <b>So the old parser is reproduced here, frozen.</b> <see cref="FrozenPreW9Reader"/> is a
/// transcription of <c>ReadOutcome</c> / <c>ReadWrappedOutcome</c> as they stood before R0: three
/// sections, the same version arms, the same tolerance. It deliberately does not call production
/// code, because calling production code would test today's reader against today's writer and prove
/// nothing about yesterday's.
/// </para>
/// <para>
/// <b>A frozen copy drifts, so there is a companion.</b>
/// <see cref="The_frozen_reader_still_agrees_with_production_on_a_pre_W9_payload"/> feeds both
/// readers a payload with no grounding section and requires identical results. If the production
/// parser ever changes shape underneath this file, that test fails and the transcription gets
/// re-taken rather than quietly becoming fiction.
/// </para>
/// </remarks>
public sealed class CoachPreW9ReaderCompatibilityTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// A W9-written payload, read by the pre-W9 parser, keeps everything that parser knew about.
    /// </summary>
    [Fact]
    public void A_pre_W9_reader_ignores_grounding_and_keeps_the_other_three_sections()
    {
        var payload = JsonSerializer.Serialize(
            new CoachStoredTurnOutcome(
                CoachGroundingSectionSchemaTests.Answer("rolling-deploy-turn"),
                CoachGroundingSectionSchemaTests.Trace(),
                CoachGroundingSectionSchemaTests.Dispute(),
                CoachGroundingSectionSchemaTests.Summary()),
            Web);

        // Non-vacuity: if the payload did not actually carry a grounding section, the assertions
        // below would pass against a payload the old reader was always going to handle.
        payload.Should().Contain(
            "\"grounding\"",
            "this test is worthless unless the payload it feeds the old reader really does carry "
            + "the new section");
        payload.Should().Contain("Enforce");

        var read = FrozenPreW9Reader.ReadOutcome(payload, 3);

        read.Should().NotBeNull(
            "an older replica reading a row this build wrote must not report the turn as absent \u2014 "
            + "that is the exact failure a version bump would have caused");

        read!.Answer.Should().NotBeNull();
        read.Answer!.TurnId.Should().Be("rolling-deploy-turn");
        read.Trace.Should().NotBeNull();
        read.Trace!.Calls.Should().ContainSingle();
        read.Dispute.Should().NotBeNull();
        read.Dispute!.Signal.Should().Be(
            SentenceStudio.Api.Coach.Application.CoachCorrectionSignal.WrongClaim);
    }

    /// <summary>
    /// The bump that was rejected, demonstrated rather than asserted.
    /// </summary>
    /// <remarks>
    /// The same payload labelled version 4 is invisible to the old reader — no answer, no trace, no
    /// dispute. This is what the ceremony's v3 → v4 proposal would have shipped, and it is why the
    /// amended ruling keeps the version at 3.
    /// </remarks>
    [Fact]
    public void The_same_payload_at_version_four_would_have_been_invisible()
    {
        var payload = JsonSerializer.Serialize(
            new CoachStoredTurnOutcome(
                CoachGroundingSectionSchemaTests.Answer(),
                CoachGroundingSectionSchemaTests.Trace(),
                CoachGroundingSectionSchemaTests.Dispute(),
                CoachGroundingSectionSchemaTests.Summary()),
            Web);

        FrozenPreW9Reader.ReadOutcome(payload, 4).Should().BeNull(
            "an older replica falls into the unknown-version arm and reports no answer at all. "
            + "Keeping the version at 3 is what avoids this");

        FrozenPreW9Reader.ReadOutcome(payload, 3).Should().NotBeNull(
            "the control: the identical bytes are readable when the version stays at 3, so the "
            + "difference is the version label and nothing else");
    }

    /// <summary>
    /// Anti-drift. The frozen copy must still describe the production parser it was taken from.
    /// </summary>
    /// <remarks>
    /// Compared on a payload with no grounding section, which is the only input on which the two
    /// readers are supposed to agree exactly. A divergence here means the transcription is stale
    /// and the compatibility claim above is being made by a file that no longer resembles the code.
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(0)]
    [InlineData(4)]
    public void The_frozen_reader_still_agrees_with_production_on_a_pre_W9_payload(int version)
    {
        var payload = version == 1
            ? JsonSerializer.Serialize(CoachGroundingSectionSchemaTests.Answer("pre-w9"), Web)
            : JsonSerializer.Serialize(
                new CoachStoredTurnOutcome(
                    CoachGroundingSectionSchemaTests.Answer("pre-w9"),
                    CoachGroundingSectionSchemaTests.Trace(),
                    CoachGroundingSectionSchemaTests.Dispute()),
                Web);

        payload.Should().NotContain("\"grounding\"", "the comparison is only valid pre-W9");

        var frozen = FrozenPreW9Reader.ReadOutcome(payload, version);
        var production = CoachConversationService.ReadOutcome(payload, version);

        (frozen is null).Should().Be(
            production is null,
            "the two readers disagree about whether version {0} is readable, which means the frozen "
            + "transcription is stale and must be re-taken from the current parser",
            version);

        if (frozen is null || production is null)
        {
            return;
        }

        frozen.Answer?.TurnId.Should().Be(production.Answer?.TurnId);
        (frozen.Trace is null).Should().Be(production.Trace is null);
        (frozen.Dispute is null).Should().Be(production.Dispute is null);
    }

    [Fact]
    public void A_pre_W9_reader_still_handles_a_row_with_no_grounding()
    {
        var payload = JsonSerializer.Serialize(
            new CoachStoredTurnOutcome(
                CoachGroundingSectionSchemaTests.Answer(),
                CoachGroundingSectionSchemaTests.Trace()),
            Web);

        var read = FrozenPreW9Reader.ReadOutcome(payload, 3);

        read!.Answer.Should().NotBeNull();
        read.Trace.Should().NotBeNull();
        read.Dispute.Should().BeNull();
    }
}

/// <summary>
/// The outcome parser exactly as it stood before W9 R0. Do not extend.
/// </summary>
/// <remarks>
/// <para>
/// A transcription, not a wrapper. It knows about three sections and three versions, and it must
/// keep knowing about exactly those — the moment somebody teaches it about grounding, it stops
/// being an older build and the compatibility claim it exists to prove evaporates.
/// </para>
/// <para>
/// Reads into <see cref="CoachStoredTurnOutcome"/> because the record's fourth parameter is
/// optional; the old parser never supplied it, and neither does this.
/// </para>
/// </remarks>
internal static class FrozenPreW9Reader
{
    private static readonly JsonSerializerOptions OutcomeJson = new(JsonSerializerDefaults.Web);

    private const int LegacyOutcomeSchemaVersion = 1;
    private const int WrappedWithoutDispute = 2;
    private const int WrappedWithDispute = 3;

    internal static CoachStoredTurnOutcome? ReadOutcome(string? payload, int? schemaVersion)
    {
        if (payload is null)
        {
            return null;
        }

        try
        {
            return schemaVersion switch
            {
                LegacyOutcomeSchemaVersion => new CoachStoredTurnOutcome(
                    JsonSerializer.Deserialize<CoachTurnResponse>(payload, OutcomeJson), null),
                WrappedWithoutDispute or WrappedWithDispute => ReadWrapped(payload),
                _ => null
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static CoachStoredTurnOutcome? ReadWrapped(string payload)
    {
        using var document = JsonDocument.Parse(payload);

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var answer = TryGetSection(document.RootElement, nameof(CoachStoredTurnOutcome.Answer)) is { } answerSection
            ? answerSection.Deserialize<CoachTurnResponse>(OutcomeJson)
            : null;

        return new CoachStoredTurnOutcome(
            answer,
            ReadTraceSection(document.RootElement),
            ReadDisputeSection(document.RootElement));
    }

    private static CoachTurnTraceSummary? ReadTraceSection(JsonElement root)
    {
        if (TryGetSection(root, nameof(CoachStoredTurnOutcome.Trace)) is not { } section)
        {
            return null;
        }

        try
        {
            var trace = section.Deserialize<CoachTurnTraceSummary>(OutcomeJson);

            return trace is not null && CoachTurnTraceIntegrity.IsReadable(trace) ? trace : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static CoachTurnDisputeState? ReadDisputeSection(JsonElement root)
    {
        if (TryGetSection(root, nameof(CoachStoredTurnOutcome.Dispute)) is not { } section)
        {
            return null;
        }

        try
        {
            var dispute = section.Deserialize<CoachTurnDisputeState>(OutcomeJson);

            if (dispute is null)
            {
                return null;
            }

            var identifier = dispute.DisputedMessageId;

            return string.IsNullOrWhiteSpace(identifier)
                   || identifier.Length > CoachTurnDisputeState.MaxDisputedMessageIdLength
                   || !Enum.IsDefined(dispute.Signal)
                   || !Enum.IsDefined(dispute.Resolution)
                ? null
                : dispute;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement? TryGetSection(JsonElement root, string name)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return property.Value.ValueKind == JsonValueKind.Null ? null : property.Value;
        }

        return null;
    }
}
