# Plan: sync every learner table across devices and the web

Captain, 2026-11-02: "we should sync all the tables in order for a user's
experience to be consistent across all app surfaces, primarily a mobile device
and the website, but perhaps also between a mobile device and a tablet and the
web and desktop."

Today CoreSync syncs 18 tables (`src/SentenceStudio.Shared/SharedSyncRegistration.cs`).
18 more are never synced. The web app writes straight to the server database,
so anything a device keeps to itself never reaches the web, and the reverse.

Captain: please mark each **Decision** row (blank = recommendation accepted).

## Why the tables can't just be added to the sync list

CoreSync merges rows from many devices by their ID. That needs three things,
and most of these tables are missing at least one:

1. **A text (GUID) ID.** 11 tables use auto-numbered IDs (1, 2, 3...). Two
   devices both create row 7, and one overwrites the other.
2. **An owner.** The server only accepts and sends rows it can prove belong to
   the signed-in learner (`src/SentenceStudio.Api/Sync/NativeOwnerSyncProvider.cs`).
   4 tables have no owner column at all.
3. **One row per real-world thing on every device.** Number Drill keeps one
   progress row per drill bucket and the database rejects duplicates. Two
   devices each create their own row with a different ID; syncing the second
   is rejected and can stop that device's whole download.

## The 18 tables

| Table | ID | Owner | What it holds | Work needed |
|---|---|---|---|---|
| NumberAttempt | text | yes | Number Drill answers | Register + server rule |
| WordAssociationScore | text | yes | Word Association results | Register + server rule |
| NumberMasteryProgress | text | yes | Number Drill review schedule (SM-2) | Same ID on every device (from learner + bucket); merge existing duplicates; register |
| NumberContext, NumberSubMode, NumberCounter | text | - (app content) | Number Drill catalog, seeded by each device | Fixed IDs everywhere (see D2); re-point existing `CounterId`s |
| ActivitySession | number | yes (`UserId`) | Resume state of in-progress activities | Convert ID to text; register |
| MinimalPair, MinimalPairSession, MinimalPairAttempt | number | yes (`UserId`) | Minimal Pairs words, sessions, answers | Convert IDs and the number links between them to text; register |
| StreamHistory | number | optional | How Do You Say audio history | Convert ID; legacy rows with no owner stay local (D3); audio files (D4) |
| ExampleSentence | number | via its word | Example sentences per word | Convert ID; owner through `VocabularyWordId` |
| ConversationMemoryState | number | via its conversation | Conversation memory | Convert ID; owner through `ConversationId` |
| ConversationScenario | number | **none** | Built-in and custom conversation scenarios | Built-ins: fixed IDs (D2). Custom: add owner column (D3) |
| SceneImage | number | **none** | Describe a Scene images | Add owner column; convert ID (D3) |
| Story | number | **none** | Generated stories | Add owner column; convert ID (D3) |
| GradeResponse | number | **none** | Saved AI grades (Teacher, Cloze) | Add owner column; convert ID (D3) |
| RefreshToken | number | yes | Server sign-in tokens | **Never sync** (D1) |

### Ownership bug found on the web (fix in the same work)

`SceneImageService`, `StoryRepository`, `ScenarioRepository` and the
`ClozureService` grade reads query these tables with no owner filter. On the
multi-tenant web app every learner sees every other learner's scene images,
stories, custom scenarios and grades. AGENTS.md forbids unscoped reads. Adding
the owner column fixes this for the web as well as enabling sync.

## Decisions

| # | Question | Recommendation | Decision |
|---|---|---|---|
| D1 | `RefreshToken` is a server sign-in credential. Exclude it from sync? | Exclude. It must never be on a device. | Exclude |
| D2 | Built-in content (Number Drill catalog, built-in scenarios) is created by each device. Make it identical everywhere, or sync it down from the server? | Fixed IDs derived from each item's code, so every device and the web create the same rows. No sync needed and it works offline on first launch. | Do recommendation |
| D3 | Existing rows with no provable owner (scene images, stories, grades, custom scenarios, old audio history). AGENTS.md forbids guessing an owner. | Keep them on the device or server where they are, visible only there, and never sync them. New rows get an owner and sync. On the web, hide unowned rows from everyone except through a later verified recovery. | Do recommendation |
| D4 | Audio history rows point at audio files saved on one device. Syncing the row doesn't move the audio. | Phase 1: sync the history; another device regenerates the audio the first time it is played (costs one speech call). Upload to server storage later if regeneration cost matters. | Do recommendation |
| D5 | Two devices already have Number Drill progress for the same bucket. Which wins when they first merge? | Keep the one reviewed most recently; keep every attempt row (attempts never conflict). | Do recommendation |

## Phases

Each phase ships on its own: build, tests, migrations validated, two-learner
isolation proven on the server, then run on simulators/emulator and the web.

1. **Number Drill and Word Association** (your original ask). Fixed catalog
   IDs, same mastery ID on every device, register `NumberAttempt`,
   `NumberMasteryProgress`, `WordAssociationScore`, server owner rules. A
   data migration re-keys existing mastery rows; it doesn't change any
   column type.
2. **Owned tables with number IDs**: ActivitySession, the three Minimal Pairs
   tables, StreamHistory, ExampleSentence, ConversationMemoryState. Number
   ID to text ID on both databases (SQLite needs a table rebuild that copies
   every row), links re-pointed, then registered.
3. **Tables with no owner**: add the owner column to SceneImage, Story,
   GradeResponse and ConversationScenario; fix the web's unscoped reads;
   fixed IDs for built-in scenarios; register.
4. **Audio** per D4.

