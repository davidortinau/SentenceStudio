# Admin portal plan

2026-11-15. Approved by the Captain the same day. Phase 0 is built; see "Phase 0 as built".

## Why

On 2026-11-15 the Captain asked to see the feedback he had reported through Sam. The reports
filed to GitHub were easy to find. The answers he had flagged inside Sam were not: they sit in
production tables nobody reads. The weekly digest job skips itself (no credentials, no firewall
access), the page that shows them is switched off outside Development, and reading them by hand
means opening the production database firewall each time.

The portal gives the Captain one place to manage the app and help its users, and gives a
maintenance agent the same information through an API, without database access.

## What the Captain decided (2026-11-15)

| Topic | Decision |
|---|---|
| Users | The Captain plus invited people. |
| First version | Feedback inbox, account help, sync and data health, AI usage and jobs. |
| Build order | Admin sign-in, then feedback, then accounts, then sync health, then AI usage and jobs. |
| Who is admin | The account holds the Admin role in the database **and** its email is on a list in the server's settings. Either alone gives nothing. |
| Sam conversations | An admin may read only the one question and answer a learner flagged, for any learner. Every read is recorded. |
| Other learner data | An admin may read a learner's words, resources, progress and history, read-only. Every read is recorded. |
| Account help | Done by hand in the portal: confirm an email, unlock, create a one-time reset link the Captain sends himself. No email service. |
| Sign-up | Only emails on a list kept in the portal can register. |
| Admin changes | The account tools above, plus switching an account off and deleting it. Nothing else is editable. |
| Delete | Switch off at once, erase the data after a waiting period, cancellable until then. |
| Protection | Dangerous actions ask for the password again: delete, switch off, reset link, reading a learner's content. |
| Tracking | A report gets a status and a linked GitHub issue that carries no private text. |
| Noticing | A count in the web app for admins, and a scheduled agent check. |
| Agent access | A token that reads what an admin can (reports, flagged exchanges, learner content) and sets report status and issue links. It can never use the account tools. |

## What already exists and is reused

- **Role tables.** The standard Identity role tables are already in the PostgreSQL schema and
  the web app is registered with roles. The API does not load or check roles yet.
- **Sam report endpoints and page.** `/api/v1/coach/operator/opportunities` (list, rollup,
  export, get, review, reveal) and the web page `/operator/sam-opportunities`. Both run only in
  Development and show only the signed-in person's own rows. The code comments already say what
  changes when an admin check exists: the Development-only gates become a policy.
- **Report statuses.** New, Reviewed, Accepted, Deferred, Dismissed.
- **GitHub feedback records.** `FeedbackSubmission` (issue number, title, platform, app version)
  and `FeedbackScreenSnapshot` (the private screen state behind each `fs-…` reference).
- **Account delete.** The API's own Delete Account erases Sam data, then feedback, then the
  profile, then the sign-in account, and stops if any step fails.
