# MAUI Android head on the rc.2 workload (2026-11-07, in progress)

The MAUI Android head (`src/SentenceStudio.Android`) did not build on this machine. The native
Android head builds and runs. Captain, 2026-11-07: the MAUI app should build and run too.

## Environment

- SDK 11.0.100-rc.2, workload set 11.0.100-rc.2.26513.2, `maui` workload 11.0.0-rc.2.26505.5.
- ASP.NET runtime installed: 11.0.0-rc.2.26504.105 (also rc.1.26425.128).
- Emulator `emulator-5556` (`sdk_gphone16k_arm64`). The MAUI app (`com.simplyprofound.sentencestudio`)
  was not installed there before; the native Compose debug app is a separate id.

## Findings

1. **Build:** `CS1705` in `SentenceStudio.MauiCompatibility`: `Microsoft.Maui` rc.1.26451.6 uses
   `Java.Interop` 99.61.0.0, higher than the rc.2 workload's `Java.Interop` 11.0.0.0. Reproduced on
   the pushed commit `a38c5ae9` in a clean worktree, so it predates the Sam work.
2. **Fix 1 (uncommitted):** `Directory.Packages.props` MAUI pins `Microsoft.Maui.Controls/Core/
   Essentials/Resizetizer` moved to 11.0.0-rc.2.26505.5 (matches the workload). MAUI Android and MAUI
   iOS (simulator) build.
3. **Runtime crash:** first launch died with `FileNotFoundException: Java.Interop, Version=99.61.0.0`
   requested by `Microsoft.AspNetCore.Components.WebView.Maui` rc.1.26451.6
   (`WebKitWebViewClient.RunBlazorStartupScripts`).
4. **Fix 2 (uncommitted):** `Microsoft.AspNetCore.Components.WebView.Maui` moved to
   11.0.0-rc.2.26505.5. Its nuspec depends on `Microsoft.AspNetCore.Components.WebView`
   11.0.0-rc.1.26425.128, which is the existing pin, so the B65 rule (WebView equal to
   Components.Web) still holds. No crash after this.
5. **Open:** the app stays up but shows only the static placeholder from
   `src/SentenceStudio.AppLib/wwwroot/index.html` (`#app` holds the loading SVG). Through MAUI DevFlow:
   `window.Blazor` exists and has started, but `#app` never receives the `Routes` root component (not
   even the `<Authorizing>` spinner). Database init completes (checkpoint 4); the only startup error
   is the expected "Sync requires a signed-in learner". No render exception is logged. A cold
   relaunch shows the same.
6. **Tried and reverted:** moving `Microsoft.AspNetCore.Components.Web/WebView/Authorization` to
   rc.2.26504.105 (the installed runtime) cascades `NU1605` downgrades across every
   `Microsoft.Extensions.*` pin, touching the API and WebApp. Reverted.

7. **MAUI iOS (same pins):** builds; installed fresh on simulator `5359E332` ("SS Voice") and
   launched (pid 43428). After about 50 seconds it is still on the native launch screen (logo), with
   no exception in the system log. So the problem is not Android-only, and on iOS it is earlier than
   Blazor. `maui devflow list` hung for 2 minutes while both apps were running.

## Next steps

- Check MAUI iOS on the rc.1 pins too (does it reach the Blazor page on `a38c5ae9`?), to learn
  whether the rc.2 MAUI packages caused the iOS stall.
- Compare `blazor.webview.js` in the built APK with the one the rc.2 `WebView.Maui` expects
  (IPC protocol mismatch would explain "started, but no root component").
- If it is the rc.1 WebView script, plan a coordinated move of the ASP.NET Components and
  `Microsoft.Extensions.*` pins to rc.2.26504.105, validated on API, WebApp and both MAUI heads.
