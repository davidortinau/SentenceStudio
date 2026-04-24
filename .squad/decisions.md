## Active Decisions

(Most recent decisions below. Archived decisions in `decisions-archive-YYYY-MM-DD.md`)

---

## 2026-04-24 — Per-Type Idempotency for Smart Resource Seeding

**Date:** 2026-04-24
**Owner:** Wash
**Status:** ✅ Shipped (e2e verified, awaiting review)

### Problem

Smart resource seeding short-circuited if ANY resource existed, preventing upgraded users from receiving new types (e.g., `Phrases` added after Daily Review / New Words / Struggling Words). Jayne's e2e Step 5 caught this: the Phrases resource was missing for an upgraded user.

### Decision

Smart resource seeding is now **per-type idempotent** via `HashSet<SmartResourceType>` check:

1. Load existing smart resources into a HashSet by type
2. Iterate canonical seed order (Daily Review → New Words → Struggling → Phrases)
3. Create each resource only if its type is not already present
4. Call `RefreshSmartResourceAsync` only for newly-created resources

### Invariants

- No schema change, no migration, no DB reset
- Existing users retain all resources with IDs and associations intact
- Seed order (Daily Review → New Words → Struggling → Phrases) is fixed
- Future smart resources: append (never insert into middle) and reuse pattern

### Future rule

When adding a 5th smart resource type:
1. Add constant on `SmartResourceService`
2. Append to seed definitions array (do NOT insert middle)
3. Wire type into `GetSmartResourceVocabularyIdsAsync` + add dedicated getter
4. Upgraded users auto-get it on next launch (per-type check sees it missing)

No migration required. No user-data impact.

### Verification

- Build: ✅ 0 errors
- E2E Step 5 re-run: ✅ Phrases smart resource present + 4 total resources in DB

---

## 2026-04-24 — Wire SmartResourceService Into UserProfileRepository.GetAsync

**Date:** 2026-04-24
**Owner:** Wash
**Status:** ✅ Shipped (e2e verified, awaiting review)

### Problem

`SmartResourceService.InitializeSmartResourcesAsync` had **zero production callers** — only test suite invoked it. The per-type idempotency fix was correct in isolation but unreachable at runtime; upgraded users still missed Phrases smart resource.

### Decision

Call `InitializeSmartResourcesAsync` once per user per session from `UserProfileRepository.GetAsync`, immediately after `EnsureMultiUserBackfillAsync`. This hooks into the canonical post-migration, post-backfill funnel where every profile access flows through.

**Why this hook:**
- Already the canonical profile resolution point (every user path flows here)
- Symmetric with existing `_backfillDone` "ensure once per session" pattern
- Reuses `_serviceProvider`, minimal ceremony, avoids touching `MauiProgram`

**Guard pattern:** Two-layer idempotence (per-user in-session + DB-layer per-type):
- Static `HashSet<string>` keyed on `profile.Id` prevents redundant calls within a session
- `SmartResourceService.InitializeSmartResourcesAsync` already per-type idempotent at DB layer
- Either guard alone sufficient; both explicit about intent

**Fault tolerance:**
- Non-fatal: exceptions logged at Warning, swallowed, never fail `GetAsync` callers
- Uses `GetService<T>` (not `GetRequiredService`), graceful degrade if service absent
- In-session guard set in `finally`, one-off failure doesn't block retries next launch
- Per-user guard: multi-profile scenarios seed each profile on first load

### Test coverage

`SmartResourcePhrasesTests` unaffected — still calls `InitializeSmartResourcesAsync` directly with explicit args.

### Verification

- Build: ✅ 0 errors
- E2E Step 5 re-run (upgraded profile): ✅ Phrases smart resource created + accessible

---

## 2026-04-23 — SQLite Migration History Reconciliation (Option A)

**Date:** 2026-04-23
**Owner:** Wash
**Status:** ✅ Complete

### Problem

Local SQLite DB had stale `__EFMigrationsHistory` rows that didn't match applied schema. EF's consistency check would prevent applying new migrations.

### Decision (Option A)

Backfill `__EFMigrationsHistory` for migrations already reflected in schema; leave target migration (`AddLexicalUnitTypeAndConstituents`) unlisted so EF applies it.

### Audit & actions

| MigrationId | Schema state | Action |
|---|---|---|
| 20260321133148_InitialSqlite | Base tables present | Backfill |
| 20260321133200_AddYouTubeChannelMonitoring | Tables absent | Leave unlisted (EF will create) |
| 20260322012812_SyncDailyPlanAndUserActivity | Schema matches | Backfill |
| 20260328192206_AddMissingVocabularyWordLanguageColumn | Column present | Backfill |
| 20260415024019_CurrentStreakToFloat | INTEGER type (SQLite permissive) | Leave unlisted |
| 20260423213242_AddLexicalUnitTypeAndConstituents | Missing columns/table | Target (leave unlisted) |
| 20260725230000_AddPassiveExposureFields | Columns present | Backfill |

**Inserted rows:**
```sql
INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES
 ('20260321133148_InitialSqlite', '10.0.4'),
 ('20260322012812_SyncDailyPlanAndUserActivity', '10.0.4'),
 ('20260328192206_AddMissingVocabularyWordLanguageColumn', '10.0.5'),
 ('20260725230000_AddPassiveExposureFields', '10.0.5');
```

### Data preservation

Row counts before/after identical:
- UserProfile: 1
- VocabularyWord: 2595
- VocabularyProgress: 1745
- Challenge: 197

Only `__EFMigrationsHistory` modified.

### Verification

- Backup created (6,758,400 bytes)
- E2E launch: ✅ Migrations apply cleanly, app reaches home shell
- DB schema verified post-migration

---

## 2026-04-23 — Ad-hoc activity tracking via synthetic plan completions

**Date:** 2026-04-23
**Owner:** David (Captain) + Copilot
**Status:** ✅ Shipped to DX24

### Problem

`ActivityLog` (dashboard day-detail) only showed `DailyPlanItem` completions — "choose my own" sessions (user navigates directly to Translation/Writing/etc. without a plan) produced zero tracking. Captain wanted duration + resource + skill surfaced for freeform practice too.

### Decision

Persist ad-hoc sessions as synthetic `DailyPlanCompletion` rows with `PlanItemId = "adhoc-{guid}"`. Reuse the existing ActivityLog pipeline unchanged, and filter the `adhoc-*` prefix out of plan reconstruction so they don't pollute "Today's Plan" on the dashboard.

### Implementation

- `IProgressService.StartAdHocSessionAsync(PlanActivityType, resourceId, skillId, estimatedMinutes=10)` creates the record. Priority=999, TitleKey=`Activity_{type}`.
- `ProgressService.ReconstructPlanFromDatabase` filters `!c.PlanItemId.StartsWith("adhoc-")` so the dashboard only sees real plan items.
- `GetActivityLogAsync` intentionally reads ALL completions so ad-hoc rows show up in day detail.
- `IActivityTimerService.StartSession(activityType, activityId?, resourceId?, skillId?)` — when `activityId` is null/empty, auto-creates the ad-hoc record and starts the stopwatch.
- `PlanSummaryCard` detects `plan.Items.All(i => i.PlanItemId.StartsWith("adhoc-"))` and renders the cluster with a ✨ icon + "Freeform practice" label instead of "Plan N". Duration shown as `N min` (no estimate denominator since ad-hoc has no committed target).
- All 10 activity razor pages now call `StartSession` unconditionally (was `if (!string.IsNullOrEmpty(PlanItemId)) …`).

### Gotchas / lessons

