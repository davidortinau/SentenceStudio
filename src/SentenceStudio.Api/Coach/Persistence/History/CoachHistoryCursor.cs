using System.Globalization;

namespace SentenceStudio.Api.Coach.Persistence.History;

/// <summary>
/// Opaque, owner-bound pagination cursors.
/// </summary>
/// <remarks>
/// <para>
/// A cursor is encrypted with <see cref="ICoachContentProtector"/> under a cursor-specific
/// content kind, bound to the owner and the record scope. That makes it unforgeable and
/// non-transferable: a cursor issued to one learner cannot be replayed by another, and a client
/// cannot hand-craft one to walk past a filter. Data Protection emits URL-safe base64, so the
/// value is query-string safe without further encoding.
/// </para>
/// <para>
/// A tampered or foreign cursor is reported as <see cref="CoachHistoryStatus.InvalidCursor"/>,
/// never as an exception and never by silently falling back to the first page — a silent
/// fallback would turn a tampering attempt into a successful full read.
/// </para>
/// </remarks>
internal static class CoachHistoryCursor
{
    private const string ConversationPrefix = "c1";
    private const string MessagePrefix = "m1";

    /// <summary>The record scope for conversation-list cursors.</summary>
    internal const string ConversationScope = "conversations";

    /// <summary>Encodes a conversation-list position.</summary>
    internal static string EncodeConversation(
        ICoachContentProtector protector,
        CoachOwner owner,
        DateTime updatedAt,
        string id)
    {
        var value = string.Create(
            CultureInfo.InvariantCulture,
            $"{ConversationPrefix}|{updatedAt.Ticks}|{id}");

        return protector.Protect(
            new CoachProtectionContext(owner, CoachProtectedContentKind.ListCursor, ConversationScope, protector.CurrentVersion),
            value);
    }

    /// <summary>Decodes a conversation-list position. Returns false for anything untrusted.</summary>
    internal static bool TryDecodeConversation(
        ICoachContentProtector protector,
        CoachOwner owner,
        string cursor,
        out DateTime updatedAt,
        out string id)
    {
        updatedAt = default;
        id = string.Empty;

        if (!protector.TryUnprotect(
                new CoachProtectionContext(owner, CoachProtectedContentKind.ListCursor, ConversationScope, protector.CurrentVersion),
                cursor,
                out var value)
            || value is null)
        {
            return false;
        }

        var parts = value.Split('|');
        if (parts.Length != 3 || parts[0] != ConversationPrefix)
        {
            return false;
        }

        if (!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
            || ticks < DateTime.MinValue.Ticks
            || ticks > DateTime.MaxValue.Ticks
            || string.IsNullOrEmpty(parts[2]))
        {
            return false;
        }

        updatedAt = new DateTime(ticks, DateTimeKind.Utc);
        id = parts[2];
        return true;
    }

    /// <summary>Encodes a message position, bound to its conversation.</summary>
    internal static string EncodeMessage(
        ICoachContentProtector protector,
        CoachOwner owner,
        string conversationId,
        long sequence)
    {
        var value = string.Create(CultureInfo.InvariantCulture, $"{MessagePrefix}|{sequence}");

        return protector.Protect(
            new CoachProtectionContext(owner, CoachProtectedContentKind.ListCursor, conversationId, protector.CurrentVersion),
            value);
    }

    /// <summary>
    /// Decodes a message position. The conversation id is part of the protection context, so a
    /// cursor from one conversation cannot be replayed against another.
    /// </summary>
    internal static bool TryDecodeMessage(
        ICoachContentProtector protector,
        CoachOwner owner,
        string conversationId,
        string cursor,
        out long sequence)
    {
        sequence = 0;

        if (!protector.TryUnprotect(
                new CoachProtectionContext(owner, CoachProtectedContentKind.ListCursor, conversationId, protector.CurrentVersion),
                cursor,
                out var value)
            || value is null)
        {
            return false;
        }

        var parts = value.Split('|');
        if (parts.Length != 2 || parts[0] != MessagePrefix)
        {
            return false;
        }

        return long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out sequence);
    }
}
