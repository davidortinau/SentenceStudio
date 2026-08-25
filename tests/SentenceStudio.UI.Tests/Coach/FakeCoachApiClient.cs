using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.LearnerMemory;
using SentenceStudio.Services.Api;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// Hand-rolled fake so the workspace-state tests exercise real transition code without HTTP.
/// Each hook can be replaced per test; the defaults return a benign completed turn.
/// </summary>
internal sealed class FakeCoachApiClient : ICoachApiClient
{
    /// <summary>
    /// Availability as this fake server reports it. Both optional features are on by default so a
    /// test that cares about history or saved preferences does not have to say so; the tests that
    /// exercise a server with a feature off, or an older server that sends no flags at all, set
    /// this explicitly.
    /// </summary>
    public CoachAvailabilityResponse Availability { get; set; } = new()
    {
        IsAvailable = true,
        State = CoachAvailabilityState.Available,
        CanEditPlan = true,
        IsDurableHistoryAvailable = true,
        IsMemoryAvailable = true
    };

    public Func<CoachSessionResponse>? OnStartSession { get; set; }

    public Func<string, CoachSessionResponse?>? OnGetSession { get; set; }

    public Func<CoachTurnRequest, CoachTurnResponse>? OnSubmitTurn { get; set; }

    public Func<CoachTurnResponse>? OnAccept { get; set; }

    public Func<CoachTurnResponse>? OnReject { get; set; }

    public Func<CoachTurnResponse>? OnUndo { get; set; }

    public int StartSessionCalls { get; private set; }

    public int SubmitTurnCalls { get; private set; }

    public int DeleteCalls { get; private set; }

    public int CancelCalls { get; private set; }

    /// <summary>Set to make the cancel endpoint fail, proving Stop still releases the UI.</summary>
    public Func<Task>? OnCancel { get; set; }

    public List<CoachTurnRequest> SubmittedTurns { get; } = new();

    /// <summary>How many times availability was read. The flags should need it once per circuit.</summary>
    public int AvailabilityCalls { get; private set; }

    /// <summary>Set to make availability unreachable rather than merely negative.</summary>
    public Func<CoachAvailabilityResponse>? OnGetAvailability { get; set; }

