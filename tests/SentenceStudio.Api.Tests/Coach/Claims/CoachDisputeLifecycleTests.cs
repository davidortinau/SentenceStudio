using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Options;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Coach.Tools.Observation;
using SentenceStudio.Api.Coach.Validation.Claims;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Claims;

/// <summary>
/// The dispute lifecycle: open, block a repeat, clear on a compliant answer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Case D is the fixture that matters.</b> The coach repeated a disputed list with more
/// confidence. From the learner's side that is indistinguishable from not having spoken, and it is
/// the one failure in this workstream with no in-conversation recovery — a learner who cannot
/// correct the coach has no remaining move.
/// </para>
/// <para>
/// The three exits are tested individually and the blocked path is tested against each of them, so
/// a change that accidentally widened one exit shows up as the blocked case going green.
/// </para>
/// </remarks>
public sealed class CoachDisputeLifecycleTests
{
    private const string DisputedMessageId = "3f1c9a44-0d3e-4c1b-9a5e-77b2c1d0e912";
    private static readonly DateTime Now = new(2026, 8, 22, 2, 5, 0, DateTimeKind.Utc);

    private static CoachDisputeCoordinator Coordinator(bool enabled = true)
    {
        var options = new CoachOptions { CorrectionState = new CoachFeatureSwitch { Enabled = enabled } };

        return new CoachDisputeCoordinator(
            new CoachCorrectionClassifier(),
            new StaticOptionsMonitor<CoachOptions>(options));
    }

    /// <summary>The turn the learner disputed: it read the plan's vocabulary.</summary>
    private static CoachTurnTraceSummary DisputedTrace() =>
        Trace(CoachScopeDefinition.TrackedVocabularyDueSummary);

    private static CoachTurnTraceSummary Trace(params CoachScopeDefinition[] definitions) =>
        new(
            [.. definitions.Select((definition, index) => new CoachTurnTraceEntry(
                Ordinal: index + 1,
                ToolName: "read",
                Outcome: CoachToolCallOutcome.Succeeded,
                FailureKind: null,
                ArgumentMask: CoachToolArgumentMask.None,
                ElapsedMs: 9,
                Coverage: CoachScopeCoverage.CompleteOwnedSet,
                DefinitionCode: definition,
                WithheldReason: CoachScopeWithheldReason.None,
                MatchedCount: 12,
                ReturnedCount: 12,
                WithheldCount: null,
                Truncated: false))],
            BudgetUsed: definitions.Length,
            BudgetLimit: 6);

    private static CoachClaimRuleContext NextTurn(string text, CoachTurnTraceSummary? trace) =>
        NextTurn(text, trace, limitation: null);

    private static CoachClaimRuleContext NextTurn(
        string text,
        CoachTurnTraceSummary? trace,
        CoachLimitationDto? limitation) => new()
    {
        Answer = ClaimFixture.Answer(text),
        Evidence = [ClaimFixture.Evidence(CoachEvidenceCoverage.CompleteOwnedSet)],
        Trace = trace,
        Limitation = limitation
    };

    // ── Opening ──────────────────────────────────────────────────────────────

    [Fact]
    public void A_correction_opens_a_dispute_keyed_to_the_message()
    {
        var dispute = Coordinator().TryOpen(
            "No, I meant the words I looked up, not the ones in the plan.",
            DisputedMessageId,
            DisputedTrace(),
            Now);

        dispute.Should().NotBeNull();
        dispute!.IsOpen.Should().BeTrue();
        dispute.DisputedMessageId.Should().Be(DisputedMessageId);
        dispute.OpenedAtUtc.Should().Be(Now);
        dispute.Resolution.Should().Be(CoachDisputeResolution.Open);

        dispute.DisputedDefinitionCodes.Should().Equal(
            [CoachScopeDefinition.TrackedVocabularyDueSummary],
            "the definitions the disputed answer read are what the next turn is compared against, "
            + "and storing them with the dispute is what makes the comparison survive a reload");
    }

