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
| B6 | Describe a Scene | **Done** (`90e59be4`): the page captures the owner and scores only that owner's words. Vocabulary scoring matches against the whole vocabulary table, not the owner's words | UI `Scene.razor` / scoring call | Scene merge |
| B7 | Writing | **Done** (`0c2007c1`, `0d6914aa`): web stops saving lookups as words. The lookup history is not built: it needs an owner-scoped synced table (schema change, Captain decision). Translate helper saves each AI translation as a `VocabularyWord` with no resource link (unattributable row; on native it blocks all CoreSync uploads). Captain expects a lookup history: design an owner-scoped history instead | UI `Writing.razor` | Native stops saving; history is future work |
| B8 | Reading | **Done** (`0c2007c1`, `49c7f8a0`). Dictionary lookup caches the word inverted (native/target swapped) and with no owner | UI `Reading.razor` | Reading merge |

## Failures recorded as fake results

| # | Activity | Defect | Where |
|---|---|---|---|
| B9 | Translation | **Done** (`c24515fb`). Failed AI grade is saved as a 0% attempt | `TeacherService` caller in `Translation.razor` |
| B10 | Describe a Scene | **Done** (`90e59be4`). Failed AI grade is saved as a 0% card and activity row | `Scene.razor` |
| B11 | Word Association | **Done** (`fc97b87c`). AI returning no verdicts records a wrong attempt | `WordAssociation.razor` |
| B12 | Cloze | **Done** (`d0b9d117`). Any AI failure shows "No cloze sentences" silently (`ClozureService` returns an empty list) | Shared `ClozureService` + `Cloze.razor` |
| B13 | Cloze | **Done** (`d0b9d117`, shared `ClozureAnswerHelper.TryBlankSentence`). Sentences where the word can't be blanked are shown with the answer visible | Shared `ClozureService` |
| B14 | Conversation | **Done** (`3e3e7f81`). Empty AI reply adds an empty bubble; start/reply failures are silent | UI `Conversation.razor` |

## AI prompts and models (shared, affects all three)

