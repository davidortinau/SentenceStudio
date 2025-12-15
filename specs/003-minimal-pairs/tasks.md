---

description: "Task list for Minimal Pairs Listening Activity"
---

# Tasks: Minimal Pairs Listening Activity

**Input**: Design documents from `specs/003-minimal-pairs/`  
**Prerequisites**: `spec.md`, `plan.md` (required); `research.md`, `data-model.md`, `contracts/`, `quickstart.md` (optional)

**Tests**: Not generating automated test tasks (not explicitly requested). Use the manual validation tasks included below.

## Phase 1: Setup (Shared Infrastructure)

- [X] T001 Confirm `dotnet ef` availability and EF toolchain setup (use `dotnet tool restore` if applicable; do not hand-write migrations)
- [X] T002 [P] Create Minimal Pairs pages folder `src/SentenceStudio/Pages/MinimalPairs/` and placeholder files for compilation order

---

## Phase 2: Foundational (Blocking Prerequisites)

- [X] T003 Implement global voice preference service in `src/SentenceStudio/Services/SpeechVoicePreferences.cs` (backed by `Preferences`, exposes `VoiceId`)
- [X] T004 Wire global voice preference into DI in `src/SentenceStudio/MauiProgram.cs`
- [X] T005 Add migration helper for global voice defaulting (copy legacy quiz voice → global if unset) in `src/SentenceStudio/Services/SpeechVoicePreferences.cs`
- [X] T006 Move Vocabulary Quiz settings UI into Settings in `src/SentenceStudio/Pages/AppSettings/SettingsPage.cs` (including voice selection UI for global voice)
- [X] T007 Remove Vocabulary Quiz preferences bottom sheet UI and entry points in `src/SentenceStudio/Pages/VocabularyQuiz/VocabularyQuizPage.cs`
- [X] T008 Update quiz preferences service to stop owning voice selection in `src/SentenceStudio/Services/VocabularyQuizPreferences.cs` (read global voice instead)
- [X] T009 Update Vocabulary Quiz audio generation to use global voice in `src/SentenceStudio/Pages/VocabularyQuiz/VocabularyQuizPage.cs`
- [X] T010 Update Edit Vocabulary Word audio generation default to use global voice in `src/SentenceStudio/Pages/VocabularyManagement/EditVocabularyWordPage.cs`
- [X] T011 Update HowDoYouSay default voice to global voice (keep picker UI unchanged) in `src/SentenceStudio/Pages/HowDoYouSay/HowDoYouSayPage.cs`
- [X] T012 [P] Add Settings + voice related localization keys in `src/SentenceStudio/Resources/Strings/AppResources.resx` and `src/SentenceStudio/Resources/Strings/AppResources.ko-KR.resx`

**Checkpoint**: Global voice preference exists, quiz sheet removed, and all affected pages default correctly.

---

## Phase 3: User Story 1 - Practice minimal pair identification (Priority: P1)

**Goal**: Start a minimal pair session, play prompt audio, answer between two choices, get immediate feedback, and see running correct/incorrect counts.

**Independent Test**: Start a session from the Minimal Pairs flow, answer several prompts, verify replay doesn’t advance, and verify counters update.

- [ ] T013 [P] [US1] Create Minimal Pairs landing page skeleton in `src/SentenceStudio/Pages/MinimalPairs/MinimalPairsPage.cs` (pair selection + Start)
- [ ] T014 [P] [US1] Create session page skeleton in `src/SentenceStudio/Pages/MinimalPairs/MinimalPairSessionPage.cs` (two choices + replay + counters)
- [ ] T015 [US1] Implement prompt selection for a single pair (random side per trial) in `src/SentenceStudio/Pages/MinimalPairs/MinimalPairSessionPage.cs`
- [ ] T016 [US1] Implement audio playback + playing-state UI using existing app pattern in `src/SentenceStudio/Pages/MinimalPairs/MinimalPairSessionPage.cs` (ElevenLabs + stream history cache + `Plugin.Maui.Audio`)
- [ ] T017 [US1] Debounce answer input so one prompt cannot be double-counted in `src/SentenceStudio/Pages/MinimalPairs/MinimalPairSessionPage.cs`
- [ ] T018 [US1] Add immediate feedback UI that does not rely on text color alone in `src/SentenceStudio/Pages/MinimalPairs/MinimalPairSessionPage.cs`
- [ ] T019 [US1] Dispose/stop audio player on page unmount in `src/SentenceStudio/Pages/MinimalPairs/MinimalPairSessionPage.cs`
- [ ] T020 [P] [US1] Add Minimal Pairs localization keys in `src/SentenceStudio/Resources/Strings/AppResources.resx` and `src/SentenceStudio/Resources/Strings/AppResources.ko-KR.resx`

---

## Phase 4: User Story 4 - Create and manage minimal pairs from vocabulary (Priority: P2)

**Goal**: Create a minimal pair from two existing vocabulary words and persist it.

**Independent Test**: Create a pair, verify it appears in the list after app restart; delete it and verify it’s removed.

- [X] T021 [P] [US4] Add EF Core entities in `src/SentenceStudio.Shared/Models/MinimalPair.cs`, `src/SentenceStudio.Shared/Models/MinimalPairSession.cs`, `src/SentenceStudio.Shared/Models/MinimalPairAttempt.cs`
- [X] T022 [US4] Register DbSets + constraints in `src/SentenceStudio.Shared/Data/ApplicationDbContext.cs` (unique `(UserId, VocabularyWordAId, VocabularyWordBId)` + indexes)
- [X] T023 [US4] Generate migration using `dotnet ef migrations add AddMinimalPairs` targeting `src/SentenceStudio.Shared/` and output to `src/SentenceStudio.Shared/Migrations/` (do not hand-write migration files)
- [X] T024 [P] [US4] Implement minimal pair CRUD repository in `src/SentenceStudio/Data/MinimalPairRepository.cs`
- [X] T025 [US4] Add DI registration for minimal pair repository in `src/SentenceStudio/MauiProgram.cs`
- [ ] T026 [US4] Implement create/delete pair UI in `src/SentenceStudio/Pages/MinimalPairs/MinimalPairsPage.cs` (select two vocab words + optional contrast label)
- [X] T027 [US4] Enforce pair normalization (A < B) and prevent duplicates in `src/SentenceStudio/Data/MinimalPairRepository.cs`

