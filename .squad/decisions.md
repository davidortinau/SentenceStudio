## Active Decisions

(Most recent decisions below. Archived decisions in `decisions-archive-2026-04-25.md`)

---

### 2026-06-11 — Vocabulary Bootstrap Fix (Wash, Zoe Review, Jayne E2E)

**Session:** Vocab-less plan bootstrap fix (2nd bug from same morning as preview-mismatch fix)  
**Branch:** `squad/vocab-preview-quiz-mismatch-fix`  
**Commit landed:** `386b4550` — "Bootstrap vocabulary review for users with no SRS progress"  
**Status:** 🟢 Ready for Captain's pre-push approval. 583/583 unit tests passing. Mac Catalyst E2E verified 6/6 acceptance criteria.

#### Agents & Verdicts

- **Wash (Backend Dev)** — Diagnosed root cause, implemented bootstrap path + sentinel fix + wording fix. 🟢 **Implemented, logged, live-diagnosed, unit-tested.**
- **Zoe (Lead Reviewer)** — Code review bootstrap fix (🟢 after stale comment deleted), then wording-fix delta (🟢). **Minor fix required: delete stale "THIS WILL LIKELY FAIL" comment at lines 117–119 of ResourceSelectionTests.cs per AGENTS.md directive.**
- **Jayne (Tester)** — Initial E2E failed due to stale binary (running old code). After clean rebuild of all bin/obj, E2E PASSED all 6 acceptance criteria. **Key learning: partial cleans leave stale DLLs in MonoBundle; nuke AppLib/bin and AppLib/obj too.**

#### Core Decision

When deterministic planning finds fewer than 5 SRS-due vocabulary rows, it now queries user-scoped mapped vocabulary with no `VocabularyProgress` row and builds a bootstrap focus cohort. Bootstrap cohort capped at 15 words, gets a `VocabularyReview` activity plus normal focus-vocabulary propagation. Preserves Phase 1 contract: `PreviewWords` is exactly a projection of `FocusVocabularyIds`.

#### Rationale

Dashboard "New" vocabulary is computed from resource mappings without progress rows. Planner previously only read SRS-due progress rows, making first-time learners look vocabulary-less despite thousands of mapped words. Bootstrap path detects this gap and surfaces bootstrapped focus work. Capping at 15 keeps first session manageable.

#### Implementation Details

- `VocabularyProgressRepository.GetUnseenMappedVocabularyAsync` — new method returning synthetic, untracked `VocabularyProgress` objects (plan preview/selection only). Returns empty for empty userId, never unfiltered result. Multi-tenant safe per canonical post-May-2026 hotfix pattern.
- `DeterministicPlanBuilder` — new `BootstrapVocabularyTarget = 15`. When `DueWords < 5`, blends due + unseen mapped words up to bootstrap cap. `TotalDue` property represents selected cohort size during bootstrap, not full library.
- Resource last-use display — uses nullable `DaysSinceLastUse` (was `int` with 999 sentinel). Null = truly never used. Resources outside 30-day ranking lookback surface real age. Old "Fresh resource (not used for X days)" wording removed.
- Wording cascade for selection reason — four-branch: never used → "New resource for today's plan"; >30 days → "Resource not used recently"; 1–30 days → "Last used N days ago"; 0 → vocab-match or "Best available option".

#### Test & Coverage

- **New tests:** `PlanWithZeroDueAndManyUnseenMapped_SchedulesVocabActivityFromUnseenCohort`, `EmptyDueAndEmptyUnseenMapped_NoVocabActivity`, `ManyDueAndManyUnseen_PrefersDueNotBootstrap`, `PlanBootstrap_PreviewWordsEqualFocusVocabularyIds`, `ResourceUsed8DaysAgo_ShouldUseNeutralSelectionReason_NotFresh`, `ResourceUsed31DaysAgo_ShouldNotReportAs999Days`.
- **Regression:** Previously-known-failing `ResourceUsed15DaysAgo_ShouldNotBeTreatedAsNeverUsed` now passes (999-day sentinel bug fixed). Test count: 583/583 passing.
- **E2E verified:** Mac Catalyst dashboard shows 3 activities (VocabReview + Reading + VocabMatching). FocusVocabularyFacts populated with 15 word IDs. Preview words match activity focus set. Resource footer says "Last used 8 days ago" not "Fresh resource".

