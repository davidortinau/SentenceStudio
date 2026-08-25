using FluentAssertions;
using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Memory;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;
using SentenceStudio.Contracts.LearnerMemory;

namespace SentenceStudio.Api.Tests.Coach.Memory;

/// <summary>
/// Proves the only route by which Sam may propose remembering something: the learner said so, in
/// this message, in words the server can point at.
/// </summary>
/// <remarks>
/// <para>
/// Every test here runs the whole reducer, not the gate in isolation, because the gate being
/// correct is worth nothing if the reducer can be persuaded to skip it. The model is scripted to
/// propose in each case; what differs is the learner's actual text.
/// </para>
/// <para>
/// A candidate is inert by construction. Nothing in this file asserts that a proposal changed a
/// plan, a setting, or a prompt, because a proposal must never do any of those — approval is a
/// separate action the learner takes later, on a different route.
/// </para>
/// </remarks>
public sealed class CoachMemoryProposalTests
{
    private static CoachAgentTurnResult Answer(string message, CoachMemoryProposalIntent? proposal) => new()
    {
        Outcome = CoachAgentOutcome.Completed,
        Intent = new CoachTurnIntent
        {
            // NoChange is the neutral carrier: the turn writes nothing, so anything observed
            // afterwards was caused by the proposal and not by a plan path running alongside it.
            Kind = CoachIntentKind.NoChange,
            CoachMessage = message,
            MemoryProposal = proposal
        }
    };

    private static CoachMemoryProposalIntent GoalProposal(string evidence) => new()
    {
        Kind = CoachProposedMemoryKind.PersistentStudyGoal,
        Scope = CoachProposedMemoryScope.TargetLanguage,
        StudyGoalText = "Prepare for a work trip to Seoul",
        EvidenceSpan = evidence
    };

    [Fact]
    public async Task ExplicitGoalStatementProducesCandidate()
    {
        using var harness = new CoachApplicationHarness(withMemory: true);
        var session = await harness.StartSessionAsync();

        const string learner = "Remember that I am preparing for a work trip to Seoul.";
        harness.Coach.NextResult = Answer("Noted.", GoalProposal("preparing for a work trip to Seoul"));

        var result = await harness.Service.SubmitTurnAsync(session, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = learner
        });

        result.IsOk.Should().BeTrue();
        var candidate = result.Value!.MemoryCandidate;
        candidate.Should().NotBeNull();
        candidate!.Kind.Should().Be(CoachMemoryKind.PersistentStudyGoal);

        // Candidate, not Active. Proposing is not remembering.
        candidate.Status.Should().Be(CoachMemoryStatus.Candidate);
        candidate.Value.StudyGoalText.Should().Be("Prepare for a work trip to Seoul");

