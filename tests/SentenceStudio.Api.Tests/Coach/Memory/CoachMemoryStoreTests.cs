using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Coach.Memory;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Contracts.LearnerMemory;

namespace SentenceStudio.Api.Tests.Coach.Memory;

/// <summary>
/// Behaviour of the memory store: candidate creation, approval, conflict, edit, forget, and the
/// ownership boundary that has to hold under every one of them.
/// </summary>
public sealed class CoachMemoryStoreTests
{
    // ---------------------------------------------------------------- candidates

    [Fact]
    public async Task CreateCandidate_StoresACandidateAndNotAnActiveFact()
    {
        using var harness = new CoachMemoryHarness();
        using var db = harness.NewContext();
        var store = harness.NewStore(db);

        var result = await store.CreateCandidateAsync(CoachMemorySamples.Owner(), CoachMemorySamples.Candidate());

        result.Status.Should().Be(CoachMemoryStatusCode.Success);
        result.Fact.Should().NotBeNull();
        result.Fact!.Status.Should().Be(CoachMemoryStatus.Candidate);
        result.Fact.Provenance.Should().Be(CoachMemoryProvenance.UserExplicit);
        result.Fact.ConfirmedAt.Should().BeNull();
    }

    [Fact]
    public async Task CreateCandidate_KeepsACountAndDatesButNeverTheEvidenceText()
    {
        using var harness = new CoachMemoryHarness();
        using var db = harness.NewContext();
        var store = harness.NewStore(db);

        var evidence = $"keep explanations concise {CoachMemorySamples.ValueSentinel}";
        var request = CoachMemorySamples.Candidate(
            evidence: evidence,
            message: $"I decided that you should {evidence} from now on.");

        var result = await store.CreateCandidateAsync(CoachMemorySamples.Owner(), request);

        result.Status.Should().Be(CoachMemoryStatusCode.Success);
        result.Fact!.EvidenceCount.Should().Be(1);

        // The whole row, every column, as raw text. The learner's words must not be in any of it.
        using var command = harness.NewRawCommand("SELECT * FROM \"CoachMemoryFact\"");
        using var reader = await command.ExecuteReaderAsync();
        var found = false;
        while (await reader.ReadAsync())
        {
            found = true;
            for (var i = 0; i < reader.FieldCount; i++)
            {
                reader.GetValue(i).ToString().Should().NotContain(CoachMemorySamples.ValueSentinel);
            }
        }

        found.Should().BeTrue();
    }

