# Admin portal plan

2026-11-15. Approved by the Captain the same day. Nothing here is built yet.

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
