# Test Plan — First-Sync Login Routing Fix

**Author:** Tester
**For:** Captain (David)
**Scope:** Fix for `LoginPage.razor:127` short-circuit to `/onboarding` that ignores server-side profile state and in-flight sync.
**Status:** Drafted from requirements ahead of Lead's implementation plan. Reconcile after Lead lands.

> **Note on locations Captain referenced:**
> - The unit test project is `tests/SentenceStudio.UnitTests/` (not `tests/SentenceStudio.Tests/`). All proposed unit tests land there.
> - There is no `.squad/agents/tester/charter.md` / `history.md` yet — Tester is a new role. Proceeding from first principles; charter to be backfilled by Coordinator.

---

## 0. The bug, restated for test purposes

**User-visible symptom:** Captain wipes Mac Catalyst install → reinstalls → signs in to an existing account that has data on the server → app drops him onto **`/onboarding`** instead of pulling his data and showing the populated dashboard.

**Root cause:** Three coupled gaps:

1. **`LoginPage.razor:127`** — `var returnUrl = Preferences.Get("is_onboarded", false) ? "/" : "/onboarding";`
   On a fresh install, `is_onboarded` is `false`, so login always routes to `/onboarding` regardless of whether the just-authenticated identity has a server profile.
2. **`MainLayout.razor:119–136`** — has a fallback that *would* set `is_onboarded=true` if `ProfileRepo.GetAsync()` returns a fully-populated profile, but that fallback only runs on the next page load — login already navigated away with `forceLoad: true`.
3. **`Index.razor:589–608` (`CheckNewUserAsync`)** — flags the user as new whenever local DB has zero resources/vocab. If login *did* land on `/` before sync finished pulling rows, the dashboard would show the "new user" / starter-pack flow even for an existing account.

A correct fix has to make login wait for (or at least account for) the server-side profile lookup *and* the in-flight initial sync, and the dashboard has to suppress the "new user" branch while `ISyncService.IsInitialSyncInProgress == true`.

---

## 1. Unit tests — `tests/SentenceStudio.UnitTests/`

### 1.1 Convention summary (verified from existing tests)

Match the style of `Services/VocabularyProgressServiceUserIdTests.cs`:

- **Framework:** xUnit + Moq + FluentAssertions, in-memory SQLite via `SqliteConnection("DataSource=:memory:")` kept open in the fixture, `ApplicationDbContext` registered with `UseSqlite(connection)`, `db.Database.EnsureCreated()`.
- **Logger:** `NullLogger<T>.Instance`.
- **Fixture style:** Constructor builds the SUT, class implements `IDisposable` to dispose `_serviceProvider` and `_connection`.
- **Naming:** Class = `<SUT>Tests` or `<SUT><Concern>Tests`. Methods = `<Method>_<Scenario>_<Expectation>` (e.g., `GetProgress_WhenUserIdEmpty_FallsBackToActiveProfile`).
- **One regression-rationale comment block** at the top of any file that exists *because of a prior bug* — see the "DO NOT DELETE" comment in `VocabularyProgressServiceUserIdTests.cs`. We will follow that pattern: this regression has now bitten us, so the test file gets a top-of-file comment explaining why.

### 1.2 Constraint: Razor component tests are NOT testable in this solution

`SentenceStudio.UnitTests.csproj` only references `SentenceStudio.Shared`. `LoginPage.razor`, `MainLayout.razor`, and `Index.razor` live in `SentenceStudio.UI`, which is **not** referenced and would pull in MAUI/Blazor types the test project doesn't target. **bUnit is not in `Directory.Packages.props`.** Therefore:

- We cannot directly assert "LoginPage navigates to `/`" with a component test.
- **Workaround (RECOMMENDED — flag this for the Lead):** extract the post-login routing decision into a pure service in `SentenceStudio.Shared` (or `SentenceStudio.AppLib`, but Shared is reachable by the test project). Proposed shape:

  ```csharp
  // In SentenceStudio.Shared/Services/PostLoginRouter.cs
  public interface IPostLoginRouter
  {
      Task<PostLoginDestination> ResolveAsync(string userId, CancellationToken ct = default);
  }

  public enum PostLoginDestination { Dashboard, Onboarding }
  ```

  The service consults `IUserProfileRepository`, `ISyncService`, and `IPreferencesService`. `LoginPage.HandleLogin` then calls `await Router.ResolveAsync(...)` and translates the enum to a URL. The routing rule becomes unit-testable; the Razor file shrinks.