- **Account lockout** (#89) and the Identity tokens for email confirmation and password reset.
- **Sam usage.** `CoachUsage`: runs, tokens and estimated cost per learner per day.
- **Content-free digest.** `scripts/sam-opportunity-digest.sh` stays as it is.

## Design in brief

- **Where it lives.** Pages under `/admin` in the WebApp project only (as the operator page is
  today), so the MAUI and native apps never ship them. English only. Data comes from new API
  routes under `/api/admin/*`, which the pages and the agent both call.
- **The admin check.** One policy on every admin route: signed in, holds the Admin role, and
  the email is on `Admin:Emails`. Both are checked on each request, so removing either takes
  effect at once. An empty list means nobody is admin. Anyone who fails gets 404, not 403, as
  the operator routes do today.
- **Granting the role.** A script (`scripts/admin-grant.sh <email>`) writes the role row. In
  production that is a one-time database write with the Captain's go. The app has no way to
  grant it.
- **Password again.** The web app re-checks the password and gives the API a proof that lasts
  10 minutes. Dangerous routes refuse without it.
- **Agent token.** Created in the portal, shown once, stored hashed, expires after 30 days, can
  be revoked. It has its own sign-in path in the API and is refused by every account route.
- **Audit.** One row per admin read of private data and per admin change: who (admin or token),
  what, which learner or report, when, and whether it worked. No content. Shown on an Audit page.
- **Storage.** One small server-only database context in the API (`AdminDbContext`, PostgreSQL
  only, its own migration history), as Sam's and Feedback's are. Nothing is added to the synced
  `ApplicationDbContext`, so no SQLite migration and nothing new reaches a device.

## Phases

Each phase is one branch, reviewed, checked running on the local stack with test accounts, and
deployed only with the Captain's go.

### 0. Admin sign-in

- API: load roles, the admin policy, the password-again proof, `AdminDbContext` with the audit
  table. Web: an `/admin` page that only an admin can open, and a nav link only an admin sees.
- Done when: a learner gets 404 on every admin route; the role without the list, and the list
  without the role, both get 404; an admin gets in; each admin request leaves an audit row.

### 1. Feedback inbox, agent access, scheduled check

- **Inbox.** Every learner's flagged Sam answers: date, reason, kind of response, failure code,
  status, linked issue. The reports filed to GitHub, each with its saved screen state.
- **Open a flagged exchange.** Shows the one flagged question and answer. Recorded. Works only
  for rows a learner's flag created. Automatic capture of Sam's misses stays off.
- **Triage.** Set a status and store a GitHub issue number.
- **Agent.** The token, its three permissions (reports, learner content, triage), and
  `docs/admin-api.md` describing the routes.
- **Noticing.** A count of new items for admins in the web app, and a weekday Claude task that
  reads new reports, files or links issues, and sends the Captain a short summary.
- **Clean-up.** The existing operator page becomes the inbox. The weekly GitHub digest job that
  only skips is removed.
- Done when: a test learner flags a Sam answer, it appears in the inbox, opening it shows the
  exchange and leaves an audit row, a status and issue link stick, the token can do the same,
  and a token without a permission is refused.

### 2. Accounts and the sign-up list

- **Find a learner.** Email, name, created, email confirmed or not, locked or not, last sign-in
  where it is recorded.
- **Account tools.** Confirm an email. Unlock. Create a one-time reset link (shown once).
- **Sign-up list.** Add and remove emails. Both sign-up paths (the API's and the web app's)
  refuse anyone else with a plain message. Existing accounts are not affected.
- **Switch off and delete.** Switch off refuses new sign-ins and ends saved sessions. Delete
  switches off and schedules the existing ordered erase 14 days later; an admin can cancel.
- **Read a learner's data.** Words, resources, progress and activity history in plain read-only
  tables. Password again, and recorded.
- **Tell invited people.** One sentence at sign-up that an admin can read their learning data
  to fix problems (English and Korean).
- Done when: an email not on the list cannot register on either path and one on the list can;
  confirming by hand lets that person sign in on a phone build; the erase runs after the waiting
  period in a test and a cancelled one does not; one learner's data never shows under another.

### 3. Sync and data health (read-only)

- Per learner: row counts for each synced table, newest change, last sync per device where the
  server records it, rows the server refused.
- App-wide: deployed API version and database migration.
- Done when: the numbers match the database for two test learners with different data.

### 4. AI usage and jobs (read-only)

- Sam: runs, tokens and estimated cost per learner per day and week.
- YouTube imports that failed or are stuck, with the reason. The part-of-speech backfill's
  last run.
- Other AI calls are not recorded per learner today. The page says so; recording them is a
  later decision.

## Needs the Captain's approval

- **Schema.** New PostgreSQL-only tables in `AdminDbContext`: audit (phase 0), agent tokens
  (phase 1), sign-up list and pending erasures (phase 2). Possibly one number column for the
  issue link on Sam's report rows.
- **Policy.** Sam's operator routes move from Development-only to the admin policy, and an
  admin may open another learner's flagged exchange in production.
- **Production settings and data.** The `Admin:Emails` setting; the one-time role grant.
- **Each deploy.**

## Defaults I chose (say if any is wrong)

- Agent token lasts 30 days. The password-again proof lasts 10 minutes.
- The waiting period before an erase is 14 days. Audit rows are kept for a year.
- Sync health and AI usage are read-only.
- Switching an account off takes effect at once on the web. A phone keeps working until its
  access token expires, at most 24 hours, unless we add a per-request check.

## Not in the first version

Editing a learner's data. Signing in as a learner. An email service. Two-step sign-in. Reading
any Sam conversation that was not flagged. Automatic capture of Sam's misses. Admin screens in
the MAUI or native apps. Recording AI usage outside Sam.

## Open technical points (settled in their own branch)

1. The sign-up list lives in the API's database context, which the web app cannot read
   directly, so the web app's sign-up has to ask the API. (Phase 2)
2. What the server records per device for sync. Needs a short read of the sync tables. (Phase 3)
3. Whether 24 hours is acceptable for a switched-off account on a phone. (Phase 2)
4. Where the scheduled task keeps its token. A cloud task cannot use this Mac's keychain. (Phase 1)

## Team and how each phase runs (Captain, 2026-11-15)

- **One fresh session per phase.** Each starts by reading this plan.
- **Lead: Fable 5.2.** Effort xhigh for phases 0 and 2 (a mistake there is a security hole or
  lost data), high for phase 1. The Captain sets these with `/model` and `/effort`.
- **Phases 3 and 4 run in parallel on a worker.** The Captain asked for this: one Sonnet 5.5
  worker at medium effort, in its own git worktree with its own files, started only after
  phase 0 is merged. The lead reviews its diff before anything is merged.
- **Review.** `/code-review` on every diff before any running check: high, and xhigh for phases
  0 and 2, which also get `/security-review`.
- **Weekday check (phase 1).** Sonnet 5.5 at medium. It triages and reports; real fixes go to a
  full session.
- **Optional, the Captain's to run:** `/code-review ultra` on phase 0 before its first
  production deploy.

## Phase 0 as built (2026-11-15, branch `admin-portal`)

What a person can do now: an admin signs in to the web app, sees an Admin link in the sidebar,
opens `/admin`, confirms their password, and reads the audit log at `/admin/audit`. Everyone else
sees no link and gets "Page not found".

**API (`src/SentenceStudio.Api/Admin/`)**

| Route | Does | Audit action |
|---|---|---|
| `GET /api/admin/access` | 204 for an admin. The web app asks it to decide whether to draw the sidebar link. | none |
| `GET /api/admin/session` | Who is signed in, and when their password confirmation runs out. | `session.read` |
| `POST /api/admin/step-up` | Checks the password and returns the proof. | `step_up.verify` |
| `GET /api/admin/audit?take=&before=` | Audit rows, newest first, 50 a page (200 at most). The row for the read in progress is left out; it shows, completed, on the next read. | `audit.read` |
| anything else under `/api/admin` | 404, for every method. | none |

- **The gate** is one policy on the route group. `AdminAccessEvaluator` reads the account, its
  email, whether the email is confirmed and the role row from the database on each request, and
  the list from the live `Admin:Emails` setting. Only the account id is taken from the token.
  A refusal is a bare 404, the same response as a path nobody mapped; there is no 401, 403 or
  405 anywhere under the prefix.
- **Password again.** `AdminStepUpService` seals the proof with the API's Data Protection key
  ring, not the JWT signing key (the web app holds that key too). A proof names one account,
  lasts 10 minutes and dies when the account's password changes. A later dangerous route adds
  `.RequireAdminStepUp()`; no phase 0 route needs it, and
  `AdminDangerousRoutePipelineTests` drives one through the pipeline.
- **Guessing the password.** Failed checks count toward the account's lockout (5 wrong, locked
  5 minutes), the same count and lock sign-in uses. `AdminPasswordCheck` counts each attempt
  before looking at it, so a burst of parallel guesses cannot get more than 5 looked at. Once an
  attempt is counted the check runs to its verdict even if the caller has stopped waiting, so a
  slow API cannot turn right passwords into counted failures.
