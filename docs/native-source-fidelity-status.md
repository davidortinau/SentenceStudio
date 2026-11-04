# Native source-fidelity status

Date: 2026-11-03

Re-audit of the 56 open source-fidelity findings from 2026-10-03 against the
current native code.

## Method

- **Code only.** Nothing was built, run, installed or tested on a simulator,
  emulator or device for this report. "Fixed" means the current code appears
  to meet the finding's acceptance text. It is not runtime evidence and it is
  not acceptance. Human acceptance stays with the Captain.
- **Base commit:** `4767f67f` on `native-platform-heads`. Line numbers are at
  that commit.
- **Input:** the 56 findings in the rebuild plan
  (`native-platform-rebuild.json`, `findings` key). For each finding, the
  `source_refs`, `source_expected` and `acceptance` were compared with the
  current iOS, Android and shared code. The October `target_refs` are stale:
  the iOS UIKit view controllers were replaced by SwiftUI views under
  `src/SentenceStudio.Native.iOS/Features/`, so current code was found by
  behavior. Commit-message claims were checked against the code.
- **Departures** count only with a citation: `docs/native-port-departures.md`,
  `AGENTS.md`, or a plan deviation (for example `D-platform-navigation`:
  native tabs instead of a flyout; `D-product-corrections`: 19-value
  auto-advance and a single Sam). No finding is fully covered by a recorded
  departure. Some rows below need a Captain decision instead.
- **Resources on Android.** R01 to R16 were recorded for iOS only, because
  Android had no Resources screen in October. Android Resources has since
  shipped (`bb5ae2bf`, AF-A01). The Android column for those rows judges
  Android against the same acceptance text.

Status words:

- **fixed**: current code meets the acceptance text.
- **partial**: some of it; the row says what is missing.
- **open**: not addressed.
- **departure**: the Captain decided a different behavior (cited).
- **unclear**: code alone cannot settle it; the row names the runtime check.
- **n/a**: the finding was recorded for the other platform only.

Path prefixes used in the tables:

- `iOS/` = `src/SentenceStudio.Native.iOS/`
- `And/` = `src/SentenceStudio.Native.Android/`
- `Shr/` = `src/SentenceStudio.Native/`

## Summary

| Status | iOS | Android |
|---|---|---|
| fixed | 40 | 24 |
| partial | 12 | 24 |
| open | 0 | 5 |
| departure | 0 | 0 |
| unclear | 1 | 0 |
| n/a | 3 | 3 |
| **Total** | **56** | **56** |

In short:

- iOS has caught up on almost every finding. What remains is mostly small
  gaps (onboarding suggested names, dashboard states, the quiz override
  retry, mnemonic image hiding) and Captain decisions.
- Android has the shared behavior but is missing several quiz screens and
  controls: photos never load (no `LoadPhotoAsync`), there are no audio
  answer buttons, hints, "Use in a sentence" or a learning-details opener.
  Android Settings has only the mode picker, and Android onboarding cannot
  finish.
- One new cross-client issue surfaced (AF-Q01): the plan-card quiz launch
  context on native does not match the web's, so the shared launch key is
  likely different.

## Findings

### Sign-in, shell, Sam and Settings

