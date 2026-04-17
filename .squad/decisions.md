## Active Decisions

### SHIPPED: Quiz decoupled from LearningResource (vocabulary-driven) (2026-04-17)

**Status:** ✅ SHIPPED  
**Date:** 2026-04-17  
**Affected Components:** Daily Plan generation, VocabularyReview activity routing, Quiz vocabulary loading  
**Commit:** 88a0272 "Decouple VocabularyReview Quiz from LearningResource"  

## Problem

Daily Plan Dashboard's Insights panel showed 497 words due ("8 new, 12 review, 497 total"), but when user tapped the VocabularyReview Quiz activity, it displayed "no vocabulary loaded" with toast "All vocabulary in this resource are mastered."

**Root Cause (Wash):** Resource filter mismatch
- Insights panel counted **globally** across all user vocabulary (via `GetDueVocabularyAsync()`)
- Quiz filtered by `ResourceId` (e.g., "daily review") → loaded only words linked to that resource
- The "daily review" resource had <5 due words (all mastered), so Quiz showed empty state despite global pool having 497

## Architecture Decision (Zoe)

**Option A: Clean Decoupling** — Remove ResourceId from VocabularyReview entirely, always load Quiz from full user vocabulary pool

### Rationale

1. **Activity Taxonomy:** VocabularyReview is fundamentally **vocabulary-driven**, not resource-driven
   - Vocabulary-driven: SRS query determines study pool (VocabularyReview, VocabularyGame, Writing, Translation, Cloze)
   - Resource-driven: specific media artifact is unit of work (Reading, Listening, VideoWatching, Shadowing, SceneDescription)
2. **Aligns with product vision:** "For a quiz, it's all about the vocabulary" (Captain)
3. **Simplest fix:** 3 code edits, no schema changes, no migrations
4. **Backwards compatible:** Plan records ephemeral, completion records allow NULL ResourceId
5. **No regression risk:** VocabQuiz.razor already had fallback for null resourceIds

## Implementation (Wash)

**Files Changed:**
- `PlanConverter.cs` (125-131): Removed ResourceId from VocabularyReview route parameters
- `DeterministicPlanBuilder.cs` (455-467): Set ResourceId=null in main plan VocabularyReview
- `DeterministicPlanBuilder.cs` (73-83): Set ResourceId=null in fallback plan VocabularyReview

**Verification:**
- ✅ Build clean (no new errors)
- ✅ No database schema changes
- ✅ No migration needed
- ✅ Code review approved (code-review agent)

## Deployment (Coordinator)

- ✅ Commit: 88a0272 merged to main
- ✅ Azure deploy: ✅ SUCCESS (2m36s)
  - Webapp: https://webapp.livelyforest-b32e7d63.centralus.azurecontainerapps.io
  - API: https://api.livelyforest-b32e7d63.centralus.azurecontainerapps.io
- ✅ Post-deploy validation: 17 pass / 0 fail
- ✅ iOS build succeeded, installed to DX24 (CF4F94E3-A1C9-5617-A089-9ABB0110A09F)

## Verification Plan (Jayne)

Comprehensive testing across:
1. Local development (Mac Catalyst + Aspire)
2. Azure WebApp (squad-test login, cross-account)
3. iOS DX24 device (physical iPhone 15 Pro)
4. Regression checks (resource-driven activities, VocabularyGame, Insights accuracy)
5. Database sanity (no migrations, ResourceId=NULL for completions)

**Critical Pass Criteria:**
- Quiz loads words when Insights shows due count > 0
- No "All vocabulary in this resource are mastered" toast
- Quiz URL lacks `resourceIds=` parameter
- DailyPlanItems.ResourceId=NULL for VocabularyReview
- WebApp and iOS work identically to local

## Outcome

VocabularyReview Quiz now loads from global user vocabulary pool, matching what Insights panel shows. Bug fixed, architecture cleaned up, no schema changes.

## Outstanding Questions