- **New routes** are mapped on `MapAdminGroup()`, which carries the policy, and must declare
  `.WithAdminAction(...)`. `AdminEndpointInventoryTests` fails otherwise.
- **Audit.** `AdminRequestMiddleware` inserts the row before the handler runs and completes it
  afterwards. If the insert fails the request fails and nothing is read or changed. A row whose
  ending could not be written stays `Started`.
- **Granting the role.** `scripts/admin-grant.sh <email>` (`--revoke`, `--list`, `--container`,
  `--prod`). It never creates an account.

**Web (`src/SentenceStudio.WebApp/Admin/`, `Components/Admin/`, `Components/Pages/Admin/`)**

- `AdminGate` wraps each admin page. The page's first API request is the gate, on every visit:
  an answer draws the page, a 404 draws "Page not found", and no answer within 15 seconds draws
  "This page could not be loaded" with a Try again button. Try again asks in the same tab,
  because a reload would drop a confirmed password. The prerendered HTML is the same waiting
  mark for everyone; admin content is drawn only in the live circuit.
- The sidebar link comes through `IHostNavItemProvider` (new, in `SentenceStudio.UI`). The web
  app registers one; the MAUI heads register none and their sidebar is unchanged.
- The proof is kept in the circuit on the server and never reaches the browser. A reload asks for
  the password again. The page takes the API's word for whether the proof is still live.

