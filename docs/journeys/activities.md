# SentenceStudio original app: learning ACTIVITIES journey audit

Scope: every learner practice activity in the ORIGINAL Blazor/MAUI-Hybrid app.
Source of truth: the frozen reference
a frozen copy of `main` at commit `59c72262` (the approved native-rebuild reference snapshot)
(call it `REF`). Live checkout the developer's `main` checkout was diffed read-only (see "Live-only differences").

Path legend for citations (all under `REF/src/`):
- `UI/` = `SentenceStudio.UI` (so `UI/Pages/VocabQuiz.razor:827` is a line in that file)
- `Shared/` = `SentenceStudio.Shared`, `AppLib/` = `SentenceStudio.AppLib`, `Api/` = `SentenceStudio.Api`
- E2E refs = `REF/.claude/skills/e2e-testing/references/*.md`; Learning Value Gate = `REF/.squad/skills/learning-value-gate/SKILL.md`

Confidence labels: unlabeled statements were read from code. "unconfirmed" = could not be proven from code read.
"E2E doc stale" = the E2E reference disagrees with the code and the code was taken as truth.

---------------------------------------------------------------------------

## 0. Summary table

| # | Activity | Route(s) | Entry points | Key modes / inputs | Main files |
|---|----------|----------|--------------|--------------------|-----------|
| 1 | Vocabulary Quiz (plan type VocabularyReview) | `/vocab-quiz` | Plan card; Choose-My-Own tile; Coach "Start Vocab Review" | MC vs typed text chosen per word by mastery; direction Target->Native / Native->Target / Mixed; text/audio/photo prompt toggles; sample-sentence hint; "Use in a sentence" AI shortcut; resume | `UI/Pages/VocabQuiz.razor`, `Shared/Models/VocabularyQuizItem.cs`, `AppLib/Services/VocabularyQuizPreferences.cs` |
| 2 | Vocabulary Matching (plan type VocabularyGame) | `/vocab-matching` | Plan card (closer step); Choose-My-Own tile | 8-pair tile match; hide-native-words mode (default on) | `UI/Pages/VocabMatching.razor` |
| 3 | Cloze | `/cloze` | Plan card; Choose-My-Own tile | AI sentences with blank; typed vs 5-choice; free navigation | `UI/Pages/Cloze.razor`, `AppLib/Services/ClozureService.cs` |
| 4 | Translation | `/translation` | Plan card; Choose-My-Own tile | native sentence -> typed target translation; vocab "blocks"; AI grade; intro screen | `UI/Pages/Translation.razor`, `AppLib/Services/TranslationService.cs`, `TeacherService.cs` |
| 5 | Writing | `/writing` | Plan card; Choose-My-Own tile | free sentence using 4 vocab blocks; AI grade; translate helper | `UI/Pages/Writing.razor` |
| 6 | Describe a Scene (SceneDescription) | `/scene` | Plan card (type exists); Choose-My-Own tile | image + typed description; AI grade; gallery manage | `UI/Pages/Scene.razor`, `Shared/Services/SceneImageService.cs` |
| 7 | Shadowing (also serves plan type Listening) | `/shadowing` | Plan card (Shadowing and Listening); Choose-My-Own tile | TTS sentence playback, slow/normal, translation toggle | `UI/Pages/Shadowing.razor`, `AppLib/Services/ShadowingService.cs` |
| 8 | Conversation | `/conversation` | Plan card (type exists); Choose-My-Own tile | AI chat with scenario persona; language switch; quick phrases; TTS of AI reply | `UI/Pages/Conversation.razor`, `AppLib/Services/ConversationService.cs`, `ScenarioService.cs` |
| 9 | Reading | `/reading` | Plan card; Choose-My-Own tile | Interactive text, timestamped TTS read-along, tap-word lookup, save word, font size | `UI/Pages/Reading.razor`, `UI/Shared/InteractiveText.razor` |
| 10 | Video Watching | `/video-watching` | Plan card only (NOT in Choose-My-Own list) | YouTube embed + collapsible transcript | `UI/Pages/VideoWatching.razor` |
| 11 | Number Drill | `/numberdrill` | Plan card (closer step); Choose-My-Own tile | 5 sub-modes x 6 contexts; Korean only | `UI/Pages/NumberDrill.razor`, `AppLib/Services/Numbers/*` |
| 12 | How Do You Say | `/how-do-you-say` | Choose-My-Own tile only | Type phrase -> TTS; history | `UI/Pages/HowDoYouSay.razor` |
| 13 | Word Association | `/word-association` | Choose-My-Own tile only | Write clues for 5 words; AI grades relatedness; scored rounds + high scores | `UI/Pages/WordAssociation.razor`, `AppLib/Services/WordAssociationService.cs` |
| 14 | Minimal Pairs (list, create, session) | `/minimal-pairs`, `/minimal-pairs/create`, `/minimal-pairs/session/{Id}` | Sidebar nav item; Choose-My-Own tile | Focus (1 pair, 10 trials) vs Mixed (all pairs, 20 trials); audio-only prompt | `UI/Pages/MinimalPairs*.razor`, `Shared/Repositories/MinimalPair*Repository.cs` |
| 15 | Flashcard Preview | `/flashcard-activity?date=` | Dashboard plan insight button "Preview plan vocabulary" | Swipe/tap cards over plan preview words; in-memory only | `UI/Pages/FlashcardActivity.razor` |
| 16 | Diary (list / editor / viewer) | `/diary`, `/diary/today`, `/diary/{Id}` | Choose-My-Own tile | Daily entry with AI prompt, word-count stars, AI feedback | `UI/Pages/Diary*.razor` |

Activity count: 16 learner-facing activity entries (15 distinct practice experiences + Diary; Minimal Pairs counted once for three pages).
There is NO dedicated Listening page (plan type Listening routes to Shadowing: `UI/Services/PlanActivityPresentation.cs` Route(), "No dedicated listening page; shares the shadowing UI").

---------------------------------------------------------------------------

## 1. Cross-cutting behaviour (applies to every activity unless the section says otherwise)

### 1.1 Entry points and the launch URL contract

1. **Today's Plan card**: Dashboard (`UI/Pages/Index.razor`) in Today's Plan mode lists `todaysPlan.Items`; each card `@onclick="LaunchPlanItem"` (`Index.razor:354-358`). `LaunchPlanItem` (`Index.razor:1048-1090`) builds `/{route}?...` where route = `PlanActivityPresentation.Route(type)` (`UI/Services/PlanActivityPresentation.cs`, Route(): VocabularyReview->vocab-quiz, Reading->reading, Listening->shadowing, Shadowing->shadowing, Cloze->cloze, Translation->translation, Writing->writing, SceneDescription->scene, Conversation->conversation, VocabularyGame->vocab-matching, VideoWatching->video-watching, NumberDrill->numberdrill, default->vocab-quiz).
   - `resourceIds` (plural) for vocab-quiz/vocab-matching, `resourceId` for the others, but resource is OMITTED for VocabularyReview, VocabularyGame, Cloze, NumberDrill (vocabulary-driven) (`Index.razor:1055-1064`).
   - Always adds `skillId` (if set), `planItemId`, `FocusVocabularyIds` (comma list), then copies any extra `RouteParameters` (e.g. `DueOnly=true`, `Mode=SRS`) that do not collide (`Index.razor:1065-1088`).
   - Plan route parameter construction: `Shared/Services/PlanGeneration/PlanConverter.cs:121-180` (DueOnly=true is set for VocabularyReview, VocabularyGame, Cloze, NumberDrill).
   - Card display: icon, localized label, resource title, "x of y min", due count, 4px progress bar when `MinutesSpent>0 && !IsCompleted`, check icon + strikethrough when `IsCompleted` (`Index.razor:354-400`).
2. **Choose My Own mode**: Dashboard mode toggle Today's Plan / Choose My Own (`Index.razor:105-116`), persisted in preference `DashboardMode` (`Index.razor:588,701-702,755`). Resource multi-select (`#resourceSelect`) and Skill single-select (`#skillSelect`) (`Index.razor:430-438`) then a grid of 14 tiles (`Index.razor:634-650`): conversation, scene, translation, cloze, writing, diary, reading, vocab-quiz, vocab-matching, shadowing, how-do-you-say, minimal-pairs, word-association, numberdrill. (video-watching and flashcard-activity are NOT tiles.) Mobile shows only first 6 until "See all" (`Index.razor:459-463`).
   - `NavigateToActivity` (`Index.razor:1273-1320`) re-validates the selection against the active profile's owned resources/skills (`ChooseOwnSelectionPreferences.Reconcile`), refuses navigation when any selected id is rejected or no active profile, then builds query: `resourceIds` (comma list) for vocab-quiz/vocab-matching/word-association, else only the FIRST selected resource as `resourceId`; `skillId` if chosen. No `planItemId`, so a timer session becomes an "ad-hoc" plan row (see 1.3).
   - Selection persisted per profile via `ChooseOwnSelectionPreferences` (`Shared/Services/ChooseOwnSelectionState.cs:56`).