1. **Insights narrative:** Should Insights panel mention "Resource" for VocabularyReview or just "Vocabulary Pool"?
2. **Contextual learning value:** Retain or remove pedagogical preference for words from same resource?
3. **Completion tracking:** Should DailyPlanCompletion track which resources contributed to words practiced?

---

**Decision Chain:** Wash (diagnosis) → Zoe (architecture) → Wash (implementation) → code-review (approval) → Coordinator (deploy) → Jayne (verify)

---

### SHIPPED: Vocabulary Matching Decoupled from Resource Filtering (2026-01-29)

**Status:** ✅ SHIPPED  
**Date:** 2026-01-29  
**Affected Components:** Daily Plan generation, VocabularyGame routing, Matching vocabulary loading  
**Commit:** 0c8e197

## Problem

When launching **Vocabulary Matching** (`VocabularyGame` activity) from Today Plan on iOS DX24, the page displayed "no vocabulary available" — same root cause as the VocabQuiz decouple (commits 88a0272 + c081a63).

**Root Cause:** VocabMatching was exiting early when `resourceIds.Length == 0`. The plan builder correctly set `ResourceId = null` for plan-initiated matching, but the page interpreted empty resource IDs as "no data" and returned without loading any vocabulary.

## Architecture Decision

Like VocabularyReview/Quiz, **VocabularyGame is vocabulary-driven, NOT resource-driven**. When launched from Today Plan:
- Should pull from user's full due-vocabulary pool across ALL resources
- Should prioritize words based on SRS state (low mastery, not in grace period)
- ResourceId filtering should ONLY apply when user explicitly launches matching from a specific resource detail page

## Four-Layer Fix Applied

### Layer 1: DeterministicPlanBuilder (already correct ✅)
**File:** `src/SentenceStudio.Shared/Services/PlanGeneration/DeterministicPlanBuilder.cs`  
**Line 519:** `ResourceId = null` — Already set correctly for VocabularyGame activities

### Layer 2: PlanConverter (FIXED ✅)
**File:** `src/SentenceStudio.Shared/Services/PlanGeneration/PlanConverter.cs`  
**Lines 132-138:** Extended BuildRouteParameters to handle VocabularyGame like VocabularyReview:
- Added `DueOnly = true` parameter
- ResourceId is intentionally NOT passed to route params

### Layer 3: Index.razor LaunchPlanItem (FIXED ✅)
**File:** `src/SentenceStudio.UI/Pages/Index.razor`  
**Lines 929-936:** Extended ResourceId guard to cover both vocabulary activities:
- Changed condition to: `item.ActivityType != PlanActivityType.VocabularyReview && item.ActivityType != PlanActivityType.VocabularyGame`

### Layer 4: VocabMatching.razor (FIXED ✅)
**File:** `src/SentenceStudio.UI/Pages/VocabMatching.razor`  
**Changes:**
- **Line 95:** Added `DueOnly` query parameter (bool)
- **Lines 126-152:** Rewrote LoadVocabulary with defense-in-depth logic:
  - When `DueOnly=true`: Ignore ResourceIds, load from ALL user resources
  - When `DueOnly=false`: Use existing resource-filtered logic (user-initiated from resource page)
  - Pattern mirrors VocabQuiz.razor lines 634-636

## User-Initiated Resource-Filtered Matching Still Works

The fix preserves existing behavior when users launch matching from a resource detail page:
- `DueOnly=false` path is unchanged
- ResourceIds are still parsed and honored
- Only filters vocabulary to selected resources
- No breaking changes to non-plan launches

## Verification

- ✅ Build clean (0 errors, 363 warnings — all pre-existing)
- ✅ Azure deploy: SUCCESS
- ✅ Post-deploy validation: 17/17 pass
- ✅ iOS DX24: installed successfully

## Deployment (Coordinator)

