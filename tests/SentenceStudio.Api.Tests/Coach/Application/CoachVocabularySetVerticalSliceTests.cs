using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Application.AppOperations;
using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Tests.Coach.History;
using SentenceStudio.Api.Coach.Validation;
using SentenceStudio.Api.Tests.Coach;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;
using SentenceStudio.Data;
using SentenceStudio.Data.AppOperations;
using SentenceStudio.Contracts.AppOperation;
using SentenceStudio.Services;
using SentenceStudio.Services.Plans;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Coach.Application;

public sealed class CoachVocabularySetVerticalSliceTests : IAsyncLifetime
{
    private const string OwnerId = "coach-vocabulary-owner";
    private readonly string _connectionString =
        $"Data Source=coach-vocabulary-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Foreign Keys=True";
    private SqliteConnection _connection = null!;
    private ApplicationDbContext _db = null!;
    private IApplicationOperationContentProtector _protector = null!;
    private TestTimeProvider _time = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection(_connectionString);
        await _connection.OpenAsync();
        _db = NewContext();
        _protector = new DataProtectionApplicationOperationContentProtector(
            new EphemeralDataProtectionProvider());
        _time = new TestTimeProvider(new DateTimeOffset(2026, 9, 3, 18, 0, 0, TimeSpan.Zero));
        await _db.Database.EnsureCreatedAsync();

        _db.UserProfiles.Add(new UserProfile
        {
            Id = OwnerId,
            Name = "Vocabulary learner",
            Email = "vocabulary@example.test",
            NativeLanguage = "English",
            TargetLanguage = "Korean",
            DisplayLanguage = "en",
            CreatedAt = DateTime.UtcNow.AddDays(-10)
        });
        _db.DailyPlans.Add(new DailyPlan
        {
            Id = "today-plan",
            UserProfileId = OwnerId,
            Date = new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc),
            GeneratedAtUtc = new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc),
            Strategy = "deterministic",
            RationaleFacts = """{"reason":"existing"}""",
            FocusVocabularyFacts = """{"terms":["unchanged"]}""",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public void Direct_food_review_intent_requires_a_topic_and_no_destination_question()
    {
        var intent = new CoachTurnIntent
        {
            Kind = CoachIntentKind.NoChange,
            CoachMessage = "I prepared ten food terms. Review the complete set before saving it.",
            VocabularySet = new CoachVocabularySetIntent
            {
                Topic = "food"
            }
        };

        new CoachIntentValidator().ValidateIntent(intent).IsValid.Should().BeTrue();

        intent.VocabularySet.Topic = "";
        new CoachIntentValidator().ValidateIntent(intent).Violations
            .Should().Contain(violation => violation.Code == "vocabulary_topic_invalid");

        intent.VocabularySet.Topic = "food";
        intent.ClarifyingQuestion = "Direct review or Today's Plan?";
        new CoachIntentValidator().ValidateIntent(intent).Violations
            .Should().Contain(violation => violation.Code == "vocabulary_set_question_forbidden");
    }

    [Fact]
    public void Approval_request_carries_only_opaque_reference_and_explicit_decision()
    {
        typeof(ApproveCoachVocabularySetRequest).GetProperties()
            .Select(property => property.Name)
            .Should().BeEquivalentTo(["ProposalReference", "Decision"]);
    }

    [Fact]
    public void Household_request_has_no_implicit_plan_authority()
    {
        const string prompt = "I wnto study vocabulary about house rooms and things I'd see in a house.";
        var authority = new CoachWriteAuthority();

        authority.ClassifyPlanRouting(prompt)
            .Should().Be(CoachWriteAuthority.PlanRoutingDecision.NoPlanReference);
        authority.IsGeneralStudyRequest(prompt).Should().BeTrue();
    }

    [Fact]
    public async Task Exact_food_prompt_returns_complete_approval_set_without_plan_write()
    {
        var generator = new StubVocabularyGenerator();
        using var harness = new CoachApplicationHarness(
            vocabularySets: generator);
        harness.ValidationData.Embargoed =
        [
            new CoachEmbargoedItem(
                TargetTerm: "\uC0AC\uACFC",
                NativeTerm: "apple")
        ];
        var sessionId = await harness.StartSessionAsync();
        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent
            {
                Kind = CoachIntentKind.NoChange,
                CoachMessage = "I prepared a food vocabulary set for your approval.",
                VocabularySet = new CoachVocabularySetIntent { Topic = "food" }
            }
        };

