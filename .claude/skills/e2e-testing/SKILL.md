---
name: e2e-testing
description: >
  End-to-end testing and verification for SentenceStudio. USE THIS SKILL whenever the user says
  "test", "verify", "check", "validate", "confirm it works", "smoke test", "run the app and check",
  "does it work", "try it", "make sure", or any variation of testing a feature or fix in a running app.
  Also use after EVERY bug fix or feature implementation as a mandatory final verification step — even
  if you think a build check is enough. Covers: launching via Aspire, interacting with Playwright (webapp)
  or maui-devflow-debug (native), verifying UI state, checking database records, and reading structured logs.
  If someone asks you to test anything in this app, or to verify a fix works, or to run a smoke test,
  or to check that CRUD operations work, or to confirm audio/quiz/import/activity features behave correctly
  — this is the skill to use. Do NOT skip this skill when verification is needed.
---

# E2E Testing

Verify SentenceStudio features and fixes by running the app and interacting with it.
The rule is simple: **if you changed it, you test it**.

## When to Use This Skill

- After fixing a bug — verify the fix works and didn't break anything
- After implementing a feature — verify it works end-to-end
- After refactoring — verify existing behavior is preserved
- When the user asks you to test something

## Testing Platforms

| Platform | Tool | When to use |
|----------|------|-------------|
| **Webapp** (Blazor Server) | Aspire + **Canvas browser** (Playwright fallback) | Default — fastest feedback loop |
| **macOS (AppKit)** / **Mac Catalyst** | maui-devflow-debug skill | macOS is the preferred native surface; Mac Catalyst only for iOS-shaped behavior |
| **iOS / Android** | maui-devflow-debug skill | When testing mobile-specific behavior |

Always test on webapp first. Only test native when the feature is platform-specific.

## Browser: Canvas First

**Use the built-in GitHub Copilot App Canvas browser when it is available. Never launch an
external Chrome as the default.**

Canvas drives the page the human is already looking at, in the window they already have open. An
external Chrome launch opens a second browser with its own profile and cookie jar, which produces
two specific, recurring lies:

- You authenticate in the automated Chrome, the human is still signed out in theirs (or signed in
  as somebody else), and "it works" is true only inside a window nobody will ever look at again.
- Screenshots you attach as evidence come from a browser whose state you created, so they cannot
  disprove the bug the human is reporting.

Order of preference:

1. **Canvas** — default for navigation, clicks, form fill, snapshots, and screenshots.
2. **Playwright** — fallback **only** when the test cannot be expressed as Canvas actions:
   network interception or request assertions, multi-tab/multi-context flows, file
   upload/download, scripted timing loops, or deterministic viewport/device emulation.

When you fall back to Playwright, say so in the report and say which of those it needed. Reusing
Playwright out of habit is how the evidence stops matching reality.

### Canvas navigation and the cookie jar

**`navigate_page(url)` creates or resets the browsing context in this host, which drops the session
cookie.** Any `[Authorize]` route then answers `302 -> /auth/login` on a completely healthy app,
and it looks exactly like a sign-in defect. It is not one — this is a tooling gotcha, not product
behavior, and it has already produced two wrong diagnoses.

For anything authenticated — a deep link, a reload, a full-document navigation — navigate **inside
the existing context** instead:

- `evaluate_javascript` with `location.href = '<url>'`, or
- click a real link or nav item in the page.

Then **prove the jar survived**: set `document.cookie = 'e2e_probe=1; path=/'` before navigating and
read it back afterwards. The Identity cookie is `HttpOnly` and will never appear in
`document.cookie`, so the probe is the only signal available for "did this context keep cookies".
If the probe is gone, the redirect you are looking at is yours.

Before reporting an authentication defect from the browser, corroborate it somewhere the browser
cannot lie: the server logs, or `tests/SentenceStudio.WebApp.Tests`, which drives the real password
sign-in and `/account-action/AutoSignIn` chain with no browser involved. See
`references/webapp-gotchas.md` for the full write-up.

## Webapp Testing Workflow

### 1. Confirm you will not clobber the human's stack

Captain's visible stack lives at `https://localhost:7071` on his real database. A second
`aspire run` from this worktree **seizes that port** and, if `LocalDb:DataVolume` is wrong, serves
a different database behind the same URL.