| # | Activity | Defect | Where |
|---|---|---|---|
| B15 | Conversation | **Done** (`3e3e7f81`): instructions go as the system role through `/api/v1/ai/chat-messages`; not yet run against the Azure content filter. Azure OpenAI content filter rejects the combined "SYSTEM INSTRUCTIONS…TASK…" opening prompt (400), so the chat opens empty. Send instructions as the system role (`/api/v1/ai/chat-messages`) | `ConversationService`, `StartConversation*.scriban-txt`. Web check 2026-11-01: default-persona path opens normally (no 400 in logs); scenario path not reachable for e2e, still unverified |
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
| B24 | Cloze | **Done** (already fixed in `4477ac82`; regression test in `d0b9d117`). Review due dates compared with local `DateTime.Now`; stored dates are UTC | Shared `ClozureService` |
| B25 | Cloze, Translation | **Done** (`c24515fb`, `d0b9d117`): rows carry UTC and the owner. `UserActivity.CreatedAt` written with local time; streaks read UTC (verify the Razor pages) | UI pages / `ProgressService` callers |
| B26 | Shadowing | **Done** (`138910b5`): synthesized once, Slow is a playback rate. Slow speed is ignored (synthesize API has no speed); apply as playback rate | `Shadowing.razor`, speech API |
| B27 | Shadowing | **Done** (`138910b5`). Waveform tap-to-seek handler returns the current position (never seeks) | `Shadowing.razor` |
| B28 | Shadowing | **Done** (`138910b5`). Audio synthesis failure is only logged; nothing tells the learner | `Shadowing.razor` |
| B29 | Reading | **Done** (`49c7f8a0`). "Audio generation failed" message is hidden as soon as generation stops | `Reading.razor` |
| B30 | Reading | **Done** (`49c7f8a0`). Single-tap "select sentence" handler never fires (dead code) | `InteractiveText.razor` |
| B31 | How Do You Say | **Done** (`10237d83`). Always re-synthesizes and adds a duplicate history row instead of using the cache | `HowDoYouSay.razor` |
| B32 | How Do You Say | **Done** (`10237d83`) (`HowDoYouSay_AudioUnavailable` added in `77e1006b`). History timestamps shown in UTC; raw exception text shown to the learner | `HowDoYouSay.razor` |
| B33 | Translation, Writing | **Done** (Writing `0d6914aa`, Translation `c24515fb`). Navigation allowed while a grade is in flight, so a late result can land on the wrong sentence | `Translation.razor`, `Writing.razor` |
| B34 | Many | **Done** (`0d6914aa`, `49c7f8a0`, `c2af4327`, `77e1006b`); `VocabQuiz.razor` still has hard-coded English and one raw exception toast. Hard-coded English strings (Cloze Familiar messages, Number Drill, Writing, Reading toasts) need resx keys (native added `NumberDrill_*`, `Writing_*`, `Reading_*`, `NativeCloze_*` keys that can be reused) | UI pages |
| B42 | Describe a Scene, Conversation | Done in phase 3 (shared code, docs/sync-all-tables-plan.md): every web learner saw and could change every other learner's scene images and custom conversation scenarios, because `SceneImageService` and `ScenarioRepository` had no owner filter. Both now read and write only the active learner's rows; example sentences gained an owner too. Built-in scenarios have fixed IDs everywhere. Rows from before, with no provable owner, are hidden on the web (D3) | `SceneImageService`, `ScenarioRepository`, `ExampleSentenceRepository` |
| B41 | Vocabulary Quiz | **Done** (`88692f06`). Open (phase 2b second review): completing the plan card from a resumed round summary does not check that the round was scored. Native now requires a learning record (`VocabularyLearningContext`, activity `VocabularyQuiz`) on one of the round's words since the run started before Done completes another client's summary (rule 6 of docs/activity-session-format.md, `NativeQuizAttemptCommitterAdapter.RoundWasScoredAsync`); the web/MAUI page should do the same when it resumes a summary it did not finish itself. Both now call the one shared check, `QuizRoundScoring.WasScoredSinceAsync` (code review 3) | `VocabQuiz.razor` resume + plan completion |
| B40 | Vocabulary Quiz | Done in phase 2b (shared code): a quiz saved after a word was mastered mid-round could not be resumed, on the web or anywhere, because the shared resume check required the round's session words to still be in the batch, and the web removes a mastered word from the batch at once. `VocabQuizLaunchValidator.CountRejectedSnapshotReferences` now requires session words to be reachable only. Takes effect on the web when the web app is rebuilt | Shared `VocabQuizLaunchValidator` |
| B39 | Vocabulary Quiz | Done in phase 2b (docs/sync-all-tables-plan.md, E3/E4): quiz runs now sync, so the web/MAUI page saves and resumes the one shared format (docs/activity-session-format.md) and moves past a question another client graded but had not scored, without scoring it. Remaining: run it in the web app against a run saved on a device (needs the test-account sign-in) | `VocabQuiz.razor` |
| B38 | How Do You Say | **Done** (`10237d83`). Audio history now syncs (Phase 2a, docs/sync-all-tables-plan.md D4), so the web lists entries whose clip was made on a phone. Playing one must synthesize the clip again in the entry's voice and point the entry at it (no second entry), like native `NativeHowDoYouSayPresenter.TogglePlayAsync` / `NativeShadowingSpeech.GetForVoiceAsync` | `HowDoYouSay.razor`, web audio cache |
| B37 | Number Drill | **Done** (`44780523`). "I was correct" fixes only the session score; the stored `NumberAttempt` and `NumberMasteryProgress` (SM-2) stay graded wrong. Shared `NumberSessionService.OverrideAttemptAsync` (`c3836877`) re-grades both; keep `NumberAttemptResult.Receipt` from the submit and pass it with the owner in `OverrideAsCorrect`, then show `NumberDrill_OverrideFailed` on Refused (Captain, 2026-11-02) | `NumberDrill.razor` |
| B43 | Conversation | **Done** (code review 3, 2026-11-03). When a reply failed or came back empty, the learner's message stayed in the chat and the input was cleared, so sending it again sent it twice. The unanswered message now leaves the chat and goes back to the input box | `Conversation.razor` |
| B44 | Vocabulary Quiz | **Done** (code review 3). After the last words were mastered the page said "All words mastered! Session complete." even when saving failed or the run was left for the client that scored it. It now says so only when the run was saved (`VocabQuiz_AllMasteredSessionComplete`, English and Korean); a failed save shows `NativeCommon_SaveFailed` and keeps the summary, so Continue tries again | `VocabQuiz.razor` |
| B45 | Vocabulary Quiz, How Do You Say | **Done** (code review 3). The offline audio checks looked only at the entry's own file, not the copy this device made of a clip recorded on another device (`entry_<id>.mp3`), so a clip that would play was reported missing | `UiSpeechClipCache.ResolveEntry` |
| B46 | Cloze | **Done** (code review 3, shared). When the plan's focus words gave no sentence that could be blanked, the activity failed instead of trying the regular word pool | Shared `ClozureService.GetSentences` |
| B47 | Number Drill | **Done** (Captain 2026-11-04, decision 5; shared). Disambiguate pages grade each choice exactly, but the shared grader normalized number words, so some wrong-system choices (for example 삼째 날 for 셋째 날) were stored as correct while the page showed them wrong. The grader now grades Disambiguate exactly per choice, both-or-nothing, on every client. Past attempts are unchanged | Shared `KoreanNumberAnswerGrader` |
| B48 | Reading | **Done** (Captain 2026-11-04, decision 6). "Remember this word" saved the word, then linked it to the resource; a failed link left an owned word with no resource. It now uses the native one-step `CreateAndAttachOwnedVocabularyAsync`: a word the learner already has is linked, not duplicated, and a refused link saves nothing. A word the resource already has (for example synced in after the page loaded) is shown as in vocabulary with "Already in <resource>." (`Lookups_AlreadyPresent`), not as "Could not save word"; same on native | `Reading.razor` |
| B49 | Translation | **Done** (shared). An AI failure while loading sentences showed "No sentences returned"; it now propagates, so the page shows its load-failed error. Also removed the `_words` field that `TranslationService` and `ClozureService` (singletons on the web) kept from the last request of any learner | Shared `TranslationService`, `ClozureService` |
| B50 | Vocabulary Quiz | **Done**. Hard-coded English (Learning Details labels and reasons, button titles, choice labels, sentence-grading toasts, feedback) and one toast with raw exception text now use resource keys in English and Korean, the same keys as the native Learning Details. Icon buttons gained accessible names | `VocabQuiz.razor` |
| B51 | Profile (API) | **Added, no web/MAUI change** (`0739f1b6`, decision 8d). New owner-scoped `PUT /api/v1/profile/me` (owner from the token only; name, languages, session length, level; Email and API keys never read or written) and `UserProfileRepository.SaveEditableProfileForOwnerAsync`. The web Profile page, the MAUI app and the existing `/api/v1/profile/{id}` routes behave as before; the MAUI and a future Flutter client can use `/me` for profile edits, which avoids the existing PUT's read-then-write race on the API keys. Needs the API redeployed before native Save works against a server. Code review: the profile choices (languages, display languages, session minutes, CEFR levels) now have one definition, `SentenceStudio.Contracts.Profiles.LearnerProfileOptions`, read by the web `Profile.razor` and `Onboarding.razor` (same values and order, no visible change), native onboarding/profile and `/me`; a field equal to the stored value is accepted even when it is not listed (an older language, a 25-minute session, other casing), so only a changed value must be a listed choice | API `ProfileEndpoints`, Shared `UserProfileRepository`, Contracts `LearnerProfileOptions`, UI `Profile.razor`, `Onboarding.razor` |
| B52 | Settings (theme colors) | Open, not a regression: the web's own exact theme colours include low-contrast pairs that native now matches (decision 8b): Vapor light `.card-ss` (`--bs-tertiary-bg` `#F8F9FA`) under body text `#32FBE2`; Slate light cards (`#F8F9FA`) under `#AAAAAA`; Flatly and Sketchy dark `--bs-primary` marks on the dark page. A contrast fix belongs in `app.css` (and then the shared native table) and needs the Captain's call | `app.css`, Bootswatch overrides |
| B53 | Writing, Reading, Vocabulary | **Done** (lookup history, decision 4 B, 2026-11-04; follows B7/B36). Lookups were not kept (Writing) or kept without their translation (Reading's activity row). Writing's translate helper and Reading's word lookup now record each successful lookup with its translation and direction in the synced `VocabularyLookup` table (docs/sync-all-tables-plan.md, "Lookup history"), after it is shown; Reading keeps its activity row. New page `/vocabulary/lookups` ("Recent lookups", from the Vocabulary header and overflow menu) lists the newest 100 with "Add to vocabulary" into a resource (`CreateAndAttachOwnedVocabularyAsync`). Same on native. Not built: deleting a lookup (needs decision 2's owner-checked deletes). A lookup is recorded only when its direction is known: a match on the learner's own word, or the Translate prompt's English for a learner whose native language is English (shared `VocabularyLookupRepository.DirectionFor`); otherwise the translation is shown and nothing is recorded, so Add to vocabulary can never swap the sides | `Writing.razor`, `Reading.razor`, `RecentLookups.razor`, `Vocabulary.razor`, `TranslationService.TranslateLookupAsync`, Shared `VocabularyLookupRepository` |
| B54 | Sync (MAUI) | **Done** (decision 1, 2026-11-04; shared and AppLib). The MAUI heads synced without the owner header, so they downloaded every learner's rows and could write any row; once the API refuses headerless sync they could not sync at all. `AddOwnerCheckedSync` (called by `SentenceStudioAppBuilder`) sends the bearer and owner header from one verified token (owner = active profile, else nothing is sent), keeps device-only and other learners' rows on the device, queues and re-requests newly synced tables. The install's store binds to its first learner; a second account is refused (403) and works offline | AppLib `AddOwnerCheckedSync`, `SignedInSyncOwnerSource`, `OwnerCheckedSyncResendRequester`; Shared `SyncOwnerHeaderHandler`, `OwnerCheckedSyncProvider`, `SyncUploadRules`, `SyncRowOwners`, `SyncService` | Native uses the same `SyncUploadRules` and token parser |
| B55 | Sync (API) | **Done** (decisions 1 and 2, 2026-11-04). `Sync:AllowHeaderlessClients` (closed by default) refuses every headerless CoreSync request (#204). One row the server could not place refused a whole upload and stopped that device's sync; now only a malformed row or a row naming another learner in its owner column refuses it, and the rest (including rows the server only infers are another learner's) is left out on the device. Uploaded deletes apply children first, before writes. An installed MAUI app's store could never be bound (every historical row had to prove the owner); now the claim is refused only when the history names other learners only | API `SyncAccessOptions`, `NativeOwnerSyncProvider`, `NativeSyncStoreBindings` | Same server for native |
| B56 | Sync (deletes) | **Done** (decision 2, 2026-11-04). Device deletes were refused (native kept them on the device), MAUI's were applied unchecked, and web deletes never reached any device. A device delete now applies only to the learner's own row (never a profile; a word only when no other learner uses it), and every delete (web, cascade, device) reaches only that learner's other devices through the server-only log `SyncDeletion` (migration `20261104162953_AddSyncDeletionLog`, PostgreSQL only) | API `NativeOwnerSyncProvider`, migration; Shared `SyncUploadRules` | Native sends deletes again (`fccdd681` kept word-link deletes local in between) |

## Platform (MAUI app only)

| # | Area | Defect | Where |
|---|---|---|---|
| B35 | Audio cache | **Done** (`10237d83`, `af88ea47`, `76c15d61`; shared `UiSpeechClipCache`). Cached clips are stored with absolute paths; on iOS the app container path changes on reinstall, so cached audio is never found and is re-synthesized | MAUI audio cache users (quiz, shadowing, how-do-you-say) |
| B36 | Writing | **Done** by B7 (MAUI uses the same Writing page and service). Same as B7 on MAUI with CoreSync: an unowned `VocabularyWord` row makes the server refuse the device's uploads (403) | MAUI CoreSync |

## Found while backporting (open)

- Fixed as B48: Reading "Remember this word" now creates and links in one step.
- Number Drill Disambiguate offers spacing-only variants as wrong choices (for example 삼층 for 삼 층, 세시 for 세 시). Attached spellings like these are accepted Korean, so exact grading marks a valid spelling wrong (the page already did). Needs a content decision.
- Fixed (2026-11-04): `UserActivityRepository.SaveAsync` wrote a row with an empty `UserProfileId` when neither the item nor the active profile named one; it now refuses the write (returns -1, logged).

- Number Drill on the web wrote attempts through `IAppState`, one singleton shared by every signed-in learner, so it could record under another learner; fixed in `44780523` (uses the circuit's profile).
- Fixed as B47: Disambiguate is graded exactly per choice on every client.

- Fixed as B49: Translation sentence loading reports AI failures.
- Fixed as B49: `ClozureService` and `TranslationService` no longer keep the last call's words in a shared field.

## Process

- Each item: reproduce in the WebApp (and MAUI where relevant), fix in shared code when possible, add a regression test, run the relevant E2E journey.
- Items marked shared (B12, B13, B15-B17, B21, B22, B24) are fixed once for both.
