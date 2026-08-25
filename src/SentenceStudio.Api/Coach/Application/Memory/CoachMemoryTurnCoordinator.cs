using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SentenceStudio.Api.Coach.Memory;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Coach.Telemetry;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;
using SentenceStudio.Contracts.LearnerMemory;

namespace SentenceStudio.Api.Coach.Application.Memory;

/// <summary>
/// The session lane's single entry point to learner memory for one turn.
/// </summary>
/// <remarks>
/// <para>
/// Two jobs, both narrow. Before the model runs, build the untrusted context block. After the
/// model answers, screen any proposal and record a candidate. Nothing else in the session service
/// touches the memory store, so there is exactly one place to look for what memory can and cannot
/// do to a turn.
/// </para>
/// <para>
/// Every failure degrades to "no memory this turn". A memory outage must not fail a language
/// question: the learner asked about grammar, and answering without a saved preference is a
/// slightly worse answer, while returning an error is no answer at all.
/// </para>
/// </remarks>
public sealed class CoachMemoryTurnCoordinator
{
    private readonly ICoachMemoryContextSelector _selector;
    private readonly ICoachMemoryStore _store;
    private readonly IOptions<CoachMemoryOptions> _options;
    private readonly ILogger<CoachMemoryTurnCoordinator> _logger;

    public CoachMemoryTurnCoordinator(
        ICoachMemoryContextSelector selector,
        ICoachMemoryStore store,
        IOptions<CoachMemoryOptions> options,
        ILogger<CoachMemoryTurnCoordinator> logger)
    {
        _selector = selector;
        _store = store;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Whether learner memory is switched on, from <c>Coach:Memory:Enabled</c>.
    /// </summary>
    /// <remarks>
    /// Snapshotted the same way the selector and the store snapshot it, deliberately. If this
    /// read were live while they were not, availability could say yes on a host where the store
    /// still refuses every write, and the client would be told to show a surface that silently
    /// does nothing. Reporting the same value they act on is worth more than reacting sooner.
    /// </remarks>
    /// <remarks>
    /// This only tells a client what to show. The store gates every one of its own methods on the
    /// same option, so nothing here is what keeps memory from being written.
    /// </remarks>
    public bool IsEnabled => _options.Value.Enabled;

    /// <summary>
    /// Selects and renders the memory block for one turn, or null when there is nothing to send.
    /// </summary>
    /// <remarks>
    /// The owner is built from the trusted profile id the caller already authenticated. It is
    /// never derived from the request body, the conversation, or anything the model produced.
    /// </remarks>
    public async Task<string?> BuildContextBlockAsync(
        string userProfileId,
        string? targetLanguageCode,
        CoachConstraintSetDto? constraints,
        string? pendingSuggestionId,
        string? learnerText,
        CancellationToken cancellationToken = default)
    {
        if (!CoachOwner.TryCreate(userProfileId, tenantId: null, out var owner))
        {
            return null;
        }

        try
        {
            var request = new CoachMemoryContextRequest(
                owner,
                targetLanguageCode,
                CoachMemoryTurnContext.Categorize(constraints, pendingSuggestionId),
                CoachMemoryTurnContext.ExcludedKinds(learnerText, constraints));

            var selection = await _selector.SelectAsync(request, cancellationToken).ConfigureAwait(false);

            if (selection.Outcome == CoachMemoryContextOutcome.StoreUnavailable)
            {
                // Content-free by design: the outcome and nothing else. The whole point of the
                // degraded path is that a memory failure leaves no trace of what was in memory.
                _logger.LogWarning("[Coach] Memory context unavailable for this turn. The turn continues without it.");
                return null;
            }

            return CoachMemoryPromptFormatter.Format(selection);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The selector already fails soft, so reaching here means something unexpected. Still
            // degrade rather than fail the turn, for the same reason.
            //
            // The exception object is deliberately not passed to the logger. This path runs with
            // the learner's turn text and the selected memory values in scope, and a store or
            // serialization failure routinely quotes the offending value in its message, in an
            // inner exception, or in Data — all of which LogWarning(ex, ...) writes through
            // Exception.ToString(). Only the sanitizer's allow-listed shape facts are logged.
            // See CoachExceptionSanitizer.
            var facts = CoachExceptionSanitizer.Describe(ex);
            _logger.LogWarning(
                "[Coach] Memory context could not be built. The turn continues without it. " +
                "Category={FailureCategory} ProviderStatus={ProviderStatus} " +
                "ProviderCode={ProviderErrorCode} InnerDepth={InnerDepth}",
                facts.Category,
                facts.ProviderStatus,
                facts.ProviderErrorCode,
                facts.InnerDepth);
            return null;
        }
    }

    /// <summary>
    /// Screens a model proposal and records an inert candidate, or returns null.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A candidate is not active and is not injected. It exists so the learner can be asked. The
    /// returned DTO is the whole of what the client needs to offer Accept, Edit, Only this chat,
    /// and Not now: an opaque fact id, a version to echo back, and the normalized value.
    /// </para>
    /// <para>
    /// "Only this chat" is a client-side decision to simply not approve — there is no third state
    /// to store, because a preference that applies to one conversation is what the learner already
    /// said in that conversation.
    /// </para>
    /// </remarks>
    public async Task<CoachMemoryFactDto?> TryRecordCandidateAsync(
        string userProfileId,
        CoachMemoryProposalIntent? proposal,
        string? learnerText,
        string? targetLanguageCode,
        string? sourceConversationId,
        string? sourceMessageId,
        CancellationToken cancellationToken = default)
    {
        if (proposal is null || !CoachOwner.TryCreate(userProfileId, tenantId: null, out var owner))
        {
            return null;
        }

        var screening = CoachMemoryProposalGate.Screen(proposal, learnerText, targetLanguageCode);
        if (!screening.IsAccepted)
        {
            // Refusal reason only. Never the proposed value, never the evidence, never the text
            // the learner sent: a refused proposal is still learner content.
            _logger.LogInformation(
                "[Coach] Memory proposal refused before any candidate was created. Reason={Reason}",
                screening.Refusal);
            return null;
        }

        try
        {
            var request = new CreateCoachMemoryCandidateRequest(
                screening.Value!,
                screening.Scope,
                screening.Scope == CoachMemoryScope.Global ? null : targetLanguageCode,
                learnerText!,
                screening.EvidenceSpan,
                sourceConversationId,
                sourceMessageId);

            var result = await _store.CreateCandidateAsync(owner, request, cancellationToken).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                _logger.LogInformation(
                    "[Coach] Memory candidate was not recorded. Status={Status}",
                    result.Status);
                return null;
            }

            return result.Fact!.ToDto();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A failed candidate must not fail the turn. The learner asked a question and got an
            // answer; the offer to remember something is strictly additional.
            //
            // The exception object is deliberately not passed to the logger. The screened value,
            // the evidence span, and the learner's own message are all in scope here, and a store
            // failure quotes the offending value often enough that LogWarning(ex, ...) would put
            // learner text in the sink. Shape facts only. See CoachExceptionSanitizer.
            var facts = CoachExceptionSanitizer.Describe(ex);
            _logger.LogWarning(
                "[Coach] Memory candidate could not be recorded. The turn is unaffected. " +
                "Category={FailureCategory} ProviderStatus={ProviderStatus} " +
                "ProviderCode={ProviderErrorCode} InnerDepth={InnerDepth}",
                facts.Category,
                facts.ProviderStatus,
                facts.ProviderErrorCode,
                facts.InnerDepth);
            return null;
        }
    }
}
