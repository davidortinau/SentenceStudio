using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;

namespace SentenceStudio.Api.Coach.Persistence.History;

/// <summary>
/// EF Core implementation of <see cref="ICoachHistoryExportReader"/>.
/// </summary>
/// <remarks>
/// Rows are streamed with <c>AsAsyncEnumerable</c> and decrypted one at a time, so a learner with
/// years of history costs the same memory as one with a single turn, and no plaintext is ever
/// written to disk.
/// </remarks>
public sealed class CoachHistoryExportReader : ICoachHistoryExportReader
{
    private readonly CoachDbContext _db;
    private readonly ICoachContentProtector _protector;
    private readonly ILogger<CoachHistoryExportReader> _logger;

    public CoachHistoryExportReader(
        CoachDbContext db,
        ICoachContentProtector protector,
        ILogger<CoachHistoryExportReader> logger)
    {
        _db = db;
        _protector = protector;
        _logger = logger;
    }

    public async IAsyncEnumerable<CoachConversationRecord> StreamConversationsAsync(
        CoachOwner owner,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (owner.IsEmpty)
        {
            _logger.LogWarning(
                "[Coach] {Operation} called with no active user id — returning no data.",
                nameof(StreamConversationsAsync));
            yield break;
        }

        var query = _db.CoachConversations.AsNoTracking()
            .Where(c => c.UserProfileId == owner.UserProfileId
                        && c.Status != CoachConversationStatus.Deleting
                        && c.DeletedAt == null)
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .AsAsyncEnumerable();

        await foreach (var conversation in query.WithCancellation(cancellationToken))
        {
            var context = new CoachProtectionContext(
                owner,
                CoachProtectedContentKind.ConversationTitle,
                conversation.Id,
                conversation.ContentProtectionVersion);

            var title = _protector.TryUnprotect(context, conversation.ProtectedTitle, out var plaintext)
                ? plaintext
                : null;

            yield return new CoachConversationRecord(
                conversation.Id,
                title,
                conversation.TitleSource,
                conversation.TargetLanguageCode,
                conversation.Status,
                conversation.HistoryStartsAt,
                conversation.LastSequence,
                conversation.Version,
                conversation.CreatedAt,
                conversation.UpdatedAt);
        }
    }

    public async IAsyncEnumerable<CoachMessageRecord> StreamMessagesAsync(
        CoachOwner owner,
        string conversationId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (owner.IsEmpty)
        {
            _logger.LogWarning(
                "[Coach] {Operation} called with no active user id — returning no data.",
                nameof(StreamMessagesAsync));
            yield break;
        }

        if (string.IsNullOrWhiteSpace(conversationId))
        {
            yield break;
        }

        var query = _db.CoachMessages.AsNoTracking()
            .Where(m => m.UserProfileId == owner.UserProfileId && m.ConversationId == conversationId)
            .OrderBy(m => m.Sequence)
            .AsAsyncEnumerable();

        await foreach (var message in query.WithCancellation(cancellationToken))
        {
            var context = new CoachProtectionContext(
                owner,
                CoachProtectedContentKind.MessagePayload,
                message.Id,
                message.ContentProtectionVersion);

            CoachMessagePayload? payload = null;
            if (_protector.TryUnprotect(context, message.ProtectedPayload, out var json) && json is not null)
            {
                CoachMessagePayloadSerializer.TryDeserialize(json, out payload);
            }

            yield return new CoachMessageRecord(
                message.Id,
                message.ConversationId,
                message.Sequence,
                message.Role,
                message.Kind,
                payload,
                message.ContentSchemaVersion,
                message.OperationId,
                message.CreatedAt);
        }
    }
}
