#if DEBUG
using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls;
using Microsoft.Maui.DevFlow.Agent.Core;
using Microsoft.Maui.Dispatching;

namespace SentenceStudio.MacOS;

/// <summary>
/// DEBUG-only bridge that starts the DevFlow agent on the macOS (AppKit) head without going
/// through the MAUI UI dispatcher.
///
/// UPSTREAM ISSUE — DevFlow agent start is lost when the UI thread blocks
/// ---------------------------------------------------------------------
/// <c>AgentServiceExtensions.AddMauiDevFlowAgent</c> (Microsoft.Maui.DevFlow.Agent 0.25.0-dev)
/// registers the singleton eagerly but starts it from exactly one trigger — a
/// <c>MacOSLifecycle.DidFinishLaunching</c> handler — and that handler never calls
/// <c>Start</c> directly. It always defers:
///
///     app.Dispatcher.Dispatch(() =&gt; service.Start(app, app.Dispatcher));
///     Console.WriteLine($"[Microsoft.Maui.DevFlow] Agent started on port {options.Port}");
///
/// Two problems follow from that deferral:
///
///  1. The handler already runs ON the AppKit main thread (it is invoked from
///     <c>MacOSMauiApplication.DidFinishLaunching</c>), so the <c>Dispatch</c> is a pure
///     round-trip that only postpones the start until the run loop next drains. If anything
///     blocks the main thread between <c>DidFinishLaunching</c> and the next run-loop turn, the
///     queued <c>Start</c> never executes and the agent never opens its listener.
///  2. The "Agent started on port N" line is printed immediately after queueing, not after the
///     listener binds, so the console *claims success* while nothing is listening. The truthful
///     line — "[Microsoft.Maui.DevFlow.Agent] HTTP server started on port N", written inside
///     <see cref="DevFlowAgentService.Start"/> — is simply absent.
///
/// That combination is exactly how this repo lost DevFlow on macOS: the app blocked the main
/// thread in a Keychain read (SecureStorage -> SecItemCopyMatching, gated behind a SecurityAgent
/// access prompt that appears after every rebuild because the ad-hoc code signature changes).
/// The run loop never turned, the queued <c>Start</c> never ran, port 9225 stayed closed, the
/// broker reported <c>agent_count=0</c>, and <c>maui devflow agent status</c> returned
/// "Cannot connect to agent at localhost:9225" — while stdout said the agent had started.
///
/// This is the worst possible failure mode for a debugging tool: the agent is unreachable
/// precisely when the UI thread is wedged, which is when you most need to inspect the app.
/// <see cref="DevFlowAgentService.StartServerOnly"/> exists for this case but the macOS
/// registration path never uses it.
///
/// WORKAROUND
/// ----------
/// Start the *already-registered* singleton synchronously from
/// <c>MauiMacOSApp.DidFinishLaunching</c>, right after <c>base.DidFinishLaunching(...)</c> has
/// built the MauiApp, created the platform window and set <c>Application.Current</c>. We are on
/// the AppKit main thread at that point, so no dispatch is needed and the listener is bound
/// before any later main-thread stall can swallow it.
///
/// Safety properties:
///  * We resolve the existing <see cref="DevFlowAgentService"/> from DI — no second service is
///    constructed, and no second broker registration is made (the BrokerRegistration was
///    already attached by <c>AddMauiDevFlowAgent</c> via <c>SetBrokerRegistration</c>;
///    <see cref="DevFlowAgentService.Start"/> only starts the HTTP listener).
///  * <c>AgentHttpServer.Start()</c> is guarded by <c>if (!IsRunning)</c>, so when upstream's
///    queued <c>Start</c> finally runs it is a no-op. Calling both is harmless.
///  * <c>TcpListener.Start()</c> does not block; the accept loop is async.
///  * If <c>Application.Current</c> is somehow null we still bring up the HTTP server via
///    <see cref="DevFlowAgentService.StartServerOnly"/> so the agent is reachable for logs and
///    diagnostics.
///
/// Release builds are unaffected: this file is entirely inside <c>#if DEBUG</c>, and the
/// DevFlow package references in SentenceStudio.MacOS.csproj carry
/// <c>Condition="'$(Configuration)'=='Debug'"</c>, so no DevFlow type is referenced or shipped.
///
/// Remove this bridge once upstream starts the agent without a dispatcher round-trip.
/// tests/SentenceStudio.UnitTests/Platform/DevFlowMacOSBridgeContractTests.cs pins the DevFlow
/// API surface this workaround depends on so a package bump cannot silently break it.
/// </summary>
internal static class DevFlowMacOSBridge
{
    private static readonly string TraceFile =
        Environment.GetEnvironmentVariable("SS_DEVFLOW_TRACE")
        ?? Path.Combine(Path.GetTempPath(), "sentencestudio-devflow-trace.log");