    [Fact]
    public void An_ordinary_question_opens_nothing()
    {
        Coordinator().TryOpen("What does this word mean?", DisputedMessageId, DisputedTrace(), Now)
            .Should().BeNull();
    }

    /// <summary>Off is a total bypass, asserted on the strongest possible input.</summary>
    [Fact]
    public void The_flag_off_opens_nothing_even_for_a_clear_correction()
    {
        Coordinator(enabled: false).TryOpen(
            "That's not what I asked.",
            DisputedMessageId,
            DisputedTrace(),
            Now).Should().BeNull("off is a total bypass, not a quieter mode");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_dispute_without_an_anchor_is_not_opened(string messageId)
    {
        Coordinator().TryOpen("That's not what I asked.", messageId, DisputedTrace(), Now)
            .Should().BeNull("an unanchored dispute would constrain the next answer about nothing");
    }

    /// <summary>
    /// The identifier is bounded, so the field cannot become a channel for prose.
    /// </summary>
    [Fact]
    public void An_oversized_message_identifier_is_refused()
    {
        var tooLong = new string('a', CoachTurnDisputeState.MaxDisputedMessageIdLength + 1);

        Coordinator().TryOpen("That's not what I asked.", tooLong, DisputedTrace(), Now)
            .Should().BeNull(
                "the bound is what turns 'do not put prose in this field' from a comment into "
                + "something the code enforces");
    }

    // ── Case D: the repeat is blocked ────────────────────────────────────────

    /// <summary>
    /// The same read, answered again more confidently. This must not resolve the dispute.
    /// </summary>
    [Fact]
    public void Repeating_the_disputed_claim_from_the_same_read_is_refused()
    {
        var dispute = Coordinator().TryOpen(
            "That's not what I asked.", DisputedMessageId, DisputedTrace(), Now)!;

        var next = NextTurn(
            "You definitely have twelve words due this week.",
            DisputedTrace());

        new CoachRepeatedDisputedClaimRule()
            .Evaluate(next.WithDispute(dispute))
            .Should().ContainSingle(
                "Case D: the coach repeated a disputed list with more confidence. Citing the same "
                + "read is not a re-read");
    }

    [Fact]
    public void An_unresolved_repeat_leaves_the_dispute_open()
    {
        var coordinator = Coordinator();
        var dispute = coordinator.TryOpen(
            "That's not what I asked.", DisputedMessageId, DisputedTrace(), Now)!;

        var resolved = coordinator.Resolve(
            dispute,
            NextTurn("You definitely have twelve words due this week.", DisputedTrace()),
            Now.AddMinutes(1));

        resolved.IsOpen.Should().BeTrue();
        resolved.ResolvedAtUtc.Should().BeNull();
    }

    // ── The three exits ──────────────────────────────────────────────────────

    /// <summary>AC-S14: the re-read uses different parameters.</summary>
    [Fact]
    public void A_re_read_with_a_different_definition_clears_the_dispute()
    {
        var coordinator = Coordinator();
        var dispute = coordinator.TryOpen(
            "No, I meant the words I looked up.", DisputedMessageId, DisputedTrace(), Now)!;

        var resolved = coordinator.Resolve(
            dispute,
            NextTurn("Here is what I found.", Trace(CoachScopeDefinition.UndueVocabularySearch)),
            Now.AddMinutes(1));

        resolved.Resolution.Should().Be(CoachDisputeResolution.ResolvedByReRead);
        resolved.IsOpen.Should().BeFalse();
        resolved.ResolvedAtUtc.Should().Be(Now.AddMinutes(1));
    }

    /// <summary>
    /// The same definition twice is the same question asked twice, not a re-read.
    /// </summary>
    [Fact]
    public void Calling_the_same_definition_again_does_not_count_as_a_re_read()
    {
        var coordinator = Coordinator();
        var dispute = coordinator.TryOpen(
            "That's not what I asked.", DisputedMessageId, DisputedTrace(), Now)!;

        var resolved = coordinator.Resolve(
            dispute,
            NextTurn(
                "Here it is again.",
                Trace(
                    CoachScopeDefinition.TrackedVocabularyDueSummary,
                    CoachScopeDefinition.TrackedVocabularyDueSummary)),
            Now.AddMinutes(1));

        resolved.IsOpen.Should().BeTrue(
            "definitions, not call counts. Case D's repeat would pass a count-based check trivially");
    }

    /// <summary>AC-S14: the prior claim is named.</summary>
    [Theory]
    [InlineData("I was wrong about that count.")]
    [InlineData("I said twelve earlier, and that was not right.")]
    // Anchored: "that was not right" alone is Sam grading the learner. It only names the prior
    // claim when the speaker has said the earlier claim was theirs.
    [InlineData("My earlier answer was wrong \u2014 I used the wrong list.")]
    public void Naming_the_prior_claim_clears_the_dispute(string text)
    {
        var coordinator = Coordinator();
        var dispute = coordinator.TryOpen(
            "That's wrong.", DisputedMessageId, DisputedTrace(), Now)!;

        coordinator.Resolve(dispute, NextTurn(text, DisputedTrace()), Now.AddMinutes(1))
            .Resolution.Should().Be(CoachDisputeResolution.ResolvedByCorrection);
    }

    [Theory]
    [InlineData(CoachLimitationCode.NotBuilt)]
    [InlineData(CoachLimitationCode.AvailableOnAnotherSurface)]
    [InlineData(CoachLimitationCode.RefusedByDesign)]
    public void A_typed_limitation_on_the_disputed_claim_clears_the_dispute(CoachLimitationCode code)
    {
        var coordinator = Coordinator();
        var dispute = coordinator.TryOpen(
            "That's wrong.", DisputedMessageId, DisputedTrace(), Now)!;

        coordinator.Resolve(
                dispute,
                NextTurn("Here is what I can tell you.", DisputedTrace(), Limitation(code)),
                Now.AddMinutes(1))
            .Resolution.Should().Be(CoachDisputeResolution.ResolvedByLimitation);
    }

    /// <summary>
    /// The exit reads the projected limitation, never the sentence.
    /// </summary>
    /// <remarks>
    /// These three sentences used to clear the dispute through a phrase list. A model that had
    /// consulted nothing could produce any of them, which made "I didn't check that" a way to
    /// escape a constraint that existed because the coach had not checked. The typed limitation
    /// comes from the turn's own findings and cannot be written by the answer text.
    /// </remarks>
    [Theory]
    [InlineData("I can't tell you that from what I looked at.")]
    [InlineData("I only looked at part of your data.")]
    [InlineData("I didn't check that.")]
    public void Prose_that_states_a_limitation_does_not_clear_the_dispute(string text)
    {
        var coordinator = Coordinator();
        var dispute = coordinator.TryOpen(
            "That's wrong.", DisputedMessageId, DisputedTrace(), Now)!;

        coordinator.Resolve(dispute, NextTurn(text, DisputedTrace()), Now.AddMinutes(1))
            .IsOpen.Should().BeTrue(
                "an unverified sentence claiming a boundary is not a boundary; the exit reads the "
                + "typed limitation the turn produced or it holds the constraint");
    }

    /// <summary>A limitation about some other request leaves this dispute standing.</summary>
    [Theory]
    [InlineData(CoachLimitationCode.WouldRemoveLearningValue)]
    [InlineData(CoachLimitationCode.ExceedsSafeChangeScope)]
    [InlineData(CoachLimitationCode.Unknown)]
    public void An_unrelated_typed_limitation_does_not_clear_the_dispute(CoachLimitationCode code)
    {
        var coordinator = Coordinator();
        var dispute = coordinator.TryOpen(
            "That's wrong.", DisputedMessageId, DisputedTrace(), Now)!;

        coordinator.Resolve(
                dispute,
                NextTurn("Here is what I can tell you.", DisputedTrace(), Limitation(code)),
                Now.AddMinutes(1))
            .IsOpen.Should().BeTrue(
                "{0} refuses a different request and says nothing about the disputed claim",
                code);
    }

    private static CoachLimitationDto Limitation(CoachLimitationCode code) =>
        new()
        {
            Code = code,
            Coverage = CoachEvidenceCoverage.CompleteOwnedSet
        };

    /// <summary>A generic apology names no claim, so it is not a correction.</summary>
    [Theory]
    [InlineData("Sorry about that.")]
    [InlineData("Let me try again.")]
    [InlineData("Apologies for the confusion.")]
    public void A_generic_apology_does_not_clear_the_dispute(string text)
    {
        var coordinator = Coordinator();
        var dispute = coordinator.TryOpen(
            "That's wrong.", DisputedMessageId, DisputedTrace(), Now)!;

        coordinator.Resolve(dispute, NextTurn(text, DisputedTrace()), Now.AddMinutes(1))
            .IsOpen.Should().BeTrue(
                "AC-S14 asks for the prior claim to be named; an apology acknowledges a feeling and "
                + "names nothing");
    }

    /// <summary>A re-read outranks a correction when both are present, deterministically.</summary>
    [Fact]
    public void A_re_read_outranks_a_named_correction()
    {
        var coordinator = Coordinator();
        var dispute = coordinator.TryOpen(
            "That's wrong.", DisputedMessageId, DisputedTrace(), Now)!;

        coordinator.Resolve(
                dispute,
                NextTurn("I was wrong. Here is what I found instead.",
                    Trace(CoachScopeDefinition.UndueVocabularySearch)),
                Now.AddMinutes(1))
            .Resolution.Should().Be(
                CoachDisputeResolution.ResolvedByReRead,
                "looking somewhere new is the more complete response, and the ordering must not "
                + "depend on which check ran first");
    }

    // ── Dismissal and idempotence ────────────────────────────────────────────

    [Fact]
    public void A_learner_can_dismiss_an_open_dispute()
    {
        var coordinator = Coordinator();
        var dispute = coordinator.TryOpen(
            "That's wrong.", DisputedMessageId, DisputedTrace(), Now)!;

        var dismissed = coordinator.Dismiss(dispute, Now.AddMinutes(5));

        dismissed.Resolution.Should().Be(
            CoachDisputeResolution.DismissedByLearner,
            "recorded as its own resolution so a metric can tell a dispute the coach satisfied from "
            + "one the learner gave up on \u2014 those two numbers mean opposite things");
        dismissed.IsOpen.Should().BeFalse();
    }

    [Fact]
    public void Resolving_a_closed_dispute_changes_nothing()
    {
        var coordinator = Coordinator();
        var dispute = coordinator.TryOpen(
            "That's wrong.", DisputedMessageId, DisputedTrace(), Now)!;

        var resolved = coordinator.Resolve(
            dispute,
            NextTurn("Here is what I found.", Trace(CoachScopeDefinition.UndueVocabularySearch)),
            Now.AddMinutes(1));

        coordinator.Resolve(resolved, NextTurn("Anything.", DisputedTrace()), Now.AddMinutes(9))
            .Should().Be(resolved, "a closed dispute does not reopen or re-timestamp");
    }

    [Fact]
    public void Timestamps_are_whole_second_utc()
    {
        var fractional = Now.AddTicks(4_821_593);

        var dispute = Coordinator().TryOpen(
            "That's wrong.", DisputedMessageId, DisputedTrace(), fractional)!;

        dispute.OpenedAtUtc.Should().Be(
            Now,
            "truncated to match every other coach timestamp; rounding up would record the dispute "
            + "as opening after the answer it disputes");
        dispute.OpenedAtUtc.Kind.Should().Be(DateTimeKind.Utc);
    }

    // ── Content-free by construction ─────────────────────────────────────────

    /// <summary>
    /// The stored dispute holds no learner text, and cannot.
    /// </summary>
    /// <remarks>
    /// One string member, bounded, holding a ledger identifier. Anything else would put learner
    /// prose into the protected outcome — a second copy with a second retention story and a second
    /// erasure path, for nothing the closed code does not already give.
    /// </remarks>
    [Fact]
    public void The_stored_dispute_has_exactly_one_bounded_string()
    {
        var strings = typeof(CoachTurnDisputeState)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.PropertyType == typeof(string))
            .Select(property => property.Name)
            .ToArray();

        strings.Should().BeEquivalentTo(
            [nameof(CoachTurnDisputeState.DisputedMessageId)],
            "the correction text lives in the encrypted message ledger, once");
    }

