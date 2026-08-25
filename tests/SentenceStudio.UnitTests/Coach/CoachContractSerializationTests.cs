using System.Text.Json;
using FluentAssertions;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.UnitTests.Coach;

/// <summary>
/// Proves the coach transport contracts survive a JSON round trip without loss.
/// </summary>
public class CoachContractSerializationTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Turn_response_round_trips_without_loss()
    {
        var original = CoachContractSamples.TurnResponse();

        var json = JsonSerializer.Serialize(original, Options);
        var restored = JsonSerializer.Deserialize<CoachTurnResponse>(json, Options);

        restored.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void Session_response_round_trips_without_loss()
    {
        var original = CoachContractSamples.SessionResponse();

        var json = JsonSerializer.Serialize(original, Options);
        var restored = JsonSerializer.Deserialize<CoachSessionResponse>(json, Options);

        restored.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void Turn_request_round_trips_a_structured_constraint_action()
    {
        var original = new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.ConstraintAction,
            ExpectedPlanVersion = "plan-v3",
            ClientTurnId = "turn-1",
            ConstraintAction = new CoachConstraintDeltaDto
            {
                AvailableMinutes = 10,
                AudioAllowed = false,
                ClearSkillEmphasis = true,
                ChangedFields = new[] { CoachConstraintField.AvailableMinutes, CoachConstraintField.AudioAllowed }
            }
        };

        var json = JsonSerializer.Serialize(original, Options);
        var restored = JsonSerializer.Deserialize<CoachTurnRequest>(json, Options);

        restored.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void Availability_response_round_trips_without_loss()
    {
        var original = new CoachAvailabilityResponse
        {
            IsAvailable = true,
            State = CoachAvailabilityState.ResumeAvailable,
            EntryPointLabel = "Resume coach",
            ActiveSessionId = "session-1",
            ActiveSessionStatus = CoachSessionStatus.SuggestionPending,
            ActiveSessionExpiresAtUtc = new DateTime(2026, 8, 15, 1, 0, 0, DateTimeKind.Utc),
            RunsRemainingToday = 4,
            RunsRemainingThisWeek = 18
        };

        var json = JsonSerializer.Serialize(original, Options);
        var restored = JsonSerializer.Deserialize<CoachAvailabilityResponse>(json, Options);

        restored.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void Start_session_request_defaults_to_resume()
    {
        var restored = JsonSerializer.Deserialize<StartCoachSessionRequest>("{}", Options);

        restored!.Resume.Should().BeTrue();
        restored.PlanDate.Should().BeNull();
        restored.InitialText.Should().BeNull();
    }

    [Fact]
    public void Enum_members_use_names_on_the_wire()
    {
        var json = JsonSerializer.Serialize(CoachContractSamples.TurnResponse(), Options);

        json.Should().Contain("\"status\":\"Completed\"");
        json.Should().Contain("\"stopReason\":\"Completed\"");
        json.Should().Contain("\"sessionStatus\":\"Active\"");
        json.Should().Contain("\"activityType\":\"VocabularyReview\"");
        json.Should().Contain("\"energyLevel\":\"Low\"");
    }

    [Fact]
    public void A_missing_required_member_is_refused()
    {
        const string json = """
            {"sessionId":"s-1","turnId":"t-1","status":"Completed"}
            """;

        var act = () => JsonSerializer.Deserialize<CoachTurnResponse>(json, Options);

        act.Should().Throw<JsonException>("the response contract states which members must be present");
    }

    [Fact]
    public void Write_requests_round_trip_their_stale_and_repeat_guards()
    {
        var decision = new CoachSuggestionDecisionRequest
        {
            ExpectedPlanVersion = "plan-v2",
            ClientTurnId = "turn-9"
        };
        var undo = new CoachUndoRequest
        {
            RevisionId = "revision-1",
            ExpectedPlanVersion = "plan-v3",
            ClientTurnId = "turn-10"
        };

        JsonSerializer.Deserialize<CoachSuggestionDecisionRequest>(JsonSerializer.Serialize(decision, Options), Options)
            .Should().BeEquivalentTo(decision);
        JsonSerializer.Deserialize<CoachUndoRequest>(JsonSerializer.Serialize(undo, Options), Options)
            .Should().BeEquivalentTo(undo);
    }

    [Fact]
    public void Collection_members_default_to_empty_not_null()
    {
        var plan = new CoachPlanStateDto
        {
            PlanDate = new DateOnly(2026, 8, 14),
            PlanVersion = "plan-v1",
            AppliedConstraints = CoachContractSamples.Constraints(),
            EstimatedTotalMinutes = 10,
            CompletedCount = 0,
            TotalCount = 0,
            CompletionPercentage = 0
        };

        plan.Items.Should().BeEmpty();
    }
}