| ID | Finding | iOS | iOS evidence | Android | Android evidence | What remains |
|---|---|---|---|---|---|---|
| AF-N01 | Sign-in links to register and forgot-password | fixed | Links `iOS/Features/Authentication/SignInView.cs:107-120`; routes `iOS/App/ApplicationController.Account.cs:43-74`; non-enumerating reset `Shr/NativeAuthPresenter.Accounts.cs:118-146` | fixed | Links `And/AndroidSliceShell.Compose.cs:276-283`; screens `And/AndroidSliceShell.Auth.cs:68-80,119-266` | Android polish: the rule list shows only unmet rules (`AndroidSliceShell.Auth.cs:182-189`) and the confirmation uses a native key, not `Register_CheckYourEmail` (`:130-135`). |
| AF-N02 | Null sign-in response means invalid credentials | fixed | `Shr/NativeAuthPresenter.cs:131-133` (invalid), `:147-153` (connection), `:161-166` (persistence); form stays editable `iOS/App/ApplicationController.cs:328-329` | fixed | Same presenter; alert `And/AndroidSliceShell.Compose.cs:190-192`; fields enabled `:203-205` | Nothing in code. |
| AF-N03 | Post-login sync overlay, then Dashboard or onboarding | fixed | Readiness defer `Shr/NativeAuthPresenter.cs:331-356`; overlay with retry and sign-out `iOS/Features/Authentication/AccountViews.cs:220-257` | fixed | Readiness screen `And/AndroidSliceShell.Auth.cs:70-117`; route only after Ready `And/AndroidRoutePolicy.cs:20-23` | Nothing in code. |
| AF-N04 | Six-step onboarding journey | partial | All six steps, Back/Next, logout, starter and skip `iOS/Features/Onboarding/OnboardingView.cs:31-212`; save and finish `iOS/App/ApplicationController.cs:422-469` | partial | Steps 0-4 render `And/AndroidSliceShell.Onboarding.cs:76-209`; step 5 shows only "unavailable" `:183-189`; Android never calls `SaveOnboardingProfileAsync` or `FinishOnboardingAsync` | Both: the source's AI suggested names (`Onboarding.razor:59-75,311-329`) are missing; iOS has a plain name field. Android: step 5 Create Starter and Skip, profile save, progress and return to the Dashboard. |
| AF-N05 | Failed sign-in is corrected with Sign In; restore is separate (iOS) | fixed | "Reconnect saved session" only for an offline stored session `iOS/Features/Authentication/SignInView.cs:101-105`; email kept, password cleared `iOS/App/ApplicationController.cs:346-357`; test `tests/SentenceStudio.Native.iOS.Tests/NativeAccountRoutesContractTests.cs:13-29` | n/a | iOS-only finding | Nothing in code. |
| AF-N06 | One owner-scoped Sam after auth and sync | fixed | Entry on authenticated Dashboard `iOS/App/ApplicationController.Sam.cs:23-27`, also Resources `iOS/App/ApplicationController.cs:722`; availability gate `Shr/Sam/NativeSamPresenter.cs:84-107` | fixed | Dashboard only, owner-checked `And/AndroidSliceShell.Sam.cs:18-25,42-47`; mounted `And/AndroidSliceShell.Compose.cs:136-145` | Meets the acceptance. The source mounts Sam on every authenticated page (`MainLayout.razor:69-85`); wider placement is a Captain scope question. |
| AF-N07 | Shell notices: ingest, update banner, What's New, sync overlay | partial | Only the sync overlay exists (`iOS/Features/Authentication/AccountViews.cs:220-257`); no What's New, update or ingest code | partial | Only the sync overlay exists (`And/AndroidSliceShell.Auth.cs:82-117`) | Captain decision needed on which notices apply natively (source `MainLayout.razor:40-45,87-92,300-325`). The ingest indicator depends on import flows not yet created. |
| AF-N08 | Menu reaches Settings, Profile, Resources and other destinations | partial | Tabs `iOS/App/ApplicationScene.cs:287-291`; More menu with Profile, Settings, Skills, Vocabulary, Activity Log `iOS/Features/Dashboard/DashboardView.cs:114-138` | partial | Tabs `And/AndroidRoutePolicy.cs:9-11`; menu has only Regenerate, Settings, Sign out `And/AndroidSliceShell.Visual.cs:186-220` | Native tabs are approved (`D-platform-navigation`). Both: Minimal Pairs has no navigation entry (source `NavMenu.razor:49`); it is reachable only as a Choose My Own tile. Android: Profile, Skills, Vocabulary and Activity Log screens do not exist; building them needs Captain approval. |
| AF-Q14 | Settings: appearance and every quiz preference | fixed | Appearance `iOS/Features/Settings/AppearanceSettingsView.cs:18-79`; quiz preferences `iOS/Features/Settings/QuizSettingsView.cs:19-114`; owner-gated save `Shr/NativeQuizSettings.cs:24-31,87-144` | partial | Settings has only System/Light/Dark and the Sam section `And/AndroidSliceShell.Appearance.cs:8-76` | Android: theme, text size, voice language and voice, direction, autoplay, mnemonics, text/audio/photo, auto-advance and Save, on the shared `NativeQuizSettings`. Out of scope here but missing on both: "Harvest example sentences" (`Settings.razor:149-170`). |
| AF-Q15 | Auto-advance persists 1-10 s in 0.5 s steps | fixed | Shared preference stores 1000-10000 ms in 500 ms steps `src/SentenceStudio.AppLib/Services/VocabularyQuizPreferences.cs:121-133`; 19 values `Shr/NativeQuizModality.cs:129-146`; slider `iOS/Features/Settings/QuizSettingsView.cs:60-77` | partial | Shared fix applies; Android has no control | Android: add the control with AF-Q14. |

### Dashboard