**Schema (approved by the Captain 2026-11-15 for the merge; applying it to production is part
of the first deploy, which needs his go).** One table, PostgreSQL only, in
`AdminDbContext` with its own `__AdminMigrationsHistory`
(`20261115224514_InitialAdminSchema`):

| `AdminAuditEntry` column | Type | Holds |
|---|---|---|
| `Id` | bigint identity | |
| `OccurredAtUtc` | timestamptz | When the request was admitted. |
| `ActorKind` | integer | 1 admin account, 2 agent token (phase 1). |
| `ActorId` | varchar(64) | Identity user id, or token id. |
| `Action` | varchar(64) | A code such as `session.read`. |
| `TargetKind`, `TargetId` | varchar(32), varchar(64), nullable | `learner` or `report` and its id, when there is one. |
| `Outcome` | integer | 0 started, 1 succeeded, 2 failed. |
| `StatusCode` | integer, nullable | HTTP status. |
| `FailureCode` | varchar(64), nullable | A code such as `password_incorrect`. |

Indexes: `OccurredAtUtc`; (`TargetKind`, `TargetId`); `ActorId`. No email, name or content.

**Choices made while building (approved by the Captain 2026-11-15)**

1. The API checks the password and issues the proof; the web app only collects the password and
   holds the proof. The plan said "the web app re-checks the password".
2. `GET /api/admin/access` leaves no audit row. Every learner's sidebar calls it on every page
   load, and it returns nothing.
3. An account whose email is not confirmed is not an admin, even with the role and the list.
4. Audit rows about a learner keep that learner's profile id after the account is erased
   (phase 2 decides whether the erase removes them).
5. A signed-in learner who loads `/admin` directly gets the app's shell with "Page not found"
   (HTTP 200), as the operator page does today. A signed-out visitor is sent to sign in. Only the
   API routes answer a bare 404. Making the page itself a 404 would take a check in the web app's
   request pipeline.

**Becoming an admin on the local stack**

```
dotnet user-secrets set "Admin:Emails:0" "<email>" --project src/SentenceStudio.AppHost
scripts/admin-grant.sh <email>          # after the AppHost has started once on this branch
```

In production the grant is `scripts/admin-grant.sh <email> --prod --yes`, while
`scripts/prod-db-access.sh open` is in effect. It refuses without `--yes`.

**Checked running (2026-11-15, Lilliput, commit `ec86ab56`)**

On an isolated stack (its own worktree, PostgreSQL 17 and Azurite volumes, since removed), with
the seeded accounts `e2e@test.local` (on the list) and `testsailor@test.local`, driven by
headless Chromium and `curl`:

- The API's fenced startup applied `20261115224514_InitialAdminSchema` under
  `__AdminMigrationsHistory`.