## Data safety

- Every phase changes the database on the server and on every device. Each
  migration gets the dual-provider workflow (PostgreSQL + hand-written SQLite,
  both validation scripts), a run on scratch copies of real-shaped databases,
  and an Up/Down check on a verified backup.
- Nothing runs on the production server, DX24, Pixel 5 or your Mac without a
  fresh verified backup and your permission in that turn.
- Installed MAUI reference apps keep working: they only send the tables their
  build registers, and the server must keep accepting that. This is checked
  in every phase.
- Rows that can't be converted safely are kept and reported, never deleted.

## Status

**Phase 1 done (2026-11-02)**: `9cf712f9`, `c51ce0fe`. Verified on the local
stack:
- the API moved its Number Drill rows to the shared IDs;
- Kaylee's simulator, upgraded from the old app, uploaded her earlier
  answers and progress;
- a second simulator on the same account downloaded them;
- practice on either simulator reached the other through the server.

The web app uses the same code for creating progress rows (test
`SubmitAnswerAsync_CreatesMasteryWithTheSharedBucketId`), but I haven't
checked a web sign-in yet: that needs the test account password.

Leftover rows: on the local server, 11 Number Drill answers and 8 progress
rows belong to two profiles that no longer exist. Per D3 and AGENTS.md they
are not given an owner or deleted. No device will download them.

Deploy order: the API ships before any app build with phase 1. An older API
refuses uploads that contain these tables.

**Phase 2 split** (2026-11-02):
- **2a**: Minimal Pairs (pairs, sessions, answers), audio history and
  conversation memory.
  - Migration `20261103015116_SyncTextIdsPhase2a` (PostgreSQL + SQLite)
    gives every row a new text ID; old numbers are not reused.
  - A pair's ID is `mp:<learner>:<word A>:<word B>` and a conversation
    memory's is `cm:<conversation>`, so each exists once everywhere.
  - Conversation memory gains its owner, filled from its conversation.
  - How Do You Say regenerates a synced entry's clip in its own voice (D4);
    web backport B38.
- **2b**: activity resume state (ActivitySession). It allows one in-progress
  session per activity and resource, and the native quiz writes its own
  bookkeeping rows into it, so it needs a design before syncing.
- Example sentences move to phase 3: they need an owner column and
  owner-filtered reads, like the other unowned tables.

**Phase 2a done (2026-11-02)**: `bad729fb`, `fe2e2011`. Verified on the local
stack:
- the API migrated (35 audio history rows kept, new IDs);
- device A migrated, created and drilled a Minimal Pair, and synced it;
- device B migrated and received the pair (same ID), its session, all 3
  answers and all 5 audio entries;
- a synced audio entry played on device B by regenerating its clip in place
  (D4).

**Rollout gap fixed (`fe2e2011`)**: a device that syncs with an older app
after the API starts tracking a table moves past that table's server rows
(CoreSync downloads only the tables the app asks for). After upgrading, the
device asks `POST /api/v1/sync/resend` to re-send its own owner's rows of
newly tracked tables, retrying until it succeeds.

**Phase 2b done (2026-11-03)**: quiz runs (ActivitySession) sync in one shared
format for every client (`docs/activity-session-format.md`). Verified on the
local stack: throwaway PostgreSQL 16 Up/Down/Up, the local server, two
simulators and the web app on one learner (details under "Phase 2b built").

## Phase 2b design: activity resume state (for review, 2026-11-03)

Captain, 2026-11-03: "I accept the recommendation, proceed." Built; see
"Phase 2b built" below.

### What the table holds today

`ActivitySession` is used only by the Vocabulary Quiz, on the web and in the
native apps. It holds two kinds of rows:

1. **Quiz resume state**: one row per quiz run (`ActivityType =
   "VocabularyReview"`). The row is In progress, Completed or Abandoned. The
   database allows only one *in-progress* row per learner + activity + launch
   (the launch is the plan card, focus words, resources, due-only and skill,
   `VocabQuizSessionSnapshot.BuildLaunchContextKey`).
2. **Native answer receipts**: the native quiz writes one permanent row per
   scored answer (`VocabularyReview.NativeAttemptReceipt.v1`,
   `VocabularyReview.NativeSentenceReceipt.v1`) so a retried answer is never
   scored twice. Receipts are never deleted, carry a copy of the quiz state
   (about 10 KB each), and point at their quiz row by its number ID
   (`SourceSessionId`).

Observed on the local stack (read-only):
- server: 31 quiz rows for 3 learners (15 in progress, 15 abandoned, 1
  completed); 6 launches already have more than one row (up to 5);
- device A: 11 quiz rows and 37 receipts; other simulators hold up to 47
  receipts.

The two apps save resume state in different formats (E3 unifies them for every client). The web saves the bare
snapshot; the native quiz wraps it with the plan date, the turn ID and a
graded answer not yet confirmed (`NativeQuizStoredState`). Each app safely
refuses the other's format and offers "start over".

### Problems syncing it as is

1. **Two devices start the same quiz**: each creates its own in-progress row
   with its own ID. The second one to sync breaks the one-in-progress rule
   and is rejected, which stops that device's whole sync (the Number Drill
   problem from phase 1).
2. **Receipts** would upload every scored answer's bookkeeping to the server,
   forever. No other device or the web needs them; the scores themselves
   already sync through `VocabularyProgress` and `VocabularyLearningContext`.
3. **Phone and web share one row per quiz once synced**. With today's
   formats, opening on the web a quiz started on the phone shows "start
   over". Starting over abandons the phone's progress too. That is worse
   than today, where the two never meet.
