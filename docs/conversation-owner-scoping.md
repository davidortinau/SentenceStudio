# Owner scoping for the legacy Conversation activity

## The defect

`Conversation` and `ConversationChunk` — the tables behind the legacy
Conversation activity — had **no owner column at all**. Every query in
`ConversationService` was therefore unfiltered:

```csharp
// before
return await db.Conversations.Include(c => c.Chunks)
    .OrderByDescending(c => c.CreatedAt).ToListAsync();
```

On a single-user device that reads as "my conversations". On the multi-tenant
Blazor WebApp — where one process serves every signed-in learner — it reads as
"everybody's conversations", and `ResumeConversation()` would hand whichever
transcript happened to sort first to whoever asked. Both entities are also
CoreSync-registered, so rows travel between the device store and the server.

## What changed

### 1. Nullable owner columns

`Conversation.UserProfileId` and `ConversationChunk.UserProfileId` are both
`string?`.

The chunk column is deliberately denormalized rather than inferred through the
parent FK: CoreSync ships each table independently, so a chunk can land in a
store before (or without) its conversation. A join-only ownership check would
have nothing to join against in that window, and "no parent yet" would have to
resolve to either "hide it" or "show it to everyone" — the second of which is
the bug being fixed. Storing the owner on the row keeps the answer local.

**Existing rows stay null.** There is no trustworthy signal for who wrote them,
and a backfill would be a guess that hands one learner's transcript to another.
Ownerless rows are simply invisible to every user.

## Why ownerless native rows are never auto-assigned

The tempting shortcut is: a device store holds exactly one person's data, so
stamp every ownerless row with whoever is signed in now. It is wrong, and the
reason is account switching.

A native store is not per-account. It is per-installation, and the same
installation has hosted whoever signed in on it: a shared iPad, a device handed
to a family member, a demo account signed in once, an account deleted and
replaced by a new one for the same human. The rows predate the owner column
precisely because they predate the point at which the app started recording who
was writing. So "whoever is signed in now" is not the author of those rows — it
is only the most recent author, and there is no stored fact that distinguishes
the two cases.

Assigning on that basis fails in exactly the direction that matters:

- The rows become **visible and editable** by an account that may not have
  written them. A transcript of somebody else's practice conversation appears in
  a stranger's history as their own.
- The mistake is **not detectable afterwards**. Once stamped, an assigned row is
  indistinguishable from one written under that account, so the error cannot be
  found later, reported, or reversed.
- It is **silent**. Nobody is asked, nothing is logged as a decision, and the
  first symptom is a learner reading a conversation they did not have.

Leaving the rows ownerless fails in the opposite direction, and that direction
is recoverable: the rows are inaccessible, the learner sees a shorter history
than they expected, they can say so, and the data is still there to be restored
once somebody can establish who it belongs to.

So the rule is:

- **Preserve.** Ownerless rows are never deleted, and no cleanup job removes
  them. The migration is an additive `AddColumn` with no `Sql(` and no backfill.
- **Do not retag.** Nothing writes an owner onto a row that has none. Not at
  sign-in, not at sync, not at first read.
- **Keep inaccessible.** The repository is the enforcement boundary and filters
  by owner, so a null owner matches nobody. That is the intended state, not a
  gap waiting to be closed.
- **Recover explicitly or not at all.** If a learner reports missing history,
  recovery is a deliberate, per-case operation with the learner's confirmation —
  the same standard `DataRecoveryService` is held to after the cross-tenant
  retag incident, where automatic recovery assigned one person's data to another
  and had to be gated behind an email-match check, a temporal sanity check, a
  one-shot flag, and a default-off preference.

This is the same trade the null owner columns make in the first place: prefer a
learner missing data they can ask for over a learner receiving data that was
never theirs.

### 2. The repository is the enforcement boundary

`SentenceStudio.Shared/Data/ConversationRepository.cs` owns all persistence.
`ConversationService` (AppLib) is now a thin delegate and contains no queries.

Owner resolution uses trusted sources only, never a caller-supplied id:

1. `IUserScopeProvider` when the host registers one (API request scope, MAUI
   device session).
2. Otherwise the claim-derived `active_profile_id` preference, which is how the
   WebApp's `WebPreferencesService` exposes the authenticated circuit user.

Rules the repository enforces:

