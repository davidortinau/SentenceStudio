using SentenceStudio.Api.Coach.Agents;
using System.Text.Json;
using FluentAssertions;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;

namespace SentenceStudio.Api.Tests.Coach.History;

/// <summary>
/// What the learner is shown, and what they are never shown.
/// </summary>
/// <remarks>
/// Durable history turns a transient reply into a record that outlives the request, so anything
/// that leaks into the projection leaks permanently. These tests fix both halves: the public
/// shape a client can rely on, and the internals that must never reach it or the disk in the
/// clear.
/// </remarks>
public sealed class CoachHistoryProjectionTests
{
    // ---------------------------------------------------------------- public shape

    [Fact]
    public async Task A_learner_message_keeps_its_words_its_role_and_its_time()
    {
        using var harness = new CoachConversationHarness();
        harness.ActAs(CoachConversationHarness.OwnerUserId);
        var conversationId = await harness.CreateConversationAsync();
        var sentAt = harness.Time.GetUtcNow().UtcDateTime;

        await harness.TurnAsync(conversationId, "저는 학생이에요");

        var page = await harness.Service.GetMessagesAsync(conversationId, pageSize: null, before: null);
        page.IsOk.Should().BeTrue(page.Detail);

        var learner = page.Value!.Items.First(m => m.Message.Role == CoachMessageRole.Learner);
        learner.Message.Text.Should().Be("저는 학생이에요");
        learner.Message.CreatedAtUtc.Should().Be(sentAt);
        learner.Sequence.Should().BePositive();
    }

    [Fact]
    public async Task A_structured_answer_survives_the_round_trip_intact()
    {
        using var harness = new CoachConversationHarness();
        harness.ActAs(CoachConversationHarness.OwnerUserId);
        var conversationId = await harness.CreateConversationAsync();

        harness.Coach.Script.Enqueue(new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent
            {
                Kind = CoachIntentKind.PedagogicalAnswer,
                CoachMessage = "Here you go",
                PedagogicalAnswer = new CoachPedagogicalAnswerIntent
                {
                    Topic = CoachAnswerTopic.Usage,
                    Blocks =
                    [
                        new CoachAnswerBlockIntent
                        {
                            Kind = CoachAnswerBlockKind.Answer,
                            Spans = [new CoachAnswerSpanIntent { Text = "Formal and casual differ.", Language = CoachLanguageRole.Display }]
                        },
                        new CoachAnswerBlockIntent
                        {
                            Kind = CoachAnswerBlockKind.Example,
                            Label = "Example",
                            Spans = [new CoachAnswerSpanIntent { Text = "저는 학생입니다.", Language = CoachLanguageRole.Target }]
                        }
                    ]
                }
            }
        });

        await harness.TurnAsync(conversationId, "Explain politeness levels");

        var page = await harness.Service.GetMessagesAsync(conversationId, pageSize: null, before: null);
        var stored = page.Value!.Items.First(m => m.Answer is not null);