| ID | Finding | iOS | iOS evidence | Android | Android evidence | What remains |
|---|---|---|---|---|---|---|
| AF-D01 | New learner Get Started (Quick Start, Add a Resource) after sync | partial | Welcome cards `iOS/Features/Dashboard/DashboardWelcomeView.cs:15-83`; new-user after sync `Shr/NativeDashboardPresenter.cs:236-262`; target-language starter `Shr/Adapters/NativeResourceStore.Generation.cs:57-95` | partial | Same page `And/AndroidSliceShell.Compose.cs:327-336,460-488`; starter and Add `And/AndroidSliceShell.Resources.cs:142-200` | Both: a missing target language shows `NativeResources_SetTargetLanguage` instead of routing to onboarding (source `Index.razor:804-811`; `iOS/App/ApplicationController.cs:541-545`, `And/AndroidSliceShell.Resources.cs:172-173`). New-user test uses progress rows, not owned words (`NativeDashboardPresenter.cs:252-255` vs `Index.razor:778-782`). Starter creates no skill profile when none exists (`Index.razor:826-837`). |
| AF-D02 | Today's Plan / Choose My Own switch, persisted per owner | partial | Switch and per-owner key `iOS/Features/Dashboard/DashboardView.cs:38-55`, `Shr/NativeDashboardPresenter.cs:30,51-62` | fixed | Switch always shown `And/AndroidSliceShell.Compose.cs:337,504-551`; handler `And/AndroidSliceShell.Dashboard.cs:23-35` | iOS shows the switch only in Ready or Empty (`DashboardView.cs:38`); the source always shows it (`Index.razor:105-117`). |
| AF-D03 | Choose My Own selectors, saved selection, tiles, See All, scoped launch | fixed | `iOS/Features/Dashboard/ChooseOwnView.cs:34-137`, `ChooseOwnSelectorView.cs:10-55`; reconcile and save `Shr/NativeDashboardPresenter.ChooseOwn.cs:39-78,118-171` | partial | Sheets `And/AndroidSliceShell.Dashboard.Compose.cs:131-250`; launch `And/AndroidSliceShell.Activities.cs:44-63` | Android behavior meets the acceptance; the tiles are a vertical text list (`AndroidSliceShell.Dashboard.Compose.cs:87-127`), not the source two-column icon grid (`Index.razor:458-471`). |
| AF-D04 | Load the cached plan, create today's when absent, once (Android) | n/a | Android-only finding | fixed | `And/AndroidSliceShell.cs:645-650` calls `LoadAfterSignInAsync`; create-if-absent `Shr/NativeDashboardPresenter.cs:64-65,272-299` | Nothing in code. |
| AF-D05 | Completed plan cards stay tappable | fixed | Every implemented card opens, completed included `iOS/Features/Dashboard/PlanCardView.cs:81-96`; completed quiz opens as re-practice `Shr/NativeDashboardPresenter.cs:180-199` | partial | `canOpen` excludes a completed Vocabulary Review card and labels it unavailable `And/AndroidSliceShell.Compose.cs:817-821` | Android: let the completed Vocabulary Review card open (re-practice launch already exists). |
| AF-D06 | Insight resource titles open the owned resource, with icons and reasons | fixed | `iOS/Features/Dashboard/PlanInsightView.cs:52-85,250-257`; open and return `iOS/App/ApplicationController.cs:834-866` | partial | Clickable title and reason `And/AndroidSliceShell.Compose.cs:600-638`; return and reload `And/AndroidSliceShell.Resources.cs:76-84` | Android: only two icons (`AndroidSliceShell.Compose.cs:608-611`); the source has five media icons (`Index.razor:1385-1392`). |
| AF-D07 | Preview plan vocabulary opens Flashcard Preview for the plan date | fixed | Button `iOS/Features/Dashboard/PlanInsightView.cs:102-114`; launch with date `iOS/App/ApplicationController.Activities.cs:149-167` (`3a063cbb`) | fixed | Button `And/AndroidSliceShell.Compose.cs:652-657`; launch `And/AndroidSliceShell.Flashcards.Compose.cs:23-39` | Nothing in code. |
| AF-D08 | Insight shows struggling, new and doing-well tiers, pattern and sample words | partial | Three tiers with badges `iOS/Features/Dashboard/PlanInsightView.cs:115-131,205-248`; pattern and samples `:132-166` | partial | Three tiers as plain comma text `And/AndroidSliceShell.Compose.cs:658-672,717-729` | iOS: the doing-well tier has no heading or accessible tier text, so meaning is by color only (`PlanInsightView.cs:127-131`). Android: no badges or severity styling; doing-well unlabeled. |
| AF-D09 | Numbers Mastery cards, due count, correct/total, empty state | fixed | `iOS/Features/Dashboard/DashboardLearningProgressView.cs:60-134`; owner-scoped query, Korean-only `Shr/NativeDashboardPresenter.cs:462-504` | partial | Section only when rows or an error exist `And/AndroidSliceShell.Compose.cs:433-447`; card `:490-502` | Android: no empty "start" state; due text shown at 0; heading not from `NativeDashboard_NumbersMastery`. |
| AF-D10 | Vocabulary section has its own loading, tiles, No Vocab Data and error | partial | Separate error and empty states `iOS/Features/Dashboard/DashboardLearningProgressView.cs:27-58`; vocab loads first `Shr/NativeDashboardPresenter.cs:264-269,437-460` (`f94e8bf0`) | partial | Tiles `And/AndroidSliceShell.Compose.cs:731-769`; fallback `:421-431` | Both: vocabulary is published only after the whole plan load finishes (one Loading phase, `NativeDashboardPresenter.cs:212-214`). iOS: a plan error can appear under the Vocabulary header (`DashboardLearningProgressView.cs:46-52`). Android: `VocabErrorKey` is never read, so a failure shows "No Vocab Data". |
| AF-D11 | iPad welcome heading and adaptive layout (iOS) | unclear | Greeting at width >= 768 `iOS/Features/Dashboard/DashboardView.cs:32-37`; adaptive grids `DashboardWelcomeView.cs:78-80`, `DashboardLearningProgressView.cs:130-131` | n/a | iOS-only finding | Runtime check: iPad vs WebApp at 768 and 1024 widths, light/dark, default and largest text. Name is read only at sign-in (`ApplicationController.cs:244`), so a rename may not refresh it. |
| AF-Q01 | Quiz accepts owned multi-resource, skill, DueOnly, plan and focus launch contexts | partial | Choose My Own passes resources and skill `iOS/App/ApplicationController.Quiz.cs:488-513` via `Shr/NativeQuizLaunchService.cs:18-61`; presenter accepts multi-resource `Shr/NativeQuizPresenter.cs:118-171` | partial | Same path `And/AndroidSliceShell.cs:370-389` | Both: the plan-card quiz sends the card's resource and `DueOnly=false` (`Shr/NativeDashboardPresenter.cs:190-199`). The source sends no resource and `DueOnly=true` for Vocabulary Review (`src/SentenceStudio.Shared/Services/PlanGeneration/PlanConverter.cs:145-151`, `Index.razor:1052-1064`), and the native catalog already records that rule (`Shr/Activities/NativeActivityCatalog.cs:122-127`). The launch key (`NativeQuizPresenter.cs:214-216` vs `VocabQuiz.razor:896-901`) likely differs, against `docs/activity-session-format.md:26`. Runtime check below. Native sessions saved under the current key need a data-preservation review before a fix. |

### Vocabulary Quiz: audio, photo and mnemonic media

