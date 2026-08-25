using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SentenceStudio.Api.Coach.Application.Memory;
using SentenceStudio.Api.Coach.Endpoints;
using SentenceStudio.Api.Coach.Memory;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// Proves the coach paths that catch a failure never hand the exception object to the logger.
/// </summary>
/// <remarks>
/// <para>
/// <c>ILogger.LogWarning(ex, ...)</c> writes <see cref="Exception.ToString"/>, which concatenates
/// the message, the whole inner chain, and <see cref="Exception.Data"/>. On a coach path those
/// carry the prompt, the learner's own words, the model's output, and the value a proposal was
/// about. A structured sink then indexes all of it.
/// </para>
/// <para>
/// So each test plants a distinctive sentinel in every place an exception can carry text and
/// asserts the sentinel appears in <b>no</b> part of the emitted record — not the rendered
/// message, not a structured state value, and not the exception field — while the safe facts
/// (category, provider status, provider code) are still present. Asserting only on the rendered
/// message would pass while a structured exporter leaked everything.
/// </para>
/// </remarks>
public class CoachSanitizedLoggingTests
{
    private const string Sentinel = "LEARNER-SENTINEL-은는-9f1c";

    private static CoachConstraintSetDto NewConstraints() => new()
    {
        AvailableMinutes = 20,
        AudioAllowed = true,
        SpeechAllowed = true,
        TypingAllowed = true,
        EnergyLevel = CoachEnergyLevel.Normal
    };

    // ------------------------------------------------------------------
    // CoachMemoryTurnCoordinator
    // ------------------------------------------------------------------

    [Fact]
    public async Task BuildContextBlock_WhenTheSelectorThrows_LogsNoExceptionText()
    {
        var logs = new CapturingLoggerProvider();
        var coordinator = new CoachMemoryTurnCoordinator(
            new ThrowingSelector(LeakyException()),
            new UnusedMemoryStore(),
            Options.Create(new CoachMemoryOptions { Enabled = true }),
            logs.CreateLogger<CoachMemoryTurnCoordinator>());

        var block = await coordinator.BuildContextBlockAsync(
            "profile-1", "ko", NewConstraints(), pendingSuggestionId: null, learnerText: Sentinel);

        block.Should().BeNull("the turn degrades rather than failing");

        var entry = logs.Single();
        entry.AllText().Should().NotContainMatch($"*{Sentinel}*");
        entry.Exception.Should().BeNull("the exception object must never reach the logger");
        entry.StateValue("FailureCategory").Should().Be("invalid_operation");
        entry.StateValue("ProviderStatus").Should().Be("429");
        entry.StateValue("ProviderErrorCode").Should().Be("content_filter");
    }

    [Fact]
    public async Task TryRecordCandidate_WhenTheStoreThrows_LogsNoExceptionText()
    {
        var logs = new CapturingLoggerProvider();
        var coordinator = new CoachMemoryTurnCoordinator(
            new UnusedSelector(),
            new ThrowingMemoryStore(LeakyException()),
            Options.Create(new CoachMemoryOptions { Enabled = true }),
            logs.CreateLogger<CoachMemoryTurnCoordinator>());

        var proposal = new CoachMemoryProposalIntent
        {
            Kind = CoachProposedMemoryKind.PersistentStudyGoal,
            Scope = CoachProposedMemoryScope.TargetLanguage,
            StudyGoalText = Sentinel,
            EvidenceSpan = Sentinel
        };

        var fact = await coordinator.TryRecordCandidateAsync(
            "profile-1",
            proposal,
            learnerText: $"please remember {Sentinel}",
            targetLanguageCode: "ko",
            sourceConversationId: "conv-1",
            sourceMessageId: "msg-1");

        fact.Should().BeNull("a failed candidate must not fail the turn");

        // The screening gate may refuse the proposal before the store is reached; either way, no
        // entry may carry the sentinel, and if the store was reached the record must be shaped.
        logs.Entries.Should().NotBeEmpty();
        foreach (var entry in logs.Entries)
        {
            entry.AllText().Should().NotContainMatch($"*{Sentinel}*");
            entry.Exception.Should().BeNull();
        }
    }

    // ------------------------------------------------------------------
    // CoachEndpointExecution.LogFailure — the shared route failure writer used by the
    // hand-rolled availability / cancel / delete handlers as well as ExecuteAsync.
    // ------------------------------------------------------------------

    [Fact]
    public void LogFailure_WritesShapeOnly()
    {
        var logs = new CapturingLoggerProvider();

        CoachEndpointExecution.LogFailure(logs, "GET /api/v1/coach/availability", LeakyException());

        var entry = logs.Single();
        entry.Level.Should().Be(LogLevel.Error);
        entry.AllText().Should().NotContainMatch($"*{Sentinel}*");
        entry.Exception.Should().BeNull();
        entry.StateValue("Route").Should().Be("GET /api/v1/coach/availability");
        entry.StateValue("FailureCategory").Should().Be("invalid_operation");
        entry.StateValue("ProviderStatus").Should().Be("429");
        entry.StateValue("ProviderErrorCode").Should().Be("content_filter");
    }

