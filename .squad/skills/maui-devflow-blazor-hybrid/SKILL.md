# MAUI DevFlow Blazor Hybrid WebView Automation

## Overview

Guide for automating Blazor Hybrid WebView content via MAUI DevFlow on Mac Catalyst, iOS, Android, and Windows. Covers working commands, known limitations, and workarounds.

## What Works Today (Mac Catalyst)

### Native MAUI UI Automation

✅ **Full native UI tree access:**
```bash
maui devflow ui tree --agent-port 10223
# Returns JSON visual tree including BlazorWebView as a node
```

✅ **Native screenshots:**
```bash
maui devflow ui screenshot --agent-port 10223 --output screenshot.png
# Captures full app window including rendered WebView content
```

✅ **Element inspection:**
```bash
maui devflow ui tree --agent-port 10223
# Shows BlazorWebView element with bounds, parent, type, native platform view (WKWebView on Mac)
```

### CDP Connection Status

✅ **Connection verification:**
```bash
maui devflow webview status --agent-port 10223
# Returns: "Connected: CDP ready (1 WebView)" when Blazor is loaded
```

✅ **WebView enumeration:**
```bash
maui devflow webview webviews --agent-port 10223
# Lists available WebViews by index, AutomationId, ElementId, ready state
```

### DOM Access

✅ **Full DOM snapshot (HTML):**
```bash
maui devflow webview snapshot --agent-port 10223
# Returns complete rendered HTML including Blazor components
```

✅ **CDP document tree (JSON):**
```bash
maui devflow webview DOM getDocument --agent-port 10223
# Returns CDP-formatted document node tree with nodeIds, attributes, children
```

### Runtime Evaluation (WITH WORKAROUND)

⚠️ **JavaScript evaluation requires `--verbose` flag** (see Known Issues below):

```bash
# Simple expressions
maui devflow webview Runtime evaluate '1+1' --agent-port 10223 --verbose
# Returns: 2

# DOM queries
maui devflow webview Runtime evaluate 'document.querySelectorAll("button").length' --agent-port 10223 --verbose
# Returns: 2 (count of buttons in DOM)

# Element properties
maui devflow webview Runtime evaluate 'document.querySelector("#email").value' --agent-port 10223 --verbose
# Returns: current value of email input
```

## Known Issues

### 🐛 BUG #1: Runtime.evaluate Fails Without --verbose Flag

**Issue:** `maui devflow webview Runtime evaluate` returns JSON parse error when run without `--verbose`.

**Error Message:**
```
Error: '<' is an invalid start of a value. LineNumber: 0 | BytePositionInLine: 0.
```

**Workaround:** Always add `--verbose` flag to Runtime evaluate commands.

**Status:** Upstream issue drafted at `.squad/decisions/inbox/wash-devflow-upstream-issue.md`

**Layer:** DevFlow CLI (layer A) — race condition in CDP response parsing

**Limitations:** The `--verbose` workaround is NOT 100% reliable. Some operations (like `window.location.href = "/path"` assignments) still trigger the race condition even WITH `--verbose`. Use `webview source` for reliable DOM access.

**Detailed Investigation:** See investigation log in `.squad/agents/wash/history.md` under "Learnings > DevFlow CDP Runtime.evaluate Bug (2026-05-05, 2026-05-10)"

### 🐛 BUG #2: snapshot Command Fails with "Error: Uncaught"

**Issue:** `maui devflow webview snapshot` consistently returns "Error: Uncaught" instead of simplified DOM snapshot.

**Error Message:**
```
Error: Uncaught
```

**Workaround:** Use `maui devflow webview source` instead. Returns identical HTML output without the buggy ref enrichment layer.

**Example:**
```bash
# ❌ FAILS
maui devflow webview snapshot --agent-port 10223
# Error: Uncaught

# ✅ WORKS
maui devflow webview source --agent-port 10223
# <html lang="en" data-bs-theme="dark">...</html>
```

**Status:** Added to upstream issue at `.squad/decisions/inbox/wash-devflow-upstream-issue.md` as Bug #2

**Layer:** DevFlow CLI (layer A) — uncaught exception in snapshot command's element ref enrichment logic

**Root Cause:** The `snapshot` command tries to add element refs for automation (e.g., `[ref="abc123"]` attributes) but hits an unhandled exception during that processing. The `source` command returns raw HTML without ref enrichment and works perfectly.

