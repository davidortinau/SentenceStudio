using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.History;

/// <summary>
/// The conversation surface itself: creation, ownership, paging, rename, close, and delete.
/// </summary>
/// <remarks>
/// Everything here is deliberately model-free. A learner listing their threads, renaming one, or
/// deleting one must never depend on an AI provider being reachable, so these tests also assert
/// the scripted coach was never called.
/// </remarks>
public class CoachConversationServiceTests
{
    [Fact]
    public async Task Create_returns_the_same_conversation_for_a_repeated_idempotency_key()
    {
        using var harness = new CoachConversationHarness();

        var first = await harness.Service.CreateAsync(new StartCoachConversationRequest
        {
            IdempotencyKey = "create-key-1"
        });

        var second = await harness.Service.CreateAsync(new StartCoachConversationRequest
        {
            IdempotencyKey = "create-key-1"
        });

        first.IsOk.Should().BeTrue();
        second.IsOk.Should().BeTrue();
        second.Value!.ConversationId.Should().Be(first.Value!.ConversationId,
            "a retried create must not leave a second empty thread behind");

        var page = await harness.Service.ListAsync(pageSize: null, cursor: null);
        page.Value!.Items.Should().HaveCount(1);
        harness.Coach.RunCount.Should().Be(0, "creating a conversation never calls a model");
    }

    [Fact]
    public async Task Create_requires_an_idempotency_key()
    {
        using var harness = new CoachConversationHarness();

        var result = await harness.Service.CreateAsync(new StartCoachConversationRequest
        {
            IdempotencyKey = "   "
        });

        result.IsOk.Should().BeFalse();
        result.Status.Should().Be(CoachOperationStatus.InvalidInput);
    }

    [Fact]
    public async Task Create_falls_back_to_a_dated_title_without_asking_a_model()
    {
        using var harness = new CoachConversationHarness();

        var result = await harness.Service.CreateAsync(new StartCoachConversationRequest
        {
            IdempotencyKey = "untitled"
        });

        result.Value!.Title.Should().NotBeNullOrWhiteSpace();
        result.Value.TitleOrigin.Should().Be(CoachConversationTitleOrigin.Generated);
        harness.Coach.RunCount.Should().Be(0, "the server never asks a model to name a thread");
    }

    [Fact]
    public async Task Create_keeps_a_learner_title_as_learner_authored()
    {
        using var harness = new CoachConversationHarness();

        var result = await harness.Service.CreateAsync(new StartCoachConversationRequest
        {
            IdempotencyKey = "titled",
            Title = "Grammar drills"
        });

        result.Value!.Title.Should().Be("Grammar drills");
        result.Value.TitleOrigin.Should().Be(CoachConversationTitleOrigin.Learner);
    }

    [Fact]
    public async Task Another_learner_cannot_see_or_touch_a_conversation()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        harness.ActAs(CoachConversationHarness.OtherUserId);

        var read = await harness.Service.GetAsync(conversationId);
        var messages = await harness.Service.GetMessagesAsync(conversationId, null, null);
        var renamed = await harness.Service.UpdateAsync(conversationId, new UpdateCoachConversationRequest
        {
            Title = "Mine now"
        });
        var exported = await harness.Service.OpenExportAsync(conversationId);
        var listed = await harness.Service.ListAsync(null, null);

        // A conversation owned by someone else is indistinguishable from one that never existed.
        read.Status.Should().Be(CoachOperationStatus.SessionNotFound);
        messages.Status.Should().Be(CoachOperationStatus.SessionNotFound);
        renamed.Status.Should().Be(CoachOperationStatus.SessionNotFound);
        exported.Status.Should().Be(CoachOperationStatus.SessionNotFound);
        listed.Value!.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_by_another_learner_leaves_the_conversation_intact()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        harness.ActAs(CoachConversationHarness.OtherUserId);
        var deleted = await harness.Service.DeleteAsync(conversationId);

        // Delete is idempotent and owner-scoped, so an intruder gets the same answer they would
        // get for an id that never existed. Telling them apart would confirm the id is real.
        deleted.IsOk.Should().BeTrue();

