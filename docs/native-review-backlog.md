# Native review backlog

Review findings recorded instead of fixed during a review loop (correctness items are fixed;
these wait for a later pass). One line each: where, then the point.

## Import Content (iOS, review of 4e1921a9..29d1fe54, 2026-11-06)

- `src/SentenceStudio.Native/Features/Import/ImportContentCoordinator.cs:736`: after a commit the target-resource list is read right away (one more redraw and a database read the result page does not show); it could be read lazily when Import more returns to the wizard.
- `src/SentenceStudio.Native.iOS/Features/Import/ImportContentView.cs:62`: `Page` detaches the previous draw's rows grid as a side effect of drawing; that move belongs to the scene, which owns the drawn nodes.
- `src/SentenceStudio.Native/Features/Import/ImportServiceText.cs:50`: the service's English is mapped back to keys with literals and regexes, which break silently when the service text changes; the service should return codes and arguments (B74).
- `src/SentenceStudio.Native.iOS/App/ApplicationScene.Import.cs:53`: `_importDrawn.Clear()` before the first draw is redundant (`ImportContentView.Page` clears it).

## Vocabulary Quiz autoplay and launch (review of `48c97189..4e1921a9`, 2026-11-06)

- **9. Off-main-thread work and Android parity of Back while loading.** `src/SentenceStudio.Native.iOS/App/ApplicationController.Quiz.cs:612` (StartQuizAsync), `src/SentenceStudio.Native.Android/AndroidSliceShell.cs:356`. Moving database work off the main thread at one call site was the wrong altitude. That Task.Run is reverted: iOS has a UIKitSynchronizationContext, and the presenter is not thread-safe. If quiz database work must leave the UI thread, do it in the presenter or data layer for every command (Open, Retry, Resume, Start fresh, Next). Android also has no queued Back while the quiz loads; decide whether it should match iOS.
- **10. Autoplay request workflow copied in both heads.** `src/SentenceStudio.Native.Android/AndroidSliceShell.Quiz.cs:74` and `src/SentenceStudio.Native.iOS/App/ApplicationController.Quiz.cs:292`. Prompt-shown dedupe, one request per turn, the linked token and its compare-and-clear are repeated in both heads (AGENTS.md: no platform copies of workflows). A presenter-owned `PromptShown(turnId)` that keeps the request's cancellation would remove both copies.
- **11. Android timing logs bypass ILogger.** `src/SentenceStudio.Native.Android/AndroidSliceShell.cs:360` and `src/SentenceStudio.Native.Android/AndroidSliceShell.Quiz.cs:81` use `Android.Util.Log.Info` instead of the head's `ILogger` pipeline (AndroidServiceHost), so the "quiz opening" and "prompt on screen" lines skip the logging configuration the presenter's "autoplay started/finished" lines go through.
- **13. Effect keys from identity hashes are not unique.** `src/SentenceStudio.Native.Android/AndroidEffectKey.cs:32`. `RuntimeHelpers.GetHashCode` can repeat for two live instances, so "a new instance restarts the effect" is not guaranteed (a photo render report or viewer focus could be skipped). A per-instance sequence number or reference tracking would be exact.
- **14. Effect keys allocate on every quiz recomposition.** `src/SentenceStudio.Native.Android/AndroidSliceShell.Quiz.Compose.cs:34,50`. `AndroidEffectKey.Of` runs twice per recomposition (each keystroke in the answer field): a params array, a boxed bool, escaped strings and a join, even when the key is unchanged. Typed overloads or a cached key would avoid it.

## Vocabulary Quiz "Use in a sentence" on web and MAUI (review of `935c17d7`, B70, 2026-11-06)

- **8. One shared VocabQuiz page factory for tests.** `tests/SentenceStudio.UI.Tests/Activities/VocabQuizSentenceShortcutTests.cs:230` (`AddQuizServices`, a leasing timer), `tests/SentenceStudio.UI.Tests/Activities/WordAudioCacheTests.cs` (`CreatePage`), `tests/SentenceStudio.UI.Tests/Audio/QuizAudioPersistenceTests.cs` (`CreateQuiz`) and `tests/SentenceStudio.UI.Tests/Activities/VocabQuizResumedSummaryTests.cs` (`ResumeSummaryAsync`) each wire the quiz page their own way. One harness factory that registers the quiz's services and opens it on seeded words would keep them in step.
- **10. Fallbacks for a word with no target term.** `src/SentenceStudio.UI/Pages/VocabQuiz.razor:1985` (`GetSentenceShortcutInstruction` falls back to an empty string) and `:1988` (`GetSentenceShortcutPlaceholder` falls back to the hard-coded English "the word"); the native card does the same (`src/SentenceStudio.Native.iOS/Features/Quiz/QuizSentenceView.cs:62`). They disagree, and "the word" is not localized. A word without a target term should not reach the shortcut; decide one rule and use a localized string if a fallback stays.

