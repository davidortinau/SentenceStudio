using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;
using SentenceStudio.Services.Plans;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// "I want to focus today on active verbs."
/// </summary>
/// <remarks>
/// <para>
/// The coach may now shape a plan around a kind of word. The authority is split three ways and
/// the model holds none of it: the model repeats the learner's wording, a controlled registry
/// decides what that wording may mean, and a tenant-scoped resolver decides which of the learner's
/// own vocabulary satisfies it. The model is never told the answer.
/// </para>
/// <para>
/// The selection is then frozen. A preview, a reload, and an acceptance all replay the same
/// identifiers, so the plan the learner accepts is the plan they were shown — not a fresh
/// selection that drifted because a word came due in between.
/// </para>
/// </remarks>
public class CoachVocabularyFocusTests
{
    private const string Utterance = "I want to focus today on active verbs";

    // ---------------------------------------------------------------- the registry

    [Theory]
    [InlineData("active verbs")]
    [InlineData("action verbs")]
    [InlineData("Active Verbs")]
    [InlineData("the active verbs")]
    [InlineData("\uB3D9\uC791 \uB3D9\uC0AC")]
    [InlineData("\uD589\uB3D9 \uB3D9\uC0AC")]
    public void TheVerbPhrases_MapToOneCanonicalFocus(string phrase)
    {
        CoachVocabularyFocusAliases.TryMap(phrase, out var alias).Should().BeTrue();
        alias.FocusCode.Should().Be(CoachVocabularyFocusAliases.ActionVerbCode);
        alias.PartOfSpeech.Should().Be(VocabularyPartOfSpeech.Verb);
    }

    [Theory]
    [InlineData("active voice")]
    [InlineData("passive voice")]
    [InlineData("hard words")]
    [InlineData("things I got wrong")]
    [InlineData("")]
    [InlineData(null)]
    public void AnUnrecognizedPhrase_MapsToNothing(string? phrase)
    {
        // "Active voice" is one letter from "active verbs" and means something else entirely: a
        // grammatical voice is not a word class, so no part-of-speech filter expresses it.
        CoachVocabularyFocusAliases.TryMap(phrase, out _).Should().BeFalse();
    }

    [Fact]
    public void AnOverlongDescription_MapsToNothing()
    {
        CoachVocabularyFocusAliases.Normalize(new string('a', 81)).Should().BeNull();
        CoachVocabularyFocusAliases.Normalize("one two three four five six seven eight nine")
            .Should().BeNull();
    }

    [Fact]
    public void EveryCanonicalFocus_IsAConcretePartOfSpeech()
    {
        // Unknown and Other would resolve to "everything" or to an unusable bucket.
        CoachVocabularyFocusAliases.All.Should().OnlyContain(a =>
            a.PartOfSpeech != VocabularyPartOfSpeech.Unknown && a.PartOfSpeech != VocabularyPartOfSpeech.Other);
    }

    // ---------------------------------------------------------------- the utterance

    [Fact]
    public async Task TheUtterance_ChangesOnlyTheFocus()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        var before = (await harness.Service.GetSessionAsync(sessionId)).Value!.ActiveConstraints;

        var result = await harness.OfferAndAcceptFocusAsync(
            sessionId, FocusResult("active verbs"), Utterance);

        result.Value!.Status.Should().Be(CoachTurnStatus.Completed);
        result.Value.ChangeReceipt.Should().NotBeNull();

        // Only the focus. No redundant minutes, modalities, energy, goal tag, or a generic
        // vocabulary skill emphasis standing in for a request the fields can express exactly.
        result.Value.ChangeReceipt!.AppliedDelta.ChangedFields
            .Should().Equal(CoachConstraintField.VocabularyFocus);

