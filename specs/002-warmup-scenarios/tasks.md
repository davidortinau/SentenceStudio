# Tasks: Warmup Conversation Scenarios

**Input**: Design documents from `specs/002-warmup-scenarios/`
**Prerequisites**: plan.md ✓, spec.md ✓, research.md ✓, data-model.md ✓, contracts/scenario-service.md ✓

**Tests**: Not explicitly requested - unit tests will be added in Polish phase

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3)
- Include exact file paths in descriptions

---

## Phase 1: Setup

**Purpose**: Database schema and model foundation

- [X] T001 Create `ConversationType` enum in `src/SentenceStudio.Shared/Models/ConversationType.cs`
- [X] T002 Create `ConversationScenario` entity model in `src/SentenceStudio.Shared/Models/ConversationScenario.cs`
- [X] T003 Add `ScenarioId` nullable FK property to `Conversation` model in `src/SentenceStudio.Shared/Models/Conversation.cs`
- [X] T004 Add `DbSet<ConversationScenario>` to `ApplicationDbContext` in `src/SentenceStudio.Shared/Data/ApplicationDbContext.cs`
- [X] T005 Create EF Core migration `AddConversationScenario` in `src/SentenceStudio.Shared/Migrations/`
- [X] T006 Verify build succeeds: `dotnet build -f net10.0-maccatalyst src/SentenceStudio/SentenceStudio.csproj`

**Checkpoint**: Database schema ready for scenario support ✅

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core infrastructure that MUST be complete before user story work

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

- [X] T007 Create `ScenarioRepository` with CRUD operations in `src/SentenceStudio/Data/ScenarioRepository.cs`
- [X] T008 Create `ScenarioIntent` enum in `src/SentenceStudio.Shared/Models/ScenarioIntent.cs`
- [X] T009 Create `ScenarioCreationState` class in `src/SentenceStudio.Shared/Models/ScenarioCreationState.cs`
- [X] T010 Create `IScenarioService` interface in `src/SentenceStudio/Services/IScenarioService.cs`
- [X] T011 Create `ScenarioService` implementing basic CRUD in `src/SentenceStudio/Services/ScenarioService.cs`
- [X] T012 Create dynamic Scriban prompt template in `src/SentenceStudio/Resources/Raw/Conversation.scenario.scriban-txt`
- [X] T013 Add `IsConversationComplete` property to `Reply` DTO in `src/SentenceStudio.Shared/Models/Reply.cs`
- [X] T014 Register `ScenarioService` in DI container in `src/SentenceStudio/MauiProgram.cs`
- [X] T015 Implement `SeedPredefinedScenariosAsync()` with 5 predefined scenarios in `src/SentenceStudio/Services/ScenarioService.cs`
- [X] T016 Call scenario seeding on app startup in `src/SentenceStudio/MauiProgram.cs`
- [X] T017 [P] Add localization keys for scenario UI to `src/SentenceStudio/Resources/Strings/AppResources.resx`
- [X] T018 [P] Add Korean translations for scenario UI to `src/SentenceStudio/Resources/Strings/AppResources.ko-KR.resx`
- [X] T019 Verify build and migration runs: test on macOS with `dotnet build -t:Run -f net10.0-maccatalyst`

**Checkpoint**: Foundation ready - user story implementation can begin ✅

---

## Phase 3: User Story 1 & 2 - Select Scenario + Scenario-Aware Conversations (Priority: P1) 🎯 MVP

**Goal**: Users can select predefined scenarios and AI adapts behavior accordingly

**Independent Test**: 
1. Open Warmup → Tap "Choose Scenario" → See 5 predefined scenarios
2. Select "Ordering Coffee" → AI greets as barista → Maintains persona throughout
3. Complete order → Finite scenario concludes naturally

### Implementation for User Stories 1 & 2

