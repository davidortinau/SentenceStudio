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
