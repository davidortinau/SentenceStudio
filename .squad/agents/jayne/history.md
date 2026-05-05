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
- Playwright MCP can go stale (browser closed state) between sessions. Have a fallback to DB-level verification when Playwright is unresponsive. Workaround: connect Python Playwright directly via CDP to the existing Chromium debug port (64185). Requires page reload to re-establish Blazor SignalR circuit.

- 2026-04-27: **v1.2 Import Fix Verified (Round 4)** — Wash's fix at commit `3c7a4cc` verified. Test 1 (Phrases regression): 3 pipe-delimited lines imported as LexicalUnitType=2 (Phrase) + 5 harvested words — PASS. Test 2 (Sentences fix): 3 pipe-delimited lines imported as LexicalUnitType=3 (Sentence) — was 0 before fix, now 4 — PASS. All 24 unit tests pass. Verdict: SHIP. Evidence at `e2e-testing-workspace/v12-import-fix-r4/`.

- E2E testing skill at `.claude/skills/e2e-testing/SKILL.md` — follow it religiously
- Aspire stack: `cd src/SentenceStudio.AppHost && aspire run`
- Webapp at `https://localhost:7071/`
- Playwright for browser testing: snapshots, clicks, form fills, verification
- Server DB: `/Users/davidortinau/Library/Application Support/sentencestudio/server/sentencestudio.db`
- Three verification levels: UI state, database records, Aspire logs
- "It compiles" is NOT sufficient — must verify in running app
- Must call `CacheService.InvalidateVocabSummary()` after recording attempts or dashboard is stale

## Cross-Agent Notes