        // The answer the learner asked for is still the answer they get.
        result.Value.Messages.Should().Contain(m => m.Text == "Noted.");
    }

    [Fact]
    public async Task ExplicitStyleStatementProducesDepthCandidate()
    {
        using var harness = new CoachApplicationHarness(withMemory: true);
        var session = await harness.StartSessionAsync();

        harness.Coach.NextResult = Answer("Understood.", new CoachMemoryProposalIntent
        {
            Kind = CoachProposedMemoryKind.ExplanationDepth,
            Scope = CoachProposedMemoryScope.TargetLanguage,
            ExplanationDepth = CoachProposedExplanationDepth.Detailed,
            EvidenceSpan = "from now on give me detailed explanations"
        });

        var result = await harness.Service.SubmitTurnAsync(session, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = "Please, from now on give me detailed explanations of the grammar."
        });

        result.Value!.MemoryCandidate.Should().NotBeNull();
        result.Value.MemoryCandidate!.Value.ExplanationDepth.Should().Be(CoachMemoryExplanationDepth.Detailed);
    }

    [Fact]
    public async Task ExplicitRegisterStatementProducesRegisterCandidate()
    {
        using var harness = new CoachApplicationHarness(withMemory: true);
        var session = await harness.StartSessionAsync();

        harness.Coach.NextResult = Answer("Understood.", new CoachMemoryProposalIntent
        {
            Kind = CoachProposedMemoryKind.ExampleRegister,
            Scope = CoachProposedMemoryScope.TargetLanguage,
            Register = CoachProposedRegister.Formal,
            EvidenceSpan = "always use formal speech"
        });

        var result = await harness.Service.SubmitTurnAsync(session, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = "For my examples, always use formal speech."
        });

        result.Value!.MemoryCandidate.Should().NotBeNull();
        result.Value.MemoryCandidate!.Value.ExampleRegister.Should().Be(CoachMemoryExampleRegister.Formal);
    }

    [Fact]
    public async Task NoExplicitMarkerProducesNoCandidate()
    {
        using var harness = new CoachApplicationHarness(withMemory: true);
        var session = await harness.StartSessionAsync();

        // The model proposes anyway. Inferring a durable preference from a passing remark is the
        // exact behavior this gate exists to refuse, so the model's willingness is not enough.
        harness.Coach.NextResult = Answer("Sure.", GoalProposal("a work trip to Seoul"));

        var result = await harness.Service.SubmitTurnAsync(session, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = "I have a work trip to Seoul next month, so this is useful."
        });

        result.IsOk.Should().BeTrue();
        result.Value!.MemoryCandidate.Should().BeNull();
        result.Value.Messages.Should().Contain(m => m.Text == "Sure.");

        (await harness.StoredMemoriesAsync()).Should()
            .BeEmpty("nothing may be written when no candidate was created");
    }

    [Fact]
    public async Task EvidenceSpanThatIsNotInTheLearnerMessageIsRefused()
    {
        using var harness = new CoachApplicationHarness(withMemory: true);
        var session = await harness.StartSessionAsync();

        // The marker is present, so the first gate passes. The quoted evidence is not: the model
        // invented a sentence the learner never wrote. Without this check a model could satisfy
        // "explicit" by fabricating the explicitness.
        harness.Coach.NextResult = Answer("Noted.", GoalProposal("I want to sound like a native speaker"));

        var result = await harness.Service.SubmitTurnAsync(session, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = "Remember that I am preparing for a work trip to Seoul."
        });

        result.IsOk.Should().BeTrue();
        result.Value!.MemoryCandidate.Should().BeNull();
    }

    [Fact]
    public async Task EvidenceSpanMustMatchExactlyIncludingCase()
    {
        using var harness = new CoachApplicationHarness(withMemory: true);
        var session = await harness.StartSessionAsync();

        harness.Coach.NextResult = Answer("Noted.", GoalProposal("PREPARING FOR A WORK TRIP"));

        var result = await harness.Service.SubmitTurnAsync(session, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = "Remember that I am preparing for a work trip to Seoul."
        });

        result.Value!.MemoryCandidate.Should().BeNull();
    }

    [Fact]
    public async Task ValueIsRebuiltFromTheDeclaredKindNotTheFilledBranches()
    {
        using var harness = new CoachApplicationHarness(withMemory: true);
        var session = await harness.StartSessionAsync();

        // Kind says study goal; the model also filled the style branches. A value assembled from
        // "whichever fields are non-null" would smuggle two preferences in under one approval.
        harness.Coach.NextResult = Answer("Noted.", new CoachMemoryProposalIntent
        {
            Kind = CoachProposedMemoryKind.PersistentStudyGoal,
            Scope = CoachProposedMemoryScope.TargetLanguage,
            StudyGoalText = "Prepare for a work trip to Seoul",
            ExplanationDepth = CoachProposedExplanationDepth.Detailed,
            CorrectionTiming = CoachProposedCorrectionTiming.Immediate,
            Register = CoachProposedRegister.Casual,
            EvidenceSpan = "preparing for a work trip to Seoul"
        });

        var result = await harness.Service.SubmitTurnAsync(session, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = "Remember that I am preparing for a work trip to Seoul."
        });

        var value = result.Value!.MemoryCandidate!.Value;
        value.Kind.Should().Be(CoachMemoryKind.PersistentStudyGoal);
        value.StudyGoalText.Should().NotBeNullOrWhiteSpace();
        value.ExplanationDepth.Should().BeNull();
        value.CorrectionTiming.Should().BeNull();
        value.ExampleRegister.Should().BeNull();
    }

    [Fact]
    public async Task AnswerAndCandidateArriveTogether()
    {
        using var harness = new CoachApplicationHarness(withMemory: true);
        var session = await harness.StartSessionAsync();

        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent
            {
                Kind = CoachIntentKind.PedagogicalAnswer,
                CoachMessage = "-요 is the polite ending.",
                PedagogicalAnswer = new CoachPedagogicalAnswerIntent
                {
                    Topic = CoachAnswerTopic.Usage,
                    Blocks =
                    [
                        new CoachAnswerBlockIntent
                        {
                            Kind = CoachAnswerBlockKind.Answer,
                            Spans =
                            [
                                new CoachAnswerSpanIntent
                                {
                                    Text = "-요 marks polite speech.",
                                    Language = CoachLanguageRole.Display
                                }
                            ]
                        }
                    ]
                },
                MemoryProposal = GoalProposal("preparing for a work trip to Seoul")
            }
        };

        var result = await harness.Service.SubmitTurnAsync(session, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = "What does -요 mean? Also, remember that I am preparing for a work trip to Seoul."
        });

        // The pedagogical answer is not displaced by the memory offer; both are present, and the
        // answer is what the learner actually asked for.
        result.IsOk.Should().BeTrue();
        result.Value!.Answer.Should().NotBeNull();
        result.Value.MemoryCandidate.Should().NotBeNull();
    }

    [Fact]
    public async Task PlanSuggestionAndCandidateArriveTogether()
    {
        using var harness = new CoachApplicationHarness(withMemory: true);
        var session = await harness.StartSessionAsync();

        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent
            {
                Kind = CoachIntentKind.SuggestConstraintChange,
                CoachMessage = "Want to shorten today to ten minutes?",
                ConstraintDelta = new CoachConstraintDeltaIntent { AvailableMinutes = 10 },
                MemoryProposal = GoalProposal("preparing for a work trip to Seoul")
            }
        };

        var result = await harness.Service.SubmitTurnAsync(session, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = "Make it 10 minutes. Also remember that I am preparing for a work trip to Seoul."
        });

        result.IsOk.Should().BeTrue();

        // Two independent offers on one turn. Accepting the plan suggestion must not be capable of
        // approving the memory, which is why they are different fields with different routes.
        result.Value!.PendingSuggestion.Should().NotBeNull();
        result.Value.MemoryCandidate.Should().NotBeNull();
        result.Value.MemoryCandidate!.Status.Should().Be(CoachMemoryStatus.Candidate);
    }

    [Fact]
    public async Task CandidateWritesNoPlanOrSettingOrReviewState()
    {
        using var harness = new CoachApplicationHarness(withMemory: true);
        var session = await harness.StartSessionAsync();

        var applyCallsBefore = harness.PlanService.ApplyCallCount;
        var itemsBefore = harness.PlanService.Current.Items.Count;

        harness.Coach.NextResult = Answer("Noted.", GoalProposal("preparing for a work trip to Seoul"));

        var result = await harness.Service.SubmitTurnAsync(session, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = "Remember that I am preparing for a work trip to Seoul."
        });

        result.Value!.MemoryCandidate.Should().NotBeNull();
        harness.PlanService.ApplyCallCount.Should().Be(applyCallsBefore);
        harness.PlanService.Current.Items.Count.Should().Be(itemsBefore);
        result.Value.ChangeReceipt.Should().BeNull();
    }

    [Fact]
    public async Task FailedTurnProducesNoCandidate()
    {
        using var harness = new CoachApplicationHarness(withMemory: true);
        var session = await harness.StartSessionAsync();

        // A turn that did not produce an answer is not a foundation for a durable decision.
        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Failed,
            Intent = new CoachTurnIntent
            {
                Kind = CoachIntentKind.NoChange,
                CoachMessage = "…",
                MemoryProposal = GoalProposal("preparing for a work trip to Seoul")
            }
        };

        var result = await harness.Service.SubmitTurnAsync(session, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = "Remember that I am preparing for a work trip to Seoul."
        });

        result.Value?.MemoryCandidate.Should().BeNull();
    }

    [Fact]
    public async Task MemoryDisabledProducesNoCandidate()
    {
        using var harness = new CoachApplicationHarness(withMemory: true);
        harness.MemoryOptions!.Enabled = false;

        var session = await harness.StartSessionAsync();
        harness.Coach.NextResult = Answer("Noted.", GoalProposal("preparing for a work trip to Seoul"));

        var result = await harness.Service.SubmitTurnAsync(session, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = "Remember that I am preparing for a work trip to Seoul."
        });

        result.IsOk.Should().BeTrue("the flag suppresses memory, it does not break the turn");
        result.Value!.MemoryCandidate.Should().BeNull();
    }

    // ---------------------------------------------------------------- the two vocabularies

    [Theory]
    [InlineData(CoachProposedMemoryKind.PersistentStudyGoal, 0)]
    [InlineData(CoachProposedMemoryKind.ExplanationDepth, 1)]
    [InlineData(CoachProposedMemoryKind.CorrectionTiming, 2)]
    [InlineData(CoachProposedMemoryKind.ExampleRegister, 3)]
    public void EveryProposedKindKeepsItsOrdinal(CoachProposedMemoryKind kind, int ordinal) =>
        // The model's vocabulary is pinned separately from the stored kinds so the two can be
        // changed independently. Pinning it is what makes "independently" mean something.
        ((int)kind).Should().Be(ordinal);

    [Fact]
    public void TheProposedVocabularyIsNotTheStoredVocabulary()
    {
        // Deliberately distinct types. If someone collapses them back into one, the learner-memory
        // contracts re-enter the model's reachable graph and the separation tests start failing
        // for a reason that looks unrelated to the change that caused it.
        typeof(CoachMemoryProposalIntent).GetProperty(nameof(CoachMemoryProposalIntent.Kind))!
            .PropertyType.Should().Be(typeof(CoachProposedMemoryKind));

        typeof(CoachMemoryProposalIntent).Assembly
            .GetType("SentenceStudio.Contracts.LearnerMemory.CoachMemoryKind")
            .Should().NotBeNull("the stored kind still exists")
            .And.NotBe(typeof(CoachProposedMemoryKind), "but it is not what the model emits");
    }

    [Fact]
    public async Task AProposedKindOutsideTheMappedSetProducesNoCandidate()
    {
        using var harness = new CoachApplicationHarness(withMemory: true);
        var session = await harness.StartSessionAsync();

        // A value no mapping arm names. The gate maps rather than casts, so this falls through to
        // a refusal instead of being reinterpreted as whichever stored kind shares the number.
        var proposal = GoalProposal("preparing for a work trip to Seoul");
        proposal.Kind = (CoachProposedMemoryKind)97;

        harness.Coach.NextResult = Answer("Noted.", proposal);

        var result = await harness.Service.SubmitTurnAsync(session, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = "Remember that I am preparing for a work trip to Seoul."
        });

        result.IsOk.Should().BeTrue();
        result.Value!.MemoryCandidate.Should().BeNull("an unmapped kind is refused, never guessed");
        (await harness.StoredMemoriesAsync(CoachMemoryListFilter.All)).Should().BeEmpty();
    }

    [Fact]
    public async Task AProposedScopeOutsideTheMappedSetProducesNoCandidate()
    {
        using var harness = new CoachApplicationHarness(withMemory: true);
        var session = await harness.StartSessionAsync();

        var proposal = GoalProposal("preparing for a work trip to Seoul");
        proposal.Scope = (CoachProposedMemoryScope)42;

        harness.Coach.NextResult = Answer("Noted.", proposal);

        var result = await harness.Service.SubmitTurnAsync(session, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = "Remember that I am preparing for a work trip to Seoul."
        });

        result.IsOk.Should().BeTrue();
        result.Value!.MemoryCandidate.Should().BeNull();
        (await harness.StoredMemoriesAsync(CoachMemoryListFilter.All)).Should().BeEmpty();
    }
}
