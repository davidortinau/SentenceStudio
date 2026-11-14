# Deploy Runbook

> **Scripts (2026-11-07).** The routine steps below run as scripts; this runbook stays the
> reference for what they do and why. Each needs the same approval as the step it runs.
>
> - Step 1 backup and any temporary database access: `scripts/prod-db-access.sh backup`
>   (or `open` / `close` / `status`).
> - Pre-checks, Option A deploy, sticky sessions, Sam settings comparison and Step 5:
>   `scripts/prod-deploy.sh check`, then `scripts/prod-deploy.sh deploy`
>   (`after` reruns the post-deploy checks).
> - Native iOS on a phone: `scripts/ios-phone.sh Duotopia|DX24` builds and checks the
>   Release app, then stops; rerun with `--yes --no-build` after the Captain approves the
>   install. The MAUI DX24 steps in "Step 2" below are unchanged.

> **Architecture: Azure PostgreSQL Flexible Server (managed)**
>
> As of 2025-07-26, production uses **Azure Database for PostgreSQL — Flexible Server**,
> provisioned by the Aspire AppHost via `AddAzurePostgresFlexibleServer("db")`.
> This eliminates the container + volume-mount data loss vulnerabilities that caused
> the 2025-07-25 incident. The managed service provides automatic backups, point-in-time
> restore, and is completely decoupled from container app deploys.

---

## Current Production Stack (as of 2026-06-07)

Production was migrated to the **business tenant** so the server hosts can authenticate
keyless to Azure AI Foundry (the Foundry resource has local-auth disabled by policy, and
managed identities cannot authenticate cross-tenant). The old personal-tenant stack
(`rg-sstudio-prod`, centralus, `livelyforest-b32e7d63`) is retained as a rollback path
pending decommission — do **not** target it.

| Thing | Value |
|---|---|
| Subscription (business) | `66f9fa8f-604f-4688-bec1-16ff9a86a8e5` |
| Resource group | `rg-sstudio-prod-biz` |
| azd environment | `sstudio-prod-biz` |
| Region | `westus3` |
| ACA environment | `cae-rsn72awybem6s` (domain `agreeablesky-76d2f81f.westus3.azurecontainerapps.io`) |
| Container Registry | `acrrsn72awybem6s` |
| PostgreSQL Flexible Server | `db-rsn72awybem6s` (db `sentencestudio`) |
| Key Vault | `dbkv-rsn72awybem6s` |
| Log Analytics | `law-rsn72awybem6s` |
| App Insights | `appi-sstudio-biz` |
| Managed identity (UAMI) | `mi-rsn72awybem6s` |
| Storage | `storagersn72awybem6s` (the AppHost declares blob containers `coach-dataprotection` and `media`; see "Speech clip cache" below) |
| **API** | `https://api.agreeablesky-76d2f81f.westus3.azurecontainerapps.io` |
| **WebApp** | `https://webapp.agreeablesky-76d2f81f.westus3.azurecontainerapps.io` |
| **Marketing** | `https://marketing.agreeablesky-76d2f81f.westus3.azurecontainerapps.io` |
| Aspire dashboard | `https://aspire-dashboard.ext.agreeablesky-76d2f81f.westus3.azurecontainerapps.io` |

**AI / Azure AI Foundry:** server hosts call Foundry resource **`daortin-sstudio-eus2`**
(eastus2, rg `rg-daortin-4819`, same business sub). Endpoint
`https://daortin-sstudio-eus2.openai.azure.com/openai/v1` is injected as the
`AI__OpenAI__Endpoint` env var by `AppHost.cs`. Auth is **keyless via Entra** — the prod
UAMI `mi-rsn72awybem6s` holds the **Cognitive Services OpenAI User** role on that account.
This one account carries chat (`gpt-5-mini` fast / `gpt-5` reasoning) plus realtime +
transcribe model availability. See memory `ai-foundry-model-tiers`.

The native Conversation voice mode (2026-11-06) needs the **`gpt-realtime`** and
**`gpt-4o-transcribe`** deployments on that account (names overridable with
`AI__OpenAI__Models__Realtime` / `AI__OpenAI__Models__Transcribe`). The API's
`POST /api/v1/realtime/client-secret` mints a 120-second secret for the signed-in learner
(10 per learner per 5 minutes, per replica) with the same UAMI; the phone then opens the
realtime session with it. Verified locally against `daortin-sstudio-eus2` on 2026-11-06.

Production deploys must **not** require an `openaikey` / `AI__OpenAI__ApiKey` parameter
for server hosts. That legacy key is only for local direct-client fallback paths; if
`azd deploy` asks for `openaikey`, stop and fix AppHost/deploy wiring instead of copying
or inventing a secret.

## Application schema startup order

The API is the **only server host** that applies `ApplicationDbContext` migrations
**and runs its shared vocabulary/phrase startup backfills**. It now uses one
nonpooled PostgreSQL session for the application, coach, and feedback migrations,
shared backfills, number seeding, development-account seeding, and CoreSync
provisioning through the caller-owned local3 provider overload. A latched
session/backend check must abort startup rather than retry writes after losing
the advisory lock. This source wiring has built locally, but a two-replica
connection-loss proof is still required before deployment. Only after startup
completes does the API serve `/api/version/info`.
The public response includes `applicationStartupContract` and
`applicationMigrationId`, in addition to the ordinary app version. Bump the startup
contract in `src/SentenceStudio.Contracts/Startup/ApiApplicationStartupContract.cs`
when API-owned shared backfill behavior changes without a new migration.
Development/Testing API hosts with `Database:SkipMigrateOnStartup=true` return 503
from the readiness route rather than falsely advertising startup completion.

The WebApp does **not** migrate or run shared startup backfills. Before opening its
HTTP listener, it requires the current startup contract and the same last compiled
application migration ID from the API, then checks its own pending migrations
**read-only**. A 200 from an older serving API revision, even with zero pending
migrations, cannot release a WebApp whose startup contract differs. A new migration
also prevents older API revisions from releasing the gate even if a contract bump
was missed.

Locally, AppHost also uses `WithHttpHealthCheck("/api/version/info")` and
`WaitFor(api)` to hold WebApp startup until API readiness. That AppHost ordering is
**not** a deployment guarantee for the `azd`/Azure Container Apps path in `azure.yaml`;
the WebApp enforces the gate in its own process regardless of launch order. It retries
temporary API failures, incompatible older revisions, or pending migrations for **up
to three minutes for this gate only**, then logs a critical startup failure and exits
rather than serving on an incompatible schema. Its actual total startup time also
includes host construction and dependency initialization; the API's migration,
backfills, and provisioning have **no three-minute bound**. This value is **not** a
claim about the Container Apps startup probe allowance. No probe duration is
configured in the checked-in `azure.yaml` or the AppHost resource declaration;
inspect the **rendered/deployed** Container Apps probe configuration and revision
health before rollout, rather than assuming any default applies. An invalid API
response or an authorization requirement on the public version route fails immediately.
The API refuses
`Database:SkipMigrateOnStartup=true` outside Development and Testing.

**Production HOLD — lock survivability:** the same-session implementation is not
yet a passing fault-injection result. Do not deploy until a separately attested,
fresh PostgreSQL cluster proves that killing the lock backend stops the old
replica's migration, backfill, and CoreSync writes before the next replica writes.
The currently drafted fault-test fixture is rejected for execution; do not run
it or infer safety from healthy-connection tests.

**Waived by the Captain for the November 2026 release only (2026-11-05).** Basis, from
read-only checks that day:
- every container app is in single revision mode, with 1 running replica;
- the API allows up to 10 replicas but has no custom scale rule, so it scales only on HTTP
  load. A new revision starts at 1 replica and gets no traffic until it is ready, so only one
  new replica runs startup;
- the deploy happens in a quiet window, after a verified fresh dump.

The hold stands for later releases until the fault test passes.