Before starting anything, check whether a stack is already running:

```bash
lsof -nP -iTCP:7071 -sTCP:LISTEN
```

- **Something is listening and you are an agent:** do not restart it and do not test against it.
  You need a **separately materialized project path** (a different worktree or full clone
  supplied by the session/workspace system -- NOT this worktree). From that separate path:
  ```bash
  # 1. Clone the volume (run from anywhere)
  scripts/clone-local-db-volume.sh --source <established> \
    --destination sentencestudio-agent-<task>-db-data \
    --database sentencestudio --username <db-user> --verify --verify-table '"UserProfile"'

  # 2. Configure the SEPARATE project path's user secrets
  dotnet user-secrets set "LocalDb:DataVolume" "sentencestudio-agent-<task>-db-data" \
    --project <SEPARATE_PATH>/src/SentenceStudio.AppHost

  # 3. Start from the SEPARATE path (never from this worktree's AppHost path)
  cd <SEPARATE_PATH>/src/SentenceStudio.AppHost
  ASPNETCORE_URLS='https://localhost:7171' aspire run

  # 4. Mandatory post-recovery gate
  scripts/post-aspire-restore.sh --expected-volume sentencestudio-agent-<task>-db-data
  ```
  ```
  UNSAFE - DO NOT RUN from the same path as a live AppHost:
  aspire run --isolated   # does NOT provide path isolation
  ```
- **Nothing is listening:** start normally.

```bash
cd src/SentenceStudio.AppHost && aspire run
```

**UNSAFE — do not use `aspire run --isolated` or `aspire start --isolated` from the
same AppHost project path.** `--isolated` does NOT provide project-path isolation;
AppHost ownership is keyed by project path and an isolated launch can stop the existing
AppHost and reuse DCP-managed resource/container identity. Use a separately materialized
AppHost path (worktree or clone) for agent stacks.

After any Aspire startup or recovery, run the post-restore gate:

```bash
scripts/post-aspire-restore.sh --expected-volume <the-volume-you-intended>
```

HTTP 302 from WebApp alone is not recovery proof; this script also checks API /health
and DB volume mount.

`LocalDb:DataVolume` is required; the AppHost refuses to boot without it rather than silently
attaching an empty auto-named volume. That refusal is deliberate — read the error, don't work
around it.

### 2. Prove which database you are on — before you test anything

```bash
scripts/validate-local-db-volume.sh --runtime --expect-volume <the-volume-you-intended>
```

A pass against the wrong database is not a pass. Run this **before** the test, not after, so you
never burn a session verifying behavior on somebody else's data.

Then confirm the webapp answers:

```bash
curl -sk -o /dev/null -w "%{http_code}" https://localhost:7071/
```

### 3. Navigate and Interact

Drive the app with **Canvas** (see *Browser: Canvas First*). The webapp runs at
`https://localhost:7071/`. Fall back to Playwright only for the cases listed there.

### 4. Verify Outcomes

Three levels of verification, use all that apply:

1. **UI state** — Canvas (or Playwright) snapshot shows expected text, buttons, counts
2. **Database** — PostgreSQL query confirms records created/updated correctly
3. **Logs** — Aspire structured logs show no errors

**The webapp/API use Aspire PostgreSQL, not SQLite.** Any guidance pointing at
`~/Library/Application Support/sentencestudio/server/sentencestudio.db` is stale — that file
predates the Postgres setup and reading it will show you data that no longer drives the app.

```bash
CID=$(docker ps --filter 'name=^db-' --format '{{.Names}}' | head -1)
PW=$(docker exec "$CID" printenv POSTGRES_PASSWORD)
docker exec -e PGPASSWORD="$PW" "$CID" psql -U dbadmin -d sentencestudio \
  -c 'SELECT "Id","Status" FROM "ActivitySession" ORDER BY "Id" DESC LIMIT 5;'
```

See [webapp-gotchas.md](references/webapp-gotchas.md) for the full DB verification recipe.

### 5. Stop Aspire When Done

Stop **only the stack you started**. If you started an isolated agent stack, stop that one and
leave the human's `https://localhost:7071` running.