    public Task<CoachAvailabilityResponse> GetAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        AvailabilityCalls++;
        return Task.FromResult(OnGetAvailability is { } hook ? hook() : Availability);
    }

    public Task<CoachSessionResponse> StartSessionAsync(StartCoachSessionRequest request, CancellationToken cancellationToken = default)
    {
        StartSessionCalls++;
        return Task.FromResult(OnStartSession?.Invoke() ?? Session());
    }

    public Task<CoachSessionResponse?> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        => Task.FromResult(OnGetSession is null ? Session(sessionId) : OnGetSession(sessionId));

    public Task<CoachTurnResponse> SubmitTurnAsync(string sessionId, CoachTurnRequest request, CancellationToken cancellationToken = default)
    {
        SubmitTurnCalls++;
        SubmittedTurns.Add(request);
        return Task.FromResult(OnSubmitTurn?.Invoke(request) ?? CoachStateMachineTests.Turn());
    }

    public Task<CoachTurnResponse> AcceptSuggestionAsync(string sessionId, string suggestionId, CoachSuggestionDecisionRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(OnAccept?.Invoke()
            ?? CoachStateMachineTests.Turn(receipt: CoachStateMachineTests.Receipt(CoachRevisionSource.AcceptedSuggestion)));

    public Task<CoachTurnResponse> RejectSuggestionAsync(string sessionId, string suggestionId, CoachSuggestionDecisionRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(OnReject?.Invoke() ?? CoachStateMachineTests.Turn());

    public Task<CoachTurnResponse> UndoAsync(string sessionId, CoachUndoRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(OnUndo?.Invoke()
            ?? CoachStateMachineTests.Turn(receipt: CoachStateMachineTests.Receipt(CoachRevisionSource.Undo, "receipt-2", "rev-2", canUndo: false)));

    public Task CancelSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        CancelCalls++;
        return OnCancel?.Invoke() ?? Task.CompletedTask;
    }

    public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        DeleteCalls++;
        return Task.CompletedTask;
    }

    // ============================================================ durable history
    //
    // An in-memory conversation ledger rather than a set of one-shot hooks. The behaviours under
    // test - reconciliation, idempotency, cursor paging - are all about what happens when the same
    // store is read twice, and a hook that answers a canned value cannot show that.
    //
    // Durable history is OFF by default, so every legacy test keeps the session-only behaviour and
    // the listing route answers the way a server with the flag off answers: not found.

    /// <summary>Whether the durable-history routes exist at all. Off by default.</summary>
    public bool DurableHistoryAvailable { get; set; }

    // ---------------------------------------------------------------- owner scoping
    //
    // Off unless a test opts in by recording an owner for a conversation. The account-boundary
    // regression needs a server that answers for the CALLER rather than for the ledger, because
    // "the previous learner's thread is still on screen" and "the previous learner's thread is
    // still in the fake" are different failures and only the first one is the defect.

    /// <summary>Who the client is currently authenticated as, when a test models that.</summary>
    public string? Owner { get; set; }

    /// <summary>Owner per conversation. Empty means every conversation answers to everybody.</summary>
    public Dictionary<string, string> ConversationOwners { get; } = new(StringComparer.Ordinal);

    /// <summary>Every conversation id a message page was asked for, in order.</summary>
    public List<string> MessagePageRequests { get; } = new();

    /// <summary>True when this conversation is readable by whoever is asking.</summary>
    private bool OwnedByCaller(string conversationId) =>
        !ConversationOwners.TryGetValue(conversationId, out var owner)
        || string.Equals(owner, Owner, StringComparison.Ordinal);

    /// <summary>The conversation ledger, newest activity last.</summary>
    public List<CoachConversationDto> Conversations { get; } = new();

    /// <summary>Messages per conversation, in server sequence order.</summary>
    public Dictionary<string, List<CoachHistoryMessageDto>> ConversationMessages { get; } =
        new(StringComparer.Ordinal);

    public List<CoachConversationTurnRequest> SubmittedConversationTurns { get; } = new();

    public List<StartCoachConversationRequest> ConversationCreateRequests { get; } = new();

    public List<UpdateCoachConversationRequest> ConversationUpdates { get; } = new();

    public List<(string ConversationId, CoachExportFormat Format)> Exports { get; } = new();

    public int ListConversationCalls { get; private set; }

    public int CreateConversationCalls { get; private set; }

    public int SubmitConversationTurnCalls { get; private set; }

    public int GetConversationOperationCalls { get; private set; }

    public int CancelConversationTurnCalls { get; private set; }

    public int DeleteConversationCalls { get; private set; }

    public int GetConversationMessagesCalls { get; private set; }

    /// <summary>Replaces the listing response entirely, for cursor and failure cases.</summary>
    public Func<int?, string?, CoachConversationPageDto?>? OnListConversations { get; set; }

    /// <summary>Replaces the message page response, for cursor and failure cases.</summary>
    public Func<string, int?, string?, CoachMessagePageDto?>? OnGetConversationMessages { get; set; }

    /// <summary>Replaces the turn submission, for failure, conflict and lost-response cases.</summary>
    public Func<string, CoachConversationTurnRequest, CoachTurnOperationDto>? OnSubmitConversationTurn { get; set; }

    /// <summary>Replaces the operation poll, for recovery cases.</summary>
    public Func<string, string, CoachTurnOperationDto?>? OnGetConversationOperation { get; set; }

    /// <summary>Replaces the update, for version-conflict cases.</summary>
    public Func<string, UpdateCoachConversationRequest, CoachConversationDto>? OnUpdateConversation { get; set; }

    /// <summary>Turns already run, keyed by idempotency key, so a replay is not a second turn.</summary>
    private readonly Dictionary<string, CoachTurnOperationDto> _operationsByIdempotencyKey =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, CoachTurnOperationDto> _operationsById =
        new(StringComparer.Ordinal);

    private long _sequence;

    public Task<CoachConversationDto> CreateConversationAsync(
        StartCoachConversationRequest request,
        CancellationToken cancellationToken = default)
    {
        CreateConversationCalls++;
        ConversationCreateRequests.Add(request);
        RequireDurable();

        var conversation = Conversation("conversation-" + (Conversations.Count + 1));
        Conversations.Add(conversation);
        ConversationMessages[conversation.ConversationId] = new List<CoachHistoryMessageDto>();
        return Task.FromResult(conversation);
    }

    public Task<CoachConversationPageDto?> ListConversationsAsync(
        int? limit = null,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        ListConversationCalls++;

        if (OnListConversations is { } hook)
        {
            return Task.FromResult(hook(limit, cursor));
        }

        // The flag-off shape: the route is simply not there, and the client reads that as
        // "no durable history" rather than as an error.
        if (!DurableHistoryAvailable)
        {
            return Task.FromResult<CoachConversationPageDto?>(null);
        }

        var ordered = Conversations
            .Where(c => OwnedByCaller(c.ConversationId))
            .OrderByDescending(c => c.UpdatedAtUtc)
            .ThenBy(c => c.ConversationId, StringComparer.Ordinal)
            .ToList();

        return Task.FromResult<CoachConversationPageDto?>(new CoachConversationPageDto
        {
            Items = ordered,
            NextCursor = null
        });
    }

    public Task<CoachConversationDto?> GetConversationAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Conversations.FirstOrDefault(c =>
            string.Equals(c.ConversationId, conversationId, StringComparison.Ordinal)
            && OwnedByCaller(conversationId)));

    public Task<CoachMessagePageDto?> GetConversationMessagesAsync(
        string conversationId,
        int? limit = null,
        string? before = null,
        CancellationToken cancellationToken = default)
    {
        GetConversationMessagesCalls++;
        MessagePageRequests.Add(conversationId);

        if (OnGetConversationMessages is { } hook)
        {
            return Task.FromResult(hook(conversationId, limit, before));
        }

        // A thread that is not the caller's answers exactly as one that never existed. That is
        // what the real route does, and it is what the client has to be able to survive.
        if (!OwnedByCaller(conversationId)
            || !ConversationMessages.TryGetValue(conversationId, out var all))
        {
            return Task.FromResult<CoachMessagePageDto?>(null);
        }

        var take = limit ?? 50;
        var ordered = all.OrderBy(m => m.Sequence).ToList();

        // "before" names a sequence: the page ends just under it. Newest page when absent.
        var window = before is null
            ? ordered
            : ordered.Where(m => m.Sequence < long.Parse(before)).ToList();

        var page = window.Skip(Math.Max(0, window.Count - take)).ToList();
        var hasOlder = window.Count > page.Count;

        return Task.FromResult<CoachMessagePageDto?>(new CoachMessagePageDto
        {
            ConversationId = conversationId,
            Items = page,
            PreviousCursor = hasOlder && page.Count > 0
                ? page[0].Sequence.ToString()
                : null,
            UnreadableCount = page.Count(m => !m.IsReadable)
        });
    }

    public Task<CoachConversationDto> UpdateConversationAsync(
        string conversationId,
        UpdateCoachConversationRequest request,
        CancellationToken cancellationToken = default)
    {
        ConversationUpdates.Add(request);

        if (OnUpdateConversation is { } hook)
        {
            return Task.FromResult(hook(conversationId, request));
        }

        var index = Conversations.FindIndex(c =>
            string.Equals(c.ConversationId, conversationId, StringComparison.Ordinal));

        if (index < 0)
        {
            throw new CoachApiException(
                System.Net.HttpStatusCode.NotFound, CoachProblemTypes.ConversationNotFound, null, null);
        }

        var current = Conversations[index];

        if (request.ExpectedStateVersion is { } expected && expected != current.StateVersion)
        {
            throw new CoachApiException(
                System.Net.HttpStatusCode.Conflict, CoachProblemTypes.ConversationStateConflict, null, null);
        }

        var updated = new CoachConversationDto
        {
            ConversationId = current.ConversationId,
            Title = request.Title ?? current.Title,
            TitleOrigin = request.Title is null ? current.TitleOrigin : CoachConversationTitleOrigin.Learner,
            TargetLanguageCode = current.TargetLanguageCode,
            CreatedAtUtc = current.CreatedAtUtc,
            UpdatedAtUtc = DateTime.UtcNow,
            HistoryStartsAtUtc = current.HistoryStartsAtUtc,
            MessageCount = current.MessageCount,
            StateVersion = current.StateVersion + 1,
            HasActiveCheckpoint = current.HasActiveCheckpoint,
            IsClosed = request.Close ?? current.IsClosed
        };

        Conversations[index] = updated;
        return Task.FromResult(updated);
    }

    public Task<CoachTurnOperationDto> SubmitConversationTurnAsync(
        string conversationId,
        CoachConversationTurnRequest request,
        CancellationToken cancellationToken = default)
    {
        SubmitConversationTurnCalls++;
        SubmittedConversationTurns.Add(request);

        // A replayed key returns the stored operation. This is the whole point of the key: a
        // retry after a lost response must not run the turn twice. A turn that *failed* is the
        // exception - there is no result to replay, and refusing to run it again would make the
        // retry affordance a lie.
        if (_operationsByIdempotencyKey.TryGetValue(request.IdempotencyKey, out var replay)
            && replay.State != CoachTurnOperationState.Failed)
        {
            return Task.FromResult(replay);
        }

        if (OnSubmitConversationTurn is { } hook)
        {
            var hooked = hook(conversationId, request);
            _operationsByIdempotencyKey[request.IdempotencyKey] = hooked;
            _operationsById[hooked.OperationId] = hooked;
            return Task.FromResult(hooked);
        }

        var learner = HistoryMessage(conversationId, CoachMessageRole.Learner, request.Turn.Text ?? string.Empty);
        var reply = HistoryMessage(conversationId, CoachMessageRole.Coach, "Sam replies.");

        var operation = new CoachTurnOperationDto
        {
            OperationId = request.OperationId,
            ConversationId = conversationId,
            State = CoachTurnOperationState.Completed,
            Result = CoachStateMachineTests.Turn(),
            Messages = new[] { learner, reply },
            FirstResponseSequence = learner.Sequence,
            LastResponseSequence = reply.Sequence,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

        _operationsByIdempotencyKey[request.IdempotencyKey] = operation;
        _operationsById[operation.OperationId] = operation;
        return Task.FromResult(operation);
    }

    public Task<CoachTurnOperationDto?> GetConversationOperationAsync(
        string conversationId,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        GetConversationOperationCalls++;

        if (OnGetConversationOperation is { } hook)
        {
            return Task.FromResult(hook(conversationId, operationId));
        }

        return Task.FromResult(_operationsById.TryGetValue(operationId, out var found) ? found : null);
    }

    public Task<CoachTurnOperationDto?> CancelConversationTurnAsync(
        string conversationId,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        CancelConversationTurnCalls++;

        if (!_operationsById.TryGetValue(operationId, out var operation))
        {
            return Task.FromResult<CoachTurnOperationDto?>(null);
        }

        var cancelled = new CoachTurnOperationDto
        {
            OperationId = operation.OperationId,
            ConversationId = operation.ConversationId,
            State = CoachTurnOperationState.Cancelled,
            CancelRequested = true,
            Result = operation.Result,
            Messages = operation.Messages,
            FirstResponseSequence = operation.FirstResponseSequence,
            LastResponseSequence = operation.LastResponseSequence,
            ErrorCode = operation.ErrorCode,
            CreatedAtUtc = operation.CreatedAtUtc,
            UpdatedAtUtc = DateTime.UtcNow
        };

        _operationsById[operationId] = cancelled;
        return Task.FromResult<CoachTurnOperationDto?>(cancelled);
    }

    public Task DeleteConversationAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        DeleteConversationCalls++;
        Conversations.RemoveAll(c =>
            string.Equals(c.ConversationId, conversationId, StringComparison.Ordinal));
        ConversationMessages.Remove(conversationId);
        return Task.CompletedTask;
    }

    public Task<Stream?> ExportConversationAsync(
        string conversationId,
        CoachExportFormat format = CoachExportFormat.Json,
        CancellationToken cancellationToken = default)
    {
        Exports.Add((conversationId, format));
        return Task.FromResult<Stream?>(new MemoryStream("exported"u8.ToArray()));
    }

    // ------------------------------------------------------------------ proposed changes

    /// <summary>The ledger this fake answers write reads and transitions from.</summary>
    public Dictionary<string, CoachWriteOperationDto> Writes { get; } =
        new(StringComparer.Ordinal);

    /// <summary>Every write route this fake was asked for, in order, as "verb operationId".</summary>
    public List<string> WriteCalls { get; } = new();

    /// <summary>The confirmation values that were sent back on a confirm request, in order.</summary>
    public List<string> SentConfirmations { get; } = new();

    /// <summary>Set to make one specific route refuse, so a failure path can be driven.</summary>
    public Func<string, string, CoachApiException?>? OnWriteRefusal { get; set; }

    /// <summary>
    /// Set to hold a route open, so two presses genuinely overlap.
    /// </summary>
    /// <remarks>
    /// A gate rather than a blocking call: blocking would stop the caller before it ever reached
    /// its first await, and the two presses would run one after the other — which is the thing
    /// the double-submit test is trying to rule out.
    /// </remarks>
    public TaskCompletionSource? WriteGate { get; set; }

    /// <summary>The confirmation this fake mints. Null makes the confirmation route answer 404.</summary>
    public Func<string, CoachWriteConfirmation?>? OnRequestConfirmation { get; set; }

    public Task<CoachWriteOperationDto?> GetWriteOperationAsync(
        string conversationId, string operationId, CancellationToken cancellationToken = default) =>
        WriteAsync("get", conversationId, operationId, transition: null);

    public Task<CoachWriteOperationDto?> AcceptWriteAsync(
        string conversationId, string operationId, CancellationToken cancellationToken = default) =>
        WriteAsync("accept", conversationId, operationId, Executed);

    public Task<CoachWriteOperationDto?> RejectWriteAsync(
        string conversationId, string operationId, CancellationToken cancellationToken = default) =>
        WriteAsync("reject", conversationId, operationId, current => With(current, CoachWriteStatus.Rejected));

    public Task<CoachWriteOperationDto?> UndoWriteAsync(
        string conversationId, string operationId, CancellationToken cancellationToken = default) =>
        WriteAsync("undo", conversationId, operationId, current => With(current, CoachWriteStatus.Undone));

    public Task<CoachWriteConfirmation?> RequestWriteConfirmationAsync(
        string conversationId, string operationId, CancellationToken cancellationToken = default)
    {
        WriteCalls.Add("confirmation " + operationId);

        if (OnWriteRefusal?.Invoke("confirmation", operationId) is { } refusal)
        {
            throw refusal;
        }

        var challenge = OnRequestConfirmation is null
            ? new CoachWriteConfirmation
            {
                OperationId = operationId,
                Value = "one-use-" + operationId,
                Summary = Writes.TryGetValue(operationId, out var found) ? found.Summary : string.Empty,
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(2)
            }
            : OnRequestConfirmation(operationId);

        return Task.FromResult(challenge);
    }

    public Task<CoachWriteOperationDto?> ConfirmWriteAsync(
        string conversationId,
        string operationId,
        CoachWriteConfirmation confirmation,
        CancellationToken cancellationToken = default)
    {
        SentConfirmations.Add(confirmation.Value);
        return WriteAsync("confirm", conversationId, operationId, Executed);
    }

    private async Task<CoachWriteOperationDto?> WriteAsync(
        string verb,
        string conversationId,
        string operationId,
        Func<CoachWriteOperationDto, CoachWriteOperationDto>? transition)
    {
        WriteCalls.Add($"{verb} {operationId}");

        if (WriteGate is { } gate)
        {
            await gate.Task.ConfigureAwait(false);
        }

        if (OnWriteRefusal?.Invoke(verb, operationId) is { } refusal)
        {
            throw refusal;
        }

        if (!Writes.TryGetValue(operationId, out var current)
            || !string.Equals(current.ConversationId, conversationId, StringComparison.Ordinal))
        {
            return null;
        }

        if (transition is not null)
        {
            current = transition(current);
            Writes[operationId] = current;
        }

        return current;
    }

    private static CoachWriteOperationDto Executed(CoachWriteOperationDto current) => With(
        current,
        CoachWriteStatus.Executed,
        new CoachWriteReceiptDto
        {
            OperationId = current.OperationId,
            ChangeKind = current.ChangeKind,
            RiskClass = current.RiskClass,
            Status = CoachWriteStatus.Executed,
            TargetKind = CoachWriteTargetKind.VocabularyWord,
            TargetId = "entity-1",
            Summary = "Applied: " + current.Summary,
            Lines = current.Lines,
            ExecutedAtUtc = DateTime.UtcNow,
            CanUndo = current.IsReversible,
            UndoExpiresAtUtc = current.IsReversible ? DateTime.UtcNow.AddMinutes(5) : null
        });

    private static CoachWriteOperationDto With(
        CoachWriteOperationDto current,
        CoachWriteStatus status,
        CoachWriteReceiptDto? receipt = null) => new()
        {
            OperationId = current.OperationId,
            ConversationId = current.ConversationId,
            TurnId = current.TurnId,
            MessageId = current.MessageId,
            ChangeKind = current.ChangeKind,
            RiskClass = current.RiskClass,
            Status = status,
            ApprovalMode = current.ApprovalMode,
            Summary = current.Summary,
            Lines = current.Lines,
            ExpiresAtUtc = current.ExpiresAtUtc,
            RequiresConfirmation = current.RequiresConfirmation,
            ConfirmationExpiresAtUtc = current.ConfirmationExpiresAtUtc,
            IsReversible = current.IsReversible,
            AlreadyExecuted = status == CoachWriteStatus.Executed,
            Receipt = receipt ?? current.Receipt
        };

    /// <summary>Builds a proposal, registers it in the ledger, and returns it.</summary>
    public CoachWriteOperationDto AddWrite(
        string conversationId,
        string operationId,
        bool requiresConfirmation = false,
        bool isReversible = true,
        CoachWriteStatus status = CoachWriteStatus.Proposed,
        CoachWriteChangeKind kind = CoachWriteChangeKind.VocabularyAdd,
        DateTime? expiresAtUtc = null,
        string? turnId = null,
        string? messageId = null)
    {
        var write = new CoachWriteOperationDto
        {
            OperationId = operationId,
            ConversationId = conversationId,
            TurnId = turnId,
            MessageId = messageId,
            ChangeKind = kind,
            RiskClass = requiresConfirmation
                ? CoachWriteRiskClass.WriteHard
                : CoachWriteRiskClass.WriteSoft,
            Status = status,
            ApprovalMode = requiresConfirmation ? "confirm" : "accept",
            Summary = "Add a word to your list",
            Lines = ["Term: one", "Meaning: two"],
            ExpiresAtUtc = expiresAtUtc ?? DateTime.UtcNow.AddMinutes(30),
            RequiresConfirmation = requiresConfirmation,
            IsReversible = isReversible
        };

        Writes[operationId] = write;
        return write;
    }

    /// <summary>Adds a durable message to a conversation and returns it.</summary>
    /// <param name="kind">
    /// How the message renders. A notice is the case reporting was extended to cover, so a test
    /// has to be able to seed one that is indistinguishable from what the server writes — right
    /// down to the reason code, which is what separates a response the learner can flag from a
    /// system marker they cannot.
    /// </param>
    /// <param name="noticeReasonCode">
    /// The server's closed-vocabulary code for a notice. Left null for other kinds. Seeding a
    /// notice without one reproduces a malformed row, which is deliberately possible.
    /// </param>
    public CoachHistoryMessageDto Seed(
        string conversationId,
        CoachMessageRole role,
        string text,
        CoachAnswerDto? answer = null,
        CoachHistoryReceiptDto? receipt = null,
        CoachHistorySuggestionDto? suggestion = null,
        bool isReadable = true,
        CoachWriteOperationDto? writeOperation = null,
        CoachMessageKind kind = CoachMessageKind.Text,
        string? noticeReasonCode = null)
    {
        var message = HistoryMessage(
            conversationId, role, text, answer, receipt, suggestion, isReadable, writeOperation,
            kind, noticeReasonCode);
        return message;
    }

    private CoachHistoryMessageDto HistoryMessage(
        string conversationId,
        CoachMessageRole role,
        string text,
        CoachAnswerDto? answer = null,
        CoachHistoryReceiptDto? receipt = null,
        CoachHistorySuggestionDto? suggestion = null,
        bool isReadable = true,
        CoachWriteOperationDto? writeOperation = null,
        CoachMessageKind kind = CoachMessageKind.Text,
        string? noticeReasonCode = null)
    {
        var sequence = ++_sequence;

        var message = new CoachHistoryMessageDto
        {
            Message = new CoachMessageDto
            {
                MessageId = "m-" + sequence,
                Role = role,
                Kind = kind,
                Text = text,
                // Deliberately not "now": these stamps are what the client is supposed to adopt,
                // so a test can tell them apart from a local capture time.
                CreatedAtUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc).AddSeconds(sequence)
            },
            Sequence = sequence,
            Answer = answer,
            Receipt = receipt,
            Suggestion = suggestion,
            NoticeReasonCode = noticeReasonCode,
            WriteOperation = writeOperation,
            IsReadable = isReadable
        };

        if (!ConversationMessages.TryGetValue(conversationId, out var list))
        {
            list = new List<CoachHistoryMessageDto>();
            ConversationMessages[conversationId] = list;
        }

        list.Add(message);
        return message;
    }

    /// <summary>Builds a conversation row and registers it in the ledger.</summary>
    public CoachConversationDto AddConversation(
        string conversationId,
        DateTime? updatedAtUtc = null,
        bool isClosed = false,
        string? title = null,
        CoachConversationTitleOrigin titleOrigin = CoachConversationTitleOrigin.Generated,
        DateTime? historyStartsAtUtc = null,
        string? owner = null)
    {
        var conversation = Conversation(conversationId, updatedAtUtc, isClosed, title, titleOrigin, historyStartsAtUtc);
        Conversations.Add(conversation);
        ConversationMessages.TryAdd(conversationId, new List<CoachHistoryMessageDto>());

        if (owner is not null)
        {
            ConversationOwners[conversationId] = owner;
        }

        return conversation;
    }

    private static CoachConversationDto Conversation(
        string conversationId,
        DateTime? updatedAtUtc = null,
        bool isClosed = false,
        string? title = null,
        CoachConversationTitleOrigin titleOrigin = CoachConversationTitleOrigin.Generated,
        DateTime? historyStartsAtUtc = null) => new()
        {
            ConversationId = conversationId,
            Title = title ?? string.Empty,
            TitleOrigin = titleOrigin,
            TargetLanguageCode = "ko",
            CreatedAtUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = updatedAtUtc ?? new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            HistoryStartsAtUtc = historyStartsAtUtc ?? new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            MessageCount = 0,
            StateVersion = 1,
            HasActiveCheckpoint = false,
            IsClosed = isClosed
        };

    private void RequireDurable()
    {
        if (!DurableHistoryAvailable)
        {
            throw new CoachApiException(
                System.Net.HttpStatusCode.NotFound, CoachProblemTypes.Unavailable, null, null);
        }
    }

    /// <summary>
    /// Mirrors the landed server behavior: a session read always answers Messages=[] and
    /// Evidence=[] because the server keeps no plaintext transcript.
    /// </summary>
    public static CoachSessionResponse Session(
        string sessionId = "session-1",
        CoachSessionStatus status = CoachSessionStatus.Active,
        PendingCoachSuggestionDto? suggestion = null,
        IReadOnlyList<CoachRevisionDto>? revisions = null,
        IReadOnlyList<CoachMessageDto>? messages = null) => new()
        {
            SessionId = sessionId,
            Status = status,
            Messages = messages ?? Array.Empty<CoachMessageDto>(),
            Evidence = Array.Empty<CoachEvidenceDto>(),
            Revisions = revisions ?? Array.Empty<CoachRevisionDto>(),
            ActiveConstraints = CoachStateMachineTests.Constraints(),
            PlanState = CoachStateMachineTests.PlanState(),
            PendingSuggestion = suggestion,
            ClarificationsRemaining = 2,
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(24)
        };

    // ==== what Sam remembers ================================================
    // A small ledger mirroring the server's: facts keyed by id, each carrying a version that a
    // write must match. Tests drive conflicts and outages through the override hooks rather than
    // by reaching into the lists, so the call counters stay honest.

    public List<CoachMemoryFactDto> ActiveFacts { get; } = new();

    public List<CoachMemoryFactDto> CandidateFacts { get; } = new();

    public int ListActiveMemoriesCalls { get; private set; }

    public int ListMemoryCandidatesCalls { get; private set; }

    public int ApproveMemoryCalls { get; private set; }

    public int RejectMemoryCalls { get; private set; }

    public int EditMemoryCalls { get; private set; }

    public int ForgetMemoryCalls { get; private set; }

    public int ForgetAllMemoriesCalls { get; private set; }

    /// <summary>The version each write was told to expect, in call order.</summary>
    public List<int> ObservedExpectedVersions { get; } = new();

    /// <summary>The edited values each write carried, in call order. Null where none was sent.</summary>
    public List<CoachMemoryValueDto?> ObservedEditedValues { get; } = new();

    public Func<CoachMemoryPageDto?>? OnListActiveMemories { get; set; }

    public Func<CoachMemoryPageDto?>? OnListMemoryCandidates { get; set; }

    public Func<string, CoachMemoryApproveRequest, CoachMemoryFactDto?>? OnApproveMemory { get; set; }

    public Action<string, CoachMemoryRejectRequest>? OnRejectMemory { get; set; }

    public Func<string, CoachMemoryEditRequest, CoachMemoryFactDto?>? OnEditMemory { get; set; }

    public Action<(string FactId, int ExpectedVersion)>? OnForgetMemory { get; set; }

    public Func<CoachMemoryForgetAllResponse?>? OnForgetAllMemories { get; set; }

    public Task<CoachMemoryPageDto?> ListActiveMemoriesAsync(
        int? pageSize = null,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        ListActiveMemoriesCalls++;

        return Task.FromResult(OnListActiveMemories is { } hook
            ? hook()
            : new CoachMemoryPageDto(ActiveFacts.ToArray(), null));
    }

    public Task<CoachMemoryPageDto?> ListMemoryCandidatesAsync(
        int? pageSize = null,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        ListMemoryCandidatesCalls++;

        return Task.FromResult(OnListMemoryCandidates is { } hook
            ? hook()
            : new CoachMemoryPageDto(CandidateFacts.ToArray(), null));
    }

    public Task<CoachMemoryFactDto?> ApproveMemoryAsync(
        string factId,
        CoachMemoryApproveRequest request,
        CancellationToken cancellationToken = default)
    {
        ApproveMemoryCalls++;
        ObservedExpectedVersions.Add(request.ExpectedVersion);
        ObservedEditedValues.Add(request.EditedValue);

        if (OnApproveMemory is { } hook)
        {
            return Task.FromResult(hook(factId, request));
        }

        var candidate = CandidateFacts.FirstOrDefault(f => f.Id == factId);

        if (candidate is null)
        {
            throw Gone();
        }

        CandidateFacts.Remove(candidate);

        var approved = candidate with
        {
            Status = CoachMemoryStatus.Active,
            Value = request.EditedValue ?? candidate.Value,
            DisplayText = request.EditedValue?.StudyGoalText ?? candidate.DisplayText,
            Provenance = CoachMemoryProvenance.UserConfirmed,
            ConfirmedAtUtc = DateTime.UtcNow,
            Version = candidate.Version + 1
        };

        ActiveFacts.Add(approved);

        return Task.FromResult<CoachMemoryFactDto?>(approved);
    }

    public Task RejectMemoryAsync(
        string factId,
        CoachMemoryRejectRequest request,
        CancellationToken cancellationToken = default)
    {
        RejectMemoryCalls++;
        ObservedExpectedVersions.Add(request.ExpectedVersion);
        ObservedEditedValues.Add(null);

        if (OnRejectMemory is { } hook)
        {
            hook(factId, request);
            return Task.CompletedTask;
        }

        CandidateFacts.RemoveAll(f => f.Id == factId);

        return Task.CompletedTask;
    }

    public Task<CoachMemoryFactDto?> EditMemoryAsync(
        string factId,
        CoachMemoryEditRequest request,
        CancellationToken cancellationToken = default)
    {
        EditMemoryCalls++;
        ObservedExpectedVersions.Add(request.ExpectedVersion);
        ObservedEditedValues.Add(request.Value);

        if (OnEditMemory is { } hook)
        {
            return Task.FromResult(hook(factId, request));
        }

        var index = ActiveFacts.FindIndex(f => f.Id == factId);

        if (index < 0)
        {
            throw Gone();
        }

        var edited = ActiveFacts[index] with
        {
            Value = request.Value,
            DisplayText = request.Value.StudyGoalText ?? ActiveFacts[index].DisplayText,
            UpdatedAtUtc = DateTime.UtcNow,
            Version = ActiveFacts[index].Version + 1
        };

        ActiveFacts[index] = edited;

        return Task.FromResult<CoachMemoryFactDto?>(edited);
    }

    public Task ForgetMemoryAsync(string factId, int expectedVersion, CancellationToken cancellationToken = default)
    {
        ForgetMemoryCalls++;

        if (OnForgetMemory is { } hook)
        {
            hook((factId, expectedVersion));
            return Task.CompletedTask;
        }

        ActiveFacts.RemoveAll(f => f.Id == factId);
        CandidateFacts.RemoveAll(f => f.Id == factId);

        return Task.CompletedTask;
    }

    public Task<CoachMemoryForgetAllResponse?> ForgetAllMemoriesAsync(
        CancellationToken cancellationToken = default)
    {
        ForgetAllMemoriesCalls++;

        if (OnForgetAllMemories is { } hook)
        {
            return Task.FromResult(hook());
        }

        var forgotten = ActiveFacts.Count + CandidateFacts.Count;
        ActiveFacts.Clear();
        CandidateFacts.Clear();

        return Task.FromResult<CoachMemoryForgetAllResponse?>(new CoachMemoryForgetAllResponse(forgotten));
    }

    // ------------------------------------------------------------------ response reports

    /// <summary>
    /// Which responses this fake server says are already reported.
    /// </summary>
    /// <remarks>
    /// A set rather than a flag, because the interesting cases are per-message: one reported
    /// response beside an unreported one is exactly the state a reload has to reproduce.
    /// </remarks>
    public HashSet<string> ReportedResponses { get; } = new(StringComparer.Ordinal);

    /// <summary>Set to null to make the report routes answer 404 — reporting switched off.</summary>
    public bool IsReportingAvailable { get; set; } = true;

    /// <summary>Set to make the report route fail rather than answer.</summary>
    public Func<string, CoachResponseReportResponse?>? OnReportResponse { get; set; }

    /// <summary>Holds the report in flight until the test releases it.</summary>
    /// <remarks>
    /// A gate rather than a blocking call, for the reason <see cref="WriteGate"/> gives and one
    /// more: the caller here is a Blazor event handler, so blocking it would block the renderer's
    /// dispatcher — and the test that wants to look at the in-flight markup needs the dispatcher to
    /// look at anything.
    /// </remarks>
    public TaskCompletionSource? ReportGate { get; set; }

    /// <summary>Every report this fake accepted, in order.</summary>
    public List<(string ConversationId, string MessageId, CoachResponseReportReason Reason)> Reports { get; } = new();

    public int ReportedResponsesCalls { get; private set; }

    public Task<CoachReportedResponsesDto?> GetReportedResponsesAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        ReportedResponsesCalls++;

        if (!IsReportingAvailable)
        {
            return Task.FromResult<CoachReportedResponsesDto?>(null);
        }

        return Task.FromResult<CoachReportedResponsesDto?>(
            new CoachReportedResponsesDto { MessageIds = ReportedResponses.ToList() });
    }

    public async Task<CoachResponseReportResponse?> ReportResponseAsync(
        string conversationId,
        string messageId,
        CoachResponseReportRequest request,
        CancellationToken cancellationToken = default)
    {
        if (ReportGate is { } gate)
        {
            await gate.Task.ConfigureAwait(false);
        }

        if (OnReportResponse is not null)
        {
            var custom = OnReportResponse(messageId);
            if (custom is not null)
            {
                ReportedResponses.Add(custom.MessageId);
            }

            return custom;
        }

        if (!IsReportingAvailable)
        {
            return null;
        }

        var repeat = !ReportedResponses.Add(messageId);
        Reports.Add((conversationId, messageId, request.Reason));

        return new CoachResponseReportResponse
        {
            MessageId = messageId,
            Reason = request.Reason,
            State = repeat ? CoachResponseReportState.AlreadyReported : CoachResponseReportState.Recorded,
            ReportedAtUtc = new DateTime(2026, 8, 21, 3, 0, 0, DateTimeKind.Utc)
        };
    }

    /// <summary>
    /// The 404 the memory routes answer. Disabled, missing, and somebody else's data are all the
    /// same status by design, so the fake has exactly one of these.
    /// </summary>
    public static CoachApiException Gone() => new(
        System.Net.HttpStatusCode.NotFound,
        problemType: null,
        title: "Not found.",
        detail: null);

    /// <summary>A fact with sane defaults. Tests override only what they are asserting on.</summary>
    public static CoachMemoryFactDto Fact(
        string id = "fact-1",
        CoachMemoryKind kind = CoachMemoryKind.PersistentStudyGoal,
        CoachMemoryStatus status = CoachMemoryStatus.Active,
        CoachMemoryScope scope = CoachMemoryScope.TargetLanguage,
        string? targetLanguageCode = "ko",
        string displayText = "Wants to order food in Korean",
        CoachMemoryProvenance provenance = CoachMemoryProvenance.UserExplicit,
        int version = 3,
        CoachMemoryValueDto? value = null) => new(
            id,
            kind,
            status,
            scope,
            targetLanguageCode,
            value ?? new CoachMemoryValueDto { Kind = kind, StudyGoalText = displayText },
            displayText,
            provenance,
            EvidenceCount: 1,
            CreatedAtUtc: new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc: new DateTime(2026, 3, 2, 12, 0, 0, DateTimeKind.Utc),
            ConfirmedAtUtc: new DateTime(2026, 3, 2, 12, 0, 0, DateTimeKind.Utc),
            LastUsedAtUtc: null,
            ExpiresAtUtc: null,
            SupersedesId: null,
            Version: version);

}
