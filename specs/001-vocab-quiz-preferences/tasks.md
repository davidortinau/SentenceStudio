# Tasks: Vocabulary Quiz Preferences

**Input**: Design documents from `/specs/001-vocab-quiz-preferences/`
**Prerequisites**: plan.md (tech stack), spec.md (user stories), data-model.md (schema), quickstart.md (guide)

**Tests**: Tests are NOT requested in the specification. This implementation focuses on the feature itself with manual testing on all platforms.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each preference feature.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (US1, US2, US3, US4)
- Include exact file paths in descriptions

## Path Conventions

- **MAUI project**: `src/SentenceStudio/`, `src/SentenceStudio.Shared/`
- **Pages**: `src/SentenceStudio/Pages/VocabularyQuiz/`
- **Services**: `src/SentenceStudio/Services/`
- **Data**: `src/SentenceStudio.Shared/Data/`, `src/SentenceStudio.Shared/Models/`
- **Resources**: `src/SentenceStudio/Resources/Strings/`, `src/SentenceStudio/Resources/Styles/`

---

## Phase 1: Setup (Database Schema)

**Purpose**: Extend UserProfile schema with vocabulary quiz preferences

- [X] T001 Create VocabularyQuizPreferences service using .NET MAUI Preferences API (REVISED: No EF migration needed)
- [X] T002 Register VocabularyQuizPreferences as singleton in MauiProgram.cs (REVISED)
- [X] T003 Verify build succeeds: `cd src/SentenceStudio && dotnet build -f net10.0-maccatalyst` (PASSED)
- [X] T004 N/A - No migration to apply (using Preferences API instead)

**Checkpoint**: ✅ Preferences service created, registered in DI, build succeeds. Ready for localization.

---

## Phase 2: Foundational (Localization & Theme)

**Purpose**: Add localized strings and verify theme resources exist

**⚠️ CRITICAL**: These strings and theme keys are used by ALL user story phases

- [X] T005 [P] Add 12 English localization keys to `src/SentenceStudio/Resources/Strings/AppResources.resx` (COMPLETE)
- [X] T006 [P] Add 12 Korean translations to `src/SentenceStudio/Resources/Strings/AppResources.ko-KR.resx` (COMPLETE)
- [X] T007 Add IconSettings to `src/SentenceStudio/Resources/Styles/ApplicationTheme.Icons.cs`, verify IconClose exists (COMPLETE)
- [X] T008 Verify MyTheme theme keys exist: Title2, SubHeadline, Body1, PrimaryButton, Error (VERIFIED)

**Checkpoint**: ✅ All localization strings and theme resources ready for preferences UI components

---

## Phase 3: User Story 1 - Configure Quiz Display Direction (Priority: P1) 🎯 MVP

**Goal**: Allow users to choose whether quiz shows target→native or native→target language, with persistence across sessions

**Independent Test**: Open preferences, select display direction, save, start quiz, verify questions show selected language format, restart app, verify preference persists

### Implementation for User Story 1

