# Troubleshooting

## Table of Contents
- [Broker Idle Timeout](#broker-idle-timeout)
- [Phantom Agent (Connected But Empty Tree)](#phantom-agent-connected-but-empty-tree)
- [Connection Refused](#connection-refused--cannot-connect)
- [Build Failures](#build-failures)
- [CDP Not Connecting](#cdp-not-connecting-blazor-hybrid)
- [Mac Catalyst Permission Dialogs](#mac-catalyst-repeated-permission-dialogs-on-rebuild)

## Phantom Agent (Connected But Empty Tree)

**Symptom:** `maui devflow wait` returns a port (e.g. 10223) and `maui devflow list` shows the
agent connected, but every inspection command misbehaves:

- `maui devflow ui tree` returns 0 windows
- `maui devflow ui status` reports no top-level window
- `maui devflow webview status` reports CDP "Not ready"
- `maui devflow logs` returns HTTP 404
- Meanwhile `log stream --predicate 'process == "SentenceStudio"'` (or equivalent) shows the
  app is alive — WebKit activity, layout, network calls all visible in system logs

This is **not an auth/code bug** — the app is running. The DevFlow agent's HTTP surface is
wedged. Most often seen on Mac Catalyst after a kill+relaunch cycle, or on a long-running
session where the broker auto-restarted underneath a still-running app.

**Recovery (in order — stop as soon as one works):**

1. `maui devflow diagnose` — sometimes prints the actionable cause (broker mismatch, port collision).
2. **Force the agent to re-register:** kill the app process directly (`kill <pid>` or close the
   window), `maui devflow list` to confirm it disappears, then relaunch via `dotnet build -t:Run`
   and `maui devflow wait`. A fresh launch almost always recovers.
3. **Recycle the broker:** `maui devflow broker stop` then `maui devflow broker status` (which
   restarts it), then relaunch the app.
4. **Last resort:** delete `~/.maui/devflow/state.json` (or platform equivalent) and restart
   the broker. This wipes any stale agent registrations.

**When to give up and use a different signal:** if you've burned ≥10 minutes on this and your
goal is to verify *behavior* (not introspect UI), fall back to indirect verification:

- API logs (Aspire `list_structured_logs`) — did the expected request hit the server?
- Database queries — did the expected row land in the DB?
- Screenshot via `xcrun simctl io <udid> screenshot out.png` (iOS) or `screencapture` (Catalyst)
  — visual confirmation without DevFlow.

This was the workaround used during the 2026-05 auth-persistence ship: with the agent wedged,
zero 401s and zero refresh storms in API logs over the runtime window were accepted as
sufficient evidence that the persistence fix held.

## Broker Idle Timeout

**Symptom:** Commands fail with connection errors after returning to debugging after a break.

**Cause:** The broker daemon shuts down after a period of inactivity (no connected agents,
no CLI commands). When you next run a CLI command, the broker auto-restarts, but any
previously connected agents are gone — they were registered with the old broker instance.

**Fix:**
1. Run `maui devflow broker status` to confirm the broker restarted.
2. Restart the app (re-run `dotnet build -t:Run`) so the agent re-registers.
3. `maui devflow wait` to confirm reconnection.

**Prevention:** For long debugging sessions with breaks, periodically run
`maui devflow list` or `maui devflow broker status` to keep the broker alive.

## Connection Refused / Cannot Connect

If `maui devflow ui status` fails with connection refused:

1. **App not running?** Verify the app launched: check the build output for errors.
2. **Check the broker:** Run `maui devflow list` to see if the agent registered. If the list
   is empty, the app may not have connected to the broker yet (wait a few seconds and retry).
3. **Wrong port?** If using `.mauidevflow`, ensure the port matches between build and CLI.
   Run CLI from the project directory so it auto-detects the config file.
4. **Port already in use?** Another process may hold the port. Check with:
   ```bash
   lsof -i :<port>       # macOS/Linux
   ```
   With the broker, this is less common since ports are auto-assigned.
5. **Android?** Did you run `adb reverse tcp:19223 tcp:19223` (for broker) and
   `adb forward tcp:<port> tcp:<port>` (for agent)? Re-run after each deploy.
6. **Mac Catalyst?** Check entitlements include `network.server` (see setup.md step 5).
7. **macOS (AppKit)?** Ensure `AddMacOSEssentials()` is called and the app window appeared.
   See [references/macos.md](macos.md) for troubleshooting.
8. **Linux/GTK?** No special network setup needed — runs directly on localhost. Check if the app started successfully.
9. **Broker issues?** `maui devflow broker status` to check. `maui devflow broker stop` then
   retry (CLI will auto-restart it).

## Build Failures

**Missing workloads:**
```
error NETSDK1147: To build this project, the following workloads must be installed: maui-ios
```
Fix: `dotnet workload install maui` (installs all MAUI workloads).

**SDK version mismatch:**
```
error : The current .NET SDK does not support targeting .NET 10.0
```
Fix: Install the required .NET SDK version, or check `global.json` for version pins.

**Android SDK not found:**
```
error XA0000: Could not find Android SDK
```
Fix: Install Android SDK via `android sdk install` or set `$ANDROID_HOME`.

**iOS provisioning / signing errors:**
Fix: For simulators, ensure no signing is configured (default). For devices, set up provisioning
profiles via `apple appstoreconnect profiles list`.

**General build failure recovery:**
1. `dotnet clean` then retry the build
2. Delete `bin/` and `obj/` directories: `rm -rf bin obj` then rebuild
3. Check the full build output (not just the last error) — earlier warnings often reveal the root cause

## CDP Not Connecting (Blazor Hybrid)

If `maui devflow webview status` fails but `ui status` works:

1. **Chobitsu not loading?** Check logs for `[BlazorDevFlow]` messages. If auto-injection failed, add `<script src="chobitsu.js"></script>` manually to `wwwroot/index.html`
2. **Blazor not initialized?** Navigate to a Blazor page first, then retry
3. Check app logs: `maui devflow logs --limit 20` — look for `[BlazorDevFlow]` errors

## Mac Catalyst: Repeated Permission Dialogs on Rebuild

If macOS prompts "App would like to access your Documents folder" on every rebuild:

**Cause:** TCC permissions are tied to the app's code signature. Ad-hoc Debug builds produce a
different signature each rebuild → macOS forgets the grant and re-prompts. This happens even
with App Sandbox disabled.

**Fix:** Don't access TCC-protected directories (`~/Documents`, `~/Downloads`, `~/Desktop`,
or dotfiles like `~/.myapp/` in the home root) programmatically. Instead use:
- `Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)` → `~/Library/Application Support/` (not TCC-protected)
- `NSOpenPanel`/`NSSavePanel` for user-initiated file access (grants automatic TCC exemption)

If you can't avoid TCC paths, sign Debug builds with a stable Apple Development certificate
so the code signature stays consistent across rebuilds.

## macOS (AppKit) Issues

For detailed macOS (AppKit) troubleshooting, see [references/macos.md](macos.md#troubleshooting).

Common issues:
- **No window appears** → Missing `AddMacOSEssentials()` in builder
- **SIGKILL on launch** → Don't re-sign manually; clean rebuild instead
- **Blazor stuck on "Loading..."** → Use `MacOSBlazorWebView`, not standard `BlazorWebView`
- **No sidebar content** → Add `MacOSShell.SetUseNativeSidebar(shell, true)` + `FlyoutBehavior.Locked`
