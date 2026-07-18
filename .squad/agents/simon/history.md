# Simon — History Summary

**Summarized by Scribe:** 2026-07-17T23:38:25Z
**Role focus:** Backend, data isolation, timer/session correctness, migration/tooling diagnostics, release hardening.

## Standing operating context

- SentenceStudio is a .NET MAUI Blazor Hybrid Korean language learning app with SQLite on native heads and PostgreSQL on server surfaces.
- User-owned data must always be scoped explicitly by `UserProfileId` / active user. Empty user IDs fail closed with warning and return empty/null/false/zero rather than falling through to unfiltered queries.
- When services bypass repositories and write directly to `ApplicationDbContext`, they must resolve and set `UserProfileId` themselves; the DbContext does not automatically scope data.
- Simon is the backend escalation owner for hardening work involving repository scoping, async timer/session ownership, migration/tooling gates, and release-blocker fixes.

## Durable backend learnings

- Import pipeline mapping must carry every AI DTO field explicitly into intermediate rows. `LexicalUnitType` is especially easy to drop; use the centralized defensive heuristic (multi-word => Phrase, else Word) as fallback.
- Transcript imports preserve original text in `LearningResource.Transcript`; `HarvestTranscript` controls both transcript storage and `MediaType = "Transcript"`.
- Scriban templates should be loaded with `_fileSystem.OpenAppPackageFileAsync`, parsed via `Template.Parse`, then rendered; do not reintroduce inline prompt builders for templated prompts.
- OpenAI/AI clients must use the named `openai` HttpClient + pipeline transport pattern so Polly resilience is preserved. Avoid naked client constructors.
- WebApp activity timers are circuit-scoped; MAUI registrations can be singleton. Timer operations must use immutable leases / explicit ownership rather than ambient cancel fallbacks that could hit a newer owner.

## Major shipped work summarized

- v1.1 import escalation: fixed NULL `UserProfileId`, transcript source text carry-through, and `LexicalUnitType` mapping gaps after Wash lockout; shipped after Jayne verification.
- M.E.AI 10.5 debt paydown: helped land CPM, Polly resilience, config extraction, AppLib agent-boundary assessment, and RetrievalService no-op defusing with six validation gates.
- Polly completion: finished remaining naked OpenAI client wiring in `AiService` and `HelpKitIntegration`; verified zero naked constructors remained and tests passed except one unrelated API auth baseline.
- Concern #2 timezone / freshness: added `VocabularyProgressRepository.GetByWordIdsForUserAsync`, switched freshness logic to server-side user scoping, and left carry-forward to migrate remaining safe-but-suboptimal call sites.
- macOS AppKit P5 build path: diagnosed Xcode 26.4 + Preview 5 SDK version gate; validated `-p:ValidateXcodeVersion=false` for `net11.0-macos` builds without source changes.
- Migration validation gate overhaul: fixed five defects in `scripts/validate-mobile-migrations.sh` (wrong DevFlow command, wrong surface/TFM, wrong AppKit launch model, missing Xcode bypass, wrong port/no identity check). Gate now targets macOS AppKit and verifies agent identity on port 9225.
- iOS Preview 5 run regression: traced MSB3073 exit 127 to unquoted `MlaunchPath` with spaces in `Application Support`; matched dotnet/macios#22481 and PR #25680, and documented `xcrun simctl install/launch` workaround until fixed packs ship.
- Photo-viewer release support: built iOS Release candidate, audited DX24 connectivity non-destructively, later installed the candidate in-place without wiping data, and recorded remaining physical validation boundaries.
- Cross-profile disclosure diagnosis: confirmed global preferences, stale UI state, unvalidated route IDs/unscoped reads, and resume amplification. Recovered E2E evidence manually after empty worker reports; WebApp/AppKit matrix passed 201/201 with cross-owner completion/progress/mapping rows at zero.

## 2026-07-17 release hardening ownership

Simon owns the 9-DR hardening layer merged as `c2c40812`, including timer/session isolation and data-boundary fixes on `candidate/photo-viewer-webview`.

Timer-disposal fix details:
- `VocabQuiz.razor` creates a per-start cancellation token and cancels it from both back navigation and dispose paths before stopping any captured lease.
- `ActivityTimerService.StartValidatedSessionAsync` checks cancellation after awaited validation/persistence and cleans/discards owned state before a timer can start.
- `ActivityTimerService` implements idempotent `IDisposable`, clearing current state, canceling/disposing `_startCts`, unsubscribing, and disposing `_tickTimer` under `_gate`.
- Jayne's second review found a residual accept-then-cancel window; Simon fixed `startCanceled` to cancel a non-null accepted lease before returning.
- Regression coverage includes cancel-before-persistence, dispose-stops-running-timer, and accept-then-caller-cancels cleanup without progress writes.
- Coordinator independently verified full UnitTests 990/990 and timer filter 9/9 before commit `c2c40812`.

## Current carry-forward

- Release `c2c40812` is on `main` and pushed; production deploy is held per Captain.
- Future timer regressions around VocabQuiz, `ActivityTimerService`, session leases, generation ownership, or accept/cancel races should route to Simon first.
- When Preview 6+ iOS packs are available, re-test `dotnet build -t:Run -f net11.0-ios` from a path containing spaces to confirm the macios fix.
- Consider adding the iOS run workaround note to `docs/deploy-runbook.md` if it is still relevant before Preview 6 adoption.
