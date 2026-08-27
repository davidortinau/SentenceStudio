using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Persistence;

/// <summary>
/// EF Core implementation of <see cref="ICoachSessionStore"/>.
/// </summary>
/// <remarks>
/// Every query starts from <see cref="OwnedSessions"/> or <see cref="OwnedRevisions"/>,
/// which require a non-empty user id and always apply the <c>UserProfileId</c> filter.
/// There is no query in this file that reads a coach table without that filter.
/// </remarks>
public sealed class CoachSessionStore : ICoachSessionStore
{
    private readonly CoachDbContext _db;
    private readonly ICoachAgentSessionProtector _protector;
    private readonly CoachPersistenceOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CoachSessionStore> _logger;

    public CoachSessionStore(
        CoachDbContext db,
        ICoachAgentSessionProtector protector,
        IOptions<CoachPersistenceOptions> options,
        TimeProvider timeProvider,
        ILogger<CoachSessionStore> logger)
    {
        _db = db;
        _protector = protector;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    private IQueryable<CoachSession> OwnedSessions(string userProfileId) =>
        _db.CoachSessions.Where(s => s.UserProfileId == userProfileId);

    private IQueryable<CoachPlanRevision> OwnedRevisions(string userProfileId) =>
        _db.CoachPlanRevisions.Where(r => r.UserProfileId == userProfileId);

    private bool HasUser(string userProfileId, string operation)
    {
        if (!string.IsNullOrWhiteSpace(userProfileId))
        {
            return true;
        }

        _logger.LogWarning("[Coach] {Operation} called with no active user id — returning no data.", operation);
        return false;
    }

    public async Task<CoachSession> CreateAsync(string userProfileId, CreateCoachSessionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(userProfileId))
        {
            // Creation is the one path that cannot degrade to "no data": writing an
            // unowned row would produce exactly the orphan state the multi-tenant rule
            // exists to prevent.
            throw new ArgumentException("A coach session requires an owning user profile id.", nameof(userProfileId));
        }

        var now = UtcNow;
        var sessionId = string.IsNullOrWhiteSpace(request.SessionId) ? Guid.NewGuid().ToString() : request.SessionId!;
        var session = new CoachSession
        {
            Id = sessionId,
            UserProfileId = userProfileId,
            AgentImplementation = request.AgentImplementation,
            AgentName = request.AgentName,
            AgentConfigVersion = _options.AgentConfigVersion,
            SessionSchemaVersion = _options.SessionSchemaVersion,
            ProtectedAgentSession = _protector.Protect(
                new CoachAgentSessionContext(userProfileId, sessionId), request.AgentSessionJson),
            ActiveConstraintsJson = CoachNormalizedJson.Serialize(request.ActiveConstraints),
            Status = CoachSessionStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = now + _options.SessionLifetime
        };

        _db.CoachSessions.Add(session);
        await _db.SaveChangesAsync(cancellationToken);
        return session;
    }

    public async Task<CoachSessionLoadResult> LoadAsync(string userProfileId, string sessionId, CancellationToken cancellationToken = default)
    {
        if (!HasUser(userProfileId, nameof(LoadAsync)) || string.IsNullOrWhiteSpace(sessionId))
        {
            return CoachSessionLoadResult.NotFound;
        }

        var session = await OwnedSessions(userProfileId)
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);

