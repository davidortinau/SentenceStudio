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
| B1 | Minimal Pairs | **Done** (`f294dfac`, `6db83a7a`). All reads and writes use hard-coded user `"1"`; a real learner's pairs never show | UI `MinimalPair*.razor`, `MinimalPair*Repository` | Minimal Pairs merge (`33ed998f`) |
| B2 | Minimal Pairs | `MinimalPairAttempt` prompt/selected word FKs are RESTRICT; deleting a word with attempts fails (and a synced delete may fail on devices) | Shared schema, both providers | Captain 2026-11-01: leave as is (known risk, no migration) |
| B3 | Vocabulary Matching | **Done** (`bae13dd0`). Uses `Preferences.Get("active_profile_id","")`; empty owner is passed through instead of refused | UI `VocabMatching.razor` | Matching foundation (`a0303765`) |
| B4 | Word Association | **Done** (`fc97b87c`). Writes with an empty profile id when none is active | UI `WordAssociation.razor`, `WordAssociationService` | Word Association merge |
| B5 | Diary | **Done** (`35f86f7b`). Entry update and feedback save don't check the owner | Shared diary repository | Diary merge |
| B6 | Describe a Scene | Vocabulary scoring matches against the whole vocabulary table, not the owner's words | UI `Scene.razor` / scoring call | Scene merge |
| B7 | Writing | **Done** (`0c2007c1`, `0d6914aa`): web stops saving lookups as words. The lookup history is not built: it needs an owner-scoped synced table (schema change, Captain decision). Translate helper saves each AI translation as a `VocabularyWord` with no resource link (unattributable row; on native it blocks all CoreSync uploads). Captain expects a lookup history: design an owner-scoped history instead | UI `Writing.razor` | Native stops saving; history is future work |
| B8 | Reading | **Done** (`0c2007c1`, `49c7f8a0`). Dictionary lookup caches the word inverted (native/target swapped) and with no owner | UI `Reading.razor` | Reading merge |

## Failures recorded as fake results

| # | Activity | Defect | Where |
|---|---|---|---|
| B9 | Translation | Failed AI grade is saved as a 0% attempt | `TeacherService` caller in `Translation.razor` |
| B10 | Describe a Scene | Failed AI grade is saved as a 0% card and activity row | `Scene.razor` |
| B11 | Word Association | **Done** (`fc97b87c`). AI returning no verdicts records a wrong attempt | `WordAssociation.razor` |
| B12 | Cloze | Any AI failure shows "No cloze sentences" silently (`ClozureService` returns an empty list) | Shared `ClozureService` + `Cloze.razor` |
| B13 | Cloze | Sentences where the word can't be blanked are shown with the answer visible | Shared `ClozureService` |
| B14 | Conversation | Empty AI reply adds an empty bubble; start/reply failures are silent | UI `Conversation.razor` |

## AI prompts and models (shared, affects all three)

| # | Activity | Defect | Where |
|---|---|---|---|
| B15 | Conversation | Azure OpenAI content filter rejects the combined "SYSTEM INSTRUCTIONS…TASK…" opening prompt (400), so the chat opens empty. Send instructions as the system role (`/api/v1/ai/chat-messages`) | `ConversationService`, `StartConversation*.scriban-txt`. Web check 2026-11-01: default-persona path opens normally (no 400 in logs); scenario path not reachable for e2e, still unverified |
| B16 | Conversation | `Reply.Comprehension` description asks for 0.0-1.0 but prompt and display expect 0-100, so it shows "0%" (WebApp showed "Comprehension: 1%" for a model 1.0 on 2026-11-01). Fixed in shared code (`bebeebd6`): schema says 0-100, `Reply.NormalizeComprehension` maps legacy fractions at web and native assignment/display; verify on WebApp/MAUI | `Shared/Models/Reply.cs`, prompts, `Conversation.razor` |
| B17 | Conversation | Persona replies include the score text ("(이해도: 85/100)") because the scenario prompt asks for scoring in the reply. Fixed in shared code (`bebeebd6`): opening prompts forbid scores in dialogue, continue prompts put the score only in `comprehension_score`; verify on WebApp/MAUI | `Conversation.scenario.scriban-txt`, `Conversation.system`, `ContinueConversation*.scriban-txt` |

