# Saved activity runs: the shared format

Captain, 2026-11-03: one saved format for **every** client: the web app, the .NET
MAUI apps, the native iOS/Android/macOS apps, and later the Flutter port. No
client translates another client's format.

This file is the contract. The sample files in
[`tests/fixtures/activity-session/`](../tests/fixtures/activity-session) are part
of it: every client's tests read them. The C# implementation is
`VocabQuizSavedState` (`src/SentenceStudio.Shared/Models/VocabQuizSavedState.cs`),
tested by `VocabQuizSavedStateContractTests`. A client in another language (the
Flutter port, Dart) implements this file and runs the same samples.

Today only the Vocabulary Quiz saves a run. A later activity that saves runs in
`ActivitySession` uses this row shape and documents its own `stateJson` here.

## The row (`ActivitySession`)

Synced between all clients by CoreSync (phase 2b of `docs/sync-all-tables-plan.md`).

| Column | Meaning |
|---|---|
| `Id` | `as:<userId>:<activityType>:<launchContextKey>`, built by `SyncIds.ActivitySession`. One row per learner + activity + launch, on every client. A later run of the same launch reuses the row (back to `InProgress`, new `StartedAt`); the finished run's record is first copied to its record row, `<run ID>@<StartedAt in UTC, yyyy-MM-ddTHH:mm:ssZ>` (`SyncIds.ActivitySessionRecord`, whole seconds). Every client derives the same record ID, so a run revived on two clients keeps one record. If a row with that ID already holds a different state (another run started in the same second), the record gets a new GUID instead. Rows saved before 2026-11-03 may have a GUID; they keep it. |
| `UserId` | The learner. Required; a client never reads or writes a row without the active learner. |
| `ActivityType` | `VocabularyReview` for the quiz. |
| `LaunchContextKey` | `plan=<plan item>|focus=<word ids>|res=<resource ids>|due=<true/false>|skill=<skill>`, IDs sorted and comma-separated (`VocabQuizSessionSnapshot.BuildLaunchContextKey`). Every client must build the same text for the same launch. |
| `StateJson` | The saved state below. |
| `Status` | `InProgress`, `Completed` or `Abandoned`. At most one `InProgress` row per learner + activity + launch. |
| `StartedAt`, `UpdatedAt`, `CompletedAt` | UTC. |

When two clients save the same run, the most recent save (`UpdatedAt`) wins. A
device that saved while offline and uploads later does not overwrite a newer save
from another client: the server keeps the newer copy and sends it back
(`ActivitySessionUploadMerge`, like Number Drill mastery). Clients therefore stamp
`UpdatedAt` with the time of the save, in UTC.

Two clients can also hold *different* runs of one launch: each started it before
they synced, or each had runs of it before runs synced. Their `StartedAt` differs
(to the second). The newer copy still takes the run row, and the server keeps the
other run as its record row (above), marked `Abandoned` if it was in progress
(one run in progress per launch). That record reaches every client; no run is
dropped.

### Device-only rows

The native quiz also writes per-answer scoring receipts into this table
(`ActivityType` `VocabularyReview.NativeAttemptReceipt.v1` or
`VocabularyReview.NativeSentenceReceipt.v1`, `Id` starting `receipt:`). They
prove what that device already scored. They are never uploaded, and the server
refuses them. Other clients never see them and must not write them.

## The Vocabulary Quiz state (`StateJson`)

JSON, camelCase field names. Unknown fields are ignored, so a newer client may
add an optional field. Changing what a field means needs a new `version`; a
client refuses a version it does not know.

| Field | Type | Required | Meaning |
|---|---|---|---|
| `version` | number | yes | `1`. |
| `userId` | string | yes | Must equal the row's `UserId` and the active learner. |
| `planDate` | `yyyy-MM-dd` | yes | The learner's local day of the run. The native apps resume only a run of today. |
| `launchContextKey` | string | yes | Must equal the row's. |
| `planItemRowId` | string | no | The run's `DailyPlanCompletion` row, if the saving client knows it. Plan rows are created per client, so a client compares it only with its own saves. |
| `adHocPlanItemId` | string | no | The ad-hoc plan item of a quiz started outside the plan, if the saving client made one. A client that resumes without it makes its own. |
| `isRepractice` | bool | no | Re-practice of a completed plan card; omitted when the saving client doesn't track it. |
| `displayDirection` | string | no | `TargetToNative`, `NativeToTarget`, `Mixed`, or `""` (unknown). Informational: the setting is per device. A resumed question not yet answered follows the resuming device's setting. |
| `turnId` | string | yes | 32 hex digits; a new one for every question. |
| `snapshot` | object | yes | The quiz position (below). |
| `pendingAttempt` | object | no | An answer graded but not yet scored (below). |
| `requiresCorrection` | bool | no | A wrong typed answer that must be retyped. |
| `summaryVisible` | bool | no | The round summary is showing. |
| `elapsedTicks` | number | no | Time spent, in 100 ns ticks. |
| `sentenceSubmissionId` | string | no | A sentence-writing turn in progress (32 hex digits). |
| `sentenceSourceInput` | string | with the above | The learner's sentences. |
| `sentenceResults` | array | no | Graded sentences (`sentence`, `usageCorrect`, `usageExplanation`, `accuracy`, `fluency`, `accuracyExplanation`, `fluencyExplanation`, `recommendedSentence`, `grammarNotes`, `gradeUnavailable`, `targetUsedForm`, `hasVerifiedTargetUse`). |
| `sentenceCreditApplied` | bool | no | The sentence turn's credit is included in the snapshot's counters. |
| `turnScoredElsewhere` | bool | no | The current question (`turnId`) was answered and scored on another client; the saving client moved past it, so it holds no receipt for it. |
| `savedBy` | string | no | The client install that saved this state (`native:<id>`). Omitted by clients that never save a graded, unscored answer (web, MAUI). |

