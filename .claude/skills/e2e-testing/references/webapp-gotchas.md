# Webapp E2E Gotchas (Blazor Server + Aspire + Playwright)

Cross-cutting frictions when driving the webapp with Playwright. Learned 2026-07-02
verifying Vocab Quiz session/resume.

## Navigation

- **Do NOT hard-`goto` an `[Authorize]` deep link.** A full-page `page.goto('https://localhost:7071/vocab-quiz?...')`
  redirects to `/` (Dashboard) — the Blazor Server auth/interactive-render boundary
  bounces deep links on a cold load. Navigate the way a user does: `goto('/')` then
  **click the activity button** (in-app interactive navigation preserves the circuit
  and the query string). If you must deep-link, expect the redirect and re-enter via UI.
- Activity buttons launch with a **launch context** in the URL (e.g.
  `?resourceIds=...&skillId=...`). That context is the resume/session key. If the
  dashboard resource selection is cleared, the same button may launch a *bare*
  `/vocab-quiz` (different context) — don't confuse "different context" with "state lost".

## Playwright automation

- **Keep `browser_run_code_unsafe` loops short (≤3 iterations).** A loop of
  `click → waitForTimeout(~3s)` over many turns exceeds the MCP tool timeout (~30s)
  and the call fails mid-run. Batch in small chunks and re-enter, or step manually.
- `browser_click` requires the `target` (ref or selector) argument — passing only
  `element` errors.
- When clicking answer choices, exclude hidden `.dropdown-item` buttons (secondary
  actions) — `main button:visible:not(.dropdown-item)` with non-empty text is a
  reliable choice selector.

## Verifying session/progress data in the DB

The webapp/API run on **Aspire PostgreSQL**, NOT the `server/sentencestudio.db`
SQLite file listed elsewhere in this skill (that file predates the Postgres setup).
To inspect `ActivitySession`, `VocabularyProgress`, etc.:

```bash
RT=$(command -v docker || command -v podman)
CID=$($RT ps --format '{{.Names}}' | grep -i '^db-')   # Aspire postgres:17 container
PW=$($RT exec "$CID" printenv POSTGRES_PASSWORD)         # user 'dbadmin', db 'sentencestudio'
$RT exec -e PGPASSWORD="$PW" "$CID" psql -U dbadmin -d sentencestudio \
  -c 'SELECT "Id","Status","LaunchContextKey" FROM "ActivitySession" ORDER BY "Id";'
```

Useful invariant checks:
- Single active session per context: `... HAVING count(*) FILTER (WHERE "Status"='InProgress') > 1` should return **0 rows**.
- Completed/Abandoned sessions must NOT be re-offered (they are excluded by the
  `Status = 'InProgress'` filter in `GetResumableAsync`).

## Test user / profile id

The Test Users table in the parent SKILL can be **stale** — the browser may already
be authenticated as a different profile (cookie from a prior session). Confirm the
actual active user before asserting DB rows: the app scopes by `active_profile_id`
from prefs, and DB `UserId` values are GUIDs (never `"1"`). Read the real id from
the row you just created rather than trusting the table.