3. **Coach**: approved vocabulary set card shows "Start Vocab Review" button navigating to `ActivityPath` = `/vocab-quiz?resourceIds={newResourceId}` (`UI/Shared/Coach/CoachVocabularySetCard.razor:51-57`, built in `Api/Coach/Application/CoachVocabularySetApplicationService.cs:619`). The Coach plan canvas (`UI/Shared/Coach/CoachPlanCanvas.razor`) is display-only (no activity launch; only a close handler at :25).
4. **Dashboard plan insight**: "Preview plan vocabulary" button -> `/flashcard-activity?date=yyyy-MM-dd` (`Index.razor:226-228, 1092-1100`).
5. **Sidebar**: only Minimal Pairs is a direct nav item (`UI/Layout/NavMenu.razor:58`). No activity is launched from Resources, Vocabulary or ActivityLog pages (grep of UI shows no activity route links there). `NavigationMemoryService` groups activity routes into the "dashboard" section so sidebar Dashboard returns to the last activity route (`UI/Services/NavigationMemoryService.cs:17-22`).
6. **Deep link / direct URL**: every activity page binds its query via `[SupplyParameterFromQuery]`; the E2E docs launch them directly (quiz-activities.md, other-activities.md). All pages carry `[Authorize]`.

### 1.2 Exit / return

- Every activity's Back button, "Done", "Go Back" or empty-state button calls `NavManager.NavigateTo("/")` (Dashboard) and first stops the timer (`PauseAndStopOwnedSession`), e.g. `VocabQuiz.razor:2473`, `VocabMatching.razor:459-463`, `Cloze.razor:429-433`.
- Exceptions: Minimal Pairs session/create go back to `/minimal-pairs` (`MinimalPairSession.razor:341`, `MinimalPairCreate.razor:190`); Minimal Pairs list goes to `/` (`MinimalPairs.razor:236`); Diary editor/viewer go to `/diary`, Diary list to `/` (`DiaryEditor.razor:296-299`, `DiaryViewer.razor:165`, `Diary.razor:126`).
- There is NO "completion" navigation: finishing an activity shows an in-page summary (where one exists) and the learner must tap Done; plan completion is driven by TIME (1.3), not by finishing.
- Components also stop the timer in `DisposeAsync` so browser/navigation away still stops it.

### 1.3 Timer, plan-item completion, streak, minutes

- Every plan-aware activity calls `ActivityTimer.StartSession(type, PlanItemId, resourceId, skillId)` in `OnInitializedAsync` (`Cloze.razor:197`, `Translation.razor:273`, `Writing.razor:216`, `Scene.razor:270`, `Shadowing.razor:161`, `Conversation.razor:250`, `Reading.razor:275`, `VideoWatching.razor:113`, `VocabMatching.razor:131`, `NumberDrill.razor:551`). VocabQuiz alone uses the owner-validated `StartValidatedSessionAsync` (`VocabQuiz.razor:1511`).
- Service: `Shared/Services/Timer/ActivityTimerService.cs`. 1-second tick (:37); on each whole-minute change and on Pause it saves minutes (`CaptureCurrentSaveLocked` / `SaveProgressAsync`, ~:517-560). `StopSession` returns elapsed. Pause+Stop helper `Shared/Services/Timer/ActivityTimerLeaseExtensions.cs`.
- No `planItemId` (Choose-My-Own) -> `StartLegacyAdHocAsync` creates a `DailyPlanCompletion` row `adhoc-{guid}` (EstimatedMinutes default 10, Priority 999) via `ProgressService.StartAdHocSessionAsync` (`Shared/Services/Progress/ProgressService.cs:983-1090`). So ad-hoc practice appears in the Activity Log as a "Freeform" row.
- With a `planItemId` the timer loads previously saved minutes and resumes from them (`LoadLegacyPlanProgressAsync`), so minutes accumulate across visits.
- **Plan item completes when `MinutesSpent >= EstimatedMinutes`** (`ProgressService.cs:821-833` for the explicit-user overload; `:858-873` for the legacy overload). Completion is therefore time-on-page, not task completion; opening an activity and idling counts. Sync is triggered after each save (`_syncService.TriggerSyncAsync`).
- Timer widget UI (`UI/Shared/ActivityTimer.razor`: mm:ss, "/ N min", pause/resume/stop, progress bar) is only rendered by Scene/Shadowing/Conversation/Reading/VideoWatching when query `fromPlan=true` (`Scene.razor:28-31` etc.). **Nothing in the repo ever sets `fromPlan` or `targetMinutes`** (grep over all .cs/.razor/.js finds only the page readers), so the visible timer is effectively dead UI; timing still runs silently. Default target minutes if supplied: Scene 10, Shadowing 15, Conversation 15, Reading 15, Video 20.
- The Reading page ALSO writes a `UserActivity` "Reading session: {title}" on Back (`Reading.razor:800-812`).
- **Streak** (`ProgressService.GetStreakInfoAsync`, `ProgressService.cs:1501-1545`) counts consecutive days that have a `UserActivity` row (90-day window). Pages that write `UserActivity`: Cloze, Translation, Writing, Scene, Reading, WordAssociation. Pages that do NOT: VocabQuiz, VocabMatching, Shadowing, Conversation, VideoWatching, NumberDrill (grep of `ActivityRepo` usage). So a day of only Vocab Quiz / Matching / Number Drill does not extend the streak (derived; whether Activity Log merges other sources is unconfirmed).

### 1.4 Vocabulary progress and mastery math (shared by quiz, matching, cloze, translation, writing, scene, conversation, word-association)

- Entry: `VocabularyProgressService.RecordAttemptAsync` (`Shared/Services/VocabularyProgressService.cs:74-158`) -> `VocabularyMasteryCalculator.ApplyAttempt` (`Shared/Services/VocabularyMasteryCalculator.cs`).
- Constants: MASTERY_THRESHOLD 0.85, MIN_PRODUCTION_FOR_KNOWN 2, EFFECTIVE_STREAK_DIVISOR 12, WRONG_ANSWER_FLOOR 0.6, MAX_WRONG_PENALTY 0.4, MAX_STREAK_PRESERVE 0.5, RECOVERY_BOOST 0.02, mastered review interval 60 days, max 365.
- Correct: `CurrentStreak += DifficultyWeight`; production modes ("Text", "Voice", "TextEntry") also `ProductionInStreak++`; `MasteryScore = max(min((streak + 0.5*prodStreak)/12,1), mastery) (+0.02 recovery)`.
- Wrong: mastery *= penaltyFactor (or `PenaltyOverride`); streak and production-in-streak shrink by a preserve fraction.
- SRS: lapse -> `ReviewInterval = max(1, round(interval*0.2))`, EaseFactor -0.2 (min 1.3); first correct 1 -> 6 days, then `interval*EaseFactor`; Known (mastery >= 0.85 and production-in-streak >= 2) forces 60-day interval and sets `MasteredAt`.
- `IsKnown` (`Shared/Models/VocabularyProgress.cs:114-118`): (mastery >= 0.85 and prodStreak >= 2) or (mastery >= 0.75 and prodStreak >= 4 and attempts >= 8).
- Familiar words (user declared, verification pending) are excluded while `IsInGracePeriod` and get `HandleVerificationProbeResultAsync` after attempts (pass -> 60-day, fail -> 1-day; `VocabularyProgressService.cs:439-475`).
- Phrase/Sentence vocabulary: a correct/incorrect attempt cascades passive exposure to constituent words (`:92-139`).
- Quiz-only counters `QuizRecognitionDemonstrations` (correct MC) and `QuizProductionDemonstrations` (correct Text) increment only when `Activity == "VocabularyQuiz"` (`:230-238`).
- After every recorded attempt pages call `CacheService.InvalidateVocabSummary()` so the Dashboard vocabulary counts (New/Learning/Familiar/Review/Known) refresh.
- AI-graded pages (Translation, Writing, Scene, Conversation) go through `ExtractAndScoreVocabularyAsync` (`VocabularyProgressService.cs:586-660`): per `VocabularyAnalysis` item matched to the user's vocabulary by `DictionaryForm`, one `Text`-mode attempt with `WasCorrect = UsageCorrect`.
- DifficultyWeight by activity: Quiz MC 1.0 / Text 1.5 / sentence-shortcut 2.5; Matching 0.8; Cloze 1.2; Translation 1.5; Writing 1.5; Scene 1.5; Conversation 1.2 with PenaltyOverride 0.8; WordAssociation 1.0.

### 1.5 AI usage map

- `AiService.SendPrompt<T>(prompt, AiTier tier=Fast, reasoningEffort)`; Reasoning tier for graders, Fast for generation (`Shared/Services/AiService.cs:59-90`; per memory index prod: Fast=gpt-5-mini, Reasoning=gpt-5).
- Prompts are Scriban templates in `AppLib/Resources/Raw/`: GetClozuresV2, GetTranslations, GetShadowingSentences, GradeTranslation, GradeSentence, GradeMyDescription, GradeWordAssociation, Translate, StartConversation(.scenario), ContinueConversation(.scenario), Conversation.system/.scenario, GenerateDiaryPrompt, GradeDiaryEntry.
- Graders (`AppLib/Services/TeacherService.cs`): GradeTranslation :138-167 (Reasoning, effort "minimal"), GradeSentence :215-251, GradeDescription :261-289, GradeTargetWordSentence :200, Translate :178. WordAssociation grading uses Reasoning without effort setting (`WordAssociationService.cs`).
- Failure behaviour is usually "return empty and toast" (ClozureService returns empty list on any exception: `ClozureService.cs:337-342`; ConversationService returns empty string/Reply on exception).