- [X] T009 [US1] Create VocabularyQuizPreferencesBottomSheet component in `src/SentenceStudio/Pages/VocabularyQuiz/VocabularyQuizPreferencesBottomSheet.cs` with Props (Preferences, OnPreferencesSaved, OnClose) and State (DisplayDirection, IsSaving, ErrorMessage)
- [X] T010 [US1] Implement RenderDisplayDirectionSection() in VocabularyQuizPreferencesBottomSheet using RadioButton controls for "TargetToNative" and "NativeToTarget" options with localized labels
- [X] T011 [US1] Implement SavePreferencesAsync() method in VocabularyQuizPreferencesBottomSheet to update Preferences.DisplayDirection property
- [X] T012 [US1] Add State.UserPreferences (VocabularyQuizPreferences) and State.ShowPreferencesSheet (bool) fields to VocabularyQuizPageState, inject _preferences service
- [X] T013 [US1] Implement LoadUserPreferencesAsync() method in VocabularyQuizPage to load preferences from VocabularyQuizPreferences service
- [X] T014 [US1] Add OpenPreferences() and ClosePreferences() methods in VocabularyQuizPage to toggle State.ShowPreferencesSheet and implement OnPreferencesSaved callback
- [X] T015 [US1] Add toolbar icon (MyTheme.IconSettings) to VocabularyQuizPage.Render() that calls OpenPreferences() when clicked
- [X] T016 [US1] Integrate VocabularyQuizPreferencesBottomSheet into VocabularyQuizPage.Render() as SfBottomSheet overlay (conditional on State.ShowPreferencesSheet)
- [X] T017 [US1] Implement GetQuestionText(VocabularyWord) method in VocabularyQuizPage that returns word.TargetLanguageTerm or word.NativeLanguageTerm based on State.UserPreferences.VocabQuizDisplayDirection
- [X] T018 [US1] Implement GetCorrectAnswer(VocabularyWord) method in VocabularyQuizPage that returns opposite language term based on display direction
- [X] T019 [US1] Update ShowNextQuestion() logic in VocabularyQuizPage to use GetQuestionText() for question display (updated LoadCurrentItem)
- [X] T020 [US1] Update answer validation logic in VocabularyQuizPage to use GetCorrectAnswer() for checking user input (updated GenerateMultipleChoiceOptionsSync, validation uses State.CurrentTargetLanguageTerm)
- [X] T021 [US1] Call LoadUserPreferencesAsync() in VocabularyQuizPage.OnMounted() lifecycle method
- [X] T022 [US1] Add ILogger<VocabularyQuizPreferencesBottomSheet> injection and log preference saves/failures (✅/❌ emoji prefixes)
- [X] T023 [US1] Add ILogger logging to OpenPreferences(), ClosePreferences(), OnPreferencesSaved() in VocabularyQuizPage (⚙️ emoji prefix)
- [ ] T024 [US1] Test User Story 1 on macOS (net10.0-maccatalyst): change display direction, verify quiz questions reflect preference, verify persistence after app restart

**Checkpoint**: User Story 1 complete - display direction preference works end-to-end with persistence and logging. Users can customize quiz language presentation.

---

## Phase 4: User Story 2 - Configure Audio Playback (Priority: P2)

**Goal**: Allow users to enable/disable automatic vocabulary word audio playback with ElevenLabs TTS

**Independent Test**: Enable auto-play vocabulary audio in preferences, start quiz, verify Korean audio plays automatically when question appears, disable preference, verify no audio plays

### Implementation for User Story 2

- [ ] T025 [US2] Add State.AutoPlayVocabAudio (bool) field to VocabularyQuizPreferencesState in VocabularyQuizPreferencesBottomSheet component
- [ ] T026 [US2] Implement RenderAudioPlaybackSection() in VocabularyQuizPreferencesBottomSheet using CheckBox for "Auto-play vocabulary audio" with localized label
- [ ] T027 [US2] Update SavePreferencesAsync() in VocabularyQuizPreferencesBottomSheet to save UserProfile.VocabQuizAutoPlayVocabAudio from State.AutoPlayVocabAudio
- [ ] T028 [US2] Add State.VocabularyAudioPlayer (IAudioPlayer) field to VocabularyQuizPageState in VocabularyQuizPage
- [ ] T029 [US2] Add [Inject] IAudioManager _audioManager dependency to VocabularyQuizPage
- [ ] T030 [US2] Implement PlayVocabularyAudioAsync(VocabularyWord) method in VocabularyQuizPage: check State.UserPreferences.VocabQuizAutoPlayVocabAudio, load audio from StreamHistoryRepository cache or generate via ElevenLabsSpeechService, create IAudioPlayer, subscribe to PlaybackEnded event, play audio
- [ ] T031 [US2] Implement OnVocabularyAudioEnded(object, EventArgs) event handler in VocabularyQuizPage to cleanup audio player and unsubscribe event
- [ ] T032 [US2] Implement StopAllAudio() method in VocabularyQuizPage to stop and dispose State.VocabularyAudioPlayer
- [ ] T033 [US2] Update ShowNextQuestion() in VocabularyQuizPage to call StopAllAudio() before displaying new question, then call PlayVocabularyAudioAsync(currentWord)
- [ ] T034 [US2] Update OnWillUnmount() lifecycle in VocabularyQuizPage to call StopAllAudio() for cleanup
- [ ] T035 [US2] Add defensive null checks and try-catch in PlayVocabularyAudioAsync(): if AudioPronunciationUri is null/empty, log warning (⚠️) and return gracefully
- [ ] T036 [US2] Add ILogger logging to PlayVocabularyAudioAsync() (🎧 emoji) and error handling (❌ emoji)
- [ ] T037 [US2] Test User Story 2 on macOS (net10.0-maccatalyst): enable audio preference, verify audio plays automatically, disable preference, verify no audio plays, verify audio stops when navigating to next question