## Activity behavior bugs

| # | Activity | Defect | Where |
|---|---|---|---|
| B18 | Number Drill | **Done** (`44780523`). Listen & Place answers are never counted and show no feedback (0/0 summary) | `NumberDrill.razor` |
| B19 | Number Drill | **Done** (`44780523`). Disambiguate hints never appear (nesting bug) | `NumberDrill.razor` |
| B20 | Number Drill | **Done** (`44780523`). Next during the 1.5 s auto-advance skips an item | `NumberDrill.razor` |
| B21 | Number Drill | **Done** (`44780523`). Mode list order is undefined (query has no ORDER BY) | Numbers repository |
| B22 | Number Drill | **Done** (`44780523`). Summary median and "Patterns to Review" are always empty (service returns 0 ms / no patterns) | `NumberSessionService` |
| B23 | Number Drill | **Done** (`44780523`). No gate for non-Korean target languages (silently runs Korean) | `NumberDrill.razor` |
| B24 | Cloze | Review due dates compared with local `DateTime.Now`; stored dates are UTC | Shared `ClozureService` |
| B25 | Cloze, Translation | `UserActivity.CreatedAt` written with local time; streaks read UTC (verify the Razor pages) | UI pages / `ProgressService` callers |
| B26 | Shadowing | **Done** (`138910b5`): synthesized once, Slow is a playback rate. Slow speed is ignored (synthesize API has no speed); apply as playback rate | `Shadowing.razor`, speech API |
| B27 | Shadowing | **Done** (`138910b5`). Waveform tap-to-seek handler returns the current position (never seeks) | `Shadowing.razor` |
| B28 | Shadowing | **Done** (`138910b5`). Audio synthesis failure is only logged; nothing tells the learner | `Shadowing.razor` |
| B29 | Reading | **Done** (`49c7f8a0`). "Audio generation failed" message is hidden as soon as generation stops | `Reading.razor` |
| B30 | Reading | **Done** (`49c7f8a0`). Single-tap "select sentence" handler never fires (dead code) | `InteractiveText.razor` |
| B31 | How Do You Say | **Done** (`10237d83`). Always re-synthesizes and adds a duplicate history row instead of using the cache | `HowDoYouSay.razor` |
| B32 | How Do You Say | **Done** (`10237d83`); one literal "Audio unavailable." still needs a key (`HowDoYouSay_AudioUnavailable`). History timestamps shown in UTC; raw exception text shown to the learner | `HowDoYouSay.razor` |
| B33 | Translation, Writing | **Writing half done** (`0d6914aa`); Translation half open. Navigation allowed while a grade is in flight, so a late result can land on the wrong sentence | `Translation.razor`, `Writing.razor` |
| B34 | Many | **Partly done**: Minimal Pairs, Vocab Matching, Word Association, Diary, Writing and Reading pages use resx keys (`0d6914aa`, `49c7f8a0`, `c2af4327`). Still needed: a key for the Minimal Pair contrast placeholder ("e.g., ㅂ vs ㅃ") and a web "double-click" variant of `Reading_JumpHint`. Hard-coded English strings (Cloze Familiar messages, Number Drill, Writing, Reading toasts) need resx keys (native added `NumberDrill_*`, `Writing_*`, `Reading_*`, `NativeCloze_*` keys that can be reused) | UI pages |
| B42 | Describe a Scene, Conversation | Done in phase 3 (shared code, docs/sync-all-tables-plan.md): every web learner saw and could change every other learner's scene images and custom conversation scenarios, because `SceneImageService` and `ScenarioRepository` had no owner filter. Both now read and write only the active learner's rows; example sentences gained an owner too. Built-in scenarios have fixed IDs everywhere. Rows from before, with no provable owner, are hidden on the web (D3) | `SceneImageService`, `ScenarioRepository`, `ExampleSentenceRepository` |
| B41 | Vocabulary Quiz | Open (phase 2b second review): completing the plan card from a resumed round summary does not check that the round was scored. Native now requires a learning record (`VocabularyLearningContext`, activity `VocabularyQuiz`) on one of the round's words since the run started before Done completes another client's summary (rule 6 of docs/activity-session-format.md, `NativeQuizAttemptCommitterAdapter.RoundWasScoredAsync`); the web/MAUI page should do the same when it resumes a summary it did not finish itself | `VocabQuiz.razor` resume + plan completion |
| B40 | Vocabulary Quiz | Done in phase 2b (shared code): a quiz saved after a word was mastered mid-round could not be resumed, on the web or anywhere, because the shared resume check required the round's session words to still be in the batch, and the web removes a mastered word from the batch at once. `VocabQuizLaunchValidator.CountRejectedSnapshotReferences` now requires session words to be reachable only. Takes effect on the web when the web app is rebuilt | Shared `VocabQuizLaunchValidator` |
| B39 | Vocabulary Quiz | Done in phase 2b (docs/sync-all-tables-plan.md, E3/E4): quiz runs now sync, so the web/MAUI page saves and resumes the one shared format (docs/activity-session-format.md) and moves past a question another client graded but had not scored, without scoring it. Remaining: run it in the web app against a run saved on a device (needs the test-account sign-in) | `VocabQuiz.razor` |
| B38 | How Do You Say | **Done** (`10237d83`). Audio history now syncs (Phase 2a, docs/sync-all-tables-plan.md D4), so the web lists entries whose clip was made on a phone. Playing one must synthesize the clip again in the entry's voice and point the entry at it (no second entry), like native `NativeHowDoYouSayPresenter.TogglePlayAsync` / `NativeShadowingSpeech.GetForVoiceAsync` | `HowDoYouSay.razor`, web audio cache |
| B37 | Number Drill | **Done** (`44780523`). "I was correct" fixes only the session score; the stored `NumberAttempt` and `NumberMasteryProgress` (SM-2) stay graded wrong. Shared `NumberSessionService.OverrideAttemptAsync` (`c3836877`) re-grades both; keep `NumberAttemptResult.Receipt` from the submit and pass it with the owner in `OverrideAsCorrect`, then show `NumberDrill_OverrideFailed` on Refused (Captain, 2026-11-02) | `NumberDrill.razor` |