- [X] T020 [US1] Add `ActiveScenario` and `AvailableScenarios` to `WarmupPageState` in `src/SentenceStudio/Pages/Warmup/WarmupPage.cs`
- [X] T021 [US1] Add `IsScenarioSelectionShown` state property in `src/SentenceStudio/Pages/Warmup/WarmupPage.cs`
- [X] T022 [US1] Inject `IScenarioService` into `WarmupPage` in `src/SentenceStudio/Pages/Warmup/WarmupPage.cs`
- [X] T023 [US1] Add "Choose Scenario" toolbar item to `WarmupPage.Render()` in `src/SentenceStudio/Pages/Warmup/WarmupPage.cs`
- [X] T024 [US1] Implement `RenderScenarioSelectionSheet()` using SfBottomSheet in `src/SentenceStudio/Pages/Warmup/WarmupPage.cs`
- [X] T025 [US1] Implement `RenderScenarioItem()` for scenario list items in `src/SentenceStudio/Pages/Warmup/WarmupPage.cs`
- [X] T026 [US1] Implement `ShowScenarioSelection()` method to load and display scenarios in `src/SentenceStudio/Pages/Warmup/WarmupPage.cs`
- [X] T027 [US1] Implement `SelectScenario()` method to switch scenarios in `src/SentenceStudio/Pages/Warmup/WarmupPage.cs`
- [X] T028 [US2] Update `GetSystemPromptAsync()` to accept scenario parameter in `src/SentenceStudio/Services/ConversationService.cs`
- [X] T029 [US2] Modify `StartConversation()` to use scenario-aware prompts in `src/SentenceStudio/Services/ConversationService.cs`
- [X] T030 [US2] Add `StartConversationWithScenario()` method in `src/SentenceStudio/Pages/Warmup/WarmupPage.cs`
- [X] T031 [US2] Update `ContinueConversation()` to include scenario context in `src/SentenceStudio/Services/ConversationService.cs`
- [X] T032 [US2] Handle finite conversation completion (check `IsConversationComplete`) in `src/SentenceStudio/Pages/Warmup/WarmupPage.cs`
- [X] T033 [US1] Apply `.ThemeKey()` styling to scenario selection UI in `src/SentenceStudio/Pages/Warmup/WarmupPage.cs`
- [X] T034 [US1] Add `ILogger<WarmupPage>` logging for scenario selection in `src/SentenceStudio/Pages/Warmup/WarmupPage.cs`
- [ ] T035 [US1/US2] Test scenario selection and AI adaptation on macOS

**Checkpoint**: MVP complete - users can select scenarios and AI adapts. Verify independently. ✅

---

## Phase 4: User Story 3 - Create Scenario via Conversation (Priority: P2)

**Goal**: Users can create custom scenarios by describing them conversationally

**Independent Test**: Say "I want to create a scenario about buying medicine" → Answer questions → Scenario appears in list

### Implementation for User Story 3

- [X] T036 [US3] Implement `DetectScenarioIntent()` with keyword patterns in `src/SentenceStudio/Services/ScenarioService.cs`
- [X] T037 [US3] Implement `GetNextClarificationQuestionAsync()` state machine in `src/SentenceStudio/Services/ScenarioService.cs`
- [X] T038 [US3] Implement `ParseCreationResponseAsync()` to update state in `src/SentenceStudio/Services/ScenarioService.cs`
- [X] T039 [US3] Implement `FinalizeScenarioCreationAsync()` to save scenario in `src/SentenceStudio/Services/ScenarioService.cs`
- [X] T040 [US3] Add `ScenarioCreationState` to `WarmupPageState` in `src/SentenceStudio/Pages/Warmup/WarmupPage.cs`
- [X] T041 [US3] Modify `SendMessage()` to detect creation intent in `src/SentenceStudio/Pages/Warmup/WarmupPage.cs`
- [X] T042 [US3] Implement creation flow handling via `HandleScenarioCreationInput()` in `src/SentenceStudio/Pages/Warmup/WarmupPage.cs`
- [X] T043 [US3] Add confirmation UI for new scenario in `src/SentenceStudio/Pages/Warmup/WarmupPage.cs`
- [X] T044 [US3] Add localization keys for creation prompts to `AppResources.resx` and `AppResources.ko-KR.resx`
- [ ] T045 [US3] Test scenario creation flow on macOS

**Checkpoint**: Users can create custom scenarios conversationally ✅

---

## Phase 5: User Story 4 - Edit Scenario via Conversation (Priority: P3)

**Goal**: Users can modify their custom scenarios through conversation

**Independent Test**: Say "edit pharmacy scenario" → See current settings → Change to finite → Scenario updates

### Implementation for User Story 4

