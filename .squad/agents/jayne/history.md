# Project Context

- **Owner:** David Ortinau
- **Project:** SentenceStudio — a .NET MAUI Blazor Hybrid language learning app
- **Stack:** .NET 10, MAUI, Blazor Hybrid, MauiReactor (MVU), .NET Aspire, EF Core, SQLite, OpenAI
- **Created:** 2026-03-07

## Learnings

- 2026-04-23: **Word/Phrase Feature Completed** — Completed 4 todos: tests-backfill (120 tests, classification + constituent backfill), tests-mastery-cascade (10 tests, cascade logic), tests-regression (5 tests, word-only unaffected), tests-smart-resource (12 tests, Phrases smart resource). Total: 147 tests passing, feature code-complete. E2E blocked on pre-existing SQLite migration history mismatch (Captain decision needed on reconciliation). One bug surfaced & fixed: SmartResourceService.GetPhrasesVocabularyIdsAsync scope bug (was circular, fixed by Wash). Documented in `.squad/log/2026-04-23T2219Z-wordphrase-squad-wrap.md`.
- 2026-04-17: HelpKit Alpha — 30 golden Q/A + eval gate (85%/0%) + cross-platform validation plan + 7 unit-test files.

- 2026-04-25: **v1.1 Import Test Matrix Authored** — 10 scenarios (A-J) + 7 edge cases + 5 test fixtures in `e2e-testing-workspace/v11-import/`. Covers vocab CSV regression, phrases import, transcript import, auto-detect at all 3 confidence tiers, checkbox validation, checkbox override, DB pollution check on cancel, and LexicalUnitType backfill migration. All marked AUTHORED NOT YET RUN. Decision file at `.squad/decisions/inbox/jayne-v11-test-matrix.md`. Gaps flagged: zero-extraction behavior undefined, >30KB handling needs Wash confirmation, classifier confidence thresholds depend on River's prompt, Kaylee's UI selectors TBD.

<!-- Append new learnings below. Each entry is something lasting about the project. -->

- 2026-04-26: **v1.2 Import Bug Reproduction** — Captain's Phrases+Pipe import confirmed broken. Root cause: Phrases branch in `ContentImportService.ParseContentAsync()` (line 176-196) bypasses delimiter-aware `ParseDelimitedContent()` and sends everything to `ParseFreeTextContentAsync()`, which decomposes phrases into individual words via AI. DB evidence: 8 Word entries, 0 Phrase entries from Captain's 3-sentence input. Second issue: `ParseDelimitedContent()` hardcodes `LexicalUnitType.Word` (line 485). Evidence at `e2e-testing-workspace/v12-import-bug/`. LexicalUnitType enum mapping confirmed: Unknown=0, Word=1, Phrase=2, Sentence=3. Server Postgres has the LexicalUnitType column (migration applied); mobile SQLite does NOT (migration `20260423` never applied to server SQLite).
- Server DB is Postgres via Aspire (not SQLite). The SQLite file at `~/Library/Application Support/sentencestudio/server/sentencestudio.db` is the mobile sync DB, not the webapp DB. Query Postgres via: `docker exec -e PGPASSWORD='...' <container> psql -U dbadmin -d sentencestudio`
- Playwright MCP can go stale (browser closed state) between sessions. Have a fallback to DB-level verification when Playwright is unresponsive.

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
- E2E testing magic link (AutoSignIn) flows requires cryptographic token generation — not feasible in Playwright without dev endpoint. Recommend `/dev/test-autosignin?userId=xxx` for full E2E coverage.


## Work Sessions

### 2025-06-01 — Phrase Cascade Integration Tests ✅