**Impact:** Cannot use `snapshot` for DOM verification. Must use `source` and parse HTML directly with grep/sed/jq.

**Detailed Investigation:** See investigation log in `.squad/agents/wash/history.md` under "Learnings > DevFlow snapshot Bug (2026-05-10)"

### 🐛 Runtime.evaluate Fails with Complex Expressions (Even With --verbose)

**Issue:** JavaScript expressions containing certain operators (like `+` for addition or string concatenation) fail even when `--verbose` is used.

**Example that FAILS:**
```bash
maui devflow webview Runtime evaluate 'document.querySelectorAll(".mode-option").length + document.querySelectorAll(".context-option").length' --agent-port 10223 --verbose
# Error: '<' is an invalid start of a value. LineNumber: 0 | BytePositionInLine: 0.
```

**Example that WORKS:**
```bash
maui devflow webview Runtime evaluate 'document.querySelectorAll("button").length' --agent-port 10223 --verbose
# Returns: 2
```

**Workaround:** Keep expressions simple. If you need to combine results, run separate queries and add them client-side:

```bash
# Instead of one complex query:
# 'document.querySelectorAll(".a").length + document.querySelectorAll(".b").length'

# Run two simple queries:
maui devflow webview Runtime evaluate 'document.querySelectorAll(".a").length' --agent-port 10223 --verbose
maui devflow webview Runtime evaluate 'document.querySelectorAll(".b").length' --agent-port 10223 --verbose
# Add the results in your script
```

**Best Practice:** Prefer `webview snapshot` for complex data extraction — parse the HTML with grep/sed/jq instead of relying on JS evaluation.

### 🚫 Page Domain Commands Not Implemented

The CLI exposes only these Page domain commands:
- `Page navigate <url>`
- `Page reload`
- `Page captureScreenshot`

Standard CDP commands like `Page.enable`, `Page.disable`, `Page.setLifecycleEventsEnabled` are not available.

### 🚫 Input Domain Commands Not Yet Available

The CLI does not currently expose Input domain commands for:
- `Input.dispatchMouseEvent` (click)
- `Input.dispatchKeyEvent` (type)
- `Input.insertText` (paste)

**Note:** Higher-level wrappers may exist — see issue #113 in dotnet/maui-labs for WebView interaction tools (maui_webview_click, maui_webview_fill, maui_webview_type).

## Decision Tree: "How Do I Verify Blazor UI State?"

### Goal: Take a screenshot of the app
✅ Use: `maui devflow ui screenshot --agent-port <port> --output screenshot.png`

### Goal: Verify an element exists in the Blazor DOM
1. Try: `maui devflow webview source --agent-port <port>` and grep the HTML (snapshot is broken, use source)
2. If you need structured node data: `maui devflow webview DOM getDocument --agent-port <port>` and parse the JSON
3. If you need to run JS logic: `maui devflow webview Runtime evaluate 'document.querySelector("selector") !== null' --agent-port <port> --verbose`

### Goal: Read text content or attribute values
1. Try: `maui devflow webview source --agent-port <port>` and extract from HTML (snapshot is broken, use source)
2. If you need JS evaluation: `maui devflow webview Runtime evaluate 'document.querySelector("selector").textContent' --agent-port <port> --verbose`

### Goal: Count elements (buttons, inputs, etc.)
✅ Use: `maui devflow webview Runtime evaluate 'document.querySelectorAll("selector").length' --agent-port <port> --verbose`

### Goal: Verify form input values
✅ Use: `maui devflow webview Runtime evaluate 'document.querySelector("#inputId").value' --agent-port <port> --verbose`

### Goal: Verify which options appear in a select/picker
1. Try: `maui devflow webview source --agent-port <port>` and extract `<option>` tags or other elements (snapshot is broken, use source)
2. If you need JS: `maui devflow webview Runtime evaluate 'Array.from(document.querySelectorAll("select option")).map(o => o.textContent)' --agent-port <port> --verbose`

### Goal: Click a button or fill a form
⚠️ **Partially supported** — CDP Input domain commands exist but have reliability issues.

