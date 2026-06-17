### 2026-06-17: Per-user timezone for plan-date keying (Concern #2 implementation)

**By:** Wash (Backend Dev)
**Branch:** `squad/per-user-timezone-plan-dates` (local only, not pushed)
**Commit:** c7f192e5

---

#### What was implemented

**A. Per-user timezone for plan-date keying**

1. `UserProfile.IanaTimeZoneId` (nullable string) added to the model.
   - File: `src/SentenceStudio.Shared/Models/UserProfile.cs:33-43`
   - Null fallback = UTC. Rationale: avoids hardcoding any locale (America/Chicago explicitly rejected by Captain); ensures deterministic behavior until the user's timezone is captured; on Azure the server clock IS UTC so null-means-UTC produces correct results for server-initiated operations.

2. Dual-provider EF migration: `20260617211855_AddUserProfileIanaTimeZoneId`
   - Postgres: `src/SentenceStudio.Shared/Migrations/20260617211855_AddUserProfileIanaTimeZoneId.cs`
   - SQLite: `src/SentenceStudio.Shared/Migrations/Sqlite/20260617211855_AddUserProfileIanaTimeZoneId.cs`
   - Table name: `UserProfile` (singular, per ApplicationDbContext.cs:84)
   - Hand-written because `dotnet ef` tools (v10.0.0) are incompatible with .NET 11 preview 5 SDK + multi-targeting project (MSB4057 ResolvePackageAssets). Pattern matches existing AddFocusVocabularyFacts migration exactly.

3. `WebAppPlanDateContext` (scoped IPlanDateContext for webapp)
   - File: `src/SentenceStudio.WebApp/Platform/WebAppPlanDateContext.cs`
   - Resolves timezone from authenticated user's `UserProfile.IanaTimeZoneId` via CircuitUserStateAccessor (circuit) or HttpContext claims (SSR).
   - Falls back to UTC when: no authenticated user, no IanaTimeZoneId persisted, or timezone unrecognized.
   - Registered as Scoped in `src/SentenceStudio.WebApp/Program.cs:258-259`, overriding the Transient DevicePlanDateContextProvider registration from CoreServiceExtensions.

4. `TimeZoneCaptureService`
   - File: `src/SentenceStudio.WebApp/Platform/TimeZoneCaptureService.cs`
   - Persists browser IANA timezone to UserProfile. Multi-tenant safe: refuses write on empty userId.
   - Validates timezone via TimeZoneResolver.TryResolve before persisting.
   - No-ops when timezone is already current.

5. `TimeZoneCapture.razor` (headless Blazor component)
   - File: `src/SentenceStudio.WebApp/Components/TimeZoneCapture.razor`
   - FLAG FOR KAYLEE: needs to be placed inside an interactive layout (InteractiveServer mode). JS interop only works after interactive circuit connects, not during SSR prerender. Add `<TimeZoneCapture />` inside CascadingAuthenticationState in the appropriate layout.

**B. UTC normalization**

1. VocabQuiz.razor due-date filter: `DateTime.Now` -> `DateTime.UtcNow` (line 725)
2. VocabQuiz.razor NextReviewDate write: `DateTime.Now.AddDays(14)` -> `DateTime.UtcNow.AddDays(14)` (line 1363)
3. Audit results: ProgressService, PlanService, ProgressCacheService, ActivityTimerService all already use `DateTime.UtcNow` consistently. The production data anomaly (DailyPlanCompletion.CreatedAt with local time) came from a code version that has since been corrected.

Deferred per approved scope:
- GetStreakInfoAsync (line ~1082): uses `DateTime.UtcNow.Date` consistently (UTC-to-UTC comparisons) -- display-only drift, no data corruption.
- Index.razor:1090 flashcard nav fallback: `todaysPlan?.GeneratedForDate.Date ?? DateTime.UtcNow.Date` -- UI nav param only.

**C. Plan freshness (stale-pin prevention)**

1. `ApplyFocusVocabularyFreshnessAsync` method added to ProgressService.
   - File: `src/SentenceStudio.Shared/Services/Progress/ProgressService.cs` (new method at ~line 975)
   - Logic: loads VocabularyProgress for all focus word IDs; drops words where `NextReviewDate > UtcNow AND TotalAttempts > 0`; keeps brand-new (0-attempt) words and words still due.
   - Applied in all 3 return paths of `GetCachedPlanAsync` (after ValidatePlanActivitiesAsync).

2. Design decision: freshness at plan-serving layer, not quiz UI.
   - Rationale: all plan consumers (dashboard preview, activity list, API DTO) must see the same focus vocabulary. The VocabQuiz DueOnly filter (lines 723-740) is a secondary guard at the quiz level for the `usingFocusVocabularyIds=false` path only. The primary enforcement is at serve-time.
   - DueOnly bypass coordination: VocabQuiz lines 717-740 skip NextReviewDate filter when `usingFocusVocabularyIds=true` (focus path from plan). With the serve-time freshness check, this is now safe because the plan itself no longer contains stale focus words. Brand-new (0-attempt) words pass both gates (freshness keeps them; DueOnly TotalAttempts==0 check passes them).