    [Fact]
    public async Task CreateCandidate_RefusesWhenTheEvidenceIsNotInTheLearnerMessage()
    {
        using var harness = new CoachMemoryHarness();
        using var db = harness.NewContext();
        var store = harness.NewStore(db);

        var request = new CreateCoachMemoryCandidateRequest(
            CoachMemorySamples.Depth(),
            CoachMemoryScope.TargetLanguage,
            CoachMemorySamples.Korean,
            "I would like more grammar practice.",
            "keep explanations concise",
            "conv-1",
            "msg-1");

        var result = await store.CreateCandidateAsync(CoachMemorySamples.Owner(), request);

        result.Status.Should().Be(CoachMemoryStatusCode.EvidenceMismatch);
        (await db.CoachMemoryFacts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task CreateCandidate_RefusesAnEmptyOwnerRatherThanQueryingUnfiltered()
    {
        using var harness = new CoachMemoryHarness();
        using var db = harness.NewContext();
        var store = harness.NewStore(db);

        var result = await store.CreateCandidateAsync(CoachMemorySamples.Empty(), CoachMemorySamples.Candidate());

        result.Status.Should().Be(CoachMemoryStatusCode.NoOwner);
        (await db.CoachMemoryFacts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task CreateCandidate_RequiresALanguageForALanguageScopedFact()
    {
        using var harness = new CoachMemoryHarness();
        using var db = harness.NewContext();
        var store = harness.NewStore(db);

        var request = CoachMemorySamples.Candidate(scope: CoachMemoryScope.TargetLanguage, language: null);

        var result = await store.CreateCandidateAsync(CoachMemorySamples.Owner(), request);

        result.Status.Should().Be(CoachMemoryStatusCode.InvalidRequest);
    }

    [Fact]
    public async Task CreateCandidate_RefusesALanguageOnAGlobalFact()
    {
        using var harness = new CoachMemoryHarness();
        using var db = harness.NewContext();
        var store = harness.NewStore(db);

        var request = new CreateCoachMemoryCandidateRequest(
            CoachMemorySamples.Depth(),
            CoachMemoryScope.Global,
            CoachMemorySamples.Korean,
            "please keep explanations concise",
            "keep explanations concise");

        var result = await store.CreateCandidateAsync(CoachMemorySamples.Owner(), request);

        result.Status.Should().Be(CoachMemoryStatusCode.InvalidRequest);
    }

    [Fact]
    public async Task CreateCandidate_StopsAtTheCandidateCap()
    {
        using var harness = new CoachMemoryHarness(options: new CoachMemoryOptions { Enabled = true, MaxCandidates = 2 });
        using var db = harness.NewContext();
        var store = harness.NewStore(db);
        var owner = CoachMemorySamples.Owner();

        for (var i = 0; i < 2; i++)
        {
            var ok = await store.CreateCandidateAsync(owner, CoachMemorySamples.Candidate(
                value: CoachMemorySamples.Goal($"Study goal number {i}"),
                evidence: $"study goal number {i}",
                message: $"My study goal number {i} matters."));
            ok.Status.Should().Be(CoachMemoryStatusCode.Success);
        }

        var overflow = await store.CreateCandidateAsync(owner, CoachMemorySamples.Candidate(
            value: CoachMemorySamples.Goal("One goal too many"),
            evidence: "one goal too many",
            message: "That is one goal too many for now."));

        overflow.Status.Should().Be(CoachMemoryStatusCode.LimitReached);
    }

    // ---------------------------------------------------------------- approval

    [Fact]
    public async Task Approve_MakesTheFactActiveAndConfirmed()
    {
        using var harness = new CoachMemoryHarness();
        using var db = harness.NewContext();
        var store = harness.NewStore(db);
        var owner = CoachMemorySamples.Owner();

        var candidate = await store.CreateCandidateAsync(owner, CoachMemorySamples.Candidate());
        var approved = await store.ApproveAsync(owner, candidate.Fact!.Id, candidate.Fact.Version);

        approved.Status.Should().Be(CoachMemoryStatusCode.Success);
        approved.Fact!.Status.Should().Be(CoachMemoryStatus.Active);
        approved.Fact.Provenance.Should().Be(CoachMemoryProvenance.UserConfirmed);
        approved.Fact.ConfirmedAt.Should().NotBeNull();
        harness.Notifier.Changes.Should().Contain(c => c.Change == CoachMemoryChangeKind.Approved);
    }

    [Fact]
    public async Task Approve_AcceptsATypedEditOfTheSameKind()
    {
        using var harness = new CoachMemoryHarness();
        using var db = harness.NewContext();
        var store = harness.NewStore(db);
        var owner = CoachMemorySamples.Owner();

        var candidate = await store.CreateCandidateAsync(owner, CoachMemorySamples.Candidate());
        var approved = await store.ApproveAsync(
            owner,
            candidate.Fact!.Id,
            candidate.Fact.Version,
            CoachMemorySamples.Depth(CoachMemoryExplanationDepth.Detailed));

        approved.Status.Should().Be(CoachMemoryStatusCode.Success);
        approved.Fact!.Value.ExplanationDepth.Should().Be(CoachMemoryExplanationDepth.Detailed);
    }

    [Fact]
    public async Task Approve_RefusesAnEditThatChangesTheKind()
    {
        using var harness = new CoachMemoryHarness();
        using var db = harness.NewContext();
        var store = harness.NewStore(db);
        var owner = CoachMemorySamples.Owner();

        var candidate = await store.CreateCandidateAsync(owner, CoachMemorySamples.Candidate());
        var approved = await store.ApproveAsync(
            owner,
            candidate.Fact!.Id,
            candidate.Fact.Version,
            CoachMemorySamples.Register());

        approved.Status.Should().Be(CoachMemoryStatusCode.InvalidRequest);
    }

    [Fact]
    public async Task Approve_RefusesAStaleVersion()
    {
        using var harness = new CoachMemoryHarness();
        using var db = harness.NewContext();
        var store = harness.NewStore(db);
        var owner = CoachMemorySamples.Owner();

        var candidate = await store.CreateCandidateAsync(owner, CoachMemorySamples.Candidate());
        var stale = await store.ApproveAsync(owner, candidate.Fact!.Id, candidate.Fact.Version + 7);

        stale.Status.Should().Be(CoachMemoryStatusCode.Conflict);
    }

    // ---------------------------------------------------------------- conflicts

    [Fact]
    public async Task ASecondCandidateForAnOccupiedSlotIsMarkedConflictPending()
    {
        using var harness = new CoachMemoryHarness();
        using var db = harness.NewContext();
        var store = harness.NewStore(db);
        var owner = CoachMemorySamples.Owner();

        var first = await store.CreateCandidateAsync(owner, CoachMemorySamples.Candidate());
        await store.ApproveAsync(owner, first.Fact!.Id, first.Fact.Version);

        var second = await store.CreateCandidateAsync(owner, CoachMemorySamples.Candidate(
            value: CoachMemorySamples.Depth(CoachMemoryExplanationDepth.Detailed)));

        second.Status.Should().Be(CoachMemoryStatusCode.Success);
        second.Fact!.Status.Should().Be(CoachMemoryStatus.ConflictPending);
    }

    [Fact]
    public async Task ApprovingAConflictSupersedesTheOldFactInsteadOfRacingIt()
    {
        using var harness = new CoachMemoryHarness();
        using var db = harness.NewContext();
        var store = harness.NewStore(db);
        var owner = CoachMemorySamples.Owner();

        var first = await store.CreateCandidateAsync(owner, CoachMemorySamples.Candidate());
        var firstActive = await store.ApproveAsync(owner, first.Fact!.Id, first.Fact.Version);

        var second = await store.CreateCandidateAsync(owner, CoachMemorySamples.Candidate(
            value: CoachMemorySamples.Depth(CoachMemoryExplanationDepth.Detailed)));
        var secondActive = await store.ApproveAsync(owner, second.Fact!.Id, second.Fact.Version);

        secondActive.Status.Should().Be(CoachMemoryStatusCode.Success);
        secondActive.Fact!.Status.Should().Be(CoachMemoryStatus.Active);
        secondActive.Fact.SupersedesId.Should().Be(firstActive.Fact!.Id);

        var old = await store.GetAsync(owner, firstActive.Fact.Id);
        old.Fact!.Status.Should().Be(CoachMemoryStatus.Superseded);

        // Exactly one active fact occupies the slot, which is the invariant the index enforces.
        var active = await store.ListAsync(owner, CoachMemoryListFilter.Active);
        active.Items.Count(f => f.Kind == CoachMemoryKind.ExplanationDepth).Should().Be(1);
    }

    [Fact]
    public async Task FactsForDifferentLanguagesDoNotConflict()
    {
        using var harness = new CoachMemoryHarness();
        using var db = harness.NewContext();
        var store = harness.NewStore(db);
        var owner = CoachMemorySamples.Owner();

        var korean = await store.CreateCandidateAsync(owner, CoachMemorySamples.Candidate(language: CoachMemorySamples.Korean));
        await store.ApproveAsync(owner, korean.Fact!.Id, korean.Fact.Version);

        var japanese = await store.CreateCandidateAsync(owner, CoachMemorySamples.Candidate(language: CoachMemorySamples.Japanese));

        japanese.Fact!.Status.Should().Be(CoachMemoryStatus.Candidate);

        var approved = await store.ApproveAsync(owner, japanese.Fact.Id, japanese.Fact.Version);
        approved.Status.Should().Be(CoachMemoryStatusCode.Success);
        approved.Fact!.SupersedesId.Should().BeNull();

        var active = await store.ListAsync(owner, CoachMemoryListFilter.Active);
        active.Items.Should().HaveCount(2);
    }

    // ---------------------------------------------------------------- edit / reject / forget

    [Fact]
    public async Task EditActive_ChangesTheValueAndBumpsTheVersion()
    {
        using var harness = new CoachMemoryHarness();
        using var db = harness.NewContext();
        var store = harness.NewStore(db);
        var owner = CoachMemorySamples.Owner();

        var candidate = await store.CreateCandidateAsync(owner, CoachMemorySamples.Candidate());
        var active = await store.ApproveAsync(owner, candidate.Fact!.Id, candidate.Fact.Version);

        var edited = await store.EditActiveAsync(
            owner,
            active.Fact!.Id,
            active.Fact.Version,
            CoachMemorySamples.Depth(CoachMemoryExplanationDepth.Balanced));

        edited.Status.Should().Be(CoachMemoryStatusCode.Success);
        edited.Fact!.Value.ExplanationDepth.Should().Be(CoachMemoryExplanationDepth.Balanced);
        edited.Fact.Version.Should().BeGreaterThan(active.Fact.Version);
        harness.Notifier.Changes.Should().Contain(c => c.Change == CoachMemoryChangeKind.Edited);
    }

    [Fact]
    public async Task EditActive_RefusesACandidate()
    {
        using var harness = new CoachMemoryHarness();
        using var db = harness.NewContext();
        var store = harness.NewStore(db);
        var owner = CoachMemorySamples.Owner();

        var candidate = await store.CreateCandidateAsync(owner, CoachMemorySamples.Candidate());

        var edited = await store.EditActiveAsync(
            owner,
            candidate.Fact!.Id,
            candidate.Fact.Version,
            CoachMemorySamples.Depth(CoachMemoryExplanationDepth.Balanced));

        // A candidate is not an active fact, so editing it is a state conflict rather than a bad
        // request: the caller has to approve or reject it first.
        edited.Status.Should().Be(CoachMemoryStatusCode.Conflict);
    }

    [Fact]
    public async Task Reject_RemovesTheCandidateEntirely()
    {
        using var harness = new CoachMemoryHarness();
        using var db = harness.NewContext();
        var store = harness.NewStore(db);
        var owner = CoachMemorySamples.Owner();

        var candidate = await store.CreateCandidateAsync(owner, CoachMemorySamples.Candidate());
        var status = await store.RejectAsync(owner, candidate.Fact!.Id, candidate.Fact.Version);

        status.Should().Be(CoachMemoryStatusCode.Success);
        (await db.CoachMemoryFacts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Forget_RemovesTheRowAndTellsTheCheckpointOwner()
    {
        using var harness = new CoachMemoryHarness();
        using var db = harness.NewContext();
        var store = harness.NewStore(db);
        var owner = CoachMemorySamples.Owner();

        var candidate = await store.CreateCandidateAsync(owner, CoachMemorySamples.Candidate());
        var active = await store.ApproveAsync(owner, candidate.Fact!.Id, candidate.Fact.Version);
        harness.Notifier.Clear();

        var status = await store.ForgetAsync(owner, active.Fact!.Id, active.Fact.Version);

        status.Should().Be(CoachMemoryStatusCode.Success);
        (await db.CoachMemoryFacts.CountAsync()).Should().Be(0);
        harness.Notifier.Changes.Should().ContainSingle(c => c.Change == CoachMemoryChangeKind.Forgotten);
    }

    [Fact]
    public async Task ForgetAll_RemovesEverythingForTheOwnerAndNothingElse()
    {
        using var harness = new CoachMemoryHarness();
        using var db = harness.NewContext();
        var store = harness.NewStore(db);
        var owner = CoachMemorySamples.Owner();
        var other = CoachMemorySamples.Other();

        await store.CreateCandidateAsync(owner, CoachMemorySamples.Candidate());
        await store.CreateCandidateAsync(owner, CoachMemorySamples.Candidate(value: CoachMemorySamples.Register()));
        await store.CreateCandidateAsync(other, CoachMemorySamples.Candidate());

        var result = await store.ForgetAllAsync(owner);

        result.Status.Should().Be(CoachMemoryStatusCode.Success);
        result.Forgotten.Should().Be(2);
        (await db.CoachMemoryFacts.CountAsync(f => f.UserProfileId == CoachMemorySamples.OtherUserId)).Should().Be(1);
        harness.Notifier.Changes.Should().Contain(c => c.Change == CoachMemoryChangeKind.ForgottenAll);
    }

    [Fact]
    public async Task ANotifierFailureDoesNotTurnASuccessfulForgetIntoAnError()
    {
        using var harness = new CoachMemoryHarness();
        using var db = harness.NewContext();
        var store = harness.NewStore(db);
        var owner = CoachMemorySamples.Owner();

        var candidate = await store.CreateCandidateAsync(owner, CoachMemorySamples.Candidate());
        var active = await store.ApproveAsync(owner, candidate.Fact!.Id, candidate.Fact.Version);
        harness.Notifier.Throws = true;

        var status = await store.ForgetAsync(owner, active.Fact!.Id, active.Fact.Version);

        status.Should().Be(CoachMemoryStatusCode.Success);
        (await db.CoachMemoryFacts.CountAsync()).Should().Be(0);
    }

    // ---------------------------------------------------------------- ownership

    [Fact]
    public async Task AForeignOwnerCannotRead()
    {
        using var harness = new CoachMemoryHarness();
        using var db = harness.NewContext();
        var store = harness.NewStore(db);

        var candidate = await store.CreateCandidateAsync(CoachMemorySamples.Owner(), CoachMemorySamples.Candidate());

        var stolen = await store.GetAsync(CoachMemorySamples.Other(), candidate.Fact!.Id);

        stolen.Status.Should().Be(CoachMemoryStatusCode.NotFound);
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    [InlineData("edit")]
    [InlineData("forget")]
    public async Task AForeignOwnerCannotWrite(string operation)
    {
        using var harness = new CoachMemoryHarness();
        using var db = harness.NewContext();
        var store = harness.NewStore(db);
        var owner = CoachMemorySamples.Owner();
        var intruder = CoachMemorySamples.Other();

        var candidate = await store.CreateCandidateAsync(owner, CoachMemorySamples.Candidate());
        var id = candidate.Fact!.Id;
        var version = candidate.Fact.Version;

        var status = operation switch
        {
            "approve" => (await store.ApproveAsync(intruder, id, version)).Status,
            "reject" => await store.RejectAsync(intruder, id, version),
            "edit" => (await store.EditActiveAsync(intruder, id, version, CoachMemorySamples.Depth())).Status,
            "forget" => await store.ForgetAsync(intruder, id, version),
            _ => throw new InvalidOperationException()
        };

        status.Should().Be(CoachMemoryStatusCode.NotFound);
        (await db.CoachMemoryFacts.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task ListNeverCrossesTheOwnerBoundary()
    {
        using var harness = new CoachMemoryHarness();
        using var db = harness.NewContext();
        var store = harness.NewStore(db);

        await store.CreateCandidateAsync(CoachMemorySamples.Owner(), CoachMemorySamples.Candidate());
        await store.CreateCandidateAsync(CoachMemorySamples.Other(), CoachMemorySamples.Candidate());

        var page = await store.ListAsync(CoachMemorySamples.Owner(), CoachMemoryListFilter.All);

        page.Items.Should().HaveCount(1);
    }

    [Fact]
    public async Task AnEmptyOwnerReadsNothingRatherThanEverything()
    {
        using var harness = new CoachMemoryHarness();
        using var db = harness.NewContext();
        var store = harness.NewStore(db);

        await store.CreateCandidateAsync(CoachMemorySamples.Owner(), CoachMemorySamples.Candidate());
        await store.CreateCandidateAsync(CoachMemorySamples.Other(), CoachMemorySamples.Candidate());

        var page = await store.ListAsync(CoachMemorySamples.Empty(), CoachMemoryListFilter.All);

        page.Status.Should().Be(CoachMemoryStatusCode.NoOwner);
        page.Items.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- paging

    [Fact]
    public async Task ListPagesThroughEverythingExactlyOnce()
    {
        using var harness = new CoachMemoryHarness(options: new CoachMemoryOptions { Enabled = true, MaxCandidates = 16 });
        using var db = harness.NewContext();
        var store = harness.NewStore(db);
        var owner = CoachMemorySamples.Owner();

        for (var i = 0; i < 6; i++)
        {
            harness.Time.Advance(TimeSpan.FromMinutes(1));
            await store.CreateCandidateAsync(owner, CoachMemorySamples.Candidate(
                value: CoachMemorySamples.Goal($"Study goal number {i}"),
                evidence: $"study goal number {i}",
                message: $"My study goal number {i} matters."));
        }

        var seen = new List<string>();
        string? cursor = null;

        do
        {
            var page = await store.ListAsync(owner, CoachMemoryListFilter.All, pageSize: 2, cursor: cursor);
            page.Status.Should().Be(CoachMemoryStatusCode.Success);
            seen.AddRange(page.Items.Select(i => i.Id));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        seen.Should().HaveCount(6);
        seen.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task AForeignCursorIsRefusedRatherThanHonoured()
    {
        using var harness = new CoachMemoryHarness();
        using var db = harness.NewContext();
        var store = harness.NewStore(db);
        var owner = CoachMemorySamples.Owner();

        for (var i = 0; i < 3; i++)
        {
            harness.Time.Advance(TimeSpan.FromMinutes(1));
            await store.CreateCandidateAsync(owner, CoachMemorySamples.Candidate(
                value: CoachMemorySamples.Goal($"Study goal number {i}"),
                evidence: $"study goal number {i}",
                message: $"My study goal number {i} matters."));
        }

        var first = await store.ListAsync(owner, CoachMemoryListFilter.All, pageSize: 1);
        first.NextCursor.Should().NotBeNull();

        var stolen = await store.ListAsync(CoachMemorySamples.Other(), CoachMemoryListFilter.All, pageSize: 1, cursor: first.NextCursor);

        stolen.Status.Should().Be(CoachMemoryStatusCode.InvalidCursor);
    }

    // ---------------------------------------------------------------- feature flag

    [Fact]
    public async Task EveryWriteIsRefusedWhenTheFeatureIsOff()
    {
        using var harness = new CoachMemoryHarness(options: new CoachMemoryOptions { Enabled = false });
        using var db = harness.NewContext();
        var store = harness.NewStore(db);
        var owner = CoachMemorySamples.Owner();

        (await store.CreateCandidateAsync(owner, CoachMemorySamples.Candidate())).Status
            .Should().Be(CoachMemoryStatusCode.Disabled);
        (await store.ApproveAsync(owner, "fact-1", 1)).Status.Should().Be(CoachMemoryStatusCode.Disabled);
        (await store.RejectAsync(owner, "fact-1", 1)).Should().Be(CoachMemoryStatusCode.Disabled);
        (await store.EditActiveAsync(owner, "fact-1", 1, CoachMemorySamples.Depth())).Status
            .Should().Be(CoachMemoryStatusCode.Disabled);
        (await store.ForgetAsync(owner, "fact-1", 1)).Should().Be(CoachMemoryStatusCode.Disabled);
        (await store.ListAsync(owner, CoachMemoryListFilter.All)).Status.Should().Be(CoachMemoryStatusCode.Disabled);
        (await store.ListEligibleForContextAsync(owner)).Should().BeEmpty();
    }

    [Fact]
    public async Task DeletionStillWorksWhenTheFeatureIsOff()
    {
        // A flag that was on last month can have left rows behind. "Forget me" is not conditional.
        using var harness = new CoachMemoryHarness();
        using var db = harness.NewContext();
        var owner = CoachMemorySamples.Owner();

        var enabled = harness.NewStore(db);
        await enabled.CreateCandidateAsync(owner, CoachMemorySamples.Candidate());

        harness.Options.Enabled = false;
        var disabled = harness.NewStore(db);

        (await disabled.DeleteAllForOwnerAsync(owner)).Should().Be(1);
        (await db.CoachMemoryFacts.CountAsync()).Should().Be(0);
    }
}