## Vocabulary Quiz prompt audio on web and MAUI (review of `947cd99e`, B70, 2026-11-06)

- `src/SentenceStudio.UI/Pages/VocabQuiz.razor:2907`: the `ObjectDisposedException` catch in `CancelPromptPlayback` is dead (the finally clears `_promptPlayback` before it disposes that source, so a disposed source is never cancelled), and `_audioDisposed ||` at `:2826`, `:2852` and `:2857` is redundant (every path that sets it also cancels `_audioLifetime`, which the playback token is linked to).
- `tests/SentenceStudio.UI.Tests/Activities/VocabQuizSentenceShortcutTests.cs:518` (`OpenQuizAsync` word seeding) and `:602` (`LeasingTimer`): the seeding and the timer fake are repeated in `tests/SentenceStudio.UI.Tests/Activities/VocabQuizResumedSummaryTests.cs` and `tests/SentenceStudio.UI.Tests/Audio/QuizAudioPersistenceTests.cs`; they belong with the one quiz page factory of item 8 above.
- `tests/SentenceStudio.UI.Tests/Activities/VocabQuizSentenceShortcutTests.cs:525`: when setup after `ActivityPageHarness.CreateAsync` throws (seeding, render, or the wait for the first turn), the harness is never disposed, so its SQLite connection, renderer and temp AppData directory leak for the rest of the run.
- `src/SentenceStudio.UI/Pages/VocabQuiz.razor:2018`: the placeholder still falls back to the hard-coded English "the word" (item 10 above; not fixed here).
- `src/SentenceStudio.UI/Pages/VocabQuiz.razor:2096` and `:2178` (seen while fixing, not in the review): sentence grading failures log the learner's sentence and target word (the second at Error); like the audio log, they should log the failure type only.
- `src/SentenceStudio.UI/Pages/VocabQuiz.razor:3051` (seen on the branch WebApp, not in the review): leaving the quiz by navigation after a browser clip played logs "Could not stop browser quiz audio" with a `JSDisconnectedException` from `DisposeAsync`, because the circuit is already gone; the browser page and its audio are gone too, so it is a Warning for nothing.

## Vocabulary word ownership (one runtime rule, `b88a4dd4`, 2026-11-06)

Resolved by B81-B84 (owner-only words, 2026-11-06): the Coach remove-word orphan case (a word recorded to no one is no longer found by the Coach), save/upload stamping (removed), `AvailableTo` (owned-only), `GetResourcesForVocabularyWordAsync` (the learner's resources only) and `SaveWordAsync` writing to a word recorded to no one (refused). Still open:

- `src/SentenceStudio.Shared/Migrations/20261104162953_AddSyncDeletionLog.cs` (`sync_deletion_by_word`): the deletion log still names a deleted ownerless word's owner from its links and progress, to choose which devices receive the delete. It decides delivery, not permission; a later migration could make it recorded-only.
- `src/SentenceStudio.Api/Sync/NativeSyncStoreBindings.cs:148`: the check that a store's earlier headerless history names only this owner sees fewer owners (words are no longer inferred); the other tables still name them.
- Production data (2026-11-05 dump): 213 ownerless words are linked only from 2 resources that have no owner and 28 only by a deleted account's progress; they are now hidden from everyone (rows kept). Deciding whose the 2 resources are (the Captain's call, like the audio rows) would let the backfill record the 213. The 96 shared words are recorded to the Captain by `scripts/record-shared-vocabulary-words.sql` before the hiding change ships (runbook "Ownerless shared words and withheld rows").
- Devices: a device's local copy of a word keeps whatever owner it last downloaded. Words the server recorded at the 2026-11-05 deploy reach every device whose anchor predates that stamping; a word an older build uploads without an owner is touched back by the server. A word the device holds with no owner that the server never re-sends (it did not change since the device's anchor) stays hidden on that device until the server row changes; nothing is deleted.

## Owner-only words (B81-B84, 2026-11-06): cleanup and altitude