4. **A graded answer could be scored twice**: the phone grades an answer and
   saves it before scoring. If the row syncs and a second device resumes
   it, the second device has no receipt for that answer and scores it again.

### Proposed design

| # | Question | Recommendation | Decision |
|---|---|---|---|
| E1 | How do two devices end up with one row per quiz? | The ID is made from learner + activity + launch (`as:<learner>:<hash of launch>`), like phase 2a's `mp:` and `cm:` IDs. The row is reused for later runs of the same launch (In progress again, new start time). Two devices on the same quiz write the same row, and the most recent save wins. | Accepted |
| E2 | Receipts | Keep them on the device: the native app leaves receipt rows out of its uploads, and the server refuses them if one ever arrives. Alternative: move receipts to their own table that is never synced (cleaner, but a new table on both databases and a rewrite of both answer committers). | Accepted |
| E3 | Resume a quiz on any client: web, MAUI, the native apps, and later Flutter | Yes, with **one saved format for every client** (Captain, 2026-11-03: unify rather than teach each client the others' formats; parity across web, native, MAUI and the future Flutter port). The format is a written, versioned contract, `docs/activity-session-format.md`, with sample JSON files that every client's tests read. The shared C# code is one implementation of it; the Flutter port (Dart) implements the same contract. It is the native wrapper, which already contains the web's snapshot. Fields only some clients use (a graded answer not yet confirmed, the sentence check) are optional. The web and MAUI share one quiz page (`VocabQuiz.razor`), so one change covers both. The web's existing saved quizzes (live in production) are converted to it once, so none are lost. Web/MAUI change is backport B39. | Accepted |
| E4 | A graded answer not yet confirmed, opened on another device or the web | Only the device that graded it finishes it. The native wrapper records which install graded it. Anywhere else, that question is asked again, so nothing is scored twice and nothing scored is lost. | Accepted |

### Existing rows (migration)

- Every number ID becomes text, on the server and on every device (SQLite
  table rebuild that copies every row).
- Per learner + activity + launch: the in-progress row gets the E1 ID; if none
  is in progress, the most recently updated row does. Older completed and
  abandoned rows keep their history with new random text IDs; no row is
  deleted.
- Receipts get new random text IDs. Their `sourceSessionId` is re-pointed to
  their quiz row's new ID inside the stored JSON, so in-flight answers still
  resolve.
- If the server and a device already each hold an in-progress row for the
  same quiz (only if it was run on both), both keep their own copy until the
  next save; then the last save wins.

### Code that changes

- `ActivitySession.Id` becomes `string`; `IActivitySessionService.AbandonAsync`
  takes the text ID (callers: web `VocabQuiz.razor`, `NativeQuizPresenter`).
- `ActivitySessionService.SaveSnapshotAsync` writes the E1 row, reviving it
  if it was completed or abandoned. It still abandons stray legacy
  duplicates.
- Receipt `SourceSessionId` becomes `string` in both native committers.
- Native upload wrapper (`NativeLocalOnlyMappingExportProvider`) drops
  receipt rows (E2); the server owner rule (`NativeOwnerSyncProvider`) checks
  `UserId` and refuses receipt types.
- E3: write `docs/activity-session-format.md` and its sample files first.
  `NativeQuizStoredState` moves to Shared as the C# implementation with one
  reader and validator; `VocabQuiz.razor` (web and MAUI) saves and resumes
  through it;
  existing bare web snapshots are converted. Two differences to settle while
  building: the web keeps no plan row for a quiz started outside the plan
  (the native app does), and the native check requires the current display
  direction to match the saved one.
- E4: the shared format records which install graded a pending answer.
- Register `ActivitySession` in both sync lists; add it to the re-send list
  (`fe2e2011`).

### Checks before it ships

- Migration: both validation scripts, scratch SQLite copies of device A's
  database (quiz rows and receipts), PostgreSQL on isolated storage, Up and
  Down on a verified backup.
- Two learners on the server: neither sees the other's rows.
- Simulators: start a quiz on device A, resume it on device B at the same
  question; same quiz started on both offline, then both sync without
  errors; a graded answer resumed on device B is asked again and not scored
  twice; receipts never reach the server (database check).
- Web: resume a phone quiz on the web and the reverse (needs the test
  account sign-in).
- Installed MAUI apps keep syncing (they don't send this table). A rebuilt
  MAUI app gets the same migration, format and sync as the native apps; its
  first install on DX24, Pixel 5 or your Mac migrates real data, so it needs a
  verified backup and your permission in that turn.
- Every client's tests read the same sample files; the Flutter port adopts
  them when it joins.

### Phase 2b built (2026-11-03)

Verified on the local stack (checks below). What it adds:

- Migration `20261103151517_SyncTextIdsPhase2b` (PostgreSQL + hand-written
  SQLite), as described above. On PostgreSQL it also rebuilds the launch index:
  the local server's index was neither unique nor filtered (an earlier version
  of `AddActivitySession`), so an older in-progress duplicate is first marked
  abandoned, as the app already does.
- The shared format: `docs/activity-session-format.md`, sample files in
  `tests/fixtures/activity-session/`, `VocabQuizSavedState` in Shared (the
  native wrapper moved there). The web/MAUI page saves and reads it (B39).
- Receipts get `receipt:` IDs; the native upload leaves them out and the server
  refuses them.
- `ActivitySession` is in both sync lists and the newly-synced-tables list, so
  devices upload their existing runs and re-request the server's.

Changes beyond the design, needed for runs to resume on another client:

- **Display direction.** The setting is per device. Native used to refuse to
  resume a run when the setting differed from the saved one; now, like the web,
  an unanswered question follows the current setting. An answered question is
  still shown as it was asked.
- **Done on a round finished elsewhere.** Native Done checked for a receipt of
  the round's last answer, which only exists on the device that scored it. A
  receipt that is present must still match; a missing one no longer blocks Done.
- **Plan rows.** Native compares a saved plan row or ad-hoc item only with its
  own saves; from other clients it resolves its own (or makes an ad-hoc item).

Code review (2026-11-03) fixes:

- A native save from before runs synced has no `savedBy`; the app now claims
  those as its own when it opens its database, before any sync, so an upgraded
  device still finishes its own graded answer.
- Done compares a recorded plan row only with this install's own saves, and
  allows a missing last-answer receipt only for another install's summary or
  one this install saved after moving past that answer (`turnScoredElsewhere`).
- A reused run row first copies the finished run's record to its own row, so
  no run history is overwritten.
- The SQLite migration also marks older in-progress duplicates abandoned before
  rebuilding the launch index; on PostgreSQL wrapped saves get the learner's
  local day when their time zone is known.
- Documented, not changed: an answer left unconfirmed on one device goes
  unscored if the run continues elsewhere; a restored backup shares the install
  ID; wrapped MAUI saves use the UTC day (docs/activity-session-format.md,
  "Known limits").

Second code review (2026-11-03) fixes:

- **No run is dropped.** Two clients can hold different runs of one launch (each
  started it before syncing, or the migration gave each client's latest run the
  run ID). The server now keeps the losing run as its own record row instead of
  dropping it (marked abandoned if it was in progress).
- **One record per run.** A run's record row has an ID every client derives (run
  ID and the second it started), so two clients reviving the same finished run
  keep one record, not two.
