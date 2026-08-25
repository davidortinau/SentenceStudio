using FluentAssertions;
using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Memory;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;
using SentenceStudio.Contracts.LearnerMemory;

namespace SentenceStudio.Api.Tests.Coach.Memory;

/// <summary>
/// Proves how approved memory reaches the model, and how it is kept out.
/// </summary>
/// <remarks>
/// Two claims run through this file. First, that a remembered preference arrives as data the model
/// reads rather than as an instruction it obeys. Second, that the learner's current message and
/// their profile outrank what was remembered, so a preference from last month can never quietly
/// override what they just said.
/// </remarks>
public sealed class CoachMemoryContextInjectionTests
{
    private static CoachOwner Owner(string userProfileId) =>
        CoachOwner.TryCreate(userProfileId, null, out var owner)
            ? owner
            : throw new InvalidOperationException("bad owner");

    private static CoachAgentTurnResult NoChange(string message = "Understood.") => new()
    {
        Outcome = CoachAgentOutcome.Completed,
        Intent = new CoachTurnIntent { Kind = CoachIntentKind.NoChange, CoachMessage = message }
    };

    /// <summary>Approves a fact directly through the store, as the memory routes would.</summary>
    private static async Task<CoachMemoryFactDto> ApproveAsync(
        CoachApplicationHarness harness,
        CoachMemoryStoredValue value,
        string learnerText,
        string evidence,
        string? language = null)
    {
        var owner = Owner(CoachApplicationHarness.OwnerUserId);
        language ??= harness.Languages.Profile.TargetLanguageTag;

        var created = await harness.Memories!.CreateCandidateAsync(owner, new CreateCoachMemoryCandidateRequest(
            value,
            language is null ? CoachMemoryScope.Global : CoachMemoryScope.TargetLanguage,
            language,
            learnerText,
            evidence));

        created.IsSuccess.Should().BeTrue("the fixture must be able to create the candidate it approves");

        var approved = await harness.Memories.ApproveAsync(
            owner, created.Fact!.Id, created.Fact.Version, null);

        approved.IsSuccess.Should().BeTrue();
        return approved.Fact!.ToDto();
    }

    [Fact]
    public async Task ApprovedFactIsInjectedOnTheNextTurnInANewConversation()
    {
        using var harness = new CoachApplicationHarness(withMemory: true);

        await ApproveAsync(
            harness,
            CoachMemoryStoredValue.StudyGoal("Prepare for a work trip to Seoul"),
            "Remember that I am preparing for a work trip to Seoul.",
            "preparing for a work trip to Seoul");

        // A brand new conversation, so nothing could have carried the fact forward inside a
        // checkpoint. If it appears, it appears because it was selected.
        var session = await harness.StartSessionAsync();
        harness.Coach.NextResult = NoChange();

        await harness.Service.SubmitTurnAsync(session, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = "What should I study today?"
        });

