## Active Decisions

(Most recent decisions below. Archived decisions in `decisions/archive/` including `decisions-archive-2026-07-15.md` and earlier archives.)

---

### 2026-07-15T19-17-33: Prototype B uses a DEBUG-only iOS native overlay above the mounted BlazorWebView

**By:** Simon
**What:** Prototype B uses a DEBUG-only iOS native overlay above the mounted BlazorWebView
**References:** src/SentenceStudio.iOS/BlazorHostPage.cs, src/SentenceStudio.iOS/NativePhotoViewerOverlayHandler.cs, src/SentenceStudio.iOS/MauiProgram.cs, src/SentenceStudio.AppLib/Controls/NativePhotoViewerOverlay.cs, src/SentenceStudio.Shared/Abstractions/PhotoViewerHostCoordinator.cs, src/SentenceStudio.UI/Pages/VocabQuiz.razor, tests/SentenceStudio.UnitTests/Services/PhotoViewerServiceTests.cs, plan.md Prototype B
**Why:** Implemented the native photo prototype as a reusable MAUI host view in SentenceStudio.AppLib with a UIKit handler in the iOS head. The iOS root remains a Grid with BlazorWebView first and the inactive/input-transparent overlay above it; no Shell, modal page, navigation stack, MauiReactor, or gesture dependency is introduced. DEBUG replaces the default photo-viewer service with the existing preference selector plus NativePhotoViewerService; Release keeps DefaultPhotoViewerService and the Razor viewer. UIScrollView owns pinch anchoring, momentum, bounce, 1x-4x zoom, centering, and double-tap zoom/reset. Only the safe-area close button dismisses; image/backdrop taps intentionally do not. Loading uses cancellable HttpClient requests, stale-generation rejection, explicit loading/error states, and an 8-entry/64 MiB encoded-data LRU cache. Accessibility uses generic labels and stable identifiers without logging image URLs; DEBUG viewport state contains only zoom, offsets, load state, and reset count.

Learning Value Gate: learning objective and language-role behavior are unchanged—the photo remains an optional mnemonic while the existing Vocab Quiz direction/text gates continue to guarantee target-language exposure or retrieval. No new direction, modality, default, answer set, or text-hiding state is introduced. The native request now uses the generic Full screen photo viewer label rather than vocabulary-specific alt text, reducing answer leakage risk. Default/no-preference behavior remains the verified Razor viewer; native is reachable only when debug_photo_viewer_prototype=native in DEBUG.

---

### 2026-07-15T20-59-58: Use DevFlow CDP for exact iOS BlazorWebView photo-viewer state; guessed HTTP JS routes are invalid

**By:** Simon
**What:** Use DevFlow CDP for exact iOS BlazorWebView photo-viewer state; guessed HTTP JS routes are invalid
**References:** https://learn.microsoft.com/en-us/dotnet/maui/developer-tools/devflow/blazor-cdp?view=net-maui-10.0, https://github.com/dotnet/maui-labs/issues/232, src/SentenceStudio.iOS/MauiProgram.cs, src/SentenceStudio.iOS/SentenceStudio.iOS.csproj, src/SentenceStudio.UI/wwwroot/js/photo-viewer-gestures.js, src/SentenceStudio.AppLib/Controls/NativePhotoViewerDebugState.cs, session artifact: files/photo-viewer-comparison/devflow-cdp-evidence.txt
**Why:** On iOS Simulator with SentenceStudio Debug, Microsoft.Maui.DevFlow.Agent/Blazor 0.25.0-dev correctly registers the Blazor CDP bridge on explicit agent port 9224. Exact DOM/state reads must use `maui devflow webview Runtime evaluate <expression> -ap 9224 -p ios` (or canonical POST `/api/v1/webview/evaluate` with a Runtime.evaluate payload) after verifying agent identity and `webview status` reports a ready context. The earlier `/api/v1/execute/js`, `/api/v1/js/eval`, `/api/v1/webview/eval`, `/api/v1/blazor/eval`, and `/api/v1/hybrid/js` probes are unsupported routes and correctly return 404; the MAUI visual tree also intentionally does not expose Blazor DOM children. For the WebView prototype, read `data-debug-*` via CDP and return `JSON.stringify(...)` for stable by-value output. WKWebView accessibility currently exposes dialog/image semantics but not custom `data-debug-*`, so it is not an exact numeric-state channel. The native prototype's `NativePhotoViewerDebugState.Snapshot` is exact but currently internal; a separate narrow DEBUG-only exposure is required for symmetric automated comparison. No product code change is part of this decision.

---

### 2026-07-16T12-51-57: Reveal hidden native text during Vocab Quiz feedback

**By:** Kaylee
**What:** Reveal hidden native photo prompts during Vocab Quiz feedback (approved by Zoe and Jayne)
**References:** src/SentenceStudio.UI/Pages/VocabQuiz.razor, src/SentenceStudio.Shared/Services/VocabQuizPhotoTextPolicy.cs, tests/SentenceStudio.UnitTests/Services/VocabQuizPhotoTextPolicyTests.cs, .claude/skills/e2e-testing/references/quiz-activities.md
**Why:** The native-language text for photo-based Vocab Quiz turns remains hidden before answer submission and reveals synchronously with Correct/Incorrect feedback (both states). Implemented as a computed display policy using the existing showAnswer feedback state; LoadCurrentItem resets showAnswer before the next item/restart, preventing answer leakage. Learning objective: after picture-to-target retrieval, the learner sees the native-language meaning during corrective feedback while retaining target-language exposure in the response/answer. Language roles remain unchanged: TargetToNative photo turns always show target text; NativeToTarget photo turns may hide native text before grading but reveal it for both correct and incorrect feedback; Mixed applies the same rule per turn; non-photo turns preserve configured text visibility. User override remains continuously visible. Image alt text, audio direction, answer sets, grading, timers, progress recording, fullscreen gestures, defaults, and reset behaviors are unchanged. Verified with 31 policy tests covering pre-answer hiding, correct/incorrect reveal, next-item reset, target-language invariance, override stability, and non-photo behavior. WebApp build and all E2E matrix scenarios passed.

---

### 2026-07-16T15-02-49: Gate and rank Vocab Quiz sentence hints safely

**By:** Squad
**What:** Gate and rank Vocab Quiz sentence hints safely
**References:** Captain requirement 2026-07-16, src/SentenceStudio.Shared/Models/ExampleSentence.cs, src/SentenceStudio.Shared/Models/UserProfile.cs, src/SentenceStudio.Shared/Data/ExampleSentenceRepository.cs, .squad/skills/learning-value-gate/SKILL.md
**Why:** Vocab Quiz sample-sentence hints are offered only on target-language prompt turns and only when scoped eligible examples exist. Before grading, render up to three target-language sentences and never native translations or answer-side artifacts. Load candidates through an explicit-user, fail-closed batch repository path; never trust unscoped word/resource IDs or perform per-turn queries. Rank known DifficultyLevel values nearest a documented CEFR heuristic (A1=1, A2=2, B1=3, B2=4, C1/C2=5), then IsCore, verification quality, CreatedAt, and ID; when level metadata is absent, fall back deterministically. Keep the hint opt-in and reset it on item changes/resume/fullscreen.

---

### 2026-07-16T15-13-37: Use an exact owned resource mapping for Vocab Quiz sentence hints

