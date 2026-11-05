# Release plan: `native-platform-heads` → `main` → Azure (November 2026)

Prepared 2026-11-04 at the Captain's request: "Prepare to merge to main and publish to azure. I worry
about the db migrations given this db is my real data." **Nothing below has been run against Azure
or `main` yet.** Each Azure step needs the Captain's go in that turn. `docs/deploy-runbook.md` stays
authoritative; this file is the order and the evidence for this one release.

## What ships

- **Merge:** `main` (59c72262, 2026-09-26) has not moved since the branch started, so the merge is
  a fast-forward of the branch (339+ commits). A trial merge found no conflicts. A push to `main`
  runs CI (`ci.yml`, `test.yml`); nothing deploys to Azure automatically.
- **Server schema:** 8 PostgreSQL migrations after main's last (`20260903175044_AddApplicationOperationLedger`):
  `AddStreamHistoryOwnerScope`, `ArchiveDisplacedPlanItems`, `AddVocabularyWordOwner`,
  `SyncTextIdsPhase2a`, `SyncTextIdsPhase2b`, `SyncOwnersPhase3`, `AddVocabularyLookup`,
  `AddSyncDeletionLog`.
- **API startup work after the migrations** (under the startup lock): word types, phrase parts,
  word owners, example-sentence owners (fixed 2026-11-04, see below), Number Drill content IDs,
  CoreSync provisioning for 13 more tables, and a one-time touch of their existing rows so devices
  download them.
- **Behavior:** owner-checked sync (headerless sync gets 403), speech through the API with the
  clip cache (B60), Sam entry changes, Number Drill content (B61), and the rest of the branch.

## Rehearsal on real data (2026-10-20 production dump, local throwaway copy)

Counts only; the copy was deleted. Summary: `scratchpad/migration-rehearsal-20261020-dump.md`
(this session).

- **No rows lost.** The 66 untouched original tables have identical counts and identical content
  hashes; the changed columns changed only as intended. All 37 foreign keys are valid.
- **All 8 migrations applied** in 215 ms. Each migration is its own transaction. A forced failure in
  the middle left the earlier ones applied, rolled back the failed one completely, and a re-run
  resumed and finished identically.
- **Startup** (Production mode, Release): 4.4 s on the real path (migrations plus backfills in one
  start). A second start changed nothing.
- **Found and fixed:** the owner migration copies an example sentence's owner from its word, but
  the server stamps word owners only later, at startup, so all 89 sentences would have been hidden.
  Startup now stamps sentence owners right after word owners (`VocabularyWordOwnership.StampExampleSentencesAsync`;
  the log line reads `Example sentence owners: {WithWords} stamped with their words, {AfterWords} stamped after`),
  and stamping a word stamps its ownerless sentences. Tests cover both; the new test fails without
  the fix. **Re-run with the fix** on a fresh restore: all 89 sentences owned (each by its word's
  owner), 0 rows lost, all 37 foreign keys valid, the same sync queue as before (338 rows, +5570
  tracking rows), startup 3.2 s with the lock held 2.4 s; a second start changed nothing.
- **Hidden, not deleted:** 217 audio history (StreamHistory) rows and 1 scene image have no
  recorded owner, so the owner checks hide them on the web and API. 424 shared or unlinked words
  stay ownerless by rule.
- **Captain's decision (2026-11-05): "assign the audio to my account."** Nothing in a row proves
  its owner, so the code never guesses. `scripts/assign-legacy-audio-history.sql` applies the
  Captain's explicit choice once, after the API has migrated:
  - it resolves exactly one profile from his sign-in email and refuses otherwise;
  - it refuses unless the ownerless count equals the number counted just before;
  - it touches only StreamHistory.UserProfileId, and the CoreSync trigger then syncs the rows to
    his devices;
  - a dry run rolls back.
  The scene image stays hidden; he named only the audio.
  What the rows are: mostly the old web app's quiz and drill audio cache, plus How Do You Say
  entries, from 2026-05 to 2026-10, in 3 voices. Once he owns them they appear in his How Do
  You Say history, as they did on `main`. Each stores a file path from the machine that made
  it, so another device regenerates the clip the first time it is played.
  The script passed a synthetic test (dry run, apply, and each refusal leaves the data unchanged).
  **Checked (see the f617d608 re-run below):** production has 24 profiles behind 7 logins, and the old web app saved ownerless audio
  for every signed-in learner, so the rows alone cannot show they are all his. Before E4, count
  on the dump copy how many other accounts were active from 2026-05 to 2026-10, and show him.
  The script keeps no list of the rows it changed, so undoing it exactly means restoring the
  C1 dump.

## Rehearsal on the fresh 2026-11-05 dump (C2, code `7b0b8931`)

Same method as above, on the dump taken in C1 (SHA-256 checked; counts equal the live counts).
- **Migrations:** all 8 applied in 119 ms, each in its own transaction, 0 errors.
- **Startup:** 3.5 s to the first response, with the lock held 2.5 s.
  - Word owners: 4781 stamped, 96 shared, 213 unlinked.
  - Example sentences: "89 stamped with their words, 0 stamped after"; 0 unowned.
  - CoreSync: 18 tracked tables became 31.
  - 359 rows queued for devices.
  - A second start changed nothing.