**Production HOLD — CoreSync package distribution:** `CoreSync.PostgreSQL` uses
the exact range `[0.1.129-local3]` from the reviewed local CoreSync commit
`d611359` (the provider nuspec records that commit). The staged provider-only
repository feed contains this SHA-256-verified archive:

| Package | SHA-256 |
| --- | --- |
| `CoreSync.PostgreSQL.0.1.129-local3.nupkg` | `c301f31bb4c7415242b545835230d566499f6805fd3bc74cd51098e10c0a05f9` |

`src/NuGet.config` maps the **exact provider ID** to
`lib/coresync-postgresql-local3`, a repository-local feed containing only the
SHA-verified provider archive. It maps the **exact base ID** `CoreSync` to
nuget.org, which serves the stable `CoreSync/0.1.129` needed by the 0.1.129
HTTP and SQLite packages. NuGet's
[package pattern precedence](https://learn.microsoft.com/en-us/nuget/consume-packages/package-source-mapping#package-pattern-precedence)
puts exact IDs ahead of every `CoreSync*` prefix or `*` wildcard on the
legacy `coresync-local`, public, and developer-local feeds; source-list
order does not override this rule.
The provider nuspec's minimum base dependency accepts stable 0.1.129, which
the stable HTTP and SQLite packages also require. The base is not part of the
repo-local provider seam; do not force the lower prerelease base or suppress
NU1603/NU1605.

A package already in the global cache bypasses source mapping, so a warm
restore is not evidence for this fix. To prove cold resolution from the
worktree root, run the following on a machine with the reviewed feed archive
available (retain the scratch cache for inspection):

```bash
export NUGET_PACKAGES="$(mktemp -d "${TMPDIR:-/tmp}/ss-coresync.XXXXXX")"
dotnet restore tests/SentenceStudio.Api.Tests/SentenceStudio.Api.Tests.csproj \
  --configfile src/NuGet.config --force --no-cache \
  -p:IncludeMobileTargets=false -p:Configuration=Release \
  -warnaserror:NU1603,NU1605
dotnet test tests/SentenceStudio.Api.Tests/SentenceStudio.Api.Tests.csproj \
  --no-restore -c Release -p:IncludeMobileTargets=false \
  --filter FullyQualifiedName~CoreSyncProviderPackageTests
```

The provenance test checks effective mappings, both resolved package IDs,
the one-package reviewed feed, the cached provider archive's SHA-256, and the
caller-owned overload. The CI workflows disable `localnugets`; CI has no
access to `$HOME/work/LocalNuGets`. **Committed 2026-11-05** with the Captain's
approval (`7b0b8931`, explicit `.gitignore` exception). In a clean checkout of that
commit, the cold restore above passed with an empty package cache and
`localnugets` disabled, and the provenance test passed 2 of 2. The remaining
check for this hold is the first CI run on `main`. The separate lock-survivability
and rollout gates above still apply. No package was published to any feed.

**Rollout hold — older revisions:** a new WebApp will not run these backfills,
but a previously deployed WebApp or API revision still contains the old startup
code and does not participate in the new API advisory lock. Before authorizing
production, confirm the actual API/WebApp revision mode, traffic routing,
and startup/scale behavior;
older replicas must not begin a competing backfill during the new API's startup.
The WebApp checks one API response, not every target in a split-traffic rollout.
Do not infer revision or probe behavior from local `WaitFor(api)` or the `azd`
manifest alone. If old startup writers or a sufficient probe allowance cannot
be ruled out, **hold deployment** until the rollout is staged safely.

Before any production rollout, verify this path against an **isolated** PostgreSQL
database with concurrently starting API replicas and API/WebApp processes; do not
use production or a developer's existing data volume as the test database. The
WebApp's readiness algorithm, redirect handling, and startup ownership tests do not
require PostgreSQL. The separate real-Identity WebApp integration family remains
opt-in: it requires `WEBAPP_PG_TEST_CONNECTION` to a loopback disposable cluster and
`WEBAPP_PG_TEST_OWNER_ID` to a fresh per-run UUID matching the server's **startup**
`cluster_name` (`webapp-it-<uuid>`). It ignores coach/design-time connection variables
and never drops databases or test preferences. The fixture attests the actual open
connection immediately before database creation, migration, and direct seeding;
an EF command interceptor also attests every migration write, including an
unexpected attempt to create a database on EF's maintenance connection;
only names created in that process may be used for migration. Without that owned
cluster it skips locally; with mismatched ownership it fails before any database
is created. The CI WebApp job sets the marker on its own identified disposable
PostgreSQL service, restarts it (the parameter is server-start-only), verifies the
marker, and fails if any PostgreSQL-gated test is missing or skipped. Test hosts set
`WebApp__AppDataRoot` to distinct paths under the test output, restore the prior
environment after use, and leave scratch data intact rather than deleting files.
After an authorized deploy, the mandatory post-deploy validator and
migration-history checks below still apply.

---

## Ownerless shared words and withheld rows (one-time, owner-only words)

Prepared 2026-11-06 with the owner-only word rule (Captain: "every word should have an owner
and only be visible and used by those owners. There is no reason for a mobile device to sync any
data for another user/owner than the one logged in."). **Nothing below has been run against
Azure.** Rehearsed only on isolated local PostgreSQL. Each step runs only with the Captain's go
in that turn, never automatically, never from a deploy pipeline.

Two scripts, in this order:

- `scripts/record-shared-vocabulary-words.sql` records to one profile the ownerless words that
  profile shares only with test accounts (listed email domains) and with progress left by deleted
  accounts. The 2026-11-05 backup has 96: 52 shared with the test account, 41 with a deleted
  account's progress, 3 with both, all in the Captain's resources, 60 of them practised by him.
  The owner backfill left them ownerless because two accounts use them.
- `scripts/send-withheld-vocabulary-rows.sql` records a no-op change for every row the new sync
  filter sends that the old one withheld (words recorded to a learner that none of the learner's
  resources link, and their progress, learning contexts, sentences, phrase parts and minimal
  pairs), so devices download them. The 2026-11-05 backup has 3 such words of the Captain's.

**Order (do not reorder; each step only with the Captain's go):**

1. Fresh verified backup (Step 1 below). Cloud sync is not a backup.
2. `record-shared-vocabulary-words.sql`: dry run, then apply with its counts (2026-11-05 backup:
   52, 41, 3). Production already has `VocabularyWord.UserProfileId` (the 2026-11-05 deploy
   applied `AddVocabularyWordOwner` and stamped 4753 words), and today's production code still
   shows these 96 words as normal, so this runs **before** the hiding change ships. The change
   that hides ownerless words must not reach production before this step commits, or the 96
   words (60 of them practised) disappear from the Captain's resources on the web, MAUI and
   native (hidden, not deleted: the rows stay).
3. Let the Captain's devices sync (they download the newly recorded owners).
4. Deploy this release (the new owner-only sync filter and the hiding ship together). The touch
   in step 6 needs the new filter live: under the old filter a touched word outside the
   learner's resources is filtered out of the download and the device's anchor moves past it,
   so it would be lost again.
5. Let every one of the Captain's devices sync against the new server and finish (no pending
   changes).
6. `send-withheld-vocabulary-rows.sql`: dry run, then apply with its counts, straight away
   after step 5 and before he edits or practises again. A device's unsynced delete of a touched
   row is undone by the touch (the row comes back; nothing is lost), which is why this step runs
   right after the devices synced.
7. Let the devices sync again (they download the touched rows).

Look up the target profile at run time; never write a real ID or email into the repository:

```bash
# PG* variables from the private credentials file of Step 1; never on a command line.
TARGET=$(psql -d sentencestudio -At -v email="$CAPTAIN_SIGN_IN_EMAIL" <<'SQL'
SELECT "UserProfileId" FROM "AspNetUsers" WHERE "NormalizedEmail" = upper(:'email');
SQL
)
echo "${TARGET:0:8}"   # check the prefix only; it must be exactly one profile

# Step 2: dry run, then apply with the dry run's counts (2026-11-05: 52, 41, 3).
psql -d sentencestudio -v ON_ERROR_STOP=1 -v target_profile="$TARGET" \
  -v test_domains=sentencestudio.test -f scripts/record-shared-vocabulary-words.sql
psql -d sentencestudio -v ON_ERROR_STOP=1 -v target_profile="$TARGET" \
  -v test_domains=sentencestudio.test -v expected_test=52 -v expected_nonprofile=41 \
  -v expected_both=3 -v expected_sentences=<dry run> -v apply=true \
  -f scripts/record-shared-vocabulary-words.sql

# Step 6: dry run, then apply at once with every count and its "tracked up to" value.
psql -d sentencestudio -v ON_ERROR_STOP=1 -f scripts/send-withheld-vocabulary-rows.sql
psql -d sentencestudio -v ON_ERROR_STOP=1 -v expected_words=<n> -v expected_progress=<n> \
  -v expected_contexts=<n> -v expected_sentences=<n> -v expected_phrases=<n> \
  -v expected_pairs=<n> -v expected_answers=<n> -v expected_tracked=<n> -v apply=true \
  -f scripts/send-withheld-vocabulary-rows.sql
```

The record script refuses a target whose email is in a listed domain, a word any other learner
uses (link, progress, sentence, pair, answer or phrase), a link from a resource with no owner,
and a word the target neither links nor practises; it reports those, and the words only the
target uses (the owner backfill's), without recording them. Other accounts' rows are not
changed. A second run records nothing. The touch script's `expected_tracked` is the newest
CoreSync change ID at the dry run, so any sync or edit in between, or an earlier apply, refuses
the apply until a fresh dry run. Both scripts check that CoreSync recorded one change per row.

**Why "right after every device synced"** (CoreSync.PostgreSQL 0.1.129-local3, CoreSync.Sqlite
0.1.129, CoreSync.Http.Server/Client 0.1.129, read from the packages):

- Each tracked table has an `AFTER UPDATE ... FOR EACH ROW` trigger with no condition that
  inserts `(tbl, 'U', id)` into `__core_sync_ct` with `src` NULL, so `SET "Id" = "Id"` is logged.
  A download sends every row with a change newer than the device's anchor whose `src` is NULL or
  another store, with the row's current values. The sync agent uploads first, then downloads.
- A device's **unsynced edit** of a touched row uploads with an anchor older than the touch, so
  the server's guarded UPDATE (`MAX(__core_sync_ct.id) for the row <= the device's server
  anchor`) reports a conflict. The CoreSync HTTP server resolves an update conflict with
  ForceWrite (`SyncAgentController` passes ForceWrite for updates and Skip for deletes;
  `NativeOwnerSyncProvider` falls back to it), so the device's edit wins and comes back to it
  unchanged in the same sync. Nothing is lost.
- A device's **unsynced delete** of a touched row is skipped by the server (delete conflicts are
  Skip), and the touch then downloads the row, which the device re-inserts (`SyncService`
  resolves a local update conflict with ForceWrite). Nothing is lost, but the delete is undone.
  Syncing every device first leaves no pending deletes.
- A device's edit that the **old filter left out** of an earlier upload (a word the learner's
  resources stopped linking, edited or practised on a device afterwards) was never stored on the
  server, and the device's anchor already moved past it. The touch downloads the server's copy
  over it without a conflict. Syncing first does not help these. The touch script's per-learner
  counts are the upper bound (2026-11-05: 3 of the Captain's words).
- **Native heads:** `NativeLocalOnlyMappingExportProvider` used to resolve every downloaded
  `VocabularyProgress` update conflict as Skip (to protect a local attempt), so a touched
  progress row the device did not have (an update that changes no row) was skipped instead of
  inserted, and its learning contexts then failed their foreign key. Fixed with this change: the
  device keeps its own copy only when that progress row exists locally; otherwise the download
  inserts it, as MAUI's `SyncService` always did
  (`tests/SentenceStudio.Native.Tests/NativeLocalOnlyMappingExportProviderTests.cs`). The native
  build with that fix must be on the devices before step 6.

---

## Speech clip cache (Phase 4 audio): Azure step awaiting approval

Prepared 2026-11-04. **Nothing below has been run against Azure.** The Captain approves the
Azure step.

**What changed.** `/api/v1/speech/synthesize` and `/api/v1/speech/synthesize-timestamped` keep
every clip they generate in the blob container `media` under `speech-cache/v1/`, named by a
SHA-256 of provider, variant, voice ID, model, voice settings, output format and exact text
(`src/SentenceStudio.Api/Speech/SpeechClipSpec.cs`). A repeat request is served from the blob
without an ElevenLabs call. No schema change, same responses. The callers are the native iOS and
Android heads and, since B60 below, the WebApp and the MAUI apps.

**How the API reaches it.** The AppHost now declares the container
(`storageAccount.AddBlobContainer("media-container", "media")`) and references it from the API,
like the coach key ring. The generated manifest gives the API
`ConnectionStrings__media-container = Endpoint={storage.outputs.blobEndpoint};ContainerName=media`;
the API authenticates with `DefaultAzureCredential`, the same path its keyless Foundry calls use.
The storage account has `allowSharedKeyAccess: false`, so only Entra access works.

**What the API identity needs.** Blob data read and write on `media` (Storage Blob Data
Contributor, at the container or account scope). No container-create rights.

**Does it already have it? Probably, but unverified.** Evidence gathered locally with
`dotnet run --project src/SentenceStudio.AppHost -- --publisher manifest --output-path <dir>`
(no Azure contact), on both `ca4122bf` and this change:

- The AppHost uses the azd manifest path (no `AddAzureContainerAppEnvironment`), where every
  container app runs as the one UAMI `mi-rsn72awybem6s`. That is the identity holding
  Cognitive Services OpenAI User above, so the API already runs as it.
- The generated `storage-roles.module.bicep` assigns **Storage Blob Data Contributor**, Table Data
  Contributor and Queue Data Contributor on the whole `storage` account to the azd principal
  (that UAMI). This module is unchanged by the speech cache.
- So the UAMI has blob data access on `storagersn72awybem6s` **if `azd provision` has run since
  the storage roles were in the AppHost.** `azd deploy` alone never applies infra, and no Azure
  command was run to confirm the assignment exists.
- Before this change, the only container the AppHost declared was `coach-dataprotection`. The
  `media` in `AddBlobs("media")` is the blob *service* resource name, not a container. The stack
  table lists a `media` container; confirm it exists before relying on it.

**Read-only checks (run these first):**

```bash
S=66f9fa8f-604f-4688-bec1-16ff9a86a8e5; RG=rg-sstudio-prod-biz; ACCOUNT=storagersn72awybem6s
PRINCIPAL=$(az identity show -g $RG -n mi-rsn72awybem6s --subscription $S --query principalId -o tsv)
SCOPE=$(az storage account show -g $RG -n $ACCOUNT --subscription $S --query id -o tsv)

# 1. Blob data roles the UAMI holds on the account, its resource group or the subscription.
az role assignment list --assignee "$PRINCIPAL" --scope "$SCOPE" --include-inherited \
  --subscription $S --query "[].{role:roleDefinitionName, scope:scope}" -o table

# 2. Does the media container exist? (management plane; needs no data access)
az storage container-rm exists --storage-account $ACCOUNT -g $RG -n media --subscription $S

# 3. The API runs as the UAMI (client ID only; not a secret).
az containerapp show -g $RG -n api --subscription $S \
  --query "{identities:identity.userAssignedIdentities, clientId:properties.template.containers[0].env[?name=='AZURE_CLIENT_ID'].value}" -o json
```

**The Azure change to approve (only what the checks show is missing):**

```bash
# Container missing:
az storage container-rm create --storage-account $ACCOUNT -g $RG -n media \
  --public-access off --subscription $S

# Blob data role missing. Least privilege, container scope:
az role assignment create --assignee-object-id "$PRINCIPAL" --assignee-principal-type ServicePrincipal \
  --role "Storage Blob Data Contributor" \
  --scope "$SCOPE/blobServices/default/containers/media" --subscription $S
```

`azd provision -e sstudio-prod-biz` would also create the container and re-apply
`storage-roles` (account scope, the same grant the coach key ring relies on), but it re-applies
all infrastructure, so prefer the two targeted commands unless a provision is planned anyway.
Role assignments can take 5 to 10 minutes to take effect.

**Then deploy as usual** (`azd deploy -e sstudio-prod-biz`), which adds the new connection string
to the API. The order does not matter for safety: with no access or no container the API logs
`Speech cache: read failed ... Status=403` (or `404` / `ContainerNotFound`) as a warning and
generates speech exactly as before. `SpeechCache__Enabled=false` on the API turns the cache off
without a code change.

**After the deploy.** Play the same quiz word on two native clients, for example an iOS
simulator and an Android emulator. Use two devices because each keeps its own copy after the
first play and does not ask the API again. App Insights traces should show `Speech cache: stored
a generated clip` for the first device, `Speech cache: served a stored clip` for the second, and
no `Speech cache: read failed` or `write failed` warnings. Log lines carry only a 12-character
hash prefix; text, user and resource never reach the blob name, metadata or the cache logs.

**Web and MAUI go through the API (B60, Captain 2026-11-04).** The shared Razor pages
(VocabQuiz, HowDoYouSay, Shadowing, Reading, NumberDrill, Minimal Pairs, Conversation, the word
editor) no longer hold an `ElevenLabsClient`. `ElevenLabsSpeechService` (AppLib) calls
`POST /api/v1/speech/synthesize` and, for Reading, `/synthesize-timestamped` through
`AddApiClients`, so every web and MAUI clip uses the same cache as native. The Settings and How
Do You Say voice lists come from `GET /api/v1/speech/voices` (`ApiVoiceDiscoveryService`).

- **Identity.** On the web, `AuthenticatedHttpMessageHandler` gets a JWT from `ServerAuthService`
  minted for the signed-in learner, from the HTTP request or the Blazor circuit, carrying
  `user_profile_id`. On MAUI it is the app's own API access token. With no active learner the
  service throws before any request. The API's auth and owner checks are unchanged:
  `/synthesize` requires a signed-in caller, `/synthesize-timestamped` reads only the caller's
  own resource.
- **Same audio as before.** Pages send their own voice settings. `/synthesize` gained optional
  `Stability`, `SimilarityBoost` and `Speed` (clamped like the timestamped endpoint; a non-finite
  value is a 400). A request without them is handled exactly as before and finds the same stored
  clip (pinned by `SpeechSynthesizeSettingsEndpointTests`). Both endpoints now also accept a raw
  ElevenLabs voice ID that the API offers: the slug voices, the client default voices,
  `AI:ElevenLabs:Voices`, or an account voice listed by `/speech/voices`. One layer owns the
  account list, the API's `VoiceDiscoveryService`: one ElevenLabs call per load, shared by concurrent
  requests, kept for 24 hours from when the load completed (the same list `/speech/voices` shows);
  a failed load is never kept and is retried on a later request after 30 seconds. Any other ID, or
  an account voice while the list cannot be read, falls back to the default voice, logged without
  its value. This changes native audio too: native quiz and shadowing send the stored ID (JiYoung
  for Korean), which the API used to answer in its default voice (Rachel).
- **No client keeps a clip under a voice the API did not use.** Both responses now carry `VoiceId`
  (the voice used) and `VoiceSubstituted` (true when the API used its default voice instead of the
  one asked for). Web, MAUI and native keep a clip only when the response names the voice and says
  it was not substituted; a substituted clip, or one from an older API that sends neither field,
  plays but leaves no file, StreamHistory row or AppData entry.
- **Reading** clips are for the server's copy of the transcript. The timestamped response carries
  `TranscriptSha256`; web, MAUI and native neither play nor keep a clip whose fingerprint differs
  from their own transcript, and the cached AppData metadata
  (`timestamped_<resource>_<voice>_<speed>.mp3/.json`) records it, so a clip for older text is made
  again. A resource created on the device and not synced yet gets a 404, which the page shows as an
  audio error.
- **No retries for synthesis.** Each synthesis request is one attempt (no resilience pipeline), so
  one clip is never paid for twice by a client retry. A word or sentence has a two-minute bound;
  Reading's timestamped request keeps five minutes for long transcripts. The voice list GET is
  idempotent and keeps the standard retries.
- **Failures** surface on the page: an API refusal throws with its status, and a missing or empty
  clip throws. The old service returned an empty stream instead.

**Deploy order: API before web and MAUI.** Production's API already has both speech endpoints:
`/synthesize` since `74f1961d` (2026-02-24) and `/synthesize-timestamped` since `aa2ff448`
(2026-05-15), both on `main`. No Azure command was run to read the deployed revision. It does
**not** have the clip cache (`aa5b7055`) or the optional settings and raw voice IDs (B60), which
are only on `native-platform-heads`. An older API ignores the new fields and answers raw voice IDs
in its default voice, so web or MAUI audio would play, but in the wrong voice and speed, until
the API is updated. Nothing is lost.

1. API from this branch, under the production HOLDs above, after the Azure step in this section.
   Prefer `azd deploy api -e sstudio-prod-biz` on its own first. The WebApp's startup gate does
   not order this change: there is no new migration or startup contract.
2. Check the API: in App Insights, a native or web quiz word shows `Speech cache: stored a
   generated clip` and no `Speech voice is not one this API offers` line for a chosen voice.
3. WebApp (`azd deploy webapp -e sstudio-prod-biz`).
4. MAUI builds (iOS, Android, Mac) once the API is live. Installed older MAUI builds keep calling
   ElevenLabs directly with their embedded key until they are replaced.

**Key removal.** `AppHost.cs` no longer passes `ElevenLabsKey` to `webapp` or `workers`; nothing in
either reads it. After the next deploy their container apps no longer carry the secret. The API keeps
`ElevenLabsKey`. Once the new builds are out, also remove `Settings:ElevenLabsKey` from the WebApp's
configuration (user secrets) and from the MAUI `src/SentenceStudio.AppLib/appsettings.json`, which is
embedded in every MAUI build. Shipped MAUI binaries contain the
key, so rotating it after the MAUI devices are updated is worth considering. Rotation changes the
API's `elevenlabskey` secret and is an Azure step for the Captain.

**Not covered yet.** Older installed MAUI builds, until they are replaced. Reading audio for a
resource that has not synced. Open items (not fixed):

- Wrong-voice clips cached before B60. Native clients sent raw voice IDs that the old API answered
  in its default voice, and kept those clips in StreamHistory rows (and files) tagged with the
  requested voice. Nothing cleans them up. Captain 2026-11-04: ignore them (no cleanup).
- How Do You Say during an outage. A clip that comes back substituted (or from an older API) is
  played but not recorded, so it does not appear in History.
- A server-side transcript check. The client could send its own `TranscriptSha256` and the API answer
  409 before generating, instead of the client discarding a clip it already paid for. A possible
  improvement.

**Data notes.** The clips are audio of learner and resource text, kept in a private container;
anyone who can read `media` can listen to them, the same exposure as the database that holds
the text. Clips are small and no lifecycle rule deletes them yet. A changed key format must bump
`SpeechClipCacheKey.Version` (a new `speech-cache/v2/` prefix) rather than reuse `v1`.

---

## MANDATORY Pre-Deploy Safety Checklist

**Every step below MUST pass before ANY production deploy command is executed.**  
**Failure of ANY step is a HARD STOP — do not proceed, do not "try anyway."**

### Step 0: NEVER mix deploy tools

**Use EITHER `azd deploy` OR `aspire deploy` — NEVER both in the same session.**

Why: `azd` and `aspire deploy` manage Azure resources through different Bicep pipelines.
Pick one tool. Stick with it.

**Current recommendation:** Use `azd deploy` until `aspire deploy` exits preview.
For infrastructure changes (like provisioning the Flexible Server), use `azd provision` first.

### Step 1: Back up the production database

Azure keeps automatic restore points for **7 days** (`backup.backupRetentionDays`),
which is a fallback, not a substitute. Before any deploy that includes a migration or
data change, take your own full dump. This procedure was verified on 2026-10-20 and again on 2026-11-05
(68 tables).

**Use the `connectionstrings--sentencestudio` secret.** The admin password changed on
2026-10-24 and only that secret (the one the API uses) holds the current one;
`connectionstrings--db` still holds the old password and fails with "password
authentication failed".

**Run step 4 as written, at the top level of your shell.** Do not wrap it in a function
that runs from another `trap ... EXIT`: macOS bash 3.2 then never runs the subshell's
EXIT trap, so the lock stays lifted (this happened once on 2026-11-05 and the post-check
caught it). Always read the lock list at the end, and recreate the lock with the Step 3
command if it is missing.

```bash
S=66f9fa8f-604f-4688-bec1-16ff9a86a8e5
RG=rg-sstudio-prod-biz; SERVER=db-rsn72awybem6s
BACKUP=~/sstudio-prod-backup-$(date +%Y%m%d); mkdir -p $BACKUP && chmod 700 $BACKUP
RULE=deploy-backup-$(date +%Y%m%d-%H%M)   # computed once; reused in step 4
PG=postgres:16                            # client major version >= the server's (16)

# 1. Credentials stay in a private file, never on a command line.
umask 077
az keyvault secret show --vault-name dbkv-rsn72awybem6s -n connectionstrings--sentencestudio \
  --query value -o tsv > $BACKUP/conn.txt
python3 - "$BACKUP/conn.txt" > $BACKUP/pg.env <<'PY'
import sys
p = dict(x.split("=", 1) for x in open(sys.argv[1]).read().strip().split(";") if "=" in x)
print(f"PGHOST={p['Host']}\nPGUSER={p['Username']}\nPGPASSWORD={p['Password']}\nPGSSLMODE=require")
PY
rm $BACKUP/conn.txt

# 2. Temporary firewall rule for your IP.
MYIP=$(curl -s https://api.ipify.org)
az postgres flexible-server firewall-rule create -g $RG --name $SERVER --subscription $S \
  --rule-name $RULE --start-ip-address $MYIP --end-ip-address $MYIP

# 3. Dump (custom format for pg_restore) and verify.
docker run --rm --env-file $BACKUP/pg.env -v $BACKUP:/out $PG \
  pg_dump -d sentencestudio -Fc -f /out/sentencestudio.dump
docker run --rm -v $BACKUP:/in $PG pg_restore -l /in/sentencestudio.dump | grep -c 'TABLE DATA'
# Record key row counts to compare against the restored copy in 4c.
docker run --rm --env-file $BACKUP/pg.env $PG psql -d sentencestudio -At -c \
  'select (select count(*) from "VocabularyWord"), (select count(*) from "VocabularyProgress"), (select count(*) from "UserProfile")' \
  | tee $BACKUP/row-counts.txt
rm $BACKUP/pg.env

# 4. Close access: delete the rule. The CanNotDelete lock on the server (Step 3) also
#    blocks deleting its firewall rules, so lift the lock, delete the rule, and put the
#    lock back. The subshell's EXIT trap restores the lock even if the delete fails.
#    The lock is off for about a minute.
(
  LOCK_ARGS=(-n do-not-delete-postgres -g $RG --resource $SERVER
    --resource-type Microsoft.DBforPostgreSQL/flexibleServers --subscription $S)
  trap 'az lock create "${LOCK_ARGS[@]}" -t CanNotDelete \
    --notes "Protect production managed PostgreSQL from accidental deletion" -o none' EXIT
  az lock delete "${LOCK_ARGS[@]}" &&
  az postgres flexible-server firewall-rule delete -g $RG --name $SERVER --subscription $S \
    --rule-name $RULE --yes
)
az lock list -g $RG --subscription $S -o table
az postgres flexible-server firewall-rule list -g $RG --name $SERVER --subscription $S -o table
```

**Pass condition:** the `TABLE DATA` count matches the number of application tables
(68 on 2026-10-20), `row-counts.txt` holds the live counts, the lock list shows
`do-not-delete-postgres` (CanNotDelete) again, and the only firewall rule left is
`AllowAllAzureIps`.
**If this fails:** STOP. Do not deploy a migration without a verified dump. If the lock
is missing, recreate it with the Step 3 command before doing anything else.

### Step 2: Verify the Flexible Server is healthy

```bash
az postgres flexible-server show \
  --name db-rsn72awybem6s --resource-group rg-sstudio-prod-biz \
  --query "{name:name, state:state, version:version, sku:sku.name}" -o json
```

**Pass condition:** `state` is `Ready`.

### Step 3: Verify resource locks exist

```bash
az lock list --resource-group rg-sstudio-prod-biz --output table
```

**Pass condition:** Output shows a lock on the Flexible Server resource (CanNotDelete).

**If no lock on the Flexible Server:** Create one:

```bash
az lock create --name do-not-delete-postgres \
  --resource-group rg-sstudio-prod-biz \
  --resource db-rsn72awybem6s \
  --resource-type Microsoft.DBforPostgreSQL/flexibleServers \
  --lock-type CanNotDelete \
  --notes "Protect production managed PostgreSQL from accidental deletion"
```

### Step 4: Validate migrations (if the deploy includes migration changes)

**If the deploy includes a new or modified EF Core migration, BOTH gates below are MANDATORY and NON-SKIPPABLE.**

**4a. Static attribute guard (always run — fast, deterministic, no device needed):**

```bash
bash scripts/validate-migration-attributes.sh
```

**Pass condition:** exits 0 with `✅ All migrations carry discoverable [Migration]/[DbContext] attributes.`

This catches the RECURRING dual-provider bug where a hand-written SQLite migration is
missing its `[Migration("<id>")]` attribute and is **silently skipped** on mobile
(`AddRefreshTokenReplacedBy` 2026-05-03, `AddActivitySession` 2026-07-02 — both reached
devices; the second killed dashboard interactivity on iOS). **A raw-DDL/schema-copy test
CANNOT catch this** — the SQL is valid; EF just never invokes the migration. This same
check runs in CI (`ci.yml` → `migration-guard`).

**4b. Mobile SQLite migration validation (real EF apply on private scratch databases):**

```bash
bash scripts/validate-mobile-migrations.sh --validate-native-sqlite-only
```

**Pass condition:** exits 0 after reporting `SQLite model parity, fresh migrations,
and data-preserving upgrade passed` for **both** `net11.0-ios` and `net11.0-android`.
This mode compiles each mobile Shared assembly in isolated outputs, checks EF discovery
and the model/snapshot differ, applies migrations through real EF on fresh and prior
scratch SQLite databases, and verifies migration history, schema, and retained rows.
It does **not** launch an app or verify a signed native bundle. Before deployment,
the platform owner must separately launch normal `MigrateAsync()` on a disposable
simulator/emulator app database and inspect `__EFMigrationsHistory` and the affected
columns/indexes. A Debug `EnsureCreated` preview is not migration proof.

The unqualified script path is **not** this gate: it would launch the old MAUI
reference head, whose production bundle identity is explicitly blocked. Do not
disable its production-bundle guard to make the unqualified command pass. For
isolated EF-only validation of the immediately previous SQLite migration,
`--validate-ef-discovery-only` is available as an additional check.

**4c. Server migration rehearsal on a copy of production (PostgreSQL):**

Restore the Step 1 dump into a throwaway local container and apply the pending
migrations to it, exactly as the API will (same shell as Step 1, so `$BACKUP` and
`$PG` are set):

```bash
docker run -d --name ss-migration-rehearsal -e POSTGRES_PASSWORD=rehearsal \
  -p 127.0.0.1:55433:5432 $PG
docker cp $BACKUP/sentencestudio.dump ss-migration-rehearsal:/tmp/prod.dump
docker exec ss-migration-rehearsal sh -c 'createdb -U postgres sentencestudio && \
  pg_restore -U postgres -d sentencestudio --no-owner --no-privileges /tmp/prod.dump'
dotnet ef database update --project src/SentenceStudio.Shared/SentenceStudio.Shared.csproj \
  --startup-project src/SentenceStudio.Shared/SentenceStudio.Shared.csproj --framework net10.0 \
  --connection "Host=127.0.0.1;Port=55433;Database=sentencestudio;Username=postgres;Password=rehearsal"
docker exec ss-migration-rehearsal psql -U postgres -d sentencestudio -At -c \
  'select (select count(*) from "VocabularyWord"), (select count(*) from "VocabularyProgress"), (select count(*) from "UserProfile")'
docker rm -f -v ss-migration-rehearsal   # the copy holds production data
```

**Pass condition:** every pending migration logs `Applying migration ...` followed by
`Done.`, and the counts printed after the update equal `$BACKUP/row-counts.txt`.
This rehearses the schema only: the API's startup backfills (such as word owners) run
on the real deploy, after `MigrateAsync`, under the API's startup lock.

**Devices back up automatically.** The MAUI apps (DX24), the native iOS head, and
the native Android head copy an existing on-device SQLite database before applying
pending migrations (`SqliteMigrationBackup`). The copy sits next to the database as
`<db>.premigration-<utc-time>-<migration>.sqlite` (Android keeps it in the app's
no-backup folder, outside Auto Backup), and only the newest 2 are kept. If
the copy can't be written and verified (integrity check plus matching row counts in
every table), nothing is migrated and startup fails with a clear log entry.

**If either gate fails: DO NOT DEPLOY.** Fix the migration first. Common failures:
- Missing `[Migration]` attribute on the SQLite copy → migration silently skipped (4a catches this)
- Unsupported SQLite operation (e.g., ALTER COLUMN) → use PatchMissingColumnsAsync pattern
- Missing column/table after migration → migration failed or was silently skipped
- Sanity check failed → critical schema piece missing

**Skip these steps ONLY if:** The deploy contains NO changes to files under `src/SentenceStudio.Shared/Migrations/`.

### All checks passed? Proceed to deploy.

---

## MANDATORY Post-Deploy Verification

**A deploy is NOT complete until validation passes. `azd deploy` exit code 0 means the upload worked — not that the system works.**

### Step 5: Run the automated validation script

```bash
./scripts/post-deploy-validate.sh
```

**This script covers:**
- Phase 1: Infrastructure health (revision status, active=latest, no crash loops, DB connectivity, endpoint codes, EF migrations)
- Blazor Server circuit safety (`webapp` ingress sticky sessions must be enabled; otherwise `_blazor` WebSocket/long-polling requests can 404 across replicas)
- Phase 2: Functional smoke test (auth flow, protected endpoints, webapp renders)
- Phase 4: Regression check (core API endpoints, marketing site)

**Exit code 0 = PASS. Non-zero = FAIL — investigate immediately.**

To skip the 30-second startup wait (e.g., if you've already waited):
```bash
./scripts/post-deploy-validate.sh --skip-wait
```

To run only infrastructure checks (faster):
```bash
./scripts/post-deploy-validate.sh --phase1-only
```

To enable auth flow tests, set the test account credentials:
```bash
DEPLOY_TEST_PASSWORD="..." ./scripts/post-deploy-validate.sh
```

### Step 5a: Re-apply webapp sticky sessions (recurring — `azd deploy` resets it)

**Known drift (validated publish 2026-06-24):** `azd deploy` updates the `webapp`
Container App from the Aspire-generated manifest, which does **not** declare ingress
sticky sessions. As a result, the webapp ingress affinity is reset to `null` on **every**
deploy, and the post-deploy validator's "Blazor Server circuit affinity" check FAILs.
Because `webapp` scales to multiple replicas (`maxReplicas: 10`), a disabled affinity lets
Blazor Server `_blazor` WebSocket/long-polling requests 404 across replicas — interactive
pages (e.g. vocabulary word edit) break under load.

**Fix (idempotent, non-destructive — re-run after every `azd deploy`):**
```bash
az containerapp ingress sticky-sessions set \
  -n webapp -g rg-sstudio-prod-biz --affinity sticky

# verify
az containerapp show -n webapp -g rg-sstudio-prod-biz \
  --query "properties.configuration.ingress.stickySessions.affinity" -o tsv   # -> sticky
```
Then re-run `./scripts/post-deploy-validate.sh --skip-wait` and confirm 0 FAIL.

> **Permanent fix (TODO):** declare `stickySessions.affinity = "sticky"` on the `webapp`
> ingress in the AppHost/Bicep so `azd deploy` stops reverting it. Until then this is a
> mandatory manual step on each deploy.

### Step 6: Change-specific validation (manual)

After the automated script passes, verify the **specific change you deployed**:

1. What changed? (DB migration, new endpoint, UI change, config change?)
2. Is the change live in the running system? (Query the DB, hit the endpoint, load the page)
3. Does it behave correctly? (Expected data shape, correct UI, proper error handling)

See `docs/specs/post-deploy-validation.md` Phase 3 for common validation patterns.

**Verify a migration applied to production (recipe — validated 2026-06-24):**
The prod DB connection string lives in Key Vault; pull it and query with `psql` directly —
no need to know the admin password by heart:
```bash
cs=$(az keyvault secret show --vault-name dbkv-rsn72awybem6s \
  --name connectionstrings--sentencestudio --query value -o tsv)
host=$(echo "$cs" | grep -oE 'Host=[^;]+' | cut -d= -f2)
db=$(echo   "$cs" | grep -oE 'Database=[^;]+' | cut -d= -f2)
user=$(echo "$cs" | grep -oE 'Username=[^;]+' | cut -d= -f2)
pass=$(echo "$cs" | grep -oE 'Password=[^;]+' | cut -d= -f2)

# Was the migration recorded?
PGPASSWORD="$pass" psql "host=$host dbname=$db user=$user sslmode=require" -t \
  -c "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\" DESC LIMIT 5;"

# Do the new columns exist? (adjust table/column names)
PGPASSWORD="$pass" psql "host=$host dbname=$db user=$user sslmode=require" -t \
  -c "SELECT column_name FROM information_schema.columns WHERE table_name='ExampleSentence';"
```
> Note: a healthy API revision (active=latest, no crash loop, DB connectivity PASS) is
> already strong evidence the migration applied — EF `MigrateAsync` runs at startup, so a
> failed migration crash-loops the revision and it never becomes active. The query above is
> the definitive confirmation.

**Verify the Sam learner-report surface (only when the coach config changed):**

`Coach:Reports:Enabled` ships `true` in `src/SentenceStudio.Api/appsettings.Production.json`, so a
learner can flag a Sam response in Production. Two things to confirm after a deploy that touched
the coach configuration:

```bash
# 1. The API started. A retention sweep switched off beside an enabled switch FAILS startup by
#    design, so an active revision already proves the pair is coherent.
az containerapp revision list -g rg-sstudio-prod-biz -n api \
  --query "[?properties.active].{name:name, healthy:properties.healthState}" -o table

# 2. The reviewer path can read what the flip produces. Content-free — no learner text, no owner,
#    conversation, message, turn, or write id, no decrypted evidence.
./scripts/sam-opportunity-digest.sh --days 7
```

The operator review surface (`/operator/sam-opportunities`) is **Development-only** and stays that
way — `CoachOpportunityOptionsValidator` fails startup if it is enabled outside Development, and
`AppHost.cs` deliberately does not forward an environment variable that could turn it on. The
digest above is the production reviewer path. Owner, cadence, and credentials:
`docs/sam-opportunity-digest.md`.

### Legacy individual checks (still valid for manual spot-checking)

**DB connectivity:**
```bash
az postgres flexible-server execute \
  --name db-rsn72awybem6s --resource-group rg-sstudio-prod-biz \
  --admin-user <admin> --admin-password <password> \
  --database-name sentencestudio \
  --querytext "SELECT count(*) FROM \"Users\";"
```

**API endpoint check:**
```bash
curl -s -o /dev/null -w "%{http_code}" \
  -X POST https://api.agreeablesky-76d2f81f.westus3.azurecontainerapps.io/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"email":"x","password":"x"}'
# Expected: 400 or 401 (API is alive, DB reachable)
# Bad: 500/502/503 (API crashed or can't reach DB)
```

**EF Core migrations:**
```bash
az containerapp logs show --name api --resource-group rg-sstudio-prod-biz --tail 50 | grep -i migrat
```

**AI / Foundry keyless auth (confirm chat calls succeed, no 401s):**
```bash
az containerapp logs show --name api --resource-group rg-sstudio-prod-biz --tail 50 \
  | grep -iE "daortin-sstudio-eus2|chat/completions|401|unauthor"
# Good: POST https://daortin-sstudio-eus2.openai.azure.com/openai/deployments/gpt-5-mini/...
# Bad:  401 / Unauthorized (UAMI missing the Cognitive Services OpenAI User role)
```

**Voice mode secrets (after a deploy that includes the realtime endpoint):** start voice in
the native Conversation on DX24, then:
```bash
az containerapp logs show --name api --resource-group rg-sstudio-prod-biz --tail 100 \
  | grep -iE "realtime client secret|realtime"
# Good: "Issued realtime client secret for user ..., model gpt-realtime"
# Bad:  503 "Realtime voice is not configured" (no endpoint) or a 401/404 from Foundry
#       (UAMI role, or no gpt-realtime deployment)
```

---

## "Publish" = Azure + DX24

When the Captain says **"publish"**, **"deploy"**, or **"push to my phone"**, execute BOTH steps below. They are not optional. Both targets must point at the **same Azure API**.

---

## Step 1: Deploy to Azure

### Option A: `azd deploy` (stable)

Run this block as written. It runs in a subshell, so the production `Coach__*` values and the
feedback key never stay in your shell, and `azd deploy` runs only if both checks pass.

- **Sam (Coach) settings.** In publish mode the AppHost reads every `Coach__*` value only from
  the deploying shell's environment (`CoachConfigurationReader`), and anything missing fails
  closed: `Coach__Enabled=false`, no allowed profiles, and no Data Protection or
  durable-history settings. On 2026-11-05 a deploy without them turned Sam off for everyone. The
  block clears any `Coach__*` left in the shell, exports the API's current values without
  printing them, and compares the exported names with the live names (11 on 2026-11-05). A
  `secretRef` entry has no value to export, so the block stops. All 11 were plain values on
  2026-11-05; if one becomes a secret, update this block (and the AppHost) before deploying.
- **Feedback key.** `feedbackhmackey` is not in `.azure/sstudio-prod-biz/config.json`; azd
  reads it from `AZURE_FEEDBACKHMACKEY`, which the block reads from Key Vault.
- **GitHub App key.** Feedback issues (the Feedback page and Sam) are filed by the GitHub App
  "SentenceStudio Feedback" (client id `Iv23ctariyo54wCJKYR2`, Issues read/write on
  `davidortinau/SentenceStudio` only), as its bot. The client id is a default in the AppHost; the
  private key is Key Vault secret `github-app-private-key`, passed as `AZURE_GITHUBAPPPRIVATEKEY`.
  One-time setup (Captain): `az keyvault secret set --vault-name dbkv-rsn72awybem6s
  -n github-app-private-key --file <app>.private-key.pem`. The API mints a ten-minute app JWT,
  finds the app's installation on the repository and exchanges it for a one-hour token
  (`GitHubTokenProvider`). `githubpat` remains only as a local-development fallback.

```bash
# Ensure VPN is OFF (management.azure.com times out on VPN)
cd <checkout of the commit you are shipping>   # it needs the .azure folder; see below
(
  for name in $(env | sed -n 's/^\(Coach__[^=]*\)=.*/\1/p'); do unset "$name"; done
  COACH_QUERY="properties.template.containers[0].env[?starts_with(name,'Coach__')]"
  LIVE_NAMES=$(az containerapp show -g rg-sstudio-prod-biz -n api \
    --query "$COACH_QUERY.name" -o tsv | sort)
  eval "$(az containerapp show -g rg-sstudio-prod-biz -n api --query "$COACH_QUERY" -o json \
    | python3 -c 'import json,shlex,sys; [print("export %s=%s" % (e["name"], shlex.quote(e["value"]))) for e in json.load(sys.stdin) if "value" in e]')"
  EXPORTED_NAMES=$(env | sed -n 's/^\(Coach__[^=]*\)=.*/\1/p' | sort)
  echo "live: $(echo "$LIVE_NAMES" | grep -c .) exported: $(echo "$EXPORTED_NAMES" | grep -c .)"
  FEEDBACK_KEY="$(az keyvault secret show --vault-name dbkv-rsn72awybem6s \
    -n feedback-hmac-key --query value -o tsv)"
  GITHUB_APP_KEY="$(az keyvault secret show --vault-name dbkv-rsn72awybem6s \
    -n github-app-private-key --query value -o tsv)"
  if [ -n "$FEEDBACK_KEY" ] && [ -n "$GITHUB_APP_KEY" ] && [ -n "$LIVE_NAMES" ] && [ "$LIVE_NAMES" = "$EXPORTED_NAMES" ]; then
    AZURE_FEEDBACKHMACKEY="$FEEDBACK_KEY" AZURE_GITHUBAPPPRIVATEKEY="$GITHUB_APP_KEY" \
      azd deploy -e sstudio-prod-biz --no-prompt
  else
    echo "STOP: the feedback key, the GitHub App key or a Coach__ setting is missing; do not deploy"
  fi
)
```

After the deploy, compare the API's `Coach__*` settings with the previous revision's (names and
values) before calling the deploy done.

The allow-list names profile IDs. After the 2026-10-20 account cleanup the Captain's login
pointed at a profile that was not on the list, so Sam said "not available on this account"
until his current profile was added on 2026-11-05 (entry 3). One entry names a profile that no
longer exists; it is harmless.

Without the feedback key, `azd deploy` stops with `parameter feedbackhmackey not found`. That happened on
2026-11-05, after the marketing app had already been updated; nothing else changed. The Key
Vault value matched the API's deployed secret that day.

The azd environment (`.azure/`, git-ignored) lives in the main checkout
`/Users/davidortinau/work/SentenceStudio`. To deploy from a worktree, symlink it:
`ln -s /Users/davidortinau/work/SentenceStudio/.azure .azure`.

### Option B: `aspire deploy` (Preview)

```bash
# Ensure VPN is OFF
cd /Users/davidortinau/work/SentenceStudio
aspire deploy -e production
```

**How it works:** The AppHost declares `AddAzureContainerAppEnvironment("aca-env").WithAzdResourceNaming()` which makes `aspire deploy` target the same resources in `rg-sstudio-prod-biz` that `azd` created. Deployment state is stored in `~/.aspire/deployments/`.

**Useful flags:**
- `aspire deploy --log-level debug` — verbose output for troubleshooting
- `aspire deploy --clear-cache` — reset deployment state and reprompt for Azure config
- `aspire deploy --no-build` — skip build if you already built

**Note:** `aspire deploy` is Preview (CLI 13.3.0-preview.1). If it fails, fall back to `azd deploy`.

**Expected output:** All services succeed (api, webapp, cache, marketing, workers).  
**Webapp URL:** `https://webapp.agreeablesky-76d2f81f.westus3.azurecontainerapps.io/`  
**API URL:** `https://api.agreeablesky-76d2f81f.westus3.azurecontainerapps.io/`

---

## Step 2: Build & Deploy iOS to DX24

DX24 is Captain's iPhone 15 Pro. Device ID: `CF4F94E3-A1C9-5617-A089-9ABB0110A09F`

### 2a. SDK requirements (no swap needed)

> **As of June 2026, the repo targets `net11.0-ios` directly** — no `global.json`
> swap is required. Captain's local `global.json` already pins to a `net11.0`
> preview SDK (currently preview 4, `11.0.100-preview.4.26230.115`) with
> `rollForward: latestPatch` and `allowPrerelease: true`. The net11 preview 4 MAUI
> workload ships iOS SDK packs that support Xcode 26.3+.
>
> If a build errors with `This version of .NET for iOS (26.2.xxxx) requires Xcode 26.2`,
> look at the pack version in the error:
> - `26.2.10xxx` (no suffix) = **net10 pack** — SDK resolution fell back to net10
>   (this should not happen on the current branch; check `dotnet --version` reports
>   a `11.0.100-preview.*` SDK from this directory)
> - `26.2.11xxx-net11-pN` = **net11 preview pack**, Xcode 26.3 compatible — correct
>
> Historical note: prior to migrating the iOS head to `net11.0-ios`, this step
> required a temporary swap to `11.0.100-preview.3.26209.122`. That swap is no
> longer needed.

### 2b. Build Release with Azure API URL

```bash
services__api__https__0=https://api.agreeablesky-76d2f81f.westus3.azurecontainerapps.io \
  dotnet build src/SentenceStudio.iOS/SentenceStudio.iOS.csproj \
  -f net11.0-ios -c Release -p:RuntimeIdentifier=ios-arm64
```

> **Signing on a Mac that also holds `MaxProWildDev`** (Lilliput): automatic selection picks that
> wildcard, which lacks the app-groups entitlement (MT7140). Set `CodesignProvision` per project
> (the app to `SentenceStudioiOS`, the share extension to `SentenceStudioShareExtension`, by UUID
> when stale copies share the name) in local, git-ignored `.csproj.user` files: one command-line
> property would give both projects the same profile. See `docs/handoff-lilliput.md`.

> The committed `src/SentenceStudio.AppLib/appsettings.Production.json` already points
> `Services:api`/`Services:web` at the westus3 URLs, so Release device builds resolve the
> correct API even without the env var. Passing `services__api__https__0` at build time is
> belt-and-suspenders and matches the historical procedure — keep it.

### 2c. Install and launch on DX24

> **Before running install:** wake DX24 and confirm unlocked. **Budget for one retry** on the `devicectl install` command — first attempt frequently fails with `NWError 57` due to deep-sleep tunnel teardown, second attempt succeeds. This is a known, validated pattern (publishes #6–#9). See [`.squad/skills/maui-ios-dx24-install/SKILL.md`](../.squad/skills/maui-ios-dx24-install/SKILL.md) for the full preemptive procedure and recovery path.

```bash
xcrun devicectl device install app \
  --device CF4F94E3-A1C9-5617-A089-9ABB0110A09F \
  src/SentenceStudio.iOS/bin/Release/net11.0-ios/ios-arm64/SentenceStudio.iOS.app

xcrun devicectl device process launch \
  --device CF4F94E3-A1C9-5617-A089-9ABB0110A09F \
  com.simplyprofound.sentencestudio
```

> **Transient devicectl errors:** The first install attempt sometimes fails with
> `Network.NWError error 57 - Socket is not connected` or
> `ControlChannelConnectionError error 1`. These are **transient** — wait a few
> seconds and retry the same command. It almost always succeeds on the second try.
> Verify the device is reachable first with `xcrun devicectl list devices`
> (DX24 should show `available (paired)`).
>
> **If blind retries keep failing with `CoreDeviceError 4000` / `ControlChannelConnectionError error 1`**
> (the tunnel is invalidated on the longer `install` session while short queries still
> work), **warm the tunnel first**: run
> `xcrun devicectl device info details --device CF4F94E3-A1C9-5617-A089-9ABB0110A09F`
> (returns device info in a few seconds), then **immediately** retry `install app`
> while the tunnel is warm. Validated 2026-07-13 after four straight blind-retry
> failures — the warmed retry printed `Acquired tunnel connection to device` and
> installed on the first try. The same warm-then-retry applies to `process launch`.
>
> **`FBSOpenApplicationErrorDomain error 7 (Locked)` on launch:** The iPhone
> is screen-locked. Unlock it and either retry the launch command or just tap
> the app icon. Install already succeeded — only launch was blocked.

---

## Local Development Build (NOT for publish)

For local Aspire development only. Points at localhost, requires Aspire running.

```bash
# Debug build — uses appsettings.json (localhost:5081)
dotnet build src/SentenceStudio.iOS/SentenceStudio.iOS.csproj \
  -f net11.0-ios -c Debug -p:RuntimeIdentifier=ios-arm64
```



## Common Issues

| Problem | Fix |
|---------|-----|
| `azd deploy` times out | Turn off VPN, retry |
| `azd deploy` targets wrong stack | Pass `-e sstudio-prod-biz` (the new business-tenant env); confirm `azd env list` shows it as DEFAULT |
| `azd provision` fails on Flexible Server | Check subscription quota AND offer restrictions (`LocationIsOfferRestricted`) for PostgreSQL Flexible Servers in the region — westus3 is the known-open region for this sub |
| `aspire deploy` creates wrong RG | Check `~/.aspire/deployments/.../production.json` has `rg-sstudio-prod-biz` |
| `aspire deploy` principal error | Ensure AppHost has `AddAzureContainerAppEnvironment` |
| `aspire deploy` stale state | Run `aspire deploy --clear-cache` to reset |
| `Speech cache: read failed` / `write failed` warnings (Status=403 `AuthorizationPermissionMismatch`, or 404 `ContainerNotFound`) | Speech still works; nothing is cached. Run the read-only checks under "Speech clip cache" and apply the missing role or container |
| AI calls 401 / Unauthorized | UAMI `mi-rsn72awybem6s` missing **Cognitive Services OpenAI User** on `daortin-sstudio-eus2`; or role assignment hasn't propagated yet (wait 5–10 min) |
| Xcode version mismatch (26.2 vs 26.3) | Verify `dotnet --version` reports a `11.0.100-preview.*` SDK from this directory and that the pack version in the error is `26.2.11*-net11-pN`, NOT `26.2.10*` |
| Build keeps picking net10 iOS pack | Confirm iOS csproj `<TargetFramework>` is `net11.0-ios`; confirm `dotnet --version` reports `11.0.100-preview.*`; delete `obj/` under `src/SentenceStudio.iOS/` and rebuild |
| `devicectl` install fails with `Socket is not connected` | Transient — retry once after a few seconds. Check `xcrun devicectl list devices` shows DX24 `available (paired)` |
| `devicectl` install keeps failing with `CoreDeviceError 4000` / tunnel invalidated | Warm the tunnel: run `xcrun devicectl device info details --device <id>`, then immediately retry `install`. See Step 2c |
| Device locked error on install | Unlock DX24, retry |
| LOCAL ribbon on phone | Built with Debug config — rebuild with Release + env var (step 2b) |
| Phone app can't reach API | Missing `services__api__https__0` env var at build time, or stale `appsettings.Production.json` URL |
| EF Core can't connect to Flexible Server | Check firewall rules allow ACA subnet; verify connection string in Aspire dashboard |
| Mixed azd + aspire deploy | **NEVER do this.** See "NEVER mix deploy tools" warning above. |

---

## Legacy: old personal-tenant stack (pending decommission)

> The original production stack lives in `rg-sstudio-prod` (personal tenant, centralus,
> ACA domain `livelyforest-b32e7d63`, resource token `3ovvqiybthkb6`). It is **retained as a
> rollback path** after the 2026-06 migration to `rg-sstudio-prod-biz` and is no longer the
> deploy target. When confident in the new stack, decommission it: take a final `pg_dump`,
> remove `CanNotDelete` locks, delete the RG. Also remove the temporary migration grants
> (firewall rule `migrate-client` on both old and new Postgres servers; Key Vault Secrets
> User grant on the old `dbkv-3ovvqiybthkb6`).
>
> Separately, the old containerized `db` app + Azure File share (`vol3ovvqiybthkb6`) from
> before the Flexible Server migration may still exist there with `CanNotDelete` locks;
> they go away when that RG is deleted.