| ID | Finding | iOS | iOS evidence | Android | Android evidence | What remains |
|---|---|---|---|---|---|---|
| AF-Q02 | Prompt audio: default on, prompt-side voice, cache, text fallback | fixed | Adapter registered `iOS/NativeServices.cs:100-109`; voice preference, owner-checked cache, then API speech `iOS/Platform/NativeQuizMedia.cs:52-117`; play button `iOS/Features/Quiz/QuizQuestionView.cs:58-61` | partial | Plays through Android system text-to-speech `And/AndroidQuizMedia.cs:38-95,186-213`, not the source speech service, voice preference or cache; `StopPlayback` not overridden (`Shr/NativeSlicePorts.cs:113`) | Android: use the owner-bound speech source, cache and selected voice like iOS, or the Captain records system TTS as a departure. |
| AF-Q03 | Native-prompt multiple choice offers audio buttons and lettered options | partial | Lettered A-D options with play buttons `iOS/Features/Quiz/QuizQuestionView.cs:180-224`; presenter no longer blocks `Shr/NativeQuizPresenter.Session.cs:259-287` | open | Choices are text only, no letters or play buttons `And/AndroidSliceShell.Visual.cs:268-307`; no caller of `PlayAnswerChoiceAsync` | iOS: a choice can be picked only after it was heard (`iOS/App/ApplicationController.Quiz.cs:152-173`); the source allows picking at once (`VocabQuiz.razor:405-417`). Not in the departures doc; Captain to record or revert. Android: add the options; code suggests that when TTS can play every choice, all choices stay disabled (`Session.cs:338-340`, `And/AndroidSliceShell.Compose.cs:1090`), so the turn may be stuck. |
| AF-Q04 | Readable fallback when audio is unavailable | fixed | Written fallback `Shr/NativeQuizModality.cs:45-53,84-85`; autoplay failure drops to text `Shr/NativeQuizPresenter.Session.cs:304-308`; tests `tests/SentenceStudio.Native.Tests/NativeQuizSourceMediaTests.cs:214,264` | partial | Same shared prompt fallback | Android: the audio-answer row has no fallback until AF-Q03 is fixed. |
| AF-Q05 | Photo prompt: real image, show/hide, loading and error fallback | fixed | Owner-checked bytes `iOS/Platform/NativeQuizMedia.cs:248-300`; show/hide `iOS/Features/Quiz/QuizQuestionView.cs:66-77`; render/fail reporting `iOS/App/ApplicationController.Quiz.cs:96-117` | open | Display code exists (`And/AndroidSliceShell.Compose.cs:1155-1195`) but `AndroidQuizMedia` does not implement `LoadPhotoAsync`, so the default returns null (`Shr/NativeSlicePorts.cs:111-112`); `PhotoRenderedAsync` is never called; no show/hide control | Android: implement `LoadPhotoAsync`, report rendered/failed, add the show/hide button. No quiz photo can appear on Android today. |
| AF-Q06 | Hide Text for native-language photo prompts; per owner; reveal on feedback | fixed | Eligibility `Shr/NativeQuizPresenter.Session.cs:355-356`; menu action `iOS/Features/Quiz/QuizView.cs:226-236`; saved per owner `Shr/NativeQuizPresenter.Commands.cs:160-187`; reveal on feedback `Session.cs:349` | open | No control; never eligible because no photo loads | Android: add the toggle after AF-Q05. |
| AF-Q07 | Full-screen photo viewer with close, backdrop, focus and zoom | fixed | Full-screen cover with zoom and close `iOS/Features/Quiz/QuizView.cs:237-259`; open from thumbnail `iOS/App/ApplicationController.Quiz.cs:77-94` | partial | Viewer built `And/AndroidQuizPhotoDialog.cs:9-80`, `And/AndroidQuizPhotoTransform.cs`; unreachable because no photo loads | Android: reachable after AF-Q05. iOS: focus return and gestures need a runtime check. |
| AF-Q18 | Autoplay prompt audio on each new or restored turn | fixed | Shared autoplay honors `AutoPlayVocabAudio` `Shr/NativeQuizPresenter.Session.cs:298-324`; also on resume `Shr/NativeQuizPresenter.cs:424-484` | fixed | Same shared code; plays via system TTS `And/AndroidQuizMedia.cs:72` | Runtime check: the presenter awaits the clip before returning the turn, so the new question may appear only after the clip ends (source plays after render, `VocabQuiz.razor:1867-1870`). |
| AF-Q20 | Correct answer reveals the mnemonic image even with photo prompt off | partial | Loaded and requested on correct feedback `Shr/NativeQuizPresenter.Session.cs:225-226,352-363`; reset next turn `:224,241-243` | partial | Feedback photo code exists `And/AndroidSliceShell.Compose.cs:43-66,1033-1039`; never fed, because no photo loads | Shared: "Hide image" on correct feedback does not hide it (`Session.cs:352-353`); after a wrong answer the source still offers Show image (`VocabQuiz.razor:2234-2238`), native does not. Android: AF-Q05 fix plus full-screen access from the feedback image. |

### Vocabulary Quiz: flow, details and summary