        var after = result.Value.ActiveConstraints;
        after.AvailableMinutes.Should().Be(before.AvailableMinutes);
        after.AudioAllowed.Should().Be(before.AudioAllowed);
        after.SpeechAllowed.Should().Be(before.SpeechAllowed);
        after.TypingAllowed.Should().Be(before.TypingAllowed);
        after.EnergyLevel.Should().Be(before.EnergyLevel);
        after.GoalTag.Should().BeNull();
        after.SkillEmphasis.Should().BeNull();
    }

    [Fact]
    public async Task TheUtterance_SelectsVerbsThroughTheResolver()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        var result = await harness.OfferAndAcceptFocusAsync(
            sessionId, FocusResult("active verbs"), Utterance);

        // Adjectives are excluded by the resolver's filter, not by anything the coach guesses.
        harness.FocusResolver.ResolveCount.Should().Be(1);
        harness.FocusResolver.Requests[0].PartOfSpeech.Should().Be(VocabularyPartOfSpeech.Verb);
        harness.FocusResolver.Requests[0].HasFilter.Should().BeTrue();

        // The learner's wording is never used for matching.
        harness.FocusResolver.Requests[0].DisplayDescription.Should().BeNull();

        var focus = result.Value!.ActiveConstraints.VocabularyFocus;
        focus.Should().NotBeNull();
        focus!.FocusCode.Should().Be(CoachVocabularyFocusAliases.ActionVerbCode);
        focus.DisplayLabel.Should().Be("action verbs");
        focus.SelectedCount.Should().Be(5);
        focus.EligibleCount.Should().Be(12);
    }

    [Fact]
    public async Task TheSelectedWords_AreReturnedInOrderWithALanguageTag()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        var result = await harness.OfferAndAcceptFocusAsync(
            sessionId, FocusResult("active verbs"), Utterance);

        var words = result.Value!.ActiveConstraints.VocabularyFocus!.Words;
        words.Select(w => w.TargetText).Should().Equal(
            "\uAC00\uB2E4", "\uBA39\uB2E4", "\uBCF4\uB2E4", "\uD558\uB2E4", "\uC77D\uB2E4");
        words.Should().OnlyContain(w => w.TargetLanguageTag == "ko-KR");
        words[0].DisplayText.Should().Be("to go");
    }

    [Fact]
    public async Task TheResolvedIdentifiers_ReachThePreviewAndTheApply()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        await harness.OfferAndAcceptFocusAsync(
            sessionId, FocusResult("active verbs"), Utterance);

        var expected = new[] { "v-1", "v-2", "v-3", "v-4", "v-5" };
        harness.PlanService.LastPreviewFocusIds.Should().Equal(expected);
        harness.PlanService.LastApplyFocusIds.Should().Equal(expected, "the plan written is the plan shown");
    }

    // ---------------------------------------------------------------- the offer rule

    [Fact]
    public async Task TheUtterance_OffersTheSetAndWritesNothing()
    {
        // The reported behaviour, now inverted. "I want to focus today on active verbs" is an
        // imperative, and it still does not write: the learner named a category and left the
        // server to choose which ten of their words satisfy it. That choice has to be shown
        // before it is stored.
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        var versionBefore = harness.PlanService.Current.Version;

        harness.Coach.NextResult = FocusResult("active verbs");
        var result = await AskAsync(harness, sessionId, Utterance);

        result.Value!.Status.Should().Be(CoachTurnStatus.Completed);
        result.Value.ChangeReceipt.Should().BeNull();

        var pending = result.Value.PendingSuggestion;
        pending.Should().NotBeNull();
        pending!.SuggestionId.Should().NotBeNullOrEmpty();
        pending.VocabularyFocus!.Words.Select(w => w.TargetText).Should().Equal(
            "\uAC00\uB2E4", "\uBA39\uB2E4", "\uBCF4\uB2E4", "\uD558\uB2E4", "\uC77D\uB2E4");
        pending.Rationale.Should().Contain("I found 5 matching action verbs for this plan");

        harness.Db.CoachPlanRevisions.Should().BeEmpty();
        harness.PlanService.ApplyCallCount.Should().Be(0);
        harness.PlanService.Current.Version.Should().Be(versionBefore);
        harness.Db.CoachSessions.Single().RevisionCount.Should().Be(0);
    }

    [Fact]
    public async Task AcceptingTheOffer_WritesExactlyOneRevisionFromTheStoredSet()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = FocusResult("active verbs");
        var offered = await AskAsync(harness, sessionId, Utterance);

        // A different answer now, so a re-resolve would be visible.
        harness.FocusResolver.NextResult = harness.FocusResolver.NextResult with
        {
            Items = [FakeVocabularyFocusResolver.Word("z-1", "\uB2EC\uB9AC\uB2E4", "to run")]
        };

        var accepted = await harness.Service.AcceptSuggestionAsync(
            sessionId, offered.Value!.PendingSuggestion!.SuggestionId, new CoachSuggestionDecisionRequest());

        accepted.Value!.ChangeReceipt.Should().NotBeNull();
        harness.Db.CoachPlanRevisions.Should().ContainSingle();
        harness.FocusResolver.ResolveCount.Should().Be(1, "acceptance replays, it does not re-resolve");
        harness.PlanService.LastApplyFocusIds.Should().Equal("v-1", "v-2", "v-3", "v-4", "v-5");
    }

    [Fact]
    public async Task DecliningTheOffer_WritesNothing()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = FocusResult("active verbs");
        var offered = await AskAsync(harness, sessionId, Utterance);

        await harness.Service.RejectSuggestionAsync(
            sessionId, offered.Value!.PendingSuggestion!.SuggestionId, new CoachSuggestionDecisionRequest());

        harness.Db.CoachPlanRevisions.Should().BeEmpty();
        harness.PlanService.ApplyCallCount.Should().Be(0);
        (await harness.Service.GetSessionAsync(sessionId)).Value!
            .ActiveConstraints.VocabularyFocus.Should().BeNull();
    }

    [Fact]
    public async Task AcceptingTwice_WritesOnlyOnce()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = FocusResult("active verbs");
        var offered = await AskAsync(harness, sessionId, Utterance);
        var suggestionId = offered.Value!.PendingSuggestion!.SuggestionId;

        var first = await harness.Service.AcceptSuggestionAsync(
            sessionId, suggestionId, new CoachSuggestionDecisionRequest());
        var second = await harness.Service.AcceptSuggestionAsync(
            sessionId, suggestionId, new CoachSuggestionDecisionRequest());

        first.Value!.ChangeReceipt.Should().NotBeNull();
        second.IsOk.Should().BeFalse("the offer was answered by the first acceptance");
        harness.Db.CoachPlanRevisions.Should().ContainSingle();
    }

    [Fact]
    public async Task ClearingAFocus_StillAppliesDirectly()
    {
        // The exception has a boundary. Removing a focus involves no server choice, so an
        // exclusive, unambiguous clear needs no confirmation.
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        await harness.OfferAndAcceptFocusAsync(sessionId, FocusResult("active verbs"), Utterance);

        // The fake planner returns the same remainder each time, so vary it or the clear is a
        // plan no-op and writes no revision.
        harness.PlanService.NextRemainder =
        [
            FakePlanService.Item(
                $"fresh-{Guid.NewGuid():N}", SentenceStudio.Services.Progress.PlanActivityType.Writing,
                priority: 1, minutes: 4, spent: 0, completed: false)
        ];

        harness.Coach.NextResult = ClearFocusResult();
        var cleared = await AskAsync(harness, sessionId, "clear vocabulary focus");

        cleared.Value!.ChangeReceipt.Should().NotBeNull("clearing needs no confirmation");
        cleared.Value.PendingSuggestion.Should().BeNull();
        cleared.Value.ActiveConstraints.VocabularyFocus.Should().BeNull();
    }

    [Fact]
    public async Task AnExactConstraint_StillAppliesDirectly()
    {
        // And the rule is scoped to the one field that delegates a choice. Minutes name the exact
        // value that will be stored, so they keep the direct path.
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = MinutesResult(30);
        var result = await AskAsync(harness, sessionId, "make it 30 minutes");

        result.Value!.ChangeReceipt.Should().NotBeNull();
        result.Value.PendingSuggestion.Should().BeNull();
        harness.Db.CoachPlanRevisions.Should().ContainSingle();
    }

    // ---------------------------------------------------------------- no fallback

    [Fact]
    public async Task AnUnrecognizedFocus_AsksOneQuestionAndWritesNothing()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = FocusResult("active voice");
        var result = await AskAsync(harness, sessionId, "focus on active voice");

        result.Value!.ClarifyingQuestion.Should().NotBeNullOrEmpty();
        result.Value.ChangeReceipt.Should().BeNull();
        harness.FocusResolver.ResolveCount.Should().Be(0, "an unmapped phrase never reaches a query");
        AssertNoWrite(harness);
    }

    [Theory]
    [InlineData(VocabularyFocusOutcome.MetadataUnavailable)]
    [InlineData(VocabularyFocusOutcome.NoMatches)]
    [InlineData(VocabularyFocusOutcome.InsufficientMatches)]
    [InlineData(VocabularyFocusOutcome.InvalidFocus)]
    public async Task AFocusTheVocabularyCannotSatisfy_ExplainsAndWritesNothing(VocabularyFocusOutcome outcome)
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.FocusResolver.NextResult = FakeVocabularyFocusResolver.Failure(outcome, matched: 3);

        // No offer to accept: a focus the vocabulary cannot satisfy never becomes one.
        harness.Coach.NextResult = FocusResult("active verbs");
        var result = await AskAsync(harness, sessionId, Utterance);

        result.Value!.Status.Should().Be(CoachTurnStatus.Rejected);
        result.Value.Messages.Should().ContainSingle()
            .Which.Text.Should().Contain("unchanged");
        result.Value.ActiveConstraints.VocabularyFocus.Should().BeNull();
        AssertNoWrite(harness);
    }

    // ---------------------------------------------------------------- immutability

    [Fact]
    public async Task AnUnrelatedChange_KeepsTheExactFocusSet()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        await harness.OfferAndAcceptFocusAsync(
            sessionId, FocusResult("active verbs"), Utterance);

        // A later change that says nothing about vocabulary must not re-resolve: a fresh
        // resolution could return different words, which the receipt would not disclose.
        harness.Coach.NextResult = MinutesResult(20);
        var result = await AskAsync(harness, sessionId, "make it 20 minutes");

        harness.FocusResolver.ResolveCount.Should().Be(1, "the focus was already frozen");
        harness.PlanService.LastApplyFocusIds.Should().Equal("v-1", "v-2", "v-3", "v-4", "v-5");

        result.Value!.Status.Should().Be(CoachTurnStatus.Completed);

        var focus = result.Value.ActiveConstraints.VocabularyFocus;
        focus.Should().NotBeNull();
        focus!.Words.Should().HaveCount(5, "the words stay visible across an unrelated change");
        focus.FocusCode.Should().Be(CoachVocabularyFocusAliases.ActionVerbCode);
    }

    [Fact]
    public async Task ClearingTheFocus_RemovesItAndItsIdentifiers()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        await harness.OfferAndAcceptFocusAsync(
            sessionId, FocusResult("active verbs"), Utterance);

        harness.Coach.NextResult = ClearFocusResult();
        var result = await AskAsync(harness, sessionId, "no vocabulary focus today");

        result.Value!.ActiveConstraints.VocabularyFocus.Should().BeNull();
        harness.PlanService.LastApplyFocusIds.Should().BeNull();
    }

    [Fact]
    public async Task AFocusSurvivesASessionReload()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        await harness.OfferAndAcceptFocusAsync(
            sessionId, FocusResult("active verbs"), Utterance);

        var reloaded = (await harness.Service.GetSessionAsync(sessionId)).Value!.ActiveConstraints;

        reloaded.VocabularyFocus.Should().NotBeNull();
        reloaded.VocabularyFocus!.FocusCode.Should().Be(CoachVocabularyFocusAliases.ActionVerbCode);
        reloaded.VocabularyFocus.Words.Select(w => w.TargetText).Should().Equal(
            "\uAC00\uB2E4", "\uBA39\uB2E4", "\uBCF4\uB2E4", "\uD558\uB2E4", "\uC77D\uB2E4");
        harness.FocusResolver.ResolveCount.Should().Be(1, "a reload never re-resolves");
    }

    // ---------------------------------------------------------------- the projection

    [Fact]
    public void TheProjectionCarriesNoIdentifierOrSchedulingField()
    {
        var forbidden = new[] { "id", "due", "mastery", "progress", "hash", "stamp", "query", "score" };

        var names = typeof(CoachVocabularyFocusDto).GetProperties()
            .Concat(typeof(CoachVocabularyFocusWordDto).GetProperties())
            .Select(p => p.Name)
            .ToArray();

        names.Should().NotContain(n => forbidden.Any(f => n.Contains(f, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task NoVocabularyIdentifierReachesTheResponse()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        var result = await harness.OfferAndAcceptFocusAsync(
            sessionId, FocusResult("active verbs"), Utterance);

        var json = System.Text.Json.JsonSerializer.Serialize(result.Value);
        foreach (var id in new[] { "v-1", "v-2", "v-3", "v-4", "v-5" })
        {
            json.Should().NotContain(id, "identifiers stay server-side");
        }
    }

    [Fact]
    public async Task TheModelIsNeverToldWhatWasSelected()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        await harness.OfferAndAcceptFocusAsync(
            sessionId, FocusResult("active verbs"), Utterance);

        // A second turn: whatever context the coach carries forward, it holds no selected word,
        // no identifier, and no count.
        harness.Coach.NextResult = MinutesResult(20);
        await AskAsync(harness, sessionId, "make it 20 minutes");

        var context = System.Text.Json.JsonSerializer.Serialize(harness.Coach.LastRequest);
        foreach (var leaked in new[] { "v-1", "\uAC00\uB2E4", "\uBA39\uB2E4", "to go" })
        {
            context.Should().NotContain(leaked);
        }
    }

    // ---------------------------------------------------------------- helpers

    private static void AssertNoWrite(CoachApplicationHarness harness)
    {
        harness.PlanService.ApplyCallCount.Should().Be(0);
        harness.Db.CoachPlanRevisions.Should().BeEmpty();
    }

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

    private static CoachAgentTurnResult ClearFocusResult() => new()
    {
        Outcome = CoachAgentOutcome.Completed,
        Intent = new CoachTurnIntent
        {
            Kind = CoachIntentKind.DirectConstraintChange,
            ConstraintDelta = new CoachConstraintDeltaIntent { ClearVocabularyFocus = true },
            CoachMessage = string.Empty
        }
    };

    private static CoachAgentTurnResult MinutesResult(int minutes) => new()
    {
        Outcome = CoachAgentOutcome.Completed,
        Intent = new CoachTurnIntent
        {
            Kind = CoachIntentKind.DirectConstraintChange,
            ConstraintDelta = new CoachConstraintDeltaIntent { AvailableMinutes = minutes },
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