- **The legacy claim runs once** per learner database, before its first sync, and
  only on that learner's rows. Before, it ran at every launch and could claim
  another client's downloaded save.
- **Another device's unconfirmed answer resumes under any direction setting.**
  Native refused to resume when this device's setting differed from the other
  device's, though that answer is only moved past.
- **Done on another client's summary needs a scored answer** on the round's
  words since the run started (learning records sync with the run), so a summary
  alone completes no plan card.
- The two newer-copy merges (Number Drill mastery, quiz runs) share one helper
  (`LatestCopyUploadMerge`), and the server sends kept rows back in one statement.

Checks so far:

- Both migration validation scripts pass. `validate-mobile-migrations.sh` was
  still pinned to the word-owner migration (not updated in phases 1 and 2a); it
  now checks the 2a to 2b step, including the new ID type and launch index.
- SQLite migration tests pass against the iOS and Android builds of the model
  (16 of 16 each), including Up and Down on real-shaped rows.
- A scratch copy of device A's database (48 rows: 11 quiz runs, 37 receipts)
  migrated with every row kept, every receipt still linked, and every quiz run
  readable in the shared format.
- All test suites pass.
- PostgreSQL 16 (production's version) on a throwaway container loaded from
  the local server's backup: Up kept all 31 runs (15 in progress got run IDs, all
  readable in the shared format), rebuilt the launch index, and Down restored
  every saved quiz and status; Up ran again cleanly.
- Local server (2026-11-03): the API migrated on restart and queued its 31 runs
  for sync.
- Simulators, one learner on device A (SS Agent quiz) and device B (SS Agent
  merge-B), both upgraded in place (each made its own pre-migration copy):
  - B's older run was claimed by B's install and uploaded under its run ID.
  - A started the quiz, scored question 1, graded question 2 and was closed
    before confirming it; A's run reached the server with that unconfirmed
    answer. A's receipt did not.
  - B resumed at question 3 ("moving past it without scoring it here"),
    scored question 3; A then resumed at B's question 4.
  - Server and both devices end with the same 65 learning records (no answer
    scored twice), one receipt on each device and none on the server, and no
    sync errors.
- Web app (Kaylee, 2026-11-03): resumed the run the simulators shared, at
  B's question 4, and scored two questions (server: 67 learning records). This
  exposed a defect in E1: device A had saved the run, closed before syncing,
  and its later upload overwrote the web's newer save, because CoreSync's
  conflict rule takes whichever upload arrives last. Fixed: the server keeps the
  most recent save per run (`ActivitySessionUploadMerge`, like Number Drill
  mastery) and sends it back to the uploading device. Scores were never
  affected; only the saved position was. Re-run after the fix: device A saved
  offline (19:03), device B saved later (19:11), then A uploaded; the server
  kept B's save and A received it.
- A second defect, from the web's own code: when a word is mastered mid-round
  the web removes it from the batch but keeps it in the round's session list,
  and the shared resume check required session words to be in the batch, so
  such a save could not be resumed anywhere (the web included). Fixed: session
  words need only be the learner's (`VocabQuizLaunchValidator`), native resume
  restores them, and the native scorer checks them as owned words. Backport B40.
- End state: server and both devices hold the same 69 learning records; receipts
  only on the devices.

Deploy order: the API (with this migration) ships before any app build with
phase 2b. An older API refuses uploads that contain `ActivitySession`.

## Phase 3: tables with no owner (2026-11-03)

Captain's decisions (2026-11-03, squad decision inbox):
- **Scene images:** image links (URLs) sync; a photo taken on a device stays on that
  device until server image storage exists (like D4).
- **Scenarios:** built-in scenarios get the same fixed ID on every client
  (`SyncIds.BuiltInScenario`, for example `scn:first-meeting`) and never sync; custom
  scenarios get an owner and sync. A conversation's scenario link is a plain text
  reference with no database constraint, so a conversation never fails to sync over a
  missing scenario row; an unknown link reads as no scenario.