| ID | Finding | iOS | iOS evidence | Android | Android evidence | What remains |
|---|---|---|---|---|---|---|
| AF-Q08 | Learning details sheet with source fields and no answer terms | fixed | Info button `iOS/Features/Quiz/QuizQuestionView.cs:62-65`; sheet `iOS/App/ApplicationScene.cs:643-658`; fields `iOS/Features/Quiz/QuizLearningDetailsView.cs:18-111` | partial | Sheet built `And/AndroidSliceShell.Compose.cs:1222-1307`; nothing in Android calls `OpenDetails()` | Android: add the info button and action. iOS: runtime check of focus return. |
| AF-Q09 | Opt-in 1-3 target-only example sentences on target-prompt turns | fixed | Toggle and panel `iOS/Features/Quiz/QuizQuestionView.cs:78-87,117-141`; owner-scoped prefetch `Shr/NativeQuizPresenter.Hints.cs:21-55`; resets `Shr/NativeQuizPresenter.Session.cs:201` | open | No toggle or panel in `RenderQuestion` (`And/AndroidSliceShell.Compose.cs:995-1153`); no caller of `ToggleSentenceHints` | Android: render the toggle and panel from existing shared state. |
| AF-Q10 | "Use in a sentence": grading, credit only for UsageCorrect, cancel/continue | fixed | Menu item `iOS/Features/Quiz/QuizView.cs:226-236`; screen `iOS/Features/Quiz/QuizSentenceView.cs:16-155`; real grading `Shr/Adapters/NativeQuizSentenceGrader.cs:23-69`; credit `Shr/NativeQuizPresenter.Sentences.cs:157-225` | open | No caller of `OpenSentenceShortcut` or `SubmitSentenceAsync`; `RenderQuestion` handles only MultipleChoice and Text (`And/AndroidSliceShell.Compose.cs:1080-1140`) | Android: menu entry, sentence screen, results, retry, cancel/continue, Back. A sentence turn saved on iOS and resumed on Android has no renderer. |
| AF-Q11 | Fuzzy-accepted answer shows the complete form | fixed | `iOS/Features/Quiz/QuizQuestionView.cs:164-173`; computed after grading only `Shr/NativeQuizPresenter.Session.cs:365-369` | fixed | `And/AndroidSliceShell.Compose.cs:1054-1062` | Nothing in code. |
| AF-Q12 | One response slot becomes "Type correct" after a wrong answer | fixed | One field switches placeholder, value and submit `iOS/Features/Quiz/QuizQuestionView.cs:226-271`; focus requested `iOS/App/ApplicationController.Quiz.cs:199-203` | partial | One field swaps state `And/AndroidSliceShell.Compose.cs:1096-1146`; disabled while busy (`:1104`); no focus requester | Android: give the correction field focus. Runtime check with Korean IME. |
| AF-Q13 | Summary marks correct, incorrect and mastered rows with totals | fixed | `iOS/Features/Quiz/QuizSummaryView.cs:20-90`; rows carry `ReadyToRotateOut` `Shr/NativeQuizPresenter.Session.cs:440-456` | fixed | Totals and per-row status `And/AndroidSliceShell.Compose.cs:915-959` | Nothing in code. Android rows use text status without icons; visual check. |
| AF-Q16 | Save error offers a safe retry that keeps the turn, plus Back (iOS) | partial | Retry for any `SaveFailed` prompt `iOS/App/ApplicationController.Quiz.cs:135-144,569-591`; rendered `iOS/Features/Quiz/QuizView.cs:119-133` | n/a | iOS-only finding; the same gap exists in shared code (see remains) | After an override save fails, Retry calls `RetryPendingSaveAsync`, which refuses while `_overrideRetryPending` (`Shr/NativeQuizPresenter.Commands.cs:451-459`), and the override button shows only in Feedback. Android has the same gap, plus the correction-save case. Fix in shared code and both heads. |
| AF-Q17 | Back from the resume choice keeps the session, no new write (Android) | n/a | Android-only finding | fixed | Presenter returns before any write `Shr/NativeQuizPresenter.Commands.cs:941-943`; Back `And/AndroidSliceShell.cs:168-184`; test `tests/SentenceStudio.Native.Tests/NativeSliceBoundaryTests.cs:16` | Runtime check: Back, relaunch, same resume choice, no double count. |
| AF-Q19 | Mnemonic text only after a correct answer when enabled | fixed | `iOS/Features/Quiz/QuizQuestionView.cs:143-160`; guard `Shr/NativeQuizPresenter.Session.cs:394-396` | fixed | `And/AndroidSliceShell.Compose.cs:1063-1068` | Nothing in code. |

### Learning Resources: list, Add and starter

| ID | Finding | iOS | iOS evidence | Android | Android evidence | What remains |
|---|---|---|---|---|---|---|
| AF-R01 | Grid/list toggle, persisted view mode | fixed | Toggle `iOS/Features/LearningResources/LearningResourcesView.cs:58-73`; grid and list `:145-193`; persist `Shr/NativeResourcePresenter.cs:187-196,926-927` | fixed | Toggle `And/AndroidSliceShell.Resources.Compose.cs:150-158`; lazy grid and list `:213-240` | Nothing in code. |
| AF-R02 | Filters always offer the source type and language catalogs | fixed | Catalogs `Shr/NativeResourcePresenter.cs:19-20,886`; menus `iOS/Features/LearningResources/LearningResourcesView.cs:47-54,224-267` | fixed | Menus `And/AndroidSliceShell.Resources.Compose.cs:137-149,268-325` | Nothing in code. |
| AF-R03 | Zero results offer Add First and Create Starter, with progress and feedback | fixed | Both buttons for Empty and NoMatches `iOS/Features/LearningResources/LearningResourcesView.cs:111-138`; progress `:155-158` | fixed | Both buttons `And/AndroidSliceShell.Resources.Compose.cs:189-205`; spinner `:167-173` | Progress is inline with the page disabled, not the source full-screen overlay. Captain may want to confirm. |
| AF-R04 | Top-level Add opens a new editable resource | fixed | Add `iOS/Features/LearningResources/LearningResourcesView.cs:195-222`; `OpenCreate` `LearningResourcesCoordinator.cs:334-349` | fixed | Add `And/AndroidSliceShell.Resources.Compose.cs:89-109`; `And/AndroidResourcesCoordinator.cs:177-192` | Nothing in code. |
| AF-R05 | Create form with all source fields, defaults, required title | fixed | Fields `iOS/Features/LearningResources/LearningResourceView.cs:87-135,159-181`; defaults and validation `Shr/NativeResourcePresenter.cs:210-213,465-474`; create `Shr/Adapters/NativeResourceStore.cs:169-218` | fixed | Fields `And/AndroidSliceShell.ResourceForm.Compose.cs:86-145` | Nothing in code. |
| AF-R06 | Add-time vocabulary by paste and by .txt/.csv file | fixed | Paste, delimiter, file `iOS/Features/LearningResources/LearningResourceVocabularyView.cs:55-110,202-269`; file importer `iOS/App/ApplicationScene.cs:794-797`; limits `Shr/NativeResourcePresenter.Vocabulary.cs:433-439` | partial | Paste, delimiter, staged rows `And/AndroidSliceShell.ResourceForm.Compose.cs:399-414,665-722`; file button disabled `:723-730` | Android file import. Needs Captain approval for a system document picker (AGENTS.md gate); the shared code is ready. |
| AF-R07 | Create Starter: guards, dedupe, shared generation, feedback | fixed | `Shr/Adapters/NativeResourceStore.Generation.cs:44-95`; `Shr/NativeResourcePresenter.Generation.cs:10-97` | fixed | Same shared path; feedback `And/AndroidResourcesCoordinator.cs:582-613` | Nothing in code. |
| AF-A01 | Android Resources is a first-class destination | n/a | Android-only finding | partial | Tab `And/AndroidRoutePolicy.cs:9-11`; route `And/AndroidSliceShell.Compose.cs:130`; browser and form (`bb5ae2bf`) | File import (R06, R14), R11, R12. The October finding put Android Resources on hold for a Captain approval; no approval is recorded in the repo. Captain to confirm. |

