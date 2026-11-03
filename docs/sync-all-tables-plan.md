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
