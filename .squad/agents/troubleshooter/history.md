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