`snapshot` (`VocabQuizSessionSnapshot`): `planItemId`, `focusVocabularyIds`,
`resourceIds`, `dueOnly`, `skillId`, `batchPool` (per word: `wordId`,
`sessionCorrectCount`, `sessionMCCorrect`, `sessionTextCorrect`,
`pendingRecognitionCheck`, `lostKnownThisSession`, `wasCorrectThisSession`,
`isDueOnlySession`, `requiresFullSessionDemonstration`, `useKnownWordShortcut`,
`recognitionDemonstrationsBaseline`, `productionDemonstrationsBaseline`),
`roundWordOrder`, `roundCursor`, `currentTurnInRound`, `roundsCompleted`,
`wordsMastered`, `totalTurns`, `correctCount`, `sessionItemsWordIds`,
`promptUsesNativeLanguage`, `userMode` (`MultipleChoice` or `Text`).

`pendingAttempt` (`VocabularyAttempt`): `vocabularyWordId`, `userId`,
`activity`, `inputMode`, `wasCorrect`, `difficultyWeight`, `contextType`,
`userInput`, `expectedAnswer`, `responseTimeMs`.

## Rules every client follows

1. **Validate before resuming.** Refuse a state with an unknown `version`, a
   missing required field, another learner's `userId`, or a `launchContextKey`
   that isn't this launch, and offer to start over. Starting over marks the row
   `Abandoned`; it is never deleted.
2. **Only the install that graded an answer may score it.** The snapshot's counters
   already include a `pendingAttempt` (and an applied sentence credit). A client
   whose install didn't save the state (`savedBy` differs or is missing) and has
   no receipt of its own for that turn moves past that question to the next one,
   as Next would, without scoring it. A sentence turn in progress follows the
   same rule. (If the learner never returns to the grading install, that answer
   stays unscored; see "Known limits".)
3. **Save between questions, or record `savedBy`.** A client that saves a graded
   answer before scoring it must record `savedBy`. The web and MAUI page saves
   only between questions, so it never does.
4. **Write every field you know.** A client fills `planItemRowId`,
   `adHocPlanItemId` and `isRepractice` when it tracks them, and leaves them out
   otherwise.

5. **Claim your own older saves, once.** A native app's saves from before runs
   synced have no `savedBy` but record `planItemRowId`; only that device could
   have made them. The native app marks the learner's saves with its install ID
   the first time it opens that learner's database with a build that syncs runs,
   before that database's first sync, and never again
   (`NativeOwnerStores.ClaimLegacyQuizSavesOnceAsync`): a later state without
   `savedBy` may be another client's.

6. **Complete only a scored round.** A client that completes a plan card from a
   round summary it holds no proof for (another client saved it, or it moved past
   the round's last answer) first checks that an answer on one of the round's
   words was scored since the run started: the scoring client's learning records
   (`VocabularyLearningContext`, activity `VocabularyQuiz`) sync with the run. Allow
   5 minutes of clock difference between clients. A summary alone completes
   nothing. .NET clients call `QuizRoundScoring.WasScoredSinceAsync`
   (`src/SentenceStudio.Shared/Data/QuizRoundScoring.cs`); another client (such as
   the Flutter port) applies the same query.

## Known limits

- **An unconfirmed answer can go unscored.** If a learner grades an answer on one
  device, leaves it on the feedback screen, and continues the run on another
  client, that client moves past the question and its save replaces the run. The
  first device then never scores that one answer. Nothing is scored twice; the
  word is scored the next time it comes up.
- **A restored backup shares the install ID.** The install ID lives in
  preferences, which a device backup copies. With two devices restored from one
  backup, either could score the other's unconfirmed answer.
- **Wrapped old MAUI saves use the UTC day.** SQLite has no time zones, so a MAUI
  save wrapped by the migration gets the UTC day of its last save as `planDate`;
  near midnight a native app may treat it as another day and offer to start
  over. Server rows use the learner's time zone when it is known.

## Samples

| File | Shows |
|---|---|
| `web-between-questions.json` | What the web/MAUI page saves. |
| `native-graded-answer.json` | A native save with a graded, unscored answer. |
| `native-sentence-turn.json` | A native sentence-writing turn with its credit applied. |
| `newer-client-extra-field.json` | An unknown field, which is ignored. |
| `invalid-*.json` | States every client must refuse. |

## History

- Before 2026-11-03 the web and MAUI page saved the bare `snapshot`, and only the
  native apps saved the wrapper. Migration `20261103151517_SyncTextIdsPhase2b`
  wrapped every bare snapshot (with `displayDirection` `""` and today's `planDate`
  taken from `UpdatedAt`), so every database holds only this format.
