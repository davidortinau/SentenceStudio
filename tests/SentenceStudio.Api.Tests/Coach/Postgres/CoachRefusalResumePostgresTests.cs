using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Application.History;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Coach.Validation.Claims;
using SentenceStudio.Api.Tests.Coach.History;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Postgres;

/// <summary>
/// A refusal surviving a reload, and — just as importantly — not surviving one it should not.
/// </summary>
/// <remarks>
/// <para>
/// <b>The latest completed turn governs, and only the latest.</b> The lookback is one row, so
/// "do not scan backwards past a later null looking for an older refusal" is a property of the
/// query rather than a rule inside a loop. A learner who was refused and then asked something
/// ordinary is no longer being refused, and surfacing the older limitation would tell them the
/// coach is still withholding an answer it has since given.
/// </para>
/// <para>
/// Real PostgreSQL, because the payload is protected and the failure this catches — an outcome that
/// round-trips in memory and cannot be decrypted through the real column — only exists here.
/// </para>
/// </remarks>
public sealed class CoachRefusalResumePostgresTests : IAsyncLifetime
{
    private CoachPostgresHarness _harness = null!;
    private string _conversationId = null!;

    public async Task InitializeAsync()
    {
        if (CoachPostgresServer.SkipReason is not null)
        {
            return;
        }

        _harness = await CoachPostgresHarness.CreateAsync("refusal-resume");
        _conversationId = await NewConversationAsync(CoachHistorySamples.Owner);
    }