- **Existing rows:** rows in a native per-learner database belong to that learner
  (`NativeOwnerStores.AdoptUnownedRowsAsync`, at activation, before any sync). Example
  sentences take their resource's owner, else their word's. Rows in the pre-account
  device database, on MAUI, and unlinked server rows stay unowned: visible only where
  they are, never synced, hidden on the web (D3).
- **Story and GradeResponse** stay out of sync (decided under the AGENTS.md YAGNI gate):
  no client writes them today (`StorytellerService` and both `SaveGrade` methods have no
  callers; 0 rows on the local server). A feature that writes them again adds an owner and
  sync then.

What changed:
- Migration `20261103230000_SyncOwnersPhase3` (PostgreSQL + hand-written SQLite): text IDs
  and an owner column for `SceneImage`, `ConversationScenario` and `ExampleSentence`;
  `Conversation.ScenarioId` becomes text and loses its constraint. SQLite rebuilds the four
  tables with foreign keys off (as EF does), because dropping `Conversation` with them on
  would delete every conversation's chunks and memory.
- **The web's ownership bug is fixed** for the live tables: `SceneImageService`,
  `ScenarioRepository` and `ExampleSentenceRepository` read and write only the active
  learner's rows (`ActiveOwner`); with none they return nothing and write nothing. Before,
  every web learner saw every other learner's scene images and custom scenarios.
- The three tables are registered for sync. The server sends and accepts only the owner's
  rows, never a device photo, a built-in or an unowned row; an example only with a word
  the owner can see and from the owner's resource. Native uploads keep those rows local.
- Rebuilding `Conversation` on SQLite drops its tracking; it is listed in
  `SyncLateTables`, so a device re-sends the learner's conversations once, with their
  converted scenario links. The server's table is altered in place and stays tracked.

Known limits:
- **Deletes do not sync from a device.** The server refused every delete in a device
  upload (a delete carries only an ID), and one refused item blocked the whole upload, so native
  uploads kept deletes on the device. Resolved by owner-checked deletes, see "Owner-checked sync
  for every client" below.
- A scene image has no timestamp, so the gallery orders by ID (stable, not creation order).
- **Release-blocking: installed MAUI apps.** Their build reads `Conversation.ScenarioId`
  as a number, so a phase 3 conversation linked to a scenario cannot be read by them. Resolved
  by decision 1: headerless sync is refused (old builds stop downloading) and new MAUI builds
  sync with the owner checks; see "Owner-checked sync for every client" below and its
  deployment order.

Checks so far:
- Both migration validation scripts pass (pinned 2b to 3; the schema contract checks the
  new ID types, owner columns and the dropped constraint). SQLite migration tests pass
  against the iOS and Android models, including a phase 3 Up and Down that keeps every
  conversation chunk, re-points links, gives the first built-in copy its fixed ID and
  restores the constraint.
- Throwaway PostgreSQL 16 loaded from the local server: Up kept all 101 example sentences
  (99 got an owner from their resource or word), 6 conversations and 57 chunks; Down
  restored numbers 1..101 and the constraint; Up ran again cleanly. With seeded scenarios,
  the first "First Meeting" got `scn:first-meeting`, its second copy a GUID, and both
  conversation links followed their scenario.
- The API suite with PostgreSQL tests enabled on that server: 5,163 passed.

## Phase 4: audio (options for the Captain, 2026-11-03)

Today (D4) a synced audio-history entry plays on another client by synthesizing the clip
again in the entry's voice: one ElevenLabs call per client per phrase. The API's
`/api/v1/speech/synthesize` has no cache, and the AppHost already provisions a `media` blob
container that no code uses yet.

| Option | What it takes | Effect |
|---|---|---|
| **A. Server clip cache (recommended)** | The speech endpoint stores each clip in `media` under a hash of voice, model, settings and text, and serves the stored clip on a repeat request. No schema or client change. | Every client, and every learner asking for the same text and voice, reuses one clip: one ElevenLabs call per distinct clip. Works for quiz audio and How Do You Say alike. The clip is derived from the text, so sharing it across learners exposes nothing the text does not. |
| B. Upload each device's clip | A new owner-scoped upload/download endpoint, a server media reference column on `StreamHistory` (schema change on both providers), client upload after synthesis and download before regenerating. | Exact bytes travel with the entry, including clips that were not synthesized (recordings). More code, a migration, storage per learner, and a deletion design. |

Both need production blob storage and its access (managed identity) configured in Azure.

### Built: A, the server clip cache (Captain, 2026-11-04)

Cross-device audio that goes through the API is now served from the server cache. The first
request for a clip pays ElevenLabs once and the API stores the result in the `media`
container under `speech-cache/v1/`; every later request for the same clip, from any device or
any learner, gets the stored bytes. So a synced audio-history entry played on a second device
still regenerates in the entry's voice, as D4 describes, but that regeneration is a cache hit
rather than a paid call.

- **Who benefits.** Callers of `/api/v1/speech/synthesize` and `/synthesize-timestamped`: the
  native iOS and Android heads (quiz word audio through `NativeQuizSpeechSource`, Shadowing,
  How Do You Say, Reading) and anything using `SpeechGatewayClient`/`SpeechApiClient` (the
  future Flutter port too). **Not yet covered:** the shared Razor pages on the web and in the
  MAUI apps (VocabQuiz, HowDoYouSay, Shadowing, Reading, NumberDrill) inject
  `ElevenLabsSpeechService`, which calls ElevenLabs in process with its own `ElevenLabsClient`
  and never reaches the API. Covering them means routing that service through the API (or
  giving the WebApp the same store), which is a client change for the Captain to decide.
