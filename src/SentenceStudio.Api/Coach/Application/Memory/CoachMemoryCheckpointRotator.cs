using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Api.Coach.Memory;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Persistence.History;

namespace SentenceStudio.Api.Coach.Application.Memory;

/// <summary>
/// Rotates the owner's agent checkpoints whenever their saved memory changes.
/// </summary>
/// <remarks>
/// <para>
/// The memory lane owns the store and deliberately does not own <c>AgentSession</c>. This is the
/// session lane's half of that contract. A checkpoint is an opaque serialized conversation that
/// already contains whatever was in the prompt when it was written, so deleting a fact row does
/// not remove the fact from a live conversation. Without this, "forget that" would look like it
/// worked while the value kept speaking for up to another twenty-four hours.
/// </para>
/// <para>
/// Rotation is deliberately coarse: every live checkpoint the owner holds, for every change kind,
/// including approvals and edits. Working out which conversations had actually seen the affected
/// fact would mean reasoning about the inside of serialized agent state, and getting that wrong
/// fails in the direction where a forgotten value survives. Rebuilding a checkpoint is cheap — the
/// ledger is canonical and the next turn reconstructs from it — so the conservative choice costs
/// one extra rebuild and the clever one costs a privacy promise.
/// </para>
/// <para>
/// Approvals rotate too, and not merely for symmetry. A newly approved preference should take
/// effect on the learner's next message; if the conversation resumed from a checkpoint written
/// before the approval, the block would be missing until that checkpoint expired and the learner
/// would reasonably conclude the feature does not work.
/// </para>
/// </remarks>
public sealed class CoachMemoryCheckpointRotator : ICoachMemoryChangedNotifier
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<CoachMemoryCheckpointRotator> _logger;

    public CoachMemoryCheckpointRotator(
        IServiceScopeFactory scopes,
        ILogger<CoachMemoryCheckpointRotator> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    public async Task MemoryChangedAsync(
        CoachOwner owner,
        CoachMemoryChangeKind change,
        int affectedCount,
        CancellationToken cancellationToken = default)
    {
        if (owner.IsEmpty)
        {
            return;
        }

        // Nothing changed, so nothing can be stale. "Forget everything" against an empty store
        // still raises a notification, and rebuilding every live conversation from the ledger to
        // remove a value that was never there is a latency cost the learner pays for no reason.
        if (affectedCount <= 0)
        {
            return;
        }

        try
        {
            // Its own scope. This is a singleton, the session store is scoped, and the request
            // scope that raised the change may already be unwinding by the time this is awaited.
            using var scope = _scopes.CreateScope();
            var sessions = scope.ServiceProvider.GetRequiredService<ICoachSessionStore>();

            var rotated = await sessions
                .ClearAgentCheckpointsAsync(owner.UserProfileId, cancellationToken)
                .ConfigureAwait(false);

            // Shape only: how many conversations were affected and why. Never the owner, never the
            // fact id, never the value. A log line is exactly the wrong place for something the
            // learner has just asked to have forgotten.
            _logger.LogInformation(
                "[Coach] Memory change rotated agent checkpoints. Change={Change} Facts={Facts} Rotated={Rotated}",
                change,
                affectedCount,
                rotated);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A notification failure must never turn a successful forget into a learner-visible
            // error: the fact is already gone from the store, which is the part that carries the
            // promise. A stale checkpoint is bounded and self-healing — it expires within the day —
            // so it is worth a warning and not worth failing the request the learner actually made.
            _logger.LogWarning(
                ex,
                "[Coach] Memory change could not rotate agent checkpoints. Change={Change}",
                change);
        }
    }
}
