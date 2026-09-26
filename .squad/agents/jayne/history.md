# Jayne — History Summary

**Summarized by Scribe:** 2026-07-17T23:38:26Z
**Role focus:** Code review, E2E validation, regression design, ship/no-ship verdicts, test methodology.

## Durable validation principles

- Recurring bugs need regression tests before closure. Prefer discriminating tests that would fail against the known bug, not vacuous coverage.
- Running-app evidence matters for UI behavior. Screenshots, Playwright sequences, DevFlow CDP state, browser console output, native logs, and database rows are stronger than static review alone.
- Accessibility snapshots can lag or omit exact Blazor DOM state; use DevFlow CDP for exact WebView/DOM reads when visual or data-debug state is required.
- Empty worker reports are infrastructure failures, not evidence. Recover manually with exact state channels and database checks before approving.
- Distinguish required proof gates from optional confidence gates. Optional physical-device validation must not continue after Captain closes the gate.

## Import and NumberDrill history

- Import validation covered vocabulary CSV, phrases, transcript import, confidence tiers, checkbox override, cancel pollution, and LexicalUnitType backfill. Jayne reproduced the Phrases+Pipe bug where delimiter-aware parsing was bypassed and later verified the fix at commit `3c7a4cc`.
- Import Complete redesign E2E validated summary cards, row tables, filters, back navigation, vocabulary links, failed-row resilience, and clean logs.
- NumberDrill combo audit mapped 30 context/sub-mode combinations, hid non-working modes, and later validated Wave 4 via Aspire + Playwright with zero console errors. iOS deeper validation was sometimes blocked by automation/CDP readiness, but startup crash classes were covered.

## Regression-test ownership highlights

- Focus Vocabulary scaffolding covered plan-level focus IDs, preview projection, deterministic ordering, propagation, min/max gates, route params, stable IDs, storage round-trips, legacy reconstruction, and multi-tenant scoping.
- Timezone/cache regressions: Jayne added tests for per-user timezone math, cache freshness, multi-tenant freshness isolation, and recurrence guards. Carry-forward remains for banned-symbol/source-scan guards and a WebAppPlanDateContext integration test.
- Vocab Quiz session/resume: 11 tests guard snapshot keys, JSON round-trip, save/update/supersede semantics, resumable lookup scoping, completed/abandoned exclusion, empty-user guard, and multi-tenant isolation.
- Persistent Vocab Quiz demonstrations: tests guard recognition/production counters, known-word shortcut behavior, snapshot round-trip, attempt increments, wrong-answer non-reset behavior, and non-quiz isolation.
- Transcript harvest: tests covered FromReading insert/dedup/cap/validation, segmenter modes, user-scoped idempotent harvesting with translations, multi-tenant isolation, and content import capture.
- FuzzyMatcher: E2E verified length-gate fix in WebApp via Aspire + Playwright.

## Recent photo, text, hint, and cross-profile validation

- Vocab Quiz photo/text preference: validated macOS AppKit via DevFlow, migration discovery/reversibility, preference persistence, photo toggle state, grading isolation, and zero console errors.
- Vocab Quiz photo/text + iOS: validated WebApp and iOS simulator paths including fullscreen interactions, preference persistence, photo decode/rendering, safe-area close, focus restoration, and clean logs. Temporary local test image URI DB edits were restored and documented; no production/personal data or physical device was touched.
- Native text feedback reveal: first code review rejected a default-state leak where hidden text could appear before grading. Zoe's independent revision was re-reviewed and approved; WebApp and macOS AppKit E2E confirmed hidden-before-answer, reveal-on-feedback, reset-on-next-item, override, non-photo behavior, and target-language invariance.
- Sentence hints: approved Wash's explicit-user one-query tenant-proof hint projection; E2E covered ranked IDs, no native/foreign content, toggle cycling, feedback transitions, reset, fullscreen, resume, no-pool, level fallback, scoped progress/logs, and migration validation.
- Cross-profile disclosure: reviewed and approved layered data/UI fixes. Reconstructed attack chain (stale selection -> direct route -> activity initiation -> progress/completion persistence), verified all vectors blocked, and accepted WebApp/AppKit matrix evidence at 201/201 with cross-owner rows at zero.
- Photo Viewer architecture: Captain accepted WebApp + iOS simulator 7/7 gesture matrix as sufficient; optional physical XCTest confidence gate was stopped. Future test plans must classify gates as required vs optional before execution.

## 2026-07-17 release review and merge update

Jayne performed focused pre-push reviews for the photo-viewer WebView + 9-DR hardening release.

- jayne-30 verdict: MINOR, safe to push, but found a VocabQuiz async timer disposal race where `activityTimerLease` was assigned only after awaited validated start. Consequence was single-tenant timer leakage/inflated minutes, not cross-tenant data leakage. Recommended component-lifetime cancellation and/or disposable `ActivityTimerService` with regression tests.
- Simon fixed the race with component-lifetime CTS threading, cancellation checks after awaited work, owned-state discard, and idempotent timer disposal.
- jayne-31 verdict: MINOR after re-review. Broad race, Dispose idempotency, CTS lifecycle, and post-await cleanup were cleared; one residual accept-then-cancel branch could still drop a non-null accepted lease.
- Simon fixed the residual by canceling a non-null lease before returning from `startCanceled`, adding `ValidatedStart_AcceptThenCallerCancelsLeaseCleanup_StopsTimerWithoutProgressUpdate`.
- Coordinator independently verified full UnitTests 990/990 and timer filter 9/9, committed the 39-file release layer as `c2c40812`, fast-forward merged to `main`, and pushed. Production deploy is held per Captain.

## Current carry-forward

- Treat `c2c40812` as the merged release-hardening baseline for future test expectations.
- Future VocabQuiz timer/session review should include accept-then-cancel, cancel-before-persistence, dispose-while-awaiting, and no-progress-write assertions.
- Future E2E matrices should keep required proof gates separate from optional confidence gates and avoid physical-device work unless explicitly reopened.


## 2026-08-06: YouTube import production failure fix (troubleshooter support)

**Session:** 2026-08-06T12-22 — Production webapp investigation + E2E regression scenarios + NuGet diagnostics
**Outcome:** Partial success — confirmed poor error UX, defined E2E scenarios, diagnosed NU1101 root cause, identified E2E blocker

**Jayne's contributions:**
- Production test account investigation: Account blocked by email confirmation on `.test` domain; cannot fully reproduce signature with auth
- Architecture analysis: MediaImport.razor calls YouTubeImportService directly (not via API endpoint); YoutubeExplode 6.6.0 works locally; hypothesis: Azure Container Apps IP range rate-limited or consent-gated by YouTube
- UI error quality assessment: Raw exception messages shown, no retry button, no error differentiation, no correlation ID surfacing
- Defined 7 E2E regression scenarios (R1-R7): valid video, no transcript, unavailable, rate-limit, timeout, channel import, failed import history
- WebApp build diagnosis: Confirmed NU1101 as user-level nuget.org disabled + repo config not clearing `disabledPackageSources` (reference NuGet/Home#14530)
- E2E blocker identified: Docker Desktop macOS admin dialog for `com.docker.vmnetd`; no destructive action taken

**Status:** Troubleshooting complete; WebApp now builds after Kaylee's NuGet fix. E2E blocked pending Docker admin dialog approval and Captain's production deployment authorization.