    [Fact]
    public async Task ExecuteAsync_WhenTheOperationThrows_LogsNoExceptionText()
    {
        var logs = new CapturingLoggerProvider();

        var result = await CoachEndpointExecution.ExecuteAsync<string>(
            () => throw LeakyException(),
            logs,
            "POST /api/v1/coach/sessions/{id}/turns");

        result.Should().NotBeNull();

        var entry = logs.Single();
        entry.AllText().Should().NotContainMatch($"*{Sentinel}*");
        entry.Exception.Should().BeNull();
        entry.StateValue("FailureCategory").Should().Be("invalid_operation");
    }

    /// <summary>
    /// An exception shaped like a real provider failure: the learner's text is echoed in the
    /// message, again in an inner exception, and again in Data, and the type carries the
    /// status/code members the sanitizer is allowed to forward.
    /// </summary>
    private static Exception LeakyException()
    {
        var inner = new InvalidOperationException($"inner echo of the prompt: {Sentinel}");
        inner.Data["request_body"] = Sentinel;

        var outer = new LeakyProviderException(
            $"The response was filtered because of the prompt: {Sentinel}",
            inner);
        outer.Data["prompt"] = Sentinel;
        return outer;
    }

    private sealed class LeakyProviderException(string message, Exception inner)
        : InvalidOperationException(message, inner)
    {
        public int Status => 429;
        public string ErrorCode => "content_filter";
    }

    // ------------------------------------------------------------------
    // Doubles
    // ------------------------------------------------------------------

    private sealed class ThrowingSelector(Exception failure) : ICoachMemoryContextSelector
    {
        public Task<CoachMemoryContextResult> SelectAsync(
            CoachMemoryContextRequest request, CancellationToken cancellationToken = default) => throw failure;
    }

    private sealed class UnusedSelector : ICoachMemoryContextSelector
    {
        public Task<CoachMemoryContextResult> SelectAsync(
            CoachMemoryContextRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The selector is not part of this path.");
    }

    private class UnusedMemoryStore : ICoachMemoryStore
    {
        public virtual Task<CoachMemoryResult> CreateCandidateAsync(CoachOwner owner, CreateCoachMemoryCandidateRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CoachMemoryPage> ListAsync(CoachOwner owner, CoachMemoryListFilter filter, int? pageSize = null, string? cursor = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CoachMemoryResult> GetAsync(CoachOwner owner, string factId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CoachMemoryResult> ApproveAsync(CoachOwner owner, string factId, int expectedVersion, CoachMemoryStoredValue? editedValue = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CoachMemoryStatusCode> RejectAsync(CoachOwner owner, string factId, int expectedVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CoachMemoryResult> EditActiveAsync(CoachOwner owner, string factId, int expectedVersion, CoachMemoryStoredValue value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CoachMemoryStatusCode> ForgetAsync(CoachOwner owner, string factId, int expectedVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CoachMemoryForgetAllResult> ForgetAllAsync(CoachOwner owner, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CoachMemoryFactRecord>> ListEligibleForContextAsync(CoachOwner owner, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> MarkUsedAsync(CoachOwner owner, IReadOnlyCollection<string> factIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> DeleteForSourceConversationAsync(CoachOwner owner, string conversationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> DeleteAllForOwnerAsync(CoachOwner owner, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ThrowingMemoryStore(Exception failure) : UnusedMemoryStore
    {
        public override Task<CoachMemoryResult> CreateCandidateAsync(
            CoachOwner owner, CreateCoachMemoryCandidateRequest request, CancellationToken cancellationToken = default) =>
            throw failure;
    }
}

/// <summary>One captured record: level, rendered message, structured state, exception field.</summary>
internal sealed record CapturedCoachLog(
    LogLevel Level,
    string Message,
    IReadOnlyList<KeyValuePair<string, string?>> State,
    string? Exception)
{
    /// <summary>Every string a sink could render, index, or export for this record.</summary>
    public IEnumerable<string> AllText()
    {
        yield return Message;
        foreach (var (key, value) in State)
        {
            yield return key;
            if (value is not null)
            {
                yield return value;
            }
        }

        if (Exception is not null)
        {
            yield return Exception;
        }
    }

    public string? StateValue(string key) =>
        State.FirstOrDefault(pair => string.Equals(pair.Key, key, StringComparison.Ordinal)).Value;
}

/// <summary>
/// An <see cref="ILoggerFactory"/> that records the rendered message AND the structured state of
/// every call, so a privacy assertion covers what a structured exporter would emit rather than
/// only what a console formatter would print.
/// </summary>
internal sealed class CapturingLoggerProvider : ILoggerFactory
{
    private readonly ConcurrentQueue<CapturedCoachLog> _entries = new();

    public IReadOnlyList<CapturedCoachLog> Entries => _entries.ToArray();

    public CapturedCoachLog Single() => Entries.Should().ContainSingle().Subject;

    public ILogger<T> CreateLogger<T>() => new Logger<T>(_entries);

    public ILogger CreateLogger(string categoryName) => new Logger<object>(_entries);

    public void AddProvider(ILoggerProvider provider) { }

    public void Dispose() { }

    private sealed class Logger<T>(ConcurrentQueue<CapturedCoachLog> entries) : ILogger<T>
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var pairs = new List<KeyValuePair<string, string?>>();
            if (state is IReadOnlyList<KeyValuePair<string, object?>> structured)
            {
                foreach (var pair in structured)
                {
                    pairs.Add(new KeyValuePair<string, string?>(pair.Key, pair.Value?.ToString()));
                }
            }

            entries.Enqueue(new CapturedCoachLog(
                logLevel,
                formatter(state, exception),
                pairs,
                exception?.ToString()));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
