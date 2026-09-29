# PostgreSQL startup-fence fault proof: safety protocol and local evidence

**This is not an executable runbook.** A future run requires independent
prewrite safety approval of a new, session-specific harness and fresh
disposable storage. Do not copy the former interactive Bash transcript: it
omitted the pinned Docker daemon/socket and private child-environment
allowlist. The independently approved private runner was bound to a specific
evidence root and two *new* volumes; those volumes are now used and stopped.
Neither its approval nor its successful execution authorizes a rerun, reuse of
that evidence root, or an improvised replacement.

No step may target production, an existing application or user database, a
pre-existing/unknown volume or container, or an unapproved Docker endpoint.
PostgreSQL's initialization of a **new** disposable PGDATA volume is the only
bootstrap write allowed before fixture attestation. There is **no operator
SQL at any point**: no `psql`, manual marker, `template0` connection or
connectability change, manual schema preparation, or external test data.

## Fail-closed order for a future, separately reviewed session

1. **Bind an isolated target before any database connection.** Require an
   explicit opt-in, an exact known scenario, a never-reused lowercase v4 run
   ID, a new private evidence directory, and the corresponding new
   `sfence_<run-id>` cluster/container and
   `sfence_pgdata_<run-id>` local named volume. Reject already existing names,
   reused result directories, unknown targets, name races, or incomplete
   ownership evidence; a successful volume-create response alone is not
   proof of freshness. Track every full 64-character container ID as soon as
   it is created. The container **and** volume must have matching
   `com.sentencestudio.startup-fence.{run-id,cluster-name,volume-name,purpose}`
   labels, with purpose `isolated-fault-test`.
2. **Pin Docker and isolate every child.** Resolve and validate the local
   Docker context's canonical Unix socket; pin the socket and daemon ID in
   explicit `DOCKER_HOST` and `STARTUP_FENCE_DOCKER_DAEMON_ID` values. Use a
   private Docker config and explicit host/config for Docker operations.
   Recheck the same socket and daemon around Docker reads, each test child,
   and exact-ID cleanup; abort on drift rather than stopping a container on
   another daemon. Spawn `dotnet test` only through the reviewed allowlisted
   child environment: pass required SDK/Docker settings and **only** the
   current fact's exact opt-in/target variables, not ambient PostgreSQL,
   Azure, telemetry, API credentials, or unrelated test flags. Keep generated
   scratch credentials out of arguments, stdout, source, `.env`, and Squad
   state. Never print unfiltered Docker inspect output or share raw TRXs/logs.
3. **Prove the target using Docker only, with zero PostgreSQL SQL.** Each
   fresh scenario must independently pass the exact Docker-only preflight
   **1/1 passed, 0 failed, 0 skipped** before its writer fact. Attest the
   reviewed `postgres:17` image ID
   `sha256:0027bef26712baaee437a4ea48fdf3d2d2e2bc5f0d81615374408ca320f3c7e3`
   and `PG_VERSION=17.10-1.pgdg13+1` (a changed image needs renewed
   review); local context, pinned daemon and socket; exact full container
   ID, labels and `cluster_name`; volume freshness, labels and sole
   consumer; sole named PGDATA mount; running, never-restarted container;
   loopback-only nondefault published port; and read-only `pg_controldata`
   identity/state. Any mismatch, missing TRX, skip, timeout, or unexpected
   process exit fails closed **before SQL**.
4. **Let the fixture perform the first SQL as a read-only census.** On the
   attested scratch database, `postgres` and `template1`, inspect all
   objects/schemas, including `pg_catalog`, `pg_toast` and
   `information_schema`, against the reviewed pristine PostgreSQL catalog
   fingerprint and reject unknown objects or unrelated sessions. For
   `template0`, inspect **only** `pg_database` metadata and require
   `datallowconn=false`; it stays non-connectable and its contents are not
   inventoried. Only after Docker attestation **and** this census may the
   fixture create and verify its scratch owner marker. The fixture then
   installs server-side write/audit gates before either application replica
   starts. The operator never creates the marker or supplies SQL.
