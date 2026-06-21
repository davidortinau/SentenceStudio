# Decisions Archive — 2026-06-17

Archived by Scribe on 2026-06-17. Tier 2 (7-day) archive — entries older than 2026-06-10.

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