---

## Phase 5: User Story 2 - See session results and progress summary (Priority: P2)

**Goal**: Show an end-of-session summary and persist results for trend over time.

**Independent Test**: Complete a short session, verify summary totals, restart app, verify history data remains.

- [ ] T028 [US2] Add session summary UI (totals + accuracy + duration) in `src/SentenceStudio/Pages/MinimalPairs/MinimalPairSessionPage.cs`
- [X] T029 [US2] Implement session/attempt repository in `src/SentenceStudio/Data/MinimalPairSessionRepository.cs` (start/end session + record attempt)
- [X] T030 [US2] Add DI registration for session repository in `src/SentenceStudio/MauiProgram.cs`
- [ ] T031 [US2] Persist session lifecycle (start/end) in `src/SentenceStudio/Pages/MinimalPairs/MinimalPairSessionPage.cs`
- [ ] T032 [US2] Record attempts on answer (prompt word id, selected id, correctness, sequence) in `src/SentenceStudio/Pages/MinimalPairs/MinimalPairSessionPage.cs`
- [ ] T033 [US2] Add offline error messaging when uncached audio is unavailable in `src/SentenceStudio/Pages/MinimalPairs/MinimalPairSessionPage.cs`

---

## Phase 6: User Story 5 - View minimal pair history and success rate (Priority: P2)

**Goal**: Show per-pair accuracy and recent history.

**Independent Test**: Practice the same pair across multiple sessions; open details and verify totals and session-by-session history.

- [ ] T034 [US5] Create pair details page in `src/SentenceStudio/Pages/MinimalPairs/MinimalPairDetailsPage.cs` (stats + recent sessions)
- [ ] T035 [US5] Implement stats aggregation queries in `src/SentenceStudio/Data/MinimalPairRepository.cs` (correct/incorrect/accuracy)
- [ ] T036 [US5] Implement history queries in `src/SentenceStudio/Data/MinimalPairSessionRepository.cs` (recent sessions for pair)
- [ ] T037 [US5] Add navigation from list to details in `src/SentenceStudio/Pages/MinimalPairs/MinimalPairsPage.cs`

---

## Phase 7: User Story 3 - Choose practice mode (focus vs mixed) (Priority: P3)

**Goal**: Support focus mode and mixed/adaptive mode.

**Independent Test**: Start focus mode (single pair only); start mixed mode (pairs sampled from a set; more weight on missed pairs).

- [ ] T038 [US3] Add mode selection UI (Focus/Mixed) in `src/SentenceStudio/Pages/MinimalPairs/MinimalPairsPage.cs`
- [ ] T039 [US3] Implement warm-up then mixed/adaptive sampler in `src/SentenceStudio/Services/MinimalPairsSampler.cs`
- [ ] T040 [US3] Use sampler in session loop in `src/SentenceStudio/Pages/MinimalPairs/MinimalPairSessionPage.cs`

---

## Phase 8: User Story 6 - Start activity from home (Priority: P3)

**Goal**: Minimal Pairs is discoverable and launchable from the home dashboard.

**Independent Test**: From Dashboard, tap Minimal Pairs and start a session.

- [ ] T041 [US6] Add Minimal Pairs activity tile to dashboard in `src/SentenceStudio/Pages/Dashboard/DashboardPage.cs`
- [ ] T042 [US6] Register routes for minimal pairs pages in `src/SentenceStudio/MauiProgram.cs`

---

## Phase 9: Polish & Cross-Cutting Concerns

- [ ] T043 Add required icons for Minimal Pairs (if missing) in `src/SentenceStudio/Resources/Styles/ApplicationTheme.Icons.cs`
- [ ] T044 Add remaining localization keys used by new pages in `src/SentenceStudio/Resources/Strings/AppResources.resx` and `src/SentenceStudio/Resources/Strings/AppResources.ko-KR.resx`
- [ ] T045 Manual cross-platform validation on iOS/Android/macOS/Windows (build + smoke test Minimal Pairs + Settings voice)
- [ ] T046 Run quickstart checklist in `specs/003-minimal-pairs/quickstart.md` and update any discovered gaps

---

## Dependencies & Execution Order

### Story completion order

```text
Setup/Foundational
  └─> US1 (Practice)
	  ├─> US6 (Home entry)
	  └─> US4 (Persist pairs)
		  └─> US2 (Persist sessions + summary)
			  └─> US5 (History + success rate)
				  └─> US3 (Mixed/adaptive mode)
```

- Foundational → US1
- US4 → US2 → US5 (persistence and history)
- US3 depends on US4+US2 for mixed sampling + attempt weighting
- US6 depends on US1 (pages/routes exist)

### Parallel opportunities

- T003–T012 are mostly sequential (settings + voice migration is intertwined), but UI/localization tasks can be done while services are in progress if coordinated.
- Within Minimal Pairs, page skeletons and repositories can proceed in parallel once entities are defined.

## Parallel Execution Examples

### US1

Run in parallel (if staffed): T013, T014, T020

### US4

Run in parallel (if staffed): T021, T024, T026
