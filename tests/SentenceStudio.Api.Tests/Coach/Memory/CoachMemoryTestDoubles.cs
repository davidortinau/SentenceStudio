using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Api.Coach.Memory;
using SentenceStudio.Api.Coach.Persistence;

namespace SentenceStudio.Api.Tests.Coach.Memory;

/// <summary>
/// Hands the checkpoint rotator the harness's single session store.
/// </summary>
/// <remarks>
/// The rotator is a singleton that opens its own scope, because in production the session store
/// is scoped and a change notification arrives outside any request. The harness has exactly one
/// store over one in-memory connection, so the "scope" here is that store. Keeping the rotator's
/// real shape — resolve through a scope factory — means the test exercises the production code
/// path rather than a constructor overload that only tests use.
/// </remarks>
internal sealed class SingleInstanceScopeFactory : IServiceScopeFactory, IServiceScope, IServiceProvider
{
    private readonly ICoachSessionStore _sessions;

    public SingleInstanceScopeFactory(ICoachSessionStore sessions) => _sessions = sessions;

    public IServiceScope CreateScope() => this;

    public IServiceProvider ServiceProvider => this;

    public object? GetService(Type serviceType) =>
        serviceType == typeof(ICoachSessionStore) ? _sessions : null;

    public void Dispose()
    {
        // The harness owns the store's lifetime.
    }
}

/// <summary>
/// Wraps the real context selector so a test can see exactly what the session lane asked for,
/// and can make the store look unavailable without tearing down the database.
/// </summary>
/// <remarks>
/// The recorded request is the evidence for the precedence rules: that the owner came from the
/// trusted scope, that the language came from the profile, that the category was derived from
/// application state, and that a kind the learner overrode in this very message was excluded
/// before selection rather than filtered after it.
/// </remarks>
internal sealed class RecordingMemoryContextSelector : ICoachMemoryContextSelector
{
    private readonly ICoachMemoryContextSelector _inner;

    public RecordingMemoryContextSelector(ICoachMemoryContextSelector inner) => _inner = inner;

    /// <summary>Every request the session lane has made, oldest first.</summary>
    public List<CoachMemoryContextRequest> Requests { get; } = [];

    /// <summary>The last result handed back to the session lane.</summary>
    public CoachMemoryContextResult? LastResult { get; private set; }

    /// <summary>When set, the selector reports the store as unavailable instead of selecting.</summary>
    public bool SimulateStoreUnavailable { get; set; }

    /// <summary>When set, the selector throws, standing in for an unhandled provider fault.</summary>
    public Exception? Throw { get; set; }

    public CoachMemoryContextRequest? Last => Requests.Count == 0 ? null : Requests[^1];

    public async Task<CoachMemoryContextResult> SelectAsync(
        CoachMemoryContextRequest request,
        CancellationToken cancellationToken = default)
    {
        Requests.Add(request);

        if (Throw is not null)
        {
            throw Throw;
        }

        if (SimulateStoreUnavailable)
        {
            LastResult = new CoachMemoryContextResult(
                [], 0, CoachMemoryContextOutcome.StoreUnavailable);
            return LastResult;
        }

        LastResult = await _inner.SelectAsync(request, cancellationToken);
        return LastResult;
    }
}