### Learning Resources: detail and edit

| ID | Finding | iOS | iOS evidence | Android | Android evidence | What remains |
|---|---|---|---|---|---|---|
| AF-R08 | One editable compound page; smart resource read-only | fixed | Open goes to edit `iOS/Features/LearningResources/LearningResourcesCoordinator.cs:413-419`; page `LearningResourceView.cs:87-202`; smart read-only `:70-86` | fixed | `And/AndroidResourcesCoordinator.cs:351-368`; `And/AndroidSliceShell.ResourceForm.Compose.cs:80-161` | Runtime check of very long read-only text on iOS. |
| AF-R09 | Edit media type, language and URL with the other fields | fixed | Pickers and URL `iOS/Features/LearningResources/LearningResourceView.cs:104-129`; save `Shr/Adapters/NativeResourceStore.cs:161-167` | fixed | `And/AndroidSliceShell.ResourceForm.Compose.cs:101-114,299-360` | Runtime check: save, reopen, sync. |
| AF-R10 | Full transcript and translation visible and editable | fixed | Full text `Shr/Adapters/NativeResourceStore.cs:146-149`; editors `iOS/Features/LearningResources/LearningResourceView.cs:182-194` | fixed | `And/AndroidResourcesCoordinator.cs:419-420`; `And/AndroidSliceShell.ResourceForm.Compose.cs:146-158` | Runtime check of long text and Korean IME. |
| AF-R11 | List each word with count and No Vocab Yet; owner-bound; virtualized | partial | Owner-bound fetch `Shr/Adapters/NativeResourceStore.cs:95-115`; rows `iOS/Features/LearningResources/LearningResourceVocabularyView.cs:24-29,111-117,172-200` in a plain stack `:147-154` | partial | Rows `And/AndroidSliceShell.ResourceForm.Compose.cs:367-371,415-430` in a scrolling column `:30-36` | Rows are drawn eagerly on both (the source also does); the acceptance asks for virtualization. Lazy rows, or a Captain call that eager rows are fine. |
| AF-R12 | Quick add: gated debounced search, keyboard, attach/create, focus | fixed | Keys `iOS/Features/LearningResources/LearningResourceQuickAddView.cs:27-56`; debounce and gate `Shr/NativeResourcePresenter.Vocabulary.cs:24-30,62`; focus restore `LearningResourcesCoordinator.cs:939-969` | partial | Search and results `And/AndroidSliceShell.ResourceForm.Compose.cs:558-640`; IME Done `And/AndroidResourcesCoordinator.cs:270-279` | Android: return focus to the field after attach or create; optional arrow and Escape navigation. |
| AF-R13 | Generate vocabulary from the transcript, with progress and result | fixed | `iOS/Features/LearningResources/LearningResourceVocabularyView.cs:30-46`; `Shr/NativeResourcePresenter.Generation.cs:99-225` | fixed | `And/AndroidSliceShell.ResourceForm.Compose.cs:373-385`; `And/AndroidResourcesCoordinator.cs:638-668` | Runtime check on a test resource. |
| AF-R14 | Bulk import: paste comma/tab and .txt/.csv up to 1 MB | fixed | `iOS/Features/LearningResources/LearningResourceVocabularyView.cs:47-110`; file importer `iOS/App/ApplicationScene.cs:794-797`; limits `Shr/NativeResourcePresenter.Vocabulary.cs:12,426-466` | partial | Paste and delimiter `And/AndroidSliceShell.ResourceForm.Compose.cs:665-722`; file disabled `:723-730` | Android file import, same approval as AF-R06. |
| AF-R15 | Per-word Remove unlinks the mapping; smart rows have none | fixed | `iOS/Features/LearningResources/LearningResourceVocabularyView.cs:191-193,271-294`; mapping-only delete `src/SentenceStudio.Shared/Data/LearningResourceRepository.NativeResources.cs:226-249` | fixed | `And/AndroidSliceShell.ResourceForm.Compose.cs:450-470`; `And/AndroidResourcesCoordinator.cs:294-304` | Runtime check on an isolated fixture. |
| AF-R16 | Delete with cancelable confirmation; smart protected | fixed | Menu `iOS/Features/LearningResources/LearningResourceView.cs:239-251`; confirmation `iOS/App/ApplicationScene.cs:798-806`; store refuses smart and foreign `Shr/Adapters/NativeResourceStore.Vocabulary.cs:80-105` | fixed | Menu `And/AndroidSliceShell.ResourceForm.Compose.cs:212-236`; dialog `:241-267` | Runtime check on a disposable fixture only, with a backup and the Captain's permission. |

## Captain decisions needed

These shape or unblock remaining work. None is recorded in
`docs/native-port-departures.md` today.

1. AF-N07: which shell notices apply natively (What's New, update banner,
   ingest indicator).