## Platform (MAUI app only)

| # | Area | Defect | Where |
|---|---|---|---|
| B35 | Audio cache | **Done for How Do You Say and Number Drill** (`10237d83`, shared `UiSpeechClipCache`); `VocabQuiz.razor`, `VocabularyWordEdit.razor` and `MinimalPairSession.razor` still check absolute paths. Cached clips are stored with absolute paths; on iOS the app container path changes on reinstall, so cached audio is never found and is re-synthesized | MAUI audio cache users (quiz, shadowing, how-do-you-say) |
| B36 | Writing | **Done** by B7 (MAUI uses the same Writing page and service). Same as B7 on MAUI with CoreSync: an unowned `VocabularyWord` row makes the server refuse the device's uploads (403) | MAUI CoreSync |

## Found while backporting (open)

- Reading "Remember this word" saves the word, then links it to the resource; if the link fails, an owned word with no resource is left (the same sync risk as B7). Native uses `CreateAndAttachOwnedVocabularyAsync`; switching changes smart-resource and duplicate behavior, so it needs a decision.
- `UserActivityRepository.SaveAsync` writes a row with an empty `UserProfileId` when neither the item nor the active profile names one (shared code; the backported pages now always set the owner).

- Number Drill on the web wrote attempts through `IAppState`, one singleton shared by every signed-in learner, so it could record under another learner; fixed in `44780523` (uses the circuit's profile).
- Disambiguate's combined answer is normalized before grading, so some wrong-system choices (for example 삼째 날 for 셋째 날) are stored as correct while the page shows them wrong, on every client. Cross-client grading decision.

## Process

- Each item: reproduce in the WebApp (and MAUI where relevant), fix in shared code when possible, add a regression test, run the relevant E2E journey.
- Items marked shared (B12, B13, B15-B17, B21, B22, B24) are fixed once for both.