- **Key.** SHA-256 of provider, endpoint variant, voice ID, model, voice settings, output
  format and exact text (`src/SentenceStudio.Api/Speech/SpeechClipSpec.cs`). Blob names and
  metadata carry no text, user or resource ID. Timestamped clips store their timings with the
  audio.
- **No contract change.** Same endpoints, responses and content type; auth and the
  timestamped owner check run before the cache. No schema change on either provider.
- **Clients keep their local copies.** `StreamHistory.RegeneratedClipFileName` and
  `UiSpeechClipCache.ResolveEntry` (and the native equivalents) stay as the offline cache: a
  device that already holds a clip plays it without the network, and one that does not asks
  the API, which now answers from storage.
- **Best effort.** If storage is unreachable or denied, the API logs it and generates as
  before; a provider failure is never stored.
- **Azure.** Production needs the API identity's blob data access on `media` confirmed (and the
  container created if missing). The checks and the change awaiting approval are in
  `docs/deploy-runbook.md`, "Speech clip cache".

## Lookup history (decision 4 B, 2026-11-04)

Captain, 2026-11-04: "a dedicated lookup table ... seems more efficient and correct long
term." Words and phrases looked up in Writing (translate helper) and Reading (word lookup)
are kept in their own synced table, `VocabularyLookup` (migration
`20261104153858_AddVocabularyLookup`, PostgreSQL + hand-written SQLite). Every client lists
them as "Recent lookups" with "Add to vocabulary". A lookup is not vocabulary: it never
enters quizzes or review until the learner adds it.

**Columns.** `Id` (text), `UserProfileId` (owner, required), `Text` (as last looked up,
trimmed), `Translation`, `Language` (the language being learned, from the lookup's resource,
else the learner's target language), `Direction` (`ToNative`: the text is target-language;
`ToTarget`: the text is native-language), `Activity` (`Writing` or `Reading`, of the last
lookup), `ResourceId` (optional plain reference, no constraint), `FirstLookedUpAt` and
`LastLookedUpAt` (UTC instants). Index: owner + last lookup.

**One row per learner, language, direction and text.** The ID is
`"lk:" + hex(first 16 bytes of SHA-256(UTF-8(owner U+001F language U+001F direction U+001F text)))`,
lower-case hex, where language is trimmed and lower-cased (invariant), direction is
`ToNative` or `ToTarget`, and text is Unicode NFC, trimmed, every run of white space made one
space, lower-cased (invariant) (`SyncIds.VocabularyLookup`). Test vectors (checked in
`VocabularyLookupRepositoryTests`): owner `owner-1`, `Korean`, `ToNative`, `물` is
`lk:f47f2616cfde663868fd1963db3d1457`; owner `owner-1`, `Korean`, `ToTarget`,
`Hello   World` is `lk:d763ec34f12a61897324811212e68380`. A repeat lookup updates its row
(latest text, translation, activity, resource and time); two offline devices that look up the
same text create the same row ID.

**Owner rule (server).** A device downloads and uploads only its owner's rows; an upload that
names another owner or reuses another learner's ID is refused; a row with no owner is left
out and stays on the device
(`NativeOwnerSyncProvider`).

**Merge rule (server).** When an uploaded copy meets the stored row, the copy with the later
`LastLookedUpAt` wins, and the earlier of the two `FirstLookedUpAt` values is kept
(`VocabularyLookupUploadMerge` on `LatestCopyUploadMerge`). A losing copy is dropped from the
upload (never refused) and the merged row is sent back to the uploader. There is **no lookup
counter**: a count cannot merge correctly across devices without per-device tallies, so it is
not stored.

**Writing a lookup (every client).** Record only a lookup that returned a translation, for
the active learner, after the translation is shown; a failed or empty lookup records nothing;
a recording failure is logged and the translation stays shown. Reading also keeps its
`UserActivity` row ("Dictionary lookup: ...") for the activity log.

**Direction must be known, or nothing is recorded** (`VocabularyLookupRepository.DirectionFor`).
A match on the learner's own word by its target term is `ToNative`; by its native term,
`ToTarget`. The Translate prompt always translates into English, so its result is `ToNative`
only for a learner whose native language is English, and only when it differs from the text
(English typed in gives English back). For any other learner (for example a Korean speaker
learning English) the translation is shown but not recorded, because "Add to vocabulary"
would otherwise create a word with its sides swapped.

**Add to vocabulary.** The learner picks one of their resources in the lookup's language (not
smart resources); the target and native terms come from `Direction`; the shared
create-and-link path (`CreateAndAttachOwnedVocabularyAsync`) links an existing word instead of
duplicating it and reports a word the resource already has. The lookup row stays.

**Lists.** Newest 100 lookups, no paging. Web and MAUI: `/vocabulary/lookups`, from the
Vocabulary page header (desktop button, overflow menu). iOS: clock button in the Vocabulary
list toolbar, pushed on the same stack. Android: "Recent lookups" in the Vocabulary header.

**Rollout.** `VocabularyLookup` is in `SyncLateTables`, so a device that synced with an older
app after the API began tracking the table asks for the learner's web lookups again. Deploy
order: the API (with this migration) ships before any app build that syncs the table; an older
API refuses uploads containing it.

**Not built yet.** Deleting a lookup in the apps. The server side exists: owner-checked
deletes and the deletion log cover `VocabularyLookup` like every synced table.

## Owner-checked sync for every client (decisions 1 and 2, 2026-11-04)

