# Admin portal phases 3 and 4: handoff

2026-11-15. Two read-only phases built on phase 0 (`39b6f754`). Branches: `admin-sync-health`
(phase 3) and `admin-ai-usage` (phase 4, made from phase 3, so it carries both). Nothing was run
in a browser or against a running stack; the checks are builds and tests (see "Not verified").

No schema change of any kind: no table, column, migration or persisted record. Both phases read
what exists.

## What a person can do now

An admin opens `/admin`, which now has two more cards.

- **Sync and data health** (`/admin/sync-health`). Sees the deployed API version, the newest
  database migration and how many are applied and pending, and the range of the sync change log.
  Searches or pages a list of learners (profile id, name, email) and opens one
  (`/admin/sync-health/{profileId}`): row counts for each of the 32 synced tables with the
  newest change in each, the device stores bound to that learner with their change numbers, and
  a plain list of what the server does not record.
- **AI usage and jobs** (`/admin/usage`). Sees Sam runs, tokens and estimated cost per learner per
  week and per day (4 weeks by default, 1 to 13 by a select), the YouTube imports that failed or
  are stuck with a reason code, and the part-of-speech backfill's state from the data. The page
  says Other AI calls are not recorded.

Every load shows a failure as a failure (an alert, or "could not be loaded"), never an empty
table. A refused person sees "Page not found".

## Routes

All on `MapAdminGroup()`, mapped by one line each from `MapAdminEndpoints`
(`group.MapAdminSyncHealthEndpoints()`, `group.MapAdminUsageEndpoints()`). None asks for the
password again (see "Choices").

| Route | Audit action | Audit target |
|---|---|---|
| `GET /api/admin/sync-health/app` | `sync_health.app_read` | none |
| `GET /api/admin/sync-health/learners?take=&after=&q=` | `sync_health.learners_read` | none |
| `GET /api/admin/sync-health/learners/{learnerId}` | `sync_health.learner_read` | `learner`, the route's id |
| `GET /api/admin/usage/sam?weeks=` | `usage.sam_read` | none |
| `GET /api/admin/usage/video-imports?take=` | `usage.video_imports_read` | none |
| `GET /api/admin/usage/part-of-speech` | `usage.part_of_speech_read` | none |

`learnerId` is the profile id (`UserProfile.Id`, the id the sync owner columns hold). An unknown
learner, or an id over 64 characters, is a 404. Query parameters are bound as text and parsed in
the handler: a bad `take`, `weeks`, `after` or `q` is the route's own 400 (`invalid_request`).
Caps: learners `take` 1 to 200 (default 50), `q` 100 characters, `weeks` 1 to 13 (default 4),
imports `take` 1 to 200 (default 50). Wire values are fixed lowercase codes (`counted`,
`no_owner`, `failed`, `stuck`, `no_transcript` ...), never enum names. Timestamps are UTC.

Files: `src/SentenceStudio.Api/Admin/AdminSyncHealth{Endpoints,Reader,Contracts}.cs`,
`AdminUsage{Endpoints,Contracts}.cs`; web `Components/Pages/Admin/AdminSyncHealth.razor`,
`AdminLearnerSyncHealth.razor`, `AdminUsage.razor`, plus client methods and records in
`AdminApiClient.cs`.

## What the sync tables record, and what they do not

CoreSync on PostgreSQL keeps three tables (columns read from `CoreSync.PostgreSQL 0.1.129-local3`
and the API's own use of them):

- `__core_sync_ct (id bigserial, tbl, op, pk_integer, pk_text, pk_bytea, src)`: one row per
  change. `id` is the change number. `src` is the store that uploaded the change, null for a
  server-side change. **No timestamp.** Per learner it would need a join to every table by key,
  so it is not used.
- `__core_sync_remote_anchor (id, local_version, remote_version)`: one row per store cursor.
  `local_version` is the newest server change the store has been sent; `remote_version` is the
  newest of the store's own changes the server applied. **No timestamp.**
- `__core_sync_local_id`: the server's own store id.

The API adds one marker row per bound store: `sentencestudio:native-store:v1:<store>:<owner>`
(`NativeSyncStoreBindings`), and keeps the store's cursor under an id derived from owner and
store. I made that derivation callable (`NativeSyncStoreBindings.ScopedStoreId`, pure, behaviour
unchanged) and read the cursor from it.

| Plan item | State |
|---|---|
| Row counts for each synced table, per learner | **Recorded.** Counted from the tables with the same owner rules the sync uses (`SyncRowOwners`): a direct owner column, or the owner of the parent row for word links, learning records and phrase parts. Two tables (`Challenge`, `VocabularyList`) sync but carry no owner; the page says "Not counted", not zero. |
| Newest change, per learner | **Recorded per table**, from each table's own last-changed column (`UpdatedAt` where there is one, else the table's creation or event time; named on the page). `ResourceVocabularyMapping` and `SceneImage` have no such column and say so. Stored times are as the app wrote them. |
| Last sync per device | **Not recorded as a time.** What exists is the change number each store has downloaded and uploaded through, and how far behind the server head that is. Only stores that synced with the owner header (every native build, and a MAUI build since owner checks) have a marker, so only those are listed; an older MAUI store that never sent the header is not attributable to a learner. |
| Rows the server refused | **Not recorded.** A refused upload is a 403 and a `LogWarning` ("Native CoreSync upload refused ...") in the API log only; rows left out as unplaceable are logged as counts. Nothing is stored. |
| Deployed API version | Recorded: the API assembly's informational version. |
| Deployed database migration | Recorded: newest applied `ApplicationDbContext` migration, applied and pending counts. |