**By:** Wash
**What:** Use an exact owned resource mapping for Vocab Quiz sentence hints
**References:** src/SentenceStudio.Shared/Data/ExampleSentenceRepository.cs, src/SentenceStudio.Shared/Models/VocabQuizSentenceHint.cs, tests/SentenceStudio.UnitTests/Data/ExampleSentenceRepositoryQuizHintTests.cs, decisions/inbox/Squad-gate-and-rank-vocab-quiz-sentence-hints-safely.md
**Why:** Implemented `ExampleSentenceRepository.GetQuizHintsForWordsAsync` as an explicit-user, fail-closed batch path. A sentence is eligible only when its `(VocabularyWordId, LearningResourceId)` exactly matches a `ResourceVocabularyMapping` whose `LearningResource.UserProfileId` equals the supplied user; null links, mismatched links, and foreign resources are omitted. One provider-neutral EF query starts from `UserProfile` and left-joins owned candidates so an unknown user can be distinguished and warned without a second query. The method rejects more than 20 distinct IDs, returns at most three immutable target-only `VocabQuizSentenceHint` records per word, and ranks by recognized CEFR-to-difficulty distance, IsCore, Verified status, CreatedAt, and ID. Null/unrecognized CEFR uses the level-neutral tie-breakers. No schema, persistence, HTTP API, DI, package, UI, or localization changes were made.

---

### 2026-07-16T17-16-01: Fail closed on stale cross-profile selection identifiers

**By:** Squad
**What:** Fail closed on stale cross-profile selection identifiers
**References:** AGENTS.md multi-tenant data scoping rule, accepted autonomously
**Why:** Choose My Own resource/skill selection is per active profile, never global. Ignore legacy global selection values, reconcile backing state against currently owned options, and revalidate immediately before navigation. Vocab Quiz must validate all direct route and resume identifiers against the explicit active user before starting timers, sessions, reads, or writes. Exact-ID resource/skill reads and write paths fail closed with a warning on empty, missing, or foreign user ownership; no unscoped fallback and no cross-owner completion references.

---

### 2026-07-16T21-06-08: Accept simulator proof for photo viewer architecture

**By:** Captain
**What:** Accept simulator proof for photo viewer architecture
**References:** photo-viewer-comparison, dx24-gesture-gate, WebApp + iOS simulator 7/7 gesture matrix
**Why:** Captain explicitly stopped the optional physical device XCTest gesture gate. Existing WebApp and iOS simulator evidence, including the complete 7/7 gesture fixture, is sufficient to finalize the WebView photo viewer architecture. Physical-device readiness is not authorization or a requirement to run optional architecture validation. Do not pursue or repair the physical signed-runner workflow unless Captain explicitly reopens that work.

---


---

### 2026-07-16T21-14-08: Keep skill surveys non-blocking and evidence-first
**By:** Captain
**What:** Keep skill surveys non-blocking and evidence-first
**References:** hypothesis-driven-debugging, skill-feedback
**Why:** Inline skill surveys must not block the user's workflow. Before requesting a rating, present a concise report naming the skill, why it ran, what work it performed, and its concrete outcome; feedback collection should remain optional and asynchronous.

---

### 2026-07-17T16:53:53-0500: Aspire stalls in git worktrees — standalone WebApp bypass (tooling-friction dogfood)

**By:** Squad (Coordinator)
**What:** In a git worktree checkout, `aspire run` (and Aspire AppHost via DCP) reliably STALLS: DCP launches each resource with `dotnet run`, which triggers cold parallel cold-builds that never reach a listening state, so the dashboard hangs on "starting" and the WebApp never binds. This blocks worktree-based WebApp E2E.

**Bypass (verified working) — run the WebApp standalone against the existing Aspire Postgres container:**
```
dotnet run --project src/SentenceStudio.WebApp/SentenceStudio.WebApp.csproj --no-build -c Debug
```
with env:
- `ConnectionStrings__sentencestudio=Host=127.0.0.1;Port=25355;Username=dbadmin;******;Database=sentencestudio`
- `ASPNETCORE_ENVIRONMENT=Development`, `DOTNET_ENVIRONMENT=Development`
- **REQUIRED** `AI__OpenAI__Endpoint=https://not-configured.openai.azure.com/openai/v1` — makes the `IChatClient` graph constructable (lazy factory, no network at validation); without it, DI validation throws at boot.

**QUIRK:** the app binds `http://localhost:5172` (a launchSettings profile overrides `ASPNETCORE_URLS`), NOT the URLS env value. Always grep "Now listening" in the log to confirm the live port.

**Why it matters (dogfood):** This is a genuine .NET MAUI/Aspire developer-experience friction signal — Aspire's worktree behavior is a candidate upstream report (aspire DCP cold-build-in-worktree stall). Correctly NOT rabbit-holed mid-release, but captured so the next dev isn't blocked and so we can file/So we can attach a repro to dotnet/aspire later. Related skill: `.claude/skills/aspire-recovery`.

---

### 2026-07-17T16:53:53-0500: Photo-viewer + 9-DR hardening release — pre-merge evidence

**By:** Squad (Coordinator), on behalf of Captain (daortin)
**Branch:** `candidate/photo-viewer-webview` @ `0b18cdbb` (feature commits) + uncommitted 9-DR layer (author = Simon)
**What:** Consolidated verification evidence gating the merge-to-main of the photo-viewer WebView gestures, native-text reveal, level-aware sentence hints, cross-profile isolation, and Simon's 9-blocker (DR-001…DR-009) hardening layer.