### 1.6 Audio / TTS

- `ElevenLabsSpeechService.TextToSpeechAsync(text, voiceId, stability, similarity, speed...)`; voice per language from `SpeechVoicePreferences.GetVoiceForLanguage(lang)` (set in Settings `Settings.razor:373,413` and inline on How Do You Say `HowDoYouSay.razor:298-303`).
- Cache: `StreamHistoryRepository.GetStreamHistoryByPhraseAndVoiceAsync(phrase, voice)` -> file in `{AppDataDirectory}/AudioCache`. Online miss -> generate, save file + `StreamHistory` row; offline miss -> "Audio unavailable offline." (Quiz `VocabQuiz.razor:2704-2769`, NumberDrill `:702-773`, MinimalPairSession `:226-295`).
- Playback: `IAudioManager.CreatePlayer(stream)`; when null / remote circuit use JS `audioInterop.playFromBase64` (webapp fallback). Reading/Shadowing/MinimalPairs/HowDoYouSay detect `JS.GetType().Name.Contains("Remote")` to choose JS audio.
- Quiz offline fallback: `CanPlayAudioAsync` hides audio affordances when offline and uncached (`VocabQuiz.razor:2916-2930`), `EnableTextFallbackAsync` disables audio and shows text if playback fails (`:2932-2938`).

### 1.7 Quiz preferences (Settings -> Voice & Quiz) (consumed by VocabQuiz)

Class `AppLib/Services/VocabularyQuizPreferences.cs` over `IPreferencesService` (per-host local prefs, not synced):

| Setting | Key | Default | Notes |
|---|---|---|---|
| Display direction | `vocab_quiz_display_direction` | `TargetToNative` | accepts legacy Forward/Reverse; Mixed |
| Autoplay vocab audio | `vocab_quiz_autoplay_vocab` | true | |
| Autoplay sample audio | `vocab_quiz_autoplay_sample` | false | no UI in Settings; unconfirmed any consumer |
| Show mnemonic image | `vocab_quiz_show_mnemonic` | true | |
| Auto-advance ms | `vocab_quiz_auto_advance_duration` | 2000 | clamped 1000..5000 (`:121-134`) |
| Use text prompt | `vocab_quiz_use_text_prompt` | true | |
| Use audio prompt | `vocab_quiz_use_audio_prompt` | true | |
| Use photo prompt | `vocab_quiz_use_photo_prompt` | false | |

Settings UI: `UI/Pages/Settings.razor:64-145, 396-420`. Surprise: slider is 1 to 10 s step 0.5 (`:140`) but the pref clamps silently to 5 s; UI default variable is 3.0 s but the stored default is 2 s (`:276`); if all three modalities are off, Save forces Text on with a toast (`:400-404`). Per-user (profile, synced) setting `UserProfile.VocabQuizShowTextWithPhoto` (default false) persisted by `UserProfileRepo.SaveVocabQuizShowTextWithPhotoAsync`.

---------------------------------------------------------------------------

## 2. Vocabulary Quiz (`/vocab-quiz`)

Files: `UI/Pages/VocabQuiz.razor` (2956 lines); `Shared/Models/VocabularyQuizItem.cs`; `Shared/Services/{VocabQuizLaunchValidator,VocabQuizPhotoTextPolicy,VocabQuizSentenceHintPolicy,RoundSampler,FuzzyMatcher}.cs`; `Shared/Models/VocabQuizSessionSnapshot.cs`; `Shared/Services/ActivitySessionService.cs`; E2E `quiz-activities.md` section 1; LVG skill.

Query: `resourceIds` (csv), `skillId`, `planItemId`, `DueOnly`, `FocusVocabularyIds` (`VocabQuiz.razor:647-658`).

### 2.1 Modes, directions, modalities, settings