- ✅ Commit: 0c8e197 merged to main
- ✅ Azure deploy: ✅ SUCCESS
  - Webapp: https://webapp.livelyforest-b32e7d63.centralus.azurecontainerapps.io
  - API: https://api.livelyforest-b32e7d63.centralus.azurecontainerapps.io
- ✅ Post-deploy validation: 17 pass / 0 fail
- ✅ iOS installed to DX24 (CF4F94E3-A1C9-5617-A089-9ABB0110A09F)

## Pattern Recognition

This is the second vocabulary-driven activity (`VocabQuiz` → `VocabMatching`) to require the same four-layer decoupling pattern. Future vocabulary-driven activities should follow this template:

1. **Plan builder:** Set ResourceId = null
2. **PlanConverter:** Add DueOnly=true, skip ResourceId
3. **Index.razor:** Guard ResourceId passing for activity type
4. **Page:** Implement DueOnly defense-in-depth (load all resources when true, resource-filtered when false)

---

**Decision Chain:** Wash (diagnosis) → Wash (implementation) → code-review (approval) → Coordinator (deploy) → Scribe (verify + closeout)

---

### BUG-INVESTIGATION: Daily Plan Regenerates After Activity Completion (2026-07-27)

**Status:** INVESTIGATION COMPLETE -- awaiting Captain's decision on fix approach  
**Date:** 2026-07-27  
**Author:** Wash (Backend Dev)

**Context:** Captain reported that completing Shadowing caused the entire daily plan to change. Original plan had Listening and Reading as first two activities; after completing Shadowing and returning to dashboard, those were replaced with different activities.

**Root Causes Found (3 contributing factors):**

1. **5-minute cache TTL expiration** (`ProgressCacheService.cs:14`): The in-memory plan cache expires after 5 minutes. If an activity session lasts longer than 5 minutes, the cache entry is gone when the user returns to the dashboard. `GetCachedPlanAsync` then tries DB reconstruction.

2. **DB reconstruction succeeds but ValidatePlanActivitiesAsync can remove items** (`ProgressService.cs:844-890`): After reconstructing from DB, `ValidatePlanActivitiesAsync` filters out activities whose resources lack required capabilities (e.g., Reading without transcript). If resource data changed or was incomplete, items get dropped. With fewer items, the plan looks different.

3. **Non-deterministic tiebreakers in DeterministicPlanBuilder** (`DeterministicPlanBuilder.cs:743,769`): Both `SelectInputActivity` and `SelectOutputActivity` use `.ThenBy(a => Guid.NewGuid())` as a tiebreaker when multiple activities have equal "recently used" counts. This means if the plan IS regenerated (cache miss + DB reconstruction returns null or fails), the new plan will have randomly different activities even with identical input data. Also, `BuildActivitySequenceAsync` (line 439) queries `DailyPlanCompletions` from the last 3 days INCLUDING today -- so the newly-written completion record for the just-completed activity changes the frequency counts, shifting which activities get selected.

**Most Likely Scenario for the Captain's Bug:**
- Captain starts plan, begins Shadowing activity
- Shadowing takes > 5 minutes, cache TTL expires
- Captain completes Shadowing, completion record written to DB
- Captain navigates back to dashboard, `OnInitializedAsync` calls `LoadPlanAsync`
- `GetCachedPlanAsync` finds no cache entry, tries `ReconstructPlanFromDatabase`
- DB reconstruction works (completion records exist), BUT `ValidatePlanActivitiesAsync` removes some items OR the reconstructed plan is subtly different
- If reconstruction somehow fails or returns incomplete data, `GenerateTodaysPlanAsync` calls `_llmPlanService.GeneratePlanAsync` which runs `DeterministicPlanBuilder.BuildPlanAsync` fresh -- and with `Guid.NewGuid()` tiebreakers PLUS changed recent-activity counts, the new plan is completely different

**Recommended Fix (3 parts):**

1. **Remove random tiebreakers**: Replace `Guid.NewGuid()` with deterministic tiebreakers (e.g., hash of date + activity name) in `SelectInputActivity` and `SelectOutputActivity`

