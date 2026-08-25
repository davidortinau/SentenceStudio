using System.Text.Json;
using System.Text.Json.Serialization;
using SentenceStudio.Api.Coach.Opportunities;
using SentenceStudio.Api.Coach.Opportunities.Endpoints;

namespace SentenceStudio.Api.Tests.Coach.Opportunities;

/// <summary>
/// The operator surface's enums must cross the wire as names, not ordinals.
/// </summary>
/// <remarks>
/// <para>
/// This host has <b>no</b> global string-enum converter: every coach enum that crosses an HTTP
/// boundary opts in with <c>[JsonConverter(typeof(JsonStringEnumConverter))]</c> individually.
/// An enum that forgets it round-trips as a number, which breaks the request in one direction and
/// the response in the other — and does so at the JSON layer, before any handler runs, so the
/// server-side tests all still pass while the feature is completely broken.
/// </para>
/// <para>
/// It also matters for the same reason the stored-ordinal contract matters: a wire format built on
/// ordinals silently re-labels itself when a member is inserted.
/// </para>
/// </remarks>
public class CoachOpportunityContractSerializationTests
{
    /// <summary>The options a minimal-API host binds and serializes with.</summary>
    private static readonly JsonSerializerOptions ServerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The options the operator client uses.</summary>
    private static readonly JsonSerializerOptions ClientOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public void AReviewRequestSentAsStringsBindsOnTheServer()
    {
        // Exactly what SamOpportunityOperatorClient puts on the wire.
        const string body = """
            {"status":"Accepted","reviewerNoteCode":"SpecWritten","linkedSpecPath":"docs/specs/x.md"}
            """;

        var request = JsonSerializer.Deserialize<CoachOpportunityReviewRequest>(body, ServerOptions);

        request.Should().NotBeNull();
        request!.Status.Should().Be(CoachOpportunityStatus.Accepted);
        request.ReviewerNoteCode.Should().Be(CoachOpportunityReviewerNoteCode.SpecWritten);
        request.LinkedSpecPath.Should().Be("docs/specs/x.md");
    }

    [Fact]
    public void AnEvidenceResponseSerializesItsStateAsAName()
    {
        var response = new CoachOpportunityEvidenceResponse(
            "opp-1", CoachOpportunityEvidenceState.Unavailable, null, null, false, 3);

        var json = JsonSerializer.Serialize(response, ServerOptions);

        json.Should().Contain("\"Unavailable\"",
            "the client models this as a string, so a numeric enum fails to deserialize and a " +
            "successful reveal is reported to the operator as a failure");
        json.Should().NotContain("\"evidenceState\":1");
    }

    [Fact]
    public void TheClientCanReadWhatTheServerWrites()
    {
        var response = new CoachOpportunityEvidenceResponse(
            "opp-1", CoachOpportunityEvidenceState.Available, "yes", "Shall I?", false, 1);

        var json = JsonSerializer.Serialize(response, ServerOptions);

        // The client's shape: EvidenceState is a string there.
        var roundTripped = JsonSerializer.Deserialize<ClientShapedEvidenceResponse>(json, ClientOptions);

        roundTripped.Should().NotBeNull();
        roundTripped!.EvidenceState.Should().Be("Available");
        roundTripped.EvidenceRevealCount.Should().Be(1);
    }

    [Fact]
    public void TheServerCanReadWhatTheClientWrites()
    {
        var clientRequest = new ClientShapedReviewRequest("Deferred", "NeedsCaptainDecision", null);
        var json = JsonSerializer.Serialize(clientRequest, ClientOptions);

        var serverRequest = JsonSerializer.Deserialize<CoachOpportunityReviewRequest>(json, ServerOptions);

        serverRequest.Should().NotBeNull();
        serverRequest!.Status.Should().Be(CoachOpportunityStatus.Deferred);
        serverRequest.ReviewerNoteCode.Should().Be(CoachOpportunityReviewerNoteCode.NeedsCaptainDecision);
    }

    [Fact]
    public void AnUnknownStatusNameIsRefusedRatherThanCoerced()
    {
        var act = () => JsonSerializer.Deserialize<CoachOpportunityReviewRequest>(
            """{"status":"Whatever","reviewerNoteCode":null}""", ServerOptions);

        act.Should().Throw<JsonException>(
            "an unrecognised status must be a 400, not a silent zero-value 'New'");
    }

    [Fact]
    public void EveryWireEnumOnTheOperatorSurfaceOptsIntoStringNames()
    {
        // The convention this host relies on, asserted rather than remembered.
        Type[] wireEnums =
        [
            typeof(CoachOpportunityStatus),
            typeof(CoachOpportunityReviewerNoteCode),
            typeof(CoachOpportunityEvidenceState)
        ];

        foreach (var type in wireEnums)
        {
            var attribute = type.GetCustomAttributes(typeof(JsonConverterAttribute), false)
                .Cast<JsonConverterAttribute>()
                .FirstOrDefault();

            attribute.Should().NotBeNull(
                $"{type.Name} crosses the HTTP boundary as an enum-typed member and this host " +
                "has no global string-enum converter");
            attribute!.ConverterType.Should().Be(typeof(JsonStringEnumConverter));
        }
    }

    private sealed record ClientShapedEvidenceResponse(
        string OpportunityId,
        string EvidenceState,
        string? LearnerMessageText,
        string? PriorCoachMessageText,
        bool CrossOwner,
        int EvidenceRevealCount);

    private sealed record ClientShapedReviewRequest(
        string Status,
        string? ReviewerNoteCode,
        string? LinkedSpecPath);
}
