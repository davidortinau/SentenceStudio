---
name: e2e-testing
description: >
  End-to-end verification of SentenceStudio features and bug fixes using Aspire, Playwright (webapp),
  and maui-ai-debugging (native apps). Use when: (1) verifying a bug fix works before marking it done,
  (2) validating a new feature end-to-end after implementation, (3) running a smoke test after any code change,
  (4) checking that activity pages record progress correctly, (5) verifying audio playback works across
  platforms, (6) confirming AI gateway calls succeed through Aspire. Invoke this skill whenever you need
  to test something in the running app — even if you think a build check is enough, it probably isn't.
  This skill should be used after EVERY bug fix and feature implementation as a mandatory final step.
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
maui-devflow wait

# Inspect UI
maui-devflow MAUI tree
maui-devflow MAUI screenshot --output test.png

# Check logs
maui-devflow MAUI logs --limit 20
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