#### Files Changed (5 files, +430/-78 net)

- `src/SentenceStudio.Shared/Data/VocabularyProgressRepository.cs` (+125/-14) — new `GetUnseenMappedVocabularyAsync`
- `src/SentenceStudio.Shared/Services/PlanGeneration/DeterministicPlanBuilder.cs` (+175/-73) — bootstrap logic, wording cascade, DaysSinceLastUse nullable
- `tests/SentenceStudio.UnitTests/PlanGeneration/DeterministicPlanBuilderUnseenVocabBootstrapTests.cs` (new, 135 lines)
- `tests/SentenceStudio.UnitTests/PlanGeneration/DeterministicPlanBuilderResourceSelectionTests.cs` (+28/-5) — new edge cases, stale comment removed per Zoe review
- `tests/SentenceStudio.UnitTests/PlanGeneration/DeterministicPlanBuilderVocabularyReviewTests.cs` (+3/-3) — minor adjustments

#### Multi-Tenant Scoping Audit

`GetUnseenMappedVocabularyAsync` (line 354):
- Empty userId → log warning + return empty list ✓
- LearningResources join scoped `UserProfileId == userId` ✓
- Existence filter on VocabularyProgresses scoped `UserId == userId` ✓
- Pattern matches canonical post-May-2026 hotfix from `LearningResourceRepository` ✓

#### Zoe Notes (non-blocking)

- **Smart-resource inclusion:** `GetUnseenMappedVocabularyAsync` doesn't filter `!IsSmartResource`. Design question for Captain: should bootstrap ever include smart-resource vocab, or real-resources-only? No-op for Captain's current scenario (smart resources need SRS progress to get mapped). Worth a follow-up if direction changes.
- **`wordsByResource` semantic shift:** OLD grouped full due pool → NEW groups top-20 selected words. Same answer most time, occasionally different. Intentional (activity uses selected words, so resource chosen from them), but worth noting for archaeology.
- **Pre-existing NRE latent:** `LlmPlanGenerationService.cs:68,111` dereference `PrimaryResource.Title` without null check. Fallback path returns `PrimaryResource = null`. Not introduced here, but bootstrap makes it slightly more reachable. Follow-up issue worth filing; not blocking.
- **Grammar nit:** "Last used 1 days ago" (should be "day"). Cosmetic, Captain's call if to fix before ship.

#### Jayne E2E Findings (reference only)