- If the Lead prefers to keep the logic inside `LoginPage.razor`, the unit-test layer is **not** viable and we must rely entirely on §2 manual verification. Tester recommends extraction.

### 1.3 Proposed test file: `tests/SentenceStudio.UnitTests/Services/PostLoginRouterTests.cs`

> Top-of-file comment (match the `VocabularyProgressServiceUserIdTests` precedent):
>
> ```
> // CRITICAL REGRESSION TESTS — post-login routing
> // These tests exist because LoginPage.razor:127 used to short-circuit to /onboarding
> // whenever the local "is_onboarded" preference was unset, which blackholed every
> // returning user after a fresh install. If these tests fail, users with server-side
> // accounts will be re-onboarded as if they were brand new — DO NOT DELETE OR WEAKEN.
> ```

| # | Test method | Asserts | Mocks | Path under test |
|---|---|---|---|---|
| 1.3.1 | `ResolveAsync_ExistingProfileOnServer_ReturnsDashboard` | Returns `PostLoginDestination.Dashboard` and sets `is_onboarded=true` via `IPreferencesService` | `IUserProfileRepository.GetAsync()` returns a fully populated profile (Name, TargetLanguage, NativeLanguage). `ISyncService` returns `IsInitialSyncInProgress=false`. `IPreferencesService.Get("is_onboarded", false)` returns `false` (fresh install). | The "fresh install of a returning user" path. |
| 1.3.2 | `ResolveAsync_NoProfileAndNoLocalOnboardingFlag_ReturnsOnboarding` | Returns `PostLoginDestination.Onboarding`. Does NOT set `is_onboarded`. | `IUserProfileRepository.GetAsync()` returns `null`. `ISyncService.IsInitialSyncInProgress=false`. `Preferences.Get("is_onboarded", false)=false`. | Brand-new account (must keep working). |
| 1.3.3 | `ResolveAsync_LocalOnboardedFlagTrue_ReturnsDashboardWithoutHittingProfileRepo` | Returns `Dashboard` and `IUserProfileRepository.GetAsync` is **never** called (verified via `mockRepo.Verify(... Times.Never)`). | `Preferences.Get("is_onboarded", false)=true`. | Happy-path returning user on a device that already onboarded — no extra round-trip. |
| 1.3.4 | `ResolveAsync_SyncInProgress_ReturnsDashboard` | Returns `Dashboard` even though local profile is null, *because* `ISyncService.IsInitialSyncInProgress=true` indicates rows are about to land. Sets `is_onboarded=true`. | `IUserProfileRepository.GetAsync()` returns `null`. `ISyncService.IsInitialSyncInProgress=true`. | The "logged in, sync triggered, profile not yet materialized locally" path. The dashboard is responsible for deferring the new-user check until sync completes (see 1.3.6). |
| 1.3.5 | `ResolveAsync_ProfileRepoThrows_FallsBackToOnboardingWithoutCrashing` | Returns `Onboarding`, no exception escapes. Logger receives a Warning. | `IUserProfileRepository.GetAsync()` throws `HttpRequestException`. | Server unreachable / transient failure — better to onboard than to crash the login screen. |
| 1.3.6 | `Index_IsNewUser_FalseWhileSyncInProgressEvenWhenLocalDbEmpty` | `Index.CheckNewUserAsync` (or its extracted helper) returns `isNewUser=false` when local DB has 0 resources AND `ISyncService.IsInitialSyncInProgress=true`. When sync completes with still-zero rows, `isNewUser` flips to `true`. | Real `LearningResourceRepository` against in-memory SQLite (zero rows). `Mock<ISyncService>` toggled between calls. | Dashboard's sync-aware new-user check. **Requires extracting the new-user determination into a testable helper** (e.g., `NewUserDetector.IsNewUserAsync(ISyncService, ILearningResourceRepository, IVocabularyRepository)`) — flag for Lead. |
| 1.3.7 | `ResolveAsync_SyncFailsAfterLogin_DoesNotLeaveOverlayStuckForever` *(edge case)* | Simulate `ISyncService.TriggerSyncAsync()` throws or completes with `IsInitialSyncInProgress` still false and `InitialSyncCompleted` event fires. Router/MainLayout wrapper exposes a state where `isSyncing=false` within bounded time. Asserted by checking that the `InitialSyncCompleted` event handler sets a flag, OR that a timeout in the router clears the syncing state. | `ISyncService` raises `InitialSyncCompleted` after a faulted task; verify the router/dashboard observable state transitions to "not syncing, no data → onboarding-or-empty-dashboard" rather than getting stuck. | Failure-mode test — answers "what if sync dies?". Without this test, a transient backend outage on first login traps users behind the spinner forever. |

