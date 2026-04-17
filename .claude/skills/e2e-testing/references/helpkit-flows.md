# HelpKit end-to-end flows

Reference for testing apps that integrate `Plugin.Maui.HelpKit` (Alpha 0.1.0).
Loaded on demand by the e2e-testing skill when a HelpKit-related fix or feature
needs verification.

## Two paths

| Path | When | Tool |
|---|---|---|
| Native (default for Alpha) | Any HelpKit-enabled MAUI app | maui-ai-debugging skill |
| Blazor companion | Beta only — when `Plugin.Maui.HelpKit.Blazor` ships | Playwright MCP |

Alpha = native only. Treat the Blazor row as a placeholder.

## Native path (maui-ai-debugging)

Per VALIDATION-PLAN.md, every HelpKit verification is three levels: UI, Data, Log.

### Quick smoke (one TFM, ~10 min)
1. Build + deploy the host app to the chosen TFM (see `lib/Plugin.Maui.HelpKit/tests/smoke-tests/smoke-<tfm>.md`).
2. Trigger help via the in-app entry point (Shell flyout `Help` item or programmatic `IHelpKit.ShowAsync()`).
3. Take a screenshot — confirm the chat modal opens with the localized empty-state string.
4. Send 3 questions covering: in-corpus answerable, out-of-corpus refusal, prompt-injection attempt.
5. Inspect the per-platform SQLite DB (paths in the smoke-tests files):
   - `sqlite3 <db> "SELECT Role, substr(Content,1,80) FROM message ORDER BY CreatedAtUtc DESC LIMIT 10;"`
   - `sqlite3 <db> "SELECT Fingerprint FROM ingestion_fingerprint;"`
   - `sqlite3 <db> "SELECT count(*) FROM answer_cache WHERE ExpiresAt > strftime('%s','now');"`
6. Read platform logs (Console.app / `adb logcat` / Xcode Console / VS Output) and confirm:
   - No unhandled exceptions.
   - At least one `helpkit.ingest.chunks` and `helpkit.retrieval.queries` metric line.
   - Zero hits for `"You are the in-app help assistant"` or `"STRICTLY from the provided documentation"` (system-prompt leak check).

### Full smoke
Run the per-TFM checklist in `lib/Plugin.Maui.HelpKit/tests/smoke-tests/smoke-<tfm>.md`.
File a GitHub issue with the line numbers that failed and a log excerpt on any miss.

### Cross-cutting scenarios
See `lib/Plugin.Maui.HelpKit/tests/VALIDATION-PLAN.md` section 4 (X01-X16).
At minimum, exercise:
- X04 (rate-limit refusal at 11th question)
- X08 (out-of-scope refusal — assert no fabricated cite paths)
- X09 (prompt injection attempt — assert no system-prompt phrase in logs or response)
- X10 (secret-pattern redaction — drop `api_key: sk-ABCDEFGHIJKLMNOPQRSTUV` into a `.md`, ingest, ask about it, confirm `[REDACTED]` not the raw token)
- X15 (Clear nukes message rows scoped to current user only)

## Per-TFM SQLite paths cheat sheet

| TFM | DB path |
|---|---|
| net11.0-maccatalyst | `~/Library/Containers/<bundle-id>/Data/Library/Application Support/<app>/helpkit/helpkit.db` |
| net11.0-ios sim | `xcrun simctl get_app_container booted <bundle-id> data` then `Library/Application Support/helpkit/helpkit.db` |
| net11.0-ios device | Xcode > Devices > Download Container > Library/Application Support/helpkit/helpkit.db |
| net11.0-android | `adb shell "run-as <pkg> sqlite3 files/helpkit/helpkit.db ..."` |
| net11.0-windows MSIX | `%LOCALAPPDATA%\Packages\<PackageFamilyName>\LocalState\helpkit\helpkit.db` |
| net11.0-windows unpackaged | `%LOCALAPPDATA%\<AppName>\helpkit\helpkit.db` |

## Blazor companion path (Beta — placeholder)

When `Plugin.Maui.HelpKit.Blazor` lands:
1. Run the host app through Aspire (per the main e2e-testing SKILL.md webapp workflow).
2. Use Playwright MCP to drive the chat overlay.
3. Inspect the same SQLite DB at the platform path above (Blazor renders inside MAUI BlazorWebView; storage path is identical).
4. Apply the same UI / Data / Log three-level verification model.

For Alpha, skip this section. If you find yourself testing HelpKit through Playwright today, you are off-script — escalate to Captain.

## Anti-patterns to flag during review

- Calling `dotnet run` for a HelpKit-enabled MAUI sample — must use `dotnet build -t:Run -f <TFM>`.
- Asserting "build succeeded" only — HelpKit changes affect runtime SQLite + ingest behavior. Always touch all three levels.
- Skipping the system-prompt leak grep on logs — silent regression risk.
- Marking the smoke-tests pass without checking `vectors.json` exists and is non-empty — fingerprint can be present while the vector store is empty (bug surface).