    private static readonly object TraceGate = new();

    /// <summary>
    /// File-based startup trace.
    /// <para>
    /// Launching the .app via <c>open</c> discards stdout, so DevFlow's own
    /// "[Microsoft.Maui.DevFlow] ..." console diagnostics are invisible and every startup
    /// failure in this head looks identical: a live process with no window and no agent.
    /// A file gives startup a channel that survives both. Point <c>SS_DEVFLOW_TRACE</c> at a
    /// path to relocate it; note <c>Path.GetTempPath()</c> is <c>$TMPDIR</c>
    /// (/var/folders/.../T/), not /tmp.
    /// </para>
    /// </summary>
    internal static void Trace(string message)
    {
        try
        {
            lock (TraceGate)
            {
                File.AppendAllText(TraceFile, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Diagnostics must never take the app down.
        }
    }

    /// <summary>
    /// Starts the DevFlow agent's HTTP listener synchronously. Must be called on the AppKit
    /// main thread from <c>MauiMacOSApp.DidFinishLaunching</c>, after the base implementation
    /// has returned. Safe to call more than once.
    /// </summary>
    /// <param name="services">Service provider from <c>MacOSMauiApplication.Services</c>.</param>
    internal static void StartAgentIfNeeded(IServiceProvider? services)
    {
        try
        {
            var service = services?.GetService<DevFlowAgentService>();
            if (service is null)
            {
                Trace("❌ DevFlowAgentService not resolvable — AddMauiDevFlowAgent did not run. "
                      + "Check for an exception earlier in MacOSMauiProgram.CreateMauiApp().");
                return;
            }

            // Qualified for the same reason as MacOSBlazorApp's base type: `SentenceStudio.Application`
            // is a namespace in the shared query layer, and it wins the simple-name lookup over the
            // imported MAUI type from inside `SentenceStudio.MacOS`.
            var app = Microsoft.Maui.Controls.Application.Current;
            var dispatcher = app?.Dispatcher ?? Dispatcher.GetForCurrentThread();

            if (service.IsRunning)
            {
                if (app is not null && !service.IsAppBound)
                {
                    service.BindApp(app);
                    Trace($"🔗 Agent already listening on port {service.Port}; bound Application.");
                }
                else
                {
                    Trace($"✅ Agent already listening on port {service.Port}; bridge is a no-op.");
                }

                return;
            }

            if (app is not null)
            {
                // Synchronous on purpose — see the class remarks. Dispatching here is what
                // upstream does, and it is what loses the agent when the main thread later blocks.
                service.Start(app, dispatcher);
                Trace($"✅ DevFlow agent started synchronously on port {service.Port} "
                      + $"(IsRunning={service.IsRunning}, IsAppBound={service.IsAppBound}).");
            }
            else if (dispatcher is not null)
            {
                service.StartServerOnly(dispatcher);
                Trace($"⚠️ Application.Current was null; started HTTP server only on port {service.Port} "
                      + $"(IsRunning={service.IsRunning}).");
            }
            else
            {
                Trace("❌ Neither Application.Current nor a dispatcher available; agent not started.");
            }
        }
        catch (Exception ex)
        {
            Trace($"❌ StartAgentIfNeeded threw: {ex}");
        }
    }
}
#endif
