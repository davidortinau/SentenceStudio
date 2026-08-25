# Local dev database volumes

Every worktree would get its own empty PostgreSQL volume by default, because Aspire derives the
volume name from the AppHost path. That isolation is the right instinct — one worktree can never
corrupt another's data — but it means a fresh worktree starts with no vocabulary, no plans, and no
history, which makes anything data-shaped (Today's Plan, progress, Learning Coach) untestable until
you rebuild a world by hand. Worse, when it happens to a worktree that *did* have data, it looks
identical to success.

So this repo does not take that default silently. `LocalDb:DataVolume` is required and the AppHost
refuses to start without it (see below).

The fix is to clone an established local database into a **new** volume and point one worktree at
the clone. The original is never touched.

## The AppHost refuses to guess

`LocalDb:DataVolume` is **required**. If it is unset, the AppHost throws at startup instead of
falling back to the auto-named volume.

That refusal exists because the fallback was a *plausible* failure, which is the worst kind. The
stack came up clean, `https://localhost:7071` answered `200`, the dashboard rendered — and the
database behind it was empty or belonged to an unrelated lineage. Nothing logged an error. The
first symptom was a human being told their account did not exist.

**Earning event — 2026-08-20.** Captain's `dave@ortinau.com` environment disappeared from
`https://localhost:7071` for the Nth time. An agent had relaunched the AppHost as:

```bash
Coach__SamWriteTools__Enabled=true nohup dotnet run --no-build --no-restore
```

No `LocalDb__DataVolume`. Aspire mounted `sentencestudio.apphost-461cee1bad-db-data` (agent
lineage, **zero** `dave@ortinau.com` rows) while `sentencestudio-local-crispy-barnacle-db-data`
(Captain's real data — 4,417 vocabulary words, 35 plans, 29 activity sessions) sat unmounted at
`LINKS 0`. Because the postgres container is `ContainerLifetime.Persistent`, that wrong container
then survived **42 hours** and every later restart silently reused it.

An unbootable stack is a five-second fix. A stack running on the wrong database costs a testing
session and the trust that testing means anything.

If you genuinely want a throwaway empty database, say so out loud:

```bash
LocalDb__AllowEphemeralVolume=true aspire run
```

## Making the choice durable

Passing `LocalDb__DataVolume=...` on one command line only protects **that** command. The next
restart — yours, an agent's, a `dotnet run` from a different shell — forgets it. Persist it once
per machine in user-secrets, which is per-developer and **never committed**:

```bash
dotnet user-secrets set "LocalDb:DataVolume" "sentencestudio-local-<worktree>-db-data" \
  --project src/SentenceStudio.AppHost
```

Now every `aspire run` and every `dotnet run` in this worktree lands on the same database with no
prefix to forget. Configuration precedence still lets a single run override it, because
environment variables are read after user-secrets:

```bash
LocalDb__DataVolume=<other-volume> aspire run   # one run only; the secret is unchanged
```

This is also why the volume name is **not** committed to the repository: user-secrets is the right
home for a machine-specific volume name, exactly as it already is for `Parameters:dbPassword`.

### Verifying you are on the right database

```bash
scripts/validate-local-db-volume.sh                     # static: the guard is still in AppHost.cs
scripts/validate-local-db-volume.sh --runtime \
  --expect-account dave@ortinau.com                     # runtime: live container mounts the right volume
```

The runtime check reads the expected volume from `LocalDb__DataVolume` or user-secrets when you do
not pass `--expect-volume`, and it **fails on any `*.apphost-*-db-data` name** — an auto-named
volume is by itself the signature of this bug. Set `PGPASSWORD` to enable `--expect-account`.
Both modes are read-only: `docker inspect` and `SELECT` only.

Guard tests: `scripts/validate-local-db-volume.test.sh` (stubs `docker` and `dotnet`, safe to run
while a stack is live).

## Agent and E2E runs must not touch the human's stack

An agent verifying a change must never point at the volume a human is testing against, and must
never take over the endpoint that human has open.

- **Never** run an agent stack on Captain's `https://localhost:7071`. That port is bound by
  whichever AppHost starts first; a second `aspire run` from this worktree seizes it.
- **Clone first, run on the clone.** `scripts/clone-local-db-volume.sh` refuses to overwrite an
  existing destination, so the human's volume cannot be reused by accident.
- Migrations apply on API startup. Pointing *any* stack at a volume mutates its schema, so
  "read-only testing" against a shared volume does not exist.
- **Same-path ownership hazard (2026-08-22 earning event):** AppHost ownership is keyed by
  project path, not by `--isolated` flag or volume name. An `aspire run` or
  `aspire start --isolated` from the SAME AppHost path as a running stack stops that stack
  and reuses DCP-managed resource/container identity. A cloned DB volume alone does NOT
  provide isolation. You MUST run from a separately materialized project path (different
  worktree or full clone). Do NOT create that worktree from inside an already worktree-backed
  Copilot session; it must be supplied by the session/workspace system or materialized manually
  outside the live path.

### Safe agent E2E sequence

**Prerequisites:** A separate project path exists (worktree or clone) that is NOT this path.

```bash
# 1. Clone the volume (can run from anywhere)
scripts/clone-local-db-volume.sh \
  --source sentencestudio-local-<worktree>-db-data \
  --destination sentencestudio-agent-<task>-db-data \
  --database sentencestudio --username <db-user> --verify --verify-table '"UserProfile"'

# 2. Configure the SEPARATE path's user secrets
dotnet user-secrets set "LocalDb:DataVolume" "sentencestudio-agent-<task>-db-data" \
  --project <SEPARATE_PATH>/src/SentenceStudio.AppHost

# 3. Start from the SEPARATE path
cd <SEPARATE_PATH>/src/SentenceStudio.AppHost
ASPNETCORE_URLS='https://localhost:7171' aspire run

# 4. Mandatory read-only post-recovery gate (proves correct volume + healthy services)
scripts/post-aspire-restore.sh --expected-volume sentencestudio-agent-<task>-db-data
```

```
UNSAFE - DO NOT RUN from the same path as a live AppHost:
LocalDb__DataVolume=sentencestudio-agent-<task>-db-data aspire run
aspire run --isolated
aspire start --isolated
```

Before reporting any E2E result, prove which database you were on:

```bash
scripts/validate-local-db-volume.sh --runtime --expect-volume sentencestudio-agent-<task>-db-data
```

A pass against the wrong database is not a pass.

## One-command workflow

```bash
# 1. Clone an established local volume into a new, worktree-specific one.
scripts/clone-local-db-volume.sh \
  --source <established-volume> \
  --destination sentencestudio-local-<worktree>-db-data \
  --database sentencestudio --username <db-user> \
  --verify --verify-table '"UserProfile"'

# 2. Run this worktree's stack against the clone.
IncludeMobileTargets=false \
LocalDb__DataVolume=sentencestudio-local-<worktree>-db-data \
aspire run
```

`PGPASSWORD` must be set for `--verify` and for `--mode dump`. It is the AppHost's `dbPassword`
parameter:

```bash
export PGPASSWORD=$(dotnet user-secrets list --project src/SentenceStudio.AppHost \
  | awk -F' = ' '/^Parameters:dbPassword/{print $2}')
```

List candidate sources with `docker volume ls | grep sentencestudio`. Pick one deliberately; the
script never guesses, and no source volume name is committed to this repository.

## `LocalDb:DataVolume`

`AppHost.cs` reads `LocalDb:DataVolume` (environment form `LocalDb__DataVolume`) and passes it to
`WithDataVolume(name)`. Leave it unset and the AppHost **throws** rather than falling back to a
generated per-worktree volume; set `LocalDb:AllowEphemeralVolume=true` to opt into that fallback
deliberately. Set the name and this worktree uses that named volume.

The AppHost only *names* a volume. It never creates, copies, migrates, or removes one. Only name a
volume you are willing to let a dev stack write to; the API applies EF migrations on startup.

## What the clone script will not do

These are refusals, not warnings. The script exits non-zero and changes nothing:

- The destination already exists. It is never reused, merged into, overwritten, or removed.
- The source does not exist, or source and destination are the same name.
- `--mode copy` (or `auto` resolving to copy) while a running container has the source mounted.
  Copying a live PostgreSQL data directory can capture a torn page. Stop the container, or use
  `--mode dump` for a logical copy from the running server.
- `--mode dump` with nothing running on the source.

On failure, a partially written destination is **left in place** and the exact `docker volume rm`
command is printed. Removing a volume is always the operator's decision, never the script's.

The source volume is only ever mounted read-only (`copy`) or read through a running server
(`dump`). Nothing is dropped, truncated, deleted, or reset in either volume.

Guard tests: `scripts/clone-local-db-volume.test.sh` — it stubs `docker`, so it is safe to run
while a stack is live.

## Modes

| Mode | When to use | How it reads the source |
|---|---|---|
| `auto` (default) | Normal use | Picks `dump` if the source is in use, otherwise `copy` |
| `copy` | Source container is stopped | File-level copy, source mounted read-only |
| `dump` | Source stack is running and you cannot stop it | `pg_dumpall` from the running container, restored into a fresh cluster |

Keep `--image` matched to the tag the AppHost pins (`postgres:17`). PostgreSQL 18 changed the
on-disk layout, and a volume written by one major version is not readable by the other.

## After the stack is up

The Learning Coach reports **unavailable until the profile has a Today's Plan**, even when the
feature flag and cohort are both correct — the coach edits Today's Plan, so with no plan there is
nothing to edit. A cloned database carries plans from the day it was captured, not today. Sign in
as the profile and open the dashboard to generate Today's Plan, then the coach entry point appears.

For the Coach feature flags themselves, see the `Coach__*` variables in `AppHost.cs`.

## Part-of-speech backfill

Vocabulary rows written before the `PartOfSpeech` column existed stay null forever unless something
classifies them. The workers host can do that once, for named profiles only:

```bash
IncludeMobileTargets=false \
LocalDb__DataVolume=<your-clone> \
VocabularyPartOfSpeechBackfill__Enabled=true \
VocabularyPartOfSpeechBackfill__UserProfileIds__0=<user_profile_id> \
aspire run --detach
```

Optional: `VocabularyPartOfSpeechBackfill__BatchSize` (default 40, max 100) and
`VocabularyPartOfSpeechBackfill__MaxWords` (default 500 per run).

The AppHost forwards these to **workers only** — never to the API. Defaults are off with an empty
allowlist, and an enabled backfill with no allowlist refuses to run rather than treating "no filter"
as "every tenant". It classifies only words the profile owns, writes only `PartOfSpeech` and only
where it is currently null, and is idempotent: re-running a converged profile costs nothing and
makes no model call. Restart without the variables when the run is done.

Take a backup clone first. The backfill is additive and never overwrites, but a clone costs seconds
and makes the whole operation reversible.

## Azure Storage (Azurite) volume

The Azurite blob emulator uses a **shared stable volume** across all worktrees:
`sentencestudio-local-azurite-data`. Unlike Postgres (per-worktree), storage is shared because the
ASP.NET Data Protection key ring lives there (`coach-dataprotection/keys.xml`). If this volume is
lost, all locally-encrypted Coach conversation payloads become permanently unreadable.

The emulator is also configured with `ContainerLifetime.Persistent`, so it survives AppHost
restarts without being recreated. The validation script `scripts/validate-azurite-persistence.sh`
enforces both the named volume and persistent lifetime statically.

```bash
docker volume ls | grep azurite   # should show sentencestudio-local-azurite-data
```

## Related

- `docs/local-dev-test-accounts.md` — seeded local-only accounts
- `docs/deploy-runbook.md` — production deploys, which never use these volumes
