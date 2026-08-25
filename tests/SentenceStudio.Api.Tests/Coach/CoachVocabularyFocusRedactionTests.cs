using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;
using SentenceStudio.Services.Plans;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// Where a learner's own words are allowed to be written down.
/// </summary>
/// <remarks>
/// <para>
/// The answer is: only inside the encrypted conversation. A vocabulary focus makes this easy to get
/// wrong, because the learner's wording is genuinely needed for one step — the controlled registry
/// reads it to pick a canonical code — and it is tempting to keep carrying it afterwards. A live
/// audit found exactly that: <c>active verbs</c> sitting in an unencrypted pending-suggestion
/// column, because the delta that was validated was also the delta that was stored.
/// </para>
/// <para>
/// After the registry has read it, nothing needs it. The canonical code, the frozen identifiers,
/// and <c>ChangedFields</c> carry every decision the server made, so the wording stops there.
/// </para>
/// </remarks>
public class CoachVocabularyFocusRedactionTests
{
    /// <summary>The exact phrase from the live audit.</summary>
    private const string Phrase = "active verbs";

    /// <summary>A phrase no other machinery could produce, so a match is proof of a leak.</summary>
    private const string Sentinel = "sentinelfocusphrase";

    // ---------------------------------------------------------------- stored columns

    [Fact]
    public async Task ThePendingColumnHoldsNoLearnerWording()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = FocusResult(Phrase);
        await AskAsync(harness, sessionId, "I want to focus today on active verbs");

        var row = harness.Db.CoachSessions.Single();
        row.PendingSuggestionDeltaJson.Should().NotBeNull();
        row.PendingSuggestionDeltaJson!.Should().NotContain(Phrase);
        row.PendingSuggestionDeltaJson.Should().NotContain("VocabularyFocusDescription\":\"");