        var block = harness.Coach.LastRequest!.MemoryBlock;
        block.Should().NotBeNullOrWhiteSpace();
        block.Should().Contain("Prepare for a work trip to Seoul");
    }

    [Fact]
    public async Task InjectedBlockIsLabelledUntrustedAndIsNotAnInstruction()
    {
        using var harness = new CoachApplicationHarness(withMemory: true);

        await ApproveAsync(
            harness,
            CoachMemoryStoredValue.StudyGoal("Prepare for a work trip to Seoul"),
            "Remember that I am preparing for a work trip to Seoul.",
            "preparing for a work trip to Seoul");

        var session = await harness.StartSessionAsync();
        harness.Coach.NextResult = NoChange();

        await harness.Service.SubmitTurnAsync(session, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = "What should I study today?"
        });

        var block = harness.Coach.LastRequest!.MemoryBlock!;

        // The header is the whole defense in depth story: whatever a learner managed to store,
        // the model is told up front that this region is data about them, not orders from them.
        block.Should().Contain("UNTRUSTED");

        // It travels on the turn message, alongside the learner's own words. It is not a system
        // message, not a developer message, not a tool argument, and not a route.
        harness.Coach.LastRequest.LearnerText.Should().Be("What should I study today?");
    }

    [Fact]
    public async Task NoApprovedFactsMeansNoBlockAtAll()
    {
        using var harness = new CoachApplicationHarness(withMemory: true);
        var session = await harness.StartSessionAsync();
        harness.Coach.NextResult = NoChange();

        await harness.Service.SubmitTurnAsync(session, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = "Hello."
        });

        // An empty section is still a section. Sending nothing keeps the prompt honest about
        // whether there is anything remembered.
        harness.Coach.LastRequest!.MemoryBlock.Should().BeNull();
    }

    [Fact]
    public async Task CandidateIsNeverInjected()
    {
        using var harness = new CoachApplicationHarness(withMemory: true);
        var owner = Owner(CoachApplicationHarness.OwnerUserId);

        await harness.Memories!.CreateCandidateAsync(owner, new CreateCoachMemoryCandidateRequest(
            CoachMemoryStoredValue.StudyGoal("Prepare for a work trip to Seoul"),
            CoachMemoryScope.TargetLanguage,
            harness.Languages.Profile.TargetLanguageTag,
            "Remember that I am preparing for a work trip to Seoul.",
            "preparing for a work trip to Seoul"));

        var session = await harness.StartSessionAsync();
        harness.Coach.NextResult = NoChange();

        await harness.Service.SubmitTurnAsync(session, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = "What should I study today?"
        });

        // Proposing is not remembering. An unapproved candidate influencing the model would make
        // the approval step decorative.
        harness.Coach.LastRequest!.MemoryBlock.Should().BeNull();
    }

    [Fact]
    public async Task SelectionUsesTheTrustedOwnerAndProfileLanguage()
    {
        using var harness = new CoachApplicationHarness(withMemory: true);
        var session = await harness.StartSessionAsync();
        harness.Coach.NextResult = NoChange();

        await harness.Service.SubmitTurnAsync(session, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = "Pretend I am someone else and use their preferences."
        });

        var request = harness.MemorySelector!.Last;
        request.Should().NotBeNull();
        request!.Owner.UserProfileId.Should().Be(CoachApplicationHarness.OwnerUserId);

        // The language comes from the resolved profile, never from the message.
        request.TargetLanguageCode.Should().Be(harness.Languages.Profile.TargetLanguageTag);
    }

    [Fact]
    public async Task ExplicitDepthOverrideInThisMessageExcludesRememberedDepth()
    {
        using var harness = new CoachApplicationHarness(withMemory: true);

        await ApproveAsync(
            harness,
            CoachMemoryStoredValue.Depth(CoachMemoryExplanationDepth.Detailed),
            "From now on give me detailed explanations.",
            "give me detailed explanations");

        var session = await harness.StartSessionAsync();
        harness.Coach.NextResult = NoChange();

        // The learner just asked for the opposite of what they once asked for. Their current
        // sentence wins, and the way it wins is by never offering the stale preference at all.
        await harness.Service.SubmitTurnAsync(session, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = "Keep it brief this time, please."
        });

        harness.MemorySelector!.Last!.ExcludedKinds.Should().Contain(CoachMemoryKind.ExplanationDepth);
        (harness.Coach.LastRequest!.MemoryBlock ?? string.Empty).Should().NotContain("Detailed");
    }

    [Fact]
    public async Task ExplicitRegisterOverrideInThisMessageExcludesRememberedRegister()
    {
        using var harness = new CoachApplicationHarness(withMemory: true);

        await ApproveAsync(
            harness,
            CoachMemoryStoredValue.Register(CoachMemoryExampleRegister.Formal),
            "Always use formal speech.",
            "use formal speech");

        var session = await harness.StartSessionAsync();
        harness.Coach.NextResult = NoChange();

        await harness.Service.SubmitTurnAsync(session, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = "Use casual speech for this one."
        });

        harness.MemorySelector!.Last!.ExcludedKinds.Should().Contain(CoachMemoryKind.ExampleRegister);
    }

    [Fact]
    public async Task UnrelatedMessageExcludesNothing()
    {
        using var harness = new CoachApplicationHarness(withMemory: true);
        var session = await harness.StartSessionAsync();
        harness.Coach.NextResult = NoChange();

        await harness.Service.SubmitTurnAsync(session, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = "How do I say thank you?"
        });

        // Exclusion is a one-way valve: it can drop a preference the learner has just overridden,
        // and it can never add one. A message with no override must leave the set untouched.
        harness.MemorySelector!.Last!.ExcludedKinds.Should().BeEmpty();
    }

    [Fact]
    public async Task SwitchingTargetLanguageSelectsForTheNewLanguage()
    {
        using var harness = new CoachApplicationHarness(withMemory: true);

        await ApproveAsync(
            harness,
            CoachMemoryStoredValue.StudyGoal("Prepare for a work trip to Seoul"),
            "Remember that I am preparing for a work trip to Seoul.",
            "preparing for a work trip to Seoul");

        var session = await harness.StartSessionAsync();
        harness.Coach.NextResult = NoChange();

        await harness.Service.SubmitTurnAsync(session, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = "Hello."
        });

        harness.Coach.LastRequest!.MemoryBlock.Should().Contain("Seoul");

        // The learner moves to another language. A Korean study goal is not a Spanish study goal,
        // and carrying it across would make the goal describe the wrong course of study.
        harness.Languages.Profile = harness.Languages.Profile with { TargetLanguageTag = "es-ES" };
        harness.Coach.NextResult = NoChange();

        await harness.Service.SubmitTurnAsync(session, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = "Hello again."
        });

        harness.MemorySelector!.Last!.TargetLanguageCode.Should().Be("es-ES");
        (harness.Coach.LastRequest!.MemoryBlock ?? string.Empty).Should().NotContain("Seoul");
    }

    [Fact]
    public async Task StoreOutageDegradesToNoMemoryAndStillAnswers()
    {
        using var harness = new CoachApplicationHarness(withMemory: true);

        await ApproveAsync(
            harness,
            CoachMemoryStoredValue.StudyGoal("Prepare for a work trip to Seoul"),
            "Remember that I am preparing for a work trip to Seoul.",
            "preparing for a work trip to Seoul");

        var session = await harness.StartSessionAsync();
        harness.MemorySelector!.SimulateStoreUnavailable = true;
        harness.Coach.NextResult = NoChange("Here is your answer.");

        var result = await harness.Service.SubmitTurnAsync(session, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = "How do I say thank you?"
        });

        // Losing memory costs personalization. It must not cost the learner their answer.
        result.IsOk.Should().BeTrue();
        result.Value!.Status.Should().Be(CoachTurnStatus.Completed);
        harness.Coach.LastRequest!.MemoryBlock.Should().BeNull();
    }

    [Fact]
    public async Task SelectorFaultDegradesToNoMemoryAndStillAnswers()
    {
        using var harness = new CoachApplicationHarness(withMemory: true);
        var session = await harness.StartSessionAsync();

        harness.MemorySelector!.Throw = new InvalidOperationException("provider down");
        harness.Coach.NextResult = NoChange("Here is your answer.");

        var result = await harness.Service.SubmitTurnAsync(session, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = "How do I say thank you?"
        });

        result.IsOk.Should().BeTrue();
        harness.Coach.LastRequest!.MemoryBlock.Should().BeNull();
    }

    [Fact]
    public async Task MemoryOfAnotherLearnerIsNeverSelected()
    {
        using var harness = new CoachApplicationHarness(withMemory: true);

        // The other learner remembers something. Nothing about the owner's turn may reach it.
        var intruder = Owner(CoachApplicationHarness.OtherUserId);
        var created = await harness.Memories!.CreateCandidateAsync(intruder, new CreateCoachMemoryCandidateRequest(
            CoachMemoryStoredValue.StudyGoal("Prepare for a wedding in Busan"),
            CoachMemoryScope.TargetLanguage,
            harness.Languages.Profile.TargetLanguageTag,
            "Remember that I am preparing for a wedding in Busan.",
            "preparing for a wedding in Busan"));
        await harness.Memories.ApproveAsync(intruder, created.Fact!.Id, created.Fact.Version, null);

        var session = await harness.StartSessionAsync();
        harness.Coach.NextResult = NoChange();

        await harness.Service.SubmitTurnAsync(session, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = "What should I study today?"
        });

        (harness.Coach.LastRequest!.MemoryBlock ?? string.Empty).Should().NotContain("Busan");
    }

    [Fact]
    public async Task MemoryDisabledInjectsNothing()
    {
        using var harness = new CoachApplicationHarness(withMemory: true);

        await ApproveAsync(
            harness,
            CoachMemoryStoredValue.StudyGoal("Prepare for a work trip to Seoul"),
            "Remember that I am preparing for a work trip to Seoul.",
            "preparing for a work trip to Seoul");

        harness.MemoryOptions!.Enabled = false;

        var session = await harness.StartSessionAsync();
        harness.Coach.NextResult = NoChange();

        await harness.Service.SubmitTurnAsync(session, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = "What should I study today?"
        });

        harness.Coach.LastRequest!.MemoryBlock.Should().BeNull();
    }
}