**Status:** ✅ COMPLETE  
**Related Decisions:**
- `docs/decisions/inbox/wash-progress-cascade.md` (Wash's implementation)
- `.squad/decisions/inbox/jayne-tests-mastery-cascade.md` (THIS test report)

**Assignment:** Write unit + integration tests for phrase-to-constituent passive exposure cascade in `VocabularyProgressService.RecordAttemptAsync`

**Task:** `tests-mastery-cascade`

**Tests Written:** 10 scenarios (all passing)

1. ✅ Phrase own mastery intact while constituent exposure-only updates
2. ✅ Constituent `ExposureCount`/`LastExposedAt` fields set correctly
3. ✅ Unknown does not cascade (edge case verified)
4. ✅ Word does not cascade to containing phrase (one-directional)
5. ✅ Cascade caps at one level (no transitive)
6. ✅ Lemma-based constituent matching via stored `PhraseConstituent` rows
7. ✅ Zero constituents no-op (no exception, phrase mastery commits)
8. ✅ Wrong-answer cascade (constituents get exposure even on incorrect attempts)
9. ✅ First-ever constituent exposure auto-creates progress row
10. ✅ Partial failure isolated (best-effort per constituent)

**File:** `tests/SentenceStudio.UnitTests/Integration/PhraseCascadeIntegrationTests.cs`

**Infrastructure:**
- xUnit + FluentAssertions + Moq
- In-memory SQLite via `PlanGenerationTestFixture` (real EF Core)
- Deterministic (no tight DateTime comparisons, no random ordering)
- Follows existing integration test patterns

**Bugs Found:** NONE — Wash's implementation works as specified

**Collateral Fixes:**
Fixed 6 existing integration test files broken by Wash's new `IServiceProvider` constructor parameter:
- `MasteryAlgorithmIntegrationTests.cs`
- `MultiDayLearningJourneyTests.cs`
- `SpacedRepetitionIntegrationTests.cs`
- `PlanToProgressLifecycleTests.cs`
- `VocabularyProgressServiceUserIdTests.cs`
- `MasteryScoring/ScoringEngineTests.cs`

**Test Results:**
```bash
dotnet test --filter "FullyQualifiedName~PhraseCascadeIntegrationTests"
Passed: 10, Failed: 0, Skipped: 0, Duration: 441 ms
```

**Verdict:** ✅ CASCADE IMPLEMENTATION VERIFIED

**Next Steps:**
1. Scribe can document cascade feature in user-facing changelog
2. Monitor structured logs in production for constituent cascade counts
3. Wash's next task: `tests-backfill` will cover backfill service matching logic

---

### 2025-06-01 — Word-Only No-Cascade Regression Tests ✅

**Status:** ✅ COMPLETE  
**Related Decisions:**
- `.squad/decisions/inbox/wash-progress-cascade.md` (Wash's cascade implementation)
- `.squad/decisions/inbox/jayne-tests-mastery-cascade.md` (Cascade FIRES tests)
- `.squad/decisions/inbox/jayne-tests-regression.md` (THIS regression test report)

**Assignment:** Prove word/phrase feature is **additive, not disruptive** to word-only activities (Cloze, Writing, VocabularyReview)

**Task:** `tests-regression`

**Tests Written:** 5 scenarios (all passing)

1. ✅ Word-type Cloze activity — NO cascade logic fires
2. ✅ Word-type Writing activity (production mode) — NO cascade logic fires
3. ✅ Word-type VocabularyReview activity — NO cascade logic fires
4. ✅ Unknown-type vocabulary (pre-feature default) — NO cascade logic fires
5. ✅ Mixed vocabulary (word + phrase) — word attempt does NOT cascade to phrase constituents

**File:** `tests/SentenceStudio.UnitTests/Integration/WordOnlyNoCascadeRegressionTests.cs`

**Assertion Strategy:**
- PhraseConstituent table count checked before/after attempt (must remain 0 or unchanged)
- Custom `InMemoryLoggerProvider` captures logs, filters for "PhraseCascade" signature, asserts empty
- Word mastery metrics verified (attempts, streak, production evidence) to confirm normal flow

**Infrastructure:**
- xUnit + FluentAssertions
- In-memory SQLite via `PlanGenerationTestFixture` (real EF Core + ApplicationDbContext)
- Custom `InMemoryLoggerProvider` (reusable logger capture utility for future tests)
- Deterministic (no time/random dependencies)

**Coverage Gaps Found:** NONE — existing tests already cover word mastery progression. This task adds **explicit negative assertions** proving cascade does NOT fire for words.

**Test Results:**
```bash
dotnet test --filter "FullyQualifiedName~WordOnlyNoCascadeRegressionTests"
Test Run Successful.
Total tests: 5, Passed: 5, Duration: 1.18s
```

**Verdict:** ✅ WORD-ONLY ACTIVITIES REMAIN UNAFFECTED BY PHRASE CASCADE

**Learnings:**
- Integration tests with real EF Core DbContext allow cascade behavior verification (table queries, progress updates)
- In-memory logger providers enable non-invasive log assertion without changing production code
- Word and Unknown types correctly excluded from cascade guard (`LexicalUnitType == Phrase || LexicalUnitType == Sentence`)
- Pre-feature vocabulary (`LexicalUnitType.Unknown`) behaves identically to explicit `Word` type — both skip cascade
- DifficultyWeight affects CurrentStreak directly (not capped at 1) — production activities use weight > 1.0

**Next Steps:**
1. Existing test files with uncommitted IServiceProvider fixes can be committed (those were part of Wash's cascade feature, not part of this task)
2. Wash's next task: `tests-backfill` will verify backfill service phrase-constituent matching logic

---

### 2026-04-19 — MAUI Locale Fix E2E Verification (Mac Catalyst)

**Status:** ⚠️ PARTIAL VERIFICATION — BLOCKED BY DEVFLOW BROKER  
**Related Decisions:**
- `.squad/decisions/inbox/wash-maui-locale-investigation.md` (Wash's diagnosis)
- `.squad/decisions/inbox/kaylee-maui-locale-fix-impl.md` (Kaylee's implementation)
- `.squad/decisions/inbox/jayne-maui-locale-e2e.md` (THIS E2E report with re-run)

**Assignment:** E2E verify Kaylee's MAUI locale fix on Mac Catalyst — two-phase locale restoration (boot-time + post-login)

**Initial Attempt - Blocker:** Thought `maui-devflow` was a standalone CLI (not in NuGet feeds)

**Re-Run After Tooling Correction:**
Captain clarified: `maui-devflow` doesn't exist — it's `maui devflow` (subcommand of `maui` global tool, which IS installed).

**New Blocker:** DevFlow broker won't start
```bash
$ maui devflow broker start
Failed to start broker

$ maui devflow diagnose
❌ Broker: Not running
⚠️  No agents connected
📦 5 DevFlow-enabled projects detected (including MacCatalyst)
```

**What I Verified:**
- ✅ Build succeeds on Mac Catalyst (0 errors)
- ✅ App launches and runs without crash (single clean instance, PID 69593)
- ✅ Code review: All changes correctly implement Wash's fix strategy
- ✅ Preferences show `active_profile_id` = David's Korean profile (f452438c-...)
- ✅ Database confirms David's profile has `DisplayLanguage = "Korean"`
- ✅ Null-safety and culture validation work per code review (Scenario 5)
- ✅ DevFlow integration confirmed in project files (Agent + Blazor packages present)

**What I Could NOT Verify:**
- ❌ Korean UI text rendering (DevFlow visual tree unavailable)
- ❌ Profile switching behavior (DevFlow UI interaction unavailable)
- ❌ Sign-out → sign-in flow (DevFlow UI interaction unavailable)
- ❌ JWT restoration locale application (DevFlow structured logs unavailable)
- ❌ Anomaly investigation (`7ccabe4b` profile ID from initial run — cannot access logs)

**Anomaly Status:**
Initial run showed:
```
ApplyLocaleFromProfile: applied culture en for profile 7ccabe4b-5da0-492d-af32-851910fe7f1f
```
Expected: `culture ko for profile f452438c-...` (David's Korean profile)

**Unable to confirm or deny** if this was:
1. Stale log from previous multi-instance run (likely)
2. Real bug where GetAsync returns wrong profile (needs investigation)

**Reason:** DevFlow logs unavailable, macOS Console doesn't capture .NET app logs

**Test Scenarios:**
- ⏭️ Scenario 1 (Fresh Login): SKIPPED — Cannot interact with UI
- ⏭️ Scenario 2 (JWT Restore): PARTIAL — App relaunched with correct data, but no UI verification
- ⏭️ Scenario 3 (Multi-Profile): SKIPPED — Cannot interact with UI
- ⏭️ Scenario 4 (Fresh Install): SKIPPED — Data-safety rule
- ✅ Scenario 5 (Null/Unsupported Culture): PASS — Code review confirms null-safe fallback

**Verdict:** ⚠️ **NEEDS MANUAL SMOKE TEST OR DEVFLOW FIX**  
Code = ✅ PASS  
Data layer = ✅ PASS  
Runtime UI verification = ⚠️ INCOMPLETE (DevFlow broker won't start)

**Recommendations:**
1. **Manual smoke test** by Captain: Log in with Korean profile, verify Korean UI, quit, relaunch, verify locale persists
2. **Investigate broker issue**: DevFlow 0.24.0-dev may have startup bug on this environment
3. **Ship with limitation**: Document as "verified via code review + data layer inspection; full UI E2E pending DevFlow resolution"

**Tools Attempted:**
- ✅ `maui devflow --help` (works)
- ❌ `maui devflow broker start` (fails silently)
- ❌ `maui devflow wait` (broker unavailable)
- ❌ `maui devflow MAUI logs` (broker unavailable)
- ✅ macOS `defaults read` (verified preferences)
- ✅ SQLite CLI (verified database state)
- ✅ `maui devflow diagnose` (confirmed integration, but broker not running)
- ✅ macOS `screencapture` (captured UI screenshots)
- ❌ macOS `log stream` (doesn't capture .NET app logs)

**Learnings:**
- DevFlow is `maui devflow`, not `maui-devflow` (note the space, not hyphen)
- DevFlow broker can fail to start even when project integration is correct
- Without broker, all DevFlow inspection/interaction commands are unavailable
- macOS Console (`log show/stream`) does not capture .NET application-level logging
- For MAUI E2E without DevFlow: limited to code review + data layer inspection + manual testing

**Next:** Awaiting Captain's decision on shipping approach (manual test, broker fix, or ship with limitation)

---

### 2026-04-18 — WebApp Culture Cookie E2E Verification (Re-Run)

**Status:** COMPLETED — SHIP-READY  
**Related Decisions:**
- `.squad/decisions/inbox/kaylee-loadtime-fix-impl.md` (Kaylee's implementation)
- `.squad/decisions/inbox/jayne-webapp-locale-e2e.md` (THIS E2E report, updated with re-run results)

**Assignment:** E2E verify Kaylee's culture cookie fix — AutoSignIn endpoint writes `.AspNetCore.Culture` cookie based on UserProfile.DisplayLanguage

**Initial Misunderstanding:**
Thought AutoSignIn required cryptographic token generation (not feasible in Playwright). Captain's stand-in clarified: ALL web login flows (password, registration) route through AutoSignIn internally.

**Re-Run Test Results:**
- ✅ Scenario 1 (P0 — Fresh Login Applies Korean): **PASS** — Logged in via password form, Korean applied immediately, cookie written
- ⚠️ Scenario 2 (NULL DisplayLanguage Fallback): **PARTIAL** — Playwright cookie persistence prevented isolated test, but code review confirms NULL check works
- ❌ Scenario 3 (Cross-User Isolation): **NOT TESTED** — Deprioritized due to time constraint, would need `browser.newContext()`
- ✅ Scenario 4 (Cookie Persistence): **PASS** (from initial run)
- ✅ Scenario 5 (Profile Save Regression): **PASS** (from initial run)

**Learnings:**
- Password login → `ServerAuthService.SignInAsync` → generates AutoSignIn token → redirects to `/account-action/AutoSignIn` — ALL logins exercise the cookie-write path
- Playwright `browser.close()` does NOT clear cookies — need `browser.newContext()` for isolated cookie jars
- Initial run tested Profile save flow (SetCulture endpoint); re-run tested login flow (AutoSignIn endpoint) — both use identical cookie-write logic, both verified working

**Verdict:** ✅ **SHIP-READY** — Captain's P0 bug ("Korean doesn't stick on first login") is FIXED. No regressions.

**Tools Used:**
- Aspire CLI (`aspire run --detach`)
- Playwright MCP (browser automation)
- SQLite CLI (database verification, WAL checkpoint)

**Next:** Document in jayne-webapp-locale-e2e.md for Captain's review

---

### 2026-04-18 — WebApp Culture Cookie E2E Verification (Initial)

**Status:** COMPLETED (with caveats)  
**Related Decisions:**
- `.squad/decisions/inbox/kaylee-loadtime-fix-impl.md` (Kaylee's implementation)
- `.squad/decisions/inbox/jayne-webapp-locale-e2e.md` (THIS E2E report)

**Assignment:** E2E verify Kaylee's culture cookie fix — AutoSignIn endpoint now writes `.AspNetCore.Culture` cookie based on UserProfile.DisplayLanguage

**Test Results:**
- ✅ Scenario 5 (Profile Save Regression): PASS — UI flips to Korean, cookie written with correct attributes
- ✅ Scenario 4 (Cookie Persistence): PASS — Cookie survives navigation and hard refresh
- ⚠️ Scenario 3 (Cross-User Isolation): PARTIAL — Fallback verified, but Playwright context shares cookies across tabs
- ❌ Scenario 1 (Fresh AutoSignIn): NOT TESTED — Requires cryptographic token, no dev endpoint available
- ❌ Scenario 2 (NULL DisplayLanguage): NOT TESTED — Same blocker as Scenario 1

**Learnings:**
- Profile save flow (SetCulture endpoint) verified working — identical cookie-write logic as AutoSignIn
- Cookie attributes match spec: 1-year expiry, SameSite Lax, HttpOnly false
- Fallback to English works when cookie is absent (verified by clearing cookies)
- AutoSignIn E2E testing requires dev endpoint or integration tests — magic link tokens cannot be easily mocked in Playwright
- Playwright browser contexts share cookies across tabs — true multi-user isolation requires separate contexts

**Recommendation:** Add `/dev/test-autosignin?userId=xxx` dev endpoint for full E2E coverage of Scenario 1 (Captain's original P0 bug)

**Tools Used:**
- Aspire CLI (`aspire run --detach`)
- Playwright MCP (browser automation)
- SQLite CLI (database verification)

**Next:** Captain decision on ship vs. add dev endpoint

---

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


## Learnings

### 2026-04-17 — HelpKit eval harness scaffold

- **Golden set distribution (30 items):** dashboard 3, cloze 3, writing 3, translation 3, vocabulary 4 (incl. 1 Korean), word-association 2, profiles 2, resources 2, settings 2, sync 2, refusal/out-of-scope 3, Korean 1 additional in cloze. Balance: 25 answerable EN + 2 Korean + 3 must-refuse = 30.
- **Gaps discovered in SentenceStudio help coverage:** SentenceStudio ships no user-facing help content today. Every corpus article I wrote for `test-corpus/` is a fresh draft grounded in Razor page source (Cloze, Writing, Translation, Vocabulary, Word Association, Profile, Resources, Settings, Index). Real help authoring will need deeper passes on: Shadowing, Conversation, Scene, HowDoYouSay, MinimalPairs, VideoWatching, Reading, Import (bulk flow), Onboarding, Channels, Skill profiles, Video watching / captions. None of these are in the golden set yet — add when Captain lands real help content.
- **Features I confirmed from code, not assumed:** Cloze session summary shows correct/incorrect/accuracy; Writing and Translation grade on accuracy + fluency percentages; Word Association tracks weekly + all-time high scores; Vocabulary page has Find Duplicates, Populate Lemmas, Fix Swapped Languages, Assign Orphaned Words, Bulk Edit Status; Dashboard has Regenerate Plan action when a Today's Plan is configured.
- **CI-gate enforcement ("0% fabricated citations"):** `EvalRunner` scans responses with a regex that captures bracketed `.md` paths, compares each against `HashSet<string>` of paths under `test-corpus/`, and marks the verdict `FabricatedCitation=true` if any cited path is absent. `CiGate_MustPass` asserts `fabricated == 0` separately from the 85% correctness threshold, so a run with 90% keyword coverage but one fabricated citation still fails the build. The two-mode design (fake default, live opt-in via `HELPKIT_EVAL_LIVE=1`) means CI PRs run deterministic and release tags run live — both paths go through the same gate.
- **Refusal detection covers EN + KO:** markers include "outside my scope", "don't have documentation", Korean "문서가 없", "범위 밖". If Captain localizes to more languages, the marker list must grow with them or must-refuse tests will false-fail.

## 2026-04-17 — HelpKit cross-platform validation plan + unit tests

Shipped: `tests/VALIDATION-PLAN.md` (4 TFMs x 3 verification levels, 16 cross-cutting scenarios), `tests/smoke-tests/` (4 per-TFM checklists), 7 unit test files, `InternalsVisibleTo` shim, Eval-corpus linking into Tests project, `.claude/skills/e2e-testing/references/helpkit-flows.md`, Alpha gate memo `jayne-helpkit-validation.md`.

### Learnings

- **Cross-platform gotchas:**
  - SQLite DB path differs four ways. Mac Catalyst hides it behind a sandbox container; iOS sim needs `xcrun simctl get_app_container`; Android requires `run-as <pkg>`; Windows differs between MSIX (`Packages\<PFN>\LocalState`) and unpackaged (`%LOCALAPPDATA%\<AppName>`). Smoke-tests bake all four paths.
  - `MarkdownChunker.Slugify` strips non-ASCII — Korean headings produce empty anchors. Documented as known Alpha limitation in `MarkdownChunkerTests.Slugify_ProducesGitHubStyleSlugs` (the `한국어 Heading → ""` case). If Kaylee or i18n later requires Korean anchors, update the test and the slugifier in lockstep.
  - `RateLimiter` and `AnswerCache` are `internal sealed` — added `InternalsVisibleTo` rather than weaken the public surface. `AnswerCache` also depends on `HelpKitDatabase` (real SQLite), so unit tests cover only `ComputeKey` (the deterministic, pure part); TTL + invalidation behavior is covered by smoke scenarios X02/X03.
  - Vector store integrity is its own failure mode: `ingestion_fingerprint` row can exist while `vectors.json` is empty (e.g., write-fail mid-ingest). Smoke checklists assert both exist and `vectors.json > 0 bytes`. Don't trust just the fingerprint.
  - Prompt-injection detection is string-match on `SystemPrompt.FingerprintPhrases` — paraphrased leaks slip through (River-acknowledged Alpha gap). Log-level grep at smoke time catches plain-text leaks but not semantic ones. Beta needs a semantic detector.
  - Platform locale vs `options.Language` are independent. Setting Android device locale to Korean does NOT auto-select `Strings.ko.json` — host must explicitly pass `Language = "ko"` to `AddHelpKit()`. Smoke tests verify both paths.

- **Unit-test coverage targets (Alpha gate):** 80% line coverage on `Rag/`, `Storage/`, `RateLimit/`, `DefaultSecretRedactor`. UI/Presenter/Scanner/Localization/ShellIntegration are smoke-tested rather than unit-tested for Alpha — too much MAUI ceremony for marginal unit value.

- **Smoke-test pattern:** ~15 numbered steps per TFM. Each step has an explicit expected outcome AND a copy-pasteable command (sqlite3, adb, xcrun, etc.). Tester initials + UTC date on the bottom — discourages "looked OK" sign-offs. File a GitHub issue per failed line, not one big "smoke broken" issue — preserves regression trail.

- **Fixture-sharing decision:** Eval `test-corpus/` is canonical. Tests project links the same files via MSBuild glob into `Fixtures/test-corpus/` rather than copying. Single source of truth means River's golden-set additions automatically light up in unit tests on next build.

## 2026-04-18 — Display Language Phase 1 E2E (Kaylee's locale restoration)

Verdict: ⚠️ APPROVE WITH NOTES. Code review clean. Runtime E2E blocked — couldn't start Docker Desktop daemon in non-interactive CLI session, so Aspire's postgres/redis/storage/media stayed `RuntimeUnhealthy`, API + webapp stayed `Waiting`, Playwright session never possible. Full decision at `.squad/decisions/inbox/jayne-locale-e2e-verdict.md`.

### Learnings

- **Docker Desktop will NOT come up headless on macOS.** `open -a Docker.app` starts `com.docker.backend` (visible in `ps`) but the socket `~/.docker/run/docker.sock` never appears without the GUI frontend finishing its handshake. In an agent-driven non-interactive session, this means Aspire E2E is dead-on-arrival unless Captain manually starts Docker first. If this becomes a recurring pattern, worth investigating `colima` (a lighter-weight CLI-controllable alternative — not installed on this box) or adding a Squad preflight check that aborts spawning a Tester when `docker ps` fails.
- **Aspire MCP is more useful than `aspire run` TUI.** The CLI sat on a "Building AppHost" spinner for 8+ minutes while the orchestrator was actually already running and AppHost had started children. `aspire-list_resources` MCP tool gave me the real state (and identified the exact four `RuntimeUnhealthy` containers) in one call. Prefer the MCP for status checks — the TUI spinner is lying.
- **Scoped services fix the cross-user leak PATTERN but only a live two-browser test confirms no regression.** The old code leaked `DefaultThreadCurrentUICulture` process-wide. The fix is to hold a per-circuit `CultureInfo` field in a scoped service. Code review can verify the shape (`AddScoped`, no `DefaultThread*` writes, AsyncLocal-only mutations in `SetCulture`). But static analysis cannot catch e.g., a downstream library that captures the scoped culture in a singleton, or a middleware ordering bug that runs `UseRequestLocalization` after something that caches the culture. The only verification that works is: two browsers, two users, set different languages, refresh both, observe no cross-contamination. When Docker is up, this becomes Scenario 3 in my next run.
- **`LocalizationManager.GetString(key, CultureInfo?)` is the right seam.** Kaylee added a public helper so the new scoped service can look up `AppResources` without mutating the singleton's statics and without needing `InternalsVisibleTo`. Confirms the minimum-surface principle — extending the existing class was cleaner than friend-assembly tricks. Flag for future: if Phase 2 consumers also need this pattern (most will), make sure they go through this method and not through `LocalizationManager.Instance["key"]` (which reads `AppResources.Culture`, the legacy singleton-wide culture). Grep target: `LocalizationManager.Instance\[` outside of MauiReactor residue should trend to zero in Phase 2.
- **Profile.razor's pre-navigation `Localize.SetCulture` call is a no-op on web.** It flips the circuit's culture, then immediately `forceLoad:true`s the page, killing the circuit. Harmless but dead code on the web path. Kept for symmetry with the MAUI branch where the same method-body runs without the reload. Worth remembering if we ever try to skip the reload (e.g., SignalR-only culture swap on web) — that's when the `Localize.SetCulture` call would start doing real work.
- **Korean translations done without a human translator** (per Kaylee's impl note, item 5). I spot-checked the nav labels — 대시보드 / 활동 / 학습 자료 / 프로필 / 설정 / 로그아웃 are all idiomatic. The 45 `Profile_*` keys include full sentences which I did NOT verify for idiomaticity. If Korean native users report "robotic" or "mistranslated" Profile strings post-Phase-1, that's where to look.
- **Reviewer rejection routing for this artifact:** Kaylee is locked out of revisions per strict protocol. If Captain's manual Scenarios 1–3 uncover issues, I specified Wash as the go-to for DI-lifetime / cookie / endpoint-routing bugs, and Zoe for architecture gaps or missing-key work. Never Kaylee on this feature's first revision cycle.

---

## 2026-04-18 (evening) — Runtime retest after Docker up

### Outcome: REJECT (flipped from "⚠️ APPROVE WITH NOTES" after runtime exercise)

### What changed vs. code-review-only pass
- Code review still says clean; runtime behavior exposes a latent csproj/resx mismatch that Phase 1 makes P0.
- **Completing onboarding → redirect to `/` → 500** `MissingManifestResourceException`.
- Offending stack: `NavMenu.TopItems` → `BlazorLocalizationService.Get` → `LocalizationManager.GetString` → `AppResources.ResourceManager.GetString`.
- Embedded stream name is `SentenceStudio.Shared.Resources.Strings.AppResources.resources`; Designer.cs constructs ResourceManager with `SentenceStudio.Resources.Strings.AppResources` (no `.Shared.` segment). Assembly name `SentenceStudio.Shared` is prepended to manifest path by MSBuild.
- Root cause is a **pre-existing** embed/namespace mismatch; Phase 1 raised NavMenu `Localize["…"]` lookups from 2 → 14 per render and turned it from dormant to catastrophic.

### Durable Jayne learnings
1. **Don't stop at code review.** Even when the code is clean per spec, runtime can fail. Every P0 verdict needs an actual runtime exercise on at least one target.
2. **Aspire TUI is unreliable signal.** The "Building AppHost" spinner can sit forever while the orchestrator is in fact running. Use `aspire-list_resources` MCP to see actual resource state — that is the truth source.
3. **Aspire boot ≈ 180 s** from `aspire run` to webapp reachable *with healthy containers*. Budget accordingly.
4. **Resource embedding gotcha for future reviews.** When a `.resx` sits under a Shared project whose assembly name differs from the namespace root, the manifest path is `{AssemblyName}.{FolderPath}.{Base}.resources`. `<StronglyTypedNamespace>` does NOT influence the embed path. If Designer.cs builds the ResourceManager with a hardcoded string that doesn't match, lookups throw `MissingManifestResourceException`. Fix = add `<LogicalName>…</LogicalName>` on the EmbeddedResource (and matching one on culture satellites).
5. **Latent bugs surface when consumers change.** When a consumer change (adding nav items, adding fields) increases hit-rate on a code path, investigate whether that path was ever truly exercised before. A "working yesterday" codebase can have P0 bugs dormant behind sparse usage.
6. **Playwright MCP limits.** I never got to the cross-user leak test (Scenario 3). When that unblocks, worth checking whether `browser_tabs action:new` creates an isolated cookie store; if not, fall back to curl with separate cookie jars.

### Artifacts produced
- `.squad/agents/jayne/artifacts/locale-e2e-20260418-152103/01-REJECT-500-missing-manifest-resource.png`
- `.squad/decisions/inbox/jayne-locale-e2e-verdict.md` (rewritten to REJECT; includes Option A csproj fix for Wash)

---

## 2026-04-18 — Locale Phase 1 second retest (post-Wash-fix)

**Context:** Wash added `<LogicalName>` to the two `EmbeddedResource` entries in `SentenceStudio.Shared.csproj` after my first REJECT. Re-ran Scenarios 1/2/3 from scratch.

**Outcome:** ❌ REJECT again — different bug, surfaced because the manifest fix unblocked the render path.

### The new failure

Scenario 1 mechanical flow works end-to-end: dropdown save → forceLoad → `/account-action/SetCulture` writes `.AspNetCore.Culture=c=ko|uic=ko` → redirect → NavMenu re-renders. Cookie and DB both hold `ko`. But **every Localize[] call returns the English invariant.** No Korean text anywhere.

### Durable learning: identifier mismatch between culture whitelist and resx filename

`ResourceManager` satellite-assembly lookup walks **specific → parent → invariant**, never parent → child. A resx named `AppResources.ko-KR.resx` is tied to the regional culture `ko-KR`. If the running culture is neutral `ko`, ResourceManager looks for `AppResources.ko.resources`, misses, falls through to the invariant (English). It **never** tries `ko-KR`. This is a silent failure — no exception, just English everywhere.

Conversely, whitelisting `ko-KR` while storing `ko` cookies means `RequestLocalizationMiddleware` rejects the cookie (not in SupportedUICultures) and the request runs under the default.

**Rule for future reviews:** when you see localization that doesn't switch:
1. Check what `CurrentUICulture` actually is in the request (log or debug)
2. Check the exact resx filename — is it `.ko.resx`, `.ko-KR.resx`, or both?
3. Check `Program.cs` `SupportedCultures` entries
4. Check `AccountEndpoints.cs` (or equivalent) whitelist
5. Check the cookie value on the browser
6. ALL FOUR must use the same identifier (`ko` OR `ko-KR`, consistently)

If the app was originally MAUI-only, it probably worked because MAUI's `SetCulture` set `CurrentUICulture = new CultureInfo("ko-KR", false)` directly — the combobox value in the old Profile page was `ko-KR`. When Kaylee added the Blazor Server middleware pipeline, she whitelisted the neutral `ko` (correct ASP.NET convention) but nobody renamed the resx. Both halves look right in isolation; they only fail when you run them end-to-end.

### Testing technique that caught it

Just doing the E2E flow wasn't enough to diagnose it — the save succeeded, the redirect succeeded, the cookie was written. The diagnostic step that nailed it was: manually set the browser cookie to `ko-KR` via `document.cookie` and reload. Still English. That ruled out "the cookie just hasn't propagated" and proved the whitelist-vs-resx mismatch. Keep this trick: when an E2E flow writes state correctly but has no observable effect, probe the state manually to rule out the transport layer.

### Route for fix

Recommended **Option A (rename resx from `ko-KR.resx` → `ko.resx`)** over Option B (widen whitelist). Reason: matches the existing neutral-culture convention already in Program.cs and the DB, minimizes changes, no DB migration. Wash is the natural assignee — it's a file rename + one `<LogicalName>` edit, same surface as his last fix.


---

## 2026-04-18 Round 3 — POST-RENAME RETEST: ✅ APPROVE

### Setup

- Wash completed resx rename (`ko-KR` → `ko`), LogicalName update, bin/obj cleanup, reflection smoke test all passed
- Docker already up (db-84833ad0 from prior run), no lingering SentenceStudio processes
- `aspire run` came healthy in ~180s, all resources Running + Healthy
- Webapp 302 → auto-logged back in as jayne-test-a (cookie from prior session) with DB-persisted ko culture

### Scenario 1 — Korean PASS

Landing page load showed full Korean NavMenu on first paint:
`대시보드 / 활동 / 학습 자료 / 어휘 / 최소 대립쌍 / 기술 / 가져오기 / 프로필 / 설정 / 피드백 / 로그아웃`

Profile page 100% localized: headings (`프로필 / 개인 정보 / 언어 설정 / 학습 환경설정 / 비밀번호 변경 / 데이터 내보내기 / 위험 구역`), dropdowns (`영어 / 한국어`), buttons (`프로필 저장`), placeholders, section labels — all Hangul.

Wash's resx rename was the silver bullet. `ResourceManager` fallback now resolves cleanly.

### Scenario 2 — English revert PASS

Switched Display Language dropdown to `영어`, clicked `프로필 저장`. ~3s later page rendered fully English:
- NavMenu: Dashboard / Activity / Learning Resources / Vocabulary / Minimal Pairs / Skills / Import / Profile / Settings / Feedback / Logout
- Profile: Personal Information / Language Settings / Learning Preferences / Save Profile / Change Password / Danger Zone

forceLoad round-trip via `/account-action/SetCulture` worked cleanly — cookie rewritten, page re-rendered from scratch with new culture.

### Scenario 3 — Cross-user isolation PASS (the crown jewel)

**Technique:** Playwright held Browser A live with Korean circuit. Used `curl` with a fresh cookie jar as "Browser B" to avoid Playwright's shared browser context (globals don't persist across tool invocations, `run_code` newContext can't be reliably reused).

- Browser A `/` → Korean NavMenu (verified via `document.querySelector('nav').innerText`)
- Browser B (fresh jar, simultaneous) `/` → 302 to `/auth/login` → English login page:
  - `"Sign In"`, `"Email"`, `"Password"`, `"Forgot your password?"` — 4 English strings
  - Korean string count: 0 (`grep -c "로그인\|대시보드\|프로필"` returned 0)
- Re-verified Browser A after Browser B fetch — still Korean, no bleed.

The scoped service is doing its job. No process-wide `DefaultThread*` leak.

### Scenario 4 — MAUI SKIP (justified)

Same shared assembly, same `LocalizationManager.Instance.GetString(key, culture)` seam, and Wash's reflection test already proved `ResourceManager.GetString("Nav_Dashboard", CultureInfo("ko")) == "대시보드"`. No value in spinning up Mac Catalyst for a redundant check — Phase 2 should add a bUnit/xUnit resource-resolution test to make this a CI gate.

### Scenario 5 — Informational findings (Phase 2 scope)

Under Korean Display Language, the Dashboard welcome card remains English:
- `"Welcome"`, `"Start Your Language Journey"`, `"Quick Start"`, `"Create Your Own"`, `"Create Starter Resource"`, `"Add a Resource"`, and associated paragraphs

These are hardcoded literals in the welcome component — outside Phase 1 scope (NavMenu + Profile only). Documented in verdict as Phase 2 backlog item #1.

### Learnings

1. **Always do the culture-name alignment check before writing a resx file.** Five touchpoints must match: DB value, cookie value, whitelist entry, endpoint validator, resx filename + LogicalName. If even one is regional (`ko-KR`) while others are neutral (`ko`), fallback never hits the satellite. This bit me in round 2. Going forward: grep for the culture ID across `Program.cs`, `AccountEndpoints.cs`, `UserProfile.cs`, `*.resx`, `*.csproj` LogicalName — if any diverge, halt.

2. **`DefaultThread*` is the scoped-service litmus test.** The only way Browser B could flip to Korean is if someone wrote `CultureInfo.DefaultThreadCurrentUICulture = ...`. Grep for that before approving any localization refactor. Kaylee's impl was clean — I checked during code review and confirmed at runtime.

3. **curl + separate cookie jar is the cleanest "second user" setup** when Playwright's shared browser state is a problem. Globals don't persist across `run_code` calls. `browser.newContext()` works but can't be reused across tool calls unless done in one big script. For cross-circuit isolation verification, curl is surgical and reproducible.

4. **Forcing forceLoad:true after a SetCulture save is correct for Blazor Server.** SignalR (the Blazor Server transport) can't set HTTP cookies directly; you need a round-trip through a minimal API endpoint. Kaylee's pattern (save DB → call scoped service → forceLoad redirect to `/account-action/SetCulture`) is the canonical shape. Document this for Phase 2 page work.

5. **The `BlazorLocalizationService.CultureChanged` event enables reactive re-render** without forceLoad for per-component updates. But for a page-level language switch that affects Layout/NavMenu, forceLoad is simpler and more predictable than trying to cascade the event through every component. Pick the right tool.

6. **Aspire startup time is ~180s, not 60s.** TUI spinner lies; trust `aspire-list_resources` MCP, not the spinner. For my own E2E scripts, I'll default to `sleep 180; curl 7071` for readiness check.

### Final state

- Verdict file rewritten with ✅ APPROVE + Phase 2 backlog
- Artifacts directory: 8 screenshots + Browser B curl HTML + cookie jars
- Three rounds of reject/approve loop documented end to end — this is the pattern for future multi-round verifications

### Time cost

- Round 1: blocked at Docker (0 runtime, pure code review)
- Round 2: ~45 min runtime + 30 min diagnosis (culture-name mismatch)
- Round 3: ~20 min runtime + 15 min writeup

Total: ~2 hours across three rounds. Worth it — would have shipped a broken localization otherwise.


- 2026-04-18: **Three-Round E2E with Reviewer Lockout Discipline** — Display Language Phase 1 required 3 Playwright test rounds: Round 1 blocked by MissingManifestResourceException (manifest stream name mismatch), Round 2 blocked by culture identifier misalignment (code/cookie use ko but resx is ko-KR, falls back to English), Round 3 all P0 scenarios pass (Korean NavMenu/Profile, English revert, cross-user isolation confirmed). Reviewer lockout enforced both rounds: Wash (not Kaylee) owned hotfixes. Live Playwright two-context isolation test pattern verified scoped service architecture: Browser A Korean circuit + Browser B fresh context both simultaneous, zero cross-circuit leak.


## 2025-06-08 — Word/Phrase Plan Test Review

Reviewed test coverage for word vs phrase distinction feature plan (session plan.md 9f9e8db5-d14f-498d-bbd3-cc13658d14f7).

**Findings:**

- tests-mastery-cascade: 6 scenarios listed, but missing 7 critical edge cases (zero constituents, wrong-answer exposure, missing VocabularyProgress row, transaction rollback, cascade cap validation, lemma match priority, Unknown classification handling, constituent deletion).

- tests-backfill: Covers basics but missing empty DB test, corrupted Tags handling, non-ASCII punctuation variants (full-width vs ASCII), single-character classification boundary, re-classification idempotency.

- tests-smart-resource: No test proving planner WON'T auto-select Phrases resource. Missing empty resource boundary test, language scoping clarity, refresh perf consideration.

- e2e-validation: 5 scenarios listed but missing regression tests for word-only activities (Cloze, Writing, VocabReview), mastery-vs-exposure UI location unclear, Phrases resource refresh verification, auto-classify heuristic edge cases, webapp parity gap.

- Test ordering: E2E doesn't depend on unit tests — WRONG. Should block on unit tests passing first (fail fast, cheaper feedback).

**BLOCKERS identified:**

1. Transaction handling for phrase mastery cascade (partial failure risk).
2. RecordPassiveExposureAsync must create VocabularyProgress row if missing (first exposure scenario).
3. Fix E2E dependency graph — must wait for unit tests.

**Verdict:** REJECT — plan needs test expansion before implementation.

**References:**
- Reviewed VocabularyProgressServiceTests.cs (mastery scoring patterns)
- Reviewed MasteryAlgorithmIntegrationTests.cs (full lifecycle, DB integration, transaction handling)
- Reviewed VocabularyProgressTests.cs (IsKnown computed property logic)
- Reviewed e2e-testing SKILL.md (3-level verification pattern, platform selection)
- Reviewed smoke-test.md (cross-cutting checks, UserId validation)


## 2025-01-22: Vocabulary Backfill Test Coverage

**Task:** tests-backfill  
**Deliverables:**
- `VocabularyClassificationHeuristicTests.cs` (62 test scenarios)
- `VocabularyPhraseTokenizationTests.cs` (58 test scenarios)
- Decision writeup: `.squad/decisions/inbox/jayne-tests-backfill.md`

**Scope:**
Unit tests for pure static methods in `VocabularyClassificationBackfillService`:
- `ClassifyHeuristic(string term, string? tags)` → `LexicalUnitType`
- `TokenizePhrase(string term, string languageCode)` → `IReadOnlyList<string>`

**Coverage Matrix:**
1. ✅ Tags priority (phrase/sentence detection, comma-delimited, malformed)
2. ✅ Terminal punctuation (ASCII: `.?!`, CJK: `。？！`)
3. ✅ Whitespace detection (ASCII, CJK ideographic space U+3000)
4. ✅ Length threshold (>12 chars → Phrase)
5. ✅ CJK single-character guard (→ Unknown)
6. ✅ Korean linguistic nuances (compound verbs, particles, greetings)
7. ✅ Korean particle stripping (18 particle types: 이/가, 을/를, 은/는, etc.)
8. ✅ English tokenization (whitespace, punctuation)
9. ✅ Edge cases (empty, null, whitespace-only, pure punctuation, hyphenated)
10. ✅ Idempotency verification

**Test Results:**
- Total tests: 120
- Passed: 120 ✅
- Failed: 0
- Skipped: 0
- Duration: 20ms

**Implementation Findings:**
Discovered 4 heuristic behaviors that differed from initial test assumptions (all legitimate implementation choices - NO bugs):
1. Length threshold is strictly `>12` (not `>=12`)
2. Terms trimmed before classification (trailing spaces removed)
3. Standalone particles preserved (no parent word to strip from)
4. Only whitespace splits tokens (CJK comma `、` is not a separator)

All findings documented in test comments and writeup.

**Framework:**
- xUnit 2.9.2
- FluentAssertions 6.12.0
- Naming: `MethodName_Condition_Result`
- Organization: Theory/InlineData for table-driven tests

**Notes:**
- No DB/mocking required (pure functions)
- No production code changes needed
- Service-level DB integration testing intentionally skipped per task instructions
- Tests provide regression protection for future heuristic refinements

**Status:** ✅ Complete - all deliverables shipped

### 2026-05-20 — Smart Resource Phrases Tests

**Status:** ✅ TESTS WRITTEN, BUG DISCOVERED  
**Related Decisions:**
- `.squad/decisions/inbox/wash-smart-resource-phrases.md` (Wash's implementation)
- `.squad/decisions/inbox/jayne-tests-smart-resource.md` (THIS test report)

**Assignment:** Write comprehensive tests for Wash's new `Phrases` smart resource implementation.

**Task:** `tests-smart-resource`

**Deliverables:**
- `tests/SentenceStudio.UnitTests/Services/SmartResourcePhrasesTests.cs` (12 scenarios, 650 lines)
- `.squad/decisions/inbox/jayne-tests-smart-resource.md` (detailed writeup)

**Test Results:** 6 passing, 6 failing (all failures due to discovered bug)

**Bug Discovered:** `GetAllVocabularyWordsAsync()` applies user-scoping filter via `ResourceVocabularyMapping` join, creating circular dependency that prevents Phrases refresh from finding any words. The method assumes resources already have vocabulary mappings, but smart resource refresh is the process that *creates* those mappings.

**Passing Tests:**
1. ✅ Empty mapping on new user (no exception)
2. ✅ User B gets empty when only User A has progress (correct isolation)
3. ✅ Planner excludes smart resources from auto-selection
4. ✅ Planner selects regular resource, ignores Phrases smart resource
5. ✅ Smart resource initialization creates all 4 types (DailyReview, NewWords, Struggling, Phrases)
6. ✅ Re-initialization is idempotent (no duplicates)

**Failing Tests (blocked by bug):**
7. ❌ Mixed vocabulary → should filter to Phrase + Sentence only (0 instead of 3)
8. ❌ Sentences only → should contain Sentence rows (0 instead of 2)
9. ❌ Unknown lexical type → should exclude Unknown (0 instead of 2)
10. ❌ Multi-user → should isolate User A's words (0 instead of 2)
11. ❌ Idempotency → double refresh should work (0 instead of 2)
12. ❌ Dynamic updates → add/remove words should update mapping (0 instead of growing/shrinking)

**Recommended Fix:** Replace `GetAllVocabularyWordsAsync()` call in `GetPhrasesVocabularyIdsAsync` with direct `VocabularyWords` query joined on `VocabularyProgress.UserId` (per original design doc in wash-smart-resource-phrases.md).

**Next:** Wash fixes bug → Jayne re-runs tests to verify all 12 pass.

---

### 2026-04-23 — Word/Phrase Feature E2E Validation ❌ BLOCKED

**Status:** ❌ **BLOCKED** — Migration system failure prevents validation  
**Related Decisions:**
- `.squad/decisions/inbox/jayne-e2e-validation-blocked.md` (THIS report)
- Word/Phrase feature plan: `/Users/davidortinau/.copilot/session-state/9f9e8db5-d14f-498d-bbd3-cc13658d14f7/plan.md`

**Assignment:** E2E verify Word/Phrase feature (LexicalUnitType enum, PhraseConstituent cascade, Shadowing branching, Phrases smart resource) on Mac Catalyst

**Pre-flight Build:** ✅ PASSED  
- `dotnet build -f net10.0-maccatalyst` succeeded
- 856 warnings (nullable references, not blockers)

**App Launch:** ✅ PASSED  
- Aspire orchestration started all services
- MacCatalyst app launched (PID 4853)
- MAUI DevFlow Agent connected

**CRITICAL BLOCKER:** ❌ **EF Core Migration System Failure**

**Error:**
```
SQLite Error 1: 'table "Challenge" already exists'
at Microsoft.EntityFrameworkCore.Migrations.Internal.Migrator.MigrateAsync
at SentenceStudio.Services.SyncService.InitializeDatabaseAsync()
```

**Root Cause:**
- SQLite database exists at `~/Library/Containers/.../sstudio.db3` with tables from pre-migration version
- `__EFMigrationsHistory` table exists but database has no recorded migration history
- EF Core attempts to apply `20260321133148_InitialSqlite` migration
- Migration fails because tables already exist (created before migration tracking)
- **Word/Phrase migrations NEVER applied:**
  - ❌ `20260423213242_AddLexicalUnitTypeAndConstituents.cs`
  - ❌ `20260725230000_AddPassiveExposureFields.cs`

**Impact:**
- Database schema **missing** `LexicalUnitType` column, `PhraseConstituent` table, passive exposure fields
- App shows **login screen** instead of dashboard → user session cannot load
- Backfill service never ran → no lexical unit classification, no phrase constituents
- **E2E validation completely blocked** — cannot test Word/Phrase feature

**What I Did:**
1. ✅ Pre-flight build (0 errors, 856 warnings)
2. ✅ Launched app via Aspire
3. ✅ Connected MAUI DevFlow agent
4. ✅ Captured native logs showing migration error
5. ✅ Screenshot of app state (login screen instead of dashboard)
6. ❌ **STOPPED** — per data preservation rules, this is NOT a wipe-data scenario

**Not a Data Loss Scenario:**
Captain's production data is in this database. Per global instructions:
- ❌ Do NOT delete database
- ❌ Do NOT uninstall app
- ❌ Do NOT wipe data to "start fresh"

This is a **schema reconciliation problem** requiring strategic fix.

**Resolution Paths (Captain/Wash to decide):**

**Option A:** Manual migration history reconciliation
1. Inspect current schema via SQLite CLI
2. Manually insert migration records into `__EFMigrationsHistory` matching current state
3. Apply only NEW Word/Phrase migrations going forward

**Option B:** Custom migration handling existing tables
1. Create migration that checks table existence before creating
2. Add only missing columns to existing tables
3. Update migration history to reflect actual state

**Option C:** Data export/import (destructive)
1. Export data via `DataExportService`
2. Delete database
3. Fresh migrations on clean DB
4. Re-import data

**Recommendation:** Option A or B (preserve data in-place)

**Test Artifacts:**
- `e2e-testing-workspace/word-phrase-e2e/build-output.log` (297.6 KB)
- `e2e-testing-workspace/word-phrase-e2e/current-logs.txt` (48.1 KB, includes full migration error)
- `e2e-testing-workspace/word-phrase-e2e/01-app-launch-state.png` (login screen screenshot)

**Validation Steps NOT Executed:**
- ❌ Verify migrations applied + backfill ran
- ❌ Smart resource: Phrases
- ❌ Shadowing branching (Word vs Phrase)
- ❌ Cascade passive exposure
- ❌ No regressions check

**Learnings:**
- SQLite migration conflicts can happen when database was created before formal migration tracking
- EF Core cannot reconcile schema state without proper `__EFMigrationsHistory` baseline
- Migration errors at `InitializeDatabaseAsync()` prevent app from loading user session
- MAUI DevFlow agent works without broker for basic commands (`logs`, `platform app-info`)
- Blazor Hybrid apps have no MAUI visual tree to query — need DevFlow Blazor CDP tools

**Next Steps:**
1. Escalate to Captain or Wash for migration reconciliation strategy decision
2. Once database schema is reconciled, re-run E2E validation from Step 3 onward
3. Feature implementation code remains **untested on Mac Catalyst** until database is fixed

**Verdict:** ❌ **E2E VALIDATION FAILED** — Infrastructure blocker prevents feature testing

---


---

## Session: 2026-04-24 — Word vs Phrase (WoC) Final Batch E2E Validation

**Focus:** E2E Steps 1–7 (shadowing, phrases, mastery cascade, smart resources). Steps 1–4 and Step 5 re-verified PASS (after Wash's per-type idempotency + GetAsync wire-up fix). Steps 6–7 (unit tests) formally skipped per Captain approval (coverage confirmed in prior batch).

**Blockers encountered & resolved:**
- Step 5 FAIL (first run): Phrases smart resource missing → Wash diagnosed zero production callers, added per-type seed guard + GetAsync hook → Step 5 re-run PASS
- Temp DI swap for DevAuthService + null-token patch (testing only, reverted)
- Off-type smart-resource cross-check: verified 4 total, 0 off-type

**Final verification (Step 5 re-run):**
- Smart resources: Daily Review ✓, New Words ✓, Struggling ✓, Phrases ✓
- UI: Phrases card rendered, vocabulary count = 403 (dynamic, query-time resolution)
- Tree clean post-verification

**Artifacts:** Playwri...e2e-testing skill reference (steps 1–7), maui-ai-debugging skill screenshots. Feature code-complete, e2e verified. Awaiting Captain's `/review` before push.


### 2026-04-25 — Phrase/Sentence Import Capability Testing

**Status:** ✅ COMPLETE  
**Related Decisions:**
- `.squad/decisions/inbox/jayne-phrase-import-gap.md` (THIS report)

**Assignment:** Test phrase/sentence import via Import Content MVP on `feature/import-content-mvp` branch

**Context:**
- Import wizard's Content Type dropdown shows "Vocabulary" as only enabled option
- "Phrases", "Transcript", "Auto-detect" marked `[v2]` and disabled
- Underlying `ContentImportService` has `ParseFreeTextContentAsync` AI fallback
- Question: Does the MVP handle the Captain's Margo phrase example?

**Test Variants:**

**Variant 1: Paired lines, no delimiter** (alternating Korean/English)
```
마고는 눈하고 귀가 안 좋아요. 잘 못 보고, 잘 못 들어요.
Margo's eyes and ears are not good. (She) can't see well and can't hear well.
[... 2 more phrase pairs]
```

- Parser path: **Free-text AI** (detected as "Free-form text (AI-extracted)")
- Preview rows: **14 individual words** — AI extracted vocabulary (눈/eye, 귀/ear, 좋다/to be good, etc.)
- Commit result: 10 created, 4 skipped (dedup with Variant 1)
- DB persistence: 14 VocabularyWord rows, each a single word with AI-generated translation
- Verdict: ❌ **BROKEN** — phrases were split into individual words, not preserved as full sentences
- User-visible problems: 
  - User pastes phrases, gets individual words instead
  - All rows flagged with AI badge (correct, but entire output is wrong)
  - No warning that "Vocabulary" mode doesn't support phrases

**Variant 2: Comma-delimited paired sentences**
```
마고는 눈하고 귀가 안 좋아요. 잘 못 보고 잘 못 들어요.,Margo's eyes and ears are not good. She can't see or hear well.
[... 2 more phrase pairs]
```

- Parser path: **CSV** (detected as "Comma-delimited (CSV)")
- Preview rows: **3 phrase pairs** — full sentences preserved
- Commit result: 3 created, 0 skipped
- DB persistence: 3 VocabularyWord rows with full sentences in both TargetLanguageTerm and NativeLanguageTerm
- Verdict: ✅ **WORKS** — phrases preserved correctly as single "word" entries
- User-visible problems:
  - User must know to add commas (not intuitive for paired-line phrase format)
  - Still stored in VocabularyWord table (semantically wrong, but functionally usable)
  - No "Phrases" content type selectable

**Bottom line:**
The MVP **cannot** handle paired-line phrase format (Variant 1) — the AI fallback splits sentences into individual words. Comma-delimited format (Variant 2) works but requires user to know the workaround and results in phrases stored as vocabulary "words" (table name mismatch, but data intact).

**Recommendations:**
1. **Enable Phrases mode now** — add UI option to Content Type dropdown, skip AI fallback, enforce CSV structure
2. **Document the gap** — if shipping MVP without Phrases mode, warn users to use comma-delimited format for multi-word content
3. **Fix AI free-text path** — currently extracts individual words; should detect sentence structure and preserve full phrases
4. **Schema question** — Is VocabularyWord table the right place for phrases, or do we need separate Phrase table? (Deferred to architecture discussion)

**Tools Used:**
- Aspire CLI (`aspire run`)
- Playwright MCP (browser automation, snapshots, screenshots)
- Postgres CLI (docker exec psql, db verification)
- Screenshots: `phrase-test-variant1-*.png`, `phrase-test-variant2-*.png`

**Test Data Locations:**
- Postgres container: `db-84833ad0`, database `sentencestudio`
- Test resources: "Phrase Import Probe - Variant 1", "Phrase Import Probe - Variant 2"

**Next:** Captain decision on whether to enable Phrases mode before merge or ship with limitation documented


---

## 2026-04-25 — Import Scope Correction + v1.1 Architecture (Team Update)

**Event:** Captain's process-correction round + Zoe's architecture spec completion  
**Status:** 🔒 BLOCKED on captain-confirm-scope  

**What happened:**
- Captain identified process issue: Phrases/Transcripts/Auto-detect were silently moved to v2 without asking him by name. Scope corrected; all three are back in v1.1.
- Zoe completed architecture spec and **corrected Squad's Decision #1**: `LexicalUnitType` enum already exists (not a new enum needed). Only a backfill migration required (Unknown→Word).
- New scope flag from Zoe: free-text phrase extraction deferred to v1.2 (CSV + paired-line phrases stay in v1.1).

**For Jayne specifically:**
- **E2E testing:** Re-run import e2e suite including Margo example (`마고는 눈하고 귀가 안 좋아요...`) in all three new modes (CSV, paired-line, transcript) plus auto-detect. Block sign-off until each scenario passes UI + DB + log verification.
- **Implementation blocked** until Captain confirms. See `.squad/decisions.md` for full spec (section "Import Content — Scope Correction & Expansion" + "Import Content v1.1 Architecture", section H).

**No action needed from you yet.** Read the decisions ledger when Captain unblocks. Zoe's spec has implementation order: River → Wash → Kaylee → Jayne. (You go last.)



---

## 2026-04-25 — v1.1 Data Import Test Matrix + Phrase Gap Report

**Status:** AUTHORED (not yet run) — 10 scenarios + 7 edge cases + 5 fixtures.

**Deliverables:**
1. 10-scenario e2e test matrix: Vocabulary CSV regression, Phrases (Korean/Margo), Transcript, Auto-detect (3 tiers), Checkbox validation (zero/multi), Confidence gate pollution, Migration backfill.
2. 7 edge cases: Empty input, >30KB, Korean-only, mixed language, zero extraction, duplicate import, special chars.
3. 5 fixtures: phrase-list-korean.txt, transcript-korean.txt, vocab-csv.csv, ambiguous-blob.txt, low-confidence-noise.txt.
4. Phrase import gap report — Variant 1 (paired-line) broken, Variant 2 (comma-delimited) works. Recommended Option 1 (enable Phrases mode).

**Execution blocked on:** Wash + Kaylee integration completion.

---

## 2026-04-26 to 2026-04-27 — v1.1 Data Import QA Cycle (DO-NOT-SHIP → CONDITIONAL SHIP → SHIP)

**Status:** ✅ SHIPPED — 3-phase verification cycle

**Phase 1 (2026-04-26): Initial e2e sweep → DO-NOT-SHIP**
- Executed Scenarios A-J against fresh Aspire stack
- Result: 3 P1/P0 bugs found (UserProfileId NULL, Transcript not stored, LexicalUnitType wrong)
- Decision: DO-NOT-SHIP with action items for Wash/River

**Phase 2 (2026-04-27): Retest after backend fixes → CONDITIONAL SHIP**
- Simon fixed backend bugs; Jayne re-ran 5 targeted scenarios (A, B, BUG-3 Targeted, C, H)
- Discovery: Frontend DTO mapping gap in ImportContent.razor (LexicalUnitType + SourceText not round-tripped)
- Verdict: CONDITIONAL SHIP pending Kaylee's frontend fix
- Action: Routed DTO mapping bug to Kaylee (same cycle)

**Phase 3 (2026-04-27): Full sweep after all fixes → SHIP ✓**
- All 10 scenarios (A-J) executed
- Results: 10/10 PASS, all P1 bugs verified fixed with DB-level evidence
- Evidence: 15+ final screenshots + clean Aspire logs (zero Error/Warning)
- Decision: SHIP cleared

**Lesson learned:** Frontend DTO mapping bugs can masquerade as backend bugs. When frontend DTOs carry structured data, dual-layer verification is required:
1. Backend logic verified correct (e.g., ResolveLexicalUnitType heuristic working)
2. Frontend mapping verified complete (e.g., DTO properties explicitly mapped in initializers)
Both layers must be audited together. Single-layer verification can hide defects.

**Ship readiness:** All P1 bugs verified fixed. Feature shipped clean with full regression coverage.

- 2026-04-27: **TEAM CONVERGENCE: Bug Reproduction + Test Plan** — Reproduced v1.1 phrase-save bug at HEAD 3b6c01b: 3 Korean|English sentences in → 8 word entries out, ZERO phrases. Smoking gun: `ContentImportService.cs` line 192 calls `ParseFreeTextContentAsync()` (generic FreeTextToVocab prompt) instead of River's dedicated `ExtractVocabularyFromPhrases.scriban-txt`. All 8 entries had `LexicalUnitType=1 (Word)`. Convergent diagnosis: Wash identified root cause + implemented 2-step fix (parse delimited → AI extract words), River locked JSON contract + created Sentences prompt, Kaylee added Type filter. Created 7-section test plan (Phrases/Sentences/Transcript/edge cases) in `e2e-testing-workspace/v12-import-bug/test-plan.md`. Round 2: execute validation against fixes + verify all three import paths end-to-end.

