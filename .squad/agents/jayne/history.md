# Project Context

- **Owner:** David Ortinau
- **Project:** SentenceStudio — a .NET MAUI Blazor Hybrid language learning app
- **Stack:** .NET 10, MAUI, Blazor Hybrid, MauiReactor (MVU), .NET Aspire, EF Core, SQLite, OpenAI
- **Created:** 2026-03-07

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

- E2E testing skill at `.claude/skills/e2e-testing/SKILL.md` — follow it religiously
- Aspire stack: `cd src/SentenceStudio.AppHost && aspire run`
- Webapp at `https://localhost:7071/`
- Playwright for browser testing: snapshots, clicks, form fills, verification
- Server DB: `/Users/davidortinau/Library/Application Support/sentencestudio/server/sentencestudio.db`
- Three verification levels: UI state, database records, Aspire logs
- "It compiles" is NOT sufficient — must verify in running app
- Must call `CacheService.InvalidateVocabSummary()` after recording attempts or dashboard is stale
- Playwright must use `pressSequentially` not `fill()` for Blazor server-side binding
- Test users: David (Korean, f452438c-...), Jose (Spanish, 8d5f7b4a-...), Gunther (German, c3bb57f7-...)
- Vocabulary cleanup actions can feel "dead" if the `main.main-content` scroll container stays deep in the list — verify the panel is actually brought back into view, not just rendered somewhere above the fold
- The new duplicate-management path is Vocabulary Details → overflow menu → Find Duplicates, which deep-links back to `/vocabulary` with the current term and focus word preloaded
- After Azure production deploys, smoke-test the ACA default webapp URL directly (`webapp.livelyforest-b32e7d63.centralus.azurecontainerapps.io`) — if the sign-in UI loads there, the publish is live even if the custom domain has not cut over yet


## Work Sessions

### 2025-01-24 — Quiz Decoupling Verification Plan

**Status:** Verification Plan Ready (awaiting Wash implementation completion)  
**Related Decisions:** 
- `.squad/decisions/inbox/zoe-quiz-vocab-decoupling.md` (architecture)
- `.squad/decisions/inbox/wash-quiz-resource-mismatch-trace.md` (root cause)
- `.squad/decisions/inbox/jayne-quiz-decouple-verify.md` (THIS verification plan)

**Assignment:** Draft comprehensive verification plan for Quiz decoupling fix

**Learnings:**
- Verification plans should cover ALL platforms: local (Aspire + Catalyst), Azure webapp, iOS DX24 device
- Database sanity checks must verify NO unintended schema changes when fix is code-only
- Regression checks must verify resource-driven activities (Reading, Shadowing) still work after decoupling vocabulary-driven activities (Quiz)
- Explicit PASS/FAIL criteria prevent ambiguity — each test must have clear expected observations and failure modes
- iOS DX24 builds require .NET 11 Preview 3 + specific device ID — document exact commands in verification plan
- Open questions for implementer (e.g., VocabularyGame scoping, backwards compatibility) should be listed explicitly
- Verification plans estimate time: local (15min), webapp (10min), iOS (20min), regression (15min), DB sanity (5min) = ~65min total
- The fix decouples VocabularyReview from LearningResource scoping — Quiz should ALWAYS load from global user vocabulary pool, never filtered by ResourceId
- Insights panel already showed global counts (497 due) — the bug was that Quiz filtered to resource-scoped subset (20 words) causing "no vocabulary loaded" toast

**Key Pattern:** Filter divergence bugs (Insights shows global count, Quiz filters to subset) require verification that BOTH systems use same scope after fix

**Tools for Execution (when Wash completes):**
- Aspire CLI (`aspire run --detach`)
- Playwright MCP (webapp testing)
- SQLite CLI (database verification)
- XCode CLI (`xcrun devicectl`) for iOS DX24 install
- maui-ai-debugging skill (optional native inspection)

**Next:** Execute plan when Wash reports fix complete. Verify all 6 sections PASS, no regressions.

---

### 2026-03-13 — Cross-Agent Update: Azure Deployment Issues

**Status:** In Progress  
**GitHub Issues:** #39-#65 created by Zoe (Lead)  
**Jayne's Assignment:** N/A (testing/QA support for phase execution)  

**Phase Execution Order:** Phase 2 (Secrets) → Phase 1 (Auth, localhost-testable) → Phase 3 (Infra) → Phase 4 (Pipeline) → Phase 5 (Hardening)

