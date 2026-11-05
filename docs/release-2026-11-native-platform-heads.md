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
  stay ownerless by rule. Decision for the Captain: leave hidden, or name the owner explicitly.

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

1. **Lock survivability fault test** (killing the lock backend stops an old replica's writes) has
   no passing result. Decision for the Captain: build the test first, or waive it for this deploy
   on the grounds of a single API replica (to be confirmed), a quiet window, and a verified dump.
2. **CoreSync package distribution:** `CoreSync.PostgreSQL 0.1.129-local3` comes from the
   untracked repo-local feed `lib/coresync-postgresql-local3` (one 40 KB SHA-verified package that
   `.gitignore` excludes). `azd deploy` from this Mac can build with it; CI on `main` and any other
   checkout cannot. Proposed: commit that one package (with a `.gitignore` exception) so CI is green
   and builds are reproducible. Needs the Captain's OK (a binary in the repo).
3. **Older revisions during rollout:** answered by the read-only revision and replica check below.

## Order (each Azure step needs the Captain's go)

**A. Local, no Azure**
1. Fix and test the example-sentence owner ordering. Done.
2. Commit the CoreSync package if approved.
3. Final full test pass and builds.

**B. Azure, read-only**
1. Runbook Step 2 (server Ready) and Step 3 (CanNotDelete lock present).
2. API, WebApp and Workers container apps: revision mode, min/max replicas, startup probe:
   `az containerapp show -g rg-sstudio-prod-biz -n <app> --query "{mode:properties.configuration.activeRevisionsMode, scale:properties.template.scale, probes:properties.template.containers[0].probes}"`.
3. Speech cache checks (runbook "Speech clip cache", read-only part).

**C. Azure, protective changes**
1. Runbook Step 1: fresh verified dump (temporary firewall rule; lock lifted and restored for
   about a minute). This also shows production's actual migration history.
2. Runbook Step 4c on that dump, plus the API startup rehearsal (Production mode against the
   local copy), comparing every table's counts. Delete the copy after.
3. Create the `media` container and blob role only if the checks show them missing.

**D. Merge:** fast-forward `main` to the branch and push (CI runs).

**E. Deploy (quiet window):**
1. Optionally scale the WebApp and Workers to zero.
2. `azd deploy -e sstudio-prod-biz` (azd only; runbook Step 0).
3. Watch the API log for the migration and backfill lines:
   - `Vocabulary word owners: …`
   - `Example sentence owners: … stamped with their words, … stamped after` (89 with their words on the 2026-10-20 copy)
   - CoreSync provisioning

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
