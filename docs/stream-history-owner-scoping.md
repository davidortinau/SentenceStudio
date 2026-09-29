# StreamHistory owner scoping

`StreamHistory` stores local audio-history metadata. Its nullable `UserProfileId`
and owner-first `(UserProfileId, CreatedAt)` index were introduced by paired EF
migrations `20260927100455_AddStreamHistoryOwnerScope` for PostgreSQL and SQLite.
The column remains null for existing rows when the migration applies; neither
migration assigns an owner or changes audio files.
There is no database foreign key: [SQLite needs a table
rebuild](https://learn.microsoft.com/en-us/ef/core/providers/sqlite/limitations)
to add one to an existing table, and cached audio history must not cascade
away with a profile. The repository verifies that the active profile exists
before use.

`StreamHistoryRepository` uses the active claim-backed user scope on API
requests, the authenticated `active_profile_id` preference in WebApp circuits,
and the login-selected `active_profile_id` on native devices. It refuses missing
or conflicting identities and active identities without a `UserProfile` row.
All list, exact-ID, phrase, voice, search, update, and delete paths filter by
owner. New entries receive their owner from this trusted context rather than
from a caller-supplied value. Ownerless rows cannot be read or deleted through
the repository.

Neither native SQLite nor server PostgreSQL automatically adopts a legacy
null-owner row. The pre-scoping `StreamHistory` schema contains no owner ID,
foreign key to an owned entity, or other per-row provenance. AudioCache paths
are generated from a phrase or audio type plus a GUID, not a profile ID.
The active preference, the number of surviving profiles, and profile/recording
timestamps cannot prove who recorded a row: `UserProfileRepository.DeleteAsync`
can remove an earlier learner's profile while leaving their audio history;
a different learner can create a profile on the same day. Even when only one
profile currently exists, assigning it legacy audio could disclose another
learner's recording. Reads report a non-sensitive warning once per repository
instance when legacy rows exist and leave those rows null and invisible.

The trade-off is deliberate: existing unowned history cannot be browsed,
played, or deleted through the app, but the database rows and audio files are
preserved. Recovery requires separately verified ownership **for each row**
on an isolated backup, followed by an explicitly authorized manual recovery
procedure; no automatic inference or bulk assignment from an active profile
is safe with the data currently stored. Until a durable proof source is
identified, there is no automatically recoverable native case.

The How Do You Say page rechecks the active owner before playing or deleting a
row and refreshes a stale list after a denied action. Deleting a history row
does **not** remove its audio file: a path may be shared by another owner or
cache entry, and a history row alone does not prove exclusive file ownership.
Any future audio-cache cleanup needs an explicit reference-safe design.

## Verification

After all worktree edits settle, run:

```bash
bash scripts/validate-migration-attributes.sh
IncludeMobileTargets=false dotnet ef migrations has-pending-model-changes \
  --context ApplicationDbContext --framework net10.0 \
  --project src/SentenceStudio.Shared/SentenceStudio.Shared.csproj \
  --startup-project src/SentenceStudio.Shared/SentenceStudio.Shared.csproj
dotnet test tests/SentenceStudio.UnitTests/SentenceStudio.UnitTests.csproj \
  --filter 'FullyQualifiedName~StreamHistoryOwner' -p:IncludeMobileTargets=false
dotnet test tests/SentenceStudio.UI.Tests/SentenceStudio.UI.Tests.csproj \
  --filter 'FullyQualifiedName~HowDoYouSayDeletionTests' -p:IncludeMobileTargets=false
```

The relational migration test applies the **actual SQLite migration** to an
isolated in-memory legacy database and checks the EF migration history, schema,
and retained row. The SQLite owner-scoping regression deletes one profile,
creates a different one on the same day, and verifies the original null-owner
audio remains unowned and inaccessible. The UI deletion test creates a scratch
audio file and verifies its bytes remain after deleting the owned history row.
Run native-head migration validation only against an isolated app database; do
not apply or reverse the migration on a production or personal database to
check it. The native preference pointer must be cleared reliably on sign-out;
`DeviceUserScopeProvider` is registered but not yet populated by the existing
sign-in flow, so native history currently relies on that pointer.
Vocabulary Word Edit, Minimal Pairs, Vocab Quiz, and Number Drill still play
newly generated audio when a history save is refused; their cache files remain
on disk by design, but no unowned database row is inserted.
