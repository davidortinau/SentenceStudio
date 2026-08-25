using Microsoft.JSInterop;

namespace SentenceStudio.WebApp.Platform.Theme;

/// <summary>
/// The browser-side half of the appearance cookie, for the context where the server cannot reach it.
/// </summary>
/// <remarks>
/// <para>
/// Inside a Blazor <c>InteractiveServer</c> circuit <c>HttpContext</c> is <see langword="null"/>:
/// the request that carried the cookie completed before the circuit opened, and the circuit's
/// transport is a WebSocket with no headers to read or write. The only route to the cookie is the
/// browser, over JS interop.
/// </para>
/// <para>
/// This is a named seam rather than an <c>IJSRuntime</c> call inlined into the store so the store's
/// logic — which context to read from, what to do with an invalid value, when to write — is
/// testable without a browser or a JS runtime.
/// </para>
/// </remarks>
public interface IAppearanceCookieChannel
{
    /// <summary>Reads the raw cookie value from the browser, or <see langword="null"/> if absent.</summary>
    ValueTask<string?> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>Writes the raw cookie value from the browser.</summary>
    ValueTask WriteAsync(string token, int lifetimeDays, CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IAppearanceCookieChannel"/> over the shared <c>app.js</c> module.
/// </summary>
/// <remarks>
/// Every call is defensive. Interop can fail for entirely ordinary reasons — the circuit is being
/// torn down, the learner navigated away mid-call, the prerender pass has no JS runtime at all —
/// and none of those are worth failing a page render over. A failed read means "no stored
/// preference, use the default"; a failed write means the choice does not survive this browser
/// session, which is a far better outcome than an unhandled exception in a settings page.
/// </remarks>
public sealed class JsAppearanceCookieChannel : IAppearanceCookieChannel
{
    private const string ModulePath = "./_content/SentenceStudio.UI/js/app.js";

    private readonly IJSRuntime _js;
    private IJSObjectReference? _module;

    public JsAppearanceCookieChannel(IJSRuntime js) => _js = js;

    public async ValueTask<string?> ReadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var module = await GetModuleAsync(cancellationToken).ConfigureAwait(false);
            return await module.InvokeAsync<string?>(
                "readAppearanceCookie",
                cancellationToken,
                AppearanceCookie.Name).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsInteropUnavailable(ex))
        {
            return null;
        }
    }

    public async ValueTask WriteAsync(
        string token,
        int lifetimeDays,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var module = await GetModuleAsync(cancellationToken).ConfigureAwait(false);
            await module.InvokeVoidAsync(
                "writeAppearanceCookie",
                cancellationToken,
                AppearanceCookie.Name,
                token,
                lifetimeDays).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsInteropUnavailable(ex))
        {
            // Nothing to do: the in-memory state is already correct for this circuit, and the next
            // successful write will persist it.
        }
    }

    private async ValueTask<IJSObjectReference> GetModuleAsync(CancellationToken cancellationToken)
    {
        return _module ??= await _js.InvokeAsync<IJSObjectReference>(
            "import",
            cancellationToken,
            ModulePath).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the exception means "there is no usable JS runtime right now" rather than a real bug.
    /// </summary>
    private static bool IsInteropUnavailable(Exception ex) =>
        ex is JSException
            or JSDisconnectedException
            or InvalidOperationException      // prerendering: interop calls are not yet allowed
            or ObjectDisposedException
            or TaskCanceledException
            or OperationCanceledException;
}