- `src/SentenceStudio.Shared/Data/VocabularyEncodingRepository.cs`, `src/SentenceStudio.Shared/Data/VocabularyLearningContextRepository.cs`, `src/SentenceStudio.Shared/Repositories/MinimalPairSessionRepository.cs`, `src/SentenceStudio.Shared/Services/VocabularyFilterService.cs`: registered or present but with no callers; their word reads are unscoped (no owner at all). Delete them, or scope them, before anything calls them. `VocabularyEncodingRepository.SaveAsync` would now be refused by the save guard for a new word (no owner).
- `src/SentenceStudio.Shared/Data/VocabularyProgressRepository.cs` (`GetByWordIdsAsync`): still returns every learner's rows for the IDs (its callers filter afterwards); a scoped overload exists (`GetByWordIdsForUserAsync`).
- `src/SentenceStudio.Shared/Services/ContentImportService.cs` (Update mode "shared word" detection) still decides a sentence's owner by its resource and still clones a word other learners' rows use; under the owner-only rule the reuse pool is the learner's own words, so the clone path is only reachable for legacy shared rows. Simplify with the import-hardening work.
- `src/SentenceStudio.UI/Pages/Vocabulary.razor:833` reads `w.ExampleSentences?.Count`, which the list query never loads, so the encoding label always counts 0 sentences (pre-existing).
- `src/SentenceStudio.UI/Pages/ResourceAdd.razor`: a refused creation shows the status code (`Invalid`, `AlreadyExists`) inside the localized "Failed to save" text; a specific localized message per status would be better.
- `src/SentenceStudio.Native/Adapters/NativeOwnerStoreWriteGuard`: still exempts `VocabularyWord` (the save guard now covers new words; updates keep their stored owner).
- `src/SentenceStudio.Native.iOS/App/ApplicationController.Quiz.cs:~1010`: a Debug-only preview read of words is unscoped.
- `src/SentenceStudio.Shared/Data/ExampleSentenceRepository.cs` (`CreateFromReadingIfNewAsync(string)`): loads the word by ID with no owner check, so a sentence could be recorded to the learner on a word they cannot see (it would stay hidden with the word). Scope it with the import-hardening work, which owns this file.
- `src/SentenceStudio.Shared/Services/SmartResourceService.cs`: the struggling, phrases and sentences selections read progress through `VocabularyProgressRepository.ListAsync()`, which uses the device's active learner, then filter by the learner passed in; on a host where those differ (the web) they come back empty. Pre-existing; pass the learner through.
- Smart resources were created twice for a new learner on the isolated E2E stack (2026-11-06: two "New Words Practice", "Struggling Words", "Phrases" and "Sentences" for e2e@test.local after its first web sign-in); the initialization looks racy. Pre-existing, not caused by the owner rule.
- `tests/SentenceStudio.UnitTests/AppOperations/ApplicationOperationConcurrencyTests.ConcurrentConfirmationUse_ConsumesExactlyOnce` failed once in a full run and passed three times alone (timing flake, unrelated).
- `SyncRowOwners.GetStoredWordOwnersAsync` is a named pass-through to `DirectOwnersAsync` (review finding 14); kept as the one name for "a word's recorded owner".

### Seen fixing the review of `bf701c42` (B85, 2026-11-06)

- `src/SentenceStudio.Native/Adapters/NativeResourceStore.VocabularyBrowser.cs` (`HasConstituentLinks`, the save's type-change check): any part link counts, a hidden part's included, so the native editor refuses a type change ("This word has constituent links") on a word whose parts it shows none of. Safe (nothing is deleted), but the message is confusing there.
- Plans and the quiz pools disagree on unlinked words: `GetDueVocabularyAsync` and `GetDueVocabCountAsync` take only words one of the learner's resources links (the Brot guard B85 now relies on), while the all-words quiz and the Vocabulary page show every word of theirs. A due word the learner unlinked from every resource is never in Today's Plan. Decide whether the plan should follow the owner-only rule too.
- `ContentImportService.CommitImportAsync` and `VideoImportPipelineService.CreateLearningResourceAsync` reuse only the learner's words that one of their resources links, so importing a term the learner already has outside every resource creates a duplicate word.
- Sam's write receipts are English except the new kept-word reply of remove-word (`Coach_VocabularyRemoval_*`, by the learner's `DisplayLanguage`); the rest (and the removal preview, which still says "Remove ... from your vocabulary" before the delete decides) are English only, as before.

## Import Content example sentences and retries (review of be58487f..548b9a61, 2026-11-06)

