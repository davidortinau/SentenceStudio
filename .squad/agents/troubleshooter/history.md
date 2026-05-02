# Troubleshooter — History

## 2026-05-01 — Login failure on Mac Catalyst + webapp (NOT email confirmation)

**Symptom:** Captain cannot log in via Mac Catalyst (Aspire dashboard https://localhost:17017) or webapp. Both fail. Asked if it's the email-confirmation issue again.

**Root cause:** **Wrong AppHost is running.** The active Aspire process (PID 87406) is in the worktree `/Users/davidortinau/work/copilot-worktrees/SentenceStudio/davidortinau-jubilant-lamp` (branch `davidortinau/login-keyboard-navigation`), NOT in the main checkout `/Users/davidortinau/work/SentenceStudio` (branch `fix/firstsync-routing-overlay`). That worktree's AppHost spun up its own postgres container `db-07bf899f` with volume `sentencestudio.apphost-07bf899fc9-db-data`, which is a **fresh, empty database** (0 AspNetUsers, 0 VocabularyWord rows).

Captain's real account `dave@ortinau.com` (EmailConfirmed=true, AccessFailedCount=0, no lockout) lives in the OTHER postgres volume `sentencestudio.apphost-84833ad037-db-data` attached to container `db-84833ad0`, which is the volume spawned by the main-checkout AppHost. That container is alive but no AppHost is currently consuming it — so the API can't see those users.

**Evidence:**
- `aspire-list_apphosts` shows the only running AppHost is in `davidortinau-jubilant-lamp` worktree (PID 87406).
- `db-07bf899f` (the one wired to running Aspire): `SELECT COUNT(*) FROM "AspNetUsers"` → 0. `SELECT COUNT(*) FROM "VocabularyWord"` → 0.
- `db-84833ad0` (orphaned): contains 6 users including `dave@ortinau.com` with `EmailConfirmed=t`, no lockout. 327 vocab words.
- `curl POST https://localhost:7012/api/auth/login {dave@ortinau.com,…}` → HTTP 401 (consistent with user-not-found in the empty DB).
- API service `/api/auth/login` route confirmed at `src/SentenceStudio.Api/Auth/AuthEndpoints.cs:20`. Login implementation is fine; in dev mode `Register` even auto-confirms email so email-confirmation isn't a blocker for new users.

**This is NOT the email-confirmation issue.** It's an environment-mismatch / wrong-worktree issue. The `op-fix-login-lockout-email-confirm` worktree exists but isn't running.

**Recommended fix (no DB mutation, no Captain approval needed for option A):**

A. **Switch Aspire to the main checkout** — preferred:
   1. Stop the running AppHost: `kill 87406` (or stop from Aspire dashboard).
   2. From `/Users/davidortinau/work/SentenceStudio` run the AppHost. It will reattach to `db-84833ad0` (volume `sentencestudio.apphost-84833ad037-db-data`) and Captain's existing account will work.

B. **Or, register a new user against the running (empty) DB** — only useful if Captain actually wants to test with the `login-keyboard-navigation` branch. Dev mode auto-confirms email, so registration immediately yields a working session.

**No destructive action taken.** No DB mutation. No data deletion.

## 2026-05-02 — Dashboard first-load: empty Learning Resources + Skill Profile dropdowns

**Symptom:** On cold start (post-login), the Dashboard's "Choose My Own" mode rendered with empty Tom Select dropdowns for Learning Resources and Skill Profile. Navigating away and back fully populated them. Data was confirmed present (327 vocab words, "Beginner Korean" skill profile in DB).

**Root cause (Blazor Hybrid first-render race):** `src/SentenceStudio.UI/Pages/Index.razor`.

The Tom Select selectors are initialized ONLY in `OnAfterRenderAsync(firstRender:true)` and only when `isTodaysPlanMode == false` at that moment. The deferred-sync code path (issue #187) intentionally returns early from `OnInitializedAsync` while `SyncService.IsInitialSyncInProgress == true`, leaving `isTodaysPlanMode` at its default `true`. So when `firstRender` fires, init is skipped. When sync completes, `OnInitialSyncCompleted → LoadDashboardAsync` flips mode to `ChooseOwn` based on the user's saved preference and calls `StateHasChanged()` to render the `<select>` elements — but `OnAfterRenderAsync` runs again with `firstRender:false`, so `InitChooseOwnSelectorsAsync` is never called. Result: empty dropdowns until the component is re-mounted via navigation.

**Fix (one-spot, ~10 lines):** In `Index.razor` at `OnInitialSyncCompleted`, after `LoadDashboardAsync` and `StateHasChanged()`, if `!isTodaysPlanMode && jsModule is not null`, await a 50 ms tick (let the just-rendered `<select>` elements land in the DOM) then call `InitChooseOwnSelectorsAsync()`. This is the same pattern `SetMode` uses when toggling modes post-firstRender. No change to the happy path — only the deferred-sync recovery path.

**Validation:** Rebuilt MacCatalyst (clean), restarted via Aspire `resource-restart`. On first load Skill Profile shows "Beginner Korean" and vocab stats render correctly without nav-away/back. Screenshot: `dash-firstload-after-fix.png`.

**Pattern lesson (reusable):** In Blazor Hybrid, **JS-interop init that depends on component state must run when that state is first valid, not necessarily at `firstRender`**. If `OnInitializedAsync` defers state resolution (e.g., to wait on an async event), every JS-interop call gated on `firstRender` becomes a latent race. The mitigation is: (1) make init idempotent and key it on a `selectorsInitialized` flag rather than `firstRender`, OR (2) re-invoke init from the deferred completion handler once state is resolved AND `jsModule != null`. Pattern #2 is minimal and was used here.