        var result = await harness.Service.SubmitTurnAsync(sessionId, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = "start a vocabulary review activity with words about food"
        });

        result.Value!.VocabularySetProposal.Should().NotBeNull();
        result.Value.VocabularySetProposal!.Terms.Should().HaveCount(10);
        result.Value.Status.Should().Be(CoachTurnStatus.Completed);
        result.Value.ClarifyingQuestion.Should().BeNull();
        result.Value.Messages.Should().NotContain(message =>
            message.Text.Contains("replace the vocabulary", StringComparison.OrdinalIgnoreCase));
        harness.ValidationData.EmbargoQueryCount.Should().Be(
            1,
            "the generated evidence-bearing proposal is checked even though discarded model prose is not");
        generator.Calls.Should().ContainSingle().Which.Should().Be(
            new VocabularyGenerationCall("food", "ko-KR", "en-US"));
        string.Join("|", generator.Calls.SelectMany(call =>
                new[] { call.Topic, call.TargetLanguageTag, call.NativeLanguageTag }))
            .Should().NotContain("\uC0AC\uACFC")
            .And.NotContain("apple");
        harness.PlanService.ApplyCallCount.Should().Be(0);
        harness.Db.CoachPlanRevisions.Should().BeEmpty();
    }

    [Theory]
    [InlineData("\uBC25", "not-a-generated-gloss")]
    [InlineData("\uC0DD\uAC01", "bread")]
    public async Task Generated_food_term_collision_returns_no_card_and_issues_no_proposal(
        string dueTarget,
        string dueNative)
    {
        var applicationService = Service(_db, CoachApplicationHarness.OwnerUserId);
        var generator = new StubVocabularyGenerator();
        using var harness = new CoachConversationHarness(
            vocabularySets: generator,
            vocabularySetApplications: applicationService);
        harness.App.ValidationData.Embargoed =
        [
            new CoachEmbargoedItem(dueTarget, dueNative)
        ];
        var conversationId = await harness.CreateConversationAsync();
        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent
            {
                Kind = CoachIntentKind.NoChange,
                CoachMessage = "I prepared a food vocabulary set for your approval.",
                VocabularySet = new CoachVocabularySetIntent { Topic = "food" }
            }
        };

        var result = await harness.TurnAsync(
            conversationId,
            "start a vocabulary review activity with words about food");

        result.IsOk.Should().BeTrue(result.Detail);
        result.Value!.Result!.Status.Should().Be(CoachTurnStatus.Rejected);
        result.Value.Result.StopReason.Should().Be(CoachStopReason.ValidationFailed);
        result.Value.Result.VocabularySetProposal.Should().BeNull();
        string.Join(" ", result.Value.Result.Messages.Select(message => message.Text))
            .Should().NotContain(dueTarget)
            .And.NotContain(dueNative);
        generator.Calls.Should().ContainSingle();
        harness.App.ValidationData.EmbargoQueryCount.Should().Be(1);
        (await _db.ApplicationOperations.AsNoTracking().CountAsync()).Should().Be(0);
        (await _db.ApplicationOperationEvents.AsNoTracking().CountAsync()).Should().Be(0);
        (await _db.ApplicationOperationReceipts.AsNoTracking().CountAsync()).Should().Be(0);
        (await _db.LearningResources.AsNoTracking().CountAsync()).Should().Be(0);
        (await _db.VocabularyWords.AsNoTracking().CountAsync()).Should().Be(0);
        (await _db.ResourceVocabularyMappings.AsNoTracking().CountAsync()).Should().Be(0);
        harness.App.PlanService.ApplyCallCount.Should().Be(0);
        harness.Db.CoachPlanRevisions.Should().BeEmpty();
    }

    [Fact]
    public async Task Vocabulary_generator_model_context_excludes_due_and_private_learner_state()
    {
        const string dueTarget = "\uC0AC\uACFC";
        const string dueNative = "apple";
        var client = new CapturingVocabularyChatClient(GeneratedFoodJson());
        var services = new ServiceCollection()
            .AddKeyedSingleton<IChatClient>(AiTier.Fast.ToKey(), client)
            .BuildServiceProvider();
        var generator = new CoachVocabularySetGenerator(
            services,
            NullLogger<CoachVocabularySetGenerator>.Instance);

        var proposal = await generator.PrepareAsync("food", "ko", "en");

        proposal.Should().NotBeNull();
        var modelContext = string.Join(
            "\n",
            client.LastMessages!.Select(message => message.Text)
                .Append(client.LastOptions!.Instructions));
        modelContext.Should().NotContain(dueTarget)
            .And.NotContain(dueNative)
            .And.NotContain("review queue")
            .And.NotContain("progress")
            .And.NotContain("diary")
            .And.NotContain("transcript");
    }

    [Fact]
    public async Task Durable_food_proposal_survives_response_projection_and_operation_replay()
    {
        var applicationService = Service(_db, CoachApplicationHarness.OwnerUserId);
        using var harness = new CoachConversationHarness(
            vocabularySets: new StubVocabularyGenerator(),
            vocabularySetApplications: applicationService);
        var conversationId = await harness.CreateConversationAsync();
        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent
            {
                Kind = CoachIntentKind.NoChange,
                CoachMessage = "I prepared a food vocabulary set for your approval.",
                VocabularySet = new CoachVocabularySetIntent { Topic = "food" }
            }
        };

        var submitted = await harness.TurnAsync(
            conversationId,
            "start a vocabulary review activity with words about food");

        submitted.IsOk.Should().BeTrue(submitted.Detail);
        submitted.Value!.State.Should().Be(CoachTurnOperationState.Completed);
        submitted.Value.Result!.VocabularySetProposal.Should().NotBeNull();
        submitted.Value.Result.VocabularySetProposal!.Terms.Should().BeEquivalentTo(FoodTerms());
        var proposalId = submitted.Value.Result.VocabularySetProposal.ProposalId;

        var durableProposal = await _db.ApplicationOperations.AsNoTracking().SingleAsync();
        durableProposal.Id.Should().Be(proposalId);
        durableProposal.UserProfileId.Should().Be(CoachApplicationHarness.OwnerUserId);
        durableProposal.CapabilityCode.Should().Be("vocabulary.set.create");
        durableProposal.Status.Should().Be(ApplicationOperationStatus.Proposed);
        harness.App.PlanService.ApplyCallCount.Should().Be(0);
        harness.Db.CoachPlanRevisions.Should().BeEmpty();

        harness.Restart();
        var replayed = await harness.Service.GetOperationAsync(
            conversationId,
            submitted.Value.OperationId);

        replayed.IsOk.Should().BeTrue(replayed.Detail);
        replayed.Value!.Result!.VocabularySetProposal.Should().NotBeNull();
        replayed.Value.Result.VocabularySetProposal!.ProposalId.Should().Be(proposalId);
        replayed.Value.Result.VocabularySetProposal.Terms.Should().BeEquivalentTo(FoodTerms());
        var restored = await harness.Service.GetVocabularyStateAsync(conversationId);
        restored.IsOk.Should().BeTrue(restored.Detail);
        restored.Value!.PendingProposal.Should().BeEquivalentTo(
            replayed.Value.Result.VocabularySetProposal);

        var accepted = await applicationService.ApproveAsync(new ApproveCoachVocabularySetRequest
        {
            ProposalReference = proposalId,
            Decision = ApplicationOperationDecision.Accept
        });
        var replayedAcceptance = await applicationService.ApproveAsync(new ApproveCoachVocabularySetRequest
        {
            ProposalReference = proposalId,
            Decision = ApplicationOperationDecision.Accept
        });

        accepted!.ResourceId.Should().NotBeNull();
        replayedAcceptance.Should().BeEquivalentTo(accepted);
        (await _db.LearningResources.AsNoTracking().CountAsync()).Should().Be(1);
        (await _db.VocabularyWords.AsNoTracking().CountAsync()).Should().Be(10);
        (await _db.ApplicationOperations.AsNoTracking().CountAsync()).Should().Be(1);
        var executedState = await harness.Service.GetVocabularyStateAsync(conversationId);
        executedState.IsOk.Should().BeTrue(executedState.Detail);
        executedState.Value!.PendingProposal.Should().BeNull();
    }

    [Fact]
    public async Task Due_content_is_owner_scoped_during_same_turn_and_reload_projection()
    {
        var ownerAService = Service(_db, OwnerId);
        var ownerAProposal = await ownerAService.IssueAsync(Proposal());
        _db.VocabularyWords.Add(new VocabularyWord
        {
            Id = "owner-a-due-food",
            TargetLanguageTerm = "빵",
            NativeLanguageTerm = "owner-a-only-gloss",
            Lemma = "빵",
            MnemonicText = "밥",
            Language = "Korean",
            CreatedAt = _time.GetUtcNow().UtcDateTime,
            UpdatedAt = _time.GetUtcNow().UtcDateTime
        });
        _db.VocabularyProgresses.Add(new VocabularyProgress
        {
            Id = "owner-a-due-progress",
            UserId = OwnerId,
            VocabularyWordId = "owner-a-due-food",
            NextReviewDate = _time.GetUtcNow().UtcDateTime.AddMinutes(-1),
            FirstSeenAt = _time.GetUtcNow().UtcDateTime.AddDays(-1),
            LastPracticedAt = _time.GetUtcNow().UtcDateTime.AddHours(-1)
        });
        await _db.SaveChangesAsync();

        var ownerAProjection = await ownerAService.ProjectForClientAsync(ownerAProposal!);
        ownerAProjection.Proposal.Should().BeNull();
        ownerAProjection.WasWithheld.Should().BeTrue();

        var ownerBService = Service(_db, CoachApplicationHarness.OwnerUserId);
        using var harness = new CoachConversationHarness(
            vocabularySets: new StubVocabularyGenerator(),
            vocabularySetApplications: ownerBService);
        var conversationId = await harness.CreateConversationAsync();
        SetVocabularyResult(harness, "food");

        var submitted = await harness.TurnAsync(
            conversationId,
            "start a vocabulary review activity with words about food");

        submitted.IsOk.Should().BeTrue(submitted.Detail);
        var sameTurnProposal = submitted.Value!.Result!.VocabularySetProposal;
        sameTurnProposal.Should().NotBeNull();
        sameTurnProposal!.Terms.Should().BeEquivalentTo(FoodTerms());
        string.Join("|", sameTurnProposal.Terms.SelectMany(term =>
                new[] { term.TargetTerm, term.NativeTerm }))
            .Should().NotContain("owner-a-only-gloss");

        var ownerBValidation = new CoachValidationDataSource(
            _db,
            new StubScope(CoachApplicationHarness.OwnerUserId),
            new FakePlanDateContext(DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime))
            {
                UtcNow = _time.GetUtcNow().UtcDateTime
            },
            NullLogger<CoachValidationDataSource>.Instance);
        (await ownerBValidation.GetEmbargoedItemsAsync(
            CoachApplicationHarness.OwnerUserId)).Should().BeEmpty();

        harness.Restart();
        var replayed = await harness.Service.GetOperationAsync(
            conversationId,
            submitted.Value.OperationId);
        replayed.IsOk.Should().BeTrue(replayed.Detail);
        replayed.Value!.Result!.VocabularySetProposal.Should().BeEquivalentTo(sameTurnProposal);

        var restored = await harness.Service.GetVocabularyStateAsync(conversationId);
        restored.IsOk.Should().BeTrue(restored.Detail);
        restored.Value!.PendingProposal.Should().BeEquivalentTo(sameTurnProposal);
    }

    [Fact]
    public async Task Due_mnemonic_collision_blocks_projection_and_approval_without_a_write()
    {
        var applicationService = Service(_db, CoachApplicationHarness.OwnerUserId);
        using var harness = new CoachConversationHarness(
            vocabularySets: new StubVocabularyGenerator(),
            vocabularySetApplications: applicationService);
        var conversationId = await harness.CreateConversationAsync();
        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent
            {
                Kind = CoachIntentKind.NoChange,
                CoachMessage = "I prepared a food vocabulary set for your approval.",
                VocabularySet = new CoachVocabularySetIntent { Topic = "food" }
            }
        };

        var submitted = await harness.TurnAsync(
            conversationId,
            "start a vocabulary review activity with words about food");
        var proposal = submitted.Value!.Result!.VocabularySetProposal!;

        _db.VocabularyWords.Add(new VocabularyWord
        {
            Id = "newly-due-word",
            TargetLanguageTerm = "기억 단서 대상",
            NativeLanguageTerm = "unrelated mnemonic source",
            Lemma = "기억 단서",
            MnemonicText = proposal.Terms[0].TargetTerm,
            Language = "Korean",
            CreatedAt = _time.GetUtcNow().UtcDateTime,
            UpdatedAt = _time.GetUtcNow().UtcDateTime
        });
        _db.VocabularyProgresses.Add(new VocabularyProgress
        {
            Id = "newly-due-progress",
            UserId = CoachApplicationHarness.OwnerUserId,
            VocabularyWordId = "newly-due-word",
            NextReviewDate = _time.GetUtcNow().UtcDateTime.AddMinutes(-1),
            FirstSeenAt = _time.GetUtcNow().UtcDateTime.AddDays(-1),
            LastPracticedAt = _time.GetUtcNow().UtcDateTime.AddHours(-1)
        });
        await _db.SaveChangesAsync();

        harness.Restart();
        var replayed = await harness.Service.GetOperationAsync(
            conversationId,
            submitted.Value.OperationId);

        replayed.IsOk.Should().BeTrue();
        replayed.Value!.Result!.VocabularySetProposal.Should().BeNull();
        replayed.Value.Result.Status.Should().Be(CoachTurnStatus.Rejected);
        replayed.Value.Result.Limitation.Should().NotBeNull();
        var restored = await harness.Service.GetVocabularyStateAsync(conversationId);
        restored.IsOk.Should().BeTrue(restored.Detail);
        restored.Value!.PendingProposal.Should().BeNull();

        var approve = () => applicationService.ApproveAsync(new ApproveCoachVocabularySetRequest
        {
            ProposalReference = proposal.ProposalId,
            Decision = ApplicationOperationDecision.Accept
        });
        await approve.Should().ThrowAsync<ApplicationOperationConflictException>();
        (await _db.LearningResources.AsNoTracking().CountAsync()).Should().Be(0);
        (await _db.VocabularyWords.AsNoTracking().CountAsync()).Should().Be(1);
        (await _db.ApplicationOperationReceipts.AsNoTracking().CountAsync()).Should().Be(0);
        (await _db.ApplicationOperations.AsNoTracking().SingleAsync())
            .Status.Should().Be(ApplicationOperationStatus.Rejected);
    }

    [Fact]
    public async Task Terminal_newer_proposal_does_not_hide_older_active_proposal()
    {
        var applicationService = Service(_db, CoachApplicationHarness.OwnerUserId);
        using var harness = new CoachConversationHarness(
            vocabularySets: new StubVocabularyGenerator(),
            vocabularySetApplications: applicationService);
        var conversationId = await harness.CreateConversationAsync();

        SetVocabularyResult(harness, "food");
        var older = await harness.TurnAsync(conversationId, "prepare food vocabulary");
        var olderProposal = older.Value!.Result!.VocabularySetProposal!;
        harness.Time.Advance(TimeSpan.FromSeconds(1));

        SetVocabularyResult(harness, "household");
        var newer = await harness.TurnAsync(conversationId, "prepare household vocabulary");
        var newerProposal = newer.Value!.Result!.VocabularySetProposal!;
        await applicationService.ApproveAsync(Request(
            newerProposal.ProposalId,
            ApplicationOperationDecision.Reject));

        harness.Restart();
        var restored = await harness.Service.GetVocabularyStateAsync(conversationId);

        restored.IsOk.Should().BeTrue(restored.Detail);
        restored.Value!.PendingProposal.Should().BeEquivalentTo(olderProposal);

        var accepted = await applicationService.ApproveAsync(Request(
            olderProposal.ProposalId,
            ApplicationOperationDecision.Accept));
        var replay = await applicationService.ApproveAsync(Request(
            olderProposal.ProposalId,
            ApplicationOperationDecision.Accept));
        replay.Should().BeEquivalentTo(accepted);
        (await _db.LearningResources.CountAsync(resource =>
            resource.UserProfileId == CoachApplicationHarness.OwnerUserId)).Should().Be(1);
    }

    [Fact]
    public async Task Active_proposal_survives_ten_newer_ordinary_outcomes_on_fresh_circuit()
    {
        var applicationService = Service(_db, CoachApplicationHarness.OwnerUserId);
        using var harness = new CoachConversationHarness(
            vocabularySets: new StubVocabularyGenerator(),
            vocabularySetApplications: applicationService);
        var conversationId = await harness.CreateConversationAsync();

        SetVocabularyResult(harness, "food");
        var proposed = await harness.TurnAsync(conversationId, "prepare food vocabulary");
        var proposal = proposed.Value!.Result!.VocabularySetProposal!;

        for (var index = 0; index < 10; index++)
        {
            harness.Time.Advance(TimeSpan.FromSeconds(1));
            harness.Coach.NextResult = new CoachAgentTurnResult
            {
                Outcome = CoachAgentOutcome.Completed,
                Intent = new CoachTurnIntent
                {
                    Kind = CoachIntentKind.NoChange,
                    CoachMessage = $"Ordinary teaching turn {index}."
                }
            };
            var ordinary = await harness.TurnAsync(
                conversationId,
                $"ordinary learner turn {index}");
            ordinary.IsOk.Should().BeTrue(ordinary.Detail);
            ordinary.Value!.Result!.VocabularySetProposal.Should().BeNull();
        }

        harness.Restart();
        var restored = await harness.Service.GetVocabularyStateAsync(conversationId);

        restored.IsOk.Should().BeTrue(restored.Detail);
        restored.Value!.PendingProposal.Should().BeEquivalentTo(proposal);
    }

    [Fact]
    public async Task Proposal_replay_is_bound_to_its_owner_and_conversation_and_terminal_state_is_not_pending()
    {
        var applicationService = Service(_db, CoachApplicationHarness.OwnerUserId);
        using var harness = new CoachConversationHarness(
            vocabularySets: new StubVocabularyGenerator(),
            vocabularySetApplications: applicationService);
        var conversationId = await harness.CreateConversationAsync();
        var otherConversationId = await harness.CreateConversationAsync();
        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent
            {
                Kind = CoachIntentKind.NoChange,
                CoachMessage = "I prepared a food vocabulary set for your approval.",
                VocabularySet = new CoachVocabularySetIntent { Topic = "food" }
            }
        };

        var submitted = await harness.TurnAsync(
            conversationId,
            "start a vocabulary review activity with words about food");
        var proposal = submitted.Value!.Result!.VocabularySetProposal!;

        (await harness.Service.GetOperationAsync(
            otherConversationId,
            submitted.Value.OperationId)).Status.Should().Be(CoachOperationStatus.SessionNotFound);
        var otherConversationState = await harness.Service.GetVocabularyStateAsync(otherConversationId);
        otherConversationState.IsOk.Should().BeTrue(otherConversationState.Detail);
        otherConversationState.Value!.PendingProposal.Should().BeNull();
        harness.ActAs(CoachConversationHarness.OtherUserId);
        (await harness.Service.GetOperationAsync(
            conversationId,
            submitted.Value.OperationId)).Status.Should().Be(CoachOperationStatus.SessionNotFound);
        (await harness.Service.GetVocabularyStateAsync(conversationId))
            .Status.Should().Be(CoachOperationStatus.SessionNotFound);
        harness.ActAs(CoachApplicationHarness.OwnerUserId);

        await applicationService.ApproveAsync(new ApproveCoachVocabularySetRequest
        {
            ProposalReference = proposal.ProposalId,
            Decision = ApplicationOperationDecision.Reject
        });
        harness.Restart();

        var terminal = await harness.Service.GetOperationAsync(
            conversationId,
            submitted.Value.OperationId);
        terminal.IsOk.Should().BeTrue();
        terminal.Value!.Result!.VocabularySetProposal.Should().BeNull();
        var terminalState = await harness.Service.GetVocabularyStateAsync(conversationId);
        terminalState.IsOk.Should().BeTrue(terminalState.Detail);
        terminalState.Value!.PendingProposal.Should().BeNull();
    }

    [Fact]
    public async Task Exact_food_prompt_overrides_a_model_destination_question()
    {
        using var harness = new CoachApplicationHarness(
            vocabularySets: new StubVocabularyGenerator());
        var sessionId = await harness.StartSessionAsync();
        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent
            {
                Kind = CoachIntentKind.AskClarification,
                CoachMessage = "I can help with food vocabulary.",
                ClarifyingQuestion =
                    "Do you mean a direct vocabulary review, or should I replace the vocabulary in Today's Plan?",
                AcceptanceState = CoachAcceptanceState.Ambiguous
            }
        };

        var result = await harness.Service.SubmitTurnAsync(sessionId, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = "start a vocabulary review activity with words about food"
        });

        result.Value!.VocabularySetProposal.Should().NotBeNull();
        result.Value.VocabularySetProposal!.Terms.Should().HaveCount(10);
        result.Value.Status.Should().Be(CoachTurnStatus.Completed);
        result.Value.ClarifyingQuestion.Should().BeNull();
        result.Value.Messages.Should().NotContain(message =>
            message.Text.Contains("replace the vocabulary", StringComparison.OrdinalIgnoreCase));
        harness.PlanService.ApplyCallCount.Should().Be(0);
        harness.Db.CoachPlanRevisions.Should().BeEmpty();
    }

    [Fact]
    public async Task Exact_household_prompt_clarifies_destination_then_direct_choice_prepares_set()
    {
        using var harness = new CoachApplicationHarness(
            vocabularySets: new StubVocabularyGenerator());
        var sessionId = await harness.StartSessionAsync();
        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent
            {
                Kind = CoachIntentKind.AskClarification,
                CoachMessage = "I can help with household vocabulary.",
                ClarifyingQuestion =
                    "Would you like a direct vocabulary review, or do you want to replace the vocabulary in Today's Plan?",
                AcceptanceState = CoachAcceptanceState.Ambiguous
            }
        };

        var first = await harness.Service.SubmitTurnAsync(sessionId, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = "I wnto study vocabulary about house rooms and things I'd see in a house."
        });

        first.Value!.ClarifyingQuestion.Should().Contain("direct vocabulary review");
        first.Value.ClarifyingQuestion.Should().Contain("replace the vocabulary");
        first.Value.VocabularySetProposal.Should().BeNull();
        first.Value.PendingSuggestion.Should().BeNull();
        harness.PlanService.ApplyCallCount.Should().Be(0);

        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent
            {
                Kind = CoachIntentKind.NoChange,
                CoachMessage = "I prepared ten household terms for your approval.",
                VocabularySet = new CoachVocabularySetIntent { Topic = "household" }
            }
        };

        var direct = await harness.Service.SubmitTurnAsync(sessionId, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = "Direct vocabulary review."
        });

        direct.Value!.VocabularySetProposal.Should().NotBeNull();
        direct.Value.VocabularySetProposal!.Terms.Should().HaveCount(10);
        harness.PlanService.ApplyCallCount.Should().Be(0);
    }

    [Fact]
    public async Task Approval_persists_complete_owned_set_once_and_does_not_mutate_plan()
    {
        var service = Service(_db, OwnerId);
        var proposal = await service.IssueAsync(Proposal());
        var request = Request(proposal!.ProposalId, ApplicationOperationDecision.Accept);

        var before = await _db.DailyPlans.AsNoTracking().SingleAsync();
        var first = await service.ApproveAsync(request);
        var replay = await service.ApproveAsync(request);
        var after = await _db.DailyPlans.AsNoTracking().SingleAsync();

        first.Should().NotBeNull();
        replay!.ResourceId.Should().Be(first!.ResourceId);
        first.ActivityPath.Should().Be($"/vocab-quiz?resourceIds={first.ResourceId}");
        (await _db.LearningResources.CountAsync(resource => resource.UserProfileId == OwnerId))
            .Should().Be(1);
        (await _db.VocabularyWords.CountAsync()).Should().Be(10);
        (await _db.ResourceVocabularyMappings.CountAsync()).Should().Be(10);
        after.Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Fresh_proposal_approved_33_seconds_later_executes_before_its_expiry()
    {
        _time = new TestTimeProvider(new DateTimeOffset(
            2026, 9, 4, 3, 17, 35, 465, TimeSpan.Zero).AddTicks(6_700));
        var service = Service(_db, OwnerId);
        var proposal = await service.IssueAsync(Proposal());
        _time.Advance(TimeSpan.FromSeconds(33));

        var approved = await service.ApproveAsync(
            Request(proposal!.ProposalId, ApplicationOperationDecision.Accept));

        approved.Should().NotBeNull();
        approved!.Decision.Should().Be(ApplicationOperationDecision.Accept);
        var operation = await _db.ApplicationOperations.AsNoTracking().SingleAsync(
            item => item.Id == proposal.ProposalId);
        operation.Status.Should().Be(ApplicationOperationStatus.Executed);
        operation.ExpiresAtUtc.Should().BeAfter(_time.GetUtcNow().UtcDateTime.AddMinutes(14));
        (await _db.ApplicationOperationEvents.AsNoTracking()
            .AnyAsync(item =>
                item.OperationId == proposal.ProposalId
                && item.Kind == ApplicationOperationEventKind.Expired)).Should().BeFalse();
        (await _db.LearningResources.CountAsync(resource => resource.UserProfileId == OwnerId))
            .Should().Be(1);
        (await _db.VocabularyWords.CountAsync()).Should().Be(10);
    }

    [Fact]
    public async Task Decline_persists_no_vocabulary_and_prevents_later_approval()
    {
        var service = Service(_db, OwnerId);
        var proposal = await service.IssueAsync(Proposal());

        var declined = await service.ApproveAsync(
            Request(proposal!.ProposalId, ApplicationOperationDecision.Reject));
        var approve = async () => await service.ApproveAsync(
            Request(proposal.ProposalId, ApplicationOperationDecision.Accept));

        declined!.Decision.Should().Be(ApplicationOperationDecision.Reject);
        await approve.Should().ThrowAsync<ApplicationOperationConflictException>();
        (await _db.LearningResources.CountAsync()).Should().Be(0);
        (await _db.VocabularyWords.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Foreign_and_expired_proposal_references_are_rejected()
    {
        var owner = Service(_db, OwnerId);
        var proposal = await owner.IssueAsync(Proposal());
        var unknownAct = async () => await owner.ApproveAsync(
            Request("unknown-proposal", ApplicationOperationDecision.Accept));
        await unknownAct.Should().ThrowAsync<ApplicationOperationConflictException>();

        await using var foreignDb = NewContext();
        var foreign = Service(foreignDb, "different-owner");
        var foreignAct = async () => await foreign.ApproveAsync(
            Request(proposal!.ProposalId, ApplicationOperationDecision.Accept));
        await foreignAct.Should().ThrowAsync<ApplicationOperationConflictException>();

        _time.Advance(TimeSpan.FromMinutes(16));
        var expiredAct = async () => await owner.ApproveAsync(
            Request(proposal!.ProposalId, ApplicationOperationDecision.Accept));
        await expiredAct.Should().ThrowAsync<ApplicationOperationConflictException>();
        (await _db.LearningResources.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Concurrent_approval_persists_one_set_and_returns_one_stable_result()
    {
        var issuer = Service(_db, OwnerId);
        var proposal = await issuer.IssueAsync(Proposal());

        async Task<CoachVocabularySetApprovalResponse?> ApproveAsync()
        {
            await using var context = NewContext();
            return await Service(context, OwnerId).ApproveAsync(
                Request(proposal!.ProposalId, ApplicationOperationDecision.Accept));
        }

        var results = await Task.WhenAll(ApproveAsync(), ApproveAsync());

        results.Select(result => result!.ResourceId).Distinct().Should().ContainSingle();
        await using var assertDb = NewContext();
        (await assertDb.LearningResources.CountAsync(resource => resource.UserProfileId == OwnerId))
            .Should().Be(1);
        (await assertDb.VocabularyWords.CountAsync()).Should().Be(10);
        (await assertDb.ResourceVocabularyMappings.CountAsync()).Should().Be(10);
    }

    [Fact]
    public async Task Concurrent_approval_waits_beyond_legacy_retry_window_and_replays_receipt()
    {
        var issuer = Service(_db, OwnerId);
        var proposal = await issuer.IssueAsync(Proposal());
        var blocker = new LearningResourceInsertBlocker();
        await using var firstDb = NewContext(blocker);
        await using var secondDb = NewContext();
        var handlerInvocationCount = 0;
        Action countHandlerInvocation = () => Interlocked.Increment(ref handlerInvocationCount);

        var first = Service(firstDb, OwnerId, handlerInvocationObserver: countHandlerInvocation).ApproveAsync(
            Request(proposal!.ProposalId, ApplicationOperationDecision.Accept));
        await blocker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var concurrent = Service(secondDb, OwnerId, handlerInvocationObserver: countHandlerInvocation).ApproveAsync(
            Request(proposal.ProposalId, ApplicationOperationDecision.Accept));
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        concurrent.IsCompleted.Should().BeFalse();

        blocker.Release.TrySetResult();
        var firstReceipt = await first;
        var concurrentReceipt = await concurrent;

        concurrentReceipt!.ResourceId.Should().Be(firstReceipt!.ResourceId);
        concurrentReceipt.Should().BeEquivalentTo(firstReceipt);
        handlerInvocationCount.Should().Be(1);
        await using var assertDb = NewContext();
        (await assertDb.LearningResources.CountAsync(resource => resource.UserProfileId == OwnerId))
            .Should().Be(1);
        (await assertDb.VocabularyWords.CountAsync()).Should().Be(10);
        (await assertDb.ResourceVocabularyMappings.CountAsync()).Should().Be(10);
    }

    [Fact]
    public async Task Accepted_proposal_recovers_expired_lease_once_after_crash()
    {
        var service = Service(_db, OwnerId);
        var proposal = await service.IssueAsync(Proposal());
        var scope = new ApplicationOperationScope(
            OwnerId,
            ApplicationExecutionAuthority.Server);
        var store = new EfApplicationOperationStore(_db, _protector);
        var coordinator = new ApplicationOperationCoordinator(store, _protector);
        var acceptedAt = _time.GetUtcNow().UtcDateTime;
        var accepted = (await coordinator.DecideAsync(
            new ApplicationOperationDecisionCommand(
                proposal!.ProposalId,
                scope,
                ApplicationOperationDecision.Accept,
                Encoding.UTF8.GetBytes(
                    $"{proposal.ProposalId}:{ApplicationOperationDecision.Accept}"),
                ConfirmationReferenceId: null,
                ConfirmationMaterial: null,
                "abandoned-lease",
                acceptedAt.AddMinutes(1),
                acceptedAt))).Operation;
        var request = Request(proposal.ProposalId, ApplicationOperationDecision.Accept);

        var liveLeaseRetry = async () => await service.ApproveAsync(request);
        await liveLeaseRetry.Should()
            .ThrowAsync<CoachVocabularySetApplicationService.CoachVocabularySetInProgressException>();
        (await _db.LearningResources.CountAsync()).Should().Be(0);

        _time.Advance(TimeSpan.FromMinutes(2));
        var recovered = await service.ApproveAsync(request);
        var replay = await service.ApproveAsync(request);

        replay!.ResourceId.Should().Be(recovered!.ResourceId);
        (await _db.LearningResources.CountAsync(resource => resource.UserProfileId == OwnerId))
            .Should().Be(1);
        (await _db.VocabularyWords.CountAsync()).Should().Be(10);
        (await _db.ResourceVocabularyMappings.CountAsync()).Should().Be(10);
        (await _db.ApplicationOperationReceipts.CountAsync()).Should().Be(1);
        (await _db.ApplicationOperationEvents.CountAsync(
            item => item.Kind == ApplicationOperationEventKind.LeaseRecovered)).Should().Be(1);
        var persisted = await _db.ApplicationOperations.AsNoTracking().SingleAsync();
        persisted.Fence.Should().Be(accepted.Version.Fence + 1);
        persisted.AttemptCount.Should().Be(2);
    }

    [Fact]
    public async Task Missing_owner_returns_no_data_logs_warning_and_writes_nothing()
    {
        var logger = new ListLogger<CoachVocabularySetApplicationService>();
        var service = Service(_db, null, logger);

        var result = await service.ApproveAsync(
            Request("unknown", ApplicationOperationDecision.Accept));

        result.Should().BeNull();
        (await _db.LearningResources.CountAsync()).Should().Be(0);
        (await _db.VocabularyWords.CountAsync()).Should().Be(0);
        logger.Messages.Should().Contain(message => message.Contains("no active userId"));
    }

    [Fact]
    public async Task Validation_data_source_with_missing_owner_fails_closed_with_warnings()
    {
        var logger = new ListLogger<CoachValidationDataSource>();
        var dataSource = new CoachValidationDataSource(
            _db,
            new StubScope(null),
            new FakePlanDateContext(DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime))
            {
                UtcNow = _time.GetUtcNow().UtcDateTime
            },
            logger);

        (await dataSource.GetEmbargoedItemsAsync(string.Empty)).Should().BeEmpty();
        (await dataSource.GetOwnedResourceIdsAsync(string.Empty)).Should().BeEmpty();

        logger.Messages.Should().Contain(message =>
            message.Contains(nameof(CoachValidationDataSource.GetEmbargoedItemsAsync)));
        logger.Messages.Should().Contain(message =>
            message.Contains(nameof(CoachValidationDataSource.GetOwnedResourceIdsAsync)));
    }

    private static ApproveCoachVocabularySetRequest Request(
        string proposalReference,
        ApplicationOperationDecision decision) => new()
    {
        ProposalReference = proposalReference,
        Decision = decision
    };

    private static CoachVocabularySetProposal Proposal() => new()
    {
        ProposalId = "ignored-generator-id",
        Topic = "food",
        Title = "Food vocabulary",
        TargetLanguageTag = "ko",
        Terms = FoodTerms()
    };

    private static void SetVocabularyResult(CoachConversationHarness harness, string topic)
    {
        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent
            {
                Kind = CoachIntentKind.NoChange,
                CoachMessage = $"I prepared a {topic} vocabulary set for your approval.",
                VocabularySet = new CoachVocabularySetIntent { Topic = topic }
            }
        };
    }

    private ApplicationDbContext NewContext(IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connectionString);
        if (interceptor is not null)
        {
            options.AddInterceptors(interceptor);
        }

        return new ApplicationDbContext(options.Options);
    }

    private CoachVocabularySetApplicationService Service(
        ApplicationDbContext db,
        string? ownerId,
        ListLogger<CoachVocabularySetApplicationService>? logger = null,
        Action? handlerInvocationObserver = null)
    {
        var store = new EfApplicationOperationStore(db, _protector);
        var serviceLogger = logger ?? new ListLogger<CoachVocabularySetApplicationService>();
        var scope = new StubScope(ownerId);
        var validationData = new CoachValidationDataSource(
            db,
            scope,
            new FakePlanDateContext(DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime))
            {
                UtcNow = _time.GetUtcNow().UtcDateTime
            },
            NullLogger<CoachValidationDataSource>.Instance);
        var leakValidator = new CoachDueItemLeakValidator();
        return handlerInvocationObserver is null
            ? new CoachVocabularySetApplicationService(
            db,
            scope,
            new ApplicationOperationCoordinator(store, _protector),
            store,
            _protector,
            validationData,
            leakValidator,
            _time,
            serviceLogger)
            : new CoachVocabularySetApplicationService(
                db,
                scope,
                new ApplicationOperationCoordinator(store, _protector),
                store,
                _protector,
                validationData,
                leakValidator,
                _time,
                serviceLogger,
                handlerInvocationObserver);
    }

    private static IReadOnlyList<CoachVocabularyTermDto> FoodTerms() =>
    [
        Pair("밥", "rice or meal"),
        Pair("빵", "bread"),
        Pair("물", "water"),
        Pair("과일", "fruit"),
        Pair("채소", "vegetable"),
        Pair("고기", "meat"),
        Pair("생선", "fish"),
        Pair("국", "soup"),
        Pair("김치", "kimchi"),
        Pair("식당", "restaurant")
    ];

    private static CoachVocabularyTermDto Pair(string target, string native) => new()
    {
        TargetTerm = target,
        NativeTerm = native
    };

    private sealed class StubScope(string? ownerId) : IUserScopeProvider
    {
        public string UserProfileId => ownerId
            ?? throw new UnauthorizedAccessException();

        public bool TryGetUserProfileId(out string userProfileId)
        {
            userProfileId = ownerId ?? string.Empty;
            return userProfileId.Length > 0;
        }
    }

    private sealed record VocabularyGenerationCall(
        string Topic,
        string TargetLanguageTag,
        string NativeLanguageTag);

    private sealed class StubVocabularyGenerator : ICoachVocabularySetGenerator
    {
        public List<VocabularyGenerationCall> Calls { get; } = [];

        public Task<CoachVocabularySetProposal?> PrepareAsync(
            string topic,
            string targetLanguageTag,
            string nativeLanguageTag,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new VocabularyGenerationCall(topic, targetLanguageTag, nativeLanguageTag));
            return Task.FromResult<CoachVocabularySetProposal?>(new CoachVocabularySetProposal
            {
                ProposalId = $"{topic}-proposal",
                Topic = topic,
                Title = $"{topic} vocabulary",
                TargetLanguageTag = targetLanguageTag,
                Terms = FoodTerms()
            });
        }
    }

    private sealed class CapturingVocabularyChatClient(string json) : IChatClient
    {
        public IReadOnlyList<ChatMessage>? LastMessages { get; private set; }

        public ChatOptions? LastOptions { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastMessages = messages.ToArray();
            LastOptions = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private static string GeneratedFoodJson() => """
        {
          "items": [
            { "targetText": "\uBC25", "nativeMeaning": "rice or meal" },
            { "targetText": "\uBE75", "nativeMeaning": "bread" },
            { "targetText": "\uBB3C", "nativeMeaning": "water" },
            { "targetText": "\uACFC\uC77C", "nativeMeaning": "fruit" },
            { "targetText": "\uCC44\uC18C", "nativeMeaning": "vegetable" },
            { "targetText": "\uACE0\uAE30", "nativeMeaning": "meat" },
            { "targetText": "\uC0DD\uC120", "nativeMeaning": "fish" },
            { "targetText": "\uAD6D", "nativeMeaning": "soup" },
            { "targetText": "\uAE40\uCE58", "nativeMeaning": "kimchi" },
            { "targetText": "\uC2DD\uB2F9", "nativeMeaning": "restaurant" }
          ]
        }
        """;

    private sealed class LearningResourceInsertBlocker : SaveChangesInterceptor
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context?.ChangeTracker.Entries<LearningResource>()
                .Any(entry => entry.State == EntityState.Added) == true)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
