# Native authentication and sync readiness

Native sign-in and stored-token restore check the current authenticated access
token for one unexpired `user_profile_id` claim and require a matching
`active_profile_id` preference. Parsing the JWT claim does not validate its
signature locally; the API must authenticate each request. The presenter
leases the owner only to its in-flight readiness operation while reading the
local profile, awaiting sync, and deciding a route. The process-wide device
owner is published after an approved route and a final owner/token check.
An unconfirmed fresh account without a local profile stays on sign-in with
no published owner. A previously populated local profile may proceed offline
with pending sync indicated.

The iOS head initializes non-UI MAUI Essentials before starting native auth.
Its `NativePreferences` adapter uses `Preferences.Default`, retaining the
bundle's existing `NSUserDefaults` keys. Shared `IdentityAuthService` still
owns the credential triple through `NativeKeychain`: switching those keys to
Essentials SecureStorage would change their Keychain service and strand existing
sessions without a separately approved migration.

The normal iOS head now uses the existing `AddSyncServices` CoreSync registration
and `SyncService`, with `AddOwnerBoundNativeSyncReadiness` as the final bearer
handler. Its `UserProfile` table is download-only: a profile fetched during
sign-in is incomplete and must not overwrite the server's complete profile
during the first upload. SQLite migrations run before readiness; the shared
CoreSync provider provisions its tracking tables on the first owner-bound run.
An iOS build without an API endpoint still uses the unconfirmed/no-op ports.
Android retains its separate, unconfirmed production-readiness composition.

Normal (non-preview) Android registers the API auth client, secure token
store, real `SyncService`, and a transient `AuthenticatedHttpMessageHandler`
for its named CoreSync client. It currently leaves `INativeSyncReadiness` at
the native slice's fail-closed `UnconfirmedNativeSyncReadiness`. The iOS
opt-in adds a final `OwnerBoundNativeSyncBearerHandler` to that same named
CoreSync client. The handler requires a current revocable readiness lease,
matching profile preference, and matching authenticated token at each send;
a published device owner without a lease cannot authorize CoreSync. The
separate `AddSentenceStudioNativeProductionAdapters()` extension wires the
five plan/quiz ports and does not enable readiness by itself.

The authenticated native CoreSync header is checked against the bearer claim
by the API's `NativeOwnerSyncProvider`. It narrows existing CoreSync change sets
to that owner's profile, resources, resource-mapped words, progress, contexts,
plans, and other directly owned tables. The native Dashboard generates an
absent plan through the existing shared `PlanService` and deterministic
generator after sync, then refreshes CoreSync to send the plan to the API.
Returning from the assigned quiz likewise refreshes the existing sync path.
This is not a new HTTP plan endpoint or seeded preview data.

Native uploads check the stored owner of every existing primary key before
CoreSync writes it, including rows owned through a resource, progress record,
or vocabulary word. An incoming owner field cannot reparent a foreign row;
new owned rows and updates of owned rows still sync. Existing vocabulary words
referenced by multiple owners cannot be edited through the native upload, but
an owner can still sync their own progress and learning contexts for a shared
word already mapped to their resource. Validated resource and word mappings
are made visible before same-upload progress and contexts, regardless of the
incoming item order. Owner-linked references are checked against stored parents
or other independently validated rows in the same upload, including optional
channel, resource, skill, and conversation links. Native rows are then applied
parent-first: pinned CoreSync can otherwise skip a valid child-first foreign-key
insert while returning a successful completion. Native delete/tombstone uploads
currently fail closed, including owned IDs, until cascading ownership and
delete convergence can be proved. The upload adapter holds a PostgreSQL
advisory lock across its ownership checks and CoreSync write, serializing sync
uploads across API replicas without a schema change. CoreSync uses its own
transaction, so this is not row-level security for unrelated database writers.

Native bulk downloads bind the server-generated session ID to the authenticated
JWT owner. Both JSON and binary chunk routes refuse a different owner or a
headerless replay before reading CoreSync's cached rows. Completed sessions
and idle sessions older than 20 minutes evict the cached rows and binding.
Headerless sessions created by existing clients retain their legacy route
behavior; these new checks apply to native-created sessions only.

Native bulk **uploads** also bind each session ID to its JWT owner before
CoreSync stages a payload. Duplicate begins cannot replace a native session;
JSON and MessagePack chunks, and both completion routes, check the owner
before touching its staged rows. Headerless calls cannot target native-created
sessions; headerless-created uploads keep their existing behavior. A per-session
gate serializes native chunks and completion, and an idle 20-minute expiry plus
a 512-session limit bounds native upload bindings. Completion or expiry evicts
CoreSync's cached payload before dropping the binding. CoreSync's bulk caches
remain process-local, so multi-replica bulk transfers still need request
affinity; a session lost on restart cannot be resumed from another replica.

The native client's existing SQLite store ID is bound to the first authenticated
owner using a reserved, durable row in pinned CoreSync's existing
`__core_sync_remote_anchor` table. A transaction-scoped PostgreSQL advisory
lock makes first use exclusive across API replicas. Native uploads, downloads,
and version saves use an owner-derived server-side store ID; the HTTP target
anchor is translated back to the unchanged client ID. This keeps native
cursors separate from headerless MAUI/Web cursors even when both send the same
raw store ID. If a raw cursor already exists but has no native binding, every
row historically attributed to that store in CoreSync change tracking must
resolve to the authenticated owner in PostgreSQL. A foreign, ownerless, deleted,
or unprovable source row refuses native adoption; headerless legacy use still
works. Once ownership is proved, its local and remote versions are copied to
the derived ID without advancing or resetting the raw cursor. This avoids
replaying a populated SQLite store: historical rows contain a mix of local
and UTC wall-time representations. A genuinely new store with no previous
cursor takes the normal owner-filtered snapshot.
The binding survives API restarts without an EF migration. Headerless legacy
clients continue to use their raw store IDs.