**Checkpoint**: User Story 2 complete - vocabulary audio auto-play works with ElevenLabs TTS integration, proper cleanup, and error handling. Works independently of US1.

---

## Phase 5: User Story 3 - Configure Sample Sentence Audio (Priority: P3)

**Goal**: Allow users to enable/disable sample sentence audio that plays after vocabulary audio completes

**Independent Test**: Enable both vocabulary audio and sample sentence audio in preferences, start quiz, verify vocabulary audio plays then sample sentence plays after, disable sample sentence audio, verify only vocabulary audio plays

### Implementation for User Story 3

- [ ] T038 [US3] Add State.AutoPlaySampleAudio (bool) field to VocabularyQuizPreferencesState in VocabularyQuizPreferencesBottomSheet component
- [ ] T039 [US3] Update RenderAudioPlaybackSection() in VocabularyQuizPreferencesBottomSheet to add CheckBox for "Auto-play sample sentence" with IsEnabled binding to State.AutoPlayVocabAudio (dependency), add localized "(requires vocabulary audio)" helper text
- [ ] T040 [US3] Update SavePreferencesAsync() in VocabularyQuizPreferencesBottomSheet to save UserProfile.VocabQuizAutoPlaySampleAudio from State.AutoPlaySampleAudio
- [ ] T041 [US3] Add State.SampleAudioPlayer (IAudioPlayer) field to VocabularyQuizPageState in VocabularyQuizPage
- [ ] T042 [US3] Add [Inject] ExampleSentenceRepository _exampleRepo dependency to VocabularyQuizPage
- [ ] T043 [US3] Implement SelectSampleSentence(List<ExampleSentence>) method in VocabularyQuizPage: filter sentences with non-null AudioUri, order by IsCore descending then CreatedAt ascending, return FirstOrDefault()
- [ ] T044 [US3] Implement PlaySampleAudioAsync(VocabularyWord) method in VocabularyQuizPage: load example sentences via _exampleRepo, call SelectSampleSentence(), create IAudioPlayer from sentence.AudioUri, play audio, log info (🎧 emoji) or warning if no sentence found (ℹ️ emoji)
- [ ] T045 [US3] Update OnVocabularyAudioEnded() event handler in VocabularyQuizPage to check State.UserPreferences.VocabQuizAutoPlaySampleAudio and call PlaySampleAudioAsync(currentWord) if enabled
- [ ] T046 [US3] Update StopAllAudio() method in VocabularyQuizPage to also stop and dispose State.SampleAudioPlayer
- [ ] T047 [US3] Add defensive null checks in PlaySampleAudioAsync(): if sentence is null or AudioUri is null/empty, log warning (⚠️) and return gracefully
- [ ] T048 [US3] Add try-catch error handling to PlaySampleAudioAsync() with ILogger logging (❌ emoji for failures)
- [ ] T049 [US3] Test User Story 3 on macOS (net10.0-maccatalyst): enable both audio preferences, verify vocabulary plays then sample sentence plays, disable sample sentence, verify only vocabulary plays, verify sample audio stops when navigating away

**Checkpoint**: User Story 3 complete - sample sentence audio chaining works with proper sentence selection (IsCore + CreatedAt), audio sequencing, and error handling. Works independently and integrates with US2.

---

## Phase 6: User Story 4 - Display Mnemonic Image (Priority: P3)

**Goal**: Allow users to show/hide mnemonic images when correct answers are confirmed

**Independent Test**: Enable mnemonic image preference, answer question correctly, verify mnemonic image appears in confirmation screen (if word has image), disable preference, verify no image shown on correct answer

### Implementation for User Story 4

