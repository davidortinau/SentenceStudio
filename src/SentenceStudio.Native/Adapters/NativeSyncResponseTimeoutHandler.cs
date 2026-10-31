namespace SentenceStudio.Native.Adapters;

/// <summary>
/// Bounds how long the native CoreSync client waits for the server to START answering.
/// The shared client allows 10 minutes per request so large bodies can transfer; a server
/// that accepts the connection but never answers (an Aspire proxy with the API stopped, a
/// captive or stalled network) therefore held sign-in readiness, pre-activity syncs, and the
/// sync semaphore for 10 minutes.
/// </summary>
/// <remarks>
/// Every CoreSync run starts with the store-id request, a tiny read, so it gets the short
/// <see cref="NativeSyncTimeouts.Probe"/> and an unresponsive server is detected quickly.
/// Change-set requests get <see cref="NativeSyncTimeouts.Response"/> (the source app's
/// 120-second attempt timeout) because the server answers them only after applying or
/// building a whole page. Body transfer after the headers stays under the HttpClient timeout.
/// A timed-out run is an ordinary failed sync: CoreSync saves no anchor, so every pending
/// local write stays pending and is sent again on the next run.
/// </remarks>
public sealed class NativeSyncResponseTimeoutHandler(NativeSyncTimeouts timeouts) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var budget = IsProbe(request) ? timeouts.Probe : timeouts.Response;
        // Disposed when SendAsync returns, so the deadline never cancels the body read.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(budget);
        try
        {
            return await base.SendAsync(request, deadline.Token);
        }
        catch (OperationCanceledException ex)
            when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new HttpRequestException(
                $"The sync server did not respond within {budget.TotalSeconds:0} seconds.",
                new TimeoutException(ex.Message, ex));
        }
    }

    internal static bool IsProbe(HttpRequestMessage request) =>
        request.Method == HttpMethod.Get
        && request.RequestUri?.OriginalString is { } uri
        && uri.TrimEnd('/').EndsWith("/store-id", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Time-to-response budgets for native CoreSync requests.</summary>
public sealed record NativeSyncTimeouts(TimeSpan Probe, TimeSpan Response)
{
    public static NativeSyncTimeouts Default { get; } =
        new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(120));
}