    public async Task DisposeAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }

    // ────────────────────────────────────────────────────────── restore

    [PostgresFact]
    public async Task A_refusal_survives_an_immediate_reload()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        await CompleteAsync(operations, "idem-refused", Outcome(Refusal()));

        var restored = await ReadLatestLimitationAsync(operations, CoachHistorySamples.Owner, _conversationId);

        restored.Should().NotBeNull(
            "a learner who reloads mid-refusal must not be told the coach has forgotten it");
        restored!.Code.Should().Be(CoachLimitationCode.UnverifiedClaimWithheld);
        restored.Coverage.Should().Be(CoachEvidenceCoverage.PageOfOwnedSet);
        restored.Destination!.Route.Should().Be(CoachRouteName.Vocabulary);
    }

    [PostgresFact]
    public async Task A_later_normal_turn_clears_the_refusal_on_reload()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        await CompleteAsync(operations, "idem-refused-first", Outcome(Refusal()));
        await CompleteAsync(operations, "idem-normal-second", Outcome(limitation: null));

        var restored = await ReadLatestLimitationAsync(operations, CoachHistorySamples.Owner, _conversationId);

        restored.Should().BeNull(
            "the learner is no longer being refused, and showing the older limitation would claim "
            + "the coach is still withholding an answer it has since given");
    }

    [PostgresFact]
    public async Task An_unreadable_latest_outcome_fails_closed_rather_than_revealing_an_older_refusal()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        await CompleteAsync(operations, "idem-refused-old", Outcome(Refusal()));

        // The newest turn's payload is not JSON this build can read. Falling back to the older row
        // would let a payload failure resurrect state the learner had already moved past.
        await CompleteAsync(operations, "idem-corrupt-new", "{ not json at all");

        var restored = await ReadLatestLimitationAsync(operations, CoachHistorySamples.Owner, _conversationId);

        restored.Should().BeNull("unreadable is null, never a search for something older");
    }

    [PostgresFact]
    public async Task An_unknown_schema_version_also_fails_closed()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        await CompleteAsync(operations, "idem-refused-known", Outcome(Refusal()));
        await CompleteAsync(operations, "idem-future-version", Outcome(Refusal()), schemaVersion: 99);

        (await ReadLatestLimitationAsync(operations, CoachHistorySamples.Owner, _conversationId))
            .Should().BeNull("a version this build cannot read is absent, not a reason to look back");
    }

    [PostgresFact]
    public async Task A_version_one_row_restores_nothing()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        // Pre-W9: the answer was stored at the root and carried no limitation member at all.
        await CompleteAsync(
            operations,
            "idem-legacy",
            System.Text.Json.JsonSerializer.Serialize(Answer(limitation: null), OutcomeJson),
            schemaVersion: 1);

        (await ReadLatestLimitationAsync(operations, CoachHistorySamples.Owner, _conversationId))
            .Should().BeNull();
    }

    [PostgresFact]
    public async Task The_stored_limitation_round_trips_exactly_at_version_three()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        var operationId = await CompleteAsync(operations, "idem-exact", Outcome(Refusal()));

        var outcome = await operations.GetOutcomeAsync(CoachHistorySamples.Owner, operationId);
        outcome!.SchemaVersion.Should().Be(3);
        outcome.IsReadable.Should().BeTrue();

        var stored = CoachConversationService.ReadOutcome(outcome.Payload, outcome.SchemaVersion);
        var limitation = stored!.Answer!.Limitation!;

        limitation.Code.Should().Be(CoachLimitationCode.UnverifiedClaimWithheld);
        limitation.AsOfUtc.Should().Be(new DateTime(2026, 8, 22, 7, 0, 0, DateTimeKind.Utc));
        limitation.AffectedCount.Should().Be(14);
        limitation.Destination!.SideEffect.Should().Be(CoachRouteSideEffect.EditsLearnerData);
        limitation.HintLadder.Should().BeEmpty();
        limitation.Alternatives.Should().BeEmpty();
    }

    // ──────────────────────────────────────────────────────── isolation

    [PostgresFact]
    public async Task A_refusal_does_not_cross_between_two_conversations_of_one_learner()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        await CompleteAsync(operations, "idem-thread-a", Outcome(Refusal()));

        var second = await NewConversationAsync(CoachHistorySamples.Owner);

        (await ReadLatestLimitationAsync(operations, CoachHistorySamples.Owner, second))
            .Should().BeNull("a refusal in one thread says nothing about another");

        (await ReadLatestLimitationAsync(operations, CoachHistorySamples.Owner, _conversationId))
            .Should().NotBeNull("and the original still restores, so the check is not vacuous");
    }

    [PostgresFact]
    public async Task A_refusal_does_not_cross_between_two_learners()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        await CompleteAsync(operations, "idem-owner-scope", Outcome(Refusal()));

        // Same conversation id, wrong owner. The owned set does the filtering, so this is not a
        // predicate the caller could have forgotten.
        (await ReadLatestLimitationAsync(operations, CoachHistorySamples.Intruder, _conversationId))
            .Should().BeNull();

        (await ReadLatestLimitationAsync(operations, CoachHistorySamples.Owner, _conversationId))
            .Should().NotBeNull();
    }

    [PostgresFact]
    public async Task An_unscoped_read_restores_nothing()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        await CompleteAsync(operations, "idem-unscoped", Outcome(Refusal()));

        (await ReadLatestLimitationAsync(operations, default, _conversationId)).Should().BeNull();
        (await ReadLatestLimitationAsync(operations, CoachHistorySamples.Owner, "")).Should().BeNull();
    }

    // ────────────────────────────────────────────── repair disclosure resume

    [PostgresFact]
    public async Task A_repair_disclosure_survives_an_immediate_reload()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        await CompleteAsync(operations, "idem-altered", Outcome(null, CoachRepairDisclosure.AnswerAltered));

        var restored = await ReadLatestAnswerAsync(operations, CoachHistorySamples.Owner, _conversationId);

        restored?.RepairDisclosure.Should().Be(CoachRepairDisclosure.AnswerAltered,
            "a learner told their answer was rewritten must still be told so after a reload");
    }

    [PostgresFact]
    public async Task A_later_clean_turn_clears_the_disclosure_on_reload()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        await CompleteAsync(operations, "idem-altered-first", Outcome(null, CoachRepairDisclosure.AnswerAltered));
        await CompleteAsync(operations, "idem-clean-second", Outcome(null, null));

        var restored = await ReadLatestAnswerAsync(operations, CoachHistorySamples.Owner, _conversationId);

        restored!.RepairDisclosure.Should().BeNull(
            "telling a learner their newest answer was rewritten, when an older one was, is a lie "
            + "about the text in front of them");
    }

    [PostgresFact]
    public async Task An_unreadable_latest_outcome_does_not_resurrect_an_older_disclosure()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        await CompleteAsync(operations, "idem-altered-old", Outcome(null, CoachRepairDisclosure.AnswerAltered));
        await CompleteAsync(operations, "idem-corrupt-newest", "{ not json at all");

        var restored = await ReadLatestAnswerAsync(operations, CoachHistorySamples.Owner, _conversationId);

        restored.Should().BeNull("unreadable is null for both facts, never a search for something older");
    }

    [PostgresFact]
    public async Task A_refusal_and_a_disclosure_never_ride_the_same_turn()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        await CompleteAsync(operations, "idem-refusal-only", Outcome(Refusal(), null));

        var restored = await ReadLatestAnswerAsync(operations, CoachHistorySamples.Owner, _conversationId);

        restored!.Limitation.Should().NotBeNull();
        restored.RepairDisclosure.Should().BeNull(
            "a refused turn shipped no answer, so there is nothing for a disclosure to be about");
    }

    [PostgresFact]
    public async Task Two_conversations_for_one_owner_restore_their_own_disclosure()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        var other = await NewConversationAsync(CoachHistorySamples.Owner);

        await CompleteAsync(operations, "idem-conv-a", Outcome(null, CoachRepairDisclosure.AnswerAltered));
        await CompleteAsync(operations, "idem-conv-b",
            Outcome(null, CoachRepairDisclosure.RepairSuppressedForLanguage),
            conversationId: other);

        (await ReadLatestAnswerAsync(operations, CoachHistorySamples.Owner, _conversationId))!
            .RepairDisclosure.Should().Be(CoachRepairDisclosure.AnswerAltered);

        (await ReadLatestAnswerAsync(operations, CoachHistorySamples.Owner, other))!
            .RepairDisclosure.Should().Be(CoachRepairDisclosure.RepairSuppressedForLanguage,
                "one conversation's rewrite is not another's");
    }

    [PostgresFact]
    public async Task Two_owners_each_restore_only_their_own_disclosure()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        var stranger = CoachHistorySamples.Intruder;
        var strangerConversation = await NewConversationAsync(stranger);

        await CompleteAsync(operations, "idem-mine", Outcome(null, CoachRepairDisclosure.AnswerAltered));
        await CompleteAsync(operations, "idem-theirs",
            Outcome(null, CoachRepairDisclosure.RepairSuppressedForLanguage),
            owner: stranger, conversationId: strangerConversation);

        // Both rows exist and both are readable — by the right owner. This is stronger than
        // asserting the stranger gets null, which an empty table would also satisfy.
        (await ReadLatestAnswerAsync(operations, CoachHistorySamples.Owner, _conversationId))!
            .RepairDisclosure.Should().Be(CoachRepairDisclosure.AnswerAltered);

        (await ReadLatestAnswerAsync(operations, stranger, strangerConversation))!
            .RepairDisclosure.Should().Be(CoachRepairDisclosure.RepairSuppressedForLanguage);

        (await ReadLatestAnswerAsync(operations, stranger, _conversationId))
            .Should().BeNull("a conversation the stranger does not own returns nothing at all");
    }

    [PostgresFact]
    public async Task A_version_one_row_restores_no_disclosure()
    {
        await using var db = _harness.NewContext();
        var operations = _harness.NewTurnOperationStore(db);

        // v1 is a bare CoachTurnResponse at the root, written before the property existed.
        await CompleteAsync(operations, "idem-v1-disclosure",
            System.Text.Json.JsonSerializer.Serialize(Answer(null, null), OutcomeJson), schemaVersion: 1);

        var restored = await ReadLatestAnswerAsync(operations, CoachHistorySamples.Owner, _conversationId);

        restored.Should().NotBeNull("a v1 row still reads: the answer survives");
        restored!.RepairDisclosure.Should().BeNull("but it carries no disclosure, because none was written");
    }

    // ───────────────────────────────────────────────────── call-site guards

    [PostgresFact]
    public async Task The_session_read_is_the_only_caller_and_the_lookback_is_one()
    {
        await Task.CompletedTask;

        var service = await File.ReadAllTextAsync(Path.Combine(
            RepositoryRoot(), "src", "SentenceStudio.Api", "Coach", "Application",
            "CoachSessionService.cs"));

        var code = string.Join('\n', service.Split('\n').Select(line =>
        {
            var comment = line.IndexOf("//", StringComparison.Ordinal);
            return comment >= 0 ? line[..comment] : line;
        }));

        Count(code, "LoadRestorableLimitationAsync(").Should().Be(
            2, "one declaration and exactly one call, on the session read");

        code.Should().Contain(
            "GetRecentOutcomesAsync(owner, conversationId, limit: 1, cancellationToken)",
            "a lookback of one is what makes 'never scan past a later null' structural rather than "
            + "a rule inside a loop somebody could edit");

        // Both restorable facts come off the one row the loader returned, so they can never
        // describe different turns. Two separate reads could interleave with a new turn and pair
        // an old limitation with a new disclosure.
        code.Should().Contain("var restorable = await LoadRestorableLimitationAsync(");
        code.Should().Contain("Limitation = restorable?.Limitation");
        code.Should().Contain("RepairDisclosure = restorable?.RepairDisclosure");

        // The grounding-refusal path still projects its own limitation exactly once. Shape
        // refusals supply their distinct limitation through the override instead.
        Count(code, "limitationOverride ?? Validation.Claims.CoachRefusalLimitationProjection.Project(")
            .Should().Be(1);
    }

    // ─────────────────────────────────────────────────────────── helpers

    private static readonly System.Text.Json.JsonSerializerOptions OutcomeJson =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>
    /// What the session read does: newest completed row only, decoded, limitation or null.
    /// </summary>
    private static async Task<CoachLimitationDto?> ReadLatestLimitationAsync(
        ICoachTurnOperationStore operations,
        CoachOwner owner,
        string conversationId)
    {
        var recent = await operations.GetRecentOutcomesAsync(owner, conversationId, limit: 1);

        if (recent.Count == 0)
        {
            return null;
        }

        return CoachConversationService
            .ReadOutcome(recent[0].Payload, recent[0].SchemaVersion)?.Answer?.Limitation;
    }

    private async Task<string> NewConversationAsync(CoachOwner owner)
    {
        await using var db = _harness.NewContext();
        var conversations = _harness.NewConversationStore(db);

        var created = await conversations.CreateAsync(owner, CoachHistorySamples.CreateConversation());
        return created.Conversation!.Id;
    }

    /// <summary>
    /// Completes one turn, then advances the clock.
    /// </summary>
    /// <remarks>
    /// The harness clock is frozen, so without this every row would share a <c>CompletedAt</c> and
    /// "most recent" would be whichever the database returned. Real turns are always separated in
    /// time; the advance models that rather than working around it.
    /// </remarks>
    private async Task<string> CompleteAsync(
        ICoachTurnOperationStore operations,
        string key,
        string payload,
        int schemaVersion = 3,
        CoachOwner? owner = null,
        string? conversationId = null)
    {
        var operationId = await CompleteCoreAsync(
            operations, key, payload, schemaVersion, owner, conversationId);

        _harness.Time.Advance(TimeSpan.FromSeconds(30));
        return operationId;
    }

    private async Task<string> CompleteCoreAsync(
        ICoachTurnOperationStore operations,
        string key,
        string payload,
        int schemaVersion,
        CoachOwner? owner = null,
        string? conversationId = null)
    {
        var actingOwner = owner ?? CoachHistorySamples.Owner;

        var claim = await operations.ClaimAsync(
            actingOwner,
            CoachHistorySamples.Claim(conversationId ?? _conversationId, key: key));

        claim.Outcome.Should().Be(CoachTurnClaimOutcome.Claimed);

        var complete = await operations.CompleteAsync(
            actingOwner,
            claim.Operation!.Id,
            "worker-a",
            claim.FencingVersion,
            outcomePayload: payload,
            outcomeSchemaVersion: schemaVersion,
            firstResponseSequence: 1,
            lastResponseSequence: 2);

        complete.Outcome.Should().Be(CoachTurnFinalizeOutcome.Success);
        return claim.Operation.Id;
    }

    private static string Outcome(
        CoachLimitationDto? limitation,
        CoachRepairDisclosure? disclosure = null,
        int schemaVersion = 3) =>
        System.Text.Json.JsonSerializer.Serialize(
            new CoachStoredTurnOutcome(Answer(limitation, disclosure), null),
            OutcomeJson);

    private static async Task<CoachTurnResponse?> ReadLatestAnswerAsync(
        ICoachTurnOperationStore operations,
        CoachOwner owner,
        string conversationId)
    {
        var recent = await operations.GetRecentOutcomesAsync(owner, conversationId, limit: 1);

        if (recent.Count == 0)
        {
            return null;
        }

        return CoachConversationService.ReadOutcome(recent[0].Payload, recent[0].SchemaVersion)?.Answer;
    }

    private static CoachLimitationDto Refusal() => new()
    {
        Code = CoachLimitationCode.UnverifiedClaimWithheld,
        Coverage = CoachEvidenceCoverage.PageOfOwnedSet,
        AsOfUtc = new DateTime(2026, 8, 22, 7, 0, 0, DateTimeKind.Utc),
        AffectedCount = 14,
        Destination = CoachRouteCatalog.Build(CoachRouteName.Vocabulary)
    };

    private static CoachTurnResponse Answer(
        CoachLimitationDto? limitation,
        CoachRepairDisclosure? disclosure = null)
    {
        var constraints = new CoachConstraintSetDto
        {
            AvailableMinutes = 10,
            AudioAllowed = true,
            SpeechAllowed = true,
            TypingAllowed = true,
            EnergyLevel = CoachEnergyLevel.Normal
        };

        return new CoachTurnResponse
        {
            SessionId = "session-1",
            TurnId = "turn-1",
            Status = limitation is null ? CoachTurnStatus.Completed : CoachTurnStatus.Rejected,
            StopReason = limitation is null
                ? CoachStopReason.Completed
                : CoachStopReason.ValidationFailed,
            SessionStatus = CoachSessionStatus.Active,
            Messages = [],
            ActiveConstraints = constraints,
            PlanState = new CoachPlanStateDto
            {
                PlanDate = new DateOnly(2026, 8, 22),
                PlanVersion = "v1",
                AppliedConstraints = constraints,
                EstimatedTotalMinutes = 10,
                CompletedCount = 0,
                TotalCount = 3,
                CompletionPercentage = 0
            },
            Limitation = limitation,
            RepairDisclosure = disclosure,
            ExpiresAtUtc = new DateTime(2026, 8, 23, 7, 0, 0, DateTimeKind.Utc)
        };
    }

    private static int Count(string source, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull();
        return directory!.FullName;
    }
}