- [ ] T050 [US4] Add State.ShowMnemonicImage (bool) field to VocabularyQuizPreferencesState in VocabularyQuizPreferencesBottomSheet component
- [ ] T051 [US4] Implement RenderConfirmationSection() in VocabularyQuizPreferencesBottomSheet using CheckBox for "Show mnemonic image" with localized label
- [ ] T052 [US4] Update SavePreferencesAsync() in VocabularyQuizPreferencesBottomSheet to save UserProfile.VocabQuizShowMnemonicImage from State.ShowMnemonicImage
- [ ] T053 [US4] Locate RenderAnswerConfirmation() or equivalent correct answer display method in VocabularyQuizPage
- [ ] T054 [US4] Update RenderAnswerConfirmation() in VocabularyQuizPage to conditionally display Image() component: check State.UserPreferences.VocabQuizShowMnemonicImage AND !string.IsNullOrEmpty(currentWord.MnemonicImageUri), set Image.Source to MnemonicImageUri, set HeightRequest 200, Aspect AspectFit
- [ ] T055 [US4] Add null check for State.UserPreferences before accessing VocabQuizShowMnemonicImage property to prevent null reference exceptions
- [ ] T056 [US4] Verify mnemonic image is cleared when advancing to next question (image should not persist from previous word)
- [ ] T057 [US4] Test User Story 4 on macOS (net10.0-maccatalyst): enable mnemonic image preference, answer correctly with word that has MnemonicImageUri, verify image displays, answer correctly with word without image, verify no broken placeholder, disable preference, verify no image shown

**Checkpoint**: User Story 4 complete - mnemonic image display works conditionally based on preference and image availability. Works independently of all other stories.

---

## Phase 7: Polish & Cross-Cutting Concerns

**Purpose**: Final integration, cross-platform testing, and documentation

- [ ] T058 [P] Update VocabularyQuizPreferencesBottomSheet component to use proper MauiReactor semantic alignment (.HStart(), .VCenter(), .HEnd()) per constitution
- [ ] T059 [P] Verify all UI elements in VocabularyQuizPreferencesBottomSheet use `.ThemeKey()` or MyTheme constants (no hardcoded colors/sizes)
- [ ] T060 [P] Verify all strings in VocabularyQuizPreferencesBottomSheet use `$"{_localize["Key"]}"` pattern (not bare `_localize["Key"]`)
- [ ] T061 Verify preferences sheet renders correctly as SfBottomSheet overlay on VocabularyQuizPage without blocking quiz content
- [ ] T062 Test complete feature on iOS (net10.0-ios): build, deploy, test all 4 user stories, verify audio permissions handled correctly
- [ ] T063 Test complete feature on Android (net10.0-android): build, deploy, test all 4 user stories, verify audio permissions handled correctly
- [ ] T064 Test complete feature on Windows (net10.0-windows10.0.19041.0): build, deploy, test all 4 user stories
- [ ] T065 Verify preferences persist across app restarts on all 4 platforms (iOS, Android, macOS, Windows)
- [ ] T066 Test edge case: change preferences mid-quiz, verify changes apply to NEXT question not current question (per FR-020)
- [ ] T067 Test edge case: vocabulary word with no AudioPronunciationUri, verify quiz continues without error (log warning ⚠️)
- [ ] T068 Test edge case: vocabulary word with no MnemonicImageUri, verify confirmation displays without broken placeholder
- [ ] T069 Test edge case: vocabulary word with no example sentences, verify sample audio gracefully skips (log info ℹ️)
- [ ] T070 [P] Add XML documentation comments to all new public methods in VocabularyQuizPreferencesBottomSheet and VocabularyQuizPage
- [ ] T071 [P] Update VocabularyQuizPage header comment to document new preferences functionality
- [ ] T072 Run quickstart.md validation: follow test cases 1-6, verify all acceptance scenarios pass
- [ ] T073 Measure and verify performance: preferences load <100ms, audio playback start <500ms, mnemonic image load <1s (per SC-003, SC-005)
- [ ] T074 [P] Update CHANGELOG.md with new vocabulary quiz preferences feature description
- [ ] T075 Code review: verify ILogger usage throughout, verify no `Debug.WriteLine()` in production code, verify async/await patterns correct