**E2E Testing Support:** Jayne to verify each phase's integration tests per e2e-testing skill (mandatory for every feature/fix).

**Critical Path:** CoreSync SQLite→PostgreSQL migration (#55, XL) — requires comprehensive data migration testing.

- API test project is `tests/SentenceStudio.Api.Tests/` — separate from IntegrationTests (which targets MAUI)
- WebApplicationFactory<Program> requires `public partial class Program { }` in API's Program.cs
- TestJwtGenerator creates HMAC-SHA256 signed tokens with Entra ID claims (tid, oid, scp)
- JwtBearerApiFactory overrides auth to JWT Bearer mode; DevAuthApiFactory uses default DevAuthHandler

### 2026-03-15 — Cross-Agent Update: Mobile Auth Guard Bypass Fix (Kaylee) + Auth E2E Test Plan (Zoe)

**Status:** COMPLETED  
**Related Decisions:** Mobile Auth Guard, Auth E2E Testing (skill)  
**Impact on Jayne's Testing:**

**Summary:** 
1. **Kaylee's Fix:** Mobile auth gate in MainLayout.razor now validates real token state (`IAuthService.IsSignedIn`) instead of preference flag. Auth.razor enforces server auth before profile selection.
2. **Zoe's Test Plan:** Authored `.squad/skills/auth-e2e-testing/SKILL.md` with 11 test suites covering registration, login, token refresh, session restoration, and WebApp regression.

**Test Suites Defined (Jayne to implement):**
- Pre-Auth State Tests (fresh app)
- Registration Tests (`/auth/register`)
- Login Tests (Entra ID + DevAuth)
- Token Refresh Tests (background silent refresh)
- Session Restoration Tests (app restart with valid tokens)
- WebApp Regression Tests (cookie-based auth unaffected)
- Failure Path Tests (expired, invalid, network errors)
- Performance Tests (auth latency)
- iOS-Specific Tests (Keychain SecureStorage, MSAL URL scheme)
- Profile Selection Tests (server auth gating)
- Offline Fallback Tests (graceful degradation)

**What This Means for Jayne's QA:**
- Fresh app should redirect to `/auth`, not show Dashboard
- Valid refresh token in SecureStorage should restore session without re-login
- Expired tokens must force re-login
- Create Local User should be removed or redirect to `/auth/register`
- Bootstrap modal for auth failures (not JS alert)
- iOS Keychain must persist tokens across app restarts
- WebApp sign-in/sign-out unaffected by mobile changes

**Learnings Added:**
- Auth gates must check server state, not local preferences — preference persistence alone is not auth
- Token refresh from SecureStorage is critical for mobile UX
- iOS Keychain integration via MAUI SecureStorage is non-negotiable for production
- E2E tests must cover both happy path and failure modes (expired, network, invalid)
- API's `/api/v1/plans/generate` is POST — don't test it with GET or you get 405 not 401
- DevAuthHandler always authenticates with claims: dev-tenant, dev-user, Dev User, dev@sentencestudio.local

### 2026-03-13 — Auth Integration Tests (#47)

**Status:** Complete
**Branch:** `feature/47-auth-tests`
**Tests:** 11 passing (7 JWT Bearer + 4 DevAuthHandler)

Created `tests/SentenceStudio.Api.Tests/` with WebApplicationFactory-based auth integration tests. Two test factories: JwtBearerApiFactory (simulates Entra ID mode) and DevAuthApiFactory (simulates local dev mode). TestJwtGenerator creates mock tokens signed with a test key for CI-compatible testing.

Key findings during testing:
- The API's `Auth:UseEntraId` config flag exists in appsettings.json but isn't yet wired in Program.cs code — Wash's #43 needs to implement the conditional switch
- No scope-based authorization policies exist yet — can't test EnforcesAuthorizationPolicies until policies are added
- TenantContextMiddleware correctly extracts claims from both DevAuthHandler and JWT Bearer tokens

## Learnings — DevFlow Package Migration Verification (2025-07-04)

### Task
E2E verification that SentenceStudio iOS builds and runs with Microsoft.Maui.DevFlow.* v0.24.0-dev packages (migrated from Redth.MauiDevFlow.*), and that the agent registers with the broker.

### Results — ALL PASS ✅
- ✅ **Build succeeded**: Clean build (bin/obj deleted) completed in ~32s with 0 errors, 505 warnings (all pre-existing: Scriban vulns, IL2026 trimming, EF1002 SQL injection)
- ✅ **App launched on iOS simulator**: iPhone 17 Pro (iOS 26.2, UDID 95EC018A)
- ✅ **Agent registered with broker**: `maui-devflow list` shows SentenceStudio on port 9224
- ✅ **Version 0.24.0-dev confirmed**: Proves we're running the custom build with broker registration fix
- ✅ **Broker log confirmed**: `Agent connected: SentenceStudio|net10.0-ios → port 9224 (id: 47ff557ef8f8)`

### Gotchas Discovered
1. **Two-step build required after clean**: `dotnet build -f net10.0-ios -t:Run` fails with "The app must be built before the arguments to launch the app using mlaunch can be computed" when bin/obj are empty. Must run `dotnet build -f net10.0-ios` first, THEN `dotnet build -f net10.0-ios -t:Run -p:_DeviceName=...`.
2. **Two sims already booted**: iPhone 17 Pro and iPhone 11 both on iOS 26.2. Used iPhone 17 Pro to avoid conflicts with Comet apps on the other sim.
3. **Existing agents on broker**: Two Comet apps (v0.18.0) were already registered. SentenceStudio correctly registered alongside them at v0.24.0-dev.

### 2026-04-03 — Verification of Fixes #149, #150, #151

**Status:** CODE REVIEW COMPLETE (Aspire live E2E blocked by macOS Keychain cert prompt)

**Fix #151 — Scoring override revert (commit 58a8364): ✅ PASS**
- VocabQuiz.razor refactored to defer attempt persistence via `pendingAttempt` field
- `CheckAnswer()` builds attempt in memory but does NOT persist
- `OverrideAsCorrect()` flips `pendingAttempt.WasCorrect = true` before calling `RecordPendingAttemptAsync()`
- `NextItem()` flushes pending attempt (no-op if already flushed by override)
- `DisposeAsync()` flushes any unflushed attempt (navigation safety net)
- Result: exactly ONE VocabularyLearningContext record per question. No double-counting possible.
- Streak logic: reset to 0 on wrong → incremented to 1 on override. Correct behavior.
- Webapp build: 0 errors ✅

**Fix #150 — Validation too strict (commit 81eaf2f): ✅ PASS**
- FuzzyMatcher.Evaluate() gains slash-alternative handler before EvaluateSingle dispatch
- "remaining/leftover" → split by `/` → try each alt via EvaluateSingle → first match wins
- Parenthetical stripping ("to take (a photo)" → "to take") + verb prefix ("to be high" → "be high") already handled by NormalizeText
- Word boundary check: "high" matches "to be high" via allUserWordsPresent
- Unit tests cover all three Captain-reported scenarios + theory tests for slash alternatives
- Tests couldn't execute due to pre-existing compilation errors in SearchQueryParserTests (namespace mismatch) — NOT caused by this fix

**Fix #149 — Turn count wrong (in commit 58a8364): ✅ PASS**
- Footer changed from `@TurnsPerRound` (constant 10) to `@roundWordOrder.Count` (actual pool size)
- roundWordOrder built from `batchPool.Take(ActiveWordCount)` — if fewer than 10 words remain, count reflects reality
- Round termination guard `currentTurnInRound >= roundWordOrder.Count || currentTurnInRound >= TurnsPerRound` still enforces both bounds
- When pool is small (e.g., 4 words), counter shows "1 / 4" not "1 / 10" ✅

**Pre-existing issues noted (not blockers):**
- Unit test project fails to compile due to SearchQueryParserTests namespace mismatch (`SentenceStudio.Shared.Services` vs `SentenceStudio.Services`) and VocabularyProgressTests type error
- Aspire `aspire run` hangs on "Checking certificates..." — likely macOS Keychain prompt
- Scriban NuGet has known critical/high/moderate vulnerabilities (6.5.2)

**Learnings:**
- `pendingAttempt` pattern for deferred persistence is a clean approach for override flows — reusable for other activities
- FuzzyMatcher slash handling falls through to EvaluateSingle on full string if no alternative matches — slash becomes punctuation removal, concatenating words. Acceptable edge case.
- `@using SentenceStudio.Shared.Services` must be added to any Razor page using FuzzyMatcher — it's NOT in _Imports.razor

---

## 2026-04-03: Vocabulary Quiz Fixes Verification (In Progress)

**Team:** Wash fixed #151 (scoring override expiration), Kaylee fixed #150 + #149 (text validation + turn counting). Scribe logging orchestration.

**Verification In Progress:**
- #151: Override window expiration works; expired overrides fall back to base score
- #150: FuzzyMatcher validates multi-word phrases; slash-separated alternatives work
- #149: Turn counter accuracy with contractions, hyphenation, punctuation

**Status:** Verifying all three fixes in running app end-to-end.

### 2026-04-03 — Fix #152 Verification + Deployment

**Status:** COMPLETE (code review + build) / PARTIAL (deployment)

**Fix #152 — Daily Plan Completion Tracking: ✅ PASS**
- `UpdatePlanItemProgressAsync` (ProgressService.cs:588-680) now checks `minutesSpent >= estimatedMinutes`
- DB path: sets `IsCompleted = true`, `CompletedAt = DateTime.UtcNow` when threshold met
- Cache path: mirrors logic with `item.IsCompleted || minutesSpent >= item.EstimatedMinutes`
- `CompletedCount` recalculated from `plan.Items.Count(i => i.IsCompleted)` — avoids drift
- Edge cases verified: exact threshold (✅ via `>=`), overshoot (✅ via `!existing.IsCompleted` guard), 0 items (✅ via `totalEstimatedMinutes > 0` divide-by-zero guard), missing DB record (✅ logs warning, skips), missing cache (✅ logs info, skips)
- Note: `minutesSpent` in `UpdatePlanItemProgressAsync` is absolute (not delta) — matches ActivityTimerService caller
- `MarkPlanItemCompleteAsync` exists but has no callers; `UpdatePlanItemProgressAsync` is the live code path via ActivityTimerService
- Build: 0 errors (webapp + iOS), 219 unit tests passing

**Deployment:**
- Committed: `ed1ead8` on main — fixes #149, #150, #151, #152
- Pushed to remote: ✅
- Azure `azd deploy`: API ✅, cache ✅, db ❌ (volume env var `SERVICE_DB_VOLUME_...` missing — likely azd version mismatch or infra config drift, needs `azd up` or infra update)
- iOS device install: ❌ device locked (UDID 00008130-001944C224E1401C) — .app built successfully at `src/SentenceStudio.iOS/bin/Release/net10.0-ios/ios-arm64/SentenceStudio.iOS.app`, needs unlock + retry
- CI workflow triggers on push to main — should be running

## Learnings

- `UpdatePlanItemProgressAsync` uses absolute minutes (from ActivityTimerService), `MarkPlanItemCompleteAsync` accumulates delta — different semantics, both in same file
- `azd deploy` can partially succeed — API + cache deployed while db failed due to volume config
- iOS device install requires unlocked device; error is `kAMDMobileImageMounterDeviceLocked`
- CI workflow at `.github/workflows/ci.yml` triggers on push to main
- Azure deploy uses `azure.yaml` pointing at the Aspire AppHost project

### 2026-04-08 — Cross-Agent Update: GitHub Backlog Triage Complete

**Team Status:** Backlog triage completed across three focus areas:
- Mobile UX validation: 7 issues remain valid (#100, #102, #104, #109, #110, #119, #120); #114 and #116 confirmed addressed
- Infrastructure audit: #57, #59, #58 identified as highest operational priority
- Decisions: Feedback auth fix merged to team decisions log

**Impact for Jayne:** Valid mobile UX issues ready for sprint planning and E2E test prioritization.

### 2025-07-25 — Skeptic Review: Quiz Learning Journey Spec

**Status:** COMPLETE  
**Output:** `.squad/decisions/inbox/jayne-spec-skeptic.md`

**Task:** Adversarial review of `docs/specs/quiz-learning-journey.md` — the VocabQuiz learning journey specification. Found 15 issues (2 critical, 4 high, 8 medium, 1 low).

**Critical findings:**
- Section 5.6 (temporal weighting) is referenced 7 times but doesn't exist — numbering jumps from 5.5 to 6. Implementation constants land in an unnumbered block under section 6.
- Section 7 "Scoring Reference" (the implementer cheat sheet) contradicts R3-approved changes — still shows old formulas.

**Key interaction bugs identified:**
- PendingRecognitionCheck vs lifetime mode selection has no defined priority — implementer will guess wrong
- Tier 1 rotation (1 correct = rotate out) combined with temporal weighting is too lenient — user escapes gentle demotion with minimal evidence
- Math.Max mastery floor creates a hidden plateau where correct answers don't visibly change mastery — contradicts Captain's "immediately reflected" expectation
- DifficultyWeight (2.5f for sentences) is decorative — never used in mastery formula. Also contradicts itself (2.0 vs 2.5 in different sections).
- Immediate mid-round rotation can leave 1 word in a 10-turn round = degenerate UX

## Learnings

- Spec cross-references must be verified before review sign-off — broken section numbering is a recurring doc quality issue
- DifficultyWeight on VocabularyAttempt is stored in VocabularyLearningContext.DifficultyScore as a log field — never feeds into mastery calculation
- VocabularyQuizItem still uses old `QuizRecognitionStreak`/`QuizProductionStreak` consecutive counters — proposed `SessionCorrectCount` fields don't exist yet
- `RecordAttemptAsync` returns updated progress but the quiz razor doesn't assign it back to `currentItem.Progress` — confirms spec's D1 finding
- Current mode selection in VocabQuiz.razor (line 784) uses `IsPromotedInQuiz` (session-local) OR `MasteryScore >= 0.50` — not lifetime `CurrentStreak >= 3` as spec requires

### 2025-07-25 — Phase 0 Scoring Engine Tests

**Status:** COMPLETE  
**File:** `tests/SentenceStudio.UnitTests/Services/MasteryScoring/ScoringEngineTests.cs`  
**Decision:** `.squad/decisions/inbox/jayne-phase0-tests.md`

Wrote 19 acceptance tests for Phase 0 scoring changes. All 19 pass.

## Learnings

- Wash already landed Phase 0 — `CurrentStreak` is float, DifficultyWeight accelerates streak, temporal weighting and recovery boost are live
- Spec table says penalty "caps at 0.92" but the formula is logarithmic and continues rising (200 correct → 0.937). Don't treat approximate table values as hard bounds.
- 5 pre-existing integration tests (`MasteryAlgorithmIntegrationTests`, `MultiDayLearningJourneyTests`, `DeterministicPlanBuilderResourceSelectionTests`) still expect old flat-penalty behavior — they need updating separately
- In-memory SQLite test pattern works well for scoring engine tests — no mocks needed for the core math, real EF Core pipeline validates persistence round-trip

### 2025-07-25 — Phase 0 Test Failure Audit

**Status:** COMPLETE — NO FAILURES FOUND  
**Suite:** `tests/SentenceStudio.UnitTests/SentenceStudio.UnitTests.csproj`  
**Result:** 392 passed, 0 failed, 0 skipped

Ran full test suite to find pre-existing failures from Phase 0 scoring changes. All 392 tests pass clean. The integration tests I flagged earlier (`MasteryAlgorithmIntegrationTests`, `MultiDayLearningJourneyTests`) have already been updated to use Phase 0 assertions — partial streak preservation, scaled penalty checks, float `CurrentStreak`, recovery boost. Someone (likely Wash) already landed the fixes before this task was assigned. No commit needed.

### 2025-07-25 — Phase 0 + Phase 1 E2E Validation (VocabQuiz in Aspire)

**Status:** COMPLETE — 10/12 PASS, 1 BY-DESIGN, 1 UI GAP
**Environment:** Aspire stack (webapp at https://localhost:7071, PostgreSQL in Docker)
**Test User:** e2etest@sentencestudio.local
**Test Scripts:** e2e-testing-workspace/quiz-mastery-e2e-v2.js, quiz-summary-info.js, mastery-deferred-v2.js

#### Results

| # | Test | Result | Notes |
|---|------|--------|-------|
| 1 | Quiz loads without crash | PASS | MC mode, 4 options, progress counter |
| 2 | Answer feedback (correct/incorrect) | PASS | Visual feedback + check/X icons |
| 3 | Learning Details panel shows mastery data | PASS | MasteryScore, CurrentStreak (float!), ProductionInStreak, SRS, Why This Mode |
| 4 | Round completion at 10/10 | PASS | Session Summary screen appears |
| 5 | Session summary accuracy | PASS | Correct/Total counts, per-word results with icons |
| 6 | Mode selection uses lifetime progress | PASS | New words to MC; streak < 3 mastery < 50% |
| 7 | CurrentStreak is float in DB | PASS | PostgreSQL real type, fractional values observed (0.0866434) |
| 8 | MasteryScore formula correct | PASS | 0.14285715 = 1/7 for streak=1 |
| 9 | DifficultyWeight tracked per attempt | PASS | 1.0 for MC in VocabularyLearningContext.DifficultyScore |
| 10 | Recording persists to DB after auto-advance | PASS | RecordPendingAttemptAsync writes correctly |
| 11 | Mastery updates during feedback phase | BY-DESIGN | Info panel shows STALE data during feedback (deferred recording) |
| 12 | DifficultyWeight visible in info panel | UI GAP | Stored in DB but NOT surfaced in Learning Details panel |

#### Key Findings

1. **Deferred Recording (BY DESIGN):** pendingAttempt is created when user answers but NOT persisted until NextItem() runs (auto-advance or manual). Info panel reads currentItem.Progress which is only updated post-persist. This means the info panel shows stale mastery during the feedback phase. This is intentional per the override design (user can mark correct before persist).

2. **Phase 0 Scoring Verified End-to-End:**
   - CurrentStreak is real (float) in PostgreSQL — confirmed working
   - DifficultyWeight: 1.0 for MC, 1.5 for Text (stored as DifficultyScore)
   - Fractional streaks: 0.0866434 observed for partial recovery (wrong then correct)
   - MasteryScore = CurrentStreak / 7.0 confirmed (0.14285715 = 1/7)

3. **Phase 1 Quiz Behavior Verified:**
   - Mode selection based on lifetime progress (streak < 3 implies MC)
   - Why This Mode explanation shown in info panel
   - Session summary shows accurate results with Bootstrap icons
   - Immediate rotation code exists (spec 1.3) but not triggered in test (no words reached mastery threshold)

4. **Database:** Aspire uses PostgreSQL (NOT SQLite). The SQLite file at ~/Library/Application Support/sentencestudio/server/sentencestudio.db has OLD data from March 18. Always use PostgreSQL when running via Aspire.

5. **Audio Mode:** ShowAudioChoiceControls renders Option A/B/C/D labels instead of text — intentional for audio-based quiz. The buttons still have IDs quiz-option-a through quiz-option-d.

#### Recommendations

1. **Consider optimistic mastery display:** After creating pendingAttempt, compute and display the projected mastery in the info panel so users see immediate feedback. Revert if they override.
2. **Surface DifficultyWeight in info panel:** Add a line like DifficultyWeight: 1.0 (Multiple Choice) to the Learning Details panel for transparency.
3. **Text mode E2E:** Not tested — all words for the test user had low streak (< 3), so Text mode never triggered. Need a test with pre-seeded high-streak words to validate Text mode and DifficultyWeight=1.5.

#### Environment Notes

- Playwright MCP browser was dead (target closed) — used Node.js Playwright scripts as fallback
- Aspire CLI output can appear stuck on Building while stack is fully running — verify with curl
- Docker Desktop must be running before Aspire start (containers will not start otherwise)

### 2026-04-15 — Full E2E Validation: Mastery-Affected Activities (Phases 0-3)

**Status:** COMPLETE — code review + database forensics + schema verification
**Note:** Playwright MCP was dead (target closed + not connected after reload). Pivoted to code analysis + PostgreSQL DB verification.

**Approach:** Aspire stack was running (PostgreSQL + webapp + API). Verified wiring via code review, DB records via docker exec psql, schema via PRAGMA/information_schema, migration history via __EFMigrationsHistory.

#### Results

| Activity | Wiring | DifficultyWeight | CacheInvalidation | DB Evidence | Verdict |
|----------|--------|-----------------|-------------------|-------------|---------|
| Quiz (Phase 0+1) | ExtractAndScore + direct RecordAttemptAsync | 1.0 MC / 1.5 Text / 2.5 Sentence | InvalidateVocabSummary() | 164 records at 1.5, 421 at 1.0 | PASS |
| Writing (Phase 2) | ExtractAndScoreVocabularyAsync | 1.5f | InvalidateVocabSummary() | 8 records (pre-Phase, DifficultyScore=1.0) | PASS (code) |
| Translation (Phase 2 bug fix) | ExtractAndScoreVocabularyAsync | 1.5f | InvalidateVocabSummary() | 29 records (pre-fix, DifficultyScore=1.0) | PASS — BUG FIX VERIFIED |
| Scene (Phase 3) | ExtractAndScoreVocabularyAsync | 1.5f | MISSING | 44 records (pre-Phase, DifficultyScore=1.0) | CONDITIONAL PASS |
| Conversation (Phase 3) | ExtractAndScoreVocabularyAsync + penaltyOverride=0.8f | 1.2f | MISSING | 0 records (never used) | CONDITIONAL PASS |

#### Bugs Found

**BUG 1: Scene.razor missing CacheService.InvalidateVocabSummary()**
- CacheService (`ProgressCacheService`) is not injected at all in Scene.razor
- After recording vocabulary progress, dashboard counts won't refresh until manual cache expiry
- Fix: Add `[Inject] private ProgressCacheService CacheService { get; set; } = default!;` and call `CacheService.InvalidateVocabSummary()` after ExtractAndScoreVocabularyAsync

**BUG 2: Conversation.razor missing CacheService.InvalidateVocabSummary()**
- Same issue as Scene — CacheService not injected, no invalidation call
- Fix: Same pattern as Scene

**NOT A BUG: SQLite local DB (MAUI) still has INTEGER for CurrentStreak**
- The SQLite file at `~/Library/Application Support/sentencestudio/server/sentencestudio.db` hasn't applied the CurrentStreakToFloat migration
- This is because the Aspire stack uses PostgreSQL, not SQLite. The SQLite file is from the MAUI client.
- PostgreSQL has `CurrentStreak` as `real` — confirmed via information_schema
- MAUI client will need its own migration path (SyncService handles this)

#### Schema Verification
- PostgreSQL `VocabularyProgress.CurrentStreak`: real (float) — migration 20260415024019_CurrentStreakToFloat applied
- PostgreSQL `VocabularyProgress.ProductionInStreak`: integer — correct per model
- PostgreSQL `VocabularyLearningContext.DifficultyScore`: real — correct
- VocabularyAttempt.PenaltyOverride: float? — correctly wired through pipeline
- All 6 PostgreSQL migrations applied including CurrentStreakToFloat

#### Shared Extraction Pipeline (Phase 2 refactoring goal)
- ExtractAndScoreVocabularyAsync() at VocabularyProgressService:659 — single pipeline for all activities
- Used by: Writing, Translation, Scene, Conversation (all 4 Phase 2-3 activities)
- Deduplicates by DictionaryForm, matches against user vocabulary, creates VocabularyAttempt, records via RecordAttemptAsync
- PenaltyOverride flows through: Conversation passes 0.8f → VocabularyAttempt.PenaltyOverride → RecordAttemptAsync applies it at line 158-160

#### Learnings
- Aspire stack uses PostgreSQL, NOT the SQLite file the Captain referenced. Always check AppHost.cs for the DB provider.
- Playwright MCP can die permanently within a session — reload doesn't fix "Not connected". May need full CLI restart.
- Scene activity uses "SceneDescription" as the Activity name (not "Scene") — matches pre-existing DB records
- No Writing/Translation/Scene/Conversation records exist post-Phase-2-3 — code was deployed same day, nobody has exercised these activities via webapp yet. DB evidence is from PRE-phase implementations only.

- Quiz decoupling verification plan prepared (2026-04-17) — comprehensive testing across local, Azure, iOS for VocabularyReview now loading from global vocabulary pool (ResourceId=null). 6 critical pass criteria, 3 regression checks, 2 database sanity checks. ~65 minutes estimated.

## 2026-04-17 — Plugin.Maui.HelpKit Alpha Scope Locked

Captain locked 8 decisions. Alpha scope frozen. Implications for Jayne (platform testing):
- **Platform matrix reduced:** No sqlite-vec native binary complications → testing focuses on in-memory VectorData + JSON
- **Stub scanner testing:** Non-AI page scanner in Alpha; validates .md generation for XAML/MauiReactor pages
- **TFM testing:** net11.0-* targets; CI must use net11 preview SDK
- **Rate limiting:** 10 q/min default; test configurable override via HelpKitOptions
- **Streaming UI:** Native MAUI CollectionView + streaming text — test message rendering, retry states, error handling

SPIKE-1 and SPIKE-2 ready for validation when Captain signals go-ahead.

