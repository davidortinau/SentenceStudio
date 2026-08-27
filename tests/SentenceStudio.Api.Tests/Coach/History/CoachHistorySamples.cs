using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.History;

/// <summary>Shared owners and payload builders for the durable history tests.</summary>
internal static class CoachHistorySamples
{
    public static CoachOwner Owner => CoachOwner.ForUser(CoachPersistenceSamples.OwnerUserId);

    public static CoachOwner Intruder => CoachOwner.ForUser(CoachPersistenceSamples.OtherUserId);

    /// <summary>The same authority with a different tenant hint, to prove tenant is not bound.</summary>
    public static CoachOwner OwnerOtherTenant =>
        CoachOwner.ForUser(CoachPersistenceSamples.OwnerUserId, "tenant-other");

    /// <summary>An owner with no authority. Every store must refuse it.</summary>
    public static CoachOwner Empty => default;

    public static CoachMessagePayload LearnerText(string? text = null) => new()
    {
        Kind = CoachMessagePayloadKind.LearnerText,
        CreatedAtUtc = new DateTime(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc),
        Text = text ?? CoachPersistenceSamples.LearnerSentinel
    };

    public static CoachMessagePayload CoachText(string text = "Understood.") => new()
    {
        Kind = CoachMessagePayloadKind.CoachText,
        CreatedAtUtc = new DateTime(2026, 8, 14, 12, 0, 1, DateTimeKind.Utc),
        Text = text
    };

    public static CoachMessagePayload StructuredAnswer(string plain = "Use the polite form.") => new()
    {
        Kind = CoachMessagePayloadKind.StructuredAnswer,
        CreatedAtUtc = new DateTime(2026, 8, 14, 12, 0, 2, DateTimeKind.Utc),
        Answer = new CoachStoredAnswer
        {
            Topic = CoachAnswerTopic.Grammar,
            PlainText = plain,
            TargetLanguageTag = "ko",
            DisplayLanguageTag = "en",
            EndsWithRecallQuestion = true,
            Blocks =
            {
                new CoachStoredAnswerBlock
                {
                    Kind = CoachAnswerBlockKind.Answer,
                    Label = "Why",
                    Spans =
                    {
                        new CoachStoredAnswerSpan
                        {
                            Text = plain,
                            Language = CoachLanguageRole.Display,
                            LanguageTag = "en"
                        }
                    }
                }
            }
        }
    };

    public static CreateCoachConversationRequest CreateConversation(string title = "Morning practice") =>
        new(title, CoachConversationTitleSource.Generated, "ko");

    public static AppendCoachMessageRequest Append(
        string conversationId,
        CoachMessagePayload payload,
        CoachMessageRole role = CoachMessageRole.Learner,
        CoachMessageKind kind = CoachMessageKind.Text,
        string? operationId = null,
        string? messageId = null) =>
        new(conversationId, role, kind, payload, operationId, messageId);

    public static ClaimCoachTurnRequest Claim(
        string conversationId,
        string key = "idem-1",
        string payload = "{\"text\":\"hello\"}",
        string leaseOwner = "worker-a",
        TimeSpan? lease = null) =>
        new(conversationId, key, payload, leaseOwner, lease ?? TimeSpan.FromMinutes(2));
}
