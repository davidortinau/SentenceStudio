using SentenceStudio.Api.Coach.Application.History;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.History;

/// <summary>
/// The conversation path rebuilds each turn with the operation id. Every other field must survive,
/// or a new field silently stops working on that path (as ClientScreen did).
/// </summary>
public class CoachConversationTurnCopyTests
{
    [Fact]
    public void Every_turn_field_except_the_client_turn_id_survives_the_operation_copy()
    {
        var original = new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = "log a bug",
            ChipId = "chip",
            ConstraintAction = new CoachConstraintDeltaDto { AvailableMinutes = 10 },
            PendingSuggestionId = "s1",
            ExpectedPlanVersion = "v1",
            ClientTurnId = "client",
            ClientCapabilities = new CoachClientCapabilityHandshake { Version = 1 },
            ClientScreen = new CoachClientScreenContext { ScreenId = "native.study" }
        };

        var copy = CoachConversationService.WithOperationId(original, "op-1");

        copy.ClientTurnId.Should().Be("op-1");
        foreach (var property in typeof(CoachTurnRequest).GetProperties()
                     .Where(p => p.Name != nameof(CoachTurnRequest.ClientTurnId)))
        {
            var value = property.GetValue(original);
            value.Should().NotBeNull($"the fixture must set {property.Name} so the copy is tested");
            property.GetValue(copy).Should().Be(value, $"{property.Name} must survive the operation copy");
        }
    }
}