5. **Run each selected writer fact on its own fresh volume.** Require the
   lock-owning startup session to perform real application DDL/row writes,
   not a simulation or client-exception timing check. Stall replica A inside
   an actual server-side write gate, kill A's lock-owning backend, then let
   waiting replica B acquire the lock and complete. Server-side audit order
   (monotonic event ID plus transaction ID), backend PID **and** backend
   start time must establish zero A writes after takeover, zero B writes
   before takeover, and zero B startup writes from a wrong backend. Verify
   committed scenario records/migration history. The application startup
   work uses one lock-owned session; runtime DI resolves a **separate**
   backend that issues SELECT-only application SQL. Its connection causes a
   persistent `ON login` audit INSERT: assert the writer audit first, then
   account for this login-only backend in the whole-run audit. Coach,
   Feedback and DevSeed may legitimately show A writes **before** takeover.
   Each writer fact requires its own exact **1/1, no-skips** TRX.
6. **Keep A/B clusters running for cross-service facts, then stop by ID.**
   For identity and Worker/WebApp refusal, use two independent, freshly
   attested, unrestarted clusters whose `baseline` and `phrase` writer
   preflight/fault facts have passed. Enforce the Docker volume `CreatedAt`
   age windows (<3300 seconds before and <3600 after each cross-service
   fact). Each fact reattests **both** exact clusters before SQL, validates
   their fixture-owned markers and persisted migrations, and requires an
   exact **1/1, no-skips** TRX. Identity, Worker and WebApp inspections open
   connections: `ON login` inserts audit rows even where their subsequent
   application SQL is SELECT-only/session read-only. Those rows are not
   startup writes. On any failure, do not restart or retry these volumes:
   stop **all** still-running, exact session-tracked IDs on the pinned
   daemon, confirm stopped state, and retain volumes/evidence. After
   success, clear opt-ins/connection variables, stop both exact IDs, and
   retain the stopped containers, volumes and private evidence. Never
   remove a volume to make another attempt possible.

The five **independent** writer scenarios (one new Docker preflight and one
writer fact per new cluster) are:

| Scenario | Lock-owned writes actually gated |
|---|---|
| `baseline` | Application migration DDL, first number inserts and reseed, lexical backfill, CoreSync provisioning. |
| `phrase` | Migration DDL and phrase-constituent backfill from fixture-seeded `VocabularyProgress` for an explicit user. |
| `coach` | First Coach migration DDL on a fresh cluster. |
| `feedback` | First Feedback migration DDL on a fresh cluster. |
| `devseed` | Development-enabled built-in Identity account/profile inserts. |

The approved session selected **one** fact per exact `FullyQualifiedName`
filter, in this order (identifiers for review, **not** executable commands):

| Fact | Exact filter used by the reviewed harness |
|---|---|
| Docker-only preflight per cluster | `FullyQualifiedName=SentenceStudio.Api.Tests.Startup.StartupFenceDockerAttestorTests.Docker_only_preflight_attests_the_exact_target_without_postgresql_sql` |
| Writer fault per cluster | `FullyQualifiedName=SentenceStudio.Api.Tests.Startup.StartupReplicaFencePostgresTests.A_dead_lock_backend_cannot_write_after_the_waiting_replica_takes_over` |
| A/B identity after both fault facts | `FullyQualifiedName=SentenceStudio.Api.Tests.Startup.PostgresDatabaseIdentityLiveTests.Independently_attested_startup_fence_clusters_have_distinct_identities` |
| Worker A-vs-B refusal | `FullyQualifiedName=SentenceStudio.UnitTests.Workers.WorkerApiReadinessGateLiveTests.Controlled_HTTP_API_A_observed_identity_cannot_release_Worker_on_attested_database_B` |
| WebApp A-vs-B refusal | `FullyQualifiedName=SentenceStudio.WebApp.Tests.Startup.ApiMigrationReadinessGateLiveTests.Controlled_HTTP_API_A_observed_identity_cannot_release_WebApp_on_attested_database_B` |

Future harness review must pin these exact selectors **and** test
arguments, verify their assertions still enforce the same contract, and
require one **1/1 passed, 0 failed, 0 skipped** TRX per invocation.
Disallow broad selectors or a skip counted as success.
Require fresh opt-in/target variables separately for writer, identity, and
cross-service facts. Do not substitute the old interactive transcript or
run the historical session-private harness against its existing evidence
directory. If the prerequisite attestation, SQL ordering or allowlist cannot
be preserved, **stop and request a new independent safety review**; do not
weaken a check or add unreviewed operator SQL.