        return await EvaluateAsync(session, cancellationToken);
    }

    public async Task<CoachSessionLoadResult> LoadResumableAsync(string userProfileId, CancellationToken cancellationToken = default)
    {
        if (!HasUser(userProfileId, nameof(LoadResumableAsync)))
        {
            return CoachSessionLoadResult.NotFound;
        }

        var now = UtcNow;
        var session = await OwnedSessions(userProfileId)
            .Where(s => s.ExpiresAt > now)
            .Where(s => s.Status == CoachSessionStatus.Active
                     || s.Status == CoachSessionStatus.AwaitingClarification
                     || s.Status == CoachSessionStatus.SuggestionPending)
            .OrderByDescending(s => s.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        return await EvaluateAsync(session, cancellationToken);
    }

    /// <summary>
    /// Applies the expiry, config-version, and decryption gates to an already
    /// ownership-filtered row, sliding the expiry forward on success.
    /// </summary>
    private async Task<CoachSessionLoadResult> EvaluateAsync(CoachSession? session, CancellationToken cancellationToken)
    {
        if (session is null)
        {
            return CoachSessionLoadResult.NotFound;
        }

        var now = UtcNow;
        if (session.ExpiresAt <= now)
        {
            if (session.Status != CoachSessionStatus.Expired)
            {
                session.Status = CoachSessionStatus.Expired;
                session.StopReason = CoachStopReason.SessionExpired;
                session.UpdatedAt = now;
                await _db.SaveChangesAsync(cancellationToken);
            }
            return new CoachSessionLoadResult(CoachSessionLoadStatus.Expired, null, null);
        }

        if (!string.Equals(session.AgentConfigVersion, _options.AgentConfigVersion, StringComparison.Ordinal)
            || session.SessionSchemaVersion != _options.SessionSchemaVersion)
        {
            _logger.LogInformation(
                "[Coach] Rejecting session {SessionId}: stored config/schema {StoredConfig}/{StoredSchema} != current {CurrentConfig}/{CurrentSchema}.",
                session.Id, session.AgentConfigVersion, session.SessionSchemaVersion,
                _options.AgentConfigVersion, _options.SessionSchemaVersion);
            return new CoachSessionLoadResult(CoachSessionLoadStatus.ConfigVersionMismatch, null, null);
        }

        string? agentSessionJson = null;
        if (!string.IsNullOrEmpty(session.ProtectedAgentSession)
            && !_protector.TryUnprotect(
                // The context is built from the row being read, so ciphertext moved into a
                // different learner's row (or a different session) no longer decrypts.
                new CoachAgentSessionContext(session.UserProfileId, session.Id),
                session.ProtectedAgentSession,
                out agentSessionJson))
        {
            return new CoachSessionLoadResult(CoachSessionLoadStatus.Unreadable, null, null);
        }

        session.ExpiresAt = now + _options.SessionLifetime;
        await _db.SaveChangesAsync(cancellationToken);

        return new CoachSessionLoadResult(CoachSessionLoadStatus.Found, session, agentSessionJson);
    }

    public async Task<bool> UpdateAsync(string userProfileId, string sessionId, CoachSessionUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        var session = await LoadUsableAsync(userProfileId, sessionId, nameof(UpdateAsync), cancellationToken);
        if (session is null)
        {
            return false;
        }

        if (update.AgentSessionJson is not null)
        {
            // Always re-protected under the current (v2) owner-bound purpose, which is what
            // retires a legacy payload that was read through the bounded v1 fallback.
            session.ProtectedAgentSession = _protector.Protect(
                new CoachAgentSessionContext(session.UserProfileId, session.Id), update.AgentSessionJson);
        }

        if (update.ActiveStateJson is not null)
        {
            session.ActiveConstraintsJson = update.ActiveStateJson;
        }
        else if (update.ActiveConstraints is not null)
        {
            session.ActiveConstraintsJson = CoachNormalizedJson.Serialize(update.ActiveConstraints);
        }

        if (update.Status is { } status)
        {
            session.Status = status;
        }

        if (update.ClearStopReason)
        {
            session.StopReason = null;
        }
        else if (update.StopReason is { } stopReason)
        {
            session.StopReason = stopReason;
        }

        session.TurnCount += update.TurnIncrement;
        session.ClarificationCount += update.ClarificationIncrement;
        Touch(session);

        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteAsync(string userProfileId, string sessionId, CancellationToken cancellationToken = default)
    {
        if (!HasUser(userProfileId, nameof(DeleteAsync)) || string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        // Deletion ignores expiry and config version on purpose: a learner must always be
        // able to erase conversation state, even for a session the server would refuse to
        // resume. The revision audit is retained; deleting coach history never undoes a plan.
        var session = await OwnedSessions(userProfileId)
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);

        if (session is null)
        {
            return false;
        }

        _db.CoachSessions.Remove(session);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<int> ClearAgentCheckpointsAsync(string userProfileId, CancellationToken cancellationToken = default)
    {
        if (!HasUser(userProfileId, nameof(ClearAgentCheckpointsAsync)))
        {
            return 0;
        }

        // Expired rows are skipped, not because clearing them would be wrong, but because they
        // already cannot be resumed: the load path refuses them before the checkpoint is read.
        // Rewriting them would spend a round trip to change nothing an agent session could see.
        var now = UtcNow;
        var sessions = await OwnedSessions(userProfileId)
            .Where(s => s.ExpiresAt > now)
            .Where(s => s.ProtectedAgentSession != null)
            .ToListAsync(cancellationToken);

        if (sessions.Count == 0)
        {
            return 0;
        }

        foreach (var session in sessions)
        {
            // Only the checkpoint. Constraints, pending suggestion, status, turn count, and the
            // revision audit all stay exactly as they were: the learner forgot a preference, not
            // a conversation and not a plan decision.
            session.ProtectedAgentSession = null;
            session.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return sessions.Count;
    }

    public async Task<bool> SetPendingSuggestionAsync(
        string userProfileId,
        string sessionId,
        string suggestionId,
        CoachConstraintDeltaDto delta,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delta);
        if (string.IsNullOrWhiteSpace(suggestionId))
        {
            return false;
        }

        var session = await LoadUsableAsync(userProfileId, sessionId, nameof(SetPendingSuggestionAsync), cancellationToken);
        if (session is null)
        {
            return false;
        }

        var now = UtcNow;
        session.PendingSuggestionId = suggestionId;
        session.PendingSuggestionDeltaJson = CoachNormalizedJson.Serialize(delta);
        session.PendingSuggestionCreatedAt = now;
        session.Status = CoachSessionStatus.SuggestionPending;
        Touch(session);

        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> SetPendingSuggestionPayloadAsync(
        string userProfileId,
        string sessionId,
        string suggestionId,
        string payloadJson,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(suggestionId) || string.IsNullOrWhiteSpace(payloadJson))
        {
            return false;
        }

        var session = await LoadUsableAsync(userProfileId, sessionId, nameof(SetPendingSuggestionPayloadAsync), cancellationToken);
        if (session is null)
        {
            return false;
        }

        session.PendingSuggestionId = suggestionId;
        session.PendingSuggestionDeltaJson = payloadJson;
        session.PendingSuggestionCreatedAt = UtcNow;
        session.Status = CoachSessionStatus.SuggestionPending;
        Touch(session);

        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<string?> GetPendingSuggestionPayloadAsync(
        string userProfileId,
        string sessionId,
        string suggestionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(suggestionId))
        {
            return null;
        }

        var session = await LoadUsableAsync(userProfileId, sessionId, nameof(GetPendingSuggestionPayloadAsync), cancellationToken);

        return session is null
            || !string.Equals(session.PendingSuggestionId, suggestionId, StringComparison.Ordinal)
            || string.IsNullOrEmpty(session.PendingSuggestionDeltaJson)
                ? null
                : session.PendingSuggestionDeltaJson;
    }

    public async Task<CoachConstraintDeltaDto?> GetPendingSuggestionAsync(
        string userProfileId,
        string sessionId,
        string suggestionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(suggestionId))
        {
            return null;
        }

        var session = await LoadUsableAsync(userProfileId, sessionId, nameof(GetPendingSuggestionAsync), cancellationToken);
        if (session is null
            || !string.Equals(session.PendingSuggestionId, suggestionId, StringComparison.Ordinal)
            || string.IsNullOrEmpty(session.PendingSuggestionDeltaJson))
        {
            return null;
        }

        return CoachNormalizedJson.Deserialize<CoachConstraintDeltaDto>(session.PendingSuggestionDeltaJson);
    }

    public async Task<bool> ClearPendingSuggestionAsync(string userProfileId, string sessionId, CancellationToken cancellationToken = default)
    {
        var session = await LoadUsableAsync(userProfileId, sessionId, nameof(ClearPendingSuggestionAsync), cancellationToken);
        if (session is null)
        {
            return false;
        }

        if (session.PendingSuggestionId is null && session.PendingSuggestionDeltaJson is null)
        {
            return false;
        }

        session.PendingSuggestionId = null;
        session.PendingSuggestionDeltaJson = null;
        session.PendingSuggestionCreatedAt = null;
        if (session.Status == CoachSessionStatus.SuggestionPending)
        {
            session.Status = CoachSessionStatus.Active;
        }
        Touch(session);

        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<CoachPlanRevision?> AppendRevisionAsync(
        string userProfileId,
        string sessionId,
        CoachPlanRevisionInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var session = await LoadUsableAsync(userProfileId, sessionId, nameof(AppendRevisionAsync), cancellationToken);
        if (session is null)
        {
            return null;
        }

        var lastNumber = await OwnedRevisions(userProfileId)
            .Where(r => r.SessionId == sessionId)
            .Select(r => (int?)r.RevisionNumber)
            .MaxAsync(cancellationToken) ?? 0;

        var beforeSnapshot = input.BeforePlanAuditJson ?? CoachNormalizedJson.Serialize(input.BeforePlan);
        var afterSnapshot = input.AfterPlanAuditJson ?? CoachNormalizedJson.Serialize(input.AfterPlan);

        var revision = new CoachPlanRevision
        {
            Id = string.IsNullOrWhiteSpace(input.RevisionId) ? Guid.NewGuid().ToString() : input.RevisionId!,
            UserProfileId = userProfileId,
            SessionId = sessionId,
            RevisionNumber = lastNumber + 1,
            Source = input.Source,
            IntentKind = input.IntentKind,
            AcceptedConstraintDeltaJson = CoachNormalizedJson.Serialize(input.AcceptedDelta),
            BeforePlanVersion = input.BeforePlanVersion,
            AfterPlanVersion = input.AfterPlanVersion,
            BeforePlanSnapshotJson = beforeSnapshot,
            AfterPlanSnapshotJson = afterSnapshot,
            BeforePlanHash = CoachNormalizedJson.Hash(beforeSnapshot),
            AfterPlanHash = CoachNormalizedJson.Hash(afterSnapshot),
            PreservedCompletedCount = input.PreservedCompletedCount,
            PreservedInProgressCount = input.PreservedInProgressCount,
            OperationId = string.IsNullOrWhiteSpace(input.OperationId) ? null : input.OperationId,
            CreatedAt = UtcNow
        };

        _db.CoachPlanRevisions.Add(revision);
        session.RevisionCount += 1;
        Touch(session);

        await _db.SaveChangesAsync(cancellationToken);
        return revision;
    }

    public async Task<CoachPlanRevision?> GetRevisionByOperationAsync(
        string userProfileId,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        if (!HasUser(userProfileId, nameof(GetRevisionByOperationAsync)) || string.IsNullOrWhiteSpace(operationId))
        {
            return null;
        }

        // Owner-scoped and exact. Deliberately not filtered by session: a revision is looked up by
        // the operation that produced it, so a conversation can find its own work even when the
        // session it ran under has since expired.
        return await OwnedRevisions(userProfileId)
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.OperationId == operationId, cancellationToken);
    }

    public async Task<IReadOnlyList<CoachPlanRevision>> GetRevisionsAsync(string userProfileId, string sessionId, CancellationToken cancellationToken = default)
    {
        if (!HasUser(userProfileId, nameof(GetRevisionsAsync)) || string.IsNullOrWhiteSpace(sessionId))
        {
            return Array.Empty<CoachPlanRevision>();
        }

        return await OwnedRevisions(userProfileId)
            .Where(r => r.SessionId == sessionId)
            .OrderBy(r => r.RevisionNumber)
            .ToListAsync(cancellationToken);
    }

    public async Task<CoachPlanRevision?> GetLatestRevisionAsync(string userProfileId, string sessionId, CancellationToken cancellationToken = default)
    {
        if (!HasUser(userProfileId, nameof(GetLatestRevisionAsync)) || string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        return await OwnedRevisions(userProfileId)
            .Where(r => r.SessionId == sessionId)
            .OrderByDescending(r => r.RevisionNumber)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<bool> MarkRevisionUndoneAsync(
        string userProfileId,
        string revisionId,
        string undoneByRevisionId,
        CancellationToken cancellationToken = default)
    {
        if (!HasUser(userProfileId, nameof(MarkRevisionUndoneAsync))
            || string.IsNullOrWhiteSpace(revisionId)
            || string.IsNullOrWhiteSpace(undoneByRevisionId))
        {
            return false;
        }

        var revision = await OwnedRevisions(userProfileId)
            .FirstOrDefaultAsync(r => r.Id == revisionId, cancellationToken);

        if (revision is null || revision.IsUndone)
        {
            return false;
        }

        revision.IsUndone = true;
        revision.UndoneAt = UtcNow;
        revision.UndoneByRevisionId = undoneByRevisionId;

        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Resolves an owned, unexpired, version-matched session for a write path, or null.
    /// </summary>
    private async Task<CoachSession?> LoadUsableAsync(string userProfileId, string sessionId, string operation, CancellationToken cancellationToken)
    {
        if (!HasUser(userProfileId, operation) || string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var session = await OwnedSessions(userProfileId)
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);

        var result = await EvaluateAsync(session, cancellationToken);
        return result.IsUsable ? result.Session : null;
    }

    private void Touch(CoachSession session)
    {
        var now = UtcNow;
        session.UpdatedAt = now;
        session.ExpiresAt = now + _options.SessionLifetime;
    }
}