### 1.4 What's explicitly NOT covered by unit tests (and why)

- **The Razor component re-render after `InitialSyncCompleted`** — needs bUnit. Out of scope for this PR; covered in §2 manual.
- **Preferences round-trip on real `Microsoft.Maui.Storage.Preferences`** — already abstracted behind `IPreferencesService`; we mock it.
- **Actual CoreSync HTTP traffic** — covered by existing API tests + manual log inspection in §2.

---

## 2. Integration / smoke — manual wipe-and-test (Mac Catalyst)

This is the test Captain runs after the fix lands. Pass = bug is fixed. Fail = tests above missed something or fix is incomplete.

### 2.1 Preconditions

- Aspire running via `cd src/SentenceStudio.AppHost && aspire run`. Confirm webapp on `https://localhost:7071/` and structured logs streaming in the dashboard.
- Production Postgres reachable (or whichever backend the running config points at — the test must use a backend that already has Captain's data).
- Captain's account credentials handy — the David / Korean test user is `f452438c-b0ac-4770-afea-0803e2670df5` per `e2e-testing/references/smoke-test.md`. Verify the account has ≥1 LearningResource with vocab on the server before starting; if not, this test isn't testing the right thing.

### 2.2 Wipe sequence (Mac Catalyst)

The sandbox path Captain provided is **`~/Library/Containers/C5750C50-4CF5-448D-8B79-E5695B8EE653/Data/Library/sstudio.db3`**. The container UUID is per-install, so confirm the current one before nuking:

```bash
# 1) Find the active SentenceStudio sandbox(es) on this Mac.
ls -1d ~/Library/Containers/*/Data/Library 2>/dev/null \
  | xargs -I{} sh -c 'test -f "{}/sstudio.db3" && echo "{}/sstudio.db3"'

# 2) Confirm with Captain it's the one to wipe. ⚠️ Data preservation rule applies.
#    Back up first:
cp "<path-from-step-1>" "$HOME/Desktop/sstudio.db3.bak.$(date +%Y%m%d-%H%M%S)"

# 3) Quit the running app (do NOT use App Store / right-click "uninstall" — that
#    would also wipe the bundle and force a re-sign that we don't want for this test).
osascript -e 'tell application "SentenceStudio" to quit' 2>/dev/null || true

# 4) Remove ONLY the local DB and the device prefs. This simulates "fresh install"
#    from the user's point of view without re-running codesign:
rm -f "<sandbox>/Library/sstudio.db3"
rm -f "<sandbox>/Library/sstudio.db3-shm"
rm -f "<sandbox>/Library/sstudio.db3-wal"
defaults delete com.simplyprofound.sentencestudio 2>/dev/null || true

# 5) Verify clean:
ls "<sandbox>/Library/" | grep -i sstudio   # should be empty
defaults read com.simplyprofound.sentencestudio 2>&1 | grep -i is_onboarded  # should be "does not exist"
```

If a fully clean reinstall is needed (bundle replacement), use `dotnet build -t:Run -f net10.0-maccatalyst` from a clean checkout — `bin/`/`obj/` need wiping if the SDK has been swapped (see decisions.md 2026-04-29 Wash entry).

### 2.3 Reinstall + relaunch

```bash
cd src/SentenceStudio.AppHost && aspire run    # if not already up
# in another terminal:
cd /Users/davidortinau/work/SentenceStudio
dotnet build -t:Run -f net10.0-maccatalyst src/SentenceStudio/SentenceStudio.csproj
```

### 2.4 Expected UX sequence + screenshots

| Step | Action | Expected | Screenshot name |
|---|---|---|---|
| A | App launches | Login screen, no spinner, no toast | `firstsync-A-login-screen.png` |
| B | Enter Captain's email + password, tap Sign In | Sync overlay appears with spinner + "Syncing your profile…" (`MainLayout_SyncingProfile`) within ~1s | `firstsync-B-sync-overlay.png` |
| C | Wait for sync to finish | Overlay dismisses; **dashboard renders with Captain's resources, vocab counts, today's plan** — NOT the starter-pack/new-user UI, NOT `/onboarding` | `firstsync-C-dashboard-populated.png` |
| D | Inspect URL via DevFlow / Aspire | Current route = `/`, NOT `/onboarding` | `firstsync-D-route.png` |
| E | Reload (Cmd+R) | Still lands on `/`, no overlay (sync already done), data present | `firstsync-E-after-reload.png` |

### 2.5 Aspire structured-log verification

In the Aspire dashboard structured-logs tab, filter resource = `webapp` (or `api`) and search for, in order:

1. `SyncService` `Initialize` — confirms first-sync code path triggered.
2. `IsInitialSyncInProgress = true` log line (or equivalent — confirm with Lead's instrumentation).
3. **Counts of rows pulled**: at least one log of the form "synced N LearningResource rows", "synced N VocabularyWord rows", etc. **N must be > 0** for Captain's account.
4. `InitialSyncCompleted` event raised.
5. NO exceptions in the `SyncService` or `UserProfileRepository` namespaces during this window.

Also verify in trace view: there should be a distributed trace from the webapp → API → Postgres for the initial sync, with no spans red-flagged.

### 2.6 Pass / fail criteria

**PASS** — all of:
- Step C dashboard shows >0 resources AND >0 vocab words (matches the server account state).
- No visit to `/onboarding` at any point in the trace.
- Aspire logs show >0 rows synced for at least `LearningResource` and `VocabularyWord`.
- `is_onboarded` preference is `true` after the flow completes.
- Screenshots A–E captured and attached to the PR.

**FAIL** — any of:
- App lands on `/onboarding`.
- Dashboard shows the starter-pack / "let's get you started" UI for an existing account.
- Sync overlay never appears (means we routed before sync started — order-of-operations bug).
- Sync overlay appears but never dismisses (the §1.3.7 edge case — file a separate bug).
- Aspire logs show 0 rows synced (means sync ran but pulled nothing — server/auth bug, not a routing bug; flag to backend).

---

## 3. Negative-path manual test — brand-new account

Same wipe procedure (§2.2) but with an account that has zero server-side data. Use one of:
- A throwaway account Captain creates fresh via the sign-up flow.
- The Jose / Spanish or Gunther / German test users IF (and only if) we've confirmed they have **no** LearningResources on the current backend. Otherwise create a fresh test user.

### 3.1 Expected UX sequence

| Step | Action | Expected | Screenshot name |
|---|---|---|---|
| A | Wipe (§2.2), launch app | Login screen | `newuser-A-login.png` |
| B | Sign in with the empty account | Brief sync overlay (sync still runs but finds nothing), then routes to **`/onboarding`** | `newuser-B-onboarding.png` |
| C | Complete onboarding | Lands on dashboard with starter resource OR the "create starter" affordance, per existing flow | `newuser-C-dashboard-starter.png` |

### 3.2 Pass / fail

**PASS** — onboarding screen reached, full onboarding flow still completes, dashboard reachable afterward, `is_onboarded=true` set after onboarding.

**FAIL** — onboarding skipped (i.e., the fix over-corrected and now treats new users as returning users), OR onboarding crashes, OR dashboard shows empty stats with no path forward.

---

## 4. Coverage matrix — which test catches which regression

| Regression scenario | Caught by |
|---|---|
| `is_onboarded` flag check ignores server profile | 1.3.1, 2.4 step C |
| New-account flow accidentally broken by fix | 1.3.2, §3 |
| Already-onboarded user pays cost of profile lookup on every login | 1.3.3 |
| Login wins the race against sync, dashboard flashes "new user" UI | 1.3.4, 1.3.6, 2.4 step B/C |
| Server unreachable at login → app crashes / shows blank screen | 1.3.5, manual ad-hoc with Aspire backend stopped |
| Sync fails after login → user stuck on overlay forever | 1.3.7, 2.6 FAIL row |
| MainLayout sync overlay doesn't render at all | 2.4 step B |

---

## 5. Open questions for the Lead

1. **Do you intend to extract the routing decision into `IPostLoginRouter`?** If yes, §1.3 is the test file. If no, §1 collapses to nothing testable and §2 carries the entire load — call that out as risk.
2. **Is the post-login sync triggered from `LoginPage` or already from `Index.OnInitializedAsync` (lines 594–606)?** If the latter, the dashboard is currently the trigger — meaning login MUST navigate to `/` (not `/onboarding`) for sync to even start, which makes the routing fix and the `IsInitialSyncInProgress`-aware `isNewUser` check inseparable. Confirm.
3. **Where does `is_onboarded` get set to `true` after a successful first-sync?** Today it's set inside `MainLayout.razor:129` only as a side effect of the existing-profile check. If the fix moves this into the router, make sure the MainLayout fallback still works (or remove it as dead code).
4. **What's the timeout for the §1.3.7 stuck-overlay case?** I'd suggest a hard 30s on `IsInitialSyncInProgress` after which `InitialSyncCompleted` fires regardless. Lead to decide.