    [Fact]
    public void A_serialized_dispute_repeats_no_learner_text()
    {
        const string Correction = "No, I meant the words I looked up, not the ones in the plan.";

        var dispute = Coordinator().TryOpen(Correction, DisputedMessageId, DisputedTrace(), Now)!;

        var json = JsonSerializer.Serialize(dispute);

        json.Should().NotContain("looked up", "the learner's words never enter the protected outcome");
        json.Should().NotContain("plan,");
        json.Should().Contain("DifferentCohort", "the closed signal is what is stored");
    }

    /// <summary>The wire projection carries codes and an identifier, and nothing else.</summary>
    [Fact]
    public void The_wire_projection_is_content_free()
    {
        var dispute = Coordinator().TryOpen(
            "That's not what I asked.", DisputedMessageId, DisputedTrace(), Now)!;

        var dto = CoachDisputeProjection.Project(dispute);

        dto.Should().NotBeNull();
        dto!.Signal.Should().Be(CoachDisputeSignal.NotWhatIAsked);
        dto.Status.Should().Be(CoachDisputeStatus.Open);
        dto.DisputedMessageId.Should().Be(DisputedMessageId);

        typeof(CoachDisputeDto)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.PropertyType == typeof(string))
            .Select(property => property.Name)
            .Should().BeEquivalentTo([nameof(CoachDisputeDto.DisputedMessageId)]);
    }

    [Fact]
    public void Projecting_no_dispute_yields_null()
    {
        CoachDisputeProjection.Project(null).Should().BeNull();
    }

    /// <summary>
    /// The two vocabularies are mirrors, so a member added to one must appear in the other.
    /// </summary>
    [Fact]
    public void The_signal_and_resolution_mirrors_are_total()
    {
        foreach (var signal in Enum.GetValues<CoachCorrectionSignal>())
        {
            var wire = CoachDisputeProjection.ToWire(signal);

            if (signal != CoachCorrectionSignal.None)
            {
                wire.Should().NotBe(
                    CoachDisputeSignal.Unknown,
                    "{0} has no wire member, so a learner would see the generic notice for a "
                    + "correction the server classified precisely",
                    signal);
            }
        }

        foreach (var resolution in Enum.GetValues<CoachDisputeResolution>())
        {
            CoachDisputeProjection.ToWire(resolution).Should().NotBe(
                CoachDisputeStatus.Unknown,
                "{0} has no wire member, so the client would render nothing for a real state",
                resolution);
        }
    }

    /// <summary>Nothing under the dispute surface names a learner or an account.</summary>
    [Theory]
    [InlineData("CoachCorrectionClassifier.cs")]
    [InlineData("CoachDisputeCoordinator.cs")]
    public void The_dispute_surface_names_no_identity_field(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null &&
               !Directory.Exists(Path.Combine(directory.FullName, "src", "SentenceStudio.Api")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull();

        var path = Path.Combine(
            directory!.FullName, "src", "SentenceStudio.Api", "Coach", "Application", fileName);

        File.Exists(path).Should().BeTrue("{0} must exist for this scan to mean anything", path);

        var code = System.Text.RegularExpressions.Regex.Replace(
            File.ReadAllText(path), @"^[ \t]*///?.*$", string.Empty,
            System.Text.RegularExpressions.RegexOptions.Multiline);

        foreach (var forbidden in new[] { "UserProfileId", "TenantId", "Email", "AccountId" })
        {
            code.Should().NotContain(
                forbidden,
                "{0} must not reach identity; the dispute is keyed to a message, and the message's "
                + "ownership is already the ledger's problem",
                fileName);
        }
    }
}

/// <summary>A fixed options monitor, so a test can set the flag without a configuration host.</summary>
internal sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
{
    private readonly T _value;

    internal StaticOptionsMonitor(T value) => _value = value;

    public T CurrentValue => _value;

    public T Get(string? name) => _value;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