The API's legacy Npgsql timestamp setting materializes `timestamptz` as
`DateTimeKind.Local`. Native-only downloads convert those values back to UTC
before JSON or MessagePack transfer; date-only and timezone-unspecified
columns are left as-is, and headerless clients retain their existing wire
format. The derived cursor's version must not change casually: replacing an
in-use alias without copying its versions would replay existing rows and
rewrite mixed local/UTC SQLite timestamps.

### Synchronized owner/reference audit

The table set below is shared by SQLite and PostgreSQL in
`SharedSyncRegistration`. Native uploads validate stored IDs before trusting
incoming fields. When a parent is included in the same upload, its stored owner
(if it already exists), its incoming owner, and its own references must all
validate before a child may point to it.

| Table | Native owner and parent/reference rule |
| --- | --- |
| `UserProfile` | Root ID must be the authenticated owner. Other owned rows require this profile in PostgreSQL or in the same upload. |
| `LearningResource` | `UserProfileId`; optional `SkillID` must name an owned `SkillProfile`. Legacy `OldVocabularyListID` remains a shared/ownerless reference. |
| `VocabularyWord` | Shared row; readable only through an owned resource mapping. A native upload cannot overwrite a word owned by another user or shared with several users. |
| `ResourceVocabularyMapping` | `ResourceId` must name an owned resource; `VocabularyWordId` must be a new word in the validated upload or an eligible existing/shared word. |
| `Challenge` | No trusted owner; withheld from native transfers. |
| `Conversation` | `UserProfileId`; optional `ScenarioId` names a non-synced, globally shared `ConversationScenario`. |
| `ConversationChunk` | `UserProfileId` and required `ConversationId` must agree with an owned `Conversation`, including a later parent in the same upload. |
| `SkillProfile` | `UserProfileId` must match; ownerless historical skills cannot serve as native parents. |
| `VocabularyList` | No trusted owner; withheld from native transfers. |
| `VocabularyProgress` | `UserId` must match; `VocabularyWordId` must be reachable through an owned resource mapping. |
| `VocabularyLearningContext` | Required `VocabularyProgressId` must belong to an eligible owned progress row; optional `LearningResourceId` must be owned. |
| `MonitoredChannel` | `UserProfileId` must match; ownerless historical channels cannot serve as native parents. |
| `VideoImport` | `UserProfileId`; optional `MonitoredChannelId` and `LearningResourceId` must each name an owned parent. |
| `DailyPlan` | `UserProfileId` must match. |
| `DailyPlanCompletion` | `UserProfileId`; optional `ResourceId` and `SkillId` must name owned rows. `PlanItemId` is an ad-hoc/plan item key, not an FK to another synced row. |
| `UserActivity` | `UserProfileId` must match. |
| `DiaryEntry` | `UserProfileId` must match. |
| `PhraseConstituent` | `PhraseWordId` must be an exclusively owned phrase word; optional `ConstituentWordId` may be a word already mapped to this owner, including a shared word. |

The disposable two-owner HTTP/PostgreSQL regressions in
`tests/SentenceStudio.Api.Tests/Sync/NativeOwnerSyncPostgresTests.cs`
exercise foreign and ownerless parents, updates/reparenting, reversed
same-upload parent ordering, both upload formats/completions, cursor persistence
across API instances, and unchanged headerless routes.

The existing AppLib bearer handler still obtains the global token on each
request and can swallow token failures; the final native handler enforces the
lease and refuses anonymous or changed-owner sends. In Development,
the API may authenticate a request without a bearer as its synthetic dev user
when dev-auth fallback is enabled (or no JWT key is configured); Production
requires a JWT signing key and rejects a missing bearer.

A read-only probe before the native API filter returned 25 `UserProfile`
rows to squad-jayne's bearer, including 24 foreign profiles. With the
native owner header, the same existing route returned one profile, 14 owned
resources, 114 resource-mapped words, 120 mappings, and 52 progress rows.
The canonical iOS simulator then synced those resources to the existing
SQLite database, generated a two-item plan, completed the assigned ten-turn
Vocabulary Quiz, uploaded ten learning contexts and the completed plan item,
and restored the same owner and completed state after a cold restart.

The legacy CoreSync route **without the native header remains unfiltered** so
existing MAUI clients keep their behavior. This is not global server-enforced
tenant isolation: a full release still requires closing that legacy gap and
validating the deployed multi-replica boundary. The native store binding's
reserved anchor rows rely on CoreSync 0.1.129's `id TEXT` metadata contract and
must be rechecked before a provider upgrade. `Challenge` and
`VocabularyList` have no trusted owner
column and are withheld from native downloads; 19 older progress rows whose
words are no longer mapped to an owned resource remain on the server rather
than being inserted locally without their vocabulary dependencies. Ownerless
delete tombstones are also withheld, so deletion convergence needs a
separate release solution. No user rows, existing sync anchors, preferences,
or app data were cleared to make the current slice work.