Captain, 2026-11-04: "do A, and you should also do what is necessary to make the MAUI app
correct. I will still ship MAUI versions and use them in addition to native platform
versions." Decision 2: A, with word-link deletes kept on the device first.

### What the server does now

- **Headerless sync is refused.** `Sync:AllowHeaderlessClients` (false unless configured, in
  every environment) makes every CoreSync route (store-id, downloads in JSON and binary, upload
  begin, chunks and complete, save-version) answer 403 to a request without
  `X-SentenceStudio-Native-Owner`; the provider repeats the check
  (`src/SentenceStudio.Api/Sync/SyncAccessOptions.cs`). A headerless request had no owner
  checks: a download returned every learner's rows (GitHub #204). Opening the switch exists only
  for a deliberate, temporary rollout of old builds.
- **A row the server cannot place stays on the device.** An upload is refused as a whole (403)
  only for a malformed row or a row that names another learner in its own owner column: the one
  thing a client can always check before sending. Every other row the server cannot place for
  the uploading learner is left out and the rest applies: it names no owner; its stored copy
  is recorded or inferred as another learner's, or has no owner; its parent or word is not
  theirs; or its table does not sync from devices (`VocabularyList`). This repeats until the
  kept rows are consistent, so a left-out parent's children stay too. A decision from stored
  state never refuses an upload, because the device's copy can disagree: an old shared word with
  no recorded owner can name two learners on a MAUI install (no owner, so it is sent) while the
  server, lacking the device's new progress row, infers the other learner. A refused upload is
  offered again on every sync, so that install would never sync again. Nothing foreign or
  ownerless is ever applied.
- **A store binds to its first learner.** A device database's CoreSync store is bound to the
  first learner that syncs it with the owner header; every other account is then refused (403).
  A store that synced before without the header (an installed MAUI app) used to need every row
  it ever uploaded to prove the claimant; an old MAUI database always holds rows that prove
  nothing, so no MAUI install could have synced again. Now the claim is refused only when that
  history names learners and none of them is the claimant. Such a store downloaded every
  learner's rows unfiltered, so its cursor is valid for any owner and is copied once.
  **The trade-off:** a store whose history proves no owner (it only downloaded, or everything
  it uploaded has no owner, was deleted since, or is a legacy table) is claimed by whichever
  learner syncs it first with the new build, even if that is not the install's usual learner.
  The other learner is then refused on that install, and their work there that never reached
  the server stays on the install: it is not uploaded from it, even after they sign in again.