**Checkpoint**: All user stories tested on all platforms, edge cases handled, performance verified, documentation complete. Feature ready for PR/merge.

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies - can start immediately
- **Foundational (Phase 2)**: Depends on Setup completion (database schema must exist before localization)
- **User Stories (Phase 3-6)**: All depend on Foundational phase completion
  - US1 can start after Foundational
  - US2 can start after Foundational (integrates with US1 but independently testable)
  - US3 depends on US2 (requires vocabulary audio playback to exist for chaining)
  - US4 can start after Foundational (completely independent)
- **Polish (Phase 7)**: Depends on all 4 user stories being complete

### User Story Dependencies

- **User Story 1 (P1)**: Depends only on Foundational - completely independent
- **User Story 2 (P2)**: Depends only on Foundational - independent (can test without US1)
- **User Story 3 (P3)**: Depends on US2 completion (requires OnVocabularyAudioEnded event to exist)
- **User Story 4 (P3)**: Depends only on Foundational - completely independent

### Within Each User Story

- Component Props/State before Render methods
- Render methods before integration into parent page
- Parent page state fields before component integration
- Parent page methods before calling them from ShowNextQuestion/OnMounted
- Logging after functionality works
- Testing after all tasks in story complete

### Parallel Opportunities

- Phase 2 (Foundational): T005, T006 can run in parallel (different .resx files)
- Once Foundational completes: US1, US2, US4 can all start in parallel (US3 must wait for US2)
- Phase 7 (Polish): T058, T059, T060, T070, T071, T074 can run in parallel (different files)
- Cross-platform testing (T062, T063, T064) can run in parallel if test devices available

---

## Parallel Example: User Story 1

Since US1 tasks are sequential (component creation → integration → logic implementation), parallelization is limited. However, these can be done simultaneously by different developers:

```bash
# After T009 (component created), these can be parallel:
Task T010: Implement RenderDisplayDirectionSection() (UI rendering)
Task T022: Add ILogger injection and logging (infrastructure)

# After T012-T016 (VocabularyQuizPage integration), these can be parallel:
Task T017: Implement GetQuestionText() method
Task T018: Implement GetCorrectAnswer() method
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup (database schema) - ~30 min
2. Complete Phase 2: Foundational (localization, theme checks) - ~20 min
3. Complete Phase 3: User Story 1 (display direction) - ~90 min
4. **STOP and VALIDATE**: Test US1 on macOS independently
5. Deploy/demo if ready - users can now customize quiz language direction

**Total MVP Time**: ~2.5 hours
**MVP Delivers**: Core preference infrastructure + display direction customization

### Incremental Delivery

1. Complete Setup + Foundational → Preferences infrastructure ready (~50 min)
2. Add User Story 1 → Test independently → Deploy (MVP: display direction works) (~90 min)
3. Add User Story 2 → Test independently → Deploy (audio playback customization) (~90 min)
4. Add User Story 4 → Test independently → Deploy (mnemonic image customization) (~45 min)
5. Add User Story 3 → Test independently → Deploy (sample sentence audio chaining) (~90 min)
6. Polish phase → Test all platforms → Final deploy (~2 hours)

**Total Time**: ~7 hours (includes cross-platform testing and polish)

Each story adds independent value:
- US1: Customize question language (recognition vs production practice)
- US2: Audio pronunciation support
- US4: Visual memory aids
- US3: Contextual learning enhancement

### Parallel Team Strategy

With 2 developers after Foundational phase completes:

1. Team completes Setup + Foundational together (~50 min)
2. Once Foundational done:
   - **Developer A**: US1 (display direction) → US3 (sample audio, after US2 done)
   - **Developer B**: US2 (vocabulary audio) → US4 (mnemonic images)
3. Stories complete independently, integrate smoothly
4. Both developers collaborate on Phase 7 polish and cross-platform testing

**Parallel Time**: ~4 hours (vs 7 hours sequential)

---

## Notes

- [P] tasks = different files, can run in parallel
- [Story] label maps task to specific user story (US1, US2, US3, US4)
- Each user story delivers independent value and can be tested standalone
- US3 is the only story with a dependency (requires US2's audio playback)
- Commit after each completed task or logical group
- Stop at any checkpoint to validate story independently on macOS first
- Cross-platform testing happens in Polish phase for all features together
- Avoid: hardcoded values, skipping localization, missing null checks, forgetting ILogger
- Follow quickstart.md test scenarios for validation