## What AI usage and jobs record

- **Sam**: `CoachUsage` (one row per learner per learner-local day: runs, input tokens, output
  tokens, estimated cost, ISO week key). Weeks are sums of the daily rows read, so a week is whole
  because the window starts on a Monday. Capped at 5,000 rows read and 2,000 days returned, with
  a `truncated` flag.
- **Other AI calls**: not recorded per learner. The page says so.
- **YouTube imports** (`VideoImport`): failed, and stuck (not finished 10 minutes after creation,
  the pipeline's own retry threshold). The stored `ErrorMessage` is free text (a language, a
  transcript length, an exception's words), so it is **never returned**. The server sorts it into a
  fixed code (`no_transcript`, `transcript_too_short`, `cancelled`, `timed_out`,
  `unexpected_error`, `other`, `none_stored`). Rows carry the import id, the learner id, status
  code, problem, reason code, times and whether a resource was made. No title, URL or video id.
- **Part-of-speech backfill**: the last run is **not recorded**. The worker logs its report and
  persists nothing. `Enabled` and the profile allowlist are Workers-host settings the API cannot
  see. What the page shows instead is the state of the data: words with and without a part of
  speech, per learner (most unclassified first, 100 at most), and words recorded to no learner.

Finding to know about: `src/SentenceStudio.Workers/Worker.cs` (around line 49) deletes every
`VideoImport` row with status Failed when the Workers host starts. The failed-imports list
therefore only holds failures since the last Workers restart. This was not changed.

## Choices the Captain should confirm

1. **No password-again on these routes.** They return counts, timestamps, ids and codes, no content,
   so they do not use `.RequireAdminStepUp()`. Each read is still audited, and a per-learner read
   names the learner.
2. **A learner list lives under sync health.** Phase 2 owns "find a learner". Until it exists the
   sync health page needs a way to pick one, so there is `GET /api/admin/sync-health/learners`
   (profile id, name, email, search by name or email). Phase 2 can replace it.
3. **Import failure reasons are codes, not text.** An unknown message shows as "another reason"
   rather than the raw text, because it can hold content. The cost is that a new failure kind needs
   a line in `AdminUsageEndpoints.ReasonCodeOf` to get its own name.
4. **Stuck means 10 minutes.** The pipeline's retry rule uses 10; the Workers host marks 30. The
   page names the number it uses.
5. **Per-device "last sync" is a change number, not a time.** If a time is wanted, that needs
   recording, which these phases were told not to add.
6. **The part-of-speech section shows data, not a run.** Same reason.
7. **Counts come from the server's tables, per owner.** Words recorded to no learner are counted
   separately and are in no learner's total, matching the one-owner-per-word rule.

## Tests

Branch `admin-sync-health` at `c7994e4e`:

| Project | Result |
|---|---|
| `SentenceStudio.Api.Tests` (scratch PostgreSQL set) | 5,749 passed, 13 skipped, 2 failed |
| `SentenceStudio.Api.Tests` (no PostgreSQL) | 5,252 passed, 480 skipped, 2 failed |
| `SentenceStudio.UI.Tests` | 2,685 passed |
| `SentenceStudio.WebApp.Tests` (no cluster) | 79 passed, 28 skipped |

Branch `admin-ai-usage` (tip: see git log):

| Project | Result |
|---|---|
| `SentenceStudio.Api.Tests` (scratch PostgreSQL set) | 5,773 passed, 13 skipped, 2 failed |
| `SentenceStudio.Api.Tests` (no PostgreSQL) | 5,275 passed, 481 skipped, 2 failed |
| `SentenceStudio.UI.Tests` | 2,692 passed |
| `SentenceStudio.WebApp.Tests` (no cluster) | 82 passed, 28 skipped |

The 2 failures are the known `CoreSyncProviderPackageTests` (package mirror). The "scratch
PostgreSQL set" runs set `COACH_PG_TEST_CONNECTION` and `APP_OPERATION_PG_TEST_CONNECTION` to the
`ss-test-pg` container; the baseline's different pass and skip split comes from which variables
were set. The WebApp suite's 28 skips are the `WebAppPostgres*` tests, which need
`WEBAPP_PG_TEST_CONNECTION` and `WEBAPP_PG_TEST_OWNER_ID` for an owned cluster I did not have.

New tests: `AdminSyncHealthTests` (19, SQLite), `AdminSyncHealthPostgresTests` (1, PostgreSQL),
`AdminUsageTests` (23, SQLite), `AdminUsagePostgresTests` (1, PostgreSQL),
`AdminSyncHealthClientTests` (4), `AdminUsageClientTests` (3), `AdminSyncHealthComponentTests`
(11), `AdminUsageComponentTests` (7). They cover: 404 for a learner and for no token on every
route, with no audit row; the audit action and learner target; numbers equal to seeded data for two
learners with different data and neither including the other's; the table list pinned to
`SharedSyncRegistration`; no learner content in any response (a marker string seeded into words,
titles and error text never appears); bad parameters as 400; failed loads shown as failures; a 404
on a later request closing the page.

## Not verified

- **Nothing was run in a browser or against a running stack.** No page was seen.
- The learner list route and the Sam route were tested on SQLite only. The per-learner counts,
  device reads and change-log range (sync health) and the imports and part-of-speech reads were
  also run against a real PostgreSQL 16 scratch database. A PostgreSQL run found one defect that
  SQLite hid, fixed here: a `MAX` over a timestamp column comes back as local time under Npgsql's
  legacy timestamp behaviour, which the API runs with, so the newest-change time is normalised to
  UTC.
- The CoreSync tables in the PostgreSQL test were created by hand to match CoreSync's DDL; the
  device read was not run against tables CoreSync itself made, or a real device sync.
- The three new pages in the first-HTML prerender tests (`AdminPagePrerenderTests`, extended with
  four paths) skip without the owned cluster, so they did not run.
- No MAUI head was built. The only shared change is `NativeSyncStoreBindings.ScopedStoreId`
  (a pure method extracted from `BindAsync`, same bytes), on the API.
- `docs/admin-portal-plan.md` is untouched (the lead updates it).

## For the lead's merge

- The branches are based on `39b6f754` (the `admin-portal` branch tip), not the local `main` of
  the `~/work/SentenceStudio` checkout, which is at `2159f21d` and does not have phase 0.
- Likely conflicts with phase 1: `AdminEndpoints.cs` (one line each), `AdminAudit.cs` (action
  constants), `AdminHome.razor` (cards), `AdminApiClient.cs` (records and methods), and
  `AdminApiFactory.cs` (this phase added a SQLite coach context so Sam's usage can be read; phase 1
  may add its own contexts beside it).

## Lead's review (2026-11-15)

`/code-review high` on both phases, and the lead's own run of the three test projects, led to
these changes on `admin-ai-usage` (they cover phase 3's files too, so the two phases merge
together):

- **Sam's weeks follow the learner's calendar.** The window was counted from UTC's Monday, so a
  run made on a learner's Sunday evening vanished from "1 week" once UTC reached Monday. It is
  now counted back from yesterday's UTC week; around a week's turn one week more than asked for
  can show, and the page says so.
- **Two different cuts are told apart.** A day list cut at 2,000 rows no longer claims the weeks
  are short. The imports section now says when its reasons were counted over the first 5,000
  failures only.
- **More import failures have a reason.** The texts `YouTubeImportService` writes itself (video
  not accessible, rate-limited, could not reach YouTube, no audio) each get a code. A test asks
  that service's own classifiers for their text, so a change of wording there fails the test.
- **The words section is not the backfill's queue, and now says so.** It counts by the learner a
  word is recorded to; the backfill picks words through progress rows and resource links.
- **"Stuck" is the pipeline's own rule and inherits its limit.** A retry keeps the first creation
  time, so an import retried a moment ago is counted; the page says so.
- **The learner list keeps only the newest request's answer.** A slow "Load more" that came back
  after a search used to append its rows and leave its cursor behind.
- **A failed request's log line carries no query string.** The search text is a learner's name
  or email.
- **"What is deployed" names what it covers.** The migration shown is the synced schema's; Sam,
  feedback and the admin audit keep their own histories and are not shown.
- **Owner columns come from `SyncRowOwners.OwnerColumn`**, not from a second list beside it.
- **Test hosts give their connections back.** The three new pages added enough web test hosts
  that a whole run on a default PostgreSQL (100 connections) failed with "too many clients";
  each host's pool is now cleared when it is disposed.

Left in `docs/native-review-backlog.md`: the reader's other duplicated helpers, the number of
round trips in one learner read, the other schemas' migrations, and counting the backfill's real
queue.