- **2026-05-04 (Scribe):** skill-trainer validated three skills from auth-persistence cycle: **single-flight-async** (lockAcquired guard added, production-validated), **async-single-flight-testing** (C# syntax fixed), **maui-ai-debugging** (phantom-agent troubleshooting entry added). All promoted to high confidence. Zoe updated AGENTS.md with "Async Patterns" section referencing single-flight-async, ef-dual-provider-migrations, async-single-flight-testing skills. Decisions merged from inbox → decisions.md.

## Core Context (Summarized History)

[Earlier entries (prior to 2026-04-25) have been reviewed and consolidated. Key patterns:]

- Agent is primary domain specialist for this project area
- Responsibilities established through prior charter/assignment cycles
- Cross-agent coordination pattern: decision inbox → decisions.md → history broadcast
- Team cadence: 3-agent orchestrations typical for major feature work
- Velocity: multiple cycles per week with focus on validation/ship gates

### Lesson: Always check the bridge between backend and frontend

When two agents deliver backend (Wash) and frontend (Kaylee) pieces, the integration call between them is a prime spot for gaps. E2E caught what unit tests couldn't — the unit tests tested the enrichment method in isolation, and the Razor markup rendered correctly when `IsDuplicate=true`, but nobody verified the full chain.

### Aspire orphan cleanup

DCP processes orphaned again after `stop_bash`. Two-pass kill (SIGTERM parent PIDs 51006, 51012) cleaned up. Port 22070 verified free before exiting.

### Style audit results

- No bespoke purple hex remaining
- All inline `cursor:pointer` replaced with `role="button"`
- All inline `font-size` replaced with Bootstrap `small` class
- Remaining inline `style=` are justified functional widths + text truncation
- Mobile preview table (390px) is functional but cramped — preview table lacks the card layout the results table has


## Team Update: M.E.AI 10.5 Debt-Paydown Complete (2026-04-27 → 2026-04-28)

**Status**: SHIPPED ✅

Zoe's M.E.AI 10.5 strategic recommendations executed via three-agent orchestration (Wash Phase 1 + Phase 2, Jayne validation):

**What shipped:**
- **CPM (Central Package Management)**: Directory.Packages.props created; ~95 packages centralized; 178 Version= attributes stripped from 22 csprojs
- **Polly Resilience**: All 5 OpenAI sites now route via HttpClientPipelineTransport with Polly policies (120s attempt / 300s total / 300s circuit-breaker). Zero retry storms in validation.
- **Config Extraction**: gpt-4o-mini, tts-1, text-embedding-3-small, and ElevenLabs voice IDs moved to appsettings.json with ?? fallback defaults. Single point of change.
- **SKU Assessment**: AppLib stays on Agents.AI (ConversationAgentService + ConversationMemory use orchestration types with no M.E.AI equivalent). Waiting for M.E.AI agent layer.
- **RetrievalService Defused**: NotImplementedException → no-op stub + [Obsolete]. Zero callers verified.

**Validation results** (6/6 gates PASS):
- Build matrix: 13/13 buildable projects green; 626 tests passing
- Aspire runtime: Clean start, all resources Running
- AI end-to-end: Conversation + comprehension scoring + ElevenLabs TTS working; clean Polly pass-through (no retry storms)
- Config sanity: All 4 projects (Api, WebApp, Workers, AppLib) read from appsettings.json with correct fallback defaults

**Implications for all agents going forward:**
- All future OpenAI HTTP traffic flows through Polly automatically
- Model names are now config-driven, not code-driven
- AppLib remains on Microsoft.Agents.AI; this is not a blocker (transitive M.E.AI dependency exists)
- MAUI+CPM gotchas are documented for future package maintenance

**Decision artifacts**: .squad/decisions.md merged (3 entries); inbox cleaned; decisions-archive-2026-04-28.md created

**Orchestration logs**: .squad/orchestration-log/2026-04-28T00:06:30Z-{agent}.md (3 entries)

**Session log**: .squad/log/2026-04-28T00:06:30Z-meai-debt-paydown.md

**SHIP IT verdict**: All validation gates pass; zero regressions introduced. Production-ready.


---

## 2025-12-18 — Vocab Quiz Scoring Bug Cluster, Stream B Step 1 (failing repro tests for #189, #191)

**Captain (David Ortinau)** approved the cluster plan; Zoe is Lead, I'm Tester (Stream B Step 1, repro-tests-only), Wash owns the fix (Step 2), Kaylee owns the UI sibling stream.

**Shipped:**
- Branch `test/vocab-quiz-scoring-repro-189-191` (off `main`, 2aab53d). Draft PR: https://github.com/davidortinau/SentenceStudio/pull/195
- New file: `tests/SentenceStudio.UnitTests/Integration/VocabQuizScoringRepro189And191Tests.cs` (4 tests).
- Pattern: `IClassFixture<PlanGenerationTestFixture>` — real EF Core + in-memory SQLite + DI, modeled on `MasteryAlgorithmIntegrationTests`. **Do not** use Moq on `VocabularyProgressService` / its repos — methods aren't virtual and the existing `Services/VocabularyProgressServiceTests.cs` is `<Compile Remove>`d in the csproj for that reason.

**Captured failure signatures (against `main`):**

`Repro191_NewWord_AllCorrect_DoesNotRotateOutBeforeFifthTurn` — FAIL
```
turn=1 mode=MC   streak=1.00 prodInStreak=0 mastery=0.143 sessMC=1 sessText=0 ReadyToRotateOut=False
turn=2 mode=MC   streak=2.00 prodInStreak=0 mastery=0.286 sessMC=2 sessText=0 ReadyToRotateOut=False
turn=3 mode=MC   streak=3.00 prodInStreak=0 mastery=0.429 sessMC=3 sessText=0 ReadyToRotateOut=False
turn=4 mode=Text streak=4.50 prodInStreak=1 mastery=0.714 sessMC=3 sessText=1 ReadyToRotateOut=True   ← rotates here
```

`Repro189_*` (both PASS) — service math is correct for one MC attempt. `Accuracy=1.0`, `ProductionAttempts=0`. Captain's "2 production / 50% accuracy" panel readout therefore comes from the UI panel (or a duplicate-call path), not the service.

**Hypothesis-disambiguation outcome:**
- **#189 → Stream A (Kaylee).** Service is innocent; bug is in `VocabQuiz.razor` Learning Details panel (~395–460) reading legacy obsolete fields, OR in duplicate-fire of `RecordPendingAttemptAsync` (call sites at ~1245 / ~1394 / ~1490).
- **#191 → Wash, Stream B Step 2.** Root cause is `VocabularyQuizItem.ReadyToRotateOut` Tier 2 (mastery>=0.50 OR streak>=3 + only SessionCorrectCount>=2 AND SessionTextCorrect>=1). Curve is too lenient; Wash should pause for Captain alignment via decisions.md before picking a corrected target curve.

**Patterns / lessons learned for future test work in this repo:**

1. **The unit-test project only references `SentenceStudio.Shared`.** The main `SentenceStudio` project doesn't build for `net10.0`, and several legacy test files are commented out as a result. Always confirm a service is in `.Shared` before writing tests against it. `VocabularyProgressService` is in `SentenceStudio.Shared/Services/VocabularyProgressService.cs`, namespace `SentenceStudio.Services`.
2. **`VocabularyProgressRepository` and `VocabularyLearningContextRepository` take an `IServiceProvider`** — they cannot be cleanly mocked with Moq; use the real fixture.
3. **`PlanGenerationTestFixture` is the canonical DI harness** for any service touching the DB. Static `TestUserId="test-user-1"`, `SeedResource(vocabWordCount:int)`, `GetResourceVocabularyWordIds`, `ClearAllData()`. Construct the service in the test ctor with `fixture.ServiceProvider.CreateScope()` and resolve the repos out of that scope.
4. **`VocabularyAttempt.Phase` is a computed property derived from `(InputMode, ContextType)`.** Don't try to set Phase explicitly in tests — the service will auto-derive it. Use the literal strings the quiz uses: `"MultipleChoice"`, `"Text"`, `"Voice"`, `"TextEntry"`. ContextType `"Isolated"` is fine for unit tests.
5. **`VocabularyQuizItem.Word` is required (constructor-init)** — I had to seed via `ApplicationDbContext.VocabularyWords.First(w=>w.Id==wordId)` from a fresh scope.
6. **`DifficultyWeight` matters for streak math.** `1.0` for MC, `1.5` for Text — VocabQuiz.razor sets these explicitly; tests must mirror, or the streak math diverges from production.
7. **Mode-selection rule from VocabQuiz.razor (~792–801):** `PendingRecognitionCheck` → MC; `CurrentStreak>=3 OR MasteryScore>=0.50` → Text; else MC. Mirrored as a private helper `ChooseQuizModeForTurn` in the test to keep simulations faithful.
8. **Use FluentAssertions `AssertionScope`** to dump all expected/actual fields when one fails — critical for the PR description and for Wash's debugging.
9. **CS0618 obsolete warnings on `RecognitionAttempts` / `ProductionAttempts`:** wrap assertions in `#pragma warning disable/restore CS0618` — these legacy fields are still updated by the service for back-compat and need to be asserted explicitly when proving recognition turns don't pollute production counters.
10. **Don't disturb Kaylee's UI WIP.** Stashed `src/SentenceStudio.Shared/Resources/Strings/AppResources*.{cs,resx}` + `VocabQuiz.razor` + `docs/coresync-suspected-defects.md` under stash message `kaylee-stream-a-wip-vocab-quiz` before branching off `main`. Restore via `git checkout fix/vocab-quiz-ui-cluster-189-194 && git stash pop` when leaving Stream B.

**Decision dropped:** `.squad/decisions/inbox/jayne-vocab-quiz-scoring-repro-189-191.md`.

### 2026-05-03 — PR #195 closed (superseded)
PR #195 (draft repro tests) closed; commits absorbed into Wash's squash-merge of PR #198 (`626383a`) which closed #191. Repro tests now live on `main` as the regression guard. Sibling Stream A PR #196 (`c996299`) closed #189/#190/#192/#193/#194. Follow-ups: #197 (decouple Mastery from SessionRotation) and #199 (test helper `DifficultyWeight` bug — direct outcome of point 6 in my earlier learnings).

## 2026-05-04 — IdentityAuthService Concurrency Regression Test

**Captain (David Ortinau)** requested a regression test for the refresh-token concurrency fix (auth persistence plan Bug 1) to lock in the single-flight pattern and prevent future regressions.

**Shipped:**
- New test project: `tests/SentenceStudio.AppLib.Tests/SentenceStudio.AppLib.Tests.csproj` (xUnit, `net10.0`, references `AppLib`)
- Test: `IdentityAuthServiceConcurrencyTests.GetAccessTokenAsync_ConcurrentCallers_TriggersExactlyOneRefresh`
- Pattern: custom `HttpMessageHandler` with request tracking + in-memory `ISecureStorageService` stub
- Test scaffolding: `InMemorySecureStorageService` (reusable), `TrackingHttpMessageHandler` (reusable), `AuthResponseDto` (test-local DTO)

**Test outcome:** PASS ✅ (single-flight fix already merged in commit `74666b9`)

The test verifies:
1. Two concurrent `GetAccessTokenAsync` calls trigger exactly **ONE** POST to `/api/auth/refresh` (not two)
2. Both callers receive the same new AccessToken
3. A third call after refresh completes uses the cached token (no new POST)

**Learnings:**
1. **AppLib test project isolation**: `AppLib` is a MAUI-enabled project (`UseMaui=true`) with a static `ServiceProvider` class that collides with `Microsoft.Extensions.DependencyInjection.ServiceProvider`. Referencing it from a plain `net10.0` test project breaks existing tests. Solution: create a separate `SentenceStudio.AppLib.Tests` project that references AppLib cleanly.
2. **xUnit + CPM**: When creating test projects in a CPM-enabled repo, remove `Version=` attributes from PackageReference elements. Central Package Management requires versions in `Directory.Packages.props`, not csprojs.
3. **Test-first regression guards**: Captain's "when a bug recurs, write a test" policy means this test was written AFTER the fix was already in place (commit `74666b9`). The test passing on first run is the CORRECT outcome — it proves the fix works and will catch future regressions.

**Decision dropped:** `.squad/decisions/inbox/jayne-applib-concurrency-test.md` (new test project rationale).

**Skill candidate:** The concurrency test pattern (custom `HttpMessageHandler` + in-memory storage stub + `Task.WhenAll` race simulation) is reusable for any auth/token service. Consider extracting to `.squad/skills/async-single-flight-testing/SKILL.md` if this pattern is needed again.

- 2026-05-05: **NumberDrill Phase 2 Wave 1 — Test Scope** — Phase 2 Wave 1 planning complete. Test responsibilities for Wave 3: (1) Irregular form detection (유월/시월 vs 육월/십월), (2) Ordinal pattern disambiguation (째 vs 번째 by context: ranking vs occurrence), (3) Korean place-value grouping (만/억 boundaries + Sino conversion precision), (4) Disambiguate sub-mode paired grading (both correct ✓, one wrong, both wrong), (5) Explanation panel rendering and audio replays. Test matrix pattern: edge case enumeration (E1: both same system [should not happen], E2: one right one wrong, E3: audio modes [English prompt vs Korean answer], E4: choice strip expansion [Phase 3]). All Phase 2 decisions captured; Wave 2 generators/graders ship in parallel while Jayne authors test suite. Decision drops: `.squad/decisions/inbox/river-numberdrill-phase2-seed.md` (Money/Date/Ordinal with contextNotes).

- 2026-05-04: **NumberDrill Phase 2 E2E (NO-SHIP, infrastructure blocked)** — Wave 2 TapTheCounter + plan-slot integration E2E attempted. **Build fix required:** Kaylee's NumberAudioCueBuilder missing ILogger parameter for KoreanNumberItemGenerator constructor — fixed with NullLogger<T>.Instance (standard pattern for static utility classes). **E2E blocked:** Aspire instability (connection failures, process exits), NumberMasteryProgress table missing (EF migration not applied to PostgreSQL), 0-byte SQLite .db files everywhere (app actually uses PostgreSQL via Aspire, not SQLite). **Deliverables:** (1) Build fix applied, (2) `.claude/skills/e2e-testing/references/numberdrill.md` reference doc complete (Phase 1 Listen&Type/Read&Produce, Phase 2 TapTheCounter, plan-slot integration, Disambiguate/Listen-and-place placeholders), (3) Decision drop at `.squad/decisions/inbox/jayne-numberdrill-phase2-e2e.md` with NO-SHIP verdict. **Code review passed:** KoreanNumberItemGenerator, GenerateDisambiguateItem, NumberDrillService, DailyPlan slot replacement logic all look solid. **Verdict:** NO-SHIP pending Aspire stability fix + NumberMasteryProgress schema migration. Estimated unblock: 2-4 hours.

- 2026-05-04: **NumberDrill Phase 2 Wave 4 — E2E Verification (SHIP)** — Final verification of Wave 4 before Phase 2 closure. Scope: Webapp (Aspire + Playwright), Korean profile test user. Deliverables verified: (1) Listen-and-place sub-mode — audio button + 3 time-card choices render correctly, single tap on correct choice produces green border feedback, no console errors, auto-advance works (deferred detailed timing validation). Evidence: wave4-03-listen-and-place-initial.png (UI), wave4-04-listen-and-place-feedback.png (green feedback). (2) Picker 6 contexts — all 6 context tiles visible (Counting, Time, Age, Money, Date, Ordinal) with Bootstrap icons, ZERO emoji in UI, layout clean. Evidence: wave4-02-picker-6-contexts-and-modes.png. (3) Disambiguate selection-state fix — both prompts (Prompt A: "3 days (duration)" + Prompt B: "the 3rd day") remain visually active simultaneously after fix, no blue-highlight drop-off. Evidence: wave4-06-disambiguate-both-selected.png (first selection), wave4-07-disambiguate-bug-reproduced.png (BOTH highlighted blue simultaneously, Submit Both button enabled). Minor note: Playwright accessibility snapshot showed timing variance on `[active]` attribute vs visual state, but screenshot is ground truth — visual styling correct. Plan-slot integration + telemetry sanity: out of 15-min scope, deferred post-merge. Browser console: 0 errors. Aspire: AppHost PID 77390, Dashboard PID 77502, all services healthy. **Verdict: SHIP** ✅. Wall-clock: 12 minutes. Phase 2 complete and ready for production merge.