| Situation | Behavior |
|---|---|
| No resolvable owner | Reads return empty/null, writes and deletes refuse, a warning is logged naming the operation only |
| Read by id owned by another account | `null` / empty — an id is not an authorization |
| Update targeting another account's row | Refused |
| Update targeting an ownerless legacy row | Refused — an update is not a claim mechanism |
| Chunk write whose parent is not owned | Refused |
| Delete of a row not owned | Refused |
| Insert | Owner stamped from the trusted scope |

Warnings name the operation and the reason. They never contain a user id or any
conversation text.

### 3. Account export / deletion

`IConversationOwnerDataService` (in Shared, registered against
`ConversationRepository`) exposes:

- `ExportOwnedAsync(userProfileId)` — that user's conversations and chunks.
- `DeleteOwnedAsync(userProfileId)` — deletes only rows carrying that owner.
- `GetUnownedDiagnosticsAsync()` — **counts only**, for operator diagnostics.

Both id-taking methods refuse an empty id rather than running unfiltered.
Ownerless legacy rows are never exported (they are not provably this user's
data) and never deleted by an account-deletion path (deleting them would
destroy data whose owner is merely unknown, not absent).

This is exposed as an interface specifically so the auth/account layer
(`AuthEndpoints.cs`) can consume it without this change having to edit it.
Wiring the call into account deletion is a follow-up for that file's owner.

### 4. Migration

`20260817021500_AddConversationOwnerScope`, dual-provider:

- `Migrations/` (PostgreSQL, `type: "text"`)
- `Migrations/Sqlite/` (SQLite, `type: "TEXT"`)

Both carry `[DbContext(typeof(ApplicationDbContext))]` + `[Migration(...)]` —
without them EF never discovers the migration and silently skips it on mobile,
which has shipped broken twice. `Up` is nullable `AddColumn` plus four indexes;
`Down` drops them. No `Sql(`, no backfill, no `AlterColumn`, no `DropTable`.

## Known limitation: CoreSync is not owner-filtered

**This repository applies no CoreSync table filters to any of its 18 synced
tables.** `SharedSyncRegistration` registers each table with a sync direction
only, and `SyncService.TriggerSyncAsync` calls `SynchronizeAsync()` with no
`SyncFilterParameter[]`. This predates the Conversation defect and is
cross-cutting: adding filters would change behavior for every synced table and
touch `SharedSyncRegistration`, `SyncService`, and the API's sync provider
registration — all outside this change's ownership.

What this change does guarantee, and what it does not:

- **Does:** the owner value is part of the synced schema and survives a sync
  round-trip in both directions; a null legacy owner stays null and is never
  materialized into an account (covered by
  `ConversationSyncOwnerFidelityTests`).
- **Does:** the WebApp — the actual multi-tenant surface — uses
  `NoOpSyncService`, so the repository closes that leak completely.
- **Does not:** stop a device from downloading remote rows it does not own.
  That is the pre-existing repo-wide sync posture. On device, the repository is
  the enforcement boundary: downloaded rows owned by someone else are filtered
  out of every read.

CoreSync 0.1.129 *is* capable of filtering (`SyncFilterParameter`,
`SyncTable.selectIncrementalQuery`, `customSnapshotQuery`,
`SyncConfiguration.ResolveTableFilter`), so this is a wiring decision, not a
library limitation. It should be tracked as its own piece of work covering all
synced tables.

## Tests

- `tests/SentenceStudio.UnitTests/Data/ConversationOwnerScopingTests.cs` —
  empty scope, owner stamping, read/update/delete isolation, same id across two
  accounts, legacy rows hidden and never claimed, export/delete contributor,
  unowned diagnostics, log hygiene, scope-provider precedence.
- `tests/SentenceStudio.UnitTests/Data/ConversationOwnerScopeMigrationTests.cs`
  — migration discovery attributes, add-column-only and reversible, provider
  column types, model snapshots, CoreSync column presence and round-trip
  fidelity.

## Verification limitations

- `scripts/validate-migration-attributes.sh` passes.
- `scripts/validate-mobile-migrations.sh` (real native-head SQLite apply) could
  **not** be run: the `macos` workload is not installed in this environment
  (`NETSDK1147`), so no MAUI head can be built here. The mobile apply must be
  confirmed on a machine with the workload before shipping.
- All builds here use `-p:IncludeMobileTargets=false` for the same reason.
