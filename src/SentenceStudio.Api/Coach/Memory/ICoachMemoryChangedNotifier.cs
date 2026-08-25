using Microsoft.Extensions.Logging;
using SentenceStudio.Api.Coach.Persistence.History;

namespace SentenceStudio.Api.Coach.Memory;

/// <summary>What happened to an owner's memory.</summary>
public enum CoachMemoryChangeKind
{
    /// <summary>A candidate became an active fact.</summary>
    Approved = 0,

    /// <summary>An active fact's value changed.</summary>
    Edited = 1,

    /// <summary>One fact was removed.</summary>
    Forgotten = 2,

    /// <summary>Every fact for the owner was removed.</summary>
    ForgottenAll = 3,

    /// <summary>Facts were removed because their source conversation was deleted.</summary>
    SourceDeleted = 4
}

/// <summary>
/// Tells the rest of the coach that an owner's memory changed.
/// </summary>
/// <remarks>
/// <para>
/// This exists because of a specific failure mode. A coach session is a serialized checkpoint. If a
/// learner forgets a fact that was already written into a checkpoint, the fact keeps influencing
/// the conversation even though the learner deleted it — deletion that does not actually delete.
/// </para>
/// <para>
/// This lane raises the signal and nothing more. Rotating or clearing affected checkpoints belongs
/// to the session lane, which owns <c>AgentSession</c>. Implementations must not throw: a
/// notification failure must never turn a successful forget into an error the learner sees.
/// </para>
/// </remarks>
public interface ICoachMemoryChangedNotifier
{
    /// <summary>Announces a change for one owner.</summary>
    /// <param name="owner">The owner whose memory changed.</param>
    /// <param name="change">What happened.</param>
    /// <param name="affectedCount">How many rows were affected. A count, never a value.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task MemoryChangedAsync(
        CoachOwner owner,
        CoachMemoryChangeKind change,
        int affectedCount,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The default notifier: records the shape of the change and does nothing else.
/// </summary>
/// <remarks>
/// Registered by default so the store can always call the notifier unconditionally. When the
/// session lane lands, it replaces this registration and the store does not change.
/// </remarks>
public sealed class NoOpCoachMemoryChangedNotifier : ICoachMemoryChangedNotifier
{
    private readonly ILogger<NoOpCoachMemoryChangedNotifier> _logger;

    /// <summary>Creates the notifier.</summary>
    public NoOpCoachMemoryChangedNotifier(ILogger<NoOpCoachMemoryChangedNotifier> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Task MemoryChangedAsync(
        CoachOwner owner,
        CoachMemoryChangeKind change,
        int affectedCount,
        CancellationToken cancellationToken = default)
    {
        // Shape only: which change, how many rows. No owner id, no value, no source ids.
        _logger.LogDebug(
            "Coach memory changed. Change={Change} Affected={Affected}",
            change,
            affectedCount);

        return Task.CompletedTask;
    }
}