- `src/SentenceStudio.Shared/Services/ImportExampleSentencePolicy.cs`: a comma list of target-language words (`사과, 바나나, 포도`) can still be kept as an example; it shows no meaning, but it is not a sentence. The prompts ask for no example there.
- `src/SentenceStudio.Shared/Services/KoreanPredicateMatcher.cs`: honorific replacement verbs (드시다 for 먹다, 주무시다 for 자다, 계시다 for 있다) and a stem written without a space after the word before it (잘먹었어요) are not found, so those rows keep no example. `TranscriptSentenceHarvestService` still has its own plain-substring copy of the word check.
- `src/SentenceStudio.Shared/Services/KoreanPredicateMatcher.cs` (B72 third review): on an exact stem match any ending's first syllable counts as an ending, so 보고서 (report) is found for 보다, 가게 and 가지 (shop, eggplant) for 가다, and 돼지 (pig) for 되다. The cheap-looking rule (the rest of the word is a known ending, then particles) needs an ending grammar: tense and honorific combinations (셨겠어요), auxiliaries written as one word (먹어봤어요, 해주세요) and -고서, a real ending, so 보고서 cannot be told from 먹고서 without a dictionary. Truly ambiguous words (가게, 가지) would stay accepted either way.
- Web run 2026-11-06: the AI returned no `exampleSentenceTranslation` for imported examples (the structured-output schema comes from the running API build); recheck after the API deploy, and if still empty, decide whether a missing translation should be filled by the translation step.
- B76 lists the other hand-rolled execution-strategy transactions (`LearningResourceRepository.DeleteVocabularyWordAsync`, `PlanService.ExecuteRevisionUnitAsync`, `VocabularyPartOfSpeechBackfillService.CommitBatchAsync`, the app-operation store and maintenance); none has a `verifySucceeded` check except `DeleteResourceAsync`. Not migrated.
- Resolved by B82 and the owner-only merge (2026-11-06): `ContentImportService.CommitImportAsync` (`existingUserWords`) and `VideoImportPipelineService.CreateLearningResourceAsync` (now inside `RetryableTransaction`) reuse only words recorded to the learner; any other term becomes a new word recorded to them.
- `tests/SentenceStudio.UnitTests/Abstractions/AuthTokenStoreTests.cs` (`Logging_NeverIncludesTokenValuesOrLengths`): fails in a checkout whose path contains the token length digits (this worktree, `...b36d`), because the log text includes the path.

## YouTube channels and import history (iOS, review of `548b9a61..527c6a50`, 2026-11-06)

- **API timestamps are host-local.** `src/SentenceStudio.Api/Program.cs:76` (also WebApp and Workers) sets `Npgsql.EnableLegacyTimestampBehavior`, so `timestamp with time zone` values are read back as host-local `DateTime` (Kind Local) and model JSON carries the host's offset (for example `MonitoredChannel.LastCheckedAt` from `/api/channels`). It predates this branch and stays out of scope; the import history now converts to UTC (`VideoImportResponse`) and the native client compares instants, but every other endpoint still returns host-local times.
- **A second retry starts a second pipeline.** `src/SentenceStudio.Api/ImportEndpoints.cs` (`RetryImport`) and `VideoImportPipelineService.RetryImportAsync` take an in-progress import as stuck from its `CreatedAt`, which a retry does not change, so a just-retried import can be retried again at once and two pipelines run on the row. Kept as the source has it; the native history now hides Retry for a row it retried until the row fails or is stuck counting from the retry.

## Conversation voice mode (iOS, review of `d8df0599..c55822d8`, 2026-11-06)

- **The API fetches a token and builds a realtime client for every secret.** `src/SentenceStudio.Api/RealtimeEndpoints.cs` (`RealtimeClientSecretIssuer.MintAsync`) calls `DefaultAzureCredential.GetTokenAsync` and constructs a new `OpenAI.Realtime.RealtimeClient` per request, outside the resilient `openai` HttpClient that the chat clients use. In Azure the managed identity caches its token; locally `AzureCliCredential` runs `az` each time (about 1-2 s per voice start), and a transient Foundry 5xx is not retried (the learner gets "Voice could not start" and can tap again; the failed attempt no longer counts against the 10-per-5-minutes budget).
- **Starting the audio holds the presenter lock.** `NativeConversationPresenter.StartVoiceAsync` awaits `INativeVoiceAudio.StartAsync` inside the gate, so Back waits for the audio session to activate (a few hundred ms). Moving it outside needs one more generation check.
- **Keyboard after Return.** The SwiftUI bridge has no focus control or `submitLabel`, so the keyboard closes after each sent message (Messages keeps it open) and Return shows the plain return key, not "send". Both need small `Microsoft.SwiftUI` additions (`.focused`, `.submitLabel(.send)`).
- **Android has no voice yet.** The shared presenter offers voice only when the head registers `INativeVoiceAudio`; Android has none yet (AudioRecord/AudioTrack with the echo canceller; the Captain approved it on 2026-11-07). Its composer follows iOS since 2026-11-07 (no Send button).