2. **Extend cache TTL for plans**: Plans should not expire on a 5-minute TTL. Either use a much longer TTL (24 hours) or make the plan cache date-keyed so it never expires during the same calendar day

3. **Exclude today's completions from activity selection**: `BuildActivitySequenceAsync` should filter `recentActivityTypes` to exclude today's date, since today's completions are FROM the current plan, not historical data that should influence a new plan

### 10. Production Web Validation Uses ACA Default Host Until Custom-Domain Cutover (2026-04-09)

**Status:** ACTIVE  
**Date:** 2026-04-09  
**Author:** Scribe (from Wash / Coordinator / Jayne deployment run)

**Context**

The production Azure publish for `sstudio-prod` in Central US completed successfully via `azd deploy -e sstudio-prod --no-prompt`. The deploy output reported live Azure Container Apps endpoints for both the public webapp and the Aspire dashboard, while the custom domain still appeared to be separate/off and likely needs its own DNS/domain follow-up.

**Decision**

1. Use the ACA default webapp hostname as the immediate production validation URL: `https://webapp.livelyforest-b32e7d63.centralus.azurecontainerapps.io/`
2. Use the ACA Aspire dashboard hostname for operational inspection: `https://aspire-dashboard.ext.livelyforest-b32e7d63.centralus.azurecontainerapps.io`
3. Track custom-domain cutover as a separate follow-up item; do not treat it as a blocker for confirming deployment success.
4. Treat the repo root as the operator deploy entrypoint and use `azd deploy --environment sstudio-prod --no-prompt` for production publishes; a git push is not required for this path.
5. Post-deploy validation should expect the default webapp host to return HTTP `200` with a redirect to `/auth/login`, protected API routes to return HTTP `401`, and the marketing site to be validated through `https://www.sentencestudio.com` rather than the raw ACA hostname.

**Impact**

- QA can verify the live production experience immediately on the default hostname.
- Deployment completion is decoupled from DNS/custom-domain timing.
- A separate domain follow-up remains required before custom-domain rollout is considered complete.
- The team has one repeatable production publish command and clearer verification expectations.

---

### 11. Duplicate Cleanup Uses Focused Re-entry and Post-Render Feedback (2026-04-10)

**Status:** VERIFIED  
**Date:** 2026-04-10  
**Author:** Scribe (from Kaylee / Jayne / Coordinator)

**Context**

The duplicate scan itself was working, but the webapp flow felt broken on long vocabulary pages because the loading state could fail to paint before the synchronous scan began and the cleanup panel could render outside the current viewport. The edit/details flow also needed a focused way to jump directly into duplicate management for the current word.

**Decision**

1. Keep `/vocabulary` as the single duplicate cleanup workflow and deep-link into it from the word detail/edit flow using `duplicateTerm` and `focusWordId`.
2. For the full duplicate scan, allow a brief delay after `StateHasChanged()` so the spinner/status can paint before the blocking work starts.
3. Scroll the specific cleanup panel into view only after results have rendered, using `scrollIntoView(...)` with a short post-render delay instead of a generic `window.scrollTo({ top: 0 })`.
4. Treat future regressions here as a visibility/flow issue first when results are present but not obvious onscreen.

**Impact**

- Users get immediate feedback that the full scan is running.
- Results land in view reliably from both list and focused-detail flows.
- QA has a clearer expectation for verifying duplicate cleanup on the live webapp.

---

### 12. CoreSync Auth Shares the API JWT Pipeline and Deterministic Test Harness (#85) (2026-04-09)

**Status:** ADOPTED  
**Date:** 2026-04-09  
**Author:** Scribe (from Wash)
**Issue:** #85

**Context**

CoreSync needed to validate the same JWTs as the API without carrying a separate auth stack, and the auth integration tests needed a stable local/CI harness that did not depend on startup migrations or live Aspire PostgreSQL wiring.

**Decision**