        stored.Answer!.Topic.Should().Be(CoachAnswerTopic.Usage);
        stored.Answer.Blocks.Should().HaveCount(2);
        stored.Answer.Blocks[0].Kind.Should().Be(CoachAnswerBlockKind.Answer);
        stored.Answer.Blocks[1].Kind.Should().Be(CoachAnswerBlockKind.Example);
        stored.Answer.Blocks[1].Spans.Should().ContainSingle()
            .Which.Text.Should().Be("저는 학생입니다.");
    }

    [Fact]
    public async Task History_is_ordered_by_sequence_and_never_by_timestamp_alone()
    {
        using var harness = new CoachConversationHarness();
        harness.ActAs(CoachConversationHarness.OwnerUserId);
        var conversationId = await harness.CreateConversationAsync();

        // The clock never moves, so every message shares a timestamp. Only the sequence can
        // order them, which is the situation a real conversation hits inside one turn.
        await harness.TurnAsync(conversationId, "First");
        await harness.TurnAsync(conversationId, "Second");
        await harness.TurnAsync(conversationId, "Third");

        var page = await harness.Service.GetMessagesAsync(conversationId, pageSize: null, before: null);
        var items = page.Value!.Items;

        items.Select(m => m.Sequence).Should().BeInAscendingOrder();
        items.Where(m => m.Message.Role == CoachMessageRole.Learner)
            .Select(m => m.Message.Text)
            .Should().ContainInOrder("First", "Second", "Third");
    }

    [Fact]
    public async Task A_notice_is_projected_with_its_reason_code_and_its_text()
    {
        using var harness = new CoachConversationHarness();
        harness.ActAs(CoachConversationHarness.OwnerUserId);
        var conversationId = await harness.CreateConversationAsync();

        harness.Coach.OnRun = async _ =>
        {
            var operationId = await harness.LatestOperationIdAsync(conversationId);
            await harness.Operations.RequestCancelAsync(harness.Owner, operationId!);
        };

        await harness.TurnAsync(conversationId, "Never mind");

        var page = await harness.Service.GetMessagesAsync(conversationId, pageSize: null, before: null);
        var notice = page.Value!.Items.Last();

        notice.NoticeReasonCode.Should().NotBeNullOrWhiteSpace();
        notice.Message.Text.Should().NotBeNullOrWhiteSpace("a notice the learner cannot read is not a notice");
    }

    // ---------------------------------------------------------------- what never leaks

    [Fact]
    public async Task The_projection_never_carries_ciphertext_or_internal_identifiers()
    {
        using var harness = new CoachConversationHarness();
        harness.ActAs(CoachConversationHarness.OwnerUserId);
        var conversationId = await harness.CreateConversationAsync();

        var turn = await harness.TurnAsync(conversationId, "Anything at all");
        var operationId = turn.Value!.OperationId;

        var page = await harness.Service.GetMessagesAsync(conversationId, pageSize: null, before: null);
        var json = JsonSerializer.Serialize(page.Value);

        json.Should().NotContain(operationId, "an operation id is server bookkeeping, not conversation content");
        json.Should().NotContain("Ciphertext");
        json.Should().NotContain("RequestDigest");
        json.Should().NotContain("AgentSessionJson");
        json.Should().NotContain("LeaseOwner");
        json.Should().NotContain("FencingVersion");
    }

    [Fact]
    public async Task Nothing_the_learner_typed_is_readable_in_the_database()
    {
        using var harness = new CoachConversationHarness();
        harness.ActAs(CoachConversationHarness.OwnerUserId);
        var conversationId = await harness.CreateConversationAsync(title: CoachPersistenceSamples.LearnerSentinel);

        await harness.TurnAsync(conversationId, CoachPersistenceSamples.LearnerSentinel);

        foreach (var table in new[] { "CoachMessage", "CoachConversation", "CoachTurnOperation" })
        {
            using var command = harness.App.Persistence.NewRawCommand($"SELECT * FROM \"{table}\"");
            using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    if (reader.IsDBNull(i))
                    {
                        continue;
                    }

                    var value = reader.GetValue(i).ToString();
                    value.Should().NotContain(
                        CoachPersistenceSamples.LearnerSentinel,
                        $"column {reader.GetName(i)} of {table} would expose the learner's words to anyone with the database");
                }
            }
        }
    }

    // ---------------------------------------------------------------- ownership

    [Fact]
    public async Task Another_learner_reading_the_history_sees_nothing_at_all()
    {
        using var harness = new CoachConversationHarness();
        harness.ActAs(CoachConversationHarness.OwnerUserId);
        var conversationId = await harness.CreateConversationAsync();
        await harness.TurnAsync(conversationId, "Private words");

        harness.ActAs(CoachConversationHarness.OtherUserId);
        var page = await harness.Service.GetMessagesAsync(conversationId, pageSize: null, before: null);

        page.IsOk.Should().BeFalse();
        page.Status.Should().Be(CoachOperationStatus.SessionNotFound);
    }
}
