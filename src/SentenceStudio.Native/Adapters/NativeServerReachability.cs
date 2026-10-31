namespace SentenceStudio.Native.Adapters;

/// <summary>
/// Remembers, for a short window, that the API did not answer (a request timed out or the
/// connection failed). Blocking callers that would otherwise each wait their own full
/// timeout check it and fail fast: sign-in restore, Dashboard readiness, and quiz speech
/// right after another step timed out. Any HTTP response from the API clears it at once.
/// </summary>
/// <remarks>
/// This is device network state, not owner data: it never authorizes or reads anything.
/// It only shortens waits; pending local writes stay pending, and background sync
/// triggers keep sending (and clear the window when the server answers again).
/// </remarks>
public sealed class NativeServerReachability(TimeProvider time, TimeSpan window)
{
    /// <summary>Long enough to cover the next blocking step after a timeout.</summary>
    public static TimeSpan DefaultWindow { get; } = TimeSpan.FromSeconds(45);

    private long _failedAt = long.MinValue;

    public NativeServerReachability() : this(TimeProvider.System, DefaultWindow) { }

    public TimeSpan Window => window;

    public bool IsRecentlyUnreachable
    {
        get
        {
            var failedAt = Interlocked.Read(ref _failedAt);
            return failedAt != long.MinValue
                && time.GetElapsedTime(failedAt) < window;
        }
    }

    public void RecordFailure() =>
        Interlocked.Exchange(ref _failedAt, time.GetTimestamp());

    public void RecordResponse() =>
        Interlocked.Exchange(ref _failedAt, long.MinValue);
}

/// <summary>
/// Observes an API client's pipeline: any response clears the recent-failure window and a
/// connection failure (no HTTP status) opens it. It never changes, retries, or blocks a request.
/// </summary>
public sealed class NativeServerReachabilityHandler(NativeServerReachability reachability)
    : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            reachability.RecordResponse();
            return response;
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            reachability.RecordFailure();
            throw;
        }
    }
}
