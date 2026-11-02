# Bug fixes from the native activity port that the WebApp and MAUI app still need

Captain, 2026-11-01: every bug fixed while porting activities to the native heads
must also be fixed in the Blazor WebApp and the MAUI app, so any of the three can
be run. This is the work-item list. Native work continues first; these are not
started yet.

"Shared" means the defect lives in shared code (`SentenceStudio.Shared`,
`SentenceStudio.AppLib`, prompts, API) and one fix covers WebApp and MAUI.
"UI" means the defect lives in the Razor pages under `src/SentenceStudio.UI`
(used by both the WebApp and the MAUI Blazor Hybrid heads).
Native fix references are commits on `native-platform-heads`.

## Owner scoping and data integrity

| # | Activity | Defect in WebApp/MAUI | Where | Native fix |
|---|---|---|---|---|
| B1 | Minimal Pairs | All reads and writes use hard-coded user `"1"`; a real learner's pairs never show | UI `MinimalPair*.razor`, `MinimalPair*Repository` | Minimal Pairs merge (`33ed998f`) |
| B2 | Minimal Pairs | `MinimalPairAttempt` prompt/selected word FKs are RESTRICT; deleting a word with attempts fails (and a synced delete may fail on devices) | Shared schema, both providers | Pending Captain decision (migration to Cascade) |
| B3 | Vocabulary Matching | Uses `Preferences.Get("active_profile_id","")`; empty owner is passed through instead of refused | UI `VocabMatching.razor` | Matching foundation (`a0303765`) |
| B4 | Word Association | Writes with an empty profile id when none is active | UI `WordAssociation.razor`, `WordAssociationService` | Word Association merge |
| B5 | Diary | Entry update and feedback save don't check the owner | Shared diary repository | Diary merge |
| B6 | Describe a Scene | Vocabulary scoring matches against the whole vocabulary table, not the owner's words | UI `Scene.razor` / scoring call | Scene merge |
| B7 | Writing | Translate helper saves each AI translation as a `VocabularyWord` with no resource link (unattributable row; on native it blocks all CoreSync uploads). Captain expects a lookup history: design an owner-scoped history instead | UI `Writing.razor` | Native stops saving; history is future work |
| B8 | Reading | Dictionary lookup caches the word inverted (native/target swapped) and with no owner | UI `Reading.razor` | Reading merge |

## Failures recorded as fake results

| # | Activity | Defect | Where |
|---|---|---|---|
| B9 | Translation | Failed AI grade is saved as a 0% attempt | `TeacherService` caller in `Translation.razor` |
| B10 | Describe a Scene | Failed AI grade is saved as a 0% card and activity row | `Scene.razor` |
| B11 | Word Association | AI returning no verdicts records a wrong attempt | `WordAssociation.razor` |
| B12 | Cloze | Any AI failure shows "No cloze sentences" silently (`ClozureService` returns an empty list) | Shared `ClozureService` + `Cloze.razor` |
| B13 | Cloze | Sentences where the word can't be blanked are shown with the answer visible | Shared `ClozureService` |
| B14 | Conversation | Empty AI reply adds an empty bubble; start/reply failures are silent | UI `Conversation.razor` |

## AI prompts and models (shared, affects all three)

| # | Activity | Defect | Where |
|---|---|---|---|
| B15 | Conversation | Azure OpenAI content filter rejects the combined "SYSTEM INSTRUCTIONS…TASK…" opening prompt (400), so the chat opens empty. Send instructions as the system role (`/api/v1/ai/chat-messages`) | `ConversationService`, `StartConversation*.scriban-txt` |
| B16 | Conversation | `Reply.Comprehension` description asks for 0.0-1.0 but prompt and display expect 0-100, so it shows "0%" | `Shared/Models/Reply.cs`, prompts, `Conversation.razor` |
| B17 | Conversation | Persona replies include the score text ("(이해도: 85/100)") because the scenario prompt asks for scoring in the reply | `Conversation.scenario.scriban-txt` |

## Activity behavior bugs

| # | Activity | Defect | Where |
|---|---|---|---|
| B18 | Number Drill | Listen & Place answers are never counted and show no feedback (0/0 summary) | `NumberDrill.razor` |
| B19 | Number Drill | Disambiguate hints never appear (nesting bug) | `NumberDrill.razor` |
| B20 | Number Drill | Next during the 1.5 s auto-advance skips an item | `NumberDrill.razor` |
| B21 | Number Drill | Mode list order is undefined (query has no ORDER BY) | Numbers repository |
| B22 | Number Drill | Summary median and "Patterns to Review" are always empty (service returns 0 ms / no patterns) | `NumberSessionService` |
| B23 | Number Drill | No gate for non-Korean target languages (silently runs Korean) | `NumberDrill.razor` |
| B24 | Cloze | Review due dates compared with local `DateTime.Now`; stored dates are UTC | Shared `ClozureService` |
| B25 | Cloze, Translation | `UserActivity.CreatedAt` written with local time; streaks read UTC (verify the Razor pages) | UI pages / `ProgressService` callers |
| B26 | Shadowing | Slow speed is ignored (synthesize API has no speed); apply as playback rate | `Shadowing.razor`, speech API |
| B27 | Shadowing | Waveform tap-to-seek handler returns the current position (never seeks) | `Shadowing.razor` |
| B28 | Shadowing | Audio synthesis failure is only logged; nothing tells the learner | `Shadowing.razor` |
| B29 | Reading | "Audio generation failed" message is hidden as soon as generation stops | `Reading.razor` |
| B30 | Reading | Single-tap "select sentence" handler never fires (dead code) | `InteractiveText.razor` |
| B31 | How Do You Say | Always re-synthesizes and adds a duplicate history row instead of using the cache | `HowDoYouSay.razor` |
| B32 | How Do You Say | History timestamps shown in UTC; raw exception text shown to the learner | `HowDoYouSay.razor` |
| B33 | Translation, Writing | Navigation allowed while a grade is in flight, so a late result can land on the wrong sentence | `Translation.razor`, `Writing.razor` |
| B34 | Many | Hard-coded English strings (Cloze Familiar messages, Number Drill, Writing, Reading toasts) need resx keys (native added `NumberDrill_*`, `Writing_*`, `Reading_*`, `NativeCloze_*` keys that can be reused) | UI pages |

## Platform (MAUI app only)

| # | Area | Defect | Where |
|---|---|---|---|
| B35 | Audio cache | Cached clips are stored with absolute paths; on iOS the app container path changes on reinstall, so cached audio is never found and is re-synthesized | MAUI audio cache users (quiz, shadowing, how-do-you-say) |
| B36 | Writing | Same as B7 on MAUI with CoreSync: an unowned `VocabularyWord` row makes the server refuse the device's uploads (403) | MAUI CoreSync |

## Process

- Each item: reproduce in the WebApp (and MAUI where relevant), fix in shared code when possible, add a regression test, run the relevant E2E journey.
- Items marked shared (B12, B13, B15-B17, B21, B22, B24) are fixed once for both.
