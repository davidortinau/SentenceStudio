# Webapp E2E Gotchas (Blazor Server + Aspire + Canvas/Playwright)

Cross-cutting frictions when driving the webapp. Learned 2026-07-02 verifying Vocab Quiz
session/resume; extended 2026-08-20 after the database behind `localhost:7071` was silently
swapped out from under a live testing session.

## Which database is behind localhost:7071 — check this FIRST

**A green page at `https://localhost:7071` tells you nothing about which database you are on.**

The AppHost picks its postgres volume from `LocalDb:DataVolume`. When that was unset, Aspire fell
back to an auto-named, path-derived volume: the stack booted clean, `/` returned `302`,
`/auth/login` returned `200` — and the database was a different lineage with none of the accounts
you were about to test with. Because the container is `ContainerLifetime.Persistent`, the wrong
container then survived days of restarts.

On 2026-08-20 that cost a session: `dave@ortinau.com` "did not exist" at `localhost:7071` while
his real data (4,417 vocabulary words, 35 plans, 29 activity sessions) sat in an unmounted volume
at `LINKS 0`.

Before you trust anything the browser shows you:

```bash
scripts/validate-local-db-volume.sh --runtime --expect-volume <the-volume-you-intended>
```

It fails on any `*.apphost-*-db-data` mount, because an auto-named volume *is* the signature of
this bug. Quick manual version:

```bash
docker inspect "$(docker ps --filter 'name=^db-' --format '{{.Names}}' | head -1)" \
  --format '{{range .Mounts}}{{.Name}}{{end}}'
```

If you are an agent: never test against the human's `7071` stack at all. Clone the volume, run on
another port, and verify the clone. See `docs/local-dev-database-volumes.md`.

## Browser: use Canvas, not an external Chrome

Prefer the built-in **GitHub Copilot App Canvas** browser. Do **not** launch an external Chrome as
the default.

An external Chrome gets its own profile and cookie jar, so it authenticates as somebody other than
the human you are helping. Both failure modes are silent: "I signed in fine" in a window nobody
will look at again, and screenshots that prove only that *you* could reach the state.

Fall back to Playwright **only** when the test cannot be expressed as Canvas actions — network
interception, multi-tab/multi-context, file upload/download, scripted timing loops, or
deterministic device emulation — and say so in the report.

## Navigation

### `navigate_page(url)` resets the cookie jar — this is tooling, not the app

**In this host, Canvas `navigate_page(url)` creates or resets the browsing context, so the session
cookie from a sign-in you just performed is gone by the time the request goes out.** Every
`[Authorize]` route then answers `302 -> /auth/login`, on a perfectly healthy app.

This is a **tooling gotcha and not product behavior.** It is worth stating plainly because it has
already been misdiagnosed twice, in opposite directions:

- Once as "the Blazor Server auth/interactive-render boundary bounces deep links on a cold load",
  which is not a thing. Deep links to `[Authorize]` routes load fine with a cookie.
- Once as "sign-in never writes a durable Identity cookie", which sent an agent looking for a
  defect in `LoginPage` / `AutoSignIn` that the server logs disproved — `ServerAuthService` had
  minted the token and `/account-action/AutoSignIn` had been requested, every time.

**For authenticated full-document or deep-link verification, do not use `navigate_page`.** Use
either:

```js
// Canvas evaluate_javascript — a real navigation inside the existing context
location.href = 'https://localhost:7071/skills';
```

or click a real link/nav item in the page. Both keep the cookie jar.

**Prove the jar survived before you trust the result.** Set a non-HttpOnly probe cookie, navigate,
and read it back — if the probe is gone, the session cookie went with it and the `302` you are
looking at is yours, not the app's:

```js
// 1. before navigating
document.cookie = 'e2e_probe=1; path=/';

// 2. navigate the supported way
location.href = 'https://localhost:7071/skills';

// 3. after the load — expect "e2e_probe=1"
document.cookie.split('; ').find(c => c.startsWith('e2e_probe='));
```

The Identity cookie itself is `HttpOnly`, so `document.cookie` will never show it. The probe is a
stand-in for "did this context keep its cookies at all", which is the only question that matters
here.

If a route really is bouncing with the jar intact, *then* it is worth reporting — and pair it with
a server-side check, because the answer is usually visible in the logs
(`ServerAuthService` minting a token, `/account-action/AutoSignIn` being requested) or reproducible
in `tests/SentenceStudio.WebApp.Tests`, which drives the real sign-in chain with no browser at all.

### Activity launch context

- Activity buttons launch with a **launch context** in the URL (e.g.
  `?resourceIds=...&skillId=...`). That context is the resume/session key. If the
  dashboard resource selection is cleared, the same button may launch a *bare*
  `/vocab-quiz` (different context) — don't confuse "different context" with "state lost".
- When you need a specific launch context, prefer entering through the dashboard button over
  hand-building the URL; the button is what a learner does and it carries the context the app
  expects.

### One more origin trap: the dev host serves two

Aspire publishes the webapp on **both** `http://localhost:5172` and `https://localhost:7071`, and
`UseHttpsRedirection` is off in Development, so both are live and they are different origins to a
browser. Sign in on one and open a link on the other and you are anonymous — historically because
the Identity cookie was `Secure` and therefore invisible to the http origin.

The cookie policy now pins `SecurePolicy = None` in Development (and `Always` everywhere else), so
one session serves both dev origins. Still: **stay on one origin within a run** and say which one
the evidence came from.

## Playwright automation (fallback only — see Canvas rule above)

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

## Canvas provider restart can orphan the automation context

A Canvas provider restart (browser crash, MCP reconnect, host process restart) orphans
the current Canvas instance. The automation session and its cookie jar are gone; opening
a new Canvas creates a new browsing context with a fresh cookie jar.

**Observed symptom:** after a provider restart, `[Authorize]` routes return `302` and
no sign-in state is retained. This looks identical to an authentication failure but is a
tooling session-continuity issue, not a product auth defect.

**Rule:** Do not report a product authentication failure from this condition. Instead:
1. Note the provider restart in the test report.
2. Re-authenticate in the new Canvas context.
3. Continue testing from the fresh context.

This is distinct from the `navigate_page` cookie-jar-reset issue above, which happens
within a healthy Canvas provider session. Both produce the same HTTP 302 symptom for
different tooling reasons.

## Post-recovery verification: use post-aspire-restore.sh

After ANY Aspire stack restart (whether from orphan cleanup, volume swap, or AppHost
crash recovery), run:

```bash
scripts/post-aspire-restore.sh --expected-volume <the-volume-you-intended>
```

This validates:
- WebApp responds (HTTP 200/301/302)
- API /health returns 200
- DB container has the expected named volume mounted
- No OptionsValidationException in API startup logs (if --log-file provided)

**HTTP 302 from WebApp alone is NOT recovery proof.** The Sam-missing incident
(2026-08-22) demonstrated that WebApp can respond 302 while the API is crashed and DCP
holds the port open returning timeouts. Always confirm API health and DB mount
independently.

## Test user / profile id

The Test Users table in the parent SKILL can be **stale** — the browser may already
be authenticated as a different profile (cookie from a prior session). Confirm the
actual active user before asserting DB rows: the app scopes by `active_profile_id`
from prefs, and DB `UserId` values are GUIDs (never `"1"`). Read the real id from
the row you just created rather than trusting the table.