- Interaction mode per word per turn: `VocabularyQuizItem.ChooseInteractionMode()` (`Shared/Models/VocabularyQuizItem.cs:115-133`): MultipleChoice if `PendingRecognitionCheck`; for plan focus words (`RequiresFullSessionDemonstration`): "Text" if known-word shortcut else Text only after 3 recognition demonstrations; otherwise Text when `CurrentStreak >= 3` or `MasteryScore >= 0.50`, else MultipleChoice.
- Direction per turn: `ShouldUseNativePrompt()` (`VocabQuiz.razor:2940-2945`): NativeToTarget -> native prompt; Mixed -> coin flip each turn; TargetToNative (default) -> target prompt.
- Prompt modality x response modality (per Learning Value Gate): text prompt, audio prompt (speaker button, speaks the PROMPT side only, `GetPromptAudioText/Language` `:2894-2900`, anti-cheat #193), photo prompt (mnemonic image); response is MC (4 options: 3 distractors drawn from the full distractor scope + correct, `GenerateChoiceOptions` `:2127-2155`) or typed Text with fuzzy match.
- Photo/text policy (`Shared/Services/VocabQuizPhotoTextPolicy.cs`): text hidden ONLY when image present, photo prompt active, image visible, prompt is NATIVE and `showTextWithPhoto` is false and no answer feedback; target-language prompts always show the target text. Heading becomes "Look and answer" / "Listen, look and answer" (`VocabQuiz.razor:2861-2885`). Toolbar "Show/Hide Text" only when `IsTextToggleEligible` (image + photo prompt active + native prompt) (`:24-34, 2826`); saves to profile, reverts and toasts on save failure (`:2661-2692`).
- Audio-choice mode: when prompt is native, audio prompt pref on, and every option has playable audio, MC buttons are labeled "Option A/B/..." with a speaker button each (`:386-401, 784-787, 2902-2915`).
- Text prompt off + audio/photo only -> instruction headings (ListenAndAnswer / LookAndAnswer); if everything would be off, text is forced on (`ApplyPromptPreferences` `:2798-2821`).
- Sample-sentence hint: button `#quiz-sentence-hint-button` only on target-prompt turns with eligible hints; panel `#quiz-sentence-hint-panel` lists up to 3 target-only sentences (no translations) ordered by CEFR level; prefetched once per batch via `ExampleSentenceRepo.GetQuizHintsForWordsAsync(userId, wordIds, cefr, 3)` (`:1625-1700`; policy `VocabQuizSentenceHintPolicy.cs`, MaxHintsPerTurn 3). Expansion survives feedback on the same turn, resets on next turn/restart/resume/fullscreen. Details in E2E 1.3.
- Mnemonic image toggle button (eye / image icon), thumbnail `#quiz-photo-thumbnail` -> fullscreen viewer `#quiz-fullscreen-viewer` (native PhotoViewer first via `PhotoViewer.TryShowNativeAsync`, else Razor overlay with pinch/pan gesture module `photo-viewer-gestures.js`, Escape closes, focus restore) (`:2502-2552, 620-640`).
- Mnemonic TEXT shows after a correct answer when `ShowMnemonicImage` pref on (`:2275`).
- Info panel (`#quiz-info-button` -> bottom offcanvas `#quiz-info-panel`, `:482-560`): status badge, TotalAttempts, CorrectAttempts, Accuracy, CurrentStreak, ProductionInStreak, EffectiveStreak, MasteryScore, SRS (NextReviewDate, ReviewInterval, EaseFactor, IsDueForReview), Quiz mode, DueOnly, and "Why this mode". Never shows target or native term (anti-cheat #194).
- Batch/round constants: BatchSize 20, ActiveWordCount 10, TurnsPerRound 10 (`:700-702`).

### 2.2 States

- Launch refused (`launchError`, banner + Done button): no active user, unowned/missing resource or skill, plan item invalid, focus words unresolved (`:44-52, 1592-1612`; validator `VocabQuizLaunchValidator.ValidateRouteAsync`).
- Resume prompt (`showResumePrompt`): "Resume" / "Start fresh"; if saved references no longer reachable, `resumeRejected` hides Resume and shows warning (`:53-77, 1614-1623`).
- Loading spinner (`isBusy`), empty ("no vocabulary" with Go Back; also toast "All vocabulary in this resource is mastered!" when the pool is all Known/grace) (`:462-468, 962-1000`).
- In-progress: prompt, response, feedback banner, footer `n / roundWordOrder.Count` + correct badge (`:472-478`).
- Feedback: green/red banner "Correct!" / "Correct! Full form: ..." / "The answer is: X"; MC buttons colored; auto-advance after `AutoAdvanceDuration` unless typed-wrong (`CheckAnswer` `:2185-2307`).
- Typed-wrong: `requireCorrectTyping`, input cleared, Submit advances (`SubmitTextInput` `:2177-2183` calls `NextItem` without re-checking the retyped text), and "I was correct" override button (`:451-460, 2412-2450`).
- Round summary (`showSessionSummary`): correct, total, mastered, rounds + per-word list with check/x icons; buttons "Continue quiz" (new round) and "Done" (`:66-120`).
- All words mastered -> toast "All words mastered! Session complete.", session completed, auto GoBack (`SetupNewRound` `:1760-1771`).
- Offline: audio affordances hidden/disabled, no network needed for quiz grading (local); sentence shortcut needs AI (error toasts).
- No cancel/pause UI; leaving mid-session persists the snapshot and next launch prompts Resume.

### 2.3 Journey A: plan -> quiz -> complete -> return (happy path)

1. Dashboard (Today's Plan) -> tap "Vocabulary Review" plan card (`Index.razor:356`) -> `/vocab-quiz?skillId=..&planItemId=..&FocusVocabularyIds=..&Mode=SRS&DueOnly=True` (`LaunchPlanItem`).
2. `OnInitializedAsync` (`VocabQuiz.razor:827`): load profile; `LaunchValidator.ValidateRouteAsync` (owner check on resources/skill); read native/target language, CEFR, `showTextWithPhoto`; parse focus ids.
3. Because `planItemId` present: load words, then `PlanProgressService.ValidatePlanItemAsync(user, planItemId, VocabularyReview, resourceId?, skillId, wordIds)`; refuse on null (`:866-893`).
4. Build `launchContextKey` (plan item + focus ids + resources + DueOnly + skill); `SessionService.GetResumableAsync(userId,"VocabularyReview",key)`; if found -> resume prompt (journey C) else `LoadVocabulary` (`:898-936`).
5. `LoadVocabulary` (`:939`): words (focus words if provided, else resources, or full user vocab if DueOnly); drop unusable; read progress; unless focus mode drop Known and grace-period words and (DueOnly) keep unseen or `NextReviewDate<=now`; order by MasteryScore asc, random tiebreak, take 20; mark focus words `RequiresFullSessionDemonstration` and capture known-word shortcut baseline.
6. Start validated timer (`EnsureActivityTimerStartedAsync` `:1493`); prefetch sentence hints; `SetupNewRound` (`:1760`): removes `ReadyToRotateOut` words; focus mode uses `RoundSampler.NextWindow` (rotating cursor), legacy takes random 10; shuffle order; `LoadCurrentItem` and persist snapshot (`SaveSnapshotAsync`).
7. Turn: pick mode and direction, build MC options or text box, check audio availability, apply prompt prefs, autoplay prompt audio if `AutoPlayVocabAudio` (`LoadCurrentItem` `:1801-1855`).
8. Learner answers (tap option `SelectMultipleChoice` or type + Submit/Enter `CheckAnswer`). Text uses `FuzzyMatcher.Evaluate` (annotations, slash alternatives, typo tolerance 75 percent similarity / Levenshtein <= 2, short words stricter; `Shared/Services/FuzzyMatcher.cs`); MC is case-insensitive equality.
9. Session counters update; `pendingAttempt` built (Activity "VocabularyQuiz", DifficultyWeight Text 1.5 / MC 1.0, response time) but NOT yet saved (`:2263-2272`).
10. Auto-advance timer (or Next button) -> `NextItem` (`:2309`): `RecordPendingAttemptAsync` saves progress, updates IsKnown-loss tracking (re-qualified Known words get 14-day interval), verification probe for Familiar, invalidates vocab summary (`:2353-2410`). Mastered words rotate out immediately (`ReadyToRotateOut`).
11. After 10 turns / empty round: summary screen. "Continue quiz" -> `SetupNewRound`; "Done" -> `GoBack`.
12. `GoBack` (`:2473`): if on summary, complete the activity session; stop timer; `NavigateTo("/")`.
13. Dashboard shows plan item minutes (and Completed state once minutes >= estimate); vocabulary counts refreshed.
14. Restart: dashboard progress persists server/local DB; reopening the same plan item with an unfinished snapshot shows Resume.

### 2.4 Journey B: wrong answer paths

- Wrong MC: banner "The answer is: X", correct option green, chosen red; `PendingRecognitionCheck` unchanged; auto-advance.
- Wrong Text: `PendingRecognitionCheck = true` (next appearance forced to MC), `requireCorrectTyping` (no auto-advance), learner either submits any text to continue or taps "I was correct" (`OverrideAsCorrect`: `ApplyOverrideAsCorrect` clears PendingRecognitionCheck for both modes, flips pending attempt to correct, records it, banner "Marked correct", auto-advances).
- Fuzzy-accepted answer with annotation shows "Correct! Full form: ...".

### 2.5 Journey C: resume / start fresh

1. Launch with same context key finds `ActivitySession` InProgress (`ActivitySessionService.GetResumableAsync`, `Shared/Services/ActivitySessionService.cs`).
2. Prompt: Resume vs Start fresh. Snapshot references re-validated (`CountRejectedSnapshotReferences` against plan id, focus ids, resources, DueOnly, skill, reachable word ids); mismatch -> "Resume unavailable" + Start fresh only.
3. Resume (`ResumePreviousSessionAsync` `:1152`): restores per-word session counters, round order, cursor, counts, prompt direction and mode; sentence-hint state is NOT restored (collapsed); reloads current item with `preservePromptState: true`; if round was finished goes straight to summary.
4. Start fresh: `SessionService.AbandonAsync`, clear, `LoadVocabulary` (`StartFreshSessionAsync` `:1318`).
5. Snapshot written on every `SetupNewRound` and `NextItem` (`PersistSnapshotAsync` `:1358`); completed via `CompleteAsync` on summary-exit or all-mastered.

### 2.6 Journey D: "Use in a sentence" shortcut (sub-flow)

1. Header dropdown "Use in a sentence" (hidden after answer reveal / during retype) -> `isSentenceShortcutMode` card with target word, textarea ("Write at least one sentence first." toast if empty), Cancel (`:12-24, 1866-1900`).
2. Submit: split on newlines/separators (`SplitSentenceEntries` `:2067`); for each sentence `TeacherSvc.GradeTargetWordSentence` (AI) -> `GetTargetWordAnalysis`; per-sentence failure shows "couldn't confirm/grade" (`:1907-2065`).
3. Credited sentences (`UsageCorrect`) each record a Text attempt, Activity "VocabularyQuiz", weight 2.5, ContextType "Sentence"; session counters +1 each; `totalTurns += entries`, `correctCount += credited`.
4. Results list: badge counts toward mastery / needs another try, accuracy%, fluency%, usage explanation, suggested sentence, collapsible details; toasts (success / info / warning / error "No progress was changed"). Button becomes "Continue quiz" (returns to the same item, `ContinueAfterSentenceShortcut`).

### 2.7 Side effects

- `VocabularyProgress` + `VocabularyLearningContext` rows; quiz demonstration counters; passive cascade; plan-item minutes through timer; `ActivitySession` snapshot rows (only activity with resume); vocab summary cache invalidated; sync triggered by plan saves. No `UserActivity` row (so no streak contribution).

### 2.8 Learning Value Gate acceptance matrix

See E2E `quiz-activities.md` 1.2 rows A-G (direction x UsePhotoPrompt x ShowTextWithPhoto) and `learning-value-gate/SKILL.md`. In code the hide rule only applies to native prompts, so row C (target prompt, hidden text) is unreachable. Default first-run path (TargetToNative, text+audio on, photo off) is row A.

---------------------------------------------------------------------------

## 3. Vocabulary Matching (`/vocab-matching`)

Files: `UI/Pages/VocabMatching.razor`. Query: `resourceIds`, `planItemId`, `DueOnly`, `FocusVocabularyIds` (`:91-101`). Plan type VocabularyGame, route from plan has no resource.

- Modes: 8 pairs (focus ids -> first 8; else resource/due words excluding Known and grace period ordered by mastery asc, take 8; if empty, 8 random) (`:201-214`). One tile per native term and one per target term, shuffled (`:216-253`).
- Hide-native mode (default true `:117`): only target tiles selectable first; selecting a target reveals all unmatched native tiles; deselect hides them again; header overflow toggle "Show all / Hide native" (`:12-16, 275-318, 421-435`). Learning-value note: a target tile is always the first pick, so a target-language cue is always present (no native->native state).
- States: spinner "loading vocab" (`:41-47`), empty with Go Back (`:80-86`), status bar `matched/total` and miss count (`:51-54`), game complete trophy card with matched/misses + "Play again" (`:21-40`).
- Journey:
  1. Plan "Vocabulary Matching" card (or Choose-My-Own tile; tile passes `resourceIds`) -> `OnInitializedAsync` starts legacy timer (`:126-133`).
  2. `LoadVocabulary`; tiles render.
  3. Tap tile -> selected (blue); second tap -> `CheckMatchWithDelay` waits 600 ms (`:321-326`).
  4. `CheckForMatch` ALWAYS records an attempt (match = correct, mismatch = incorrect) against the first tile's word: Activity "VocabularyMatching", InputMode MultipleChoice, weight 0.8, ContextType Isolated (`:345-357`); Familiar words get verification probe; invalidates vocab cache.
  5. Match -> both tiles faded (opacity 0.3), `matchedPairs++`; mismatch -> deselect, `incorrectGuesses++`.
  6. All matched -> complete card. "Play again" or header "New game" reload a fresh 8 (`RestartGame` `:410`), timer keeps running.
  7. Back -> stop timer -> `/`.
- Notes: on a mismatch the progress penalty lands on whichever word was tapped first (not both) (`:339`); uses unscoped repo calls and `Preferences.Get("active_profile_id","")` (empty string is passed through rather than refused, `:197-199, 344`), so this page lacks the fail-closed owner checks the Quiz has; native build must not copy that.
- No timer widget, no summary beyond the card, no resume.

---------------------------------------------------------------------------

## 4. Cloze (`/cloze`)

Files: `UI/Pages/Cloze.razor`, `AppLib/Services/ClozureService.cs`, `Shared/Services/ClozureAnswerHelper.cs`, prompt `GetClozuresV2.scriban-txt`. Query: `resourceId`, `skillId`, `planItemId`, `DueOnly`, `FocusVocabularyIds` (`:147-160`). Plan: resource omitted (vocabulary-driven), `DueOnly=true`.

- Generation: AI makes 8 sentences with a missing word (`GetSentences(resourceId, 8, skillId, dueOnly, focusIds)`, `Cloze.razor:217`). Sources: focus words (required-first) -> due words from full pool (dueOnly or no resource) -> resource vocabulary (max 40 sampled, excludes grace period). Skill optional (grammar bias only). Self-healing: repairs `VocabularyWordAsUsed` to be a substring, ensures the correct answer is within exactly 5 guesses (`ClozureService.cs` ~:255-325).
- Modes/toggles: `inputMode` default "Text" with toggle button between "Type" and "Choices" (5 options) (`:424-427, 116-124`); not persisted; both can be switched mid-sentence.
- Prompt: sentence with `______` replacing the as-used form (fallback to dictionary form, else unmodified) plus native translation hint (`SetCurrentChallenge` `:243-273`). The translation is always shown (target + native on screen).
- Grading: MC strict case-insensitive; Text via `ClozureAnswerHelper.AreAnswersEquivalent` (lenient on Korean particles) (`:286-294`). Feedback "Correct!" or "The answer was ..."; correct reveals the full sentence. Correct auto-advances after 1200 ms; wrong stays (graded, input locked: Go disabled, MC buttons disabled) until the learner taps next/previous chevrons (`:365-370`).
- Side effects: `UserActivity` (Activity Clozure, accuracy 100/0, `:310-319`); `RecordAttemptAsync` for the first vocabulary match (Activity "Clozure", weight 1.2, ContextType Sentence, InputMode Text/MultipleChoice) when the challenge links a word; Familiar probe toasts "Confirmed! This one is solid." / "This one is tricky in context..." (`:332-354`). Cache invalidated.
- States: spinner "Loading sentences", empty "No cloze sentences" + Go Back (also toast), session summary (correct / incorrect / accuracy %) with "Continue practice" (reload 8 sentences, counters reset) and "Done" (`:12-37, 416-422`). Errors toast `Error: {message}`. No resume.
- Navigation row: previous/next chevrons and `n / total`; next on last sentence opens the summary even if some sentences were skipped (`:393-402`).
- Journey: plan card (or tile) -> `/cloze?...` -> timer starts -> spinner -> sentence 1 -> answer (type + Go, or tap choice) -> feedback -> (correct: auto-advance 1.2 s; wrong: tap next) -> ... -> summary -> Done -> `/`.
- Surprise: after a WRONG answer there is no retry on the same sentence; E2E doc says "Correct answer revealed" (matches) but states "MCQ or text input" without default noted.

---------------------------------------------------------------------------

## 5. Translation (`/translation`)

Files: `UI/Pages/Translation.razor`, `AppLib/Services/TranslationService.cs` (`GetTranslationSentences` :47-), `TeacherService.GradeTranslation`. Query: `resourceId`, `skillId`, `planItemId`, `FocusVocabularyIds` (NO `DueOnly`) (`:215-225`).

- Direction: prompt is the NATIVE sentence (`currentSentence = challenge.RecommendedTranslation`, `:386`); learner types the TARGET-language translation (placeholder "Translate to {targetLanguage}"). Reference translation `challenge.SentenceText` is passed to the grader (`:428-431`).
- Generation: AI (`GetTranslations.scriban-txt`), 5 sentences; requires a resource OR focus ids, and requires a skill unless focus ids exist (otherwise empty -> "No sentences" state) (`TranslationService.cs:75-94`). Grace-period words excluded; max 40 vocab sample.
- Intro screen (`showIntro`) before the first sentence when any of resource title / vocabulary / grammar bullets exists: resource name, up to 12 vocabulary (focus words first), up to 8 grammar bullets parsed from the skill description (stops at examples) and a Start button (`:47-91, 316-374`).
- Input modes: "Text" (default per sentence) or "Blocks" (button toggles; shows vocabulary term chips that append the word to the input), reset to Text on each sentence (`:166-176, 393, 537-545`).
- Grading: `TeacherSvc.GradeTranslation` (Reasoning tier). Feedback card: user's text, Accuracy% and Fluency% colored (>=80 green, >=50 warn), suggested translation, collapsible details (accuracy explanation, fluency explanation, grammar notes) (`:95-146, 413-480`). Toast "Graded! Accuracy: n%".
- Side effects: `UserActivity` Translation (accuracy, fluency); `ExtractAndScoreVocabularyAsync` with activity "Translation" weight 1.5 against the current challenge vocabulary (`:448-468`); cache invalidated; session sums for summary.
- States: spinner, intro, active, empty ("no sentences" + Go Back + toast "No sentences returned from the translation service."), error toast on grade failure ("Error grading your translation. Please try again."), summary (graded count, avg accuracy, avg fluency) with Continue/Done (`:12-39, 505-514`).
- Navigation: prev/next chevrons, `n / total`; next on last = summary; grading is optional per sentence (you can skip). `LoadMoreSentences` exists but is unreferenced (`:516-535`).
- Journey: plan card -> `/translation?resourceId=..&skillId=..&planItemId=..&FocusVocabularyIds=..` -> spinner -> intro -> Start -> type -> Grade (spinner on button) -> feedback -> next ... -> summary -> Done -> `/`.

---------------------------------------------------------------------------

## 6. Writing (`/writing`)

Files: `UI/Pages/Writing.razor`; `Shared/Services/WritingActivityVocabularyFilter.cs`. Query: `resourceId`, `skillId`, `planItemId`, `FocusVocabularyIds` (`:172-182`).

- Mechanic: show 4 "vocabulary blocks" (target terms, only LexicalUnitType.Word entries; phrases and sentences filtered out per Captain directive, `:291-296`); learner writes any sentence (tap block appends the word); shuffle button re-draws 4 (focus mode keeps first 4) (`:298-311`).
- Source: focus ids (all user vocab) else the chosen resource (needs a resource; warnings "Please select a resource..." / "No vocabulary found in the selected resource.") excluding grace-period words (`:223-289`).
- Grade: `TeacherSvc.GradeSentence(userInput, "")` (no prompt context) -> list of previous sentences (newest last in code order) each with accuracy%, fluency%, "Suggested", collapsible details (`:313-392`). Records `UserActivity` Writer and `ExtractAndScoreVocabularyAsync` over ALL loaded vocabulary (weight 1.5). Toast "Graded! Accuracy: n%". Familiar probe toasts.
- Translate helper button (`TranslationSvc.TranslateAsync(userInput)` -> info toast with translation, `:394-408`).
- "End session" button appears after 3 graded sentences -> summary (graded, avg accuracy, avg fluency) -> Continue (clears list, reloads) / Done (`:159-166, 410-423`).
- States: spinner, empty ("No vocabulary" + Go Back), grading spinner on button, error toasts. No resume.
- Journey: plan card -> `/writing?...` -> blocks -> write -> Grade -> card appended, input cleared, new blocks -> repeat -> (>=3) End session -> summary -> Done -> `/`.

---------------------------------------------------------------------------

## 7. Describe a Scene (`/scene`)

Files: `UI/Pages/Scene.razor`, `Shared/Services/SceneImageService.cs`. Query: `fromPlan`, `targetMinutes`, `planItemId`, `resourceId`, `skillId` (`:213-226`). Reachable as a plan card (type SceneDescription exists; the deterministic plan builder does not pick it: `DeterministicPlanBuilder.cs` input/output pools, so only LLM-generated plans could) and Choose-My-Own tile.

- Image source: random `SceneImage` from the global table (`SceneImageService.GetRandomAsync` :87-103). The table has no user/owner column (`ListAsync` returns all rows), so scene images are shared across accounts in this DB (derived from code; ownership model unconfirmed).
- Interaction: single-line description input + Grade; "next image" refresh button (`LoadNewScene`); results list (newest first) with color-coded left border (>=80 green, >=50 warn, else red), accuracy badge, accuracy explanation, suggested sentence, expandable "More/Less" (fluency, fluency explanation, grammar notes) (`:43-99, 315-377`). Hard-coded English "More ▼ / Less ▲".
- AI: `TeacherSvc.GradeDescription(input, sceneDescription, targetLang, nativeLang)` where `sceneDescription` is the stored image `Description` (images added by URL have no description, so the reference text may be empty; `AddImage` does not generate one; unconfirmed that another path fills it).
- Side effects: `UserActivity` SceneDescription; `ExtractAndScoreVocabularyAsync` over `GetAllVocabularyWordsAsync()` (the entire vocabulary, not resource-scoped; weight 1.5); toast "Excellent / Good effort / Keep practicing - Accuracy: n%".
- Gallery overlay (`OpenGallery` toolbar icon): lists all images, tap to switch (clears results), paste URL + Add (`SaveAsync`), multi-select mode with delete (`:136-210, 381-488`); deleting the current image clears the scene to the empty state.
- States: loading spinner, empty ("no scene images" + Browse Gallery + Go Back), grading spinner, toasts for failures. No summary, no end screen; exit only via Back.
- Journey: tile/plan -> `/scene` -> random image -> type description -> Grade -> result card -> repeat or change image -> Back -> `/`.

---------------------------------------------------------------------------

## 8. Shadowing (`/shadowing`; also plan type Listening)

Files: `UI/Pages/Shadowing.razor`, `AppLib/Services/ShadowingService.cs`, `UI/Shared/WaveformDisplay.razor`. Query: `resourceId` (REQUIRED, silently returns if empty `:182`), `skillId`, `fromPlan`, `targetMinutes`, `planItemId` (`:109-122`).

- Content: `ShadowingService.GetOrGenerateSentencesAsync(resourceId, 10, skillId)`: if the resource has a transcript -> random sentences extracted from it (no translation, no AI); else AI generation from up to 10 random eligible vocabulary words (Word-type words via `GetShadowingSentences` prompt with pronunciation notes; Phrase/Sentence/Unknown terms used as-is, no AI) (`ShadowingService.cs:141-190, 60-135`).
- Per sentence: large target text, optional native translation (toggle button, hidden again on each sentence change, only if translation exists), italic pronunciation notes (`:32-52`).
- Audio: TTS generated per sentence at current speed (0.6 "Slow" or 1.0 "Normal") with the profile target-language voice (`GenerateAudioForCurrent` `:208-268`); play/pause toggle; replays from start if finished; changing speed regenerates audio and autoplays; waveform shown is a SEEDED PLACEHOLDER (`GeneratePlaceholderWaveform` `:270-277`), seek by tap on waveform (native player only). Progress timer 100 ms.
- There is NO microphone recording or pronunciation scoring; "shadowing" is listen-and-repeat only. No progress or UserActivity is recorded; only plan minutes.
- Audio failure is only logged (`:260-263`); the play button then calls `GenerateAudioForCurrent` again.
- States: generating spinner ("Generating sentences"), empty ("No sentences" + Go Back), buffering spinner on play button (buttons disabled). Prev/Next buttons with `n / total`; no end screen (Next disabled on last).
- Journey: plan card (Shadowing or Listening) or tile -> `/shadowing?resourceId=..` -> timer -> sentences -> first audio generated -> Play -> Next ... -> Back -> `/`.
- Surprise: a "Listening" plan item is not a listening comprehension drill (no questions); it is this same page.

---------------------------------------------------------------------------

## 9. Conversation (`/conversation`)

Files: `UI/Pages/Conversation.razor`, `AppLib/Services/ConversationService.cs`, `ScenarioService.cs`. Query: `scenarioId`, `fromPlan`, `targetMinutes`, `planItemId`, `resourceId`, `skillId` (resource/skill accepted but unused beyond timer, `:265-267`).

- Scenarios: `ScenarioService.GetAllScenariosAsync()`; seeded predefined list: First Meeting, Ordering Coffee, Ordering Dinner, Asking for Directions, Weekend Plans (`ScenarioService.cs:296-374`, personas e.g. Korean names). Default = `?scenarioId` else first scenario. Scenario `<select>` (changing restarts the conversation).
- Language: toolbar dropdown of `Constants.Languages` to change target language (restarts chat; not persisted to the profile) (`:12-35, 280-286`).
- Chat: AI opens (`StartConversation` template per scenario or default), learner types a message (Enter or Send), AI replies via `ContinueConversation` returning `Reply{Message, GrammarCorrections, Comprehension, VocabularyAnalysis}`. User bubble shows grammar corrections (struck original -> corrected, explanation) and "Comprehension: n%"; AI bubbles have a "Listen" TTS button (`:67-125, 317-377`).
- Quick phrases: toggle shows 8 HARD-CODED KOREAN helper phrases regardless of target language (`:224-243`).
- Side effects: vocabulary scoring via `ExtractAndScoreVocabularyAsync` over ALL vocabulary (weight 1.2, `PenaltyOverride` 0.8 softer penalty, `:356-365`); no `UserActivity`; conversation text is NOT saved from this page (ConversationService has save/resume methods but the page never calls them).
- TTS: `SpeechService.TextToSpeechAsync` to a temp file then `AudioMgr.CreatePlayer` (no JS fallback on this page; on the webapp the E2E doc notes `CreatePlayer()` returns null, so Listen is likely broken there: unconfirmed); Stop button while playing.
- States: "Starting conversation" spinner while no chunks, reply-pending indicator + disabled input, toast errors "Could not start conversation." / "Error starting conversation." / "Error getting response. Please try again." / "Audio playback failed."
- No summary, no end action; exit via Back.
- Journey: tile/plan -> `/conversation` -> scenario + persona opening line -> type reply -> Send -> AI reply + corrections -> ... -> Back -> `/`.

---------------------------------------------------------------------------

## 10. Reading (`/reading`)

Files: `UI/Pages/Reading.razor`, `UI/Shared/InteractiveText.razor`, `AppLib` `SpeechService.GenerateTimestampedAudioAsync`, `TimestampedAudioManager`, `SentenceTimingCalculator`. Query: `resourceId` (required: silently nothing if empty `:289`), `skillId`, `fromPlan`, `targetMinutes`, `planItemId`, `FocusVocabularyIds` (`:201-217`).

- Content: resource transcript split into sentences; error states "Resource not found.", "Resource has no transcript." with Go Back (`:293-324`).
- Interactive text: words are tappable; resource vocabulary words highlighted (`word--vocab`), plan focus words highlighted additionally (`word--focus-vocab`); single click selects/deselects a sentence; double click jumps audio to that sentence (first-time toast "Double-click a sentence to jump to it!", pref `ReadingActivity_HasShownJumpHint`) (`:686-709, InteractiveText.razor:3-30`).
- Word lookup bottom sheet: tapped word -> if in resource vocabulary show its native term and "In your vocab"; else AI/translate lookup with sentence context (`TranslationSvc.TranslateAsync`) and a `UserActivity` "Dictionary lookup" row; offers "Remember this word" -> creates `VocabularyWord`, links to the resource, adds `UserActivity` "Added vocabulary" (`:620-775`). Falls back to "(translation unavailable)".
- Audio: background `InitializeAudioAsync` generates timestamped TTS for the whole transcript (progress banner: Initializing / Generating / Processing timestamps / Loading), with sentence highlight + auto-scroll while playing; transport Prev / Play-Pause / Next, time `m:ss / m:ss`, sentence counter, speed select (0.5..2.0x), font-size slider 14..40 step 2 (`:340-428, 138-198`). JS audio fallback on webapp (`audioInterop`).
- Persisted prefs: `ReadingActivity_FontSize` (default 20), `ReadingActivity_PlaybackSpeed` (default 1.0), `ReadingActivity_HasShownJumpHint`. Surprise: the speed value is saved but never applied to playback anywhere in the page (`playbackSpeed` is only read/written at :281,542-543), so the speed selector is a no-op in the original.
- States: busy spinner, audio generating banner, "Audio generation failed - reading without audio." / "Audio error: ..." (read still works), transport disabled until audio loaded, toasts "Audio is still loading..." / "No audio available.".
- Side effects: `UserActivity` Reading on Back ("Reading session: {title}") and on lookups/saves; plan minutes via timer; no vocabulary mastery.
- Journey: plan card (Reading requires a resource with transcript) / tile -> `/reading?resourceId=..` -> text + audio loads -> tap words / play -> Back (stops audio, records session) -> `/`.

---------------------------------------------------------------------------

## 11. Video Watching (`/video-watching`)

Files: `UI/Pages/VideoWatching.razor`, `VideoWatchingService` (YouTube id extraction + embed URL). Query: `resourceId` (required), `fromPlan`, `targetMinutes`, `planItemId` (`:80-90`). Only reachable from a plan card (type VideoWatching; plan builder adds it only for resources with audio AND a YouTube URL `DeterministicPlanBuilder.cs:1038-1060`); not in Choose-My-Own.

- UI: resource title, "Open in YouTube" external link, 16:9 iframe embed (`allowfullscreen`), collapsible transcript card (Show/Hide) (`:18-62`).
- Errors (centered danger text + Go Back): "No resource specified.", "Resource not found.", "Could not extract YouTube video ID from this resource.", "Error loading video: ..." (`:121-157`); empty "no video".
- No playback tracking; the page timer runs for as long as the page is open (including paused video). No progress, no UserActivity.
- Journey: plan card -> `/video-watching?resourceId=..&planItemId=..` -> embed -> Back -> `/`.

---------------------------------------------------------------------------

## 12. Number Drill (`/numberdrill`)

Files: `UI/Pages/NumberDrill.razor` (+ `.razor.css`, `.razor.js` focus helper), `AppLib/Services/Numbers/{NumberSessionService,KoreanNumberItemGenerator,KoreanNumberAnswerGrader,KoreanNumberNormalizer,NumberAudioCueBuilder,NumberAudioCache}.cs`, seed `REF/lib/content/numbers/ko.json`, E2E `numberdrill.md`. Query: `DueOnly` (property exists but is never read; checkbox uses local `dueOnly`=false, `:487, 511, 669`), `ResourceId` (ignored/nulled `:553-558`), `PlanItemId`. Language hardcoded "ko" (`:673`), so Korean learners only.

- State machine Setup -> InSession -> Summary (`:497-499`). There are no `?context=&submode=` query parameters in this version (E2E doc stale: "context picker then /numberdrill?context=...&submode=...").
- Setup card: Context chips from DB (active): Counting, Time, Age, Money, Date, Ordinal, plus "Any"; Sub-mode chips from DB (`Phase <= 2`, active): ListenAndType, ReadAndProduce, TapTheCounter, Disambiguate, ListenAndPlace; valid combos enforced (`IsValidCombo` :630-646: TapTheCounter only with Counting; ListenAndPlace only with Time; neither with Any); item count stepper 5..20 step 5 (default 10); "Due items only" checkbox (default off); Start button; error text "No items available. Try different settings..." (`:30-110, 650-700`).
- Item generation: `NumberSessionService.StartSessionAsync(userId,"ko",request)` builds items from due/all `NumberMasteryProgress` rows for the user (and context), then pads with random unseen items from buckets "1-10"/"11-99" (`NumberSessionService.cs:31-133`). Session is held in an in-memory dictionary (`_activeSessions`), so there is no resume.
- Sub-mode UX:
  - ListenAndType: Play audio button (TTS of `AudioCue`, cached per phrase), hints accordion, text input with placeholder "Example pattern: ___시 ___분"; manual Submit.
  - ReadAndProduce: shows `DisplayPrompt` (digits/number), type the Korean words; hints; manual Submit.
  - TapTheCounter: noun cue + sentence frame with blank + counter chips; tap one -> graded immediately; auto-advance after 1500 ms.
  - ListenAndPlace: play audio (replay available) and choose among 3 time cards; auto-advance 1500 ms.
  - Disambiguate: two paired prompts (Sino vs Native) each with choice strip; Submit both enabled only when both chosen; both-or-nothing grading; feedback with per-prompt hints; manual Next.
- Feedback (text modes): verdict, "You typed", number (digits) and Korean canonical answer ALWAYS both shown, error class + tip when wrong, progress "x/y correct, next review {date}", "I was correct" override (wrong only) then Next/Finish (`:354-424`).
- Override (`:821-860`): flips the on-screen grade, increments the LOCAL correct counter, logs telemetry, auto-advances after 1.5 s; it does NOT re-write the stored attempt or mastery (already recorded as wrong). Surprise vs quiz override.
- Side effects: `NumberAttempt` row and `NumberMasteryProgress` update with SM-2 (quality from correctness + latency) each submit (`NumberSessionService.cs:135-250`); no `VocabularyProgress`; no `UserActivity`; plan minutes via timer. Plan builder picks NumberDrill as the STEP-4 closer when any `NumberMasteryProgress.DueDate <= tomorrow` exists (`DeterministicPlanBuilder.cs:796-830`).
- Summary: "Session Complete!", correct/total, median latency ms (actually running mean), "Patterns to Review" error-class badges, buttons Try again (back to Setup) and Back (to `/`) (`:427-468`). Title uses localization key `PlanItemNumberDrillTitle`; much of the rest is hard-coded English ("Choose your drill", "Items", "Due items only", "Submit", "Next", "Session Complete!") (no localization).
- Journey: plan closer card or tile -> `/numberdrill?...` -> timer -> setup -> choose context/mode/count -> Start -> items -> summary -> Try again or Back -> `/`.

---------------------------------------------------------------------------

## 13. How Do You Say (`/how-do-you-say`)

Files: `UI/Pages/HowDoYouSay.razor`. No query. Choose-My-Own tile only. No timer, no plan/progress/UserActivity.

- UI: input "Enter a phrase in {target language}" (Enter submits), voice dropdown from `IVoiceDiscoveryService.GetVoicesForLanguageAsync(targetLanguage)` (selection saved immediately per language), Speak button (spinner "Generating"), history list (play/pause toggle, delete) (`:11-86, 129-160, 298-303`).
- Submit: requires a voice (else warning toast "Please select a voice first."); TTS with stability 0.5/0.75/0.85; saves mp3 to `AudioCache` and a `StreamHistory` row; plays (JS or native); inserts at top of history; clears input; toast "Audio generated!" (`:163-232`).
- History: ALL `StreamHistory` rows via `GetAllStreamHistoryAsync()` ordered newest first, so it also lists phrases cached by Quiz, Number Drill and Minimal Pairs audio, and delete removes the shared cached file (`:145-160, 275-296`); owner scoping of StreamHistory is unconfirmed.
- States: history loading, empty (no list), error toasts ("Error: {msg}", "Playback error.", "Audio file not found.", "Error deleting item.").
- Journey: tile -> page -> type phrase -> Speak -> audio plays and history grows -> Back -> `/`.

---------------------------------------------------------------------------

## 14. Word Association (`/word-association`)

Files: `UI/Pages/WordAssociation.razor`, `AppLib/Services/WordAssociationService.cs`, entity `WordAssociationScore`. Query: `resourceIds` or `resourceId`, `skillId` (unused) (`:189-196`). Choose-My-Own tile only (multi-resource). No timer, no plan.

- Round: 5 random words from the chosen resources, grace-period words excluded (`GetRoundWordsAsync`, count 5; empty resources -> "No vocabulary found in selected resources.").
- Per word: shows target term and native gloss; learner types clues (one per submit/Enter), can remove ungraded clues; "Grade clues" sends all ungraded clues for the word to AI (`GradeCluesAsync`, Reasoning) returning per-clue `Related`, `IsCloze` (clue contains the target word, worth 2 points), explanation, corrected text. Badge green/red, explanation, "(Corrected: ...)", per-word score (`:56-130, 282-364`).
- Nav: previous (always), next/Finish enabled only when current clues are all graded (`:166-181`). Finish grades leftover clues.
- Scoring: related clue = 1 point, related cloze-style clue = 2; round summary: related, not related, score, plus high-score banners "New all-time high" / "Best this week" (`WordAssociationService.CheckHighScoreAsync/SaveRoundScoreAsync`, weekly = Monday-anchored) (`:384-418`).
- Side effects per word graded: `RecordAttemptAsync` (Activity "WordAssociation", InputMode Text, ContextType Association, weight 1.0, correct when >=50 percent of clues related); `UserActivity` WordAssociation (accuracy = related share); Familiar probe toasts. Cache invalidated.
- Fail-open note: `userId = Preferences.Get("active_profile_id","")` is used directly (`:292, 409`); `SaveRoundScoreAsync` would write a row with an empty `UserProfileId`. The native rebuild must fail closed.
- States: spinner, empty (+ Go Back), grading spinner "Grading clues", AI failure returns empty response (no clue graded) with toast "Error grading clues. Please try again." only on exception; summary with Play again / Done.
- Journey: tile (select resources first) -> `/word-association?resourceIds=..` -> 5 words -> clues -> Grade -> Next ... -> Finish -> summary (+ high score) -> Play again or Done -> `/`.

---------------------------------------------------------------------------

## 15. Minimal Pairs (`/minimal-pairs`, `/minimal-pairs/create`, `/minimal-pairs/session/{Id}`)

Files: `UI/Pages/MinimalPairs.razor`, `MinimalPairCreate.razor`, `MinimalPairSession.razor`; `Shared/Repositories/MinimalPairRepository.cs`, `MinimalPairSessionRepository.cs`; E2E `listening-activities.md` section 2. Entry: sidebar item, Choose-My-Own tile. No timer / plan / VocabularyProgress.

### 15.1 List (`/minimal-pairs`)
- Header: Back (to `/`), primary "Create pair"; mode buttons Focus (default) / Mixed; Focus requires selecting one pair card (toast "Please select a pair to practice in Focus mode."), Mixed starts with all pairs ("Start session (n pairs)") (`MinimalPairs.razor:17-39, 159-192`).
- Each card: word A vs word B (target terms), contrast label, delete button -> confirm modal "Delete A vs B?" -> `DeletePairAsync` -> toast "Pair deleted." (`:57-115, 194-232`).
- Empty: "no pairs" + "Create your first pair". Load error toast.
- Start navigates to `/minimal-pairs/session/0?pairIds=..&mode=Focus|Mixed&trials=10|20` (`:186-191`).
- DEFECT in original: list loads `GetUserPairsAsync("1")` (hard-coded user id "1", `:146`) while Create stores `userId = Preferences.Get("active_profile_id","")` (`MinimalPairCreate.razor:163-164`). Pairs created by a real profile would therefore not appear in the list (derived from repository filter `p.UserId == userId`, `MinimalPairRepository.cs:63-70`). Do not replicate.

### 15.2 Create (`/minimal-pairs/create`)
1. Load all vocabulary (`GetAllVocabularyWordsAsync`, not owner scoped by page).
2. Pick word A and word B from two searchable 5-row selects (search = target prefix or native contains), optional contrast label placeholder "e.g., ㅂ vs ㅃ".
3. Create enabled when both chosen; validation messages for missing or same word; `CreatePairAsync`; on success toast + back to list; `null` -> "Failed to create pair" (`:144-188`).

### 15.3 Session (`/minimal-pairs/session/{Id}`; Id is ignored, always "0")
1. Parse `pairIds`, `mode`, `trials` (default 20); load pairs; `SessionRepo.StartSessionAsync("1", mode, trials)` (hard-coded "1") (`:148-188`).
2. Each trial: random pair, random side as the PROMPT word, tiles left/right randomized; prompt is AUDIO ONLY (TTS of the prompt word with the global voice `VoicePrefs.VoiceId`, cached, plays automatically 500 ms after start and after each trial) (`:199-224, 226-295`).
3. Learner selects a tile, taps Check answer (enabled only with a selection; replay disabled after check). Result: green/red border tile styles, check icon on the correct tile (`:303-326, 343-387`).
4. `RecordAttemptAsync("1", ...)` stores an attempt; counts; after 1500 ms next trial + audio.
5. After `totalTrials` -> `EndSessionAsync` -> summary: correct, incorrect, accuracy %, duration minutes; Done -> `/minimal-pairs` (`:9-33, 334-338`).
- States: initializing spinner, empty ("no pairs") + Go Back, offline-uncached "Audio unavailable offline." inline text, "Audio playback error." text. Leaving mid-session never calls EndSession (session stays open in DB; unconfirmed cleanup).
- Learning-value note: the prompt is audio-only in the target language and the options are target-language words, so target exposure is guaranteed.

---------------------------------------------------------------------------

## 16. Flashcard Preview (`/flashcard-activity`)

Files: `UI/Pages/FlashcardActivity.razor`. Query: `date` (default today UTC). Entry: Dashboard plan insight "Preview plan vocabulary" (shown only when `insight.PreviewWords` exist) (`Index.razor:221-229`). Titled "Flashcard Preview" (hard-coded English).

- Data: cached plan's `Narrative.VocabInsight.PreviewWords` for that date (`PlanProgressService.GetCachedPlanAsync`); empty -> "No flashcard preview vocabulary is available for this daily plan." with Go Back (`:124-146`).
- Card: target term front, tap (movement <= 12 px) flips to native; drag horizontally >= 110 px to swipe right = correct, left = incorrect; buttons Incorrect/Correct do the same with a 160 ms animation (`:58-96, 161-246`).
- Counts: "Card n / total", correct and incorrect tallies. After last card summary list of each word with check/x and Done -> `/`.
- Side effects: NONE (results are in-memory only; no progress, no timer, no sessions).

---------------------------------------------------------------------------

## 17. Diary (`/diary`, `/diary/today`, `/diary/{Id}`)

Files: `UI/Pages/Diary.razor`, `DiaryEditor.razor`, `DiaryViewer.razor`; `DiaryEntryRepository`, `DiaryService` (AI). Entry: Choose-My-Own tile. No timer, no plan, no vocabulary progress, no `UserActivity` (so no streak).

1. List (`Diary.razor`): "Start/Continue today" card (shows today's word count), "Past entries" newest first with date label (Today/Yesterday/formatted), 120-char snippet, word count, sparkles icon when AI feedback exists; empty state text; reloads when route returns to `/diary` (`:25-90, 103-150`).
2. Editor (`/diary/today`): loads/creates today's entry for the target language (`GetByDateAsync(today, targetLanguage)`); a new entry immediately requests an AI prompt (`DiaryAi.GeneratePromptAsync`) shown in a collapsible banner (state pref `diary_prompt_collapsed`) with "New prompt" button; borderless textarea with font size A-/A+ (12..32 step 2, default 16, pref `diary_editor_font_size`); star meter from word count (goal 200 words: 50->1 star, 101->2, 151->3); autosave 5 s after last keystroke; manual Save; status Saving/Saved; Back and dispose flush a silent save (`DiaryEditor.razor:134-310`). Errors: toasts "Diary_SaveFailed" / "Diary_PromptFailed".
3. Viewer (`/diary/{Id}`): read-only entry with prompt and content; AI feedback panel: "Get feedback" / "Regenerate" -> `DiaryAi.GetFeedbackAsync(content, prompt, language)` -> saves Recommended (rewrite), Notes, Strengths; "not found" state; Back -> `/diary` (`DiaryViewer.razor:104-167`). Requires non-empty content.
- Journey: tile -> `/diary` -> Today card -> editor -> write (autosave) -> Back -> list -> open past entry -> Get feedback -> Back -> list -> Back -> `/`.

---------------------------------------------------------------------------

## 18. Live-only differences (checkout the developer's `main` checkout, main with ~25 uncommitted changes)

Method: `diff -rq REF/src <live>/src` excluding bin/obj/backup/.DS_Store/appsettings/logs; plus `diff -rq` of `.claude`, `.squad/skills`, `docs`, `lib`; plus `git diff` on the changed files.

- Activity code: **no differences**. All 45 `UI/Pages/*.razor` activity pages, every activity service, models, and the `.claude/skills/e2e-testing/references/*` activity references are byte-identical to the frozen reference except as listed.
- `src/SentenceStudio.UI/Pages/Resources.razor` (live-only, not an activity): grid/list virtualization wrappers (`resources-virtualized-grid`, `resources-virtualized-list`) and extra `Logger.LogDebug` calls; `UI/wwwroot/css/app.css` (+18 lines for those classes); `SentenceStudio.iOS.csproj` (pins `Microsoft.AspNetCore.Components.WebView` so Virtualize JS and .NET callbacks agree).
- `.claude/skills/e2e-testing/SKILL.md` and `references/management-pages.md`: live-only native-iOS testing guidance (Settings and Practice Log runtime plans, owner isolation). Not original behaviour.
- Untracked live-only: `docs/native-platform-claude-handoff.md` (native rebuild status, mentions Quiz control sizing and Activity Log checks; not source behaviour), `scripts/reference-resources-repair/`, `tests/SentenceStudio.Reference.Resources.Tests/`, `report.20260928...json`, `.squad/memory/*`.
- Nothing present only in live changes any activity entry point, mode, default or side effect.

---------------------------------------------------------------------------

## 19. Surprises, defects, and E2E-doc drift worth knowing before the rebuild

1. Plan completion is time-based (minutes >= estimate), not task-based (1.3). A learner can complete a plan item without answering anything.
2. The visible activity timer widget is dead UI in practice (`fromPlan` never set).
3. Only Vocab Quiz has owner-validated launch (`VocabQuizLaunchValidator`), validated plan item, explicit-user timer, and resume snapshots. All other pages use legacy unscoped repository calls and `Preferences.Get("active_profile_id","")` with empty-string fallthrough (Matching, Cloze, Translation, Writing, Scene, Conversation, Word Association, Minimal Pairs "1"). The native rebuild must fail closed everywhere.
4. Minimal Pairs hard-codes user id "1" in list/session/attempt but creates pairs with the real profile id (list/create mismatch).
5. Reading playback-speed selector persists but is never applied.
6. NumberDrill: `DueOnly` query ignored; text modes need manual Next while tap/time modes auto-advance at 1.5 s; override does not rewrite stored mastery; language hard-coded Korean; E2E doc (`numberdrill.md`) describes `?context=&submode=` URLs and 1.2 s auto-advance that do not exist.
7. Shadowing has no recording/scoring and uses a placeholder waveform; Listening plan items share it.
8. Conversation: quick phrases are Korean-only literals; transcript is not persisted; language switch is page-local.
9. Matching records an attempt for every pair tap including mismatches, attributing the miss to one word only.
10. Streak only counts days with a `UserActivity` row, which Quiz/Matching/NumberDrill/Shadowing/Conversation/Video never write.
11. Settings auto-advance slider range (1-10 s) exceeds the stored clamp (1-5 s).
12. Hard-coded English strings: NumberDrill, FlashcardActivity title, Scene "More/Less", many toasts (`"Graded! Accuracy:"` etc.) bypass localization.
13. Scene images and (probably) StreamHistory are not owner scoped; How Do You Say history shows cached audio from other activities.
14. Quiz typed-wrong "retype" step accepts any non-empty text (no validation).
15. Learning Value Gate: reachable states checked in code for Quiz (text hide only on native prompts), Matching (target tile first), Cloze (target sentence + native hint), Translation (native prompt -> target production), Minimal Pairs (target audio + target options). Writing/Scene/Conversation/Word Association all require target-language production. Hint accordions and Number Drill always show target-language answers after feedback.

## 20. Unconfirmed items (could not be proven from code read)

- Whether LLM plan generation (`LlmPlanGenerationService`) emits SceneDescription or Conversation plan items (deterministic builder never does).
- Whether the Activity Log merges non-UserActivity sources into streak/"minutes" visuals (only `ProgressService.GetStreakInfoAsync` was read).
- Owner scoping of `StreamHistory`, `SceneImage`, `Challenge` and scenario tables.
- `AutoPlaySampleAudio` / `ShouldPlaySampleAudio` have no consumer in the frozen src (only a string resource), so they are dead preferences.
- Conversation TTS behaviour on the webapp (no JS audio fallback in that page) and cleanup of un-ended Minimal Pairs sessions.
- Exact runtime copy of localized strings (resource files not exhaustively read).
- Scene image `Description` is never auto-generated in the Blazor path: `DescribeThisImage.scriban-txt` has no caller anywhere in `REF/src` (confirmed by grep), so URL-added images carry no reference description.
