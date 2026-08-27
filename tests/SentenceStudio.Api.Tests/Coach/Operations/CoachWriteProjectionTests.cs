using FluentAssertions;
using SentenceStudio.Api.Coach.Operations;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Operations;

/// <summary>
/// The translation from the ledger's internal vocabulary to the closed contract a client reads.
/// </summary>
/// <remarks>
/// <para>
/// Two things are being pinned. The first is coverage: every registered write tool, every stored
/// status, and every entity kind has to reach a named client value, or a change the learner is
/// asked to approve is described to them as "Unknown".
/// </para>
/// <para>
/// The second is the failure direction. Anything this build does not recognise must arrive as the
/// value that offers no control, because the alternative — mapping an unfamiliar status onto a
/// familiar one — produces a plausible card describing something that is not true.
/// </para>
/// </remarks>
public class CoachWriteProjectionTests
{
    [Fact]
    public void Every_registered_write_tool_has_its_own_change_kind()
    {
        var mapped = CoachToolNames.AllWrite
            .ToDictionary(name => name, CoachWriteProjection.ChangeKind);

        mapped.Values.Should().NotContain(CoachWriteChangeKind.Unknown,
            "a tool the client cannot name is a change described to the learner as 'Unknown'");
        mapped.Values.Should().OnlyHaveUniqueItems(
            "two tools sharing a kind would give a removal the same heading as an edit");
    }

    [Fact]
    public void The_change_kinds_are_exactly_the_write_tools_plus_the_fallback()
    {
        Enum.GetValues<CoachWriteChangeKind>()
            .Count(kind => kind != CoachWriteChangeKind.Unknown)
            .Should().Be(CoachToolNames.AllWrite.Count,
                "a kind with no tool behind it is a card nothing can produce, and a tool with no "
                + "kind is a card the client cannot label");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("get_skill_list")]
    [InlineData("propose_something_new")]
    public void An_unrecognised_tool_is_the_neutral_kind(string? toolName) =>
        CoachWriteProjection.ChangeKind(toolName).Should().Be(CoachWriteChangeKind.Unknown);

    [Theory]
    [InlineData(CoachToolRiskClass.WriteSoft, CoachWriteRiskClass.WriteSoft)]
    [InlineData(CoachToolRiskClass.WriteHard, CoachWriteRiskClass.WriteHard)]
    public void The_two_write_risk_classes_map_across(
        CoachToolRiskClass source, CoachWriteRiskClass expected) =>
        CoachWriteProjection.RiskClass(source).Should().Be(expected);

    /// <summary>
    /// A read tool never produces a proposal, so a read risk class arriving here is a shape the
    /// client should not offer to approve.
    /// </summary>
    [Fact]
    public void A_read_risk_class_is_not_approvable() =>
        CoachWriteProjection.RiskClass(CoachToolRiskClass.Read).Should().Be(CoachWriteRiskClass.Unknown);

    [Fact]
    public void Every_stored_status_reaches_a_named_client_status()
    {
        foreach (var status in Enum.GetValues<CoachWriteOperationStatus>())
        {
            CoachWriteProjection.Status(status).Should().NotBe(CoachWriteStatus.Unknown,
                $"{status} is a state the ledger can be in and the card has to describe it");
        }
    }

    [Fact]
    public void The_two_status_sets_line_up_one_for_one()
    {
        Enum.GetValues<CoachWriteOperationStatus>()
            .Select(CoachWriteProjection.Status)
            .Should().OnlyHaveUniqueItems("two ledger states sharing a client state hides one of them");
    }

    /// <summary>
    /// The interesting direction: a status this build has never seen must not read as applied.
    /// </summary>
    [Fact]
    public void An_unrecognised_status_offers_nothing() =>
        CoachWriteProjection.Status((CoachWriteOperationStatus)99).Should().Be(CoachWriteStatus.Unknown);

    [Fact]
    public void Every_entity_kind_reaches_a_named_target_kind()
    {
        foreach (var kind in Enum.GetValues<CoachWriteEntityKind>())
        {
            var target = CoachWriteProjection.TargetKind(kind);

            if (kind == CoachWriteEntityKind.None)
            {
                target.Should().Be(CoachWriteTargetKind.None);
                continue;
            }

            target.Should().NotBe(CoachWriteTargetKind.None,
                $"{kind} names a real row and the receipt has to say what it touched");
        }
    }

    [Fact]
    public void An_unrecognised_entity_kind_points_at_nothing() =>
        CoachWriteProjection.TargetKind((CoachWriteEntityKind)99).Should().Be(CoachWriteTargetKind.None);

    /// <summary>
    /// The wire name of the confirmation header, pinned on the server side.
    /// </summary>
    /// <remarks>
    /// The client cannot reference this assembly, so it writes the same literal and pins it in its
    /// own suite. A rename on either side is a 404 at runtime and nowhere else; two tests over one
    /// literal is what makes the rename visible at build time instead.
    /// </remarks>
    [Fact]
    public void The_confirmation_header_name_is_the_one_the_client_sends() =>
        CoachWriteHeaders.Confirmation.Should().Be("X-Coach-Write-Confirmation");

    /// <summary>
    /// The two approval-channel literals are part of the wire contract too: the client compares
    /// against them to decide whether a card's risk class and its approval mode agree.
    /// </summary>
    [Fact]
    public void The_approval_mode_literals_are_stable()
    {
        CoachWriteApprovalModes.Accept.Should().Be("accept");
        CoachWriteApprovalModes.Confirm.Should().Be("confirm");
    }
}