        harness.ActAs(CoachConversationHarness.OwnerUserId);
        var stillThere = await harness.Service.GetAsync(conversationId);
        stillThere.IsOk.Should().BeTrue("an intruder's delete must not reach the owner's data");
    }

    [Fact]
    public async Task List_pages_newest_first_and_a_cursor_walks_backwards_without_repeats()
    {
        using var harness = new CoachConversationHarness();

        var created = new List<string>();
        for (var i = 0; i < 7; i++)
        {
            created.Add(await harness.CreateConversationAsync(title: $"Thread {i}"));
            harness.Time.Advance(TimeSpan.FromMinutes(1));
        }

        var firstPage = await harness.Service.ListAsync(pageSize: 3, cursor: null);
        firstPage.Value!.Items.Should().HaveCount(3);
        firstPage.Value.NextCursor.Should().NotBeNull();

        var seen = new List<string>(firstPage.Value.Items.Select(i => i.ConversationId));
        var cursor = firstPage.Value.NextCursor;

        while (cursor is not null)
        {
            var page = await harness.Service.ListAsync(pageSize: 3, cursor: cursor);
            page.IsOk.Should().BeTrue();
            seen.AddRange(page.Value!.Items.Select(i => i.ConversationId));
            cursor = page.Value.NextCursor;
        }

        seen.Should().OnlyHaveUniqueItems("paging must never hand back the same row twice");
        seen.Should().BeEquivalentTo(created);
        seen.Should().Equal(Enumerable.Reverse(created), "the list is newest first");
    }

    [Fact]
    public async Task List_clamps_the_page_size_to_the_published_maximum()
    {
        using var harness = new CoachConversationHarness();
        for (var i = 0; i < 3; i++)
        {
            await harness.CreateConversationAsync();
        }

        var huge = await harness.Service.ListAsync(pageSize: 5_000, cursor: null);
        var negative = await harness.Service.ListAsync(pageSize: -1, cursor: null);

        // Clamped, not rejected: an over-large page is a client bug, not an attack, and the cap
        // is what protects the server.
        huge.IsOk.Should().BeTrue();
        huge.Value!.Items.Count.Should().BeLessThanOrEqualTo(CoachHistoryLimits.ConversationPageMax);
        negative.IsOk.Should().BeTrue();
    }

    [Theory]
    [InlineData("not-a-cursor")]
    [InlineData("MDAwMQ==")]
    [InlineData("../../etc/passwd")]
    [InlineData("999999999")]
    public async Task A_tampered_conversation_cursor_is_rejected_rather_than_interpreted(string cursor)
    {
        using var harness = new CoachConversationHarness();
        await harness.CreateConversationAsync();

        var result = await harness.Service.ListAsync(pageSize: 2, cursor: cursor);

        result.IsOk.Should().BeFalse();
        result.Status.Should().Be(CoachOperationStatus.InvalidInput);
    }

    [Fact]
    public async Task A_cursor_minted_for_one_learner_does_not_read_another_learners_history()
    {
        using var harness = new CoachConversationHarness();
        for (var i = 0; i < 4; i++)
        {
            await harness.CreateConversationAsync();
            harness.Time.Advance(TimeSpan.FromMinutes(1));
        }

        var page = await harness.Service.ListAsync(pageSize: 2, cursor: null);
        var ownerCursor = page.Value!.NextCursor;
        ownerCursor.Should().NotBeNull();

        harness.ActAs(CoachConversationHarness.OtherUserId);
        var stolen = await harness.Service.ListAsync(pageSize: 2, cursor: ownerCursor);

        // Either refused outright or scoped to the caller — never someone else's rows.
        if (stolen.IsOk)
        {
            stolen.Value!.Items.Should().BeEmpty();
        }
        else
        {
            stolen.Status.Should().Be(CoachOperationStatus.InvalidInput);
        }
    }

    [Fact]
    public async Task Rename_requires_the_expected_state_version()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        var current = await harness.Service.GetAsync(conversationId);
        var version = current.Value!.StateVersion;

        var stale = await harness.Service.UpdateAsync(conversationId, new UpdateCoachConversationRequest
        {
            ExpectedStateVersion = version - 1,
            Title = "Stale write"
        });

        stale.IsOk.Should().BeFalse();
        stale.Status.Should().Be(CoachOperationStatus.PlanChangedElsewhere);

        var fresh = await harness.Service.UpdateAsync(conversationId, new UpdateCoachConversationRequest
        {
            ExpectedStateVersion = version,
            Title = "Fresh write"
        });

        fresh.IsOk.Should().BeTrue();
        fresh.Value!.Title.Should().Be("Fresh write");
        fresh.Value.StateVersion.Should().BeGreaterThan(version, "a write moves the token");
    }

    [Fact]
    public async Task Rename_rejects_an_over_long_title()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        var result = await harness.Service.UpdateAsync(conversationId, new UpdateCoachConversationRequest
        {
            Title = new string('x', CoachHistoryLimits.TitleMaxLength + 1)
        });

        result.IsOk.Should().BeFalse();
        result.Status.Should().Be(CoachOperationStatus.InvalidInput);
    }

    [Fact]
    public async Task A_closed_conversation_stays_readable_and_refuses_new_turns()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        var closed = await harness.Service.UpdateAsync(conversationId, new UpdateCoachConversationRequest
        {
            Close = true
        });

        closed.IsOk.Should().BeTrue();
        closed.Value!.IsClosed.Should().BeTrue();

        var read = await harness.Service.GetAsync(conversationId);
        read.IsOk.Should().BeTrue("closing hides nothing");
        read.Value!.IsClosed.Should().BeTrue();

        var listed = await harness.Service.ListAsync(null, null);
        listed.Value!.Items.Should().ContainSingle(i => i.ConversationId == conversationId,
            "a closed thread is still the learner's history");

        var turn = await harness.TurnAsync(conversationId, "one more thing");
        turn.IsOk.Should().BeFalse();
        turn.Status.Should().Be(CoachOperationStatus.PlanChangedElsewhere);
        harness.Coach.RunCount.Should().Be(0, "a closed conversation refuses before reaching a model");
    }

    [Fact]
    public async Task Reopening_a_closed_conversation_accepts_turns_again()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        await harness.Service.UpdateAsync(conversationId, new UpdateCoachConversationRequest { Close = true });
        var reopened = await harness.Service.UpdateAsync(conversationId, new UpdateCoachConversationRequest { Close = false });

        reopened.Value!.IsClosed.Should().BeFalse();

        var turn = await harness.TurnAsync(conversationId, "back again");
        turn.IsOk.Should().BeTrue(turn.Detail);
    }

    [Fact]
    public async Task Delete_hides_the_conversation_immediately_and_is_idempotent()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();
        await harness.TurnAsync(conversationId, "something to keep");

        var first = await harness.Service.DeleteAsync(conversationId);
        var second = await harness.Service.DeleteAsync(conversationId);

        first.IsOk.Should().BeTrue();
        second.IsOk.Should().BeTrue("deleting what is already gone is the desired end state");

        var read = await harness.Service.GetAsync(conversationId);
        read.Status.Should().Be(CoachOperationStatus.SessionNotFound);

        var listed = await harness.Service.ListAsync(null, null);
        listed.Value!.Items.Should().BeEmpty();

        var messages = await harness.Service.GetMessagesAsync(conversationId, null, null);
        messages.Status.Should().Be(CoachOperationStatus.SessionNotFound,
            "a hidden conversation's messages are hidden with it");
    }

    [Fact]
    public async Task The_whole_surface_is_off_when_durable_history_is_disabled()
    {
        using var harness = new CoachConversationHarness(durableHistory: false);

        harness.Service.IsEnabled.Should().BeFalse("the flag defaults to off and E2E turns it on");

        var created = await harness.Service.CreateAsync(new StartCoachConversationRequest
        {
            IdempotencyKey = "off"
        });

        created.IsOk.Should().BeFalse();
        created.Status.Should().Be(CoachOperationStatus.Unavailable);
    }
}