- **Data:** 0 rows lost, 66 of 66 original tables unchanged outside the intended columns, and
  37 of 37 foreign keys valid.
- **Audio:** 237 ownerless rows: the same 217 plus 20 from late October. The dry run (exit 0)
  changed nothing. The apply (exit 0) gave all 237 to his profile and changed only the owner
  column; the sync tracking table grew by exactly 237 rows.
- **Found and fixed (B63):** the word-owner backfill stamped 28 words with an owner ID that is no
  profile. Their only link was progress left by deleted accounts. Now only an existing profile
  can own a word, so those 28 stay ownerless. No rows were lost either way.

**Re-run at `f617d608`** (the final server code; `e85c7f94` changes only device saves):
- Startup took 3.2 s, with the lock held 2.3 s.
- Word owners logged "4753 stamped, 96 shared, 213 unlinked, 28 left ownerless (only owner is
  not a profile)". 0 words now have an owner that is not a profile.
- The 28 words trace back to 1 owner ID. That ID matches no profile and no login, even
  compared case-insensitively and trimmed.
- Example sentences: 89 stamped with their words, 0 after.
- `__core_sync_ct` grew by 5112. 0 rows lost; 37 of 37 foreign keys valid.
- A second start changed nothing.

**Who made the audio** (read-only counts on the same copy):
- **Logins and profiles:** only 2 of the 24 profiles have a login: his, and one test-domain
  account. The one other real-domain login points at a deleted profile and last signed in in
  April, before the audio began.
- **Phrases:** 202 of the 237 rows match only his vocabulary and 0 match only someone else's.
  23 match both; the other side is always a profile with no login or a deleted account,
  never the test account. 12 match neither.
- **Timing:** every row falls on a day he was active, and 188 have his activity within 10
  minutes. No one else has activity within 10 minutes of any row.
- **Conclusion:** no sign that any row came from another account.

## Risks and how this plan handles them

| Risk | Handling |
|---|---|
| Irreversible key rewrites (Phase2a/2b/3, Number Drill IDs). `Down` renumbers and cannot restore the original IDs. | The only rollback is the pre-deploy dump (or Azure point-in-time restore). Take and verify it first (runbook Step 1). |
| Old API/WebApp/Workers revisions write to tables whose IDs changed. | Deploy in a quiet window. Confirm single-revision mode and replica counts first (read-only). Consider scaling the old WebApp and Workers to zero for the migration minutes. |
| Migrations take table locks; no lock timeout; a long transaction from a live old revision could stall one past EF's 30 s timeout. | Quiet window. The API restarts and resumes the chain if a migration fails (rehearsed). |
| Startup probe. | 4.4 s locally against the default probe's about 4 minutes. Read the deployed probe settings before deploying. |
| Old MAUI builds get 403 on sync against the new API. | They keep working offline and keep their changes; install the new MAUI builds right after the API deploy. |
| New MAUI against the old API caches no speech, and every play is a paid generation. | Deploy the API before the MAUI builds. |
| Production has changed since the 2026-10-20 dump. | Re-run the same rehearsal on a fresh dump before deploying. |

## Production holds in `docs/deploy-runbook.md`

1. **Lock survivability fault test: waived for this release** (Captain, 2026-11-05: "waive it").
   Basis: single revision mode and 1 running API replica, a new revision starting at 1
   replica with no traffic until ready, a quiet window, and a verified dump. See the runbook hold.
2. **CoreSync package distribution: resolved.** With the Captain's OK (2026-11-05), the one
   37 KB SHA-verified package is committed (`7b0b8931`, explicit `.gitignore` exception).
   Checked in a clean checkout of that commit, with an empty package cache and `localnugets`
   disabled as CI does:
   - the runbook's cold restore of Api.Tests passed, and Api.Tests built;
   - `CoreSyncProviderPackageTests` passed 2 of 2;
   - Api, SamOpportunityDigest, WebApp, AppLib, UnitTests and UI.Tests restored;
   - WebApp, SamOpportunityDigest and AppLib built with 0 errors.
   Not tried locally: WebApp.Tests and the dependency-closure job. The first CI run on `main`
   covers them.
3. **Older revisions during rollout:** answered by the read-only revision and replica check below.

## Order (each Azure step needs the Captain's go)

**A. Local, no Azure**
1. Fix and test the example-sentence owner ordering. Done.
2. Commit the CoreSync package. Done (`7b0b8931`).
3. Final full test pass and builds.