**Evidence collected this session (running-host, Simon's uncommitted tree):**
- .NET UnitTests: **986 / 987**. The single failure `SharedIngestProcessorTests.YouTubeUrlItem_VideoImportKickedOff_...` is a pre-existing fire-and-forget async race in the video-import pipeline — OUTSIDE the 9-DR scope, NOT in Simon's diff, and passes **2/2 in isolation**. Baseline noise, not a regression.
- JS tests: **56 / 56** (photo-viewer-math 27, gestures 25, modal 4). Note: `node --test tests/js/` (directory form) mis-resolves on node 22; run per-file.
- DR-001 focus-trap: **PASSED** on the LIVE WebApp (standalone on :5172) via Simon's `revision-webapp-modal.py` contract test against the served `photo-viewer-modal.js` — sibling placement, inert background, initial focus on close, Tab/Shift+Tab wraparound, nested-dialog rejected.
- DR-006/008 cross-tenant: prior authoritative E2E (`e2e-cross-profile-resource-fix/verification-summary.json`, HEAD `eb5b1cc6`) W1–W5 + gateC/gateD on BOTH WebApp AND macOS AppKit — `crossOwnerCompletionRows:0, crossOwnerProgressRows:0, crossOwnerMappingRows:0`. Re-confirmed live: freshly-registered `squad-kaylee` (isolated Korean owner) sees 0 vocabulary and only her own resources; none of jayne's 12 or other profiles' 76/105 leak in.
- Independent reviews: jayne-29 **LIFT REJECT** (all 9 DRs fixed, no regressions); zoe-33 **ARCHITECTURE APPROVED** (1 non-blocking follow-up: give scoped `ActivityTimerService` an `IAsyncDisposable` that self-disposes its `System.Timers.Timer` — mitigated because all ~13 activity pages stop the timer on dispose). Neither reviewer is a locked-out author.

**Staged commit scope:** 39 files (31 modified + 8 new), +2445/−595, all under `src/` and `tests/`. `.squad/session-files/` E2E evidence deliberately EXCLUDED from the commit.

**Known residual gaps (presented to Captain, not silently closed):**
- DR-002 concurrent-circuit timer isolation: code-reviewed + unit-tested (scoped DI + generation/lease); a live two-circuit race demo is limited by single Playwright auth context.
- macOS AppKit DR-001 modal on WKWebView: the identical `photo-viewer-modal.js` RCL asset already passed the live WebApp contract and 4/4 unit tests; a fresh macOS build to re-open the modal in WKWebView is optional belt-and-suspenders.

**Why:** Records the objective merge gate so the decision to push is auditable. STOP at merge boundary — production deploy only on explicit Captain go-ahead.

---

### 2026-07-17T17:10:00-0500: Pre-push /review verdict (Jayne code-review) — MINOR, safe to push

**By:** Squad (Coordinator) — code-review agent jayne-30
**Scope reviewed:** staged 9-DR diff, 39 files, +2445/−595, branch `candidate/photo-viewer-webview`.
**Verdict:** **MINOR — safe to push.** Independent rebuild clean (0 errors), 987 unit + 56 JS tests pass.

**Verified clean:**
- Multi-tenant fail-closed (DR-006/008): every touched read/write refuses on empty/foreign user; `GetWordByTargetTermAsync`, `GetAllVocabularyWordsWithResourcesAsync`, `SearchVocabularyWordsAsync`, `GetQuizHintsForWordsAsync`, all `ProgressService` mutators, `VocabQuizLaunchValidator`, `RepositoryMutationOutcome.From` overloads. Covered by CrossProfileRepositoryBoundaryTests + CrossProfileAdHocProgressTests.
- Timer isolation (DR-002…005): scoped (WebApp) vs singleton (MAUI); `OwnsCurrentLocked` (SessionId+Generation+UserId+ActivityType) makes stale continuations no-ops; all mutations gated under `_gate`. No cross-circuit path.
- Focus trap (DR-001/009): real sibling of inert content, nested-dialog rejected, Tab/Shift+Tab wraparound correct, `FullscreenDialogLifecycle` prevents double-open/re-entrant deadlock. 4/4 modal tests.

**One finding (Medium/Medium), CONVERGENT with zoe-33's follow-up:**
`VocabQuiz.EnsureActivityTimerStartedAsync` (VocabQuiz.razor ~1504–1521) assigns `activityTimerLease` only AFTER `await StartValidatedSessionAsync`. `BeginSession` sets `_currentLease`/`_startCts` synchronously but the tick timer starts later in `TryAcceptStart` (post-await DB validation + AI sentence-hint load). If the user navigates away mid-await, `DisposeAsync` runs `PauseAndStopOwnedSession(null)` = no-op, nothing cancels `_startCts`, and `TryAcceptStart` starts a timer for a disposed component. `ActivityTimerService` is `AddScoped` but not `IDisposable`. Consequence: per-circuit timer keeps ticking, inflating that plan item's minutes ~1/min. SINGLE-TENANT (no cross-tenant leak), no crash, self-heals on next activity launch. Only VocabQuiz exposed (async validated start); the 12 synchronous StartSession pages capture the lease synchronously so their dispose cancels.
**Fix:** cancel in-flight start on dispose regardless of lease (component-lifetime CancellationToken) and/or make ActivityTimerService IDisposable to stop `_tickTimer` on scope teardown. Add regression test.

**Why:** Final pre-push net over the exact staged bytes. Not a blocker; decision (fix-now vs track) presented to Captain at the merge boundary.

---

### 2026-07-17: Timer-disposal fix — SECOND iteration verified, release gate GREEN

**By:** Squad Coordinator (for Captain / @daortin_microsoft)

**Round 2 finding (jayne-31, MINOR):** `VocabQuiz.EnsureActivityTimerStartedAsync` — the `if (startCanceled) return false;` branch dropped a possibly non-null accepted lease, leaving a running per-circuit timer on a disposed component (narrow accept-then-cancel dispatcher window; same symptom class as the original Medium finding). Confirmed against source before acting.

**Fix (Simon, author):** `startCanceled` branch now cancels a non-null lease before returning, mirroring the invariant enforced in the block immediately below. One-line semantic fix. Added regression test `ValidatedStart_AcceptThenCallerCancelsLeaseCleanup_StopsTimerWithoutProgressUpdate` (strict mock: accepted+running lease → CancelSession → IsActive/IsRunning false, CurrentActivityId null, UpdatePlanItemProgressAsync Times.Never).

**Independent verification (Coordinator):**
- Read the actual `VocabQuiz.razor` edit — correct, mirrors existing cleanup.
- Read the new test — directly asserts the fix invariant, strict mock, no progress write.
- Full UnitTests: **990 passed / 0 failed / 990** (net10.0, clean build) — the previously-flaky SharedIngest test also passed this run.
- Timer filter: **9/9** (3 regression tests now).
- Final staged scope: **39 files, +2681/−594**, all src/+tests/, nothing stray.

**Two review rounds complete:** jayne-30 MINOR (1 finding → fixed round 1) + jayne-31 MINOR (1 residual → fixed round 2, re-verified). Both prior release reviewers (jayne-29 LIFT REJECT, zoe-33 ARCHITECTURE APPROVED) had already cleared the 9-DR layer.

**Status:** Release gate GREEN. Awaiting Captain's explicit go for direct-merge to main (author=Simon) + production deploy. Nothing committed/pushed/merged/deployed.

---

### 2026-07-17: VocabQuiz timer-disposal fix — independently verified

**By:** Squad Coordinator (for Captain / @daortin_microsoft)
**Context:** jayne-30's MINOR finding (VocabQuiz per-circuit timer can outlive component disposal in a narrow async window). Captain chose fix_first. Simon authored the fix.

**What was fixed:** `ActivityTimerService` now `IDisposable` with `_disposed` guards on all mutation paths + idempotent `Dispose()` under `_gate`; `StartValidatedSessionAsync` takes a `CancellationToken` and re-checks `ct.IsCancellationRequested` after every awaited point (releasing the lease via `CancelIfOwned` and discarding any already-persisted ad-hoc row). `VocabQuiz.razor` threads a component-lifetime CTS and cancels it in both dispose paths before `PauseAndStopOwnedSession`.

**Independent verification (Coordinator, not agent-reported):**
- Full UnitTests suite: **988 passed / 1 failed / 989** (net10.0, clean build). The +2 vs prior 987 are Simon's 2 new regression tests, both green.
- The lone failure = known-flaky `SharedIngestProcessorTests.YouTubeUrlItem_VideoImportKickedOff` — isolates GREEN (1/1), outside fix scope, accepted baseline.
- Timer filter: **8/8** incl. the 2 new tests; the 2 new tests by name **2/2**.
- Final staged scope: **39 files, +2641/−594**, all under src/+tests/, nothing stray.

**Fix delta:** 3 files, ~+214/−17, folded into the staged 9-DR layer.

**Gate status:** focused pre-push re-review (jayne-31) in flight. STOP at merge boundary for Captain's explicit go. No commit/push/merge/deploy performed.

---

### 2026-07-17: Release revision ownership — Simon owns the hardening layer

**By:** Simon
**What:** Simon owns the release hardening layer on `candidate/photo-viewer-webview`, including the VocabQuiz timer-disposal fix and related regression tests.
**Why:** The 9-DR hardening and timer-disposal changes were authored as a cohesive backend/release layer. Simon is the right owner for follow-up review questions, post-merge triage, and any timer/data isolation regressions that trace to commit `c2c40812`.

---

### 2026-07-17: Simon timer dispose fix

**By:** Simon
**What:** Thread VocabQuiz's component-lifetime cancellation into `ActivityTimerService.StartValidatedSessionAsync` instead of using an unconditional `CancelSession()` fallback when the component has not yet captured a lease.
**Why:** The scoped WebApp service has one pending timer per circuit, so an unconditional fallback would close the reported race there. The MAUI registrations are singleton, though, so a disposed VocabQuiz component with no captured lease could accidentally cancel a newer component's active timer. A component-owned `CancellationTokenSource` is precise across both lifetimes.

**What changed:**
- `VocabQuiz.razor` now creates a per-start cancellation token, passes it to `StartValidatedSessionAsync`, and cancels it from both `GoBack` and `DisposeAsync` before stopping any captured lease.
- `ActivityTimerService` now checks cancellation after awaited validation/persistence and cancels/discards owned state before `TryAcceptStart` can start `_tickTimer`.
- `ActivityTimerService` now implements idempotent `IDisposable`, clearing current state, canceling/disposing `_startCts`, unsubscribing, and disposing `_tickTimer` under `_gate`.
- Added regression coverage for cancel-before-persistence-completion and dispose-stops-running-timer.

**Verification:**
- `dotnet build tests/SentenceStudio.UnitTests/SentenceStudio.UnitTests.csproj -c Debug --nologo` passed.
- `dotnet test tests/SentenceStudio.UnitTests/SentenceStudio.UnitTests.csproj -f net10.0 -c Debug --nologo --filter 'FullyQualifiedName~ActivityTimer'` passed: 8/8.
- Full unit suite passed except the documented flaky `SharedIngestProcessorTests.YouTubeUrlItem_VideoImportKickedOff_ItemRemoved_NotifierVideoImportStarted`: 988 passed, 1 known flaky failure, 989 total.

---

### 2026-07-17: main CI is RED due to net10-CI / net11-mobile-TFM skew (PRE-EXISTING, not the photo-viewer release)

**By:** Coordinator (verifying the c2c40812 merge push), requested by Captain (David Ortinau)

**What:** After pushing the photo-viewer release merge (`c2c40812`) to `main`, both CI workflows failed at the RESTORE step — before any test ran:
- `test.yml` (Unit Tests): `NETSDK1147: workload 'ios' must be installed` for `SentenceStudio.Shared.csproj::TargetFramework=net11.0-ios`.
- `ci.yml` (Build Api/WebApp/AppLib): `NETSDK1178: Microsoft.iOS.Sdk.net10.0_26.0 pack does not exist` for the same `Shared.csproj::net11.0-ios`.

**Root cause:** `src/SentenceStudio.Shared/SentenceStudio.Shared.csproj` multi-targets `net10.0;net11.0-ios;net11.0-android;net11.0-maccatalyst;net11.0-macos`. Both workflows pin `dotnet-version: '10.0.x'` and run `dotnet workload install maui` (the **net10** MAUI workload). The net10 workload cannot restore the project's **net11** mobile TFMs, so `dotnet restore` dies. Any project that transitively references Shared (UnitTests, Api, WebApp, AppLib) fails to restore.

**Proof it is NOT the release:**
- Prior main `6ff8078f` (2026-07-15) failed with the IDENTICAL NETSDK1147 error on the same project/TFM.
- Merge commit `c2c40812` did not touch `Shared.csproj` or any `.github/workflows/*`.
- `net11.0-ios` entered `Shared.csproj` at `55105cc0` (share-to-vocabulary), days before this release.
- Local `dotnet test` was 990/990 green because Captain's machine has the net11 mobile workloads installed; the CI runner (SDK 10.0.30x + net10 maui workload) does not.

**Fix options (candidate next focus — NOT yet actioned):**
1. Bump CI `dotnet-version` to a net11 SDK (`11.0.x` preview) + `dotnet workload install maui` for that band, matching `Shared.csproj`'s mobile TFMs. Aligns CI with the repo's actual TFM matrix.
2. Or scope CI restore/build/test to the `net10.0` TFM only (`-f net10.0` / `-p:TargetFramework=net10.0`) so the mobile TFMs are never restored on the server heads/tests. Cheaper, but diverges CI coverage from the shipped mobile TFMs.
3. Or split Shared into a net10-only server slice + a net11 mobile slice (largest change).

**Status:** Reported to Captain. Merge stands (functionally clean). CI-green is deferred to an explicit next-focus decision. Deploy remains HELD.

---

### 2026-07-17: CI green strategy for net10/net11 toolchain skew

Date: 2026-07-17
Author: Zoe

## Decision

Use `IncludeMobileTargets=false` as the CI server/test slice switch. When set, `SentenceStudio.Shared` evaluates to `net10.0` only, and `SentenceStudio.AppLib` does not import MAUI SDK targets. Default local/dev builds leave the property unset and keep the full mobile target set plus MAUI SDK behavior.

## Rationale

GitHub Actions net10 lanes failed because NuGet restore walks all target frameworks of project references, so `SentenceStudio.Shared` exposed net11 mobile TFMs to a net10 SDK without net11 workloads. WebApp and AppLib target net11.0, so they need the net11 base SDK, but they do not need mobile workloads in CI when building the server/test slice. Avoiding CI mobile workload installation keeps those lanes away from preview/Xcode-coupled workload fragility.

## CI lane policy

- Api and unit-test jobs use the net10 SDK and pass `-p:IncludeMobileTargets=false` on restore, build, and test.
- WebApp and AppLib jobs use the net11 preview base SDK and pass `-p:IncludeMobileTargets=false` on restore and build.
- No CI lane installs MAUI workloads for these server/test builds.
- MAUI head builds leave `IncludeMobileTargets` unset so default developer/mobile behavior remains unchanged.

## Verification notes

Local verification confirmed `SentenceStudio.Shared` resolves to `net10.0` with the property and to `net10.0;net11.0-ios;net11.0-android;net11.0-maccatalyst;net11.0-macos` by default. UnitTests and Api passed under a pinned net10 SDK with no workloads installed. WebApp and AppLib passed under the net11 preview SDK with the property set. Shared also built for `net11.0-macos` with default mobile targets. Full macOS head build is currently blocked locally by SDK/Xcode/workload environment skew unrelated to the project change.

---

### 2026-07-17: CI local NuGet and appsettings fix

Date: 2026-07-17
Author: Zoe (Lead)

## Decision

Keep `src/NuGet.config` intact for local development and make CI disable the dev-only `localnugets` source with `dotnet nuget disable source localnugets --configfile src/NuGet.config` before restore/build/test.

Guard `SentenceStudio.AppLib`'s `appsettings.json` asset/resource items with `Exists('$(MSBuildProjectDirectory)/appsettings.json')` instead of copying `appsettings.template.json` in CI.

## Rationale

`localnugets` points at Captain's machine-local package stack and must remain available for dogfooding local MAUI/DevFlow packages. Disabling the source in the ephemeral CI checkout avoids NU1301 when the absolute path is absent without editing the committed source list or package source mappings.

`appsettings.json` is intentionally gitignored because it can hold secrets. Build-only CI should not require that file; committed `appsettings.Development.json` and `appsettings.Production.json` remain embedded unconditionally. The `Exists` guard preserves local/prod behavior when the file is present and lets fresh checkouts compile when it is absent.

## Verification

In a fresh worktree without `appsettings.json`, AppLib build reproduced CS1566 before the guard. With a simulated missing `localnugets` path, API restore reproduced NU1301 before disabling the source. After the fix, with `localnugets` disabled and pointed at a missing path, UnitTests restore exited 0, AppLib build exited 0, WebApp build exited 0, and UnitTests passed 979/979 locally.

### 2026-07-17T21:34:00-05:00: iOS build blocked by Xcode/.NET-iOS-SDK version skew

**By:** David Ortinau (Captain), via Coordinator
**What:** net11.0-ios Release build failed with "error: .NET for iOS (26.5.11546-net11-p5) requires Xcode 26.5. Current Xcode is 26.4." The default /Applications/Xcode.app is 26.4, but the only installed net11 iOS workload pack is preview 5, which is version-locked to Xcode 26.5. Preview-4 SDK band has NO iOS manifest on this machine (only emscripten/mono), so pinning back to p4 is not viable.
**Fix:** Xcode 26.5 was already installed at /Applications/Xcode_26.5.app. Build with `DEVELOPER_DIR=/Applications/Xcode_26.5.app/Contents/Developer` prefixed — scoped to the build process, no sudo, no global xcode-select change, no workload restore, no global.json edit. Rebuild succeeded 0 errors. This is the recommended fix for all future net11.0-ios builds on this machine until the default Xcode is bumped.
**Why it matters (dogfooding):** Xcode and the .NET iOS SDK are version-locked per preview (p3↔26.3, p4↔26.4, p5↔26.5). A machine with multiple Xcodes + a preview-N iOS pack whose matching Xcode is not the selected default produces a confusing hard error. Worth surfacing as .NET MAUI/iOS tooling friction.

### 2026-07-17T21:34:00-05:00: `cmd | tail` masks build failure (false-positive)

**By:** David Ortinau (Captain), via Coordinator
**What:** Piping a dotnet build to `tail` (`dotnet build ... 2>&1 | tail -40`) returns tail's exit code (0), masking a real build failure. This caused a false "build succeeded" while the actual build had failed on the Xcode skew, leaving a STALE .app that looked shippable.
**Fix:** Never pipe build commands whose exit code you need. Redirect to a log file and check `$?` directly, or check `${PIPESTATUS[0]}`. Also verify the output .app mtime is fresh before installing.
**Why it matters:** Prevents shipping a stale artifact and claiming new features are present.

### 2026-07-18T03-04-57: Fixed VocabQuiz footer un-anchoring: styled .vocab-quiz-modal-host as a full-height flex passthrough to restore the activity-shell percentage-height chain
**By:** Kaylee
**What:** Fixed VocabQuiz footer un-anchoring: styled .vocab-quiz-modal-host as a full-height flex passthrough to restore the activity-shell percentage-height chain
**References:** skill: blazor-activity-layout-shell, commit: c2c40812, src/SentenceStudio.UI/Pages/VocabQuiz.razor, src/SentenceStudio.UI/wwwroot/css/app.css
**Why:** ## Bug
On the Vocabulary Quiz page, the `activity-footer` (the "N / 10" progress span `#quiz-progress` + the green "N correct" pill `#quiz-correct-count`) stopped anchoring to the bottom of the window. It floated mid-screen with a large empty area below it. Reported by Captain (screenshot, iOS/DX24, photo "Look and answer" scenario) — but it is a shared-CSS bug, not iOS-specific.

## Root cause (confirmed)
Commit c2c40812 ("harden vocab-quiz release + fix ActivityTimer disposal race") wrapped the activity shell in a NEW outer div `<div class="vocab-quiz-modal-host">` (VocabQuiz.razor line 8, closed line 643). Its purpose is legitimate: the fullscreen photo viewer `#quiz-fullscreen-viewer` was moved to be a SIBLING of `.activity-page-wrapper` (both inside the host) so that applying `inert` to the quiz content (`QuizContentAttributes`) does not also disable the modal — see the `QuizContentAttributes` doc comment ("The dialog is a sibling, so making this subtree inert cannot disable the modal itself").

BUT `.vocab-quiz-modal-host` had ZERO CSS (no rule anywhere in app.css; no JS targets it). An undefined block div is `height: auto` (shrinks to content). The shared activity shell relies on a percentage-height chain: `.activity-page-wrapper` uses `height: calc(100% + 2rem)` (app.css ~1414), a PERCENTAGE that only resolves if its parent has a resolved height. Previously the wrapper was a direct child of `main` (which has a definite height via the `vh-100` flex chain in MainLayout.razor + 1rem `p-3` padding). With the `height:auto` host inserted between them, `100%` became indefinite → the wrapper collapsed to content height → `.activity-content { flex:1 }` only filled the short wrapper → the `flex-shrink:0` footer sat directly under the content, mid-screen. Exactly the reported symptom. VocabQuiz is the ONLY activity page with a `-modal-host` wrapper; the nine siblings place `.activity-page-wrapper` as a direct child of `main` and anchor correctly.

## Fix (chosen: passthrough, NOT wrapper removal)
Preserved the wrapper's inert/modal-sibling purpose. Gave `.vocab-quiz-modal-host` a transparent full-height flex passthrough definition in app.css (immediately after `.activity-page-wrapper`, +20 lines incl. comment referencing the skill):

```css
.vocab-quiz-modal-host {
    display: flex;
    flex-direction: column;
    height: 100%;
    min-height: 0;
}
```

Why this is geometry-preserving (verified by measurement on 2 viewports):
- `main` is `display:block` for vocab quiz (only the matching page makes `main` flex), definite height. Host `height:100%` resolves against main's content-box → definite. `.activity-page-wrapper`'s `calc(100% + 2rem)` then resolves exactly as it did when the wrapper was a direct child of main.
- No padding/margin on the host, so the wrapper's `margin:-1rem` compensation still nets out against main's 1rem padding (mobile 0-top padding also nets identically — verified).
- No `transform`/`filter`/`will-change`/`contain` on the host, so it does NOT create a containing block for the `position:fixed` photo modal / info offcanvas — they stay viewport-anchored.

Alternative considered and rejected: removing the wrapper and relocating the fullscreen viewer. Rejected because the passthrough is the minimal change and the wrapper serves a real inert-isolation purpose.

## Validation (webapp via Aspire + Playwright, squad-jayne Korean account)
- Mobile 390x844: main content-box 806px → host height 806px → wrapper 838px (=calc(100%+2rem)) → footer y=801..848, gapBelowFooter = -4px (flush, intentional safe-area bleed). Screenshot: footer pinned bottom, content fills above.
- Desktop 1280x900: main content-box 842px → host 842px → wrapper 874px → footer y=853..900, gapBelowFooter = 0px. Screenshot confirms.
- Info offcanvas `#quiz-info-panel`: position:fixed, bottom=844 (viewport bottom), full-width, backdrop present — NOT trapped by the flex host.
- Sentence-shortcut mode: wrapper retains full height (838px), content fills, footer correctly absent — layout intact.
- Photo "Look and answer" scenario: not separately reproduced (jayne has only 5 photo words, none in the sampled batch). It is covered by the content-independent height-chain proof (a photo renders inside `.activity-content` which scrolls above the flex-shrink:0 footer) and by the empirically-confirmed position:fixed non-trapping (info panel generalizes to the higher-z fullscreen viewer).

## Files changed
- src/SentenceStudio.UI/wwwroot/css/app.css (+20 lines; the `.vocab-quiz-modal-host` rule + explanatory comment). No .razor change needed.

Not committed — left for Captain to review + direct-merge.
---

### 2026-08-06T12-22-09: YouTube media-import regression plan: production failure investigation and post-fix E2E scenarios

**By:** Jayne
**What:** YouTube media-import regression plan: production failure investigation and post-fix E2E scenarios
**References:** import-and-resources.md (E2E ref), src/SentenceStudio.UI/Pages/MediaImport.razor, src/SentenceStudio.Shared/Services/YouTubeImportService.cs, src/SentenceStudio.Api/ImportEndpoints.cs
**Why:** ## Investigation Summary (2026-08-06 ~12:15 UTC)

Captain reported consistent YouTube media-import failure on production webapp (`webapp.agreeablesky-76d2f81f.westus3.azurecontainerapps.io`).

### Architecture Findings

1. **The MediaImport.razor page (`/import`) calls YouTubeImportService DIRECTLY** — not via the API `/api/imports` endpoint. The page injects `YouTubeImportService` and calls `GetVideoMetadataAsync()` / `GetAvailableTranscriptsAsync()` synchronously in the Blazor Server circuit.
2. **YoutubeExplode 6.6.0** is the library version (pinned in `Directory.Packages.props`).
3. **Error surfacing**: The page catches exceptions and shows `errorMessage = $"Error fetching transcripts: {ex.Message}"` — raw exception text, no user-friendly wrapper.

### Reproduction Results

- YoutubeExplode 6.6.0 successfully fetched metadata + transcripts for a well-known English video from local machine (2026-08-06T12:20:59Z).
- Could NOT authenticate to production with squad-jayne test account (requires email confirmation on `.test` domain).
- **Root cause hypothesis**: Azure Container Apps IP range is likely rate-limited or consent-gated by YouTube. YoutubeExplode uses HTTP scraping of YouTube's internal APIs — Azure datacenter IPs are frequently blocked. This is a KNOWN recurring issue with YoutubeExplode in cloud environments.

### UI Error Quality Assessment: POOR

- Error shows raw exception message (e.g., `"Error fetching transcripts: Failed to fetch transcripts: ..."`)
- No retry button on the single-video tab's fetch step
- No differentiation between "video unavailable", "rate limited", "no transcripts in target language", and "network error"
- No correlation/trace ID surfaced to user

### Post-Fix E2E Verification Scenarios

Per `import-and-resources.md` reference, the canonical flow is:
1. Navigate to `/import` → paste YouTube URL → click Fetch
2. Transcript text appears; language dropdown populated
3. Select language → transcript updates
4. Click "Polish with AI" → reformatted (30-60s)
5. Click Save → redirects to `/resources/edit/{id}`

**Regression scenarios that MUST pass after fix:**

| # | Scenario | Expected |
|---|----------|----------|
| R1 | Valid Korean video URL → Fetch | Transcript appears, Korean track auto-selected |
| R2 | Valid video with no Korean track | Clear error: "No Korean transcript available" |
| R3 | Invalid/unavailable video URL | User-friendly error, not raw exception |
| R4 | YouTube rate-limit/block (simulate) | Retry guidance, not cryptic error |
| R5 | Network timeout | Timeout error with retry option |
| R6 | Channel tab → TriggerCheck with new video | Background import completes, appears in history |
| R7 | Import history shows failed import | Error message visible, retry button works |

### Acceptance Criteria for Fix

1. **Structured error messages**: Map YoutubeExplode exceptions to user-friendly categories (unavailable, rate-limited, no transcripts, network error).
2. **Retry affordance**: Single-video fetch failure should show a "Try Again" button without clearing input.
3. **If Azure IP blocking is root cause**: Consider server-side proxy, rotating user-agent, or fallback to YouTube Data API v3 with API key.
4. **Correlation**: Log a correlation ID server-side that can be referenced in telemetry.

---

### 2026-08-06T12-24-19: YouTube import production failure: root cause is YoutubeExplode 6.5.6 using deprecated YouTube client; fix is upgrade to 6.6 + enable Azure Monitor telemetry

**By:** Wash (REJECTED by Zoe)
**What:** YouTube import production failure: root cause is YoutubeExplode 6.5.6 using deprecated YouTube client; fix is upgrade to 6.6 + enable Azure Monitor telemetry
**References:** YoutubeExplode PR #936: Switch to ANDROID_VR client, YoutubeExplode 6.5.7 release notes
**Why:** ## Production Evidence (2026-08-06 ~12:11 UTC)

Container Apps logs from `webapp` in `rg-sstudio-prod-biz` show:
- `YoutubeExplode.Exceptions.VideoUnavailableException: Video 'X' is not available.`
- Multiple distinct video IDs ALL failing with same exception
- Error path: `YouTubeImportService.GetVideoMetadataAsync` → `VideoClient.GetAsync` → `VideoController.GetVideoWatchPageAsync`

## Root Cause

YoutubeExplode **6.5.6** (Oct 2025) uses the default YouTube web client which YouTube has since locked behind PO token validation. The library cannot fetch video pages without the token, so ALL videos appear "unavailable".

**Fix:** YoutubeExplode **6.5.7** (Feb 2026) switched to ANDROID_VR client to bypass PO token requirement. v6.6 (Apr 2026) is latest stable.

## Why App Insights showed NOTHING

The Azure Monitor exporter was **commented out** in `src/SentenceStudio.WebServiceDefaults/Extensions.cs` (lines 73-78). The OTLP exporter only fires when `OTEL_EXPORTER_OTLP_ENDPOINT` is set (local Aspire only). Production Container Apps had zero telemetry flowing.

## Changes Made

1. `Directory.Packages.props`: YoutubeExplode 6.5.6 → 6.6.0
2. `src/SentenceStudio.WebServiceDefaults/Extensions.cs`: Enabled Azure Monitor Exporter for traces, metrics, and logs when `AzureMonitor:ConnectionString` or `APPLICATIONINSIGHTS_CONNECTION_STRING` is configured
3. `src/SentenceStudio.WebServiceDefaults/SentenceStudio.WebServiceDefaults.csproj`: Added `Azure.Monitor.OpenTelemetry.Exporter` package reference
4. `src/SentenceStudio.Shared/Services/YouTubeImportService.cs`: Added `VideoImportException` structured exception class, catch specific `VideoUnavailableException`/`RequestLimitExceededException`/`HttpRequestException` with user-safe messages and error codes
5. `src/SentenceStudio.Shared/Services/VideoImportPipelineService.cs`: Catch `VideoImportException` specifically for structured logging with `{ErrorCode}`
6. `tests/SentenceStudio.UnitTests/Services/VideoImportPipelineErrorHandlingTests.cs`: Regression tests

## Limitation

Build cannot complete locally due to pre-existing workload registration corruption (`macos` workload metadata requires elevated permissions). CI should build fine. Captain needs to run `sudo dotnet workload restore` to fix the local environment.

**STATUS: REJECTED** — Zoe review findings: WebServiceDefaults is dead (no project references it), tests invalid (Moq on sealed AudioAnalyzer, non-virtual methods), error classification incomplete. Locked out; Simon selected for independent revision.

---

### 2026-08-06T12-45-40: YouTube import production failure: root cause, error classification, telemetry gap fix

**By:** Simon (REJECTED by Zoe)
**What:** YouTube import production failure: root cause, error classification, telemetry gap fix
**References:** Zoe reviewer rejection, YoutubeExplode PR #936, aspnetcore-azure-monitor SKILL.md
**Why:** ## Root Cause

YoutubeExplode 6.5.6 uses a deprecated YouTube client (WEB) that was blocked by YouTube's PO-token requirement. All video imports fail with VideoUnavailableException at VideoController.GetVideoWatchPageAsync. Upstream fix: YoutubeExplode 6.5.7+ (PR #936) switches to ANDROID_VR client. CPM bump to 6.6.0 resolves the provider-side failure.

## What Was Changed (Revision of Wash's Rejected Artifact)

### Reverted
- **WebServiceDefaults edits** — WebServiceDefaults is dead (no project references it). All deployed hosts use ServiceDefaults, which already has correct Azure Monitor exporters at Extensions.cs ~118-136 behind `#if !DEBUG`.

### Error Classification (YouTubeImportService.cs)
- Introduced `IYouTubeImportService` interface (testable seam) with 4 methods: GetVideoMetadataAsync, GetAvailableTranscriptsAsync, DownloadTranscriptTextAsync, ExtractAudioClipAsync.
- **ALL four methods** now classify: VideoUnavailableException → VideoUnavailable, RequestLimitExceededException → RateLimited, HttpRequestException → NetworkError, catch-all → Unknown.
- **OperationCanceledException is re-thrown** (not reclassified).
- Shared classifier helpers (ClassifyVideoUnavailable, ClassifyRateLimited, ClassifyNetwork) — no video ID or URL in user-facing messages.
- `VideoImportException` carries ErrorCode + sanitized Message + inner exception for server-side diagnostics.

### Pipeline (VideoImportPipelineService.cs)
- Uses `IYouTubeImportService` instead of concrete type.
- OperationCanceledException catch block: logs, persists failure, **re-throws** (preserving cancellation semantics).
- Unclassified Exception catch: persists generic sanitized message ("An unexpected error occurred during import.") — never leaks raw ex.Message to DB/UI.

### DI Registrations
- Updated in: CoreServiceExtensions.cs (AppLib), Program.cs (Workers), Program.cs (API) — all use `AddSingleton<IYouTubeImportService, YouTubeImportService>()`.

### Telemetry Gap Fix
- **Root cause of App Insights blindness**: Only API had `AzureMonitor:ConnectionString` in appsettings.Production.json. Workers (where VideoImportPipelineService runs) and WebApp had NO connection string — completely invisible to App Insights.
- **Evidence**: `az monitor app-insights query` over 7 days shows only one cloud_RoleName: `[cae-rsn72awybem6s]/SentenceStudio.Api` (15 rows). Workers/WebApp/Marketing/Cache: 0 rows. The `exceptions` table is empty for the 12:00-12:30 UTC window because the pipeline runs in Workers, which doesn't export to App Insights.
- **Fix**: Added `AzureMonitor:ConnectionString` to `src/SentenceStudio.Workers/appsettings.Production.json` (new file) and `src/SentenceStudio.WebApp/appsettings.Production.json`. The ServiceDefaults Release-mode Azure Monitor exporter wiring already exists — it just needed the config key to be present.
- No env var mismatch: ServiceDefaults reads `AzureMonitor:ConnectionString` (not `APPLICATIONINSIGHTS_CONNECTION_STRING`). No container env vars were set for either key. File-embedded config is the delivery path, consistent with how API already works.

### Tests (VideoImportErrorHandlingTests.cs)
- Replaced Wash's broken test file (Moq on sealed AudioAnalyzer, non-virtual YouTubeImportService methods).
- Uses `FakeYouTubeImportService` implementing `IYouTubeImportService` — no Moq needed for the YouTube seam.
- Uses SQLite in-memory (matching project convention) instead of InMemoryDatabase (not referenced).
- 9 tests: exception model (3), pipeline classified errors (3), sanitized unclassified message (1), cancellation passthrough (1), log verification (1).
- Full suite: 999/999 pass, 0 regressions.

## Remaining E2E Limitation
- Production fix is NOT deployed (no deploy authorized).
- Full prod verification requires `azd deploy` + post-deploy-validate.sh.
- Workers telemetry fix will only take effect after next Release deployment (ServiceDefaults Azure Monitor is gated by `#if !DEBUG`).

**STATUS: REJECTED** — Zoe review findings: MediaImport.razor:275 and ChannelDetail.razor:219 still inject concrete `YouTubeImportService` instead of interface. `AddSingleton<IYouTubeImportService, YouTubeImportService>()` does NOT self-register concrete type; pages will throw InvalidOperationException at activation. Locked out; Kaylee selected for final revision.

---

### 2026-08-06T12-59-13: Fix DI mismatch: Blazor pages must inject IYouTubeImportService, not concrete YouTubeImportService

**By:** Kaylee (APPROVED by Zoe)
**What:** Fix DI mismatch: Blazor pages must inject IYouTubeImportService, not concrete YouTubeImportService
**References:** Zoe review finding: concrete injection mismatch, Simon's IYouTubeImportService interface introduction
**Why:** ## Context

Simon introduced `IYouTubeImportService` and changed all DI registrations (CoreServiceExtensions, Api/Program.cs, Workers/Program.cs) to `AddSingleton<IYouTubeImportService, YouTubeImportService>()`. However, two Blazor pages still injected the concrete type:

- `MediaImport.razor` line 275: `[Inject] private YouTubeImportService ImportSvc`
- `ChannelDetail.razor` line 219: `[Inject] private YouTubeImportService? ImportSvc`

`AddSingleton<TService, TImpl>()` does NOT self-register TImpl in the container. Any `@inject` or `[Inject]` requesting the concrete type throws `InvalidOperationException` at runtime — the page fails to activate.

## Decision

Changed both pages to inject `IYouTubeImportService` instead of the concrete type. This is the correct consumer pattern when an interface seam exists. All three methods called by MediaImport (`GetVideoMetadataAsync`, `GetAvailableTranscriptsAsync`, `DownloadTranscriptTextAsync`) are defined on the interface.

## Regression guard

Added `YouTubeImportDiRegistrationTests.cs` with two tests:
1. `InterfaceRegistration_ResolvesYouTubeImportService` — confirms the interface resolves.
2. `ConcreteType_IsNotDirectlyResolvable_WhenOnlyInterfaceRegistered` — confirms requesting the concrete type returns null, catching any future reversion to concrete injection.

## Verification

- UI project builds with 0 errors.
- Full test suite: 1001/1001 passing.
- WebApp build blocked by pre-existing NuGet source mapping issue (`Microsoft.AspNetCore.App.Internal.Assets` not in mapped sources) — unrelated to this change.

**STATUS: APPROVED** (Zoe full diff review).

---

### 2026-08-06T13-25-26: Fix NuGet restore: clear disabledPackageSources in repo config to prevent user-level nuget.org disable from breaking PSM resolution

**By:** Kaylee (APPROVED by Zoe)
**What:** Fix NuGet restore: clear disabledPackageSources in repo config to prevent user-level nuget.org disable from breaking PSM resolution
**References:** NuGet/Home#14530, Jayne E2E evidence: clean config proves WebApp builds, src/NuGet.config
**Why:** ## Problem

`dotnet restore` for `SentenceStudio.WebApp` failed with:
```
error NU1101: Unable to find package Microsoft.AspNetCore.App.Internal.Assets.
No packages exist with this id in source(s): coresync-local, localnugets.
PackageSourceMapping is enabled, the following source(s) were not considered: …nuget.org…
```

## Root cause

The user-level `~/.nuget/NuGet/NuGet.Config` contains:
```xml
<disabledPackageSources><add key="nuget.org" value="true"/></disabledPackageSources>
```

The repo's `src/NuGet.config` uses `<packageSources><clear />` to reset sources, but NuGet merges `disabledPackageSources` **independently** of `packageSources`. Per NuGet/Home#14530, when Package Source Mapping is enabled, disabled sources are excluded from resolution — even if they have a `*` pattern mapping. Since `nuget.org` has the `*` wildcard mapping but is disabled, packages like `Microsoft.AspNetCore.App.Internal.Assets` that only exist on nuget.org cannot resolve.

## Fix

Added `<disabledPackageSources><clear /></disabledPackageSources>` to `src/NuGet.config` immediately after the `</packageSources>` block. This resets any inherited disabled-source entries, ensuring all repo-declared sources are active regardless of user-level config.

## Safety check

- The repo config already uses `<packageSources><clear />` so only explicitly-listed sources are active.
- `<disabledPackageSources><clear />` does not ADD any source — it only prevents inherited disable entries from suppressing the repo's explicit sources.
- PSM still constrains which packages resolve from which source (no change to `<packageSourceMapping>`).
- No new sources are enabled beyond: coresync-local, dotnet10, nuget.org, localnugets, dotnet9 feed.

**STATUS: APPROVED** (Zoe full diff review, WebApp build now passes).


---

### 2026-09-26T17-10-27: Use GPT models exclusively at maximum quality

**By:** Captain (David Ortinau)
**What:** All Squad coordinator and agent dispatches use GPT models exclusively. Default to `gpt-6-sol` with `xhigh` reasoning effort and `long_context`. If unavailable, select only the strongest available GPT model with the highest supported reasoning and context tier; never select or fall back to Anthropic/Claude, and never omit explicit model selection if that could permit a non-GPT default. If no GPT model is available, stop and report the failure rather than dispatching to another provider.
**References:** .squad/config.json, .github/copilot-instructions.md
**Why:** Captain's 2026-09-26T12:10:16-05:00 directive requires maximum-quality, GPT-only Squad execution, regardless of cost.

---

### 2026-09-26: Correct GPT-only reasoning tier and block unsafe upgrades

**By:** Scribe (correction to the 2026-09-26T17-10-27 entry)
**What:** The prior entry's `xhigh` is incorrect: every Squad spawn, including Scribe, uses explicit `gpt-6-sol` / `max` reasoning effort / `long_context`, with no model-free or non-GPT fallback. Published `squad upgrade` is blocked in this repository until its source templates are audited, active/template GPT policy is reconciled, and the repo-local `node .github/skills/squad/scripts/check-policy.js` passes after upgrade/regeneration and before Squad dispatch. GitHub issue assignment to @copilot is blocked: neither triage nor issue-assign may label, dispatch, or assign @copilot while the interface cannot enforce and verify all three model settings. Pre-Ship remains a once-per-artifact-revision Fact Checker + Rai gate before user-facing finalization, not for Scribe bookkeeping.
**References:** `.squad/config.json`; `.github/skills/squad/SKILL.md`; `.squad/templates/session-init-reference.md`; `.squad/ceremonies.md`; `.github/workflows/squad-triage.yml`; `.github/workflows/squad-issue-assign.yml`.

---

### 2026-09-26: Block GitHub issue assignment to @copilot until all model settings are enforceable
**By:** Kaylee
**What:** Keep `copilot-auto-assign: false` and fail closed on manual labels, workflow dispatch, triage, and Ralph handoffs to @copilot. Preserve human/Squad issue routing. Keep the model-only API request disabled and test its actual payload shape in CI. Require GPT model, `max` reasoning effort, and `long_context` for every Squad dispatch without a lower-tier fallback.
**Why:** GitHub issue assignment can select a model but cannot set and verify the other two required settings. A model-only assignment would violate Captain's no-exceptions dispatch policy.


---

### 2026-08-06T15-24-40: Azure Production Deploy — Succeeded with Known Import Limitation
**By:** Scribe
**What:** 2026-08-06: Azure Production Deploy — Succeeded with Known Import Limitation
**References:** Captain (authorization), Simon (deploy path), Jayne (E2E validation), docs/deploy-runbook.md
**Why:** **Date:** 2026-08-06T10:24:13Z (deployment completed ~2026-08-06T09:36Z)

**Status:** ✅ Deployment succeeded. Core functionality verified. Known blocker remains: YouTube caption retrieval from Azure egress fails (upstream datacenter IP blocking).

**Outcome Summary:**
- Azure deploy: `IncludeMobileTargets=false azd deploy --no-prompt -e sstudio-prod-biz` completed in 1m31s
- All services deployed: api--0000022, webapp--0000022, workers--0000022, marketing--0000025
- Traffic: 100% on healthy revisions, no infrastructure/database recreation
- Post-deploy validation: 17 PASS, 0 FAIL, 2 SKIP (auth creds), 2 WARN (workers scale-to-zero, aged migration logs)
- Sticky sessions re-applied post-deploy
- Production Jayne E2E (native Python Playwright): auth PASS, Media Import page/tabs PASS, DI/startup PASS, App Insights traces/requests PASS, Workers traces PASS, safe invalid-video classification PASS
- Telemetry: Azure Container Apps logs and App Insights traces live and queryable
- Known-good Korean-caption test video: still returns VideoUnavailable from Azure (YouTube cloud/datacenter IP blocking confirmed via upstream issue research)

**The Blocker:**
Media Import's core success case (importing owned YouTube videos with captions) remains unavailable from Azure. Root cause: YouTube's cloud egress blocks datacenter IPs. Local Aspire/webapp work because localhost requests see residential/ISP egress. Azure container apps use shared datacenter egress, which YouTube rejects.

**Recovery Options (Next Action Requires Captain Vendor/Budget Decision):**
1. **Residential proxy** — full captions/audio/channel monitoring, but vendor TOS/privacy/cost decision needed
2. **Managed transcript API** — captions only, lighter vendor dependency, lower cost
3. **Manual transcript upload** — no automation, high friction

**No Code Changes Required:** The deployed revision is healthy. Classification of failures (invalid video, unsupported codec, unavailable captions) is working correctly. Telemetry is reporting failures cleanly.

**Next Steps:**
- Captain decides on vendor/proxy/API path
- Simon/Zoe hold for Captain decision (no further deploy/code until vendor chosen)

**References:**
- Deployment initiated: Captain authorization
- Workaround discovered: Simon (web-only path, no workload install)
- E2E validation: Jayne (production Jayne account, Playwright native)
- Upstream research: Simon/Zoe (YouTube IP blocking confirmed)

---

### 2026-08-06T18-01-26: Corporate VPN blocks Azure ACA data plane access; WebApp unavailable behind VPN
**By:** Scribe
**What:** Corporate VPN blocks Azure ACA data plane access; WebApp unavailable behind VPN
**References:** prod-incident, webapp-load-failure, vpn-networking, deployment-runbook
**Why:** **Incident:** WebApp would not load from Captain's Mac at sentencestudio.com/account.

**Investigation (Jayne + Wash):**
- WebApp/API/Marketing Azure Container App FQDNs all resolve to 134.33.19.82
- TCP 443 timed out from Captain's Mac
- sentencestudio.com IIS host (separate) worked normally
- Entra identity provider login succeeded
- management.azure.com also timed out
- Captain's Mac has 5 active utun interfaces (corporate VPN active)

**Validation:**
- External public probe returned HTTP 200 for ACA (environment healthy, no redeploy needed)
- Deployment runbook explicitly requires: "VPN must be off"

**Root Cause:**
Corporate VPN firewall policy blocks Azure Container Apps data plane and Azure management plane traffic from Captain's Mac.

**Resolution:**
1. Disconnect corporate VPN, OR
2. Split-tunnel Azure address ranges, then
3. Reload WebApp and re-run post-deploy diagnostics

**Impact:**
- No server/resource/code/data changes required
- Environment is healthy; access issue is client-side VPN policy
- Applies to any Captain access from behind corporate VPN (not specific to this incident)

**Status:** Diagnosed and mitigated. Safe to proceed post-VPN disconnect.