2. AF-R06, AF-R14: an Android system document picker for vocabulary file
   import (AGENTS.md UI approval gate; only the photo picker is approved).
3. AF-N08, AF-A01: Android Profile, Skills, Vocabulary and Activity Log
   screens (feature expansion), and confirmation of the Android Resources
   expansion that shipped in `bb5ae2bf`.
4. AF-Q03: keep or revert the iOS rule that an audio answer can be picked
   only after it was heard.
5. AF-Q02: Android system text-to-speech, or the source speech service and
   selected voice.
6. AF-R11: virtualized vocabulary rows, or eager rows like the source.
7. AF-N06: Sam on every authenticated page (source), or Dashboard (and
   Resources on iOS) as today.
8. AF-R03: inline starter progress, or the source full-screen overlay.

## Runtime checks

Run these before the Captain reviews the "fixed" rows. Use simulators and
emulators, isolated stores and disposable fixtures.

- AF-Q01: start the assigned Vocabulary Review card on the WebApp and on
  native for the same owner and day. Compare `LaunchContextKey` in the two
  `ActivitySession` rows and check that each client offers to resume the
  other's run.
- AF-Q03 (Android): Korean TTS installed, NativeToTarget or Mixed, audio
  prompt on, a multiple-choice turn. The code suggests every choice stays
  disabled.
- AF-Q05, AF-Q07, AF-Q20 (Android): a word with a mnemonic image. The code
  suggests no image ever appears.
- AF-Q20 (iOS): with the photo prompt off, answer correctly and tap Hide
  image. The code suggests the image stays.
- AF-Q16 (both): force a save failure on an override and on a correction, and
  confirm Retry works. Force a failure after the commit but before the next
  snapshot, and confirm Back leaves the quiz.
- AF-Q18 (both): on Next and on resume, check that the new question is
  visible while autoplay plays, and that clips never overlap.
- AF-D11 (iOS): iPad and WebApp side by side at 768 and 1024 widths, light
  and dark, default and largest text.
- AF-N02, AF-N03, AF-N05: wrong password, offline sign-in with no local
  profile, offline stored session on iOS.
- AF-Q14, AF-Q15 (iOS): change each setting, relaunch, and confirm the values
  persist; 7.5 s auto-advance drives feedback timing.
- AF-R09 to AF-R16 (both): save, reopen and sync edits; Korean IME in long
  fields; remove and delete only on disposable fixtures.

## Proposed fix workstreams

Grouped by shared presenter and feature area, with one owner per shared
presenter (AGENTS.md). Sizes: S = under a day, M = a few days, L = a week or
more.

### W1. Sign-in, onboarding and shell notices (M)

Findings: AF-N04, AF-D01 (missing target language routes to onboarding),
AF-N01 (Android polish), AF-N07 (after decision 1).

- Shared: `src/SentenceStudio.Native/NativeAuthPresenter.Onboarding.cs`; a
  suggested-name port in `src/SentenceStudio.Native/NativeSlicePorts.cs` and
  `src/SentenceStudio.Native/NativeSliceRegistration.cs`.
- iOS: `src/SentenceStudio.Native.iOS/Features/Onboarding/OnboardingView.cs`;
  `src/SentenceStudio.Native.iOS/App/ApplicationController.cs` (onboarding,
  lines 374-469, and the starter route, lines 518-576);
  `src/SentenceStudio.Native.iOS/App/ApplicationScene.cs` for AF-N07 only.
- Android: `src/SentenceStudio.Native.Android/AndroidSliceShell.Onboarding.cs`,
  `AndroidSliceShell.Auth.cs`, `AndroidSliceShell.Resources.cs` (starter
  route, lines 153-200), onboarding fields in `AndroidSliceShell.cs`.

### W2. Android Settings and navigation (M; L if decision 3 adds screens)

Findings: AF-Q14, AF-Q15 (Android), AF-N08 (Android menu).

- Shared: `src/SentenceStudio.Native/NativeQuizSettings.cs` (use as is).
  Optionally move the platform-neutral logic of
  `src/SentenceStudio.Native.iOS/Features/Settings/SettingsCoordinator.cs`
  into shared code so both heads use it.
- iOS: none.
- Android: `src/SentenceStudio.Native.Android/AndroidSliceShell.Appearance.cs`
  (becomes the full Settings, or a new `AndroidSliceShell.Settings.Compose.cs`),
  the Dashboard menu in `AndroidSliceShell.Visual.cs` (lines 186-220),
  settings state in `AndroidSliceShell.cs`.

### W3. Dashboard (M)

Findings: AF-D01 (new-user test, starter skill profile), AF-D02, AF-D03,
AF-D05, AF-D06, AF-D08, AF-D09, AF-D10, AF-D11 (check), AF-N08 (iOS Minimal
Pairs menu entry), and the Dashboard side of AF-Q01 (the launch built in
`SelectAssignedQuizAsync` and the completed-session key).

- Shared: `src/SentenceStudio.Native/NativeDashboardPresenter.cs`;
  Dashboard records in `src/SentenceStudio.Native/NativeSliceContracts.cs`;
  `src/SentenceStudio.Native/Adapters/NativeResourceStore.Generation.cs`
  (starter skill profile).
- iOS: `src/SentenceStudio.Native.iOS/Features/Dashboard/DashboardView.cs`,
  `DashboardActions.cs`, `DashboardLearningProgressView.cs`,
  `PlanInsightView.cs`.
- Android: the Dashboard range of
  `src/SentenceStudio.Native.Android/AndroidSliceShell.Compose.cs` (about
  lines 308-887), `AndroidSliceShell.Dashboard.Compose.cs`,
  `AndroidSliceShell.Dashboard.cs`.

### W4. Vocabulary Quiz (L)

Findings: AF-Q02 to AF-Q10, AF-Q12, AF-Q16, AF-Q20, and the quiz side of
AF-Q01. This is the largest stream. It can run as two owners in parallel:

- **W4a. Shared quiz presenter and iOS quiz (S to M):** AF-Q16 retry
  routing, AF-Q20 hide and show rule, AF-Q01 validation and key, AF-Q03 iOS
  after decision 4.
  - Shared: `src/SentenceStudio.Native/NativeQuizPresenter.cs`,
    `NativeQuizPresenter.Session.cs`, `NativeQuizPresenter.Commands.cs`, quiz
    records in `NativeSliceContracts.cs`,
    `src/SentenceStudio.Native/Adapters/NativeQuizAttemptCommitterAdapter.cs`,
    `src/SentenceStudio.Native/Adapters/NativeQuizSentenceAttemptCommitter.cs`;
    tests in `tests/SentenceStudio.Native.Tests/`.
  - iOS: `src/SentenceStudio.Native.iOS/App/ApplicationController.Quiz.cs`,
    `src/SentenceStudio.Native.iOS/Features/Quiz/QuizView.cs`,
    `src/SentenceStudio.Native.iOS/Features/Quiz/QuizQuestionView.cs`.
  - The AF-Q01 key change needs a data-preservation review for native
    sessions already saved under the current key, and it must land together
    with the W3 change to `SelectAssignedQuizAsync`.
- **W4b. Android quiz screen and media (M to L):** AF-Q02 to AF-Q10,
  AF-Q12, the Android side of AF-Q16 and AF-Q20. No shared change is needed
  except the AF-Q16 retry routing from W4a.
  - Android: `src/SentenceStudio.Native.Android/AndroidQuizMedia.cs`
    (`LoadPhotoAsync`, `StopPlayback`, voice source),
    `AndroidServiceHost.cs` (if the voice source changes), the quiz range of
    `AndroidSliceShell.Compose.cs` (about lines 889-1322), quiz commands in
    `AndroidSliceShell.cs`, `ChoiceAction` in `AndroidSliceShell.Visual.cs`
    (lines 268-307), `AndroidQuizPhotoPresentationPolicy.cs`,
    `AndroidQuizFeedbackPhotoPolicy.cs`, a new
    `AndroidSliceShell.QuizSentence.Compose.cs`; tests in
    `tests/SentenceStudio.Native.Android.Tests/`.

### W5. Learning Resources (S to M)

Findings: AF-R11 (after decision 6), AF-R12 (Android), AF-R06 and AF-R14
(Android, after decision 2), AF-A01.

- Shared: none.
- iOS: `src/SentenceStudio.Native.iOS/Features/LearningResources/LearningResourceVocabularyView.cs`,
  `LearningResourceView.cs` (AF-R11 only).
- Android: `src/SentenceStudio.Native.Android/AndroidSliceShell.ResourceForm.Compose.cs`,
  `AndroidResourcesCoordinator.cs`, `MainActivity.cs` (document picker beside
  the photo picker).

### Shared files to serialize

The streams above do not share a presenter, but some head files and the
strings are touched by more than one stream. Merge these one stream at a time,
or keep new code in new partial files and limit edits here to small hooks.

- `src/SentenceStudio.Shared/Resources/Strings/AppResources.resx`,
  `AppResources.ko.resx`, `AppResources.Designer.cs`: every stream that adds
  text.
- `src/SentenceStudio.Native.Android/AndroidSliceShell.cs` (state and
  commands): W1, W2, W4b.
- `src/SentenceStudio.Native.Android/AndroidSliceShell.Compose.cs`: W3
  (Dashboard range) and W4b (quiz range); W1 only for AF-N07.
- `src/SentenceStudio.Native.Android/AndroidSliceShell.Visual.cs`: W2
  (Dashboard menu) and W4b (`ChoiceAction`).
- `src/SentenceStudio.Native/NativeSliceContracts.cs`: W3 (Dashboard records)
  and W4a (quiz records).
- AF-Q01 spans W3 (`NativeDashboardPresenter.cs`) and W4a
  (`NativeQuizPresenter.cs` and the committers). Land both halves together.

## Progress after the audit (2026-11-03)

Captain's decisions on the eight questions above are in the squad decision inbox
(2026-11-03): pick an audio answer at once; Android server speech; the Android document
picker approved for vocabulary import only; Android Profile, Skills, Vocabulary and
Activity Log screens to build; What's New, update banner and import indicator deferred;
Sam on every signed-in screen; inline starter progress kept; lazy vocabulary rows
(AGENTS.md).

Merged on `native-platform-heads` (local, not pushed):

| Stream | Findings | Runtime-verified | Test- or code-only |
|---|---|---|---|
| W4a shared quiz + iOS | Q01, Q03, Q16, Q20 | iOS: pick an unheard letter, play then pick, centered answer, persistence | Q01 plan key (test derives the web key), Q16 save retry, Q20 mnemonic toggle |
| W4b Android quiz | Q02 to Q12, Q16, Q20 | Android: server speech and cache, details sheet, Use in a sentence with grade, receipt and credit, resume after restart; Release/R8 keeps the new calls | lettered audio answers (needs a direction setting Android lacks until W2), photos, Hide Text, sample sentences, Type-correct focus, save-failure Retry |
| W5 Resources | R06, R11, R12, R14, A01 | Android: file import through the system picker (Add form), lazy rows, quick-add focus | iOS lazy rows (no signed-in simulator); Android arrow and Escape keys are not possible with the Compose facade |

Not started: W1 (sign-in, onboarding; Sam on every signed-in screen per decision 6),
W2 (Android Settings and the four new Android screens), W3 (Dashboard).

Runtime checks still owed on a signed-in iOS simulator: AF-R11 lazy rows (empty resource,
smart resource, quick add), and the Android lettered-answer turn once W2 adds the
direction setting.