**B. Azure, read-only** (done 2026-11-05, Captain's go)
1. Runbook Step 2 (server Ready) and Step 3 (CanNotDelete lock present).
2. API, WebApp and Workers container apps: revision mode, min/max replicas, startup probe:
   `az containerapp show -g rg-sstudio-prod-biz -n <app> --query "{mode:properties.configuration.activeRevisionsMode, scale:properties.template.scale, probes:properties.template.containers[0].probes}"`.
3. Speech cache checks (runbook "Speech clip cache", read-only part).

Results:
- **Server:** Ready, PG 16, B1ms, HA off, 7-day backups (earliest restore 2026-10-30).
- **Lock and firewall:** the CanNotDelete lock is present; the only firewall rule is
  AllowAllAzureIps.
- **Container apps** (api, webapp, workers, marketing, cache):
  - all in Single revision mode with 1 running, healthy replica each;
  - scale min 1, max 10, no custom rules;
  - no probes configured, so the platform default applies;
  - webapp sticky sessions are on;
  - the active revisions date from 2026-09-06.
- **Speech cache:**
  - the app identity `mi-rsn72awybem6s` (the API's only identity) holds Storage Blob Data
    Contributor on the storage account;
  - the `media` container does **not** exist; the only container is `coach-dataprotection`;
  - without it the API still speaks, but it caches nothing: every clip is a paid generation,
    and each one logs a "Speech cache: ... failed" warning.
  - Creating the container is step C3 and needs the Captain's go.
- **Role check:** the directory lookup in `az role assignment list --assignee` is blocked by a
  conditional-access token policy. Use `--fill-principal-name false` and filter by principalId.

**C. Azure, protective changes** (C1 done 2026-11-05)
1. Runbook Step 1: fresh verified dump (temporary firewall rule; lock lifted and restored for
   about a minute). This also shows production's actual migration history.
2. Runbook Step 4c on that dump, plus the API startup rehearsal (Production mode against the
   local copy), comparing every table's counts. Then run the audio script on the copy, first as
   a dry run and then for real, and record its ownerless count; it becomes `expected` in E4.
   Delete the copy after.
3. Create the `media` container (missing; the role is already there). Needs the Captain's go.

C1 result:
- **Dump:** `~/sstudio-prod-backup-20261105/sentencestudio.dump`, 10.4 MB, 68 tables, SHA-256
  recorded beside it. pg_dump took 18 s.
- **Live counts:** VocabularyWord 5090, VocabularyProgress 2760, UserProfile 24.
- **Migration history:** 24 applied, latest `20260903175044_AddApplicationOperationLedger`. So
  the 8 rehearsed migrations are exactly what is pending.
- **Access:** the temporary firewall rule was removed and the lock restored. The final state was
  verified.
- **First attempt failed:**
  - The `connectionstrings--db` secret holds the admin password from before 2026-10-24. The
    runbook now names `connectionstrings--sentencestudio`.
  - That attempt also left the lock lifted: my wrapper ran the runbook's re-lock from inside an
    EXIT trap, where macOS bash 3.2 skips it.
  - The post-check showed the lock missing, and it was recreated and verified within about two
    minutes.
  - Nothing else changed in that window. The runbook now warns about this.

**D. Merge:** fast-forward `main` to the branch and push (CI runs).

**E. Deploy (quiet window):**
1. Optionally scale the WebApp and Workers to zero.
2. `azd deploy -e sstudio-prod-biz` (azd only; runbook Step 0).
3. Watch the API log for the migration and backfill lines:
   - `Vocabulary word owners: …`. The 2026-11-05 copy gave 4753 stamped, 96 shared, 213 unlinked,
     28 left ownerless (only owner is not a profile).
   - `Example sentence owners: … stamped with their words, … stamped after`. The 2026-11-05 copy
     gave 89 and 0.
   - CoreSync provisioning
4. Once the API, WebApp and Workers all run their new revisions (the old WebApp writes
   ownerless audio rows, so this must wait for it to be replaced), and before any device
   installs (G), run `scripts/assign-legacy-audio-history.sql`.
   - **Captain re-confirms first.** He has seen the other-account activity counts (see "Open"
     under the rehearsal) and confirmed again that the rows are his. Without that, skip E4;
     the rows stay hidden, not deleted.
   - **Count first.** Take a read-only count of the ownerless StreamHistory rows. If it
     differs from the C2 count, report the difference to the Captain. Rows added since the
     dump came from the old web app's audio cache. Use the live count as `expected` only
     after he confirms.
   - **Run it:**
     - use the database access of runbook Step 1 (temporary firewall rule; lock lifted and
       restored);
     - pass `-v owner_email="$OWNER_EMAIL"` (his sign-in, kept in a variable) and
       `-v expected=<count>`;
     - run first without `apply` (dry run), then with `-v apply=true`.
   It prints counts only. If any check refuses, nothing changes. Stop and report.

**F. Verify:**
1. Runbook Step 5 validation script, and Step 5a sticky sessions.
2. Read-only row counts against the Step 1 counts.
3. Sign in on the web: Dashboard, a quiz with audio, Reading. Check the speech cache log lines.

**G. Devices:** new MAUI Release builds for DX24, the Pixel 5 and the Mac (each install needs the
Captain's go). They back up their database before migrating. Sign in with the Captain's own account
first on each.

**Rollback** (only if the API cannot start or data looks wrong, and only with the Captain's go):
1. Stop the new revisions.
2. Restore the Step 1 dump (or an Azure point-in-time restore from just before the deploy).
3. Redeploy main's previous images.

Writes made after the deploy are lost in a rollback, which is another reason for a quiet window.
