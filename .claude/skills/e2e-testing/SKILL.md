---
name: e2e-testing
description: >
  End-to-end testing and verification for SentenceStudio. USE THIS SKILL whenever the user says
  "test", "verify", "check", "validate", "confirm it works", "smoke test", "run the app and check",
  "does it work", "try it", "make sure", or any variation of testing a feature or fix in a running app.
  Also use after EVERY bug fix or feature implementation as a mandatory final verification step — even
  if you think a build check is enough. Covers: launching via Aspire, interacting with Playwright (webapp)
  or maui-ai-debugging (native), verifying UI state, checking database records, and reading structured logs.
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
| **Webapp** (Blazor Server) | Aspire + Playwright | Default — fastest feedback loop |
| **Mac Catalyst** | maui-ai-debugging skill | When testing native audio, MAUI handlers, platform features |
| **iOS / Android** | maui-ai-debugging skill | When testing mobile-specific behavior |

Always test on webapp first. Only test native when the feature is platform-specific.

## Webapp Testing Workflow

### 1. Start the Stack

```bash
cd src/SentenceStudio.AppHost && aspire run
```

Wait for the dashboard URL, then verify webapp is up:

```bash
curl -sk -o /dev/null -w "%{http_code}" https://localhost:7071/
```

### 2. Navigate and Interact with Playwright

Use Playwright browser tools to navigate, click, fill forms, and verify outcomes.
The webapp runs at `https://localhost:7071/`.

### 3. Verify Outcomes

Three levels of verification, use all that apply:

1. **UI state** — Playwright snapshot shows expected text, buttons, counts
2. **Database** — SQLite query confirms records created/updated correctly
3. **Logs** — Aspire structured logs show no errors

Database location:
```
/Users/davidortinau/Library/Application Support/sentencestudio/server/sentencestudio.db
```

### 4. Stop Aspire When Done

Stop the async shell running `aspire run`.

## Native App Testing Workflow

Use the **maui-ai-debugging** skill for native testing. Key commands:

```bash
# Build and run Mac Catalyst
dotnet build -f net10.0-maccatalyst -t:Run

# Wait for agent
maui devflow wait

# Inspect UI
maui devflow ui tree
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
| [quiz-activities.md](references/quiz-activities.md) | Vocab Quiz, Vocab Matching, Cloze, Writing, Translation |
| [import-and-resources.md](references/import-and-resources.md) | YouTube Import, Resource Edit + vocab generation, Vocabulary Detail |
| [listening-activities.md](references/listening-activities.md) | Shadowing, Minimal Pairs, How Do You Say |
| [other-activities.md](references/other-activities.md) | Conversation, Reading, Scene, Video Watching |
| [management-pages.md](references/management-pages.md) | Resources, Vocabulary, Skills, Profile, Settings CRUD |

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