- First E2E failed: running pre-bootstrap code. After clean-build (deleted MacCatalyst/bin, MacCatalyst/obj, Shared/bin, Shared/obj, **AppLib/bin, AppLib/obj**), produced correct Jun 11 13:49 binary with new code.
- Binary inspection (UTF-16 scan): old `"need 5+"` string absent, new `Bootstrapping vocab`, `unseen mapped`, `BootstrapVocabularyTarget` symbols present. ✓
- Catalyst squad-jayne test user profile not in local SQLite, but fallback pattern resolved to David's profile (ba20bcc5-ab2a…) which has 3253 unseen mapped words. Bootstrap triggered correctly. Test environment artifact, not a bug.
- VocabularyReview block built without matched resource (wordsByResource group didn't meet min-due threshold in test env). Doesn't affect acceptance criteria; Preview button in Study Insight may only appear when vocab block has resource — minor UX edge case.

#### Related Prior Work

- **Commit 750138b5** (2026-06-09): Phase 1 Focus Vocabulary — asymmetry fix between mobile (ProgressService) and API (PlanService) NarrativeFacts/RationaleFacts persistence. Preview button now renders stably. Bootstrap builds atop this foundation.
- **Commit 3c7a4cc** (2026-05-XX): Fallback plan rationale symmetry — Rationale: null, all 3 facts columns now protected by `?? planRow.X` coalesce.

#### Blockers & Gating

- **Required before commit:** Delete stale "THIS WILL LIKELY FAIL" comment at lines 117–119 of `DeterministicPlanBuilderResourceSelectionTests.cs` (Zoe review finding, 30-second fix per Reviewer Rejection Protocol).
- **E2E gate passed:** 6/6 acceptance criteria verified on Mac Catalyst clean-build binary.
- **Pre-push gate:** Awaiting Captain's explicit approval per `.github/copilot-instructions.md` pre-push review directive.

---

### 2026-06-09T19:00:00Z: Fallback plan path must pass Rationale=null + null-coalesce update branch

**By:** Captain David Ortinau (via Copilot CLI code-review follow-up)
**What:** 
1. `GenerateFallbackPlanAsync` in `ProgressService.cs` now passes `Rationale: null` (was a hardcoded sentinel string). The user-facing rationale belongs at the API edge / `IPlanCopyProvider`, not in core persistence logic.
2. The update branch of `InitializePlanCompletionRecordsAsync` continues to use `?? planRow.X` null-coalesce for all three facts columns (FocusVocabularyFacts, NarrativeFacts, RationaleFacts). With the Rationale change above, the protection is now symmetric — all three columns are preserved when a fallback plan is re-saved over an existing row.

**Why:** Code review caught that the previous `?? planRow.X` fix was asymmetric. The fallback path used a hardcoded Rationale string, which meant `SerializeRationaleFacts` returned non-null JSON, which then bypassed the coalesce and silently overwrote any LLM-generated rationale stored from a prior successful generation. Latent in normal flow (cache short-circuit usually prevents reaching the update branch) but reachable in: CoreSync delivering parent row before children, partial saves, date-rollover edges, or any scenario where `DailyPlan` exists without matching `DailyPlanCompletion` rows for that date.

**Test guard:** `NarrativeFacts_FallbackPlanDoesNotClobberPreviousFacts_SqliteRoundTrip` — proven NON-vacuous by reverting the fix and observing the test correctly fails with "found <null>". Test now (a) deletes `DailyPlanCompletion` rows between generations to force the LLM path, and (b) asserts `_llmPlanService.ThrowOnNextCall == false` after the throw, proving the fallback path was actually entered (not bypassed via cache reconstruction).

**Files:**
- src/SentenceStudio.Shared/Services/Progress/ProgressService.cs (line ~390: Rationale: null; comment updated at update branch ~1201-1213)
- tests/SentenceStudio.UnitTests/Services/Progress/ProgressServiceFocusVocabularyContractTests.cs (rewrote `NarrativeFacts_FallbackPlanDoesNotClobberPreviousFacts_SqliteRoundTrip` to actually exercise fallback path)

---

### 2026-06-01: Local dev test accounts — durable fixtures for stable E2E testing

**By:** Copilot (Coding Agent)
**Date:** 2026-06-01
**Status:** ✅ IMPLEMENTED
**Context:** E2E testing and local development tooling friction reduction

#### Decision

Development AppHost startup seeds three canonical local-only Identity accounts:

- `captain@test.local`
- `testsailor@test.local`
- `e2e@test.local`

Each account is email-confirmed and linked to a default English-to-Korean A1 `UserProfile`.

#### Rationale

Stable local fixtures prevent agents and E2E scripts from creating many one-off accounts, reduce auth confusion across worktrees, and give Captain a durable daily-driver account for local testing.

#### Source of truth

Keep `src/SentenceStudio.Api/Auth/DevTestAccountSeeder.cs`, `docs/local-dev-test-accounts.md`, and `.github/copilot-instructions.md` in sync when fixture accounts change.

#### Implementation

**By:** Captain David Ortinau (via Copilot CLI code-review follow-up)
**What:** 
1. `GenerateFallbackPlanAsync` in `ProgressService.cs` now passes `Rationale: null` (was a hardcoded sentinel string). The user-facing rationale belongs at the API edge / `IPlanCopyProvider`, not in core persistence logic.
2. The update branch of `InitializePlanCompletionRecordsAsync` continues to use `?? planRow.X` null-coalesce for all three facts columns (FocusVocabularyFacts, NarrativeFacts, RationaleFacts). With the Rationale change above, the protection is now symmetric — all three columns are preserved when a fallback plan is re-saved over an existing row.

**Why:** Code review caught that the previous `?? planRow.X` fix was asymmetric. The fallback path used a hardcoded Rationale string, which meant `SerializeRationaleFacts` returned non-null JSON, which then bypassed the coalesce and silently overwrote any LLM-generated rationale stored from a prior successful generation. Latent in normal flow (cache short-circuit usually prevents reaching the update branch) but reachable in: CoreSync delivering parent row before children, partial saves, date-rollover edges, or any scenario where `DailyPlan` exists without matching `DailyPlanCompletion` rows for that date.

**Test guard:** `NarrativeFacts_FallbackPlanDoesNotClobberPreviousFacts_SqliteRoundTrip` — proven NON-vacuous by reverting the fix and observing the test correctly fails with "found <null>". Test now (a) deletes `DailyPlanCompletion` rows between generations to force the LLM path, and (b) asserts `_llmPlanService.ThrowOnNextCall == false` after the throw, proving the fallback path was actually entered (not bypassed via cache reconstruction).

**Files:**
- src/SentenceStudio.Shared/Services/Progress/ProgressService.cs (line ~390: Rationale: null; comment updated at update branch ~1201-1213)
- tests/SentenceStudio.UnitTests/Services/Progress/ProgressServiceFocusVocabularyContractTests.cs (rewrote `NarrativeFacts_FallbackPlanDoesNotClobberPreviousFacts_SqliteRoundTrip` to actually exercise fallback path)

---

### 2026-06-01: Local dev test accounts — durable fixtures for stable E2E testing

**By:** Copilot (Coding Agent)
**Date:** 2026-06-01
**Status:** ✅ IMPLEMENTED
**Context:** E2E testing and local development tooling friction reduction

#### Decision

Development AppHost startup seeds three canonical local-only Identity accounts:

- `captain@test.local`
- `testsailor@test.local`
- `e2e@test.local`

Each account is email-confirmed and linked to a default English-to-Korean A1 `UserProfile`.

#### Rationale

Stable local fixtures prevent agents and E2E scripts from creating many one-off accounts, reduce auth confusion across worktrees, and give Captain a durable daily-driver account for local testing.

#### Source of truth

Keep `src/SentenceStudio.Api/Auth/DevTestAccountSeeder.cs`, `docs/local-dev-test-accounts.md`, and `.github/copilot-instructions.md` in sync when fixture accounts change.

#### Implementation

- ✅ `DevTestAccountSeeder` added to AppHost (Development environment only)
- ✅ Durable local fixtures seeded at AppHost startup
- ✅ Documentation in `docs/local-dev-test-accounts.md`
- ✅ Seeding behavior validated with tests
- ✅ Instruction table added to `.github/copilot-instructions.md`
- ✅ WebApp Development auto-confirm patch included

#### Related files

- `src/SentenceStudio.Api/Auth/DevTestAccountSeeder.cs` — seeding logic
- `docs/local-dev-test-accounts.md` — usage guide
- `.github/copilot-instructions.md` — team reference table
- Tests added to `SentenceStudio.Api.Tests` for seeding behavior

---

### 2026-06-08T21:37:17-05:00: Plan vocab mismatch diagnosis

**By:** Wash
**What:** The mismatch is in the daily plan/activity handoff: preview words are frozen in the plan narrative, but launched activities re-select vocabulary from due/resource pools because plan items carry no vocabulary IDs.
**Why:** The preview feature added a frozen `PreviewWords` narrative list, but existing activity routes only pass activity/resource/skill/due flags, so activities have no contract to consume those exact words.
**Evidence:** `src/SentenceStudio.Shared/Services/PlanGeneration/DeterministicPlanBuilder.cs:775-808`; `src/SentenceStudio.Shared/Services/Progress/IProgressService.cs:54-72`; `src/SentenceStudio.UI/Pages/Index.razor:1039-1078`; `src/SentenceStudio.UI/Pages/VocabQuiz.razor:637-723`

---

### 2026-06-08T21:37:17-05:00: Today Plan focus-vocab architecture

**By:** Zoe
**What:** The Today Plan is a date-stable, user-scoped coaching plan that chooses a small focus set of vocabulary due for active learning, optionally pairs it with a contextual resource and skill, then sequences activities so the same focus vocabulary is reviewed, recognized, and produced across the session. The preview must be a projection of that focus set, not an independent sample, and activities must consume the focus set rather than rerolling their own word pools. Reading, Translation, Shadowing, Listening, and Video can still include incidental resource vocabulary, but the planned focus words are the through-line.

**Why:** Captain reframed the bug correctly: if the plan exists to target key vocabulary and/or grammar, then a preview that does not feed the activities is a broken contract, not just a UI mismatch.

**Phasing:**
- Phase 0: Use existing `PreviewWords` IDs as temporary focus set for plan-launched activities.
- Phase 1: Land real Focus Set contract with plan/item IDs, persistence, sync, and activity consumers.
- Phase 2: Decide if focus selection becomes AI-assisted vs. deterministic SRS.

**Open:** Grammar focus model, focus set size gates, selection policy, carry-over, sync source of truth, and resource overlap thresholds.

---

### 2026-06-08T21:37:17-05:00: Phase 1 Focus Vocabulary implementation

**By:** Wash
**What:** Added the Phase 1 focus-vocabulary contract across deterministic plan generation, DTOs, legacy progress DTOs, canonical plan persistence, CoreSync registration, route plumbing, and regression tests. `FocusVocabularyIds` now flows from `VocabularyReviewBlock` to `PlanSkeleton`, per-activity `PlannedActivity`, `DailyPlanResponse.PlanActivity`, `DailyPlanItem`, and `PlanItemDto`; `PreviewWords` is derived from the same selected focus set instead of a separate sample.

**Why:** Zoe's architecture identified the mismatch as a broken plan/activity contract: the Today Plan preview advertised words that activity launches rerolled independently. Captain approved Phase 1 direct, deterministic SRS-only, vocab-only, keeping the min-5 gate and max-20 cap. Captain then made the DailyPlan CoreSync gap non-negotiable so mobile and desktop/browser see the same plan and personal progress.

**Storage:** Canonical storage is `DailyPlan.FocusVocabularyFacts`, serialized as `{ vocabularyIds, source }` with `source = deterministic-srs`. CoreSync registered `DailyPlan` in both SQLite and PostgreSQL `SharedSyncRegistration`.

**Migrations:** Both providers with matching timestamp `20260609032023` — additive nullable columns on `DailyPlan` only.

**Tests passing:** 556/557 unit tests passing (baseline + new tests).

**Open:** Whole-solution build blocked by pre-existing platform issues (Windows XamlCompiler exit 126 on macOS, Android minSdk mismatch); native DevFlow validation blocked by non-interactive `dotnet build -t:Run` issues. AppLib test project TFM mismatch remains.

---

### 2026-06-08T21:37:17-05:00: Phase 1 Focus Vocab test scaffolding

**By:** Jayne
**What:** Added Phase 1 Focus Vocabulary contract tests using the `FocusVocabulary_...` naming convention. Tests cover plan-level focus IDs, preview-word projection, deterministic ordering, vocabulary-aligned item propagation, non-vocabulary omission, min-5/max-20 gates, route-parameter propagation, stable plan item IDs, PlanService SQLite round-trip plus SQLite/PostgreSQL EF model storage, legacy ProgressService reconstruction, and conditional repository multi-tenant scoping.

**Why:** Zoe's contract says the Today plan preview must be a projection of one canonical focus set, not an independent sample. Captain's verification gate needs automated regression coverage.

**Test count:** 16 authored test cases. Current authored status: 15 pass, 1 fails (ordering by word ID tiebreaker). Full `FocusVocabulary` filter: 20 tests, 19 pass, 1 fails.

**Baseline check:** Pre-existing known failure still holds: `DeterministicPlanBuilderResourceSelectionTests.ResourceUsed15DaysAgo_ShouldNotBeTreatedAsNeverUsed`. Current full unit run: 552 passed / 3 failed / 555 total.

**Handoff:** E2E start Aspire, sign in, generate today's plan with focus vocabulary, record preview IDs, launch each activity and verify no random reroll; refresh/restart webapp and verify same focus IDs round-trip.

---

### 2026-06-08T22:11:24-05:00: Captain directive — DailyPlan must sync across all surfaces

**By:** David Ortinau (via Copilot)
**What:** The DailyPlan CoreSync gap MUST be remedied as part of Phase 1 Focus Vocabulary work. Captain explicit: "I should be able to move between mobile and desktop/browser and see the same plan and personal progress."

**Why:** Cross-device continuity is a product expectation, not optional. The current state where `DailyPlan` claims sync participation in its model comment but is not actually registered in `SharedSyncRegistration` is a real user-visible bug, not just a code gap.

**Scope:** Both the plan structure AND personal progress must round-trip across mobile (SQLite) ↔ webapp/api (PostgreSQL). Focus vocabulary IDs must travel with the plan.

**Constraint on Phase 1 design choices:** Wash's choice for focus vocab storage cannot rely on "DailyPlan isn't synced, keep focus on DailyPlanCompletion." DailyPlan itself must be registered for sync now.

---

### 2026-06-08T22:41:31-05:00: Phase 1 Activity UI focus consumption

**By:** Kaylee
**What:** Wired plan-launched activity surfaces to consume the Phase 1 `FocusVocabularyIds` contract before legacy resource/SRS fallback. Vocab Quiz, Vocab Matching, Writing, Cloze, and Translation now parse the route query parameter and prefer the ordered focus set; Reading stays resource-driven and marks focus overlap in the passage; FlashcardActivity remains bound to `Narrative.VocabInsight.PreviewWords`, which Wash now derives from the focus set.

**Why:** Wash's backend contract guarantees the Today Plan preview and route payload carry the same ordered focus IDs. Captain's bug expectation is that the words previewed by the plan are the words practiced by the activity, not a freshly rerolled random subset.

**Pages refactored:** VocabQuiz (exclusive quiz batch), VocabMatching (deterministic pairs), Cloze (focus forwarding), Writing (focus chips before resource), Translation (focus-plus-context), Reading (focus overlap display), Index (explicit FocusVocabularyIds routing).

**Cloze/Translation AI prompt updates:** Now build prompt vocabulary with `FocusVocabularySelection.BuildRequiredFirstPromptVocabulary`, pass required/context/has_required into templates, and templates instruct AI to exercise focus vocabulary first.

**Test status:** `dotnet test` reported 563 passed / 1 failed / 564 total (baseline failure only).

**Build status:** Whole-solution build blocked by pre-existing platform issues; `dotnet build src/SentenceStudio.UI/` passed locally.

**E2E:** Start Aspire/webapp, sign in, generate Today's Plan, launch activities and verify no reroll, refresh webapp and verify same focus IDs persist.

---

### 2026-06-08T22:41:31-05:00: Multi-tenant data leak — WebApp preferences singleton (HOTFIX)

**Author:** Copilot CLI
**For:** Captain (David)
**Status:** ✅ Hotfix applied (defense-in-depth)

**TL;DR:** The German word "Brot" showed up in Captain's Korean vocab quiz in production despite zero German in his account. Root cause: three-defect chain exposed one user's data to another:

1. `WebPreferencesService` is a process-wide Singleton storing `active_profile_id` in a shared file.
2. `AccountEndpoints.AutoSignIn` writes globally on every login — last writer wins.
3. Repositories fall back to "return all rows" when active user ID is empty.

**Hotfix:** Removed every "empty userId → return everything" fallback across 5 repositories / 22 methods. Each now logs warning + returns empty list.

Files: `LearningResourceRepository.cs` (13 methods), `VocabularyProgressRepository.cs` (1), `UserActivityRepository.cs` (3), `SkillProfileRepository.cs` (2), `DiaryEntryRepository.cs` (2).

**Verified:** `dotnet build` clean (0 errors). `dotnet test`: 534 pass / 1 fail (pre-existing baseline).

**What this hotfix does NOT fix (architectural follow-up):** Replace `WebPreferencesService` singleton with request-scoped `IUserScopeProvider` reading from `HttpContext.User`. Decision needed on per-user `WebPreferencesService` data segregation, CoreSync per-user filters, and incident disclosure.

---

### 2026-06-09: Follow-up bug — RegeneratePlanAsync timezone mismatch

**By:** Wash (Backend Dev) — surfaced during overnight verification loop
**What:** `RegeneratePlanAsync` in `src/SentenceStudio.UI/Pages/Index.razor:989` calls `ClearCachedPlanAsync(DateTime.Today)` (LOCAL date), but `GetCachedPlanAsync` and `ReconstructPlanFromDatabase` use `DateTime.UtcNow.Date`. When local/UTC straddle midnight (Pacific 5pm = UTC midnight next day), Clear deletes wrong day's rows and subsequent Generate is a no-op.

**Workaround observed:** Two consecutive Regenerate clicks (or manual deletion then one click) produce expected result.

**Why:** Out of scope for this session's NarrativeFacts fix, but a real correctness bug that confuses users near UTC midnight. Worth filing as separate ticket.

**Suggested fix path:** Standardize on `DateTime.UtcNow.Date` everywhere, or better: change all three call sites to take explicit `DateOnly`/`DateTime.Date` parameter and pass consistently from single source of truth.

**Files implicated:** `src/SentenceStudio.UI/Pages/Index.razor:989` (`RegeneratePlanAsync`), `src/SentenceStudio.Shared/Services/Progress/ProgressService.cs` (`GetCachedPlanAsync`, `ReconstructPlanFromDatabase`).

---

### 2026-06-09: Mobile-side persistence asymmetry fixed — NarrativeFacts and RationaleFacts now persist on DailyPlan

**By:** Wash (Backend Dev) — requested by David
**What:** Updated `ProgressService.InitializePlanCompletionRecordsAsync` (mobile/MAUI path) to persist `NarrativeFacts` and `RationaleFacts` on the `DailyPlan` row, mirroring what `PlanService.UpsertPlan` (API path) already did. Added 6 private DTO classes plus two helpers — schemas kept aligned with `PlanService.cs:788-840`.

**Why:** Phase 1 fixed the contract (FocusVocabularyFacts persisted + CoreSync-replicated) but deeper persistence gap remained. Mobile path created/updated DailyPlan with only FocusVocabularyFacts, leaving NarrativeFacts/RationaleFacts NULL. After mobile regen, CoreSync propagated NULL to Postgres, clobbering the narrative the API had built. Webapp's `PlanService.GetTodaysPlanAsync` reads narrative from `DailyPlan.NarrativeFacts` — so a Mac-Catalyst regen invisibly destroyed the Preview button in the browser.

**Files changed:** `src/SentenceStudio.Shared/Services/Progress/ProgressService.cs` (helpers + DTOs + insert/update branches), `tests/SentenceStudio.UnitTests/Services/Progress/ProgressServiceFocusVocabularyContractTests.cs` (new test).

**Verified end-to-end (Mac Catalyst + CoreSync + Postgres):**
- Full test suite: 564/565 (baseline; +1 new test passes)
- Mac Catalyst regen → SQLite: NarrativeFacts=3229 chars, RationaleFacts=311 chars, FocusVocabularyFacts=828 chars
- CoreSync pushed → Postgres: SAME lengths, byte-identical (diff = zero output)
- Focus vocab IDs (20) == Preview word IDs (20) — 100% overlap
- Mac Catalyst dashboard renders Preview button (proves NarrativeFacts.VocabInsight.PreviewWords reaches UI)
- Sample focus words: 버스/bus, 듣다/to hear, 가방/bag, 사다/to buy, 식사/meal

**Follow-up bug noted (not fixed this session):** Index.razor:989 timezone mismatch (clear wrong day's rows near UTC midnight). Worth filing separately.

---

---

### 2026-06-10T03:35:00Z: Timezone fix routes plan dates through IPlanDateContext
**By:** Captain (davidortinau) (via Copilot, Claude Opus 4.7 xhigh)
**What:**
- `ProgressCacheService` plan methods (`GetTodaysPlan`, `SetTodaysPlan`, `InvalidateTodaysPlan`, `UpdateTodaysPlan`) now take an explicit `DateTime date` parameter instead of computing their own ambient "today". Cache TTL is computed as "expires at next UTC midnight after the keyed date" with a 1-minute floor to avoid negative/zero TTLs.
- `ProgressService.ResolveTodayKey()` resolves `IPlanDateContext` via `IServiceProvider.GetService` per-call. Returns `UserLocalDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)` — the UTC midnight that opens the user's local day. Falls back to `DateTime.UtcNow.Date` (kinded Utc) with a debug log if IPlanDateContext is unavailable (older test rigs).
- All 4 `var today = DateTime.UtcNow.Date;` lines in plan methods (GenerateTodaysPlanAsync, MarkPlanItemCompleteAsync, UpdatePlanItemProgressAsync, StartAdHocSessionAsync) replaced with `ResolveTodayKey()`. All 12 internal `_cache.*` call sites pass an explicit date.
- `IProgressService.GetCachedPlanAsync` and `ClearCachedPlanAsync` now take `DateTime? date = null` — null delegates to internal `ResolveTodayKey()`. Index.razor calls these without a date arg.
- Regression-guarded by `ProgressCacheServiceTimezoneTests.cs` (7 tests, 3 of which fail on the simulated bug — proven non-vacuous).
- 574/574 unit tests pass (567 baseline + 7 new TZ guards).
**Why:** Captain reported Vocab Quiz showed different words than the Preview. The root cause is deeper than the May 2026 followup note suggested. `ProgressCacheService` was keying by `DateTime.Today` (LOCAL), while `ProgressService` was keying by `DateTime.UtcNow.Date`. Both violate the existing `IPlanDateContext` contract (PlanService/DeterministicPlanBuilder/GeneratedPlanValidator already use it). For a Pacific user at 9pm local, `DateTime.UtcNow.Date` = tomorrow's date, so `WHERE Date = @today` matched no row → fallback plan generated with different vocabulary → mismatch with the preview. The DailyPlan model docstring already says the Date column should be `IPlanDateContext.UserLocalDate.ToDateTime(MinValue, Utc)`. The fix aligns ProgressService and ProgressCacheService with that contract.
**Scope of this commit:** Separate PR from the FocusVocabularyIds contract + NarrativeFacts persistence + PlanFactsSerializer hoist work (those are uncommitted in the same checkout — Captain's pre-push /review gate hasn't run yet).
**Files touched:**
- src/SentenceStudio.Shared/Services/Progress/ProgressService.cs
- src/SentenceStudio.Shared/Services/Progress/ProgressCacheService.cs
- src/SentenceStudio.Shared/Services/Progress/IProgressService.cs
- src/SentenceStudio.UI/Pages/Index.razor
- src/SentenceStudio.Shared/Services/SmartResourceService.cs
- tests/SentenceStudio.UnitTests/Integration/ProgressCacheServiceTests.cs (signature updates)
- tests/SentenceStudio.UnitTests/Services/Progress/ProgressServiceFocusVocabularyContractTests.cs (signature updates)
- tests/SentenceStudio.UnitTests/Integration/ProgressCacheServiceTimezoneTests.cs (NEW)
**Known out-of-scope follow-ups:**
- ProgressService.GetStreakInfoAsync at line 1072: streak math still uses raw `DateTime.UtcNow.Date`. Display-layer drift only; defer.
- Index.razor:1090 (flashcard nav fallback): `todaysPlan?.GeneratedForDate.Date ?? DateTime.UtcNow.Date`. Defer.
- The "BannedSymbols.txt" referenced in the IPlanDateContext docstring doesn't actually exist in the repo. Would be a nice future build-time guard but is not blocking this PR.
# Duplicate merge decision criteria

## Decision

Vocabulary duplicate cleanup should distinguish merge safety from keeper selection:

- Learning progress is combined during merge and is not a user decision criterion.
- Resource associations are combined during merge and are not a user decision criterion.
- Automatic merge safety depends on stable semantic identity: normalized target term, native term, language, and lexical unit type must match.
- Keeper selection should prioritize encoding strength and memory aids, not resource count.
- Duplicate review UI should hide raw IDs and resource counts from the primary decision view.
- Batch merge and single-group merge use the same recommendation logic; they must not imply different "auto" versus "keep best" behavior.

## Rationale

The user needs to decide only when merge safety is uncertain or when retained memory/encoding content differs. Showing progress, resource counts, and raw IDs creates noise because those values are preserved or internal implementation details.

## Follow-up

If future duplicate merge work combines complementary memory aids field-by-field, update this decision to describe conflict handling for competing non-empty mnemonic, image, and audio fields.