- [X] T046 [US4] Add edit intent detection to `DetectScenarioIntent()` in `src/SentenceStudio/Services/ScenarioService.cs`
- [X] T047 [US4] Implement `GetScenarioForEditing()` to find scenario by name in `src/SentenceStudio/Services/ScenarioService.cs`
- [X] T048 [US4] Add edit state tracking to `ScenarioCreationState` in `src/SentenceStudio.Shared/Models/ScenarioCreationState.cs`
- [X] T049 [US4] Modify `FinalizeScenarioCreationAsync()` to handle edit updates in `src/SentenceStudio/Services/ScenarioService.cs`
- [X] T050 [US4] Block editing of predefined scenarios (return error message) in `src/SentenceStudio/Services/ScenarioService.cs`
- [X] T051 [US4] Add edit flow handling via `StartScenarioEditing()` in `src/SentenceStudio/Pages/Warmup/WarmupPage.cs`
- [X] T052 [US4] Add delete scenario capability via `HandleDeleteScenario()` in `src/SentenceStudio/Pages/Warmup/WarmupPage.cs`
- [X] T053 [US4] Add localization keys for edit prompts to `AppResources.resx` and `AppResources.ko-KR.resx`
- [ ] T054 [US4] Test edit and delete flows on macOS

**Checkpoint**: Full scenario management via conversation complete ✅

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: Improvements that affect multiple user stories

- [ ] T055 [P] Add conversation completion dialog for finite scenarios in `src/SentenceStudio/Pages/Warmup/WarmupPage.cs`
- [ ] T056 [P] Add scenario indicator badge to WarmupPage showing active scenario
- [ ] T057 [P] Update feature documentation in `docs/warmup-scenarios.md`
- [ ] T058 Code cleanup: remove any debug logging, unused code
- [ ] T059 [P] Verify CoreSync works with ConversationScenario entity
- [ ] T060 Test on iOS simulator: scenario selection, AI adaptation, creation, editing
- [ ] T061 Test on Android emulator: scenario selection, AI adaptation, creation, editing
- [ ] T062 Test on Windows: scenario selection, AI adaptation, creation, editing
- [ ] T063 Run quickstart.md validation scenarios

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies - can start immediately
- **Foundational (Phase 2)**: Depends on Setup completion - BLOCKS all user stories
- **User Stories 1&2 (Phase 3)**: Depends on Foundational - combined as they're tightly coupled
- **User Story 3 (Phase 4)**: Depends on Foundational - can run in parallel with US1&2 if staffed
- **User Story 4 (Phase 5)**: Depends on US3 (uses same creation state machine)
- **Polish (Phase 6)**: Depends on all user stories being complete

### User Story Dependencies

- **User Stories 1 & 2 (P1)**: Combined - scenario selection + AI adaptation are inseparable
- **User Story 3 (P2)**: Independent - can start after Foundational
- **User Story 4 (P3)**: Depends on US3 - extends the creation state machine for editing

### Within Each User Story

- Models before services
- Services before UI
- Core implementation before integration
- Story complete before moving to next priority

### Parallel Opportunities

- T017 and T018 (localization) can run in parallel
- T055, T056, T057 (polish tasks) can run in parallel
- T060, T061, T062 (platform tests) can run in parallel

---

## Parallel Example: Phase 3 (MVP)

```bash
# These tasks can run in parallel (different sections of WarmupPage.cs):
T020: Add state properties
T022: Inject service

# These must be sequential (same method areas):
T023 → T024 → T025 → T026 → T027 (UI build-up)
T028 → T029 → T030 → T031 (service modifications)
```

---

## Implementation Strategy

### MVP First (User Stories 1 & 2 Only)

1. Complete Phase 1: Setup (T001-T006)
2. Complete Phase 2: Foundational (T007-T019)
3. Complete Phase 3: User Stories 1 & 2 (T020-T035)
4. **STOP and VALIDATE**: Test scenario selection and AI adaptation
5. Deploy/demo if ready - users can now practice with predefined scenarios!

### Incremental Delivery

1. Complete Setup + Foundational → Foundation ready
2. Add US1 & US2 → Test → **MVP deployed!** (5 predefined scenarios work)
3. Add US3 → Test → Users can create custom scenarios
4. Add US4 → Test → Users can edit their scenarios
5. Polish → Production ready

---

## Notes

- User Stories 1 & 2 are combined because scenario selection (US1) is meaningless without AI adaptation (US2)
- Predefined scenarios provide immediate value even without custom scenario creation
- The `Conversation.scenario.scriban-txt` template is the key to AI persona adaptation
- Test hot reload after each task - this branch includes hot reload fix!
