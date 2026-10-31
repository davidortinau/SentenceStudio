# Original app journey inventory

Read-only audit of every user journey in the original SentenceStudio app
(MAUI Blazor Hybrid + Blazor WebApp), written 2026-10-31 to scope the native
SwiftUI and Compose rebuild. Source of truth is the frozen reference
`native-reference-59c72262-20261001` (Copilot session files). The live `main`
checkout was compared read-only; differences are labeled "live-only".

Each file starts with a summary table, then numbered `start -> ... -> end`
journeys with file:line citations into the reference. Statements not proven
from code are marked "unconfirmed". Nothing was run to produce these files.

| File | Scope | Size |
| --- | --- | --- |
| [activities.md](activities.md) | 16 learning activities: Vocabulary Quiz, Vocabulary Matching, Cloze, Translation, Writing, Describe a Scene, Shadowing (also serves plan type Listening), Conversation, Reading, Video Watching, Number Drill, How Do You Say, Word Association, Minimal Pairs, Flashcard Preview, Diary. Plus launch contract, timers and plan completion, mastery, AI, TTS and quiz preferences. | ~480 lines |
| [views-features.md](views-features.md) | Everything else: shell and navigation, auth and account lifecycle, onboarding, Dashboard and Today's Plan, resources, vocabulary, skills, import, activity log and progress, profile, settings, sync, help and feedback, operator pages, modal/sheet/toast index, platform matrix, preference keys. | ~600 lines |
| [sam-coach.md](sam-coach.md) | Sam (the learning coach): 24 surfaces and 37 journeys, including chat, plan edits, write tools, vocabulary sets, memory, conversations, reports, limitations, route catalog, flags, APIs, operator surfaces. Also tracks Sam changes on the target branch after the frozen snapshot. | ~520 lines |

## Original-app defects found by the audit

These are behaviors of the original app, not of the native rebuild. Whether
the native rebuild reproduces, fixes or drops each one is a product decision.

- Plan items complete when minutes spent reach the estimate, not when the
  activity is finished. The visible plan timer widget never receives its
  query parameters.
- Only Vocabulary Quiz validates its owner on launch; most other activity
  pages pass an empty user ID through instead of failing closed.
- Minimal Pairs list and session hard-code user id "1", so pairs a real user
  creates never appear.
- Reading speed selector is saved but never applied; Shadowing has no
  recording; Conversation transcripts are not saved.
- Streaks ignore Quiz, Matching, Number Drill, Shadowing, Conversation and
  Video, which never write a `UserActivity` row.
- Data export discards the ZIP and still reports success; export queries are
  not owner-filtered and include an API key column.
- Delete account on the MAUI heads routes to a WebApp-only page.
- Regenerate Plan deletes today's completed plan rows without confirmation.
- Plan insight resource chips link to a route that does not exist.
- "Remember me" is written but never read.
- Sync failures have no UI.

See each file's findings section for citations and the full list.