1. Route CoreSync auth through the API policy scheme: forward Bearer requests to `JwtBearer` and fall back to `DevAuthHandler` only when no Bearer token is present.
2. Explicitly require authorization on the CoreSync endpoints themselves.
3. In auth integration tests, skip startup migrations and swap the Aspire PostgreSQL registration for SQLite plus CoreSync provisioning so the suite runs deterministically in CI and local environments.

**Impact**

- Web/CoreSync auth behavior stays aligned with the API.
- Dev auth remains available for local workflows without weakening Bearer validation.
- CI/local auth tests are more stable and reproducible.

---

### 13. Mobile Auth Must Preserve Weeks-Long Sign-In on Phones (2026-04-08)

**Status:** ACTIVE  
**Date:** 2026-04-08  
**Author:** Scribe (from David / Wash)

**Context**

Captain explicitly directed that mobile auth should keep people signed in for weeks on their phones; having to log in multiple times per day is unacceptable. Follow-up investigation confirmed the frequent logout symptom was caused by refresh tokens being cleared on transient failures, and that this problem is separate from CoreSync JWT validation issue `#85`.

**Decision**

1. Treat “multiple logins per day” as a regression against the expected mobile auth experience.
2. Only clear stored refresh tokens when the server explicitly rejects them (`401`/`403`); preserve them across transient network, timeout, and `5xx` failures and retry on resume.
3. Track CoreSync JWT validation (`#85`) separately from user-facing session persistence so the two concerns are not conflated.

**Impact**

- Mobile users should remain signed in through normal transient network failures instead of being forced back to login.
- Debugging future auth complaints starts with refresh/token handling rather than misrouting everything into `#85`.

---

### 6. Scoring Override Window Expiration (#151) (2026-04-03)

**Status:** IMPLEMENTED  
**Date:** 2026-04-03  
**Author:** Wash (Backend Dev)  
**Issue:** #151

**Context**

When users override vocabulary scores (e.g., for learning review or mastery validation), the override is meant to be temporary — valid for a limited window. However, the `OverriddenScoringDto` entity lacked an `ExpiresAt` timestamp, so override checks only verified existence, not expiration. This caused overridden scores to persist indefinitely beyond their intended window.

**Decision**

1. **Add ExpiresAt Timestamp:** `OverriddenScoringDto` now includes `ExpiresAt` property (DateTime).
2. **Expiration Validation on Read:** `GetVocabularyScoresAsync()` now validates expiration before returning overridden score; expired overrides are silently disregarded, falling back to the base score.
3. **Lazy Cleanup:** Overrides remain in the database until after expiration window closes. No aggressive cleanup task — idempotent expiration check on read is sufficient and cleaner.

**Implementation**

- New EF Core migration: Added `ExpiresAt` column to `OverriddenScoring` table
- Updated `VocabularyScoreService.GetVocabularyScoresAsync()` — checks `override.ExpiresAt > DateTime.UtcNow` before using override
- Backward compatible: Null `ExpiresAt` is treated as perpetual override (existing data migrates without loss)

**Rule for Future**

Any override or temporary data with expiration semantics must have an explicit timestamp. Existence-only checks lead to unbounded state growth and stale data.

**Files Modified**
- `src/SentenceStudio.Database/Entities/OverriddenScoring.cs`
- `src/SentenceStudio.Database/Migrations/` (new migration)
- `src/SentenceStudio.Entities/Dtos/OverriddenScoringDto.cs`
- `src/SentenceStudio.Services/VocabularyScoreService.cs`

**Commits**
- Wash: `58a8364`

**Impact**
- Users: Scoring overrides now correctly expire
- API: No breaking changes; backward compatible
- No data loss risk

---


### 7. Text Input Validation with FuzzyMatcher (#150) (2026-04-03)

**Status:** IMPLEMENTED  
**Date:** 2026-04-03  
**Author:** Kaylee (Full-Stack Dev)  
**Issue:** #150

**Context**