- 404 from every admin route for: no token (Development's sign-in fallback), the learner, `e2e`
  listed before the grant, and `testsailor` holding the role without being listed. After
  `scripts/admin-grant.sh e2e@test.local`, `e2e` got 204 and 200. A wrong verb and an unknown
  admin path were 404 for the admin too. Revoking took effect on the next request with the same
  token.
- In the browser: the learner saw no Admin link and "Page not found" on `/admin` and
  `/admin/audit`, and stayed signed in. The admin saw the link (also in the phone-width menu),
  `/admin` named the account, a wrong password was refused, the right one confirmed, and the
  audit log listed each request, 50 to a page with older rows on demand. The browser itself never
  called `/api/admin`, and no proof appeared in the page, storage or cookies.
- 40 wrong passwords sent at once: 4 were looked at, 36 refused, the account locked for 5
  minutes, and sign-in was refused too.
- A proof issued before an API restart was still honoured after it (the key ring is in Azurite
  locally), and the audit rows were still there.
- With the API stopped, `/admin` showed a spinner and then "This page could not be loaded"
  within 15 seconds; after the API came back a reload opened it. (This check found the blank
  page fixed in `ec86ab56`.)
- No error-level lines in the API or web app logs, and no password, proof or email in either.
  No audit row was left `Started`; none contains an address.

Not checked: any MAUI head build (they consume the same `SentenceStudio.UI` assembly the web app
and `SentenceStudio.UI.Tests` built), light theme, and production.

**Running a web check on Lilliput.** The everyday stack is the Captain's, so an agent's check
runs beside it, not on it. (Until 2026-11-15 that stack also had no web app: Kestrel served an
untrusted second dev certificate, the API's health check failed and the web app waited for it
for ever. The Captain removed that certificate; `https://localhost:7071` works again.) From a
separate worktree, `aspire start --isolated -lp http --no-build` with `ASPIRE_ALLOW_UNSECURED_TRANSPORT=true`, fresh `LocalDb__DataVolume` and
`LocalAzurite__DataVolume` names, scratch `WebApp__AppDataRoot` and
`SENTENCESTUDIO_APPDATA_ROOT`, throwaway `Parameters__dbUser`, `Parameters__dbPassword`,
`Parameters__jwtkey` and `Parameters__feedbackhmackey`, and `Admin__Emails__0` brings up the
whole stack over http on random ports beside it. `aspire describe --apphost <path>` lists the
URLs; `aspire stop --apphost <path> --force` stops it (never `--all`), and its `db-` and
`storage-` containers then need `docker rm`.

**Seeing the portal on the everyday stack.** Put the account's email in the AppHost's user
secrets (`dotnet user-secrets set "Admin:Emails:0" "<email>" --project
src/SentenceStudio.AppHost`), restart the AppHost so the API gets the list, and run
`scripts/admin-grant.sh <email>`. The account has to exist already; one made through the web
app's sign-up in Development has its email confirmed for it. The grant needs no restart.

**Left for later**

- Deleting audit rows older than a year. Nothing can be that old before 2027-11; build it with
  phase 2's scheduled erase.
- Before the first production deploy: `scripts/prod-deploy.sh` carries only `Coach__*` settings
  forward. It must also carry `Admin__Emails__*`, or each deploy empties the list (which fails
  closed: nobody is an admin). The first deploy also needs the list set and the role granted
  (a production database write).
- Review findings not fixed here: `docs/native-review-backlog.md`, "Admin portal phase 0".

## Status

| Phase | State |
|---|---|
| 0. Admin sign-in | Built, reviewed and checked running on `admin-portal` (`ec86ab56`). Not pushed, merged or deployed. Waiting on the Captain: the schema above, then merge. |
| 1. Feedback inbox, agent access, scheduled check | Not started. |
| 2. Accounts and the sign-up list | Not started. |
| 3. Sync and data health | Not started. Worker, after phase 0 merges. |
| 4. AI usage and jobs | Not started. Worker, after phase 0 merges. |