        // What the server decided does survive, because that is what an acceptance replays.
        row.PendingSuggestionDeltaJson.Should().Contain(CoachVocabularyFocusAliases.ActionVerbCode);
        row.PendingSuggestionDeltaJson.Should().Contain("VocabularyFocus");
    }

    [Fact]
    public async Task NoColumnHoldsTheWordingAfterAFullLifecycle()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        // Offer, accept, then undo: every column this feature writes.
        harness.Coach.NextResult = FocusResult(Sentinel);
        harness.FocusResolver.NextResult = harness.FocusResolver.NextResult;

        // The registry must map it, so alias the sentinel through a phrase it knows while the
        // model still emits the sentinel-bearing description.
        harness.Coach.NextResult = FocusResult(Phrase);
        var offered = await AskAsync(harness, sessionId, $"focus on {Sentinel} please");

        await harness.Service.AcceptSuggestionAsync(
            sessionId, offered.Value!.PendingSuggestion!.SuggestionId, new CoachSuggestionDecisionRequest());

        await harness.Service.UndoAsync(sessionId, new CoachUndoRequest());

        var session = harness.Db.CoachSessions.Single();

        foreach (var column in new[]
                 {
                     session.ActiveConstraintsJson,
                     session.PendingSuggestionDeltaJson ?? string.Empty
                 })
        {
            column.Should().NotContain(Phrase);
            column.Should().NotContain(Sentinel);
        }

        foreach (var revision in harness.Db.CoachPlanRevisions.ToList())
        {
            foreach (var column in new[]
                     {
                         revision.AcceptedConstraintDeltaJson,
                         revision.BeforePlanSnapshotJson,
                         revision.AfterPlanSnapshotJson
                     })
            {
                column.Should().NotContain(Phrase, "a revision row is permanent");
                column.Should().NotContain(Sentinel);
            }
        }
    }

    [Fact]
    public async Task TheOfferOnTheWireCarriesNoWordingEither()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = FocusResult(Phrase);
        var offered = await AskAsync(harness, sessionId, "focus today on active verbs");

        offered.Value!.PendingSuggestion!.Delta.VocabularyFocusDescription.Should().BeNull();

        // The client still learns everything it needs from the projection.
        offered.Value.PendingSuggestion.VocabularyFocus!.DisplayLabel.Should().Be("action verbs");
        offered.Value.PendingSuggestion.Delta.ChangedFields
            .Should().Contain(CoachConstraintField.VocabularyFocus);
    }

    // ---------------------------------------------------------------- the focus still works

    [Fact]
    public async Task TheFocusSurvivesReloadAcceptAndUndoWithoutTheWording()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = FocusResult(Phrase);
        var offered = await AskAsync(harness, sessionId, "focus today on active verbs");

        // Reload: the redacted delta is enough to rebuild the whole offer.
        var reread = (await harness.Service.GetSessionAsync(sessionId)).Value!.PendingSuggestion!;
        reread.VocabularyFocus!.Words.Select(w => w.TargetText).Should().Equal(
            "\uAC00\uB2E4", "\uBA39\uB2E4", "\uBCF4\uB2E4", "\uD558\uB2E4", "\uC77D\uB2E4");

        var accepted = await harness.Service.AcceptSuggestionAsync(
            sessionId, offered.Value!.PendingSuggestion!.SuggestionId, new CoachSuggestionDecisionRequest());

        accepted.Value!.ChangeReceipt!.VocabularyFocus.Status
            .Should().Be(CoachVocabularyFocusStatus.Applied);
        harness.PlanService.LastApplyFocusIds.Should().Equal("v-1", "v-2", "v-3", "v-4", "v-5");
        harness.FocusResolver.ResolveCount.Should().Be(1);

        var undone = await harness.Service.UndoAsync(sessionId, new CoachUndoRequest());
        undone.Value!.ChangeReceipt!.VocabularyFocus.Status
            .Should().Be(CoachVocabularyFocusStatus.Cleared);
    }

    [Fact]
    public void RedactionKeepsEveryServerDecision()
    {
        var delta = new CoachConstraintDeltaDto
        {
            AvailableMinutes = 30,
            AudioAllowed = false,
            VocabularyFocusDescription = Phrase,
            ChangedFields = [CoachConstraintField.AvailableMinutes, CoachConstraintField.VocabularyFocus]
        };

        var redacted = delta.WithoutRawFocusText();

        redacted.VocabularyFocusDescription.Should().BeNull();
        redacted.AvailableMinutes.Should().Be(30);
        redacted.AudioAllowed.Should().BeFalse();
        redacted.ChangedFields.Should().Equal(delta.ChangedFields);
    }

    [Fact]
    public void RedactingAClearKeepsTheClearFlag()
    {
        var redacted = new CoachConstraintDeltaDto
        {
            ClearVocabularyFocus = true,
            ChangedFields = [CoachConstraintField.VocabularyFocus]
        }.WithoutRawFocusText();

        redacted.ClearVocabularyFocus.Should().BeTrue("an apply replayed from storage must still know");
        redacted.ChangedFields.Should().Equal(CoachConstraintField.VocabularyFocus);
    }

    // ---------------------------------------------------------------- legacy rows

    [Fact]
    public async Task ALegacyPendingRowWithWording_StillAcceptsAndIsRewrittenClean()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = FocusResult(Phrase);
        var offered = await AskAsync(harness, sessionId, "focus today on active verbs");

        // Put the wording back, as a row written before this fix would have it.
        var row = harness.Db.CoachSessions.Single();
        var stored = CoachPendingSuggestionEnvelope.TryRead(row.PendingSuggestionDeltaJson)!;
        row.PendingSuggestionDeltaJson = CoachNormalizedJson.Serialize(stored with
        {
            Delta = new CoachConstraintDeltaDto
            {
                VocabularyFocusDescription = Phrase,
                ChangedFields = stored.Delta.ChangedFields
            }
        });
        harness.Db.SaveChanges();
        row.PendingSuggestionDeltaJson.Should().Contain(Phrase);

        var accepted = await harness.Service.AcceptSuggestionAsync(
            sessionId, offered.Value!.PendingSuggestion!.SuggestionId, new CoachSuggestionDecisionRequest());

        accepted.Value!.ChangeReceipt.Should().NotBeNull("a legacy row still accepts");
        harness.PlanService.LastApplyFocusIds.Should().Equal("v-1", "v-2", "v-3", "v-4", "v-5");

        // And the row it wrote on the way through carries none of it.
        harness.Db.CoachPlanRevisions.Single()
            .AcceptedConstraintDeltaJson.Should().NotContain(Phrase);
    }

    // ---------------------------------------------------------------- projection hygiene

    [Fact]
    public async Task WordsAreTrimmedAndBlankGlossesBecomeNoGloss()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.FocusResolver.NextResult = harness.FocusResolver.NextResult with
        {
            Items =
            [
                Word("w-1", "  \uAC00\uB2E4  ", "  to go  "),
                Word("w-2", "\uBA39\uB2E4", "   "),
                Word("w-3", "\uBCF4\uB2E4", null)
            ]
        };

        harness.Coach.NextResult = FocusResult(Phrase);
        var offered = await AskAsync(harness, sessionId, "focus today on active verbs");

        var words = offered.Value!.PendingSuggestion!.VocabularyFocus!.Words;

        words[0].TargetText.Should().Be("\uAC00\uB2E4");
        words[0].DisplayText.Should().Be("to go");

        // A whitespace-only gloss is not a translation, and a client should not have to decide
        // whether an empty string means one.
        words[1].DisplayText.Should().BeNull();
        words[1].DisplayLanguageTag.Should().BeNull();
        words[2].DisplayText.Should().BeNull();
    }

    // ---------------------------------------------------------------- helpers

    private static VocabularyFocusItem Word(string id, string target, string? native) => new()
    {
        VocabularyWordId = id,
        TargetLanguageTerm = target,
        NativeLanguageTerm = native,
        PartOfSpeech = VocabularyPartOfSpeech.Verb,
        MatchReason = VocabularyFocusMatchReason.DueForReview
    };

    private static CoachAgentTurnResult FocusResult(string description) => new()
    {
        Outcome = CoachAgentOutcome.Completed,
        Intent = new CoachTurnIntent
        {
            Kind = CoachIntentKind.DirectConstraintChange,
            ConstraintDelta = new CoachConstraintDeltaIntent { VocabularyFocusDescription = description },
            CoachMessage = string.Empty
        }
    };

    private static Task<CoachOperationResult<CoachTurnResponse>> AskAsync(
        CoachApplicationHarness harness, string sessionId, string text) =>
        harness.Service.SubmitTurnAsync(sessionId, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = text
        });
}