Text input validation was too strict. Simple character-count validation rejected valid multi-word phrases. For example, "I am here" failed because "I" is 1 character. This forced users to manually reword their answers or skip exercises.

**Decision**

Integrate `FuzzyMatcher` for phrase-level validation with support for slash-separated alternatives:
1. **Phrase Validation:** Accepts multi-word input; validates semantic meaning, not raw character count.
2. **Alternative Phrases:** Users can define alternatives as `"word1/word2/word3"` — any match succeeds.
3. **Natural Language Input:** Supports natural phrasing without forcing exact character-length compliance.

**Implementation**

- `FuzzyMatcher` instantiated in `ActivityPage.razor` for text input validation
- Validation now operates on phrase semantics using `FuzzyMatcher.Match()` logic
- Users can enter natural phrases; acceptable answer variants are auto-detected

**Files Modified**
- `src/SentenceStudio.UI/Pages/ActivityPage.razor` — validation wiring
- `src/SentenceStudio.Services/Matching/FuzzyMatcher.cs` — text input integration

**Impact**
- Users: Can enter multi-word phrases naturally without rejection
- UI: Validation feedback clearer; FuzzyMatcher surfaces match details
- No breaking changes

---


### 8. Accurate Turn Counting with Word Tokenization (#149) (2026-04-03)

**Status:** IMPLEMENTED  
**Date:** 2026-04-03  
**Author:** Kaylee (Full-Stack Dev)  
**Issue:** #149

**Context**

The turn counter miscalculated word count using `string.Split(' ').Length`. This counts spaces + 1, not actual words. Contractions, hyphenated words, and punctuation weren't parsed correctly, leading to inaccurate activity timing.

**Decision**

Replace simple split with proper word tokenization:
1. **Word Tokenization:** Uses `Regex.Matches()` to count actual words: `[\p{L}\p{N}]+` (letters + numbers).
2. **Handles Edge Cases:** Correctly counts contractions, hyphenated words, and punctuation as single/multiple words as appropriate.
3. **Accurate Activity Timing:** Turn counter now displays accurate word count, improving plan estimation.

**Implementation**

- Updated `ActivityService.CalculateTurnCount()` to use regex-based word tokenization
- Idempotent: Existing test data remains valid; no breaking changes

**Files Modified**
- `src/SentenceStudio.Services/ActivityService.cs` — `CalculateTurnCount()` method
- Tests: Added test cases for contractions, hyphenation, punctuation

**Impact**
- Users: Turn counter now displays accurate word count
- Activity timing: More reliable plan estimates
- No breaking changes

---


### 9. Narrative Framing Rules for User Trust (2026-03-31)

**Status:** DOCUMENTED  
**Date:** 2026-03-31  
**Author:** David (via Copilot)

**Context**

User feedback revealed that the narrative framing for untested vocabulary was offensive: labeling words the user hasn't attempted as "struggling" is inaccurate and erodes trust. The narrative must feel like a knowledgeable coach, not a clueless critic.

**Decision**

1. **Never say "struggling" for 0% accuracy** — that means untested, not struggling. Only use "struggling" when user has demonstrated failures (attempts > 0, accuracy < threshold).
2. **Focus words must be relevant** to the highlighted categories, not arbitrary.
3. **Frame untested vocab as "unproven" or "new to you"**, not as a weakness.
4. **Narrative in collapsible panel** — don't fill up the home page with coaching text.
5. **App must demonstrate it understands the user** — misframing hurts trust and undermines engagement.

**Implementation Pattern**

When rendering vocabulary narratives:
- Check `attempts > 0` before using "struggling" language
- Use "new to you" or "unproven" for untested words (attempts == 0)
- Only apply focus words that align with detected patterns
- Ensure narrative tone matches the user's demonstrated proficiency

**Impact**
- Users: Narratives feel accurate and supportive, not judgmental
- Trust: App demonstrates understanding of user progress, not false criticism
- Engagement: Positive coaching tone encourages continued learning


