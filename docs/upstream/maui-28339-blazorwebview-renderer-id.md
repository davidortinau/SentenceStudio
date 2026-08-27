# Upstream issue draft — BlazorWebView: render batch rejected with "There is no browser renderer with ID 3" across a forced document load

**Target repo:** `dotnet/maui` (comment on existing issue **#28339**, which is closed as needs-repro)
**Related:** `dotnet/aspnetcore` — `Microsoft.AspNetCore.Components.WebView`
**Status:** draft, not yet filed.

---

## Why this is a comment on #28339, not a new issue

`dotnet/maui#28339` reports exactly this string from `Renderer.ts` /
`IpcReceiver.OnRenderCompleted` and was closed for want of a reproduction. What follows is a
reproduction with the trigger identified, so the issue can be reopened with something actionable.

**We are not currently blocked by it.** Our own code was creating the condition, and removing that
fixed our symptom completely (0 occurrences across a full account-switch matrix, where previously
it appeared during the switch). The framework behaviour underneath is still worth reporting: the
error is unactionable for an app developer, arrives on the finalizer thread with no useful stack,
and the app is given no way to avoid or observe the window.

## Versions (read from this machine, not assumed)

| Component | Version |
|---|---|
| `Microsoft.AspNetCore.Components.WebView` | `11.0.0-preview.4.26230.115` |
| `Microsoft.AspNetCore.Components.WebView.Maui` | `11.0.0-preview.4.26230.3` |
| `Microsoft.Maui.Controls` | `11.0.0-preview.4.26230.3` |
| `Microsoft.Maui.Platforms.MacOS` / `.BlazorWebView` | `0.26.0-dev` (`dotnet/maui-labs`) |
| .NET SDK (selected; no `global.json` present) | `11.0.100-preview.7.26381.103` |
| Workload version | `11.0.100-preview.7.26410.2` (`maui 11.0.0-preview.7.26406.9`, `macos 26.5.11997-net11-p7`) |
| TFM | `net11.0-macos` (AppKit head, **not** Mac Catalyst) |
| Host | macOS on Apple silicon, Xcode 26 SDK |

## What happens

Calling `NavigationManager.NavigateTo(url, forceLoad: true)` inside a BlazorWebView tears the page
down and builds a new one. From the decompiled
`Microsoft.AspNetCore.Components.WebView.WebViewManager`:

```csharp
internal async Task AttachToPageAsync(string baseUrl, string startUrl)
{
    if (_currentPageContext != null)
    {
        await _currentPageContext.DisposeAsync();     // disposes WebViewRenderer + the DI scope
    }
    AsyncServiceScope serviceScope = _provider.CreateAsyncScope();   // brand-new scope
    _currentPageContext = new PageContext(_dispatcher, serviceScope, _ipcSender, _jsComponents, baseUrl, startUrl);
    foreach (var (selector, rootComponent) in _rootComponentsBySelector) { ... }
    await Task.WhenAll(list);
}
```

and `PageContext.DisposeAsync`:

```csharp
public async ValueTask DisposeAsync()
{
    await Renderer.DisposeAsync();
    await _serviceScope.DisposeAsync();
}
```

`AttachToPageAsync` is only reached when the **new** document's `blazor.webview.js` sends
`AttachPage` (`IpcReceiver.OnMessageReceivedAsync`). Between the old document beginning to unload
and that message arriving, the .NET side still owns a live `PageContext` and can still emit render
batches over the IPC channel. Those batches land in a JS context that has no WebView renderer
registered, and `Renderer.ts` throws:

```
System.AggregateException: A Task's exception(s) were not observed either by Waiting on the Task
or accessing its Exception property. As a result, the unobserved exception was rethrown by the
finalizer thread. (Error: There is no browser renderer with ID 3.)
 ---> System.InvalidOperationException: Error: There is no browser renderer with ID 3.
   at Microsoft.AspNetCore.Components.WebView.IpcReceiver.OnRenderCompleted(PageContext pageContext, Int64 batchId, String errorMessageOrNull)
   at Microsoft.AspNetCore.Components.WebView.IpcReceiver.OnMessageReceivedAsync(PageContext pageContext, String message)
```

(ID 3 is `WebRendererId.WebView`, not an instance counter.)

## Observed impact

The batch is dropped, so the DOM keeps whatever the previous render left. In our app the .NET side
logged the correct tree:

```
dbug: Microsoft.AspNetCore.Components.RenderTree.Renderer[1]
      Initializing component 18 (SamOverlayHost) as child of 8 (MainLayout)
dbug: Microsoft.AspNetCore.Components.RenderTree.Renderer[1]
      Initializing component 19 (SamFab) as child of 18 (SamOverlayHost)
dbug: Microsoft.AspNetCore.Components.RenderTree.Renderer[3]
      Rendering component 19 of type SamFab
```

while `document.getElementById("sam-fab")` was `null` and stayed `null` across further navigation.
Only relaunching the app recovered it. Two symptoms that make this expensive to diagnose:

1. **The error is asynchronous and unowned.** It surfaces from the finalizer thread as an
   unobserved-task exception, often seconds later, with no link to the navigation that caused it.
2. **The UI failure is silent.** Nothing indicates that a batch was dropped; the app simply renders
   a stale DOM while its component tree is correct.

## Reproduction

1. MAUI app with a `BlazorWebView` root component.
2. Any interaction that calls `NavigationManager.NavigateTo("/", forceLoad: true)` while components
   are actively re-rendering (ours: a sign-in handler that also raised
   `AuthenticationStateChanged`, so `CascadingAuthenticationState`, the layout and several scoped
   services all re-rendered around the same tick).
3. Repeat the sign-in/sign-out cycle several times in one app session.

Timing-dependent — it did not reproduce on every cycle for us, which is presumably why #28339 was
closed as needs-repro. It reproduced reliably enough to block an account-switch test run, and
stopped entirely once the forced load was removed.

## What would help

1. **Do not throw for a batch that arrives with no renderer attached.** The old document is gone;
   the batch is meaningless. Dropping it with a debug-level log would be strictly better than an
   unobserved exception, and it is already the semantics of
   `Renderer.AddToRenderQueue` for a removed component.
2. **Await or cancel in-flight batches in `PageContext.DisposeAsync`**, so the old renderer cannot
   emit into the gap.
3. **Document that `forceLoad: true` recreates the DI scope in a BlazorWebView.** This is not
   obvious, and it is the opposite of the Blazor Server intuition, where scope lifetime follows the
   circuit. Code that reasons about "scoped means per user" — including sample code — is wrong on
   this host in both directions: a soft navigation keeps the scope across sign-out, and a forced
   load replaces it mid-session.

## Our fix (app side)

We removed the forced document load on the WebView host. It was only ever needed on the web host,
where signing in must round-trip through an HTTP endpoint so the server can set an auth cookie; on
the WebView there is no cookie and no server round-trip. Removing it also revealed — and let us fix
— a genuine bug it had been masking: the login page signed in through `IAuthService` directly and
never raised `AuthenticationStateChanged`, so authorization only ever picked up the new principal
because the whole page was being rebuilt.

Result across a full A → B → A → B account-switch matrix in one app session: one DI scope for the
whole session (previously one per sign-in), the overlay re-mounting correctly on every sign-in, and
zero occurrences of the error.