Review of the voice pace and read-aloud change (2026-11-06; correctness findings fixed in the same commit):

- **The pace log reads the whole session.** `ConversationVoiceSession.LogPace` writes the `session.updated` model to JSON and parses it to read speed and silence. It runs once per pace change and never logs content, but the typed `RealtimeServerUpdateSessionUpdated.Session` properties would avoid the copy (it includes the instructions with the learner's recent turns).
- **Three copies of the checked menu row.** `ConversationScreen` builds a menu row with a checkmark, the selected state and an id in `ScenarioMenu`, `LanguageMenu` and `PaceMenu`; one `CheckedMenuItem(label, active, id, action)` helper would keep them alike.
- **A pace change racing End.** `SetPaceAsync` is skipped once the session is disposing, but a send already on the socket when End runs can fail and log a warning. Harmless; noted only.

## Conversation composer (Android, 2026-11-07)

- **iOS has no composer automation ID.** Android tags the Messages-style composer row
  `native.conversation.composer` (`NativeConversationAccess.Composer`); the iOS `ConversationScreen`
  composer has no identifier. Add `.WithAccessibilityIdentifier(NativeConversationAccess.Composer)`
  there so both heads expose the same IDs. The unused `native.conversation.send` ID was removed.
- **Scenario fallback on iOS.** `ConversationScreen.ScenarioMenu` uses `ActiveScenarioId ?? Scenarios[0].Id`,
  so an id that is no longer in the list shows no check and an empty name. Android now falls back to
  the first scenario, as the source picker did.

## Import Content (Android, review 2026-11-07)

- **Every keystroke rebuilds the preview rows.** `AndroidSliceShell.ImportContent.Compose.cs` reads
  `ImportContentCoordinator.State` on each draw, which copies every preview row, and rebuilds the
  lazy list's items; iOS keeps its drawn rows until `RowsVersion` changes. A Compose equivalent
  needs per-row text state (a cached row with an old `Target` would reset the field), so it waits
  until a long preview shows typing lag.
- **Two dropdown helpers.** `ImportChoice` repeats `MoreDropdown` (`AndroidSliceShell.Settings.Compose.cs`)
  with its own `_importMenu`, without `MoreDropdown`'s one-line ellipsis for long option labels.
- **Shell logging.** The Android shell logs with `Android.Util.Log` (the More screens, Import
  Content), not `ILogger<T>` templates, so these lines do not reach structured logs.

## YouTube import (Android, review 2026-11-07)

- **Taps draw twice.** `YouTubeEdit` and `ChannelEdit` call `RenderMoreNow` after coordinator calls
  that already raise `Changed`; only the bound text inputs (link, channel URL, transcript) need it.
- **Two button factories.** `YouTubeBusyAction` repeats `Action` (`AndroidSliceShell.Visual.cs`) plus a
  spinner; an optional `busy` on `Action` would cover both.
- **Badge colors.** The info badge `#0DCAF0` and the black and white badge text are literals in the
  Import Content and YouTube badges; palette tokens (Info, OnWarning) would keep them in one place.
- **Channel form language list.** Rebuilt from `Constants.Languages` on each draw (each URL keystroke).
- The refused-open and failure logs use `Android.Util.Log` (see the Import Content note above).

## Conversation voice (Android, review 2026-11-07)

- **An audio underrun ends the partner's turn early (iOS too).** Playback idle fires as soon as the
  queued partner audio has played; on a slow network the queue can empty mid-turn, so the
  microphone reopens after the quiet tail while the partner is still speaking. Both heads follow
  the same rule (`AndroidVoiceAudio.Playback`, iOS `NativeVoiceAudio.OnBufferPlayed`); a short grace
  before idle, or idle on the session's response-done event, would fix both.
- **A dead AudioTrack is not rebuilt.** After an audio route or server change every write fails;
  Android logs it once and the partner stays silent until voice is restarted.
- **No live region.** The Compose binding has no live-region semantics, so TalkBack does not
  announce the voice status changes (Connecting, speaking, Listening) on its own.
- **Invisible untinted icons.** An `Icon` without `Tint` inside a TextButton, OutlinedButton or
  DropdownMenuItem draws invisibly in this binding (fixed on Conversation, Import Content and
  YouTube). Still untinted: `AndroidSliceShell.Scene.Compose.cs` (Delete, Add) and
  `AndroidSliceShell.NumberDrill.Compose.cs` (PlayArrow).