- **Owner-checked deletes.** A device delete is applied only when the stored row, looked up by
  its ID first, is the uploading learner's. A learner profile is never deleted from a device, and
  a word only when no other learner's link, progress, example, pair or phrase uses it. A delete
  of a row the same upload writes again is not applied. Any other delete (another learner's row,
  an unowned row, a row the server never had) is left out without refusing the upload: a device
  cannot know the owner of a row it already deleted. Deletes apply before writes, children
  before parents: a minimal pair answer references its two words with ON DELETE RESTRICT (the
  only restricting references among synced tables), so a word goes only after its answers, and
  a word delete is left out while an answer the upload does not remove (the learner's or
  another's) still uses it. CoreSync still skips a delete of a row changed on the server since
  the device last synced (an update elsewhere wins).
- **Deletes reach the learner's other devices.** Migration `20261104162953_AddSyncDeletionLog`
  adds a server-only log, `"SyncDeletion"` (table, row ID, owner, time), filled by a
  `BEFORE DELETE` trigger on each of the 28 synced tables that have an owner, so every delete
  path is captured: the web app, bulk deletes, foreign-key cascades (a child takes its owner from
  its parent's log row) and device deletes. A download sends a delete only when the row is gone
  from the server and the log names that device's learner; nobody else's devices learn of it,
  and a row that exists again is not deleted.

Why a server-only log, filled by triggers: CoreSync already carries deletes to devices (it
records each delete's ID and devices apply them), so the server only needs to prove whose row
it was, which needs the owner from before the row was gone. Triggers capture every delete path,
including cascades and CoreSync's own SQL, which an EF SaveChanges interceptor would miss. A
synced table that clients apply would need every client (MAUI, native, Flutter) to implement
it. The log is PostgreSQL only and not in the EF model: devices never read it, so a SQLite
table would be dead schema and one more migration on every device database
(`validate-migration-attributes.sh` warns that it has no SQLite counterpart; that is
intended).

### What a client does (contract for every client, Flutter included)

1. Every sync request carries `Authorization: Bearer <token>` and
   `X-SentenceStudio-Native-Owner: <the token's user_profile_id>`, both from the same verified,
   unexpired token. With no signed-in learner, or a token whose owner is not the active
   profile, send nothing (fail closed). Never send a headerless sync request.
2. Uploads leave out, and keep on the device: quiz answer receipts; scene images, custom
   scenarios and example sentences with no owner; device photos; built-in scenarios; and any
   row that is another learner's (its owner column names another learner, or its owner resolved
   from local parent rows does: a link from its resource, a learning record from its progress,
   a phrase part from its phrase word, a word from its recorded owner or the one learner its
   links and progress name; `SyncRowOwners`, `SyncUploadRules`). A row naming another learner in
   its owner column must be kept: the server refuses the whole upload for it. A row resolved to
   another learner only from parent rows is optional to keep (the server leaves it out anyway);
   the shared rules keep it. Leaving a row out still moves the upload cursor past it; it is
   offered again only when it changes.
3. Send every other change, deletes included. The server applies what it can place and leaves
   out the rest without failing the upload.
4. Apply downloaded deletes (CoreSync does), forcing them over local changes as `SyncService`
   does today.
5. After tracking a newly synced table for the first time, queue the learner's existing rows
   and ask `POST /api/v1/sync/resend` for the server's (`SyncLateTables`).
6. Keep one database (one store) per learner if the client lets several accounts sign in, as
   native does; a single-database client must expect a second account to be refused.

### What the MAUI apps do now

`AddOwnerCheckedSync` (AppLib), called by `SentenceStudioAppBuilder` after `AddSyncServices`:

- **Owner header.** `SignedInSyncOwnerSource` reads the owner from the sign-in token (the same
  parser native uses, `JwtOwnerClaim`) and requires it to be the active profile;
  `SyncOwnerHeaderHandler` adds the bearer and owner header from that one token or refuses to
  send.
- **Rows the server refuses stay on the device, unchanged.** `OwnerCheckedSyncProvider` leaves
  out device-only rows and other learners' rows (an installed MAUI app downloaded every
  learner's rows before owner checks, and its startup backfills may touch them); the server
  leaves out rows with no owner, legacy tables and rows whose parent it lacks. Nothing is
  deleted, re-owned or guessed; they stay visible on that install as before.
- **Deletes sync.** A delete on MAUI reaches the server and the learner's other devices when the
  row is theirs; otherwise it stays a local delete.
- **Newly synced tables** (phases 1 to 3, quiz runs, lookups) queue the learner's rows and ask
  the server to re-send its rows (`OwnerCheckedSyncResendRequester`).
- **A second account on the same install.** The install's store binds to the first learner who
  syncs it with this build; a second account signed in on that install gets 403 on sync, keeps
  working offline, and its work stays on that install (when the first learner syncs again, the
  second account's rows are left out as another learner's, and never upload from this install,
  even after that account signs in again). The first learner keeps syncing.
  To use a second account, use another install or a native app (one database per account). If
  the wrong account syncs first, the binding can only be cleared on the server
  (`__core_sync_remote_anchor` row `sentencestudio:native-store:v1:<store>:<owner>`), which
  needs the Captain's approval.
- **Upgrade.** An installed MAUI database at main's newest migration
  (`20260903175044_AddApplicationOperationLedger`) migrates to this branch with every row kept,
  through the same verified-backup step, and then uploads only its learner's rows
  (`MauiUpgradeFromMainTests`, against the iOS and Android models).

### Deployment order (Captain)

1. Ship the new MAUI builds to DX24, Pixel 5 and the Mac (each first install migrates real
   data: verified backup and your permission in that turn), and the native builds.
2. Deploy the API with this branch, `Sync:AllowHeaderlessClients` left unset (closed), together
   with or after step 1. The API migration adds the deletion log; nothing else in the database
   changes.
3. Sign in on each MAUI install with your own account first: the first account that syncs an
   install owns its sync.

An installed old MAUI build that syncs with the new API gets 403 and keeps working offline with
the data on the device; its changes stay pending and upload once it is updated (rows it changed
while offline included). A new MAUI or native build against an older API: the older API ignores
the header (main) or refuses deletes (earlier builds of this branch), so ship the API with or
before the apps that send deletes.

### Known limits

- Deletes made by a native build while deletes were kept on the device (2026-11-03 and the first
  commit of this work) were left out of uploads and their place in the upload queue has passed:
  they do not reach the server now. Delete such a row again on the web.
- A row left out of an upload stays left out until it changes on the device.
- The deletion log has no retention rule yet; rows are small (about 100 bytes plus index).
  Deferred by the Captain (2026-11-04), tracked in issue #216. Proposed, not built: a daily job removes log rows older than a year, and at once the rows of
  an account that no longer exists (account erasure logs every deleted row, and nobody can
  download them). A device offline for longer than a year then misses those deletes and keeps
  the rows (stale, never lost or leaked). Removal by `"Id"` below the first row newer than the
  cut-off needs no new index. CoreSync's own change table, `__core_sync_ct`, grows faster (one
  row per change of any kind) and has no retention either; pruning it needs a plan for devices
  whose cursor falls below the oldest kept change.
- A delete of a row whose owner nobody can prove (an old word linked from two learners, a phrase
  on such a word) reaches no device.
- A delete can cascade only into the learner's own rows, except legacy cross-owner links: a
  `SET NULL` child of another learner (an import or learning record pointing at the deleted
  resource) has that link cleared.

### Checks

- PostgreSQL 16 on a throwaway container (`APP_OPERATION_PG_TEST_CONNECTION`): every CoreSync
  route refuses a headerless request and changes nothing, the owner path still works
  (`HeaderlessSyncSwitchPostgresTests`); a mixed upload applies the learner's rows and leaves out
  the rest, a foreign row still refuses it (`UnplaceableRowsPostgresTests`); a MAUI-shaped
  database syncs through the shared `SyncService` with the MAUI rules and header, is not stuck,
  and a second account is refused while the first keeps syncing; a delete on one MAUI install
  reaches the learner's other install; an old shared word the server infers as another
  learner's is left out and the install keeps syncing (`MauiOwnerCheckedSyncPostgresTests`); two
  learners and several devices for device, web and cascaded deletes, foreign and unprovable
  deletes, a word deleted with its minimal pair answers, a word an answer still uses, and a
  recreated row; the deletion log read through its index (EXPLAIN); the migration is
  discovered, applied, rolled back and re-applied (`OwnerCheckedDeletesPostgresTests`).
- Both migration validation scripts (`validate-mobile-migrations.sh` in its EF-only and native
  SQLite modes; its full mode launches the MAUI macOS head, which this Mac's real MAUI app and
  data rule out).