## Cross-service identity: scope and limitations

API startup contract **v3** exposes a SHA-256 equality token derived from
the **observed** PostgreSQL server identifier, database/schema provenance
and persisted migration history. Worker and WebApp independently compute
identity through their own EF/Npgsql connections and refuse readiness when
their observed identity differs from the API contract; the local mismatch
facts require **zero readiness releases** and no raw connection detail in
the rejection. Distinct initialization does not by itself guarantee
distinct system identifiers: each live A/B fact asserts distinct observed
identifiers and fingerprints before claiming mismatch.

The local gate facts use a controlled loopback HTTP **stand-in** for A's
observed API-version identity, with real Worker/WebApp EF connections to B.
They do **not** launch a real API, Worker or WebApp host and are not
production proof. Live A/B database names differ; same-name mismatches
have separate **synthetic** regression coverage, not a same-name live
cluster proof. A physical clone can preserve the control-file ID, database
OID, schema/owner marker and migration history; a hidden backend proxy
switch with no client-visible identity change may also evade this token.
Access by the intended **non-superuser production roles** to
`pg_control_system()` and persisted migration history remains unverified.
Never grant privileges or connect to production/user data as a shortcut
to checking this limitation.

## Independently reviewed local evidence (September 28, 2026 UTC)

- **Earlier, separate writer pass, 00:35–00:36 UTC:** five Docker-only
  preflights, five scenario writer facts (`baseline`, `phrase`, `coach`,
  `feedback`, `devseed`), and one A/B identity fact each reported
  **1/1 passed, 0 skipped** on fresh disposable clusters. Their volumes
  were stopped and retained. This did **not** include live Worker/WebApp
  refusal facts.
- **New two-cluster cross-service pass, 02:22:38–02:24:09 UTC:** new
  `baseline` run `7031b74991a34d3c90e201109493036e` and `phrase` run
  `74a93f2696274df899cf991569642146` produced **seven** passing,
  non-skipped TRXs: 2 Docker-only preflights + 2 fault proofs + 1 identity
  + 1 Worker + 1 WebApp. Server-side B takeover/write event sequences
  were baseline **28/826/999/1171** and phrase **28/837**; B writes
  after takeover were positive and A post-takeover writes were zero.
  Both cross-service facts refused A-versus-B identity with zero
  readiness releases. **Coach, Feedback and DevSeed were not rerun in
  this two-volume pass.**
- Rai independently **APPROVED the post-run result** after checking the
  seven TRXs, server audit, runner/helper review identity and projected
  Docker state. Both exact containers were stopped:
  `8115b58cadf1779844e0b27835baaa9920ccf63bcf9b3d69eccc1a527c946454`
  (`baseline`) and
  `8c83199f6031d4eba16dbeef61e455defc42c72a64ce5c9b72a13ef8e2db101c`
  (`phrase`); both named volumes were **retained**, not deleted or reused.
  The reviewed private runner/helper hashes were respectively
  `abcae95d92f35e44f3a15dc13e8a50ebb4a3449b913569c6fd8b59e0de34f5df`
  and `5ed38794fa401159b61a8558377fb77ee818e5287c68f01a1cc477f3d0fe7734`.
- **Post-run offline checks, 02:25–02:34 UTC:** targeted API startup/seeder
  tests passed **229/232** (3 opt-in skips); Worker readiness tests passed
  **52/53** (1 opt-in skip); WebApp startup tests passed **28/29** (1 opt-in
  skip). Sequential API/Worker/WebApp builds finished with **0 errors**
  (40/40/503 warnings respectively). The static migration-attribute gate
  passed at 02:34:14 UTC; private SQLite EF-discovery/application on two
  scratch databases passed at 02:34:43 UTC without launching a native app.
  The full native-app migration mode was **not** run.

The local evidence lives at
`/Users/davidortinau/.copilot/session-state/588e0b81-01aa-497b-8dca-491b234ef8b6/files/cycle2-cross-service-evidence/`.
It is **private**, not a deployable artifact or future runner input.
Do not publish its raw logs, TRXs, credentials or unfiltered Docker metadata.
No production database, production role or real service host was exercised
by these local cross-service facts.