## Native App Testing Workflow

Use the **maui-devflow-debug** skill for native testing. Key commands:

```bash
# Build and run the macOS head (preferred native surface)
dotnet run -f net11.0-macos --project src/SentenceStudio.MacOS/SentenceStudio.MacOS.csproj

# Verify integration health, then wait for the agent
maui devflow diagnose
maui devflow wait

# Inspect UI
maui devflow ui tree --depth 1
maui devflow ui screenshot --output test.png

# Check logs
maui devflow logs --limit 20
```

## Test Users

| User | Language | Profile ID |
|------|----------|------------|
| David | Korean | `f452438c-b0ac-4770-afea-0803e2670df5` |
| Jose | Spanish | `8d5f7b4a-7710-4882-af45-a550145dad4b` |
| Gunther | German | `c3bb57f7-e371-43d4-b91f-32902a9f9844` |

## Test Scripts by Feature Area

Load **only** the reference file relevant to your change. Don't load all of them.

| Reference file | Covers |
|----------------|--------|
| [smoke-test.md](references/smoke-test.md) | 5-min smoke test + cross-cutting checks — **run after every change** |
| [learning-coach.md](references/learning-coach.md) | Learning Coach — `/api/v1/coach`, Coach dialog/workspace UI, plan-revision acceptance, deterministic-ownership guardrails |
| [quiz-activities.md](references/quiz-activities.md) | Vocab Quiz, Vocab Matching, Cloze, Writing, Translation |
| [numberdrill.md](references/numberdrill.md) | NumberDrill — context picker, Listen&Type and Read&Produce submodes, mastery/attempt persistence |
| [import-and-resources.md](references/import-and-resources.md) | YouTube Import, Resource Edit + vocab generation, Vocabulary Detail |
| [import-content.md](references/import-content.md) | Content Import (Wave 2) — new vs. existing resource paths |
| [listening-activities.md](references/listening-activities.md) | Shadowing, Minimal Pairs, How Do You Say |
| [other-activities.md](references/other-activities.md) | Conversation, Reading, Scene, Video Watching |
| [management-pages.md](references/management-pages.md) | Resources, Vocabulary, Skills, Profile, Settings CRUD |
| [helpkit-flows.md](references/helpkit-flows.md) | `Plugin.Maui.HelpKit` integration flows (separate solution under `lib/`) |
| [webapp-gotchas.md](references/webapp-gotchas.md) | Blazor Server deep-link redirects, Playwright automation limits, Aspire Postgres DB verification — **read before driving the webapp** |

## Common Verification Patterns

### Activity records progress correctly

After any quiz/activity, verify the DB:

```sql
SELECT UserId, VocabularyWordId, MasteryScore, TotalAttempts
FROM VocabularyProgress
WHERE UserId = '<userId>'
ORDER BY LastPracticedAt DESC LIMIT 5;
```

UserId must be a GUID string — never `"1"`.

### Dashboard reflects changes

After recording progress, navigate to `/` and check:
- "Learning" count increased
- "New" count decreased
- "7-day accuracy" is non-zero

If counts are stale, the activity page is missing `CacheService.InvalidateVocabSummary()`.

### Audio plays without errors

Click any 🔊 button and verify:
1. Button shows spinner (loading state)
2. Button returns to normal (playback complete)
3. No errors in browser console or Aspire logs

On webapp, audio uses JS interop (`audioInterop.js`). On native, it uses `Plugin.Maui.Audio`.

### AI calls succeed through Aspire

Check Aspire structured logs for the API service. Look for:
- No 503 (service unavailable — usually Polly timeout)
- No 401 (invalid API key — check Aspire resource config)
- No NullReferenceException (usually reflection/type resolution)

## Marking a Task Complete

Only mark a task done when ALL of these are true:

- ✅ Build passes
- ✅ App launches without crash
- ✅ Changed feature verified in running app (screenshot or Playwright snapshot)
- ✅ No regressions in related functionality
- ✅ DB records correct (if applicable)
- ✅ No errors in logs

❌ "It compiles" is NOT sufficient.

## Post-Deploy Validation

**`azd deploy` exit code 0 means the upload worked, NOT that the system works.**

After EVERY deployment to Azure, run the post-deploy validation script:

```bash
./scripts/post-deploy-validate.sh
```

### Why This Is Mandatory

The deploy command succeeds when files are uploaded. It does NOT verify:
- The app starts without crashing
- Database migrations applied correctly
- Environment variables and secrets are configured
- The specific change you deployed actually works

### No /health Endpoint Exists

SentenceStudio has no dedicated health endpoint. Instead, the validation script uses a proxy health check:

```bash
# POST with bad credentials → 400 or 401 = app is alive and processing requests
curl -s -o /dev/null -w "%{http_code}" \
  -X POST "$API_BASE/api/auth/login" \
  -H "Content-Type: application/json" \
  -d '{"email":"healthcheck@test.invalid","password":"x"}'
```

Expected: HTTP 400 or 401. If you get 503, 502, or timeout → the app is not running.

### Verify the SPECIFIC Change

Don't just check "app is up." Verify what you deployed:
- If you deployed a migration → query the DB for the new column/table
- If you deployed a bug fix → reproduce the original bug scenario and confirm it's fixed
- If you deployed a new feature → exercise the feature end-to-end

## Data Integrity Patterns

### EF Core + SQLite Gotchas

**NULL in non-nullable columns:** Phone-side SQLite databases may have NULL values in columns marked as `[Required]` or non-nullable when EF migrations weren't applied (e.g., offline-first apps where the schema drifted). Always check for this after data recovery:

```sql
-- Find rows with NULL in columns that should be non-null
SELECT * FROM VocabularyWords WHERE UserId IS NULL;
SELECT * FROM UserSentences WHERE LanguageId IS NULL;
```

**`[ObservableProperty]` source generator strips nullability:** CommunityToolkit MVVM source generators can strip `required` annotations. Fix by adding explicit configuration in `OnModelCreating`:

```csharp
entity.Property(e => e.OptionalField).IsRequired(false);
```

**`.AsSplitQuery()` for many-to-many Includes on SQLite:** SQLite has limited support for complex JOINs. Queries with multiple `.Include()` / `.ThenInclude()` on many-to-many relationships will fail or return incorrect results without split queries:

```csharp
var words = await context.VocabularyWords
    .Include(w => w.Tags)
    .Include(w => w.Progress)
    .AsSplitQuery()          // REQUIRED for SQLite with multiple Includes
    .Where(w => w.UserId == userId)
    .ToListAsync();
```

### Sync/Retagging Data Recovery

When retagging user IDs (e.g., merging anonymous → authenticated user data):
1. **Clear CoreSync tracking tables** after retagging — sync won't upload retagged data if the old tracking entries still exist
2. Handle `UNIQUE` constraint conflicts — the target user may already have some of the same data
3. Use the `DataRecoveryService` pattern: scan all user-scoped tables, retag orphans, handle conflicts gracefully

---

## Migration validation gate (REQUIRED before mobile deploy)

**Any PR that adds or modifies an EF Core migration MUST pass `scripts/validate-mobile-migrations.sh` before the author can claim the work item complete.**

This script:
- Builds the Mac Catalyst Debug head
- Launches it via `maui devflow`
- Scans the first 15s of native logs for migration failures or schema sanity check errors
- Fails with exit code 1 if any migration errors are detected

**If the script reports failures, FIX THE MIGRATION — do not deploy.** Schema integrity is non-negotiable.

Run it from the repo root:
```bash
bash scripts/validate-mobile-migrations.sh
```

Expected output on success:
```
✅ Mobile migrations validated on net10.0-maccatalyst — no errors found
```

On failure, the script will print the full native log showing the error context. Common failure modes:
- `SQLite Error X: 'near "ALTER": syntax error'` — migration uses unsupported SQLite operation
- `no such column: TableName.ColumnName` — migration failed silently, `PatchMissingColumnsAsync` didn't run, or column name mismatch
- `sanity check failed` — critical schema piece missing after migration (DEBUG builds throw, Release logs Critical)
- `MigrateAsync failed` — migration threw an exception (now FATAL with hardened SyncService catch)

The validation runs in DEBUG mode, so the in-app `MigrationSanityCheckService` will throw immediately if schema is incomplete — this is by design to surface issues during development.