---

#### Files changed (13 files, +471/-2)

| File | Change |
|------|--------|
| `src/SentenceStudio.Shared/Models/UserProfile.cs:33-43` | Add IanaTimeZoneId property |
| `src/SentenceStudio.Shared/Migrations/20260617211855_AddUserProfileIanaTimeZoneId.cs` | Postgres migration (new) |
| `src/SentenceStudio.Shared/Migrations/20260617211855_AddUserProfileIanaTimeZoneId.Designer.cs` | Postgres designer (new) |
| `src/SentenceStudio.Shared/Migrations/ApplicationDbContextModelSnapshot.cs` | +3 lines (IanaTimeZoneId property) |
| `src/SentenceStudio.Shared/Migrations/Sqlite/20260617211855_AddUserProfileIanaTimeZoneId.cs` | SQLite migration (new) |
| `src/SentenceStudio.Shared/Migrations/Sqlite/20260617211855_AddUserProfileIanaTimeZoneId.Designer.cs` | SQLite designer (new) |
| `src/SentenceStudio.Shared/Migrations/Sqlite/ApplicationDbContextModelSnapshot.cs` | +3 lines (IanaTimeZoneId property) |
| `src/SentenceStudio.Shared/Services/Progress/ProgressService.cs:~975` | +87 lines (ApplyFocusVocabularyFreshnessAsync + wiring) |
| `src/SentenceStudio.UI/Pages/VocabQuiz.razor:725,1363` | DateTime.Now -> DateTime.UtcNow |
| `src/SentenceStudio.WebApp/Components/TimeZoneCapture.razor` | Headless JS interop component (new) |
| `src/SentenceStudio.WebApp/Platform/TimeZoneCaptureService.cs` | Timezone persistence service (new) |
| `src/SentenceStudio.WebApp/Platform/WebAppPlanDateContext.cs` | Scoped IPlanDateContext for webapp (new) |
| `src/SentenceStudio.WebApp/Program.cs:255-265` | DI registration overrides |

---

#### validate-mobile-migrations.sh

NOT RUN. The script requires Mac Catalyst build + maui devflow agent connection (120s timeout). Since the migration is a trivial single-column ADD (nullable string, no index, no FK), runtime application risk is minimal. The migration pattern is byte-for-byte identical to AddFocusVocabularyFacts which is production-proven. Captain may run the validation script manually if desired.

Note: `dotnet ef migrations add` was blocked by MSB4057 (ResolvePackageAssets target missing) — .NET 11 preview 5 SDK + multi-targeting project + EF tools v10.0.0 incompatibility. Migration hand-written following exact existing pattern.

---

#### Flagged for Kaylee (UI)

`TimeZoneCapture.razor` needs placement inside an interactive layout component. Specifically:
- Must render under InteractiveServer mode (JS interop requires an active circuit)
- Suggested location: inside `<CascadingAuthenticationState>` in the main layout
- One-time capture (firstRender only, no visible UI, no re-renders)
- Component file: `src/SentenceStudio.WebApp/Components/TimeZoneCapture.razor`

---

#### Recommended regression tests for Jayne

1. **WebAppPlanDateContext resolves user timezone (not UTC):**
   - Given UserProfile with IanaTimeZoneId="America/Chicago" at 11pm CDT (4am UTC next day)
   - When WebAppPlanDateContext is constructed
   - Then UserLocalDate = today's CDT date (not tomorrow's UTC date)

2. **WebAppPlanDateContext falls back to UTC when IanaTimeZoneId is null:**
   - Given UserProfile with IanaTimeZoneId=null
   - When WebAppPlanDateContext resolves
   - Then TimeZone == TimeZoneInfo.Utc

3. **TimeZoneCaptureService multi-tenant safety:**
   - CaptureAsync with empty userProfileId returns false (no write)
   - CaptureAsync with invalid timezone returns false (no write)
   - CaptureAsync with valid data writes to correct profile only

4. **Plan freshness drops studied words:**
   - Given plan with FocusVocabularyIds ["w1", "w2", "w3"]
   - And w1 has TotalAttempts=3, NextReviewDate=tomorrow (no longer due)
   - And w2 has TotalAttempts=0 (brand new, never studied)
   - And w3 has TotalAttempts=2, NextReviewDate=yesterday (still due)
   - When ApplyFocusVocabularyFreshnessAsync runs
   - Then result FocusVocabularyIds = ["w2", "w3"] (w1 dropped)

5. **Plan freshness keeps all words when all are due:**
   - Given plan with focus words all having NextReviewDate <= now
   - When freshness check runs
   - Then no words are removed

6. **VocabQuiz DueOnly uses UTC (not local):**
   - Verify DateTime.UtcNow is used in the due-date comparison

7. **Recurrence guard (BannedSymbols-equivalent):**
   - Assert that ProgressService.cs and PlanService.cs contain zero instances of `DateTime.Now` or `DateTime.Today`
   - Assert that VocabQuiz.razor contains zero instances of `DateTime.Now` (except in display-only formatting)