**Recommended approaches (in order):**
1. **For text input fields:** Use `Runtime.evaluate` to set `.value` and dispatch `input` events:
   ```bash
   # Fill email field
   maui devflow webview Runtime evaluate 'document.querySelector("#email").value = "user@example.com"; 1' --agent-port <port> --verbose
   # Notify Blazor of the change
   maui devflow webview Runtime evaluate 'document.querySelector("#email").dispatchEvent(new Event("input", { bubbles: true })); 1' --agent-port <port> --verbose
   ```

2. **For button clicks:** `Runtime.evaluate` with `.click()` often returns "Error: Uncaught" (exception in Blazor event handler) but may not actually execute the click. Unreliable for navigation.

3. **For navigation:** Use `Runtime.evaluate` with direct Router calls or native UI automation (`maui devflow ui tap` on coordinates) if the Blazor control has a native platform representation.

4. **Fallback:** Manual interaction by the user.

**Known Issue:** Clicking Blazor buttons via `element.click()` in Runtime.evaluate frequently throws uncaught exceptions that surface as "Error: Uncaught" but don't indicate whether the click succeeded. This is a Blazor event handler issue, not a DevFlow bug.

## Recommended Validation Workflow

For validating Blazor Hybrid features like NumberDrill picker UI:

```bash
# Step 1: Verify agent is connected
maui devflow list
# Should show your agent (Mac Catalyst, iOS, Android, etc.) with port number

# Step 2: Verify CDP is ready
maui devflow webview status --agent-port <port>
# Should return: "Connected: CDP ready (1 WebView)"

# Step 3: Get full DOM snapshot
maui devflow webview snapshot --agent-port <port> > dom.html
# Inspect the HTML to see what Blazor rendered

# Step 4: Take a screenshot for visual verification
maui devflow ui screenshot --agent-port <port> --output test.png

# Step 5: Use Runtime evaluate for dynamic checks (with --verbose!)
maui devflow webview Runtime evaluate 'document.querySelectorAll("button").length' --agent-port <port> --verbose
# Returns count of buttons in the current view

# Step 6: Extract specific data
maui devflow webview Runtime evaluate 'document.querySelector(".activity-title").textContent' --agent-port <port> --verbose
# Returns text content of element
```

## Platform-Specific Notes

### Mac Catalyst
- WebView platform handler: `WebKit.WKWebView`
- Remote debugging: Enabled automatically when DevFlow Blazor agent is registered
- Known working: All commands above

### iOS Simulator
- WebView platform handler: `WebKit.WKWebView`
- Same behavior as Mac Catalyst
- Tested with iOS 18.4 simulator

### iOS Device
- WebView platform handler: `WebKit.WKWebView`
- May require additional entitlements for remote debugging in Release builds
- Not tested yet — assumed same as simulator

### Android Emulator/Device
- WebView platform handler: `Android.Webkit.WebView`
- ChromeDevTools integration — may have different CDP endpoint behavior
- Not tested yet

### Windows
- WebView platform handler: `Microsoft.Web.WebView2`
- Different CDP implementation than WebKit
- Not tested yet

## Source Code References

- **DevFlow CLI:** https://github.com/dotnet/maui-labs (branch: feature/comet-go-upgrade)
- **Agent Package:** Microsoft.Maui.DevFlow.Agent 0.25.0-dev
- **Blazor Package:** Microsoft.Maui.DevFlow.Blazor 0.25.0-dev
- **Related Issue:** #113 (MCP: Add WebView interaction tools)

## Integration in SentenceStudio

DevFlow is integrated in `src/SentenceStudio.MacCatalyst/MauiProgram.cs`:

```csharp
#if DEBUG
builder.AddMauiDevFlowAgent();
builder.AddMauiBlazorDevFlowTools();
#endif
```

Packages referenced in `Directory.Packages.props`:
```xml
<PackageVersion Include="Microsoft.Maui.DevFlow.Agent" Version="0.25.0-dev" />
<PackageVersion Include="Microsoft.Maui.DevFlow.Blazor" Version="0.25.0-dev" />
```

Local NuGet packages at `/Users/davidortinau/work/LocalNuGets/`.

## Last Updated

2026-05-05 by Wash (Backend Dev)
- Documented Runtime.evaluate --verbose workaround
- Confirmed Mac Catalyst working commands
- Drafted upstream issue for JSON parse bug
