# maui-devflow CLI Report

**Date**: 2026-02-07
**Tool version**: MauiDevFlow.CLI (via NuGet `Redth.MauiDevFlow.Agent`)
**Platform**: iOS Simulator — iPhone 17 Pro, iOS 26.1
**App**: .NET MAUI with MauiReactor (Reactor.Maui)
**Usage context**: AI agent (GitHub Copilot CLI) using maui-devflow to validate code changes on a running app in the simulator.

## ✅ What Worked

| Command | Notes |
|---|---|
| `MAUI status` | Correctly reported connection status (including "cannot connect" when agent wasn't ready) |
| `MAUI tree` | Returned full visual tree with element IDs, positions, sizes, text — very useful for understanding UI structure |
| `MAUI navigate "//RouteName"` | Successfully navigated between Shell flyout pages (e.g., `//ListLearningResourcesPage`) |
| `MAUI navigate ".."` | Successfully popped back in navigation stack |
| `MAUI tap <elementId>` | Worked for some elements (e.g., Grid, Button) |
| `MAUI logs` | Returned app logs, useful for checking errors |

## ❌ What Did NOT Work

| Issue | Details |
|---|---|
| **`tap` fails on many element types** | `Border`, `Label`, `Image` all returned "Failed to tap" even with valid element IDs. Only `Grid` and `Button` seemed tappable. Elements like `Label text="Details >"` (ID `367173220098`), `Border` (ID `0c2cef6b0b02`), `Label` title (ID `e78cc6e7bc1e`) all failed. No error message explaining why. |
| **Cannot tap hidden/flyout elements** | FlyoutItem elements show as `[hidden] [disabled]` in the tree. Tapping them fails. There's no way to open the flyout menu via the CLI — the hamburger button isn't in the visual tree. `navigate` is the workaround, but you can't simulate what a user does (open flyout, tap item). |
| **`screenshot` fails** | `MAUI screenshot --output /tmp/file.png` returned "Error: Failed to capture screenshot" with no further details. Had to fall back to `xcrun simctl io <udid> screenshot` for all screenshots. |
| **No coordinate-based tap** | `MAUI tap` only accepts an element ID. No `--x --y` coordinate option. This makes it impossible to tap elements that fail ID-based tap (common workaround in other tools). |
| **No text-based tap** | No `--text "OK"` option to tap by visible text. Would be very useful for dismissing alerts/dialogs and tapping labels/buttons by their display text. |
| **No swipe/scroll support** | No scroll or swipe commands. Can't scroll down a page to find off-screen elements. |
| **No alert/dialog handling** | No way to detect or dismiss system alerts (e.g., Syncfusion license popup). Had to use other tools to handle these. |
| **Agent connection timing** | After fresh app launch, the agent takes several seconds to become available. No retry/wait mechanism — you just get "Error: Cannot connect to agent at localhost:9223" and have to keep polling. |
| **Silent failures** | Most failures return a single-line message like "Failed to tap: {id}" with no explanation (element not found? not visible? not interactive? wrong type?). More detailed error messages would save significant debugging time. |

## 🔧 Feature Requests (Priority Order)

1. **Text-based tap** (`MAUI tap --text "OK"`) — Most impactful. Would solve alert dismissal, label tapping, and general "find and tap" workflows.
2. **Coordinate tap** (`MAUI tap --x 200 --y 400`) — Fallback when element tap fails.
3. **Fix screenshot** — Currently non-functional on iOS sim.
4. **Verbose error mode** (`MAUI tap --verbose <id>`) — Explain WHY a tap failed.
5. **Scroll/swipe** (`MAUI scroll down`, `MAUI swipe left`) — Required for real testing flows.
6. **Wait/retry** (`MAUI status --wait 10`) — Block until agent connects, with timeout.
7. **Alert handling** (`MAUI alert dismiss`, `MAUI alert text`) — Detect and handle modal alerts.
8. **Query by text** (`MAUI query --text "Submit"`) — Find elements by visible text, not just tree grep.

## Workarounds Used

| Problem | Workaround |
|---|---|
| Screenshot fails | `xcrun simctl io <udid> screenshot /tmp/file.png` |
| Can't open flyout | `MAUI navigate "//RouteName"` (skips flyout animation) |
| Can't dismiss alerts | Used Appium or waited for auto-dismiss |
| Can't tap Labels/Borders | Found a tappable parent (Grid, Button) via `MAUI tree` |
| Agent not ready | Polled `MAUI status` or `MAUI tree` until response |

## Overall Assessment

The `tree` and `navigate` commands are genuinely useful — tree gives fast insight into the live UI, and navigate lets you jump around the app efficiently. However, `tap` reliability is a major blocker for end-to-end test automation. In this session, roughly **50% of tap attempts failed** on valid, visible elements. The tool works well as an **inspection/navigation** tool but is not yet reliable enough for **interaction/automation** workflows without falling back to simctl or Appium.