- **`PlanActivityType` enum values** are narrower than the activity page set. Valid: `VocabularyReview, Reading, Listening, VideoWatching, Shadowing, Cloze, Translation, Writing, SceneDescription, Conversation, VocabularyGame`. **No** `VocabularyMatching, HowDoYouSay, WordAssociation, MinimalPairs`. VocabMatching was passing `"VocabularyMatching"` which silently failed `Enum.Parse` — fix was to use `"VocabularyGame"` (matches the `/vocabulary-matching` route's enum mapping).
- HowDoYouSay / WordAssociation / MinimalPairs pages still exist; they won't record ad-hoc rows because there's no enum value. `ActivityTimerService.StartAdHocThenLoadAsync` logs a warning and runs the timer without persistence in that case — acceptable fallback until the enum is widened.
- Plan clustering groups completions within 60s of each other. An ad-hoc session started mid-day will form its own cluster — hence the visual differentiator. Don't try to force ad-hoc rows into the user's morning plan cluster.
- **Resource/skill query param naming is NOT uniform** across pages. Captured:
  - `ResourceIdParam` (singular): Translation, Writing, Cloze, Reading, Shadowing, Conversation, Scene, VideoWatching
  - `ResourceIdsParam` (plural, comma-separated): VocabQuiz, VocabMatching
  - No skill: VocabMatching, VideoWatching
  - For ad-hoc persistence of plural-resource activities, take the **first** id: `ResourceIdsParam?.Split(',').FirstOrDefault()`.

### Razor parser quirk (worth remembering)

Nested `@if/@else` with string-interpolated text containing parens around a method chain (e.g. `@Localize["Key"] (@plan.GeneratedAt.ToLocalTime().ToString("h:mm tt"))`) throws `CS1002: ; expected`. Workaround: pre-compute strings in a `@{ }` block and emit `<span>@label (@timeLabel)</span>`. Don't fight the parser.

---

## 2026-04-23 — DX24 deploy playbook: pack version trumps folder name

**Date:** 2026-04-23
**Owner:** David (Captain) + Copilot
**Status:** ✅ Documented

### Problem

Azure deployment pipeline (.NET 10 container image packed to DX24 ACA) appeared to be stale even though the latest code built. Captain thought the artifact folder name was the source of truth; it's not.

### Decision

The `version.txt` **inside the container** (set at build time by `azd` / `.github/workflows/bicep/app/`; value = `$(PackageVersion)` from the latest .NET SDK build) is the ground truth. Folder name is cosmetic. After deploy, verify:

1. **In Azure:** ACA revision UI shows healthy (green checkmark)
2. **In browser:** Webapp URL (`webapp.livelyforest-b32e7d63.centralus.azurecontainerapps.io`) loads and shows live data

If both pass, the deploy is live **regardless of folder name.** The old folder is just stale history.

### Corollary

Auto-traffic routing can be brutal: if a new revision crashes on startup, Azure will route back to the last healthy revision silently. So **smoke-test the webapp URL directly** after every deploy — don't just assume the new revision won because it was last.

---


## 2026-04-23 — Ad-hoc activity tracking via synthetic plan completions

**Date:** 2026-04-23
**Owner:** David (Captain) + Copilot
**Status:** ✅ Shipped to DX24

### Problem

`ActivityLog` (dashboard day-detail) only showed `DailyPlanItem` completions — "choose my own" sessions (user navigates directly to Translation/Writing/etc. without a plan) produced zero tracking. Captain wanted duration + resource + skill surfaced for freeform practice too.

### Decision

Persist ad-hoc sessions as synthetic `DailyPlanCompletion` rows with `PlanItemId = "adhoc-{guid}"`. Reuse the existing ActivityLog pipeline unchanged, and filter the `adhoc-*` prefix out of plan reconstruction so they don't pollute "Today's Plan" on the dashboard.

### Implementation

- `IProgressService.StartAdHocSessionAsync(PlanActivityType, resourceId, skillId, estimatedMinutes=10)` creates the record. Priority=999, TitleKey=`Activity_{type}`.
- `ProgressService.ReconstructPlanFromDatabase` filters `!c.PlanItemId.StartsWith("adhoc-")` so the dashboard only sees real plan items.
- `GetActivityLogAsync` intentionally reads ALL completions so ad-hoc rows show up in day detail.
- `IActivityTimerService.StartSession(activityType, activityId?, resourceId?, skillId?)` — when `activityId` is null/empty, auto-creates the ad-hoc record and starts the stopwatch.
- `PlanSummaryCard` detects `plan.Items.All(i => i.PlanItemId.StartsWith("adhoc-"))` and renders the cluster with a ✨ icon + "Freeform practice" label instead of "Plan N". Duration shown as `N min` (no estimate denominator since ad-hoc has no committed target).
- All 10 activity razor pages now call `StartSession` unconditionally (was `if (!string.IsNullOrEmpty(PlanItemId)) …`).

### Gotchas / lessons

- **`PlanActivityType` enum values** are narrower than the activity page set. Valid: `VocabularyReview, Reading, Listening, VideoWatching, Shadowing, Cloze, Translation, Writing, SceneDescription, Conversation, VocabularyGame`. **No** `VocabularyMatching, HowDoYouSay, WordAssociation, MinimalPairs`. VocabMatching was passing `"VocabularyMatching"` which silently failed `Enum.Parse` — fix was to use `"VocabularyGame"` (matches the `/vocabulary-matching` route's enum mapping).
- HowDoYouSay / WordAssociation / MinimalPairs pages still exist; they won't record ad-hoc rows because there's no enum value. `ActivityTimerService.StartAdHocThenLoadAsync` logs a warning and runs the timer without persistence in that case — acceptable fallback until the enum is widened.
- Plan clustering groups completions within 60s of each other. An ad-hoc session started mid-day will form its own cluster — hence the visual differentiator. Don't try to force ad-hoc rows into the user's morning plan cluster.
- **Resource/skill query param naming is NOT uniform** across pages. Captured:
  - `ResourceIdParam` (singular): Translation, Writing, Cloze, Reading, Shadowing, Conversation, Scene, VideoWatching
  - `ResourceIdsParam` (plural, comma-separated): VocabQuiz, VocabMatching
  - No skill: VocabMatching, VideoWatching
  - For ad-hoc persistence of plural-resource activities, take the **first** id: `ResourceIdsParam?.Split(',').FirstOrDefault()`.

### Razor parser quirk (worth remembering)

Nested `@if/@else` with string-interpolated text containing parens around a method chain (e.g. `@Localize["Key"] (@plan.GeneratedAt.ToLocalTime().ToString("h:mm tt"))`) throws `CS1002: ; expected`. Workaround: pre-compute strings in a `@{ }` block and emit `<span>@label (@timeLabel)</span>`. Don't fight the parser.

---

## 2026-04-23 — DX24 deploy playbook: pack version trumps folder name

**Date:** 2026-04-23
**Owner:** David (Captain) + Copilot
**Status:** ✅ Documented in `docs/deploy-runbook.md`

### Context

Hit a false "blocker" during DX24 publish: build errored with `This version of .NET for iOS (26.2.10191) requires Xcode 26.2. The current version of Xcode is 26.3` even after swapping `global.json` to .NET 11 preview 3. I concluded (wrong) that no Xcode-26.3-compatible pack existed because every iOS SDK folder was named `*_26.2`. Captain correctly pointed out this setup HAD just worked earlier in the same session.

### What was actually true

- Folder name (`Microsoft.iOS.Sdk.net11.0_26.2`) reports SDK GENERATION, not Xcode requirement.
- The **pack version** inside it tells the real story:
  - `26.2.10xxx` (no suffix) = net10 pack, Xcode 26.2 only
  - `26.2.11xxx-net11-pN` = net11 preview pack, Xcode 26.3 compatible (e.g. `26.2.11588-net11-p3`)
- The error referenced `26.2.10191` — a net10 pack — which means SDK resolution had fallen back to the net10 workload for `net10.0-ios` TFM. The fix was to simply retry the build under net11 preview 3 SDK; it picked the correct net11-p3 pack on the second try.

### Rules going forward

1. **Don't declare "blocker" when an environment previously worked in the same session** — retry and verify first.
2. When reading iOS SDK pack errors, read the **full version string**, including the `-net11-pN` suffix. Pack version disambiguates; folder name doesn't.
3. Transient `devicectl` errors (`Socket is not connected`, `ControlChannelConnectionError`) — wait a few seconds and retry. Don't treat as a hard failure.
4. `FBSOpenApplicationErrorDomain error 7 (Locked)` on launch = phone is locked. Install succeeded; tell Captain to unlock and tap the icon. Not a build/install failure.

`docs/deploy-runbook.md` step 2a now documents the pack-version vs folder-name distinction, and the Common Issues table covers the transient/locked cases.

---

## 2026-04-18 — Phase 1 Display Language Restoration (Complete)

**Date:** 2026-04-18  
**Owner:** Zoe (Lead), Kaylee (Frontend), Wash (Backend), Jayne (Tester)  
**Status:** ✅ COMPLETE — All P0 scenarios pass E2E  
**Tracking:** Phase 1 closed, Phase 2 & Phase 3 backlogged

### Summary

Display Language feature was broken after MauiReactor → Blazor migration. Captain reported: changing language on Profile doesn't update any app strings. Phase 1 scope: restore end-to-end localization infrastructure + localize NavMenu + Profile pages. Korean as priority language. Spanish/French deferred to Phase 2.

**Root causes identified:**
1. Profile save never calls `LocalizationManager.SetCulture` (dead code path)
2. Blazor UI is ~99% hardcoded English literals; only "Help" in NavMenu uses localization
3. No startup culture wiring (`AddLocalization()` / `UseRequestLocalization` missing)
4. Resx file (488 keys) orphaned from Blazor rendering path

### Phase 1 Architecture

**Blazor WebApp path:** Scoped `BlazorLocalizationService` per-circuit (no cross-user leak). On startup, `UseRequestLocalization` reads `.AspNetCore.Culture` cookie (set by last Profile save). `BlazorLocalizationService` holds its own `CultureInfo`, reads resx via new `LocalizationManager.GetString(key, culture)` overload, raises `CultureChanged` event for components to re-render.

When user saves Profile: (1) flips circuit's culture via `Localize.SetCulture`, (2) components subscribed to `CultureChanged` re-render immediately, (3) navigates with `forceLoad:true` to `/account-action/SetCulture?culture=ko&returnUrl=/profile`, which writes `.AspNetCore.Culture` cookie and redirects back—next request everything is Korean.

**MAUI Blazor Hybrid path:** On launch, `LocalizationInitializer` (IMauiInitializeService) reads saved `UserProfile.DisplayLanguage`, calls `LocalizationManager.Instance.SetCulture(culture)`. This sets process-wide `DefaultThreadCurrentUICulture` (fine in single-user client). When user saves from Profile, same sequence runs without cookie redirect (single-user process).

**Database:** `UserProfile.DisplayLanguage` column exists and is migrated (verified Round 1 of this session). No schema risk.

**Culture identifier alignment (critical):** All five touchpoints use neutral `ko` (not `ko-KR`):
- DB: `UserProfile.DisplayLanguage` stores `"ko"`
- Cookie: `.AspNetCore.Culture=c=ko|uic=ko`
- Whitelist: `Program.cs` `SupportedCultures = [new("en"), new("ko")]`
- Endpoint validator: `/account-action/SetCulture` whitelist is `["en", "ko"]`
- Resx: `AppResources.ko.resx` + `AppResources.ko-KR.resx` renamed to `AppResources.ko.resx` (via Wash Round 2)

ResourceManager fallback chain: `ko` → invariant → throws. Previous state had `ko` in code/cookie/DB but `ko-KR` in resx, so fallback walked `ko` → invariant, missing the satellite. Wash's Round 2 fix aligns all five.

**Resx manifest (`<LogicalName>` override, critical):** `AppResources.Designer.cs` hardcodes neutral resource path as `"SentenceStudio.Resources.Strings.AppResources"`, but MSBuild's default embeds as `"SentenceStudio.Shared.Resources.Strings.AppResources"` (includes assembly name). Mismatch → `MissingManifestResourceException` at runtime. Wash's Round 1 fix added `<LogicalName>` override to `.csproj` to force correct stream name. This latent bug was weaponized by Kaylee's increased use of `Localize[]`.

### Files Changed

**Code (Kaylee):**
- `src/SentenceStudio.UI/Services/BlazorLocalizationService.cs` — rewritten scoped, holds `_culture`, raises `CultureChanged`, no process-wide mutation on web path
- `src/SentenceStudio.UI/Services/BlazorUIServiceExtensions.cs` — Singleton → Scoped
- `src/SentenceStudio.MacOS/MacOSMauiProgram.cs` — Singleton → Scoped
- `src/SentenceStudio.Shared/Common/LocalizationManager.cs` — public `GetString(key, CultureInfo?)`
- `src/SentenceStudio.AppLib/Setup/LocalizationInitializer.cs` (NEW)
- `src/SentenceStudio.AppLib/Setup/SentenceStudioAppBuilder.cs`
- `src/SentenceStudio.WebApp/Program.cs`
- `src/SentenceStudio.WebApp/Auth/AccountEndpoints.cs`
- `src/SentenceStudio.UI/Layout/NavMenu.razor` — 11 `Nav_*` keys
- `src/SentenceStudio.UI/Pages/Profile.razor` — 30 `Profile_*` keys
- `src/SentenceStudio.Shared/Resources/Strings/AppResources.resx` — +56 keys (en)
- `src/SentenceStudio.Shared/Resources/Strings/AppResources.ko.resx` — +56 Korean (renamed)

**Csproj (Wash):**
- `src/SentenceStudio.Shared/SentenceStudio.Shared.csproj` — `<LogicalName>` overrides + culture filename rename

**Squad artifacts:**
- `.squad/skills/blazor-localization/SKILL.md` (NEW — Kaylee documented pattern for reuse)

### E2E Testing (3 Rounds)

**Round 1 (Jayne):** REJECT — `MissingManifestResourceException` on every page. Fixed by Wash (R1 manifest).

**Round 2 (Jayne):** REJECT — Page loads, but UI stays English despite `ko` cookie. Fixed by Wash (R2 culture rename).

**Round 3 (Jayne):** ✅ APPROVE — All P0 scenarios pass.
- Scenario 1: Set Display Language → Korean, NavMenu + Profile flip to Hangul (대시보드 / 활동 / 학습 자료 / 어휘 / ... / 프로필 / 설정 / 피드백 / 로그아웃)
- Scenario 2: Revert to English, UI reverts cleanly
- Scenario 3 (P0): Cross-user isolation — Browser A (Korean circuit), Browser B (fresh cookie) stays English. Scoped service architecture confirmed sound. No cross-circuit leak.

**Confidence:** 100% — live runtime validation, multiple language switches, simultaneous browser isolation.

### Decisions

1. **Use scoped `BlazorLocalizationService`** (not singleton) — each circuit gets its own `CultureInfo`, read from cookie at request start. MAUI path additionally calls `LocalizationManager.Instance.SetCulture` for process-wide state (acceptable in single-user client).
2. **Cookie round-trip via `/account-action/SetCulture` endpoint** — SignalR cannot write HTTP cookies directly; endpoint pattern matches existing `/SignOut` / `/AutoSignIn` infrastructure.
3. **Use neutral `ko` culture identifier everywhere** — all five touchpoints (DB, cookie, whitelist, endpoint, resx) must match. ResourceManager fallback chain expects parent-language resources.
4. **`<LogicalName>` override in `.csproj` is required** — latent bug exposed by mass-localization. Designer hardcodes neutral resource path; csproj config forces correct embed stream name.
5. **Phase 2 backlog: Dashboard + remaining pages + es/fr/ja/zh resx** — Phase 1 proves infrastructure, Phase 2 is content localization.
6. **Tech debt (5 follow-ups):**
   - Culture cookie needs `HttpOnly=true` + `Secure` flags
   - `/SetCulture` GET endpoint is CSRF-able by construction (impact bounded, acceptable for Phase 1)
   - `Toast.ShowSuccess` on web path swallowed by `forceLoad:true` redirect (UX nit)
   - Startup `LocalizationInitializer` uses blocking sync-over-async (matches existing patterns)
   - Legacy resx keys (`Dashboard`, `Settings`) orphaned after Phase 2 — Phase 3 cleanup
7. **MauiReactor residue (LocalizationManager, LocalizeExtension, FilterChip) stays** — Phase 3 cleanup per Captain's "don't touch MauiReactor residue" directive.

### Reviewer Lockout Enforcement

Kaylee was locked out after Zoe's code review approval. Both Wash hotfixes honored the lockout:
- Round 1 (manifest fix): Wash applied, Kaylee did NOT touch
- Round 2 (culture rename): Wash applied, Kaylee did NOT touch

Lockout was per-artifact (Phase 1 implementation code), not per-agent.

### Next

- Push Phase 1 to Captain for `/review` gate
- Phase 2 tracking issue: localize Dashboard welcome card + remaining Blazor pages + add es/fr/ja/zh resx
- Phase 3 tracking issue: retire orphaned keys + MauiReactor residue cleanup

---

## 2026-04-18 — Diagnosis: MacCatalyst Post-Login Splash Hang

**Date:** 2026-04-18  
**Owner:** Copilot (Coordinator)  
**Status:** Pending Implementation  
**Tracking:** `maccatalyst-forceload-fix` (todo)

### Context

Captain logged in via MacCatalyst, got stuck on dark-navy splash (translation icon only). Dashboard never loaded.

### Root Cause

`LoginPage.razor:130` and `RegisterPage.razor:172` call `NavManager.NavigateTo(returnUrl, forceLoad: true)` after successful login. In BlazorHybrid (MAUI), `forceLoad: true` forces a full WebView document reload of `app://0.0.0.0/`. The WebView reloads but the Blazor component tree does not cleanly re-bootstrap its auth state + cascade, leaving the user on a rendered-but-empty layout.

### Evidence

- API structured logs: `/api/auth/login` returned 200 with JWT; zero follow-up API calls from Dashboard → no component initialization
- macOS unified log: WebKit alive and looping every 2s, but no Blazor component render events
- Code inspection: `LoginPage.razor:126 & 130` and `RegisterPage.razor:172` all use `forceLoad: true`; pattern identical to `NavMenu.razor:106-107` (isWeb detection already established)

### Fix (Durable, ~2 Files)

1. `LoginPage.razor` + `RegisterPage.razor`: platform-gate `forceLoad` using `isWeb` pattern from `NavMenu.razor`
2. MAUI branch: call `MauiAuthenticationStateProvider.LogInAsync()` (triggers `NotifyAuthenticationStateChanged`) + `NavigateTo(returnUrl)` WITHOUT `forceLoad`
3. Web branch: keep existing `forceLoad: true` (cookie-backed auth survives reload)

### Immediate Unblock (No Code Change)

Restart `maccatalyst` resource from Aspire dashboard. On cold start, `MauiAuthenticationStateProvider` fast-paths to authenticated state from `SecureStorage` → Shell routes to Index normally → Dashboard loads.

### Recommendation

Apply durable fix in next session. High-risk changes (auth + nav) warrant full `/review` gate before merge.

---

## 2026-04-17 — Help Flyout Menu Item for MAUI Hybrid

**Date:** 2026-04-17  
**Owner:** Zoe  
**Status:** Implemented  
**Commit:** 8d71a41

### Context

Captain ordered: "Wire the Help trigger into the SentenceStudio app as a flyout/sidebar menu item."

SentenceStudio is a Blazor Hybrid app:
- MAUI apps (iOS/MacCatalyst) use `BlazorWebView` hosting `SentenceStudio.UI` (Razor class library)
- Standalone WebApp (ASP.NET Core) uses the same `SentenceStudio.UI` components
- HelpKit (Plugin.Maui.HelpKit) is registered ONLY in MAUI apps via `UseHelpKit()` in `MauiProgram.cs`
- WebApp does NOT have HelpKit (it's a MAUI-only library)

The challenge: `SentenceStudio.UI` is a plain Razor class library (`<TargetFramework>net10.0</TargetFramework>`, `<SupportedPlatform>browser</SupportedPlatform>`) and CANNOT reference MAUI packages.

### Decision

1. **Add Help menu item to `SentenceStudio.UI/Layout/NavMenu.razor`** (used by both MAUI and WebApp)
2. **Use dynamic type resolution** to keep the UI project portable:
   - Check at runtime if `Plugin.Maui.HelpKit.IHelpKit` is registered
   - Invoke `ShowAsync()` via reflection if available
   - Gracefully hide the Help button when HelpKit is not registered (WebApp)
3. **Add localization keys** ("Help" / "도움말") to `AppResources.resx` + Korean
4. **Use Bootstrap icon** `bi-question-circle` (no custom icon needed)

### Implementation

```razor
@inject IServiceProvider Services
@inject BlazorLocalizationService Localize

<a class="nav-link nav-link-ss rounded px-3 py-2"
   href="" @onclick="ShowHelpAsync" @onclick:preventDefault>
     <i class="bi bi-question-circle"></i> <span class="nav-label">@Localize["Help"]</span>
</a>

@code {
    private bool _isHelpKitAvailable;

    protected override void OnInitialized()
    {
        var helpKitType = Type.GetType("Plugin.Maui.HelpKit.IHelpKit, Plugin.Maui.HelpKit");
        _isHelpKitAvailable = helpKitType is not null && Services.GetService(helpKitType) is not null;
    }

    private async Task ShowHelpAsync()
    {
        if (!_isHelpKitAvailable) return;
        
        var helpKitType = Type.GetType("Plugin.Maui.HelpKit.IHelpKit, Plugin.Maui.HelpKit");
        var helpKit = Services.GetService(helpKitType);
        var showMethod = helpKitType.GetMethod("ShowAsync");
        await (Task)showMethod.Invoke(helpKit, [default(CancellationToken)])!;
        await CloseOffcanvas();
    }
}
```

### Why Dynamic Resolution?

**Alternative A (rejected):** Add MAUI package reference to `SentenceStudio.UI`
- ❌ Breaks WebApp build (MAUI libs incompatible with browser target)
- ❌ Forces UI project to become multi-targeted
- ❌ Pollutes shared UI with platform concerns

**Alternative B (chosen):** Dynamic resolution via reflection
- ✅ UI project stays plain Razor class library (browser-only)
- ✅ Works in both MAUI (HelpKit present) and WebApp (HelpKit absent)
- ✅ No compile-time dependency on MAUI packages
- ✅ Graceful degradation (Help button hidden when HelpKit not available)

### Files Changed

- `src/SentenceStudio.UI/Layout/NavMenu.razor` — Help button + reflection logic
- `src/SentenceStudio.Shared/Resources/Strings/AppResources.resx` — "Help" key
- `src/SentenceStudio.Shared/Resources/Strings/AppResources.ko-KR.resx` — "도움말" key

### Result

✅ Help menu item appears at bottom of sidebar in MAUI apps (iOS/MacCatalyst)  
✅ Tapping it invokes `IHelpKit.ShowAsync()` → opens HelpKit overlay  
✅ WebApp build unaffected (UI project remains browser-only)  
✅ No emojis used (Bootstrap icon `bi-question-circle` only)  
✅ Localized in English + Korean  
✅ Build succeeded: 0 errors, 97 warnings (pre-existing)

---

## 2026-04-17 — Plugin.Maui.HelpKit Alpha Build Complete

### Public API (Zoe)
Contract frozen at 0.1.0-alpha. `HelpKitOptions`, `IHelpKit` (five methods: Show/Hide/ClearHistory/Ingest/StreamAskAsync), `IHelpKitPresenter`, `IHelpKitContentFilter` (DefaultSecretRedactor). All registrations use `TryAddSingleton` for host customization.

### RAG Pipeline (River)
Wave 1 code landed: `MarkdownChunker` (512/128 token, paragraph-boundary, GitHub slug anchors), `PipelineFingerprint` (SHA-256 of model+chunker+size+overlap), `CitationValidator` (regex parse + fallback to path-only), `SimilarityThresholds` (per-model table, 0.35–0.75 cosine), `SystemPrompt` (delimiter-fenced docs, grounding rules, citation format), `PromptInjectionFilter` (output fingerprint leak detector). Chunker version, threshold table, and phrase set are locked into design doc.

### Storage, Ingestion, Rate Limit & Diagnostics (Wash)
SQLite via sqlite-net-pcl (singular table names, string GUID PKs, UTC ticks). Pipeline fingerprint gates re-ingest; answer cache (7-day TTL) invalidated wholesale on fingerprint change. Rate limiter: concurrent sliding window, 10 q/min default, per-user buckets. Vector store: hand-rolled in-memory + JSON persistence (no Microsoft.Extensions.VectorData concrete pkg in Alpha). Diagnostics: `HelpKitMetrics` exposes counters (ingest, retrieval, LLM tokens, cache, rate limit). Scanner: non-AI XAML walker emitting one `.md` per page (title, route, field names); AI enrichment deferred to Beta.

### UI & Presenters (Kaylee)
`HelpKitPage` (CollectionView chat, streaming mutation, auto-scroll). `DefaultPresenterSelector` resolves at call-time (Shell first, Window fallback). `HelpKitLocalizer` (embedded JSON, en/ko, fallback to key). Accessibility: SemanticProperties + AutomationProperties set; message bubbles include role in a11y name; input disabled while streaming; no color-only role distinction. Twelve localization keys shipped; runtime theme switching deferred. Shell flyout helper wired via `IMauiInitializeService`.

### Samples (Kaylee)
Three runnable projects (Shell, Plain, MauiReactor) with shared stubs. `StubChatClient` (deterministic 4-answer router, 30ms streaming delay, citation tokens). `StubEmbeddingGenerator` (32-dim FNV-1a hash vectors, stable). `SampleHelpContentInstaller` (copies bundled `.md` to AppData on first run). Each sample includes provider-migration comments (Azure OpenAI, OpenAI, Ollama, unkeyed).

### Eval Harness & CI Gate (Jayne)
30 golden Q/A over SentenceStudio help corpus. CI gate: **>= 85% correct AND 0 fabricated citations**. Two modes (deterministic `FakeChatClient` default, live opt-in `HELPKIT_EVAL_LIVE=1`). Seven unit-test files landed: CitationValidator, MarkdownChunker, PipelineFingerprint, PromptInjectionFilter, RateLimiter, AnswerCache, extended DefaultSecretRedactor. Four-TFM smoke-test checklists. 80% line coverage enforced on Rag, Storage, RateLimit, DefaultSecretRedactor folders.

### CI + Documentation (Zoe)
`.github/workflows/helpkit-ci.yml`: matrix build (macOS+iOS, Linux+Android, Windows), unit tests, eval gate. `DOTNET_ROLL_FORWARD=LatestMajor`, net11 preview SDK + prerelease flag. `README.md`: MIT license, honest FAQ (offline caveat, no-hallucination caveat, citations are validated), provider-neutral quickstart (four examples), explicit Alpha defects list. `SUPPORT.md`, `SECURITY.md`, `CONTRIBUTING.md` drafted. `EXTRACT-RUNBOOK.md`: post-Alpha path to standalone repo via `git subtree split`.

### SentenceStudio Dogfood Integration (Wash) — BLOCKED
Code staged dormant under `#if NET11_0_OR_GREATER`. Eleven help articles (Getting Started, Dashboard, Profile, Sync, Settings, six activities). DI wiring: unkeyed + keyed aliases (no-op today). Embedding model: `text-embedding-3-small` from existing OpenAI client. Per-profile history via `Preferences.Get("active_profile_id")`. Content in AppData, copied from bundle on first run. **Blocker: net10 head cannot reference net11-only library.** Three unblock options: (1) Bump SentenceStudio to net11 dev (counter to Captain's intent). (2) Multi-target HelpKit to also include net10 TFMs (Zoe's call; recommended by Wash). (3) Activate only on iOS Release publish path (dev loop dogfooding lost). **Wash recommends Option 2**: lowest-risk, keeps Captain's net10 workflow, enables dogfooding in real builds, reversible at Alpha close.

---

## 2026-04-17 — Plugin.Maui.HelpKit Planning

### 2026-04-17T20:21Z: Plugin.Maui.HelpKit — Alpha scope locked (Captain verdicts)
**By:** Captain (David Ortinau) via Squad coordinator
**Context:** Plan v2 open questions answered, Alpha scope now frozen.

**Decisions:**
1. **UI pivot confirmed** — Native MAUI chat (CollectionView + streaming) is PRIMARY for Alpha. BlazorWebView deferred to post-Alpha optional companion package.
2. **Incubation confirmed** — Develop inside `lib/Plugin.Maui.HelpKit/` in SentenceStudio until end of Alpha. Extract to standalone repo at Alpha close via `git subtree split`.
3. **Storage default confirmed** — `Microsoft.Extensions.VectorData` in-memory + JSON disk persistence. `sqlite-vec` fully deferred to v1 (weeks of native-build work; not Alpha-worthy).
4. **License: MIT.**
5. **AI provider ownership: host app brings the `IChatClient` AND `IEmbeddingGenerator`.** HelpKit does NOT ship, bundle, or recommend a specific model. Samples in SentenceStudio demonstrate wiring to the Captain's existing Foundry-hosted model. README documents "bring your own M.E.AI client" with examples for OpenAI, Azure OpenAI, Foundry, Ollama. No MiniLM ONNX shipping.
6. **Stub scanner: shipped in Alpha.** Non-AI page scanner that emits one `.md` per detected XAML/MauiReactor page (title + route + field names). AI-enriched scanner stays in Beta.
7. **TFMs: `net11.0-*` MAUI targets.** net9 is out of support imminent; Captain is all-in on net11 previews. If community demand surfaces for net10, we can multi-target at Alpha close — but primary target is net11.
8. **Rate limit default: 10 questions/min**, configurable via `HelpKitOptions.MaxQuestionsPerMinute`.

**Implications:**
- R1 (sqlite-vec) is officially shelved for Alpha → gate-zero SPIKE-1 drops the sqlite-vec variant entirely; focus purely on native-first + in-memory VectorData Release-on-device.
- R3 (BlazorWebView) is officially shelved for Alpha → no Blazor spike needed.
- Embedding-dimension handling (Skeptic H1) still requires SPIKE-1 validation since dev-provided embedding generator means dimension is not fixed at package time. Pipeline fingerprint gates re-ingest on model/dimension change.
- net11 preview TFM means CI must use the net11 preview SDK; document global.json handoff for the standalone repo.
- "Bring your own client" messaging becomes central in README alongside the honesty fixes.

**Next:**
- SPIKE-1 and SPIKE-2 unblock (gate-zero).
- Zoe updates plan.md with net11 TFM and "app owns the model" framing.
- README draft incorporates MIT + BYO-IChatClient.


---

## 2026-04-17 — .NET SDK Detection Skill (Reframed)

**Author:** Squad Coordinator  
**Captain Directive:** "the skill I wanted you to write was about how you should determine what version of .net you have installed to use, which ought to include awareness of global.json as a thing that you might encounter. So it should be useful in any .net project environment."

### What Changed

1. **Renamed:** `.squad/skills/dotnet-sdk-pinning/` → `.squad/skills/dotnet-sdk-detection/` with complete `SKILL.md` rewrite.
   - New scope: generic 100-level skill for ANY .NET project — "which SDK is the CLI actually picking?"
   - 4-layer mental model: installed SDKs vs. selected SDK vs. workload manifests vs. project TFMs
   - `global.json` as ONE input among several (env vars, `.gitignore` status, walk-up behavior), not headline
   - Includes "newer SDK can build older TFMs" rule with MAUI-workload + Xcode wrinkles
   - Worked example anonymized (no agent names)

2. **Corrected AGENTS.md:** Replaced "## SDK Pinning via global.json" with "## .NET SDK Selection in This Repo" — accurate facts:
   - `global.json` is gitignored (`.gitignore:412–414`), per-machine, NOT a project convention
   - Captain pins net10 locally because default machine SDK is net11 preview; pin makes `dotnet` commands use matching net10 SDK + workload
   - Other contributors / CI / fresh checkouts do NOT need `global.json`
   - Publish-workflow `global.json` swap is iOS-only, Xcode 26.3-driven (per `docs/deploy-runbook.md` Step 2a) — Azure uninvolved

### Why Prior Version Was Wrong

Earlier iterations guessed at rationale (workload alignment / Azure can't host preview). Captain rejected both with "if you cannot explain it, it's just an impediment we don't need." Real story found in `docs/deploy-runbook.md` Step 2a (Xcode 26.3 mismatch) and `.gitignore:412–414` (file never committed).

### Scope

- `.squad/skills/dotnet-sdk-detection/SKILL.md` — new file (rewrite)
- `.squad/skills/dotnet-sdk-pinning/` — deleted (renamed)
- `AGENTS.md` — section replaced
- No code/csproj/global.json changes

### Standing Rule (Enforced Going Forward)

Before agents write "X SDK isn't installed" / "we need to multi-target" / "build using wrong framework," they MUST run diagnostic order in `.squad/skills/dotnet-sdk-detection/SKILL.md`. Routing-relevant skill — surface in spawn prompts when work touches `dotnet build/restore/test/publish`.


---

## 2026-04-18/04-19 — Phase 2 Blazor Localization (COMPLETE — 1,024 Keys, 40+ Files)

**Date:** 2026-04-18 through 2026-04-19  
**Owner:** Kaylee (Lead), Zoe (Architecture), Wash (Infrastructure), Jayne (E2E)  
**Status:** ✅ COMPLETE — 10 commits shipped on `main` (unpushed, awaiting Captain's `/review`)  
**Tracking:** Phase 2 closed; Phase 3 (es/fr/ja/zh) backlogged

### Summary

Phase 2 expanded on Phase 1's display-language restoration by localizing 99% of hardcoded Blazor UI strings to Korean. Scale: 1,024 new resx keys added to both `AppResources.resx` and `AppResources.ko.resx`, distributed across Dashboard, Activity Pages (14 variants), Management Pages (Resources, Vocabulary, Skills), Auth (Login/Register/Onboarding), and Shared Components. All infrastructure from Phase 1 proved stable under 18x key volume increase (Phase 1: 56 keys, Phase 2: 1,024 keys).

### Batches Shipped

| Batch | Files | Keys | Focus | Commits |
|-------|-------|------|-------|---------|
| Batch 1 | Dashboard, ActivityLog, MainLayout | 118 | High-traffic landing pages + sync UI | 9543146 |
| Batch 2 | 14 Activity Type pages (Quiz, Reading, Writing, Conversation, etc.) | 350 | Core engagement features | 4afd2c2, 844326d |
| Batch 3 | Resources, Vocabulary, Skills, Settings | 400 | Management + configuration | fa78ea4, ec0ab9d, a0b41f8 |
| Batch 4 | Auth (Login/Register/Forgot) + Shared Components | 156 | Entry point + reusable UI | 3ce28d7 |

**Commits:**
- `9543146` — Batch 1 (Dashboard, ActivityLog, MainLayout)
- `bd57ce2` — Batch 1 progress note + Kaylee history update
- `4afd2c2` — Batch 2 PARTIAL (7 of 14 activity pages)
- `844326d` — Batch 2 FINISH (remaining 7 activity pages)
- `fa78ea4` — Batch 3 Part 1 (Skills, Resources)
- `ec0ab9d` — Batch 3 Part 2 (ResourceAdd, Settings)
- `a0b41f8` — Batch 3 FINISH (Vocabulary, VocabularyWordEdit, ResourceEdit)
- `3ce28d7` — Batch 4 (Auth + Shared Components)
- `9280f9e` — Phase 2 Summary doc
- `9a49f8c` — Move Phase 2 Summary to `docs/` per Captain's rule

**Prior unpushed commit (Phase 1 follow-up):** `f8ff7ad` (locale load-time fix)

### Key Architectural Decisions (Locked In From Phase 1, Validated at Scale)

1. **Enum-driven localization keys (NOT AI-generated string keys):** Activity pages switch on typed enums (`PlanActivityType`, `ActivityCategory`) to determine resx keys, never on user-provided string keys. Prevents PascalCase/snake_case collision. Example:
   ```csharp
   string label = item.ActivityType switch
   {
       PlanActivityType.VocabReview => Localize["PlanItemVocabReviewTitle"],
       PlanActivityType.Reading => Localize["PlanItemReadingTitle"],
       // ...
   };
   // NOT: Localize[item.TitleKey] (would be "plan_item_vocab_review_title" and miss the resource)
   ```

2. **Naming conventions (locked Phase 1, validated Phase 2 at scale):**
   - `PageName_*` — page-specific strings (unique to one page)
   - `Common_*` — shared across 3+ pages (Save, Cancel, Delete, etc.)
   - No collision with legacy unprefixed keys (`Save`, `Reading`, `OK`, `Refresh` reserved for MauiReactor)

3. **Razor quote-nesting gotcha:** `title="@Localize["Key"]"` is mangled by edit tool to `title="@Localize[" Key "]"`. Use single-quoted outer: `title='@Localize["Key"]'`. Also safe in `@(...)` C# expressions.

4. **CultureChanged subscription pattern (inherited from Phase 1, proved at scale):** Every localized `.razor` component needs:
   ```razor
   @implements IDisposable
   @code {
       protected override void OnInitialized() => Localize.CultureChanged += OnCultureChanged;
       void OnCultureChanged() => InvokeAsync(StateHasChanged);
       void IDisposable.Dispose() => Localize.CultureChanged -= OnCultureChanged;
   }
   ```

5. **Shared components require per-consumer wiring:** Components like `ActivityTimer`, `WhatsNewModal` used by multiple pages must subscribe to `CultureChanged` in every consumer page — wiring in the shared component alone is insufficient (parent needs its own `StateHasChanged` hook).

6. **Expression-bodied properties beat mutable fields:** Use `string Title => Localize["Key"]` instead of `string _title; void OnCultureChanged() { _title = Localize["Key"]; }`. Cleaner, less state, fewer bugs.

### Tooling & Process (Proven Reusable)

**Batch automation script:** `scripts/i18n-work/add_keys.py` + `batchN.json`
- Reads JSON tuples: `{ key, en, ko, comment }`
- Appends to both `.resx` and `.ko.resx` files
- De-dupes by key
- Reusable for Phase 3 (es/fr/ja/zh)

**Build gate (required before commit):**
```bash
dotnet build src/SentenceStudio.WebApp/SentenceStudio.WebApp.csproj
```
Catches missing key references. Phase 2 achieved 0 errors, 151 pre-existing warnings.

**Commit format (per Captain):**
```
feat(i18n): Phase 2 Batch N — {area} strings to Korean
- Adds {N} keys to AppResources.resx + AppResources.ko.resx
- Localizes {files}…
Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>
```

### E2E Testing (Spot Checks, Not Full Regression)

Jayne verified Batch 1 on Blazor WebApp:
- Language toggle (Profile → Display Language) switches NavMenu + Dashboard to Korean
- Revert to English works cleanly
- No cross-user culture bleed in multi-session browser tests

E2E coverage: high-traffic pages (Dashboard, ActivityLog) + representative activity types (VocabQuiz, Reading, Writing) + auth flow (Login → Display Language change).

### Process-Wide Culture Mutation Gotcha (Carryover From Phase 1)

**MAUI (single-user client):** Safe to set `DefaultThreadCurrentUICulture` process-wide. Single user, so no isolation risk.

**Blazor Server (multi-user):** NEVER set process-wide culture. Use scoped `BlazorLocalizationService` per circuit, each holding its own `CultureInfo`. Phase 1 lock-in remains in effect for Phase 2: Blazor Server uses scoped service; MAUI Blazor Hybrid client uses `LocalizationManager.Instance.SetCulture()` (process-wide, acceptable in single-user app).

### Tech Debt (Phase 3 Backlog, Not Blocking Phase 2)

From Phase 1, carried forward and validated at Phase 2 scale:
1. **Culture cookie hardening:** Add `HttpOnly=true` + `Secure` flags to `.AspNetCore.Culture` cookie (currently `HttpOnly=false`, `Secure=false`)
2. **CSRF on GET `/SetCulture` endpoint:** Current implementation is CSRF-able; impact bounded (low sensitivity of culture setting), acceptable for Alpha but needs hardening for production (consider POST + CSRF token in Phase 3)
3. **Toast timing regression:** `Toast.ShowSuccess` on WebApp path is swallowed by `forceLoad:true` redirect during Profile save; UX nit, deferred
4. **Async-over-sync in `LocalizationInitializer`:** Matches existing patterns but should be audited in Phase 3
5. **Legacy unprefixed keys cleanup:** Phase 1 orphaned keys (`Dashboard`, `Settings`, `Refresh` from old pattern) unused by Phase 2 code; Phase 3 cleanup task

### Files Changed (Core Localization)

**Razor files** (40+ modified):
- Dashboard: `Index.razor`, `ActivityLog.razor`
- Activity pages: 14 variants (Conversation, Cloze, Writing, Translation, WordAssociation, VocabQuiz, VocabMatching, Shadowing, Reading, Scene, HowDoYouSay, MinimalPairs, MinimalPairSession, VideoWatching)
- Management: Resources, ResourceAdd, ResourceEdit, Vocabulary, VocabularyWordEdit, Skills, Settings
- Auth: LoginPage, RegisterPage, ForgotPasswordPage, Onboarding, Import, Feedback, DebugHealth
- Shared components: ActivityTimer, WhatsNewModal, UpdateAvailableBanner, PlanSummaryCard, ChannelDetail, MinimalPairCreate

**Resx files:**
- `AppResources.resx` — +1,024 keys (en)
- `AppResources.ko.resx` — +1,024 keys (ko)

### Decisions

1. **Enum-based key selection is mandatory for activity types** — no exceptions. Prevents string/enum collision bugs at scale.
2. **`Common_*` namespace promoted ONLY at 3+ uses** — validates at Batch boundaries; reusable keys defined in early batches (Batch 1/2) for downstream reuse.
3. **Single-quoted outer attributes in Razor** — avoids editor mangling of Localize[...] calls.
4. **No MauiReactor localization in Phase 2** — Blazor-only to reduce cognitive load and merge conflicts. MauiReactor cleanup is Phase 3 backlog (Captain's standing directive: "don't touch MauiReactor unless asked").
5. **Build gate required before every commit** — prevents merge of code with missing resx keys.

### Reviewer Lockout Enforcement

Kaylee remained locked after Zoe's Phase 1 code-review approval and shipped Phase 2 solo. Wash was available for infrastructure fixes (manifest, culture rename) during Phase 1; no Wash infrastructure changes required in Phase 2 (Phase 1 fixes stood).

### Handoff

- **To Captain:** 10 commits on `main` ready for `/review` before push (final gate)
- **To Phase 3 backlog:** Spanish/French/Japanese/Chinese resx files (add language support); tech debt follow-ups (HttpOnly/Secure, CSRF, legacy key cleanup)
- **To future maintainers:** Enum-driven keys pattern locked in; reusable tooling (`add_keys.py`) documented in Phase 2 commits

---

## 2026-04-18 — Aspire Orphan Process Cleanup & Culture Cookie Cross-Origin Investigation

**Date:** 2026-04-18  
**Owner:** Squad Coordinator  
**Status:** ✅ COMPLETE — Process tree cleared, investigation closed (by-design behavior confirmed)

### What Happened

Developer noticed Aspire dashboard applying culture changes across multiple localhost ASP.NET Core apps in the same browser session. Example: Set Korean in Aspire dashboard → language persists when switching to WebApp.

### Root Cause

**Browser cookie scope:** `.AspNetCore.Culture` cookie issued by one localhost:* app is visible to all localhost:* apps in the same browser session. This is **by-design browser behavior**, not a Aspire or app bug.

Cookie domain is `localhost` (no port in domain restriction on same-host). When App A sets `.AspNetCore.Culture=c=ko|uic=ko` at localhost:5000, App B at localhost:5001 sees the same cookie on its next request (same domain). The middleware in App B reads the cookie and applies the culture.

### By-Design Determination

This is **NOT a bug** — it's standard browser + HTTP semantics:
1. Multiple services on localhost share the same domain scope
2. If isolation is desired, use separate machines, custom domain names, or accept shared culture as a feature
3. No fix needed for Phase 2; acceptable for Alpha dev environment

### Aspire Process Orphan Cleanup

Separate issue: Orphaned Aspire process tree on port 22070 (15 processes) was blocking subsequent runs. Coordinator cleared via two SIGKILL passes; port now available for next Aspire launch.

### Decision

- **No code change required.** Culture cookie cross-origin sharing is by-design browser behavior.
- **Document for future maintainers:** If developers complain about "unexpected culture change," check browser cookies and dev environment scope (localhost vs separate machines).
- **Phase 3 tech debt:** Consider an environment flag to disable culture cookie for dev scenarios where strict isolation is preferred (ASPNETCORE_CULTURE_COOKIE_ENABLED or similar).


---

## 2026-04-19 — Phase 2 Localization Blocker Fix (Captain Review Pending)

**Date:** 2026-04-19  
**Owner:** Zoe (Lead)  
**Commit:** `b56c1c1` (local, unpushed)  
**Status:** ✅ Fixed locally; awaiting Captain's `/review` + push

### Context

Code review of 12 unpushed Phase 2 localization commits (f8ff7ad..ba84ada) by code-review agent surfaced 2 BLOCKING issues. Kaylee was locked out of revision per **Reviewer Rejection Protocol**. Lead (Zoe) took ownership.

### Blocker 1 — Missing resx keys

**Finding:** Grep diff over all `.razor` files against `AppResources.resx` found **30 missing keys**. (Reviewer memo reported 38; 8 had been added in late Phase 2 batch the reviewer hadn't rebased on.)

**Fix:** Added 30 keys to both `AppResources.resx` (en) and `AppResources.ko.resx` via `scripts/i18n-work/add_keys.py`:
- `Common_*` (9): Email/Password/RememberMe, Create button, error-toast formatters
- `VocabWordEdit_*` (13): Language/Lemma/Tags labels, mnemonic UI, validation + toast
- `ResourceEdit_*` (4): File import status, generation success/failure, skipped-duplicates count
- `Vocabulary_*` (3): Stats badge, bulk-select count, no-match empty state
- `SkillEdit_*` (1): Save-failure toast

All Korean translations idiomatic for context (not literal), matching Phase 1/2 conventions.

### Blocker 2 — Skills.razor missing CultureChanged subscription

**Finding:** Skills.razor does not implement `IDisposable` or subscribe to `CultureChanged` event. Inconsistent with 39 other Phase 2 razor files.

**Fix:** Added `@implements IDisposable`, subscribed in `OnInitializedAsync`, unsubscribed in `Dispose`. Matches pattern used throughout Phase 2.

### Verification

- Build: `dotnet build src/SentenceStudio.WebApp/SentenceStudio.WebApp.csproj -f net10.0` → **0 errors**, 377 warnings (pre-existing)
- Resx XML validated via ET.parse on both files
- Grep diff post-fix: 0 missing keys in en, 0 missing keys in ko
- Designer.cs auto-regenerated (270 new lines), included in commit

### Lockout Enforcement

Kaylee did not touch any file in this revision. All changes authored by Zoe. Consistent with protocol: original author frozen out of fix cycle after reviewer rejection.

### Follow-ups (non-blocking, Phase 3)

1. **Lint rule:** Any `.razor` with `@inject ... BlazorLocalizationService` must have `@implements IDisposable` + `CultureChanged` pair. Roslyn analyzer or shell check in CI.
2. **i18n-diff script:** Automate `grep 'Localize\["[^"]+"\]'` vs resx keys diff; run as commit hook.
3. **ResourceEdit_GenerateFailed dual-use:** Intentionally left with zero `{0}` placeholder, used both bare and with `string.Format(Localize[...], ex.Message)` (extra args silently ignored). If exception detail wanted in future, add separate `ResourceEdit_GenerateFailedWithDetail` key.

### Decision

- Ship as `b56c1c1` on `main` (local only)
- **Do NOT push** — Captain owns push gate via `/review`
- Kaylee stays locked out; Phase 2 batch is Zoe-revised and ready for final review

---

## 2026-04-19 — Production Observability for SentenceStudio API

**Date:** 2026-04-19  
**Author:** Wash (Backend Dev)  
**Status:** Proposed — awaiting Captain review

### Problem

Captain reported intermittent production errors (quiz sentence scoring, feedback submission) and asked whether he can see them in "Aspire on Azure." He cannot — Aspire dashboard is a local-dev tool only. Today on Azure Container Apps we have:

- ✅ stdout/stderr → `ContainerAppConsoleLogs_CL` in Log Analytics `law-3ovvqiybthkb6`
- ✅ Default ASP.NET Core console logger (captures `ILogger<T>` writes)
- ❌ No Application Insights
- ❌ No `UseExceptionHandler` / ProblemDetails middleware
- ❌ No `/health` endpoint mapped
- ❌ OTLP exporter in `ServiceDefaults.ConfigureOpenTelemetry` is gated on `OTEL_EXPORTER_OTLP_ENDPOINT` — unset in prod → OpenTelemetry traces/metrics are generated but go nowhere
- ❌ `/api/v1/ai/chat` and `/ai/chat-messages` return `Results.Problem(...)` with no `logger.LogError` on the catch path → OpenAI failures are invisible unless the ASP.NET Core pipeline emits an unhandled-exception log

Consequence: Captain can't triage "quiz scoring failed this morning" without reading raw container logs and guessing.

### Proposal

Three-part change, landed together in one PR (Wash, ~1 day of work):

**1. Wire Application Insights**
   - Add `Aspire.Azure.Monitor.OpenTelemetry` package reference in `SentenceStudio.ServiceDefaults`
   - In `ConfigureOpenTelemetry`, register `UseAzureMonitor()` if `APPLICATIONINSIGHTS_CONNECTION_STRING` is set
   - In `AppHost.cs`, add `builder.AddAzureApplicationInsights("appinsights")` and `.WithReference(appinsights)` on `api`, `webapp`, and `workers`
   - **Gain:** end-to-end request traces, dependency calls (OpenAI, ElevenLabs, Postgres), unhandled exceptions with stack traces, Application Map view

**2. Unhandled exception middleware + ProblemDetails**
   - In `Program.cs`, before auth: `builder.Services.AddProblemDetails()` + `app.UseExceptionHandler()` + `app.UseStatusCodePages()`
   - **Gain:** every unhandled exception is logged with full stack + request context; clients get structured ProblemDetails

**3. Try/catch + `LogError` in AI endpoints**
   - `/api/v1/ai/chat`, `/ai/chat-messages`, `/ai/analyze-image` wrap `GetResponseAsync` call with try/catch + structured logging (prompt hash, user-profile-id)
   - **Gain:** OpenAI failures correlated to specific users/requests

**4. `/health` endpoint**
   - `app.MapHealthChecks("/health")`
   - **Gain:** ACA health probes become explicit; simple DB ping check

### Cost

App Insights in sampling mode: well under $5/month. LAW workspace already exists.

### Immediate Workaround (while PR is in flight)

```bash
az containerapp logs tail -g rg-sstudio-prod -n api --follow --tail 200
```

KQL for retrospective search:
```kusto
ContainerAppConsoleLogs_CL
| where TimeGenerated > ago(12h)
| where ContainerAppName_s == "api"
| where Log_s has_any ("error", "Exception", "fail", "Unhandled", "FeedbackEndpoints")
| project TimeGenerated, Log_s
| order by TimeGenerated desc
```

### Ask

Approve the four items above. Wash implements in single PR, ~1 day including end-to-end verification (MAUI → API → OpenAI traces flow).

---

## 2026-04-20 — Mobile Observability via Azure Monitor OpenTelemetry (App Insights)

**Date:** 2026-04-20  
**Author:** Wash (Backend Dev)  
**Status:** 🔵 PROPOSED — awaiting Captain decisions  
**Companion:** `.squad/decisions/inbox/wash-observability.md` (API side)

### TL;DR

OpenTelemetry is **already wired** in `SentenceStudio.MauiServiceDefaults` (HttpClient + Runtime instrumentation). `MauiExceptions.cs` already normalizes crashes. We need to: (1) add Azure Monitor exporter NuGet, (2) subscribe `ILogger` to unhandled exceptions, (3) add Blazor JS error bridge, (4) ship connection string in embedded `appsettings.Production.json`, (5) suppress telemetry in DEBUG builds by default.

**Estimated effort:** ~1 day full path; ~3 hours small-slice (exporter + subscriber + Mac Catalyst proof-of-concept).

**Blocker:** API-side memo must ship first for end-to-end correlation.

### Current State Inventory

| Component | Status |
|---|---|
| OTel Logging + Metrics + Tracing in `SentenceStudio.MauiServiceDefaults` | ✅ Configured; OTLP exporter gated on env var |
| `MauiExceptions.cs` normalization (all platforms) | ✅ Wired; **no subscriber attached** |
| Blazor WebView JS error capture | ❌ Missing |
| Connection string transport | — Ready to embed in `appsettings.Production.json` |
| Classic `Microsoft.ApplicationInsights.*` refs | ✅ None (clean slate) |

### Implementation Plan — Five Hooks

| Hook | Where | Impact |
|---|---|---|
| Unhandled .NET exceptions | Subscribe `ILogger` to `MauiExceptions.UnhandledException` in `AddMauiServiceDefaults` | iOS/Mac/Android/Windows unified crash capture |
| Blazor component errors | Already captured by OTel Logging (default Warnings+) | Component render/event handler failures |
| JS exceptions in WebView | New `wwwroot/js/error-bridge.js` + `[JSInvokable]` service | Third-party script failures, Blazor JS errors |
| HTTP failures to API | Already captured via OTel HttpClient instrumentation | 5xx responses, timeouts (auto spans with status codes) |
| Custom business events | New extension methods: `LogQuizScoringFailed`, `LogFeedbackSubmitFailed` in catch sites | Sliceable failure analytics |

### NuGet & Configuration

**Package:**
```xml
<PackageReference Include="Azure.Monitor.OpenTelemetry.Exporter" Version="1.3.0" />
```

**Connection string location:**
```json
// src/SentenceStudio.AppLib/appsettings.Production.json
{
  "AzureMonitor": {
    "ConnectionString": "InstrumentationKey=...;IngestionEndpoint=...;LiveEndpoint=..."
  }
}
```

**Dev vs. Prod Toggle (C# code in `AddMauiServiceDefaults`):**
```csharp
var aiConnString = builder.Configuration["AzureMonitor:ConnectionString"];
#if DEBUG
aiConnString = null;  // Never send telemetry from Debug builds
#endif
if (!string.IsNullOrWhiteSpace(aiConnString))
{
    builder.Services.AddOpenTelemetry()
        .UseAzureMonitor(o => o.ConnectionString = aiConnString);
}
```

### iOS-Specific Gotchas

1. **Linker/AOT stripping:** Add `Properties/LinkerConfig.xml` preserve directive for `Azure.Monitor.OpenTelemetry.Exporter` and `OpenTelemetry.Exporter.*`
2. **Startup cost:** ~50-150ms amortized (acceptable)
3. **Offline buffering:** Built-in 24h local cache; enabled by default (don't disable)
4. **Privacy manifest (iOS 17+):** Need `PrivacyInfo.xcprivacy` declaring "Crash Data" + "Performance Data" — ~15 min task; required before next App Store submission
5. **No DiagnosticSource reflection issues** on net10 (resolved in .NET 9 era)

### Correlation (Client ↔ Server)

**Automatic once both sides emit OTel.** OpenTelemetry's `HttpClientInstrumentation` injects `traceparent` header; ASP.NET Core picks it up. Same `Operation-Id` spans both sides. Zero code.

**Prerequisite:** API-side memo must ship first (or simultaneously).

### PII / Privacy

- **HTTP bodies:** Not captured by default; don't opt in
- **User IDs:** OK to include `UserProfileId` (GUID) as baggage; **never** log emails, names, user sentences
- **Device IDs:** Use `DeviceInfo.Idiom` + `DeviceInfo.Platform`; avoid `DeviceInfo.Name` (may contain personal data)
- **Exception messages:** Discipline at log sites — don't log user text inline with exceptions
- **TelemetryProcessor:** Optional tag truncation for values > 256 chars (lower priority, address if telemetry exceeds quota)

### Sequencing

1. **First (Day 1):** API-side memo ships (retrospective visibility on current prod errors)
2. **Second (Day 2):** MAUI client side (this memo)
3. **Third (Day 3, optional):** Custom dashboards + alert rules in App Insights

**Parallel opportunity:** Kaylee could own Blazor JS error bridge independently while Wash does .NET wiring.

### Ballpark Effort Breakdown

- ~2h — NuGet + exporter + connection string + DEBUG toggle in `MauiServiceDefaults`
- ~1h — `MauiExceptions` subscriber + `ILogger<App>` wiring
- ~1.5h — JS error bridge (`error-bridge.js` + `JsErrorBridge.cs` + JSInterop)
- ~1h — Custom business event extensions (`LogQuizScoringFailed`, `LogFeedbackSubmitFailed`)
- ~1h — iOS linker preserve config + Release-to-device smoke test
- ~1h — `PrivacyInfo.xcprivacy` update (can defer if not submitting this cycle)
- ~0.5h — Controlled exception smoke test on each platform
- ~1h — End-to-end correlation smoke test (tap quiz → client span → server span under same `operation_Id`)

**Total: ~1 day.** Small-slice (proof-of-concept): ~3 hours.

### Recommended First Increment

**Wire Azure Monitor exporter + `MauiExceptions` subscriber only. Mac Catalyst DEBUG with connection string forced on. Skip Blazor JS bridge, custom events, iOS AOT work.**

**Proves:**
- Package compatibility with OTel setup ✓
- Connection string loading from embedded `appsettings` ✓
- Unhandled crashes reach App Insights ✓
- End-to-end correlation with API ✓ (if API memo lands first)

**Effort:** ~3 hours. Green-light the rest if successful; kill if blockers emerge before 1-day investment.

### Open Questions for Captain

1. **One App Insights resource or two (client vs server)?** One is simpler + correlation just works. Two gives separation but doubles setup. **Recommendation: One.**
2. **OK with `appsettings.Production.json` shipping the connection string in app bundle?** Standard practice; low risk. Alternative (fetch from API at startup) creates chicken-and-egg problem.
3. **When is next App Store submission?** Drives whether `PrivacyInfo.xcprivacy` update is urgent or can slip.
4. **Include Marketing site?** Out of this memo's scope but trivial to add via API-side path.

### Decision Required

- Approve full 1-day plan OR small-slice 3-hour proof-of-concept?
- Answer the four open questions above (drives implementation order)?
# Mobile App Insights — Follow-up Answers

**Author:** Wash (Backend Dev)
**Date:** 2026-04-20
**Companion:** `.squad/decisions/inbox/wash-mobile-observability.md` (original scope)
**Questions from Captain:** 1 (one vs two resources), 2 (connection string security), + evaluate `TinyInsights.Maui`

---

## A. One App Insights resource vs two

**What a "resource" is.** In Azure, an Application Insights resource is a billable instance that holds a bucket of telemetry. It has one **connection string** (endpoint + InstrumentationKey) that tells a client where to send data, a daily ingestion cap, a retention period, and its own KQL query surface. One resource = one scope for billing, alerts, dashboards, and queries.

**Options:**
- **ONE shared resource** — MAUI client and API both emit to the same resource with the same connection string (client bundled, server from env var).
- **TWO resources** — `ai-sstudio-mobile` + `ai-sstudio-api`, each with its own connection string, billing, and dashboards.

**Recommendation: ONE resource.** Reasons:
1. **End-to-end traces in a single query.** OpenTelemetry injects a W3C `traceparent` header automatically. Both tiers land in the same `requests`/`dependencies`/`exceptions` tables, so one KQL query walks the whole call.
2. **Concrete example — Quiz "Score" fails.** With ONE resource:
   ```kusto
   union customEvents, dependencies, requests, exceptions
   | where operation_Id == "<trace-id>"
   | order by timestamp asc
   ```
   You see: `QuizScoreTapped` event (MAUI) → outgoing HTTP `POST /api/v1/ai/chat` (MAUI) → incoming request (API) → OpenAI dependency call → the exception, with stack trace. One timeline. With TWO resources you'd run two KQL queries and correlate by hand.
3. **Simpler billing + one daily cap** to protect cost.
4. `cloud_RoleName` already distinguishes "MAUI" from "api"/"webapp" for filtering when you want tier-specific dashboards.

**When TWO would win:** different retention/access control per tier, or you give the mobile team access while keeping server telemetry siloed. Neither applies here — Captain owns both.

---

## B. Connection string security

**What it actually is.** `InstrumentationKey=…;IngestionEndpoint=https://…` — a **write-only token** for Azure Monitor's ingestion endpoint. It cannot read telemetry, list resources, or touch anything else in your Azure subscription. Reading telemetry requires an Entra identity with `Reader` on the resource (your Azure login).

**Embedding in the app bundle is the standard.** Microsoft's own App Insights and Azure Monitor docs tell mobile/desktop/JS clients to ship the connection string in the app. The SDK cannot exist without it and the key's blast radius is bounded to "someone pushes fake telemetry at your resource".

**Threat model + mitigations:**
| Risk | Mitigation |
|---|---|
| Attacker extracts key, spams fake telemetry | **Daily data cap** ($5–10/day) — App Insights stops accepting once hit, no overage bill |
| Same attacker floods you with noise | **Sampling** (10–25%) — exporter drops most repeat traces before send |
| You want to rotate after abuse | Regenerate connection string in Azure portal; ship next app build |

Daily cap is the single most important knob. Set it at resource creation.

**Alternatives and why they're worse for a mobile app:**
- **Fetch from authenticated API at startup** — chicken-and-egg: if the app can't reach the API you get zero telemetry about that exact outage. Also adds a mandatory network hop before any crash from boot can be reported.
- **Per-user keys** — massive complexity, no security win (key is still write-only).
- **Key Vault** — requires an Azure identity the app doesn't have; granting one would be *worse* than the write-only key.

**Recommendation: embed the connection string in `appsettings.Production.json` inside the MAUI app bundle.** Set daily cap to $5/day, sampling to 10% for dependencies/requests, 100% for exceptions/crashes. Rotate only if abuse is observed.

---

## C. TinyInsights.Maui evaluation

**Source:** https://github.com/dhindrik/TinyInsights.Maui (Daniel Hindrikes, Microsoft MVP; active — last commit 2026-04-15 including net10 support, crash-handling improvements).

**What it is.** A thin wrapper over the **classic `Microsoft.ApplicationInsights` 2.23.0 SDK** (NOT OpenTelemetry). Provides:
- `UseTinyInsights(connectionString)` one-liner in `MauiProgram.cs`
- `IInsights` interface for `TrackEventAsync`, `TrackPageViewAsync`, `TrackErrorAsync`, `TrackDependencyTracker`
- Automatic crash capture (hooks the platform exception pipelines) with store-and-forward on next launch
- `InsightsMessageHandler` for HttpClient dependency tracking
- `UseTinyInsightsAsILogger` variant — `ILogger` calls become telemetry
- A companion web UI for mobile-friendly viewing

**Verdict: Do NOT adopt. Stick with `Azure.Monitor.OpenTelemetry.Exporter`.**

**Why:**
1. **Wrong SDK family.** TinyInsights depends on the **legacy** `Microsoft.ApplicationInsights.*` SDK. Our API side is OpenTelemetry + Azure Monitor exporter. The two emit to the same resource but **don't share Activity context** — W3C `traceparent` correlation between MAUI and API would be fragile or broken. The whole point of Section A (one resource, one query) collapses.
2. **Fights our existing wiring.** `SentenceStudio.MauiServiceDefaults.ConfigureOpenTelemetry` already builds the OTel pipeline (HttpClient + Runtime instrumentation, logging, metrics, tracing). TinyInsights would run in parallel — double telemetry cost, two exporters, two code paths for the same signals.
3. **Conveniences we don't need.** Auto page-view tracking is Shell/MAUI XAML-centric; SentenceStudio is Blazor Hybrid (one MAUI page, all navigation inside Blazor). The `InsightsMessageHandler` duplicates what OTel HttpClient instrumentation already does. Crash auto-capture is ~30 lines we already have MauiExceptions for.
4. **Single-maintainer risk for a core dependency.** Active now, but one-person projects stall. OTel + Azure Monitor exporter is Microsoft-maintained and tracks .NET 10/11/12 automatically.
5. **Null positive: crash store-and-forward.** That's the one nice feature TinyInsights ships that the exporter doesn't give you for free — but `Azure.Monitor.OpenTelemetry.Exporter` has a built-in 48-hour local file cache for offline ingestion, which covers the same scenario.

**Where TinyInsights WOULD be right:** a greenfield MAUI app with no OTel, no server-side correlation needs, and a dev who wants `insights.TrackEventAsync("ButtonTap")` without reading OTel docs. Not us.

---

## First Increment (unchanged from original memo)

~3 hours on Mac Catalyst:
1. `<PackageReference Include="Azure.Monitor.OpenTelemetry.Exporter" Version="1.3.0" />` in `SentenceStudio.MauiServiceDefaults`.
2. In `ConfigureOpenTelemetry`, call `.UseAzureMonitor(o => o.ConnectionString = cfg["ApplicationInsights:ConnectionString"])` only when the value is present AND build is Release.
3. Subscribe `ILogger<AppCrash>` to `MauiExceptions.UnhandledException` so crashes land as exception telemetry.
4. Embed connection string in `appsettings.Production.json` shipped inside `SentenceStudio.AppLib`.
5. Ship in parallel with the server-side App Insights PR so the first trace you query already spans both tiers.

Daily cap $5, sampling 10% (requests/dependencies), 100% (exceptions).

**Ready for approval — no blockers.**

---

## 2026-04-22 — Mobile↔API Distributed Tracing Correlation — COMPLETE

**Date:** 2026-04-22  
**Owner:** Wash (Backend Observability)  
**Status:** ✅ COMPLETE — Production verified on DX24 iOS  
**Tracking:** PRs #165, #166, #172, #173 shipped; issue #171 downgraded to lower priority

### Summary

End-to-end mobile↔API distributed tracing is now working on production DX24. Mobile iOS app propagates W3C `traceparent` headers through 7 HttpClients via `ApiActivityHandler` DelegatingHandler. API receives and correlates incoming requests to the originating mobile operation. Single-user-action tracing from iOS app → ACA API is operationalized for production diagnosis.

**KQL verification** (AppId `74e94530-d17f-404a-8726-b7266724b70f`, 15m window):
- Q1 (mobile deps): 39 rows, `HTTP` type to ACA target, non-empty `operation_Id`
- Q2 (mobile→api join): 20 joined rows, 200 statuses across chat + sync flows
- **Result:** ✅ Correlation working end-to-end

### Delivery Arc

1. **PR #165** — Mobile App Insights bootstrap (Azure.Monitor.OpenTelemetry.Exporter wired, Mac Catalyst validated)
2. **PR #166** — Server-side companion for mobile role name handling (OTel exporter + role name on API side)
3. **PR #172** — Manual `ApiActivityHandler` DelegatingHandler on all 7 HttpClients + `GetRequiredService<T>()` hardening in `OpenTelemetryInitializer` to force TracerProvider materialization
4. **PR #173** — Explicit `DistributedContextPropagator.Current.Inject(...)` in `ApiActivityHandler.SendAsync` before `base.SendAsync` — **this closed the loop**

### Root Cause Identified

**Framework gap:** MAUI's `MauiApp` doesn't run `IHostedService`, so OTel's `TelemetryHostedService.StartAsync` never executes. This means `AddHttpClientInstrumentation()` wiring is effectively a no-op on MAUI, and `HttpClient`'s `DiagnosticsHandler` never auto-injects traceparent. User-space workaround (PR #172+#173) is sufficient for now.

**Eliminated hypotheses (for future diagnosticians):**
- ❌ Missing `AddHttpClientInstrumentation()` — already wired (commit 216a2da1)
- ❌ IL trimming strips listeners — disproved via `<MtouchLink>None</MtouchLink>` build (136MB/333 DLLs, same zero-correlation)
- ✅ IHostedService doesn't run on MAUI — root cause, worked around in user space

### Handler Ordering & DI Lifetime

- **Placement:** `ApiActivityHandler` is first in every `AddHttpMessageHandler` chain (outermost) so the Activity wraps auth token attachment and its context is current for any downstream header injection
- **DI lifetime:** DelegatingHandlers consumed by HttpClientFactory MUST be transient. Registered via internal `TryAddApiActivityHandler()` helper — idempotent across 4 entry points (ServiceCollectionExtensions + SentenceStudioAppBuilder)

### Known Follow-Ups (Not in Scope)

- Raw `new HttpClient()` in `src/SentenceStudio.Shared/Services/AiService.cs:93` bypasses the factory and won't get correlation — separate refactor
- `docs/deploy-runbook.md` KQL still has the wrong requests-to-requests join — separate docs PR
- Issue #171 remains open as lower-priority framework improvement

### Verification Details

**Device:** iPhone 15 Pro (DX24), production API endpoint  
**Build:** `SentenceStudio.iOS` Release (net10.0-ios, arm64)  
**Duration:** 15m observation window  
**Confidence:** 100% — live production data

**Q1 query (mobile deps):**
```
dependencies 
| where timestamp > ago(15m) 
| where cloud_RoleName startswith "SentenceStudio.Mobile" 
| where target contains "azurecontainerapps.io" 
| where isnotempty(operation_Id) 
| summarize count()
```
**Result:** 39 rows

**Q2 query (mobile→api join):**
```
let mobile_deps = dependencies 
  | where timestamp > ago(15m) 
  | where cloud_RoleName startswith "SentenceStudio.Mobile" 
  | where target contains "azurecontainerapps.io" 
  | where isnotempty(operation_Id);
requests 
  | where timestamp > ago(15m) 
  | where cloud_RoleName startswith "SentenceStudio" 
  | where isnotempty(operation_Id) 
  | join kind=inner (mobile_deps) on operation_Id 
  | summarize count()
```
**Result:** 20 joined rows, all 200 statuses

### Decisions

1. **Use `ApiActivityHandler` DelegatingHandler** in user space (not OTel framework auto-injection) until MAUI `MauiApp` supports `IHostedService`
2. **Handler ordering matters:** Place `ApiActivityHandler` outermost in chain, before auth handlers
3. **DelegatingHandler DI lifetime must be transient** — idempotent registration via `TryAddApiActivityHandler()` helper
4. **W3C `traceparent` must be injected explicitly** via `DistributedContextPropagator.Current.Inject()` before sending the HTTP request
5. **Framework issue #171 downgraded to lower-priority improvement** — user-space workaround proved sufficient and operationalized

### Tech Debt & Follow-Ups

- Mobile correlation working, but `AiService.cs:93` raw `HttpClient()` creation is a bypass — track as separate refactor
- `docs/deploy-runbook.md` KQL queries need requests-to-requests join fix — separate docs PR
- Raw `HttpClient()` bypass affects crash+chat flows when they use direct API calls — low priority but note it

### Reviewer Notes

- Correlation is now operational for production diagnosis — Captain can trace mobile actions to API tier
- Framework gap (issue #171) identified but worked around; no blocker for this PR
- All 4 PRs shipped; iOS production validation complete

---

## 2026-07-26: Squad — Word/Phrase Plan Review (5-agent consensus)

**Status:** Phase complete (plan review locked, 3 Captain decisions, 14 todos folded in, implementation ready for `model-enum`)

**Agent Verdicts:**
- **Zoe (Plan):** Architecture sound, mastery policy correct, sequencing approved with 6 clarifications
- **Wash (Schema):** 5 required changes (enum conversion, FK nullability, indexes, backfill location, dual-provider migrations)
- **River (AI/Prompt):** 2 prompts + 1 DTO change needed, Korean classification rules essential
- **Kaylee (UI):** 2 Blazor pages + 70 lines markup, existing patterns cover all needs
- **Jayne (Tests):** 8 missing mastery scenarios + backfill edge cases, 3 blockers (transaction handling, constituent row creation, E2E dependency order)

**Captain's 3 Locked Decisions:**
1. **Shadowing + Unknown:** Use text as-is AND flag for UI reclassification. Do NOT auto-wrap unknown rows in carrier sentences.
2. **Cascade transaction policy:** Best-effort with logging. Phrase mastery commits independently; constituent exposures each independent; failures logged but do not roll back.
3. **First-ever constituent exposure:** Explicit `GetOrCreateProgressAsync` before `RecordPassiveExposureAsync` (defense in depth).

**Details:** See `.squad/decisions/inbox/{zoe,wash,river,kaylee,jayne}-word-phrase-*-review.md` for full agent analysis, required changes, and implementation guardrails.

---

## 2026-04-23 — Word/Phrase Feature: Final Architecture Review & Approval

**Date:** 2026-04-23  
**Owner:** Zoe (Architecture Lead) + Team  
**Status:** ✅ APPROVED — Implementation complete  

[Content from zoe-word-phrase-plan-review.md — see `.squad/decisions/inbox/zoe-word-phrase-plan-review.md` for detailed review]

**Key findings:**
1. ✅ Architecture soundness — `LexicalUnitType` enum + `PhraseConstituent` join table placement correct
2. ✅ Mastery policy — phrase production = full credit, constituents = passive exposure only (correct)
3. ✅ Sequencing — Model → Migration → Backfill → Behavior → Tests → E2E (proper dependency order)
4. ⚠️ **Required clarifications before implementation:**
   - `model-constituent` todo: Ensure `ValueGeneratedNever()` on `PhraseConstituent.Id` in `OnModelCreating`
   - `migration-schema` todo: Must generate migrations for BOTH SQLite and PostgreSQL providers
   - `ai-generation-emit` todo: List specific prompt files + DTO classes needing updates
   - `backfill-constituents` todo: Note performance consideration (use lemma lookup dictionary, not N+1)

---

## 2026-04-23 — Word/Phrase Feature: All Todos Delivered (14/15)

**Feature:** LexicalUnitType + PhraseConstituent + Cascading Exposure  
**Agents:** Wash, River, Kaylee, Jayne  
**Status:** ✅ FEATURE COMPLETE (e2e BLOCKED)

### Todos Delivered

1. **model-enum** (Wash) ✅ — `LexicalUnitType` enum (Unknown, Word, Phrase, Sentence) added to `VocabularyWord`
2. **model-constituent** (Wash) ✅ — `PhraseConstituent` join table with EF Core config + `ValueGeneratedNever()`
3. **migration-schema** (Wash) ✅ — Migrations generated for SQLite + PostgreSQL
4. **backfill-classification** (Wash) ✅ — Heuristic rules (punctuation, whitespace, length, tags)
5. **backfill-constituents** (Wash) ✅ — Lemma-based tokenization + substring matching
6. **progress-cascade** (Wash) ✅ — Passive exposure cascade in `VocabularyProgressService.RecordAttemptAsync`
7. **shadowing-consumer** (Wash) ✅ — `ShadowingService` branches on LexicalUnitType
8. **smart-resource-phrases** (Wash) ✅ — New "Phrases" smart resource type
9. **smart-resource-phrases-fix** (Wash) ✅ — Fixed `GetAllVocabularyWordsAsync` scope bug
10. **ai-generation-emit** (River) ✅ — Prompt updates + DTO fields for LexicalUnitType + RelatedTerms
11. **ui-import-edit** (Kaylee) ✅ — Classification dropdown + constituent editor + import preview (14 UI strings)
12. **tests-backfill** (Jayne) ✅ — 120 unit tests
13. **tests-mastery-cascade** (Jayne) ✅ — 10 integration tests
14. **tests-regression** (Jayne) ✅ — 5 regression tests (word-only unaffected)
15. **tests-smart-resource** (Jayne) ✅ — 12 tests (failed initially, fixed by Wash's scope bug fix)
16. **e2e-validation** (Jayne) 🚫 **BLOCKED** — Pre-existing SQLite migration history mismatch

### Test Summary

- **147 total tests passing** (120+10+5+12)
- **Zero regressions** found in existing functionality
- **One bug surfaced & fixed:** `SmartResourceService.GetPhrasesVocabularyIdsAsync` called `GetAllVocabularyWordsAsync` (ResourceVocabularyMapping-first filter), causing circular dependency. Fixed to use VocabularyProgress-first join.

### Build Status

✅ All target projects build green (Shared, MacCatalyst, Api, UI+AppHost)

---

## Known Follow-Ups

### 1. ActiveUserId Pattern Audit

**Issue:** Same bug pattern as `SmartResourceService.GetPhrasesVocabularyIdsAsync` may exist in other methods:
- `GetDailyReviewVocabularyIdsAsync()` (line ~230-250)
- `GetStrugglingVocabularyIdsAsync()` (line ~260-280)

Both may be calling `GetAllVocabularyWordsAsync()` which depends on `ActiveUserId` being set, but SmartResourceService has no mechanism to set it. Same scope bug Wash fixed for Phrases.

**Recommendation:** Audit both methods separately (out of scope for this feature).

### 2. UI Localization (14 Strings)

**Kaylee added 14 new English strings in Blazor UI:**
- `VocabularyWordEdit.razor`: "LexicalUnitType", "Word", "Phrase", "Sentence", "Constituent Words", "Add Constituent", "Search constituent words...", "No constituents"
- `ResourceAdd.razor`: Similar classification + constituent preview labels

**Action:** Schedule localization sprint for Korean (ko), Spanish (es), French (fr), and any other target languages.

### 3. ShadowingUnknownTerm Structured Logs → Reclassification Backlog

**What's logged:** When `ShadowingService.GenerateSentencesAsync()` encounters `LexicalUnitType.Unknown`, it logs:
```
ShadowingUnknownTerm: WordId={WordId} Term={Term} needs classification
```

**Production opportunity:** Aggregate these logs (WordId + Term + frequency) to build a reclassification backlog. Long-tail unknown terms indicate:
- Heuristic classification missed them
- AI extraction is inconsistent
- Manual review needed

**Action:** Wire production logs into admin dashboard. Surface top 50 unknown terms by frequency. Flag for Captain/Zoe to prioritize AI prompt refinement or manual backfill.

### 4. RelatedTerms Resolution at AI-Emission Time

**Current state:** River's AI extraction emits `RelatedTerms` array (constituent words), which are stored in Tags as `constituents:term1,term2` hint (transient, not normalized).

**Future work:** Parse this hint at AI-emission time and immediately resolve to proper `PhraseConstituent` rows (instead of deferring to backfill service). Avoids ambiguity from substring/lemma matching.

**Action:** Defer to next iteration. Current approach (backfill) is more resilient to AI classification variance.

### 5. SQLite Migration History Reconciliation (e2e-validation BLOCKED)

**Issue:** Captain's local `sstudio.db3` has missing migration history rows in `__EFMigrationsHistory` table. Running e2e tests fails because EF Core's migration validator detects mismatch.

**Options:**
- **Option A (Recommended):** Back up `sstudio.db3`, inspect `__EFMigrationsHistory` table schema, identify missing migration IDs, insert them manually with `ProductVersion = "10.0.101"`. Relaunch tests.
- **Option B:** Wipe `sstudio.db3`, let `MigrateAsync()` run fresh. Simpler but loses any test data.
- **Option C:** Diagnose root cause (earlier dev session had uncommitted migrations? Migration file renamed?). May reveal related issues.

**Action:** Awaits Captain decision. Scribe cannot proceed without explicit guidance.


---

## 2026-04-24 — DX24 LexicalUnitType Production Hotfix

**Date:** 2026-04-24  
**Agent:** Wash (Backend Dev)  
**Status:** Implemented, code-reviewed, shipped  
**Type:** Production Emergency Fix

### Problem

DX24 Release iOS build crash. Symptom: "no such column: LexicalUnitType" on Vocabulary page and all activity pages.

### Root Cause

Migration `20260423213242_AddLexicalUnitTypeAndConstituents` failed silently on DX24. The schema was incomplete (column never added). EF entity property expects `LexicalUnitType` to exist and be non-nullable. Query fails on schema mismatch.

Pattern: SQLite on mobile can silently fail migrations. `SyncService.InitializeDatabaseAsync` had catch-all exception handler that logged and continued (degraded mode).

### Solution

Two-file hotfix (SQLite provider only; PostgreSQL migration unchanged on Azure):

1. **SQLite Migration** — Convert Up() to empty, idempotent no-op
   - File: `src/SentenceStudio.Shared/Migrations/Sqlite/20260423213242_AddLexicalUnitTypeAndConstituents.cs`
   - Pattern: Exact match to `AddMissingVocabularyWordLanguageColumn.cs` precedent
   - Leave Down() functional for rollback

2. **SyncService** — Extend PatchMissingColumnsAsync
   - File: `src/SentenceStudio.Shared/Services/SyncService.cs`
   - Add LexicalUnitType column patch + PhraseConstituent table patch
   - Both use IF NOT EXISTS / pragma checks (idempotent)
   - Runs BEFORE and AFTER MigrateAsync (defense-in-depth)

### Verification

- Build: ✅ 0 errors
- Code review: ✅ SHIP IT (5/5 checks passed)
- DX24 deployment: ExposureCount NULL count 1871 → 0; vocab + activities load

### Rule for Future Migrations

**REQUIRED for all new SQLite schema changes:**
1. Make SQLite migration Up() empty with doc comment explaining pattern
2. Add corresponding entry to `PatchMissingColumnsAsync` at the SAME TIME
3. Use IF NOT EXISTS / pragma checks for idempotency
4. Leave Down() functional for rollback

---

## 2026-04-24 — Mobile Migration Validation Strategy

**Status:** Active  
**Date:** 2026-04-24  
**Context:** DX24 production emergency — migration schema mismatches on iOS/Android.  
**Participants:** Captain, Wash

### Problem

**Migration test architecture is wrong.** xUnit projects target `net10.0` (server TFM). Conditional compilation in `SentenceStudio.Shared.csproj` excludes SQLite migrations from server TFMs. Tests tried to apply PostgreSQL migrations to SQLite database → "near ALTER" syntax error. Tests don't validate mobile.

### Solution: Three-Layer Defense

**Layer 1: Runtime schema sanity check (mobile DEBUG only)**
- File: `src/SentenceStudio.Shared/Services/MigrationSanityCheckService.cs`
- Validates critical tables + columns post-migration using `pragma_table_info`
- DEBUG: Throws `InvalidOperationException` (fail-fast for devs)
- Release: Logs `LogCritical` (don't brick user apps)

**Layer 2: Automated Mac Catalyst validation (pre-deploy gate)**
- File: `scripts/validate-mobile-migrations.sh`
- Builds Mac Catalyst Debug + launches app via `maui devflow`
- Fetches logs, greps for SQLite errors
- Run BEFORE any migration-touching PR merge

**Layer 3: Hardened exception handling**
- File: `src/SentenceStudio.Shared/Services/SyncService.cs`
- Split try/catch: migration failures → `LogCritical` + re-throw (FATAL)
- Background tasks → `LogError` + continue (degraded OK)

**Layer 4: Defense-in-depth patching (existing, keep)**
- File: `SyncService.PatchMissingColumnsAsync`
- Runs BEFORE and AFTER MigrateAsync on mobile
- Patches missing columns/tables with IF NOT EXISTS

### Why NOT xUnit for mobile migrations

xUnit can't target mobile TFMs (no test runner on iOS simulator). SQLite migrations aren't compiled into `net10.0` test projects. Would need duplicate migration code or MSBuild hacks. **Rejected.** Use runtime + script validation instead.

### What We Learned

SQLite has severe ALTER TABLE limits (no type changes, no drops, no renames on old iOS versions). Exceptions on mobile are silent (caught and logged). Conditional compilation makes unit tests misleading. Only real validation is on actual mobile build.

---

## 2026-04-24T15:54:33Z — User Directive

**By:** David Ortinau (via Copilot)

**Directive:** Agents MUST drive physical-device verification themselves (tap, navigate, screenshot, read errors). NOT ask Captain to tap buttons or read errors on DX24 Release builds.

**Rationale:** Captain explicitly stated twice: "you should be logging these in debug so you can read them yourself. I'm not your error reading monkey" and "you need to be doing this e2e yourself. There's no excuse for asking me to tap buttons."

**Tools available:** `appium-automation` skill, `xcrun devicectl`, Appium. MauiDevFlow being `#if DEBUG` is not an excuse — use another tool.

**Scope:** Applies to all device work, not just Debug builds. Agents own the full e2e cycle.

# Decision: Data Import Feature Architecture

**Date:** 2026-07-27
**Owner:** Zoe (Lead)
**Status:** Proposed — awaiting Captain review
**Requested by:** Captain (David Ortinau)

---

## 1. Feature Surface (UX Flow)

### Where Import Lives

The feature extends the existing `/import` Blazor page (`Import.razor`), which currently handles YouTube video imports only. Add a fourth tab: **"Text / File"** alongside Channels, Single Video, and History. This keeps all import paths in one place.

- **Blazor webapp**: Primary surface. Full form with file upload, text paste, preview table.
- **MAUI (MauiReactor mobile)**: Defer to v2. The Blazor Hybrid WebView renders the same Razor pages, so MAUI users get it for free via the shared UI project. A native MauiReactor page is not needed for MVP.

### Core Form Fields

| Field | Type | Required | Notes |
|---|---|---|---|
| **Input mode** | Toggle: Paste Text / Upload File | Yes | File accepts .csv, .tsv, .txt, .json |
| **Text input** | Textarea | If paste mode | Multiline, no size limit in UI |
| **File input** | InputFile | If file mode | Uses existing Blazor InputFile pattern from ResourceAdd.razor |
| **Format description** | Textarea | No | Free-text hint: "columns are Korean, English, part of speech" |
| **Content type** | Select: Vocabulary / Phrases / Transcript / Auto-detect | Yes, default Auto-detect | |
| **Target resource** | Select: "Create new" / picker of existing LearningResources | Yes, default "Create new" | Filtered to user's resources |
| **New resource title** | Text input | If "Create new" selected | Pre-populated from filename if file upload |
| **Target language** | Select | Yes, default from user profile | Korean, Spanish, etc. |
| **Native language** | Select | Yes, default from user profile | English, etc. |
| **Tags** | Text input | No | Comma-separated, pre-populated from selected resource |

### Additional Anticipated Fields

| Field | Type | Default | Rationale |
|---|---|---|---|
| **Delimiter hint** | Radio: Auto / Comma / Tab / Semicolon / Pipe | Auto | CSV/TSV ambiguity is common |
| **Header row** | Checkbox | Off | "First row is column names" |
| **Dedup behavior** | Radio: Skip duplicates / Update existing / Import all | Skip duplicates | Captain's data preservation rule demands explicit choice |
| **Source attribution** | Text input | Blank | "Where did this content come from?" (textbook name, URL, teacher) |
| **Dry-run preview** | Always shown | N/A | Non-negotiable. Every import shows parsed rows before commit. |

Fields NOT included in MVP (defer to v2): CEFR/TOPIK level tagging per-word, batch scheduling, audio file extraction, language pair detection.

### Preview-Before-Commit Step

After the user fills the form and clicks "Preview Import":

1. Parsing pipeline runs (see Section 4).
2. Results displayed in an editable table: checkbox per row (select/deselect), inline edit for target term and native term, row-level error badges.
3. Summary bar: "42 words parsed, 3 duplicates skipped, 2 errors".
4. Captain can edit, exclude rows, then click "Commit Import".
5. On commit: words saved, mappings created, toast confirmation with count.

---

## 2. Content Type Detection Strategy

### When Set to Auto-detect

**Heuristic-first, AI fallback.** Rationale: deterministic checks are fast, free, and predictable. AI is expensive and non-deterministic. Use AI only when heuristics are inconclusive.

### Heuristic Signals

| Content Type | Signals |
|---|---|
| **Vocabulary** | Two-column structure (delimiter-separated pairs), short tokens (< 30 chars per cell), no timestamps, no speaker tags |
| **Phrases** | One column of medium-length strings (10-80 chars), no paired translation column, complete utterances but no paragraph flow |
| **Transcript** | Timestamps (`[00:01:23]`, `0:15`), speaker tags (`A:`, `Speaker 1:`), paragraph flow, > 500 chars total, line breaks between utterances |

### Decision Flow

```
1. Check for timestamps/speaker tags → Transcript (high confidence)
2. Check for two-column delimiter structure → Vocabulary (high confidence)
3. Check for JSON array → parse structure, classify by field names
4. Single column, short lines → Phrases (medium confidence)
5. Otherwise → AI classification (low confidence flag)
```

### Low-Confidence Handling

When heuristic confidence is below threshold OR AI is used:
- Show a yellow info bar: "We detected this as [Vocabulary]. Is that correct?"
- Provide a dropdown to override. The override feeds back into the parsing stage.
- Never auto-commit low-confidence detections.

---

## 3. Data Model and Persistence

### New Entities: No

No new database entities for MVP. Rationale:

- **No ImportJob table.** The import is a synchronous, single-request operation. State is held in the Blazor component during preview. If the user navigates away, the preview is lost — this is acceptable for MVP. The VideoImport entity exists for the YouTube pipeline (which is async/background), but text/file imports are fast enough to be synchronous.
- **No ImportSource table.** Source attribution is stored as a tag or in the resource's Description field.
- **No ImportError table.** Per-row errors are transient UI state during preview. Not persisted.

### How Imports Map to LearningResource

| Target selection | Behavior |
|---|---|
| **Create new** | New LearningResource created. MediaType = "Vocabulary List" or "Transcript" based on content type. UserProfileId set from active user. Vocab words linked via ResourceVocabularyMapping. |
| **Existing resource** | Vocab words added to existing resource via new ResourceVocabularyMapping rows. Existing mappings untouched. No words removed. |

"Append" is the only merge mode for MVP. "Replace all words on resource" is destructive and deferred to v2 behind a confirmation modal.

### Vocabulary Dedup Strategy

VocabularyWord is shared (no UserProfileId). Dedup key: **TargetLanguageTerm, case-sensitive, whitespace-trimmed.**

This matches the existing pattern in `VideoImportPipelineService.CreateLearningResourceAsync` (line 367-368):
```csharp
var existing = await db.VocabularyWords
    .FirstOrDefaultAsync(w => w.TargetLanguageTerm == word.TargetLanguageTerm);
```

Dedup behavior based on user's selection:
- **Skip duplicates** (default): If a VocabularyWord with the same TargetLanguageTerm exists, reuse its ID for the ResourceVocabularyMapping. Do not update the existing word's fields.
- **Update existing**: If match found, update NativeLanguageTerm, Lemma, Tags on the existing word. Create mapping if not already linked.
- **Import all**: Create new VocabularyWord regardless of duplicates (allows variant definitions). This is an escape hatch.

ResourceVocabularyMapping dedup: always check for existing mapping before inserting. Never create duplicate (ResourceId, VocabularyWordId) pairs.

### Migration Plan

No migration needed for MVP. All data flows through existing tables: LearningResource, VocabularyWord, ResourceVocabularyMapping.

If v2 adds an ImportHistory entity (for audit trail/undo), that would require an EF Core migration following the established pattern (`dotnet ef migrations add`).

---

## 4. Parsing Pipeline

### Stages

```
Source Intake → Format Detection → Parse → Validate → Preview → Commit
```

| Stage | Deterministic | AI | Notes |
|---|---|---|---|
| **Source Intake** | Yes | No | Read text or file bytes, detect encoding (UTF-8 assumed, BOM sniff) |
| **Format Detection** | Yes (heuristic) | Fallback | See Section 2. Determine delimiter, column count, content type |
| **Parse** | Yes (structured) | Yes (unstructured) | CSV/TSV/JSON: deterministic. Free text/transcript: AI extraction |
| **Validate** | Yes | No | Required fields present, no empty terms, encoding issues, length limits |
| **Preview** | N/A | N/A | UI renders parsed + validated rows |
| **Commit** | Yes | No | SaveChangesAsync with dedup logic |

### Where AI Fits

1. **Content type classification** (Auto-detect fallback): Short prompt, returns enum.
2. **Transcript to vocabulary**: Reuses existing `ExtractVocabularyFromTranscript.scriban-txt` prompt and `VocabularyExtractionResponse` DTO. Already battle-tested in the YouTube pipeline.
3. **Free-text to vocab pairs**: New prompt needed. Input: unstructured text + format description from user. Output: `VocabularyExtractionResponse`.
4. **Format inference**: When delimiter/structure is ambiguous and user provided a format description, AI interprets the description to produce parsing rules.

### Where Deterministic Parsing Fits

- **CSV/TSV**: Split by delimiter, map columns. Column mapping: if 2 columns, assume (TargetLanguageTerm, NativeLanguageTerm). If 3+, check header row or use format description.
- **JSON**: Deserialize, look for `vocabulary` array (matches existing DTO) or flat array of objects with recognizable field names.
- **Line-delimited**: One item per line. Single column = target terms only (native term left blank for user to fill or AI to populate in v2).

### Error Handling

- Per-row errors collected in a list. Each error: row number, raw content, error message.
- Partial success: valid rows shown in preview with green checkmarks; error rows shown with red badges and the raw text.
- Captain reviews errors in preview. Can edit and re-validate, or exclude and proceed.
- No silent data loss. Every row accounted for in the preview summary.

---

## 5. AI Integration Touchpoints

### Prompts Needed

| Prompt | New/Existing | Input | Output DTO |
|---|---|---|---|
| **ContentTypeDetect** | New `.scriban-txt` | Sample of input text (first 500 chars) + user's format description | `ContentTypeDetectionResponse { ContentType, Confidence, Reasoning }` |
| **FreeTextToVocab** | New `.scriban-txt` | Raw text + format description + target/native language | `VocabularyExtractionResponse` (existing DTO) |
| **TranscriptToVocab** | Existing `ExtractVocabularyFromTranscript.scriban-txt` | Transcript text + language | `VocabularyExtractionResponse` (existing DTO) |

### Integration Pattern

Reuse `AiService.SendPrompt<T>()` with structured DTOs, following the `VideoImportPipelineService` precedent. The `[Description]` attributes on DTO properties guide the AI. No manual JSON formatting in prompts (Captain's standing rule from Microsoft.Extensions.AI guidelines).

New prompt templates go in `src/SentenceStudio.AppLib/Resources/Raw/` following the `.scriban-txt` naming convention.

River will detail the prompt engineering. The architecture only mandates: structured DTOs in, structured DTOs out, via `SendPrompt<T>`.

---

## 6. Architecture / Dependency Flow

### Service Layer

**New service: `ContentImportService`** in `SentenceStudio.Shared/Services/`.

Location rationale: Shared project, not AppLib. The import logic needs `ApplicationDbContext` and `AiService`, both available in Shared. This matches the placement of `VideoImportPipelineService`.

```
ContentImportService
  ├── ParseContent(text/bytes, formatHint, contentType, delimiter) → ImportPreview
  ├── DetectContentType(sample, formatDescription) → ContentTypeResult
  ├── CommitImport(ImportPreview, targetResourceId, dedupPolicy) → ImportResult
  └── Dependencies: AiService, LearningResourceRepository, IFileSystemService
```

**API endpoint: Not needed for MVP.** The Blazor webapp and MAUI Hybrid both run in-process with direct service access. The YouTube import needs API endpoints because the Workers project processes imports server-side. Text/file imports are user-initiated and synchronous.

If v2 adds server-side import (e.g., bulk API upload), add endpoints then.

### UI Layer

Primary: `Import.razor` — extend with a new tab. Component decomposition:
- `ImportTextFileTab.razor` — the form
- `ImportPreviewTable.razor` — the editable preview grid
- Reuse existing `PageHeader`, `form-control-ss`, `card-ss` patterns from ResourceAdd.razor

### Background Work

**Not needed for MVP.** Text/file imports parse in < 2 seconds for deterministic formats. AI-backed transcript extraction takes 5-15 seconds — show a spinner, same pattern as the Single Video tab's "Polish" and "Generate Vocabulary" buttons.

If imports exceed 1000 rows or transcripts are very long, v2 can add a progress indicator with cancellation. No queue infrastructure needed.

---

## 7. Scope Phasing

### MVP (v1)

Ship end-to-end with:
- Text paste or CSV/TSV/TXT file upload
- Vocabulary content type (paired columns)
- Delimiter detection (auto + manual override)
- Header row toggle
- Create new or append to existing LearningResource
- Dedup by TargetLanguageTerm (skip / update / import all)
- Editable preview table before commit
- Basic error display per row

### v2

- **Transcript content type**: AI extraction using existing `ExtractVocabularyFromTranscript` prompt
- **Phrases content type**: AI-backed phrase parsing
- **Auto-detect content type**: Heuristic + AI fallback
- **JSON import format**: Structured import matching `VocabularyExtractionResponse` schema
- **Format description AI interpretation**: User describes format, AI generates column mapping
- **Import history / undo**: Persist import records for audit trail, enable "undo last import" (remove mappings, optionally remove orphaned words)
- **Batch/async processing**: Progress bar, cancellation for large imports
- **Column mapping UI**: Drag-and-drop column assignment for multi-column files
- **CEFR/TOPIK level tagging**: Per-word level assignment during import
- **Audio file extraction**: Import audio with transcript alignment

---

## 8. Open Questions for Captain

1. **Dedup default behavior**: When importing vocabulary that already exists in the database, should the default be "skip and reuse existing" (safest) or "update existing definitions" (keeps data fresh but modifies shared words)? Current YouTube pipeline uses skip-and-reuse.

2. **Source attribution field**: Worth including in MVP, or defer? It adds one text field to the form. If included, should it go on the LearningResource.Description, a tag, or a new column?

3. **Native language term requirement**: If the user imports a single-column list (target language terms only, no translations), should we (a) leave NativeLanguageTerm blank and let the user fill in later, (b) use AI to generate translations as part of the import, or (c) reject single-column imports?

4. **Import from clipboard on mobile**: The MAUI app could offer a "Paste from clipboard" shortcut that skips the textarea entirely. Worth prioritizing, or is the textarea sufficient?

5. **Merge vs append for existing resources**: When adding words to an existing resource, the plan says append-only (no words removed). Should there be a "replace all vocabulary on this resource" option, even with a confirmation modal? Or is that too dangerous for MVP?

---

## 9. Risks / Data Preservation Callouts

### Dedup Risk

The dedup key is TargetLanguageTerm (case-sensitive, trimmed). Two risks:
- **Near-duplicates**: "먹다" vs "먹다 " (trailing space) — mitigated by trimming.
- **Homographs**: Different words with the same spelling but different meanings. The "Import all" escape hatch handles this, but users must know to select it.
- **Shared word mutation**: "Update existing" dedup mode modifies a VocabularyWord that may be linked to other resources. The NativeLanguageTerm change affects all resources referencing that word. This must be clearly communicated in the UI: "This will update the definition for all resources using this word."

### Merge Risk

Appending to an existing resource never removes words. Safe by default. The v2 "replace" option must:
- Show exactly which words will be removed
- Require explicit confirmation
- Preserve removed words in the VocabularyWord table (only remove ResourceVocabularyMapping rows)
- Never delete VocabularyWord rows as part of an import

### No Silent Data Loss

Every import goes through preview. No row is committed without the Captain seeing it in the preview table. Error rows are visible, not silently dropped.

### Reversibility

MVP does not include formal undo. However:
- Words added to a resource can be removed via the existing resource edit page (bulk remove from ResourceVocabularyMapping).
- VocabularyWord rows created during import persist but are harmless orphans if unlinked.
- v2 ImportHistory would enable one-click undo by recording which words and mappings were created per import.

### File Upload Safety

- File size limit: 5 MB (configurable). Prevents accidental large file processing.
- Encoding: UTF-8 assumed. BOM detection for UTF-16. Encoding errors reported per row, not silently mangled.
- No file is persisted to disk. Content is read into memory, parsed, then discarded after commit. The raw content lives only in the LearningResource.Transcript field if the content type is Transcript.

---

## Summary

This is a focused MVP: paste text or upload a file, pick vocabulary, preview, commit. The heavy AI work (transcripts, auto-detect, format inference) is deferred to v2. The architecture reuses existing patterns (VideoImportPipelineService for pipeline structure, VocabularyExtractionResponse for AI DTOs, ResourceAdd.razor for UI patterns) rather than inventing new ones.

No new database tables. No migrations. No background infrastructure. One new service, two new prompt templates, one new Blazor tab with a preview component.
# Decision: Import Feature Placement — Separation Revision

**Date:** 2026-07-27
**Owner:** Zoe (Lead)
**Status:** Proposed — awaiting Captain ruling
**Context:** Captain feedback: "the existing import is video subscription related, and I think video subs should be somewhat considered separately."

---

## 1. Case Assessment: Shared Tab vs. Separate Pages

### Arguments for keeping it as a tab on `/import`

- **Single discovery point.** Users think "I want to bring content in" and go to one place. One nav entry, not two.
- **Code reuse.** Both flows end at the same commit target: LearningResource + VocabularyWord + ResourceVocabularyMapping. Shared components (resource picker, preview table) stay co-located.

### Arguments for separating (stronger)

- **Different mental models.** YouTube subscriptions are an ongoing pipeline: subscribe to a channel, monitor for new videos, auto-process. The new import is a one-shot action: paste text, preview, commit. Mixing these in tabs conflates a subscription manager with a data tool.
- **Different growth trajectories.** YouTube import will gain channel management features (polling frequency, auto-tagging, notification preferences). The data import will gain format support, column mapping, batch processing. Coupling them means one page accumulates unrelated complexity.
- **Navigation clarity.** "Import" today means "YouTube stuff." Adding a 4th tab for a fundamentally different workflow muddies the label. Two focused pages with clear names are better than one overloaded page.
- **The Captain said so.** He knows his own product's information architecture better than I do. The instinct is correct.

**Verdict: Separate. The Captain is right.**

---

## 2. Placement Recommendation

### Navigation structure

| Current | Proposed | Route | Icon |
|---|---|---|---|
| Import | **Video Subscriptions** | `/video-subscriptions` | `bi-youtube` (or `bi-camera-video`) | *(superseded — see Final Rulings 2026-04-24)* |
| _(new)_ | **Import Content** | `/import-content` | `bi-box-arrow-in-down` |

Rationale for the names:
- "Video Subscriptions" says exactly what it is: YouTube channel monitoring and single-video import. The word "Import" was always too generic for what is really a subscription manager. *(superseded — see Final Rulings 2026-04-24)*
- "Import Content" is the new generic data import. "Content" scopes it to learning material (not settings, not backups). The route `/import-content` avoids collision with the existing `/import` route during migration.

### Nav order in `NavMenu.razor`

```csharp
new NavItem("resources",           "bi-book",               Localize["Nav_LearningResources"]),
new NavItem("vocabulary",          "bi-card-text",           Localize["Nav_Vocabulary"]),
new NavItem("import-content",      "bi-box-arrow-in-down",   Localize["Nav_ImportContent"]),
new NavItem("media-import",        "bi-film",               Localize["Nav_MediaImport"]), // (superseded — see Final Rulings 2026-04-24)
```

Import Content sits next to Resources and Vocabulary (data management cluster). Media Import (renamed from Video Subscriptions per Captain ruling) follows — related but distinct.

### What happens to existing `/import`

Two options, recommend Option A:

**Original Options A/B:** (superseded — see Final Rulings 2026-04-24)

**Final Ruling:** Rename `Import.razor` to `MediaImport.razor`, change `@page "/import"` to `@page "/media-import"`. Keep `@page "/import"` as a secondary route for backward compatibility redirect. Update `NavMenu.razor` with new label "Media Import" and icon `bi-film`. The route rename is low-risk (no external consumers of `/import` — it is an authenticated SPA page, not a public URL).

### Does Import.razor need refactoring?

No structural refactoring needed. It is already focused on YouTube concerns (channels, single video, history). Renaming the file and route is sufficient. The three tabs (Channels, Single Video, History) remain as they are.

The only code-level change: update localization keys from `Import_*` to `MediaImport_*` (or keep the old keys and just change the display values — cheaper, no functional difference). Recommend keeping the old keys for MVP to avoid a localization churn; rename in a cleanup pass. *(superseded — see Final Rulings 2026-04-24)*

---

## 3. Updated Plan Sections

### Section 1 — Feature Surface (revised)

The import feature lives at a **new page** `/import-content` (`ImportContent.razor`), accessible from a dedicated nav entry "Import Content" with `bi-box-arrow-in-down` icon.

This page is not tabbed. It is a single-purpose form:
- Input mode toggle (paste / file upload)
- Format and content type fields
- Target resource picker
- Language, tags, delimiter, dedup controls
- Preview table
- Commit button

No relationship to the YouTube/Media Import page. No shared tabs or parent component. *(superseded — see Final Rulings 2026-04-24)*

Blazor webapp is the primary surface. MAUI Hybrid gets it via the shared UI project. Native MauiReactor page deferred to v2.

### Section 6 — Architecture / Dependency Flow (revised)

**UI layer:**
- New file: `src/SentenceStudio.UI/Pages/ImportContent.razor` — the form + preview.
- New component: `src/SentenceStudio.UI/Pages/ImportPreviewTable.razor` — the editable preview grid (can be shared with Media Import later if needed).
- `NavMenu.razor`: Add `import-content` entry, rename `import` to `media-import`. *(superseded — see Final Rulings 2026-04-24)*
- Localization: Add `Nav_ImportContent` and `Nav_MediaImport` keys. Add `ImportContent_*` keys for the new page. Existing `Import_*` keys remain untouched (they serve Media Import). *(superseded — see Final Rulings 2026-04-24)*

**Service layer:** No change from original plan. `ContentImportService` in Shared, no API endpoint for MVP.

**Existing `Import.razor`:** Rename to `MediaImport.razor`, route to `/media-import`, keep `@page "/import"` as secondary route for backward compat. No structural changes to its internals. *(superseded — see Final Rulings 2026-04-24)*

---

## Summary

Separate pages. "Import Content" at `/import-content` for the new feature. "Media Import" (renamed from "Video Subscriptions" per Captain ruling) at `/media-import` for YouTube. Clean names, clean growth paths, no shared complexity. The rest of the architecture plan (Sections 2-5, 7-9) is unaffected.

---

## 2026-04-24 — Captain's Final Rulings — Data Import

**Date:** 2026-04-24  
**Respondent:** David Ortinau (Captain)  
**Context:** Ruling on 7 open questions from Zoe's architecture proposal  
**Status:** ✅ Final  

### Rulings

1. **Dedup default behavior:** Skip and reuse existing. When importing vocabulary that already exists in the database, the default is "skip and reuse existing" (matches YouTube pipeline pattern). Safest path: prevents accidental mutation of shared words.

2. **Source attribution field:** Deferred to v2. Not included in MVP. Can be added post-launch as an optional field on `LearningResource.Description` or a dedicated tag.

3. **Single-column imports (target language terms only):** Use AI to translate on import. **New MVP work item: `mvp-single-column-translate`.** Reuses existing `GetTranslations`-style prompt pattern. Editable AI-filled cells in preview, badged to indicate AI-filled, never silently committed blank. User can edit or delete cells before commit.

4. **Mobile clipboard paste shortcut:** Not needed for MVP. The textarea on ImportContent is sufficient. Shortcut can be added in v2 if users request it.

5. **"Replace all vocabulary" mode:** Deferred to v2. MVP only supports append-only merge. v2 will add "replace all words on this resource" option behind confirmation modal.

6. **Renamed YouTube import page:** Label is **"Media Import"**, route is **/media-import** (NOT "Video Subscriptions" / `/video-subscriptions` as originally proposed). Primary route is `/media-import`. Keep `/import` as secondary `@page` attribute for backward-compatibility redirect. This clarifies the page scope without using "video" which may confuse users when transcripts or other media are added.

7. **Separate-page placement:** Confirmed. New import feature lives at `/import-content` (separate from `/media-import`). No shared tabs. Clean separation enables independent growth.

### MVP Work Items Added

- **mvp-single-column-translate:** Add AI prompt task to translate single-column vocabulary imports. Produces editable preview with AI-filled cells (badged), user can edit or delete before commit.
# Import Data Layer Scout — Findings for Zoe

**Date:** 2026-05-30  
**Scout:** Wash (Backend Dev)  
**Consumer:** Zoe (Architect)  
**Purpose:** Pre-architecture survey for new vocabulary import feature

---

## 1. Existing Import Paths

### YouTube Video Import (Production)
- **Service:** `VideoImportPipelineService` (`src/SentenceStudio.Shared/Services/VideoImportPipelineService.cs`)
- **Pipeline:** Fetch transcript → AI cleanup → vocab generation → save `LearningResource` + `VocabularyWord` entities
- **Status tracking:** `VideoImport` entity with enum states (`Pending`, `FetchingTranscript`, `CleaningTranscript`, `GeneratingVocabulary`, `SavingResource`, `Completed`, `Failed`)
- **API endpoints:** `ImportEndpoints.cs` exposes `/api/imports` (GET history, POST start, POST retry)
- **Background execution:** Pipeline runs via `Task.Run` (non-blocking), caller polls for progress
- **Dedup logic:** Line 368 — `FirstOrDefaultAsync(w => w.TargetLanguageTerm == word.TargetLanguageTerm)` — **case-sensitive, exact match on TargetLanguageTerm only**
- **File picker:** NOT USED — YouTube import is URL-based, no file upload

### CSV/Text File Import (Gap)
- **Search result:** No CSV parsing service found
- **UI evidence:** `Import.razor` exists but contains ONLY YouTube tabs (Channels, Single Video, History) — no file import tab
- **File picker abstraction:** Exists at `IFilePickerService` / `MauiFilePickerService` — ready to use, tested in other features
- **Parsers:** `VocabularyWord.ParseVocabularyWords()` static method (line 78-102 in `VocabularyWord.cs`) — supports comma or tab delimited, returns `List<VocabularyWord>` — **NO resource linkage, NO dedup, NO persistence**

**VERDICT:** File import UI and service layer are missing. Parser exists but is a static utility, not wired to repository/persistence.

---

## 2. LearningResource Model

**File:** `src/SentenceStudio.Shared/Models/LearningResource.cs`

### All Fields (lines 11-78):
- `Id` (string GUID, PK)
- `Title`, `Description`, `MediaType`, `MediaUrl`, `Transcript`, `Translation`, `Language`
- `SkillID`, `OldVocabularyListID` (legacy compat)
- `Tags` (comma-separated string)
- `IsSmartResource` (bool) — system-generated flag
- `SmartResourceType` (string) — `"DailyReview"`, `"NewWords"`, `"Struggling"`, `"Phrases"`
- `CreatedAt`, `UpdatedAt` (DateTime)
- `UserProfileId` (string, FK) — **per-user ownership**
- `Vocabulary` (List<VocabularyWord>, skip navigation via `VocabularyMappings`)
- `VocabularyMappings` (List<ResourceVocabularyMapping>, join entity)

### IsSmartResource Semantics (lines 50-54, 76-77):
- `IsSmartResource == true` → system-generated "smart" resource (daily review, new words, struggling, phrases)
- `SmartResourceType` enum specifies which type
- Helper property `IsSystemGenerated` aliases `IsSmartResource`
- User-created resources have `IsSmartResource == false`, no `SmartResourceType`

**KEY INVARIANT:** Smart resources are singleton-per-type-per-user, refreshed in-place. Import targets will ALWAYS be user-created (`IsSmartResource = false`).

---

## 3. VocabularyWord + ResourceVocabularyMapping

### VocabularyWord (src/SentenceStudio.Shared/Models/VocabularyWord.cs)
- **PK:** `Id` (string GUID)
- **Core fields:** `TargetLanguageTerm`, `NativeLanguageTerm`, `Language`
- **Encoding fields:** `Lemma`, `Tags`, `MnemonicText`, `MnemonicImageUri`, `AudioPronunciationUri`, `LexicalUnitType`
- **NO UserProfileId** — confirmed at line 10-11, no FK to UserProfile
- **Shared vocabulary:** Words are global, per-user association lives in `VocabularyProgress` (via `UserId` FK) and resource linkage via `ResourceVocabularyMapping`
- **Navigation properties:** `LearningResources` (skip nav), `ResourceMappings`, `ExampleSentences`

### ResourceVocabularyMapping (src/SentenceStudio.Shared/Models/ResourceVocabularyMapping.cs)
- **PK:** `Id` (string GUID)
- **FKs:** `ResourceId`, `VocabularyWordId`
- **Purpose:** Many-to-many join between `LearningResource` and `VocabularyWord`
- **Creation pattern (from VideoImportPipelineService, lines 364-390):**
  1. Check for existing word by `TargetLanguageTerm`
  2. If exists, use existing `Id`; if new, insert word first
  3. Create mapping: `new ResourceVocabularyMapping { ResourceId = resource.Id, VocabularyWordId = wordId }`
  4. SaveChanges in single transaction

**IMPORTANT:** Mappings are created AFTER word dedup check — no duplicate mappings within single resource.

---

## 4. LearningResourceRepository + VocabularyProgressRepository

### LearningResourceRepository (`src/SentenceStudio.Shared/Data/LearningResourceRepository.cs`)

**Public surface for import:**
- `GetWordByTargetTermAsync(string)` — line 45, exact match on `TargetLanguageTerm` (used for dedup)
- `GetWordByNativeTermAsync(string)` — line 36
- `SaveWordAsync(VocabularyWord)` — line 108, upserts word (detaches nav props)
- `SaveResourceAsync(LearningResource)` — line 210, upserts resource + handles vocab associations
- `AddVocabularyAsync(resourceId, List<VocabularyWord>)` — line 254+ (view_range truncated, likely exists)

**Key method: SaveResourceAsync (lines 210-250+)**
1. Captures `Vocabulary` list before detach
2. Checks if resource exists (`AnyAsync` by `Id`)
3. If exists: fetches with `.Include(r => r.Vocabulary)`, updates props, clears + re-adds vocab
4. If new: adds resource + vocab mappings
5. Saves in single transaction
6. Triggers sync

**Dedup helpers:**
- Line 940: `vw.TargetLanguageTerm.Trim().ToLower()` — case-insensitive after trim (found in grep)
- Line 1007-1047: `MergeVocabularyWordsAsync` — merges duplicate words, reassigns mappings, deletes duplicates

### VocabularyProgressRepository (`src/SentenceStudio.Shared/Data/VocabularyProgressRepository.cs`)

**Progress tracking (per-user):**
- `GetByWordIdAndUserIdAsync(wordId, userId)` — line 38
- `GetOrCreateAsync(vocabularyWordId)` — line 117, auto-creates progress for new words
- `SaveAsync(VocabularyProgress)` — line 137, upserts progress

**IMPORTANT:** Progress is created lazily when word is first practiced, NOT at import time. Import only creates `VocabularyWord` + `ResourceVocabularyMapping`.

---

## 5. Dedup Keys Today

**Current dedup logic:** `VideoImportPipelineService.cs:368`
```csharp
var existing = await db.VocabularyWords
    .FirstOrDefaultAsync(w => w.TargetLanguageTerm == word.TargetLanguageTerm);
```

**Dedup key:** `TargetLanguageTerm` ONLY  
**Case sensitivity:** Exact match (case-sensitive via EF default)  
**Trimming:** NOT applied in pipeline (raw AI output used)

**HOWEVER:** `LearningResourceRepository.cs:940` shows alternative pattern:
```csharp
vw.TargetLanguageTerm.Trim().ToLower() == targetTerm.Trim().ToLower()
```
This suggests dedup SHOULD be case-insensitive + trimmed, but `VideoImportPipelineService` doesn't use this pattern.

**Native term ignored:** No check on `NativeLanguageTerm` — multiple meanings for same target term allowed (e.g., "가다" with multiple English definitions creates separate words).

**RECOMMENDATION FOR IMPORT:** Use case-insensitive trimmed dedup OR add explicit merge tool post-import. Current YouTube pipeline will create duplicates if same word appears with different casing.

---

## 6. Migration Mechanics for This Repo

**Canonical workflow (AGENTS.md line 110, .squad/agents/wash/history.md:90, 129, 1424):**

```bash
dotnet ef migrations add <MigrationName> \
  --project src/SentenceStudio.Shared/SentenceStudio.Shared.csproj \
  --startup-project src/SentenceStudio.Shared/SentenceStudio.Shared.csproj
```

**CRITICAL GOTCHA (wash/history.md:190, 1424):**
- `dotnet ef migrations add` FAILS on multi-TFM projects (ResolvePackageAssets error)
- **Workaround:** Temporarily change `<TargetFrameworks>` to `<TargetFramework>net10.0</TargetFramework>` (singular), generate migration, restore multi-targeting
- **Dual providers:** PostgreSQL (Azure) + SQLite (mobile)
  - PostgreSQL: auto-generated via `dotnet ef`
  - SQLite: hand-converted from PG migration (type substitution: `integer`→`INTEGER`, `timestamp with time zone`→`TEXT`)
- **Table naming:** Use singular names via `.ToTable("VocabularyWord")` in `OnModelCreating` — EF respects these, no plural gotcha

**SQLite idempotency pattern (wash/history.md:1560):**
- MigrateAsync failures are swallowed in production (catch-all at `SyncService.InitializeDatabaseAsync:227-230`)
- Mobile migrations MUST be idempotent via `PatchMissingColumnsAsync` before MigrateAsync
- Pattern: Leave SQLite migration `Up()` empty, add patch in `PatchMissingColumnsAsync` using `CREATE TABLE IF NOT EXISTS` + `CREATE INDEX IF NOT EXISTS`

**Runtime application:** Migrations auto-apply at `UserProfileRepository.GetAsync()` via `MigrateAsync()` — no manual `dotnet ef database update` needed.

---

## 7. Aspire Wiring (DI Registration)

**Service registration:** `src/SentenceStudio.AppLib/Setup/SentenceStudioAppBuilder.cs`

**Relevant section (lines 36-84):**
- Line 36: `RegisterServices(builder.Services)` — central registration point (method likely in same file, not shown in view_range)
- Line 54: `builder.Services.AddDataServices(dbPath)` — registers DbContext + repos
- Line 62: `builder.Services.AddSyncServices(dbPath, syncServerUri)` — sync services
- Line 70: `builder.Services.AddApiClients(apiBaseUri)` — HTTP clients for API endpoints
- Line 71: `builder.Services.AddSingleton<ISyncService, SyncService>()` — sync service singleton
- Line 77-78: Scoped repos (`MinimalPairRepository`, `MinimalPairSessionRepository`)

**Pattern for new ImportService:**
```csharp
builder.Services.AddScoped<VocabularyImportService>();
// OR if needs file picker:
builder.Services.AddScoped<IFilePickerService, MauiFilePickerService>(); // Already exists
```

**Existing file picker registration:** NOT found in shown section — likely already registered in `RegisterServices` or `AddDataServices` extension.

---

## Summary for Zoe

**What works today:**
- YouTube import: full pipeline with status tracking, background processing, API endpoints
- File picker abstraction: `IFilePickerService` ready to use
- Static parser: `VocabularyWord.ParseVocabularyWords()` exists but needs repository wiring
- Dedup: exists but inconsistent (case-sensitive in pipeline, case-insensitive in repo util)

**What's missing:**
- File import service layer (CSV/text parsing → resource creation)
- File import UI (no tab in `Import.razor`)
- Batch import status tracking (no `FileImport` entity like `VideoImport`)
- Standardized dedup strategy (case-insensitive + trim not enforced)

**Data integrity considerations:**
- `VocabularyWord` has NO `UserProfileId` — shared vocabulary across users
- Per-user data lives in `VocabularyProgress` (created lazily on first practice, NOT at import)
- Smart resources (`IsSmartResource = true`) are system-managed, import targets are user-created (`IsSmartResource = false`)
- Mappings support many-to-many: same word can link to multiple resources

**Migration path:**
- Any schema changes: use EF migrations (workaround multi-TFM gotcha)
- SQLite: add idempotent patch in `PatchMissingColumnsAsync` for mobile safety
- No schema changes needed for file import — existing models sufficient

**Recommended architecture hooks:**
1. New service: `VocabularyImportService` (scoped, registered in `SentenceStudioAppBuilder`)
2. Reuse: `IFilePickerService`, `LearningResourceRepository.SaveResourceAsync`, dedup via `GetWordByTargetTermAsync`
3. Add: File import UI tab in `Import.razor`, optional `FileImport` entity for status tracking (like `VideoImport`)
4. Standardize: Case-insensitive trimmed dedup in service layer (YouTube + file imports)

---

**Next step:** Zoe proposes architecture, Wash reviews data layer implications before implementation.
# AI Strategy for Data Import Feature

**Date:** 2026-04-26  
**Owner:** River (AI/Prompt Engineer)  
**Status:** 📋 Design — Ready for Zoe's architecture plan

---

## Overview

This document defines the AI strategy for importing vocabulary, phrases, and transcripts into SentenceStudio. The Captain wants to paste text or upload files, optionally describe the format OR let AI detect it, classify content type (Vocabulary | Phrases | Transcript | Auto), and target a new or existing LearningResource.

**Design philosophy:** Permissive grading. Accept reasonable variations, never reject for spelling. Fill missing translations gracefully. Structured JSON output via `SendPrompt<T>` with [Description] attributes.

---

## 1. AI Tasks Needed

### Task 1: Format Inference (when Captain skips format field)

**When:** Captain provides raw text/file but doesn't describe the format.

**Input:**
- First N lines of text (500-1000 chars for fast classification)
- Optional: File extension hint

**Output DTO (sketch):**
```csharp
public class ImportFormatInferenceResponse
{
    [Description("Detected format: CSV, TSV, JSON, LineDelimited, FreeFormText, Transcript")]
    public string DetectedFormat { get; set; }
    
    [Description("For delimited formats: the delimiter character (comma, tab, pipe, etc.)")]
    public string? Delimiter { get; set; }
    
    [Description("True if first row appears to be column headers")]
    public bool HasHeaderRow { get; set; }
    
    [Description("Suggested role for each column: TargetLanguage, NativeLanguage, Notes, Romanization, PartOfSpeech, Ignore")]
    public List<string> ColumnRoles { get; set; } = new();
    
    [Description("Confidence 0.0-1.0 in this format detection")]
    public float Confidence { get; set; }
    
    [Description("Human-readable explanation of the detection reasoning")]
    public string Notes { get; set; } = string.Empty;
}
```

**Confidence signal:** `>= 0.85` = auto-proceed. `< 0.85` = show UI confirmation with Notes.

**Reuse:** Similar to no existing template, but follows VocabularyExtractionResponse pattern with [Description] attributes.

---

### Task 2: Content Type Classification (Vocabulary vs Phrases vs Transcript)

**When:** Captain selects "Auto" for content type.

**Input:**
- First 10-20 rows of parsed data (after format inference or heuristic parse)
- Target language hint

**Output DTO (sketch):**
```csharp
public class ImportContentClassificationResponse
{
    [Description("Detected content type: Vocabulary, Phrases, Transcript, Mixed")]
    public string ContentType { get; set; }
    
    [Description("Confidence 0.0-1.0 in this classification")]
    public float Confidence { get; set; }
    
    [Description("Why this classification was chosen — cite specific clues from the data")]
    public string Reasoning { get; set; } = string.Empty;
    
    [Description("If Mixed: suggested split strategy (e.g., 'rows 1-50 Vocabulary, 51-100 Transcript')")]
    public string? SplitSuggestion { get; set; }
}
```

**Heuristics first (deterministic):**
- Single column of short terms (< 6 words each) → likely Vocabulary
- Pairs with arrow/equals separator (Korean → English, 한국어 = Korean) → Vocabulary
- Numbered speaker labels (Speaker 1:, A:, B:) or timestamps → Transcript
- Long multi-sentence paragraphs → Transcript
- Two-column with medium-length phrases → Phrases

**AI call only if:** Heuristics are inconclusive (confidence < 0.7).

**Reuse:** New task. Follows ExtractVocabularyFromTranscript pattern (uses TOPIK level knowledge, target language awareness).

---

### Task 3: Vocabulary Extraction (clean pairs from messy text)

**When:** Content type = Vocabulary, OR Classification response = Vocabulary.

**Input:**
- Parsed rows (after format inference + heuristic split)
- Target language
- Native language
- Proficiency level (for TOPIK level estimation if missing)

**Output DTO (REUSE EXISTING):**
```csharp
// DIRECTLY REUSE: src/SentenceStudio.Shared/Models/VocabularyExtractionResponse.cs
// Already has ExtractedVocabularyItem with all needed [Description] attributes
```

**Structured extraction goals:**
- Normalize dictionary forms (conjugated Korean → -다 form, nouns without particles)
- Fill missing NativeLanguageTerm if only TargetLanguageTerm provided (translation-fill)
- Infer PartOfSpeech, TopikLevel, LexicalUnitType
- Generate romanization if missing
- Tag unparseable lines separately (do NOT fail entire import)

**Permissiveness rules:**
- Accept spacing variations (띄어쓰기 mistakes common in Korean)
- Accept synonym definitions (multiple valid English translations for one Korean term)
- Auto-fill missing translations using AI (no blank rejection)
- Accept romanization variations (both revised and McCune-Reischauer, convert to revised)

**Reuse prompt:** `ExtractVocabularyFromTranscript.scriban-txt` (lines 1-75) — mirrors extraction rules, [Description] guidance, TOPIK level logic, LexicalUnitType classification, permissiveness for language learning content.

**Token strategy:** If > 1000 vocabulary rows, chunk into batches of 200-300 rows per call. Aggregate results.

---

### Task 4: Phrase Extraction

**When:** Content type = Phrases, OR Classification response = Phrases.

**Input:**
- Parsed rows (phrase text in target language, optional translation)
- Target language
- Native language

**Output DTO (sketch):**
```csharp
public class PhraseExtractionResponse
{
    [Description("List of extracted phrase entries")]
    public List<ExtractedPhraseItem> Entries { get; set; } = new();
    
    [Description("Rows that couldn't be parsed — return verbatim for manual review")]
    public List<string> UnparseableLines { get; set; } = new();
}

public class ExtractedPhraseItem
{
    [Description("Target language phrase (cleaned, normalized spacing)")]
    public string Phrase { get; set; } = string.Empty;
    
    [Description("Native language translation — if missing, generate one")]
    public string Translation { get; set; } = string.Empty;
    
    [Description("Optional usage context or example scenario")]
    public string? Context { get; set; }
    
    [Description("Romanization of the phrase")]
    public string? Romanization { get; set; }
    
    [Description("Estimated TOPIK level 1-6")]
    public int TopikLevel { get; set; } = 3;
    
    [Description("Comma-separated tags (topic, formality, usage context)")]
    public string? Tags { get; set; }
}
```

**Translation-fill rule:** If Translation is blank, AI generates natural native-language translation (permissive — accept the Captain's content even if incomplete).

**Reuse:** Hybrid of `ExtractVocabularyFromTranscript` (TOPIK level, romanization, tags) + `GetTranslations` (translation fill logic).

**Token strategy:** Batch 100-150 phrases per call. Phrases are longer than single vocabulary words.

---

### Task 5: Transcript Segmentation and Extraction

**When:** Content type = Transcript, OR Classification response = Transcript.

**Input:**
- Raw transcript text (or pre-segmented speaker turns if format inference detected them)
- Target language
- Native language
- Proficiency level

**Output DTO (sketch):**
```csharp
public class TranscriptExtractionResponse
{
    [Description("Segmented transcript units suitable for study (speaker turns, logical utterances, or topical chunks)")]
    public List<TranscriptSegment> Segments { get; set; } = new();
    
    [Description("Optional: extracted vocabulary terms from transcript for separate LearningResource")]
    public List<ExtractedVocabularyItem>? ExtractedVocabulary { get; set; }
}

public class TranscriptSegment
{
    [Description("Target language text for this segment (cleaned, properly punctuated)")]
    public string Text { get; set; } = string.Empty;
    
    [Description("Native language translation — generate if missing")]
    public string? Translation { get; set; }
    
    [Description("Speaker identifier if detected (A, B, Speaker 1, etc.)")]
    public string? Speaker { get; set; }
    
    [Description("Timestamp if detected (e.g., '00:12:34' or '12:34')")]
    public string? Timestamp { get; set; }
    
    [Description("Segment number in sequence (1-based)")]
    public int SequenceNumber { get; set; }
    
    [Description("Comma-separated tags for this segment (question, exclamation, formal, casual, etc.)")]
    public string? Tags { get; set; }
}
```

**Segmentation strategy:**
- If speaker labels detected → segment by speaker turn
- If timestamps detected → segment by timestamp boundaries (30-60 second chunks)
- If neither → AI segments into logical utterance boundaries (sentence-final endings in Korean: -다/-요/-까/-네/-군요)

**Optional vocabulary extraction:** If Captain wants both transcript segments AND vocabulary extraction, run two tasks:
1. Transcript segmentation (this task)
2. Vocabulary extraction (Task 3) against the full transcript text

**Reuse prompts:**
- Segmentation logic: similar to `CleanTranscript.scriban-txt` (line-by-line cleanup, punctuation repair)
- Vocabulary extraction: `ExtractVocabularyFromTranscript.scriban-txt` (full reuse)

**Token strategy:**
- Transcripts can be LARGE (10k+ chars). Chunk into 2000-3000 char segments for cleanup.
- For vocabulary extraction: run against full transcript (or aggregate chunks) with max_words cap (100-200).

**Permissiveness:**
- Accept missing speaker labels (generate generic Speaker A, B, C if needed for clarity)
- Accept missing timestamps (use sequence numbers instead)
- Accept mixed languages in transcript (extract only target language portions)
- Accept punctuation errors (AI cleans during segmentation)

---

## 2. DTO Design Summary

### Existing DTOs to reuse directly:
- **VocabularyExtractionResponse** (src/SentenceStudio.Shared/Models/VocabularyExtractionResponse.cs) — already has [Description] attributes, TOPIK level, LexicalUnitType, RelatedTerms, Tags. PERFECT for Task 3.
- **TranscriptCleanupResult** (src/SentenceStudio.Shared/Models/TranscriptCleanupResponse.cs) — document-only, not used for JSON deserialization, but pattern applies.

### New DTOs needed (sketched above with [Description] attributes):
- ImportFormatInferenceResponse (Task 1)
- ImportContentClassificationResponse (Task 2)
- PhraseExtractionResponse + ExtractedPhraseItem (Task 4)
- TranscriptExtractionResponse + TranscriptSegment (Task 5)

**[Description] philosophy:** Every property gets a [Description] attribute. Microsoft.Extensions.AI uses them automatically for prompt context. NO manual JSON formatting in Scriban templates — let the library serialize/deserialize.

**JsonPropertyName philosophy:** ONLY use [JsonPropertyName] when the AI must output a specific JSON field name that differs from C# property naming conventions (e.g., camelCase vs PascalCase). Otherwise, omit — the library handles it.

---

## 3. Heuristic-vs-AI Split (Routing Rule)

**Deterministic heuristics (no AI call needed):**
1. **CSV/TSV with clear header row** (e.g., "Korean,English" or "Term,Definition,Notes"):
   - Parse with CSV library
   - Map columns by header name (case-insensitive match for common variants: term/word/vocabulary, translation/definition/meaning, notes/context/example)
   - If ambiguous header → AI Task 1 (format inference)

2. **JSON array of objects** (e.g., `[{"term": "...", "definition": "..."}]`):
   - Deserialize with System.Text.Json
   - Map fields by name (support common variants: term/word/targetLanguageTerm, definition/meaning/nativeLanguageTerm)
   - If ambiguous structure → AI Task 1

3. **Line-delimited single column** (one term per line, no delimiter):
   - Split by newline
   - Content type = Vocabulary (assume dictionary form terms)
   - AI Task 3 for translation-fill (generate missing NativeLanguageTerm)

4. **Arrow/equals separator pairs** (e.g., "한국어 → Korean", "먹다 = to eat"):
   - Regex parse: `(.+?)\s*(?:→|=>|=|:)\s*(.+)`
   - Left = TargetLanguageTerm, Right = NativeLanguageTerm
   - Content type = Vocabulary

**AI-required scenarios (call Task 1 format inference):**
- Inconsistent delimiters (mixed commas/tabs/pipes within same file)
- No obvious header row (all rows look like data)
- Multi-column with unclear roles (which column is target language?)
- Free-form pasted text (paragraphs, no structure)
- Transcripts with mixed speaker labels and timestamps

**Content classification heuristics (Task 2 optional):**
- If heuristics give >= 0.7 confidence → auto-classify, skip AI
- If heuristics give < 0.7 confidence → AI Task 2 (classification)

**Routing decision tree:**
```
1. File extension hint? (CSV → heuristic CSV parse, JSON → heuristic JSON parse)
   ├─ Success + high confidence (>= 0.85)? → Use heuristic result
   └─ Failure or low confidence? → AI Task 1 (format inference)

2. Content type specified by Captain? → Use it, skip Task 2
   └─ Content type = Auto? → Run heuristic classification
       ├─ Confidence >= 0.7? → Use heuristic result
       └─ Confidence < 0.7? → AI Task 2 (classification)

3. Execute extraction:
   ├─ Vocabulary → AI Task 3 (reuse VocabularyExtractionResponse)
   ├─ Phrases → AI Task 4 (new PhraseExtractionResponse)
   └─ Transcript → AI Task 5 (new TranscriptExtractionResponse)
```

**Why this split?**
- CSV/JSON/arrow-separated are 80%+ of imports (common export formats from Anki, Quizlet, spreadsheets). Heuristics handle them fast and free.
- Free-form text, transcripts, and ambiguous delimiters are the 20% that genuinely need AI.
- Token cost optimization: heuristics save ~500-1000 tokens per import.

---

## 4. Confidence and Low-Confidence Handoff

**Confidence thresholds:**
- **>= 0.85:** Auto-proceed (high confidence)
- **0.70 - 0.84:** Show UI confirmation with reasoning, proceed if Captain approves
- **< 0.70:** Show UI warning + manual format selection fallback

**Where confidence appears:**
- Task 1 (format inference): `ImportFormatInferenceResponse.Confidence`
- Task 2 (content classification): `ImportContentClassificationResponse.Confidence`
- Task 3-5 do NOT have top-level confidence (they're extraction tasks, not classification)

**UI handoff pattern (for Zoe/Wash):**
```
IF confidence < 0.85 THEN
    Display: "I detected [DetectedFormat] with [ColumnRoles]. Does this look correct?"
    Show: Notes field (AI reasoning)
    Buttons: [Proceed] [Manual Override]
END IF
```

**UnparseableLines pattern (Task 3-5):**
- Extraction DTOs include `UnparseableLines` or similar field
- UI shows count: "Imported 284 items. 3 lines couldn't be parsed."
- Click to review → show unparseable lines in editable list (Captain can fix and re-submit)

**No rejection philosophy:**
- NEVER fail entire import due to partial parse errors
- NEVER reject for spelling mistakes, capitalization, spacing variations
- Extract what's extractable, flag the rest for manual review
- Auto-fill missing translations (permissive by default)

---

## 5. Cost / Token Strategy

### Model tier suggestions:
- **Default model:** GPT-4o-mini (current project standard, balance of cost and quality)
- **Format inference (Task 1):** Can downgrade to GPT-4o-mini or even GPT-3.5-turbo (simple classification, short input)
- **Content classification (Task 2):** GPT-4o-mini (simple classification)
- **Extraction tasks (3-5):** GPT-4o-mini (matches existing VocabularyExtraction usage, proven adequate)

**Token optimization:**
- **Format inference:** Limit input to first 500-1000 chars (enough to detect structure, delimiters, headers)
- **Content classification:** Limit input to first 10-20 rows (enough to classify content type)
- **Vocabulary extraction:** Batch 200-300 rows per call (balance latency vs token count)
- **Phrase extraction:** Batch 100-150 phrases per call (phrases are longer than vocab words)
- **Transcript segmentation:** Chunk 2000-3000 chars per call (avoid exceeding context window, maintain coherence)

### Chunking strategy for large imports:
```
IF row count > 300 (vocabulary) OR > 150 (phrases) OR char count > 3000 (transcript) THEN
    Split into batches
    Process batches in parallel (up to 3 concurrent calls to avoid rate limits)
    Aggregate results (merge arrays, deduplicate if needed)
END IF
```

**Rate considerations:**
- Current project: no rate limit handling visible in AiService.cs
- Recommendation: Add exponential backoff + retry for 429 errors if importing large datasets (500+ rows)
- Parallel calls: cap at 3 concurrent to avoid triggering rate limits

**Cost estimate (ballpark):**
- Format inference: 1 call × ~500 tokens = ~$0.001 per import (negligible)
- Content classification: 1 call × ~800 tokens = ~$0.002 per import (negligible)
- Vocabulary extraction (300 rows): 1-3 calls × ~2000 tokens each = ~$0.01-0.03 per import
- Transcript (10k chars): 3-5 calls × ~3000 tokens each = ~$0.03-0.05 per import

**Total per-import cost:** $0.01 - $0.10 depending on size. Acceptable for this use case.

---

## 6. Reuse of Existing Prompts

### Direct reuse:
1. **ExtractVocabularyFromTranscript.scriban-txt** (Task 3):
   - Lines 1-75: extraction rules, TOPIK level logic, LexicalUnitType classification, [Description] guidance, romanization rules
   - Permissiveness philosophy: "accept spacing variations, synonym definitions, auto-fill missing translations"
   - REUSE 90% of this prompt for vocabulary import (just swap "transcript" context with "imported data" context)

2. **GetTranslations.scriban-txt** (Task 3 & 4 — translation-fill logic):
   - Uses vocabulary list, generates natural native-language translations
   - Permissive grading: "accept reasonable variations"
   - REUSE for auto-filling missing NativeLanguageTerm in vocabulary/phrase imports

3. **CleanTranscript.scriban-txt** (Task 5 — transcript segmentation):
   - Line-by-line cleanup, punctuation repair
   - REUSE for transcript cleanup before segmentation

### New prompts needed:
1. **ImportFormatInference.scriban-txt** (Task 1) — NEW
   - Input: first N lines of raw text + optional file extension hint
   - Output: ImportFormatInferenceResponse (DetectedFormat, Delimiter, HasHeaderRow, ColumnRoles, Confidence, Notes)
   - Pattern: similar to classification prompts (clear instructions, [Description] attributes, confidence signal)

2. **ImportContentClassification.scriban-txt** (Task 2) — NEW
   - Input: first 10-20 rows of parsed data + target language hint
   - Output: ImportContentClassificationResponse (ContentType, Confidence, Reasoning)
   - Pattern: mirrors Task 1 structure

3. **ImportPhraseExtraction.scriban-txt** (Task 4) — NEW
   - Hybrid: ExtractVocabularyFromTranscript logic + GetTranslations translation-fill
   - Input: parsed phrase rows + target/native language
   - Output: PhraseExtractionResponse (Entries, UnparseableLines)

4. **ImportTranscriptSegmentation.scriban-txt** (Task 5) — NEW
   - Reuses CleanTranscript logic + adds segmentation boundaries + speaker/timestamp detection
   - Input: raw transcript text + target/native language
   - Output: TranscriptExtractionResponse (Segments, optional ExtractedVocabulary)

**Reuse ratio:** 60% reuse (ExtractVocabularyFromTranscript, GetTranslations, CleanTranscript patterns), 40% new (format inference, classification, segmentation scaffolding).

---

## 7. Permissiveness for Language Learning Content

**Project rule:** "Permissive grading philosophy. Accept reasonable variations, never reject for spelling. Fill missing translations gracefully."

**How this applies to import:**

### Vocabulary/Phrase extraction:
- **Spelling:** Accept Korean spacing mistakes (띄어쓰기 errors common for learners)
- **Romanization:** Accept both Revised and McCune-Reischauer, convert to Revised automatically
- **Capitalization:** Normalize (Korean has no case, but English definitions may vary)
- **Synonym definitions:** Accept multiple valid translations (e.g., "먹다 = to eat / to consume / to have a meal" → take first, tag rest)
- **Missing translations:** Auto-generate via AI (Task 3/4 translation-fill) — NEVER leave blank or reject
- **Conjugated forms:** Auto-normalize to dictionary form (Korean -다 form, nouns without particles)
- **PartOfSpeech ambiguity:** If uncertain, default to "expression" (catch-all)
- **TOPIK level uncertainty:** If unsure, default to level 3 (mid-intermediate, safe default)

### Transcript extraction:
- **Missing punctuation:** AI adds during cleanup (CleanTranscript pattern)
- **Mixed languages:** Extract only target language portions, ignore filler in native language (common in learning videos)
- **Speaker ambiguity:** Generate generic labels (Speaker A, B, C) if not clear
- **Timestamp ambiguity:** Use sequence numbers if no timestamps detected
- **Incomplete sentences:** Accept fragments (transcripts often have interruptions, false starts)

### Format inference:
- **Inconsistent delimiters:** If 80%+ rows use comma, treat as CSV (ignore outlier rows)
- **Ambiguous column roles:** If unsure, default to [TargetLanguage, NativeLanguage, Notes] (most common)
- **No header row:** Infer from first data row structure (e.g., "한국어, English" pattern → treat first row as header)

**Error handling philosophy:**
- Extract the good, flag the bad (UnparseableLines), NEVER fail entire import
- Log warnings, not errors (permissive = forgiving)
- Default to safe assumptions (e.g., Vocabulary > Phrases > Transcript if uncertain)

---

## Implementation Notes for Zoe

**Service architecture suggestion (not code, just guidance):**
```
ImportService (new)
├── ParseFormat() — heuristics first, AI Task 1 fallback
├── ClassifyContent() — heuristics first, AI Task 2 fallback
├── ExtractVocabulary() — AI Task 3, reuse VocabularyExtractionResponse
├── ExtractPhrases() — AI Task 4, new PhraseExtractionResponse
└── ExtractTranscript() — AI Task 5, new TranscriptExtractionResponse

All call AiService.SendPrompt<T> with Scriban-rendered prompts.
```

**Scriban template rendering:**
- Load .scriban-txt files from embedded resources (existing pattern in LearningResourceRepository)
- Render with Scriban.Template.Parse + template.Render(model)
- Pass rendered string to AiService.SendPrompt<T>

**LearningResource creation flow:**
1. Captain selects "New LearningResource" OR "Add to existing [Resource Name]"
2. Import extracts vocabulary/phrases/transcript
3. Service creates VocabularyWord entities from extraction DTOs
4. Service associates words with LearningResource via ResourceVocabularyMappings (existing pattern)
5. For transcripts: optionally create TWO resources (one for segments, one for extracted vocabulary)

**Error UX:**
- Show progress: "Analyzing format... (1/3)" → "Extracting vocabulary... (2/3)" → "Saving to database... (3/3)"
- On low confidence: modal with "Does this look correct?" + Notes field + [Proceed] [Manual Override]
- On UnparseableLines: toast notification "Imported 284 items. 3 lines need review." + clickable link to review list

---

## Open Questions for Captain

1. **Transcript vocabulary extraction:** Should we ALWAYS extract vocabulary from transcripts, or make it optional? (Recommendation: optional checkbox — some transcripts are study content, others are just listening practice)

2. **Duplicate handling:** If imported vocabulary already exists in database (exact TargetLanguageTerm match), should we:
   - Skip (avoid duplicates)
   - Update (merge definitions/tags)
   - Create new (keep both)
   - Ask Captain each time
   (Recommendation: Skip with notification "5 words already exist, skipped")

3. **LexicalUnitType override:** Should Captain be able to override AI's LexicalUnitType classification (Word/Phrase/Sentence) during import review? (Recommendation: yes, add UI override for ambiguous cases)

4. **Batch import limit:** Hard cap on import size to avoid UI freeze? (Recommendation: 2000 vocabulary rows, 1000 phrases, 50k chars for transcripts — warn if exceeded)

---

## Next Steps

1. **Zoe:** Architecture plan (ImportService, UI flow, error handling, LearningResource wiring)
2. **River (future):** Write 4 new Scriban templates (after architecture approved):
   - ImportFormatInference.scriban-txt
   - ImportContentClassification.scriban-txt
   - ImportPhraseExtraction.scriban-txt
   - ImportTranscriptSegmentation.scriban-txt
3. **Wash:** Implement ImportService + UI pages
4. **Jayne:** E2E test scripts for import scenarios

---

## References

**Existing code examined:**
- `src/SentenceStudio.Shared/Services/AiService.cs` — SendPrompt<T> pattern (lines 45-74)
- `src/SentenceStudio.Shared/Models/VocabularyExtractionResponse.cs` — [Description] attribute pattern, TOPIK level, LexicalUnitType (lines 1-94)
- `src/SentenceStudio.AppLib/Resources/Raw/ExtractVocabularyFromTranscript.scriban-txt` — extraction rules, permissiveness philosophy (lines 1-75)
- `src/SentenceStudio.AppLib/Resources/Raw/GetTranslations.scriban-txt` — translation generation pattern (lines 1-24)
- `src/SentenceStudio.AppLib/Resources/Raw/CleanTranscript.scriban-txt` — transcript cleanup logic
- `src/SentenceStudio.Shared/Services/SmartResourceService.cs` — no direct AI usage, but document for LearningResource wiring pattern

**Project conventions:**
- [Description] attributes on DTO properties (Microsoft.Extensions.AI uses them)
- NO manual JSON formatting in Scriban templates
- Permissive grading (accept variations, fill missing, never reject)
- Scriban templates in `src/SentenceStudio.AppLib/Resources/Raw/*.scriban-txt`
- DTOs in `src/SentenceStudio.Shared/Models/*Response.cs`
# UI Pattern Scout: Import Page (Blazor)
**Kaylee, Planning Phase**  
Date: 2026-04-24 | For: Zoe | Status: Findings only (no code edits)

---

## 1. Existing Admin/Settings/Management Pages

### Blazor Pages (src/SentenceStudio.UI/Pages/)

| Page | File | Pattern | Notes |
|------|------|---------|-------|
| **Settings** | `Settings.razor` (484 lines) | Tabbed form groups (Appearance, Voice/Quiz, Data Management, Debug/DB) | Theme swatches, radio button groups, range sliders, spinners on async ops, Toast notifications. Uses `ThemeService` + `VoiceDiscoveryService` for async state mgmt. Cards (`card-ss p-4`) organize settings into sections with centered spinners during load. |
| **Import** | `Import.razor` (28.7 KB) | Multi-tab feature (Channels, Single Video, History) with YouTube-specific flows | Demonstrates tab nav pattern (btn-group), list-group for history, card-based channel display with toggle switch. Uses multi-step URL→transcript→polish→save flow. Error/success alerts inline. |
| **ResourceAdd** | `ResourceAdd.razor` (389 lines) | Multi-card CRUD form with file & text import | PageHeader with back button, basic info card, media content card (conditional), vocab section with paste/file import. Features InputFile + delimiter radio buttons + preview table. |
| **ResourceEdit** | `ResourceEdit.razor` (24.4 KB) | Full resource editor with nested vocab table | Similar card structure; extends ResourceAdd with transcript/translation textareas. Delete via dropdown action. |
| **Resources** | `Resources.razor` (290 lines) | List page with search + filter + dual view mode (grid/list) | Demonstrates resource **selection/lookup pattern**: search bar, media-type + language dropdowns, grid/list toggle, Virtualize for perf, empty state with CTA buttons. MediaType icons via `GetMediaTypeIcon()` helper. |
| **Profile** | `Profile.razor` | User profile form | Not yet examined in detail. |
| **Onboarding** | `Onboarding.razor` (387 lines) | Multi-step onboarding flow | Not yet examined; candidate for multi-step form pattern. |

**Takeaway:** Settings is lightweight single-page; Import is multi-tab with nested forms; ResourceAdd/Edit show card-based CRUD. Resources shows polished list/lookup (search, filter, dual views, Virtualize).

---

## 2. File Picker Usage & Patterns

### Abstraction Layer (IFilePickerService)
**File:** `src/SentenceStudio.Shared/Abstractions/IFilePickerService.cs`

```csharp
public interface IFilePickerService
{
    Task<FilePickerResult?> PickAsync(FilePickerRequest request, CancellationToken cancellationToken = default);
}

public record FilePickerRequest(string? Title, IReadOnlyCollection<string>? FileTypes);
public record FilePickerResult(string FileName, Stream Content);
```

**Why it matters for Import:** Shared abstraction means Import UI can work in both Blazor (WebApp) and MAUI (AppLib) contexts.

### Blazor Implementation
**File:** `src/SentenceStudio.WebApp/Platform/WebFilePickerService.cs`

- **Method:** JSInterop → `filePickerInterop.pickFile(acceptTypes)`
- **Returns:** FilePickerJsResult (FileName + byte[] Content)
- **Wraps:** Raw JS file picker; accepts comma-delimited file types (e.g., `.txt,.csv`)
- **Usage in ResourceAdd.razor:** `InputFile` component (lines 119–130)
  - `OnChange="HandleFileImport"`, max 1 MB, accepts `.txt,.csv`
  - Reads stream, parses vocab via `VocabularyWord.ParseVocabularyWords(content, delimiter)`
  - Shows spinner + status text during import; Toast on success/error

**Takeaway:** Blazor uses two patterns:
1. **InputFile** (built-in `<InputFile>`) for direct file upload → stream → parse
2. **IFilePickerService** abstraction for cross-platform scenarios (not yet used in Blazor, but available)

### MAUI Implementation
**File:** `src/SentenceStudio.AppLib/Abstractions/MauiFilePickerService.cs`

- **Method:** `FilePicker.Default.PickAsync()` (MAUI.Storage)
- **Returns:** FilePickerResult (FileName + Stream)
- **Used in:** MAUI-only features (not examined yet)

**Takeaway:** MAUI has native FilePicker built-in; Blazor uses InputFile or JS interop.

---

## 3. Form Patterns & Validation

### Pattern: Bootstrap Cards + Sections
**Seen in:** Settings.razor, ResourceAdd.razor, ResourceEdit.razor

```razor
<div class="card card-ss p-4 mb-3">
    <h5 class="ss-title3 mb-3">Section Title</h5>
    <div class="mb-3">
        <label class="form-label ss-body2 text-secondary-ss">Label</label>
        <input type="text" class="form-control form-control-ss" @bind="model.Field" />
    </div>
</div>
```

**Key classes:**
- `card-ss` — custom card style
- `form-control-ss` — custom input styling
- `ss-title3`, `ss-body2` — theme typography
- `text-secondary-ss` — theme text color

### Pattern: Multi-Step Flows
**Seen in:** Import.razor (URL → Transcript → Polish → Save)

1. **Step 1:** Input field + button to trigger fetch
2. **Step 2 (conditional):** Show transcript after fetch; allow edit
3. **Step 3:** Polish button + Save button
4. **Success state:** Alert + View/ImportAnother CTA buttons

**Error handling:** Inline alert (`alert-danger`) or Toast.

### Pattern: File Import + Delimiter Selection
**Seen in:** ResourceAdd.razor (lines 96–130)

```razor
<div class="d-flex align-items-center gap-3 mb-3">
    <div class="form-check">
        <input class="form-check-input" type="radio" name="delimiter" id="delimComma"
               checked="@(delimiter == "comma")" @onchange="SetDelimiterComma" />
        <label class="form-check-label ss-body2" for="delimComma">@Localize["ResourceAdd_CommaDelimiter"]</label>
    </div>
    <div class="form-check">
        <input class="form-check-input" type="radio" name="delimiter" id="delimTab"
               checked="@(delimiter == "tab")" @onchange="SetDelimiterTab" />
        <label class="form-check-label ss-body2" for="delimTab">@Localize["ResourceAdd_TabDelimiter"]</label>
    </div>
    <button class="btn btn-ss-secondary btn-sm ms-auto" @onclick="ImportVocabulary" disabled="@string.IsNullOrWhiteSpace(vocabList)">
        @Localize["ResourceAdd_ImportVocabulary"]
    </button>
</div>
```

**Pattern:** Radio button pair + action button (disabled when input empty).

### Pattern: Preview Table
**Seen in:** ResourceAdd.razor (lines 132–177)

```razor
<table class="table table-sm table-hover">
    <thead><tr><th>Target Term</th><th>Native Term</th><th>Type</th><th>Tags</th><th></th></tr></thead>
    <tbody>
        @foreach (var word in resource.Vocabulary)
        {
            <tr>
                <td>@word.TargetLanguageTerm</td>
                <td>@word.NativeLanguageTerm</td>
                <td><select class="form-select form-select-sm" @bind="word.LexicalUnitType">...)</td>
                <td><small>@(string.IsNullOrEmpty(word.Tags) ? "—" : word.Tags)</small></td>
                <td><button class="btn btn-sm btn-outline-danger" @onclick="() => RemoveVocabularyWord(word)"><i class="bi bi-x"></i></button></td>
            </tr>
        }
    </tbody>
</table>
<small class="text-secondary-ss">@string.Format(Localize["ResourceAdd_VocabWordsAdded"], resource.Vocabulary.Count)</small>
```

**Pattern:** Responsive table with inline edit (dropdowns), delete buttons (bi-x icon), count summary.

### Validation
**Seen in:** ResourceAdd.razor SaveResource() (lines 225–258)

```csharp
if (string.IsNullOrWhiteSpace(resource.Title))
{
    Toast.ShowError($"{Localize["ResourceAdd_TitleRequired"]}");
    return;
}
```

**Pattern:** Explicit null/empty checks → Toast errors (not form validation UI). No DataAnnotations-based client validation visible.

### Notification Pattern: Toast
**Seen in:** All pages via `[Inject] private ToastService Toast { get; set; }`

```csharp
Toast.ShowSuccess($"{Localize["ResourceAdd_SavedSuccess"]}");
Toast.ShowError($"{string.Format(Localize["ResourceAdd_SaveFailed"], ex.Message)}");
Toast.ShowInfo($"{Localize["Settings_ModalityRequired"]}");
```

**Pattern:** Non-blocking, auto-dismiss toasts for user feedback. No inline error fields.

---

## 4. Resource List / Lookup UI

### Pattern: Search + Filter + View Mode Toggle
**File:** `src/SentenceStudio.UI/Pages/Resources.razor` (lines 26–58)

```razor
<div class="d-flex flex-wrap gap-2 mb-4 align-items-center">
    <input type="text" class="form-control form-control-ss w-100 w-md-auto flex-md-grow-1"
           placeholder='@Localize["Resources_SearchPlaceholder"]'
           @bind="searchText" @bind:after="SearchResources" />
    <select class="form-select form-control-ss" style="max-width: 180px;" @bind="filterType" @bind:after="FilterResources">
        <option value="All">@Localize["Resources_AllTypes"]</option>
        @foreach (var type in mediaTypes) { <option value="@type">@type</option> }
    </select>
    <select class="form-select form-control-ss" style="max-width: 180px;" @bind="filterLanguage" @bind:after="FilterResources">
        <option value="All">@Localize["Resources_AllLanguages"]</option>
        @foreach (var lang in languages) { <option value="@lang">@lang</option> }
    </select>
    <div class="ms-auto btn-group btn-group-sm flex-shrink-0" role="group">
        <button class="btn @(viewMode == "grid" ? "btn-secondary" : "btn-outline-secondary")" @onclick='() => SetViewMode("grid")'><i class="bi bi-grid-3x3-gap"></i></button>
        <button class="btn @(viewMode == "list" ? "btn-secondary" : "btn-outline-secondary")" @onclick='() => SetViewMode("list")'><i class="bi bi-list-ul"></i></button>
    </div>
</div>
```

**Grid view (lines 82–101):**
```razor
<div class="row g-3">
    <Virtualize Items="resources" Context="resource">
        <div class="col-12 col-md-6 col-lg-4 mb-3">
            <div class="card card-ss p-3" role="button" @onclick="() => EditResource(resource.Id)">
                <div class="d-flex align-items-start gap-3">
                    <i class="bi @GetMediaTypeIcon(resource.MediaType) fs-4"></i>
                    <div class="flex-grow-1 overflow-hidden">
                        <h6 class="ss-title3 mb-1 text-truncate">@resource.Title</h6>
                        <small class="text-secondary-ss">@resource.MediaType • @resource.Language</small>
                    </div>
                    <small class="text-secondary-ss text-nowrap">@resource.CreatedAt.ToString("d")</small>
                </div>
            </div>
        </div>
    </Virtualize>
</div>
```

**List view (lines 104–114):**
```razor
<div class="list-group">
    <Virtualize Items="resources" Context="resource">
        <button class="list-group-item list-group-item-action d-flex align-items-center gap-3 py-2" @onclick="() => EditResource(resource.Id)">
            <i class="bi @GetMediaTypeIcon(resource.MediaType) fs-5"></i>
            <span class="fw-semibold flex-grow-1 text-truncate">@resource.Title</span>
            <span class="text-secondary-ss d-none d-md-inline text-nowrap">@resource.MediaType • @resource.Language</span>
            <small class="text-secondary-ss text-nowrap">@resource.CreatedAt.ToString("d")</small>
        </button>
    </Virtualize>
</div>
```

**Empty state (lines 60–74):**
```razor
@if (resources.Count == 0)
{
    <div class="text-center p-5">
        <p class="ss-body1 text-secondary-ss mb-4">@Localize["Resources_NoResourcesFound"]</p>
        <div class="d-flex gap-2 justify-content-center">
            <button class="btn btn-ss-primary" @onclick="AddResource">@Localize["Resources_AddFirstResource"]</button>
            <button class="btn btn-ss-secondary" @onclick="CreateStarterResource" disabled="@isCreatingStarter">...</button>
        </div>
    </div>
}
```

**Key patterns:**
- **Search + filter live update:** `@bind:after` triggers search on every keystroke + filter change
- **Virtualize for performance:** Only renders visible rows
- **Dual view modes:** Grid (cards) + List (button list); persisted via `Preferences`
- **Responsive layout:** `w-100 w-md-auto flex-md-grow-1` on search; `d-none d-md-inline` hides metadata on mobile
- **Empty state:** Centered text + CTA buttons (Add, Create Starter)
- **Icons:** MediaType via `GetMediaTypeIcon()` helper using `bi-*` class

**Takeaway:** Reusable lookup/selection UI pattern: search + filter + dual views + Virtualize + empty state.

---

## 5. Navigation Placement Recommendation

### Current Navigation Structure
**File:** `src/SentenceStudio.UI/Layout/NavMenu.razor` (lines 52–61)

```csharp
private NavItem[] TopItems => new[]
{
    new NavItem("dashboard",    "bi-house-door",         Localize["Nav_Dashboard"]),
    new NavItem("activity",     "bi-calendar3",          Localize["Nav_Activity"]),
    new NavItem("resources",    "bi-book",               Localize["Nav_LearningResources"]),
    new NavItem("vocabulary",   "bi-card-text",          Localize["Nav_Vocabulary"]),
    new NavItem("minimal-pairs","bi-soundwave",          Localize["Nav_MinimalPairs"]),
    new NavItem("skills",       "bi-bullseye",           Localize["Nav_Skills"]),
    new NavItem("import",       "bi-box-arrow-in-down",  Localize["Nav_Import"]),
};

private NavItem[] BottomItems => new[]
{
    new NavItem("profile",  "bi-person",    Localize["Nav_Profile"]),
    new NavItem("settings", "bi-gear",      Localize["Nav_Settings"]),
    new NavItem("feedback", "bi-chat-dots", Localize["Nav_Feedback"]),
};
```

**Route:** `/import` → defined in `src/SentenceStudio.UI/Pages/Import.razor:1` as `@page "/import"`

**Icon:** `bi-box-arrow-in-down` (download/import semantics ✓)

**Current placement:** Top-level nav item, below Skills, above Profile/Settings (data import feels lower priority than activities/resources but higher than settings).

### Recommendation
✅ **Keep Import at top level.** Rationale:
1. **Precedent:** Already in NavMenu as primary nav item (not buried in menu dropdown)
2. **Parity with Resources:** Users add content via Resources (manual) or Import (automated/bulk) — companion features deserve same nav weight
3. **Icon semantics:** `bi-box-arrow-in-down` (import) is clearer than alternatives; consistent with Bootstrap icon vocabulary

**Alternative placements considered:**
- ❌ **Sub-item under Resources:** Would break UI convention (all other sections are top-level)
- ❌ **Dropdown menu under Settings:** Import is a data workflow, not a preference
- ✅ **Tab within Resources page:** Could work as "Import" tab alongside list view; not done currently; would require Resources.razor refactor

**Final call:** Maintain current placement (`import` in `TopItems`, icon `bi-box-arrow-in-down`).

---

## 6. Blazor-vs-MauiReactor Parity

### Current State: Blazor-Heavy Web App

**Web presence:**
- `src/SentenceStudio.WebApp/` (ASP.NET Core Blazor Server app, net10.0)
- `src/SentenceStudio.UI/` (Blazor component library, net10.0)
  - All pages (Import, Resources, Settings, etc.) are **Blazor-only**
  - Uses Blazor-specific components: `InputFile`, `Virtualize`, `PageHeader`

**MAUI presence:**
- `src/SentenceStudio.AppLib/` (Shared MAUI library, net10.0 + UseMaui=true)
  - Primarily backend: services, data access, shared logic
  - Does NOT contain activity pages; HelpKit (external sample integration) handles native UI
- `src/SentenceStudio.iOS/`, `src/SentenceStudio.Android/`, `src/SentenceStudio.Windows/` (platform heads)
  - No MauiReactor pages found (yet)

### Convention: "Shared Logic, Separate UI"

1. **Shared data access:** LearningResourceRepository, VocabularyWordService in AppLib (net10.0)
2. **Shared models:** LearningResource, VocabularyWord in Shared
3. **Separate UI:** Blazor in WebApp (src/SentenceStudio.UI/Pages/), no desktop/mobile MAUI pages currently shipped

### Implication for Import Page

**Status:** Blazor-only (matching existing pattern).

- **If new Import page is Blazor:**
  - Place in `src/SentenceStudio.UI/Pages/Import.razor` (already exists at 28.7 KB; likely template/stub)
  - Reuse IFilePickerService abstraction (ready in Shared)
  - Use InputFile (Blazor built-in) for file uploads
  - Follow Settings/ResourceAdd/ResourceEdit card + form patterns

- **If native MAUI/MauiReactor Import is needed in future:**
  - Create `src/SentenceStudio/Pages/ImportPage.cs` (MauiReactor fluent syntax)
  - Use IFilePickerService via MauiFilePickerService
  - Mimic Blazor form structure in MauiReactor (VStack, HStack, Pickers, etc.)
  - Share ViewModels between platforms (currently not done; each platform has its own UI)

### Takeaway
**No MauiReactor parity required for Zoe's Import design phase.** All existing management pages are Blazor-only. Ship Blazor first; if native MAUI equivalent is needed later, migrate data access logic (already portable) and rewrite UI in MauiReactor.

---

## Summary for Zoe

### What's Already Built
1. **Import.razor exists** (28.7 KB) — likely YouTube-only template; reusable structure for new import types
2. **File picker abstractions ready** — IFilePickerService + WebFilePickerService (Blazor) + MauiFilePickerService (MAUI)
3. **Form patterns proven** — Settings (lightweight), ResourceAdd (multi-card), Resources (search + filter + dual views)
4. **Resource lookup UI polished** — Virtualize, search, filter, grid/list toggle, empty states
5. **Navigation slot ready** — Import already in top nav (line 60, NavMenu.razor)

### Key Decisions to Make
1. **Multi-step import flow vs. single-form?** (Import.razor shows multi-step; ResourceAdd shows single-form + preview)
2. **Where does user "select resource to import into" fit?** (New design decision; Resources.razor shows pattern for resource lookup)
3. **File type support?** (InputFile already accepts `.txt,.csv`; can extend to `.xlsx`, `.json`, etc.)
4. **Delimiter/format detection** (ResourceAdd.razor shows manual radio buttons; could auto-detect)

### UI Components to Leverage
- `PageHeader` — title + back button + primary/secondary actions (ResourceAdd.razor:6)
- `card-ss` + form groups — Settings.razor shows multi-section pattern
- `Toast` notifications — error/success feedback (all pages)
- `Virtualize` + search + filter — Resources.razor shows lookup pattern
- Preview table — ResourceAdd.razor (lines 132–177) for import preview

### Files to Study Before Design
- `src/SentenceStudio.UI/Pages/Import.razor` — current template (28.7 KB)
- `src/SentenceStudio.UI/Pages/ResourceAdd.razor` — form + file import + preview (389 lines)
- `src/SentenceStudio.UI/Pages/Resources.razor` — resource selection pattern (290 lines)
- `src/SentenceStudio.UI/Layout/NavMenu.razor` — import nav placement (157 lines)
- `src/SentenceStudio.Shared/Abstractions/IFilePickerService.cs` — file picker contract

---

**Findings complete. Ready for architecture phase with Zoe.**
### 2026-04-24T22:28:00Z: User directive — Import scope
**By:** David Ortinau (Captain, via Copilot)
**What:** The existing Import.razor (YouTube subscription / video transcript pipeline) should be considered SEPARATELY from the new generic data import feature. The new feature should not be designed around the YouTube flow. Captain is open to being convinced otherwise.
**Why:** Scope clarification — keeps the new import feature focused on text/file → vocabulary/phrases/transcript without coupling to video subscription concerns.
---

## Data Import MVP — Wave 1 Implementation — 2026-04-24

### Wash: ContentImportService API Surface + Transaction Pattern

**Date:** 2026-04-24  
**Status:** ✅ Complete  
**Scope:** Backend service skeleton with production-quality commit logic, scoped DI registration, locked API surface for Wave 1

**Key Decisions:**
- **CommitImportAsync:** Full transaction pattern mirroring `LearningResourceRepository.SaveResourceAsync`; all words + mappings + resource update in single SaveChangesAsync
- **Dedup Rule:** Case-sensitive, whitespace-trimmed on `TargetLanguageTerm` only; `NativeLanguageTerm` explicitly excluded (allows multiple English definitions for same Korean word)
- **Dedup Modes:** Skip (default/safest), Update (mutates shared word, affects all resources), ImportAll (creates duplicate)
- **DI Lifetime:** Scoped (transient operations, no shared state)
- **Transaction Safety:** Detach nav props on Update mode to prevent EF Core cascade-insert errors; HashSet check prevents duplicate mappings within single import
- **Wave 1 Scope:** CommitImportAsync body, DI registration, dedup logic, DTO surface locked
- **Wave 2 Placeholders:** Format detection (MVP stubs), AI fallback (free-form text, phrases, transcripts), single-column translation

**MVP API Surface:**
- `ParseContentAsync(ContentImportRequest, CancellationToken)` → `ContentImportPreview` (rows + validation errors)
- `DetectContentType(string, string?)` → `ContentTypeDetectionResult` (MVP: stub, returns explicit type)
- `CommitImportAsync(ContentImportCommit, CancellationToken)` → `ContentImportResult` (counts + warnings)

**DTOs:** `ContentImportRequest`, `ContentImportPreview`, `ImportRow`, `ContentImportCommit`, `ImportTarget`, `DedupMode`, `ContentImportResult`, `ContentType`, `RowStatus`, `ImportTargetMode` — all with `[Description]` attributes per Microsoft.Extensions.AI conventions

**Files Created:** `src/SentenceStudio.Shared/Services/ContentImportService.cs` (566 lines)

**Files Modified:** `src/SentenceStudio.AppLib/Services/CoreServiceExtensions.cs` (scoped registration, line ~96)

**Build:** ✅ 0 errors, 164 warnings (pre-existing)

---

### Kaylee: Media Import Rename Strategy

**Date:** 2026-04-24  
**Status:** ✅ Implemented  
**Scope:** Rename YouTube import → Media Import, move `/import` → `/media-import`, free up `/import` namespace for Wave 2+ generic content-import

**Key Decisions:**
- **Dual @page directives** (Option 1) — simpler, no redirect hops, immediate resolution, both routes work transparently
- **Routes:** `MediaImport.razor` owns both `/media-import` (primary) + `/import` (back-compat); child routes (`ChannelDetail`) updated similarly
- **Navigation:** `NavigationMemoryService` section key changed to `media-import`; icon changed to `bi-camera-video` (was `bi-box-arrow-in-down`); label → `Localize["Nav_MediaImport"]`
- **Localization:** Added `Nav_MediaImport` (EN: "Media Import", KO: "미디어 가져오기"); kept `Nav_Import` for future generic import page
- **Bookmarks/Deep-links:** All `/import*` routes continue to work (no breaking changes for existing users)

**Alternatives Considered:**
- Redirect middleware (rejected: more complex, extra hop, UX inconsistency)

**Future Constraints:**
- Wave 3 must NOT create `/import` route (already owned by MediaImport)
- New page will use `/import-content` instead per Captain's ruling

**Files Modified:**
1. `src/SentenceStudio.UI/Pages/Import.razor` → `MediaImport.razor` (component rename + dual routes)
2. `src/SentenceStudio.UI/Pages/ChannelDetail.razor` (routes + NavigateTo calls)
3. `src/SentenceStudio.UI/Layout/NavMenu.razor` (section key, icon, label)
4. `src/SentenceStudio.UI/Services/NavigationMemoryService.cs` (section definition)
5. `src/SentenceStudio.Shared/Resources/Strings/AppResources.resx` + `.ko.resx` (Nav_MediaImport)

**Build:** ✅ 267 warnings, 0 errors

---

### River: FreeTextToVocab AI Prompt — Behavior Contract

**Date:** 2026-04-28  
**Status:** ✅ Template + DTO Complete — Ready for Wave 2 Wiring  
**Scope:** Two Scriban templates + response DTOs for AI fallback paths in content import

**Key Decisions:**
- **FreeTextToVocab:** Extracts vocabulary from free-form pasted text (mixed languages, prose, typos, etc.); returns confidence-scored items (high/medium/low); deduplicates within response
- **TranslateMissingNativeTerms:** Bulk-translates target-language terms list (single-column CSV fallback); returns translations in same order; validates with `[unknown]` for gibberish
- **Output Guarantees:** Never drops rows silently; uncertain items get confidence flag + notes instead of being skipped
- **Confidence Scoring:** `"high"` (clearly extractable), `"medium"` (term correct, definition uncertain), `"low"` (uncertain if should extract); UI will badge each level
- **Lexical Units:** Word/Phrase/Sentence classification per existing `ExtractVocabularyFromTranscript` logic
- **Dictionary Form Normalization:** Verbs → `-다` form, nouns → no particles, compounds like `공부하다` as single Word
- **Related Terms:** List of constituent words in dictionary form for Phrases/Sentences

**Template: FreeTextToVocab.scriban-txt**
- Inputs: `source_text`, `target_language`, `native_language`, optional `format_hint`, `topik_level`
- Output: JSON array of extracted items with confidence, part-of-speech, lexical unit type, notes, related terms

**Template: TranslateMissingNativeTerms.scriban-txt**
- Inputs: `terms` (list), `target_language`, `native_language`
- Output: JSON array of translation pairs in input order, with unknown-term safeguard

**DTO Converter Pattern:** `.ToVocabularyWord()` method on extraction response to convert AI DTOs into persistable `VocabularyWord` entities (tags include confidence/notes/constituents if not all defaults)

**Files Created:**
1. `src/SentenceStudio.AppLib/Resources/Raw/FreeTextToVocab.scriban-txt`
2. `src/SentenceStudio.Shared/Models/FreeTextVocabularyExtractionResponse.cs` + nested `ExtractedVocabularyItemWithConfidence`
3. `src/SentenceStudio.AppLib/Resources/Raw/TranslateMissingNativeTerms.scriban-txt`
4. `src/SentenceStudio.Shared/Models/BulkTranslationResponse.cs` + nested `TranslationPair`

**Cost Estimate:** Free-text (~$0.01-0.02), translation-fill (~$0.005-0.01) → total ~$0.01-0.03 per messy import (acceptable per plan)

**Wave 2 Integration Guidance for Wash:**
- Call `ExtractVocabularyFromFreeTextAsync()` during ParseContentAsync
- Call `TranslateMissingNativeTermsAsync()` for single-column imports
- Apply Wash's dedup logic afterward

**Wave 3 Integration Guidance for Kaylee:**
- Confidence badges: green (high), yellow (medium), red (low) with notes tooltip
- Editable preview: override confidence, edit native term, delete rows
- Translation-fill UI: single-column CSV detected → show "Translate missing definitions?" checkbox

**References:** ExtractVocabularyFromTranscript.scriban-txt (lines 1-75), GetTranslations.scriban-txt (lines 1-24), VocabularyExtractionResponse.cs, TranslationDto.cs

---
