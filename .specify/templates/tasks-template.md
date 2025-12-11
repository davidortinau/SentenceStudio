---

description: "Task list template for feature implementation"
---

# Tasks: [FEATURE NAME]

**Input**: Design documents from `/specs/[###-feature-name]/`
**Prerequisites**: plan.md (required), spec.md (required for user stories), research.md, data-model.md, contracts/

**Tests**: The examples below include test tasks. Tests are OPTIONAL - only include them if explicitly requested in the feature specification.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3)
- Include exact file paths in descriptions

## Path Conventions

- **MAUI project**: `src/SentenceStudio/`, `tests/` at repository root
- **Pages**: `src/SentenceStudio/Pages/` (MauiReactor components)
- **Services**: `src/SentenceStudio/Services/` (business logic, AI integration)
- **Data**: `src/SentenceStudio/Data/` (SQLite repositories, models)
- **Resources**: `src/SentenceStudio/Resources/Strings/` (localization), `src/SentenceStudio/Resources/Styles/` (MyTheme.cs)
- **Shared**: `src/SentenceStudio.Shared/` (cross-project utilities)
- Paths shown below assume this structure - adjust based on plan.md if different

<!-- 
  ============================================================================
  IMPORTANT: The tasks below are SAMPLE TASKS for illustration purposes only.
  
  The /speckit.tasks command MUST replace these with actual tasks based on:
  - User stories from spec.md (with their priorities P1, P2, P3...)
  - Feature requirements from plan.md
  - Entities from data-model.md
  - Endpoints from contracts/
  
  Tasks MUST be organized by user story so each story can be:
  - Implemented independently
  - Tested independently
  - Delivered as an MVP increment
  
  DO NOT keep these sample tasks in the generated tasks.md file.
  ============================================================================
-->

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Project initialization and basic structure

- [ ] T001 Create project structure per implementation plan
- [ ] T002 Initialize [language] project with [framework] dependencies
- [ ] T003 [P] Configure linting and formatting tools

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core infrastructure that MUST be complete before ANY user story can be implemented

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

Examples of foundational tasks for MAUI projects (adjust based on your project):

- [ ] T004 Setup SQLite database schema and repository base classes in `src/SentenceStudio/Data/`
- [ ] T005 [P] Configure `MyTheme.cs` with required theme keys in `src/SentenceStudio/Resources/Styles/`
- [ ] T006 [P] Setup localization resources in `src/SentenceStudio/Resources/Strings/Resources.resx` and `Resources.ko.resx`
- [ ] T007 Create base page components and navigation structure in `src/SentenceStudio/Pages/`
- [ ] T008 Configure error handling and `ILogger<T>` infrastructure in `src/SentenceStudio/Services/`
- [ ] T009 Setup `appsettings.json` configuration management (template: `appsettings.template.json`)
- [ ] T010 [P] Configure AI service integration (OpenAI, Microsoft.Extensions.AI) in `src/SentenceStudio/Services/`
- [ ] T011 Verify build succeeds on all platforms: iOS, Android, macOS, Windows

**Checkpoint**: Foundation ready - user story implementation can now begin in parallel

---

## Phase 3: User Story 1 - [Title] (Priority: P1) 🎯 MVP

**Goal**: [Brief description of what this story delivers]

**Independent Test**: [How to verify this story works on its own]

### Tests for User Story 1 (OPTIONAL - only if tests requested) ⚠️

> **NOTE: Write these tests FIRST, ensure they FAIL before implementation**

- [ ] T010 [P] [US1] Contract test for [endpoint] in tests/contract/test_[name].py
- [ ] T011 [P] [US1] Integration test for [user journey] in tests/integration/test_[name].py

### Implementation for User Story 1

- [ ] T012 [P] [US1] Create [Entity1] model in `src/SentenceStudio/Data/Models/[Entity1].cs`
- [ ] T013 [P] [US1] Create [Entity2] model in `src/SentenceStudio/Data/Models/[Entity2].cs`
- [ ] T014 [US1] Implement [Repository] in `src/SentenceStudio/Data/Repositories/[Repository].cs` (depends on T012, T013)
- [ ] T015 [US1] Implement [Service] in `src/SentenceStudio/Services/[Service].cs`
- [ ] T016 [US1] Create [Page] component in `src/SentenceStudio/Pages/[Page].cs` using MauiReactor MVU
- [ ] T017 [US1] Add localization keys to `Resources.resx` and `Resources.ko.resx`
- [ ] T018 [US1] Apply theme styling using `.ThemeKey()` methods
- [ ] T019 [US1] Add `ILogger<T>` logging for service operations
- [ ] T020 [US1] Test on all platforms: iOS, Android, macOS, Windows

**Checkpoint**: At this point, User Story 1 should be fully functional and testable independently on all platforms

---

## Phase 4: User Story 2 - [Title] (Priority: P2)

**Goal**: [Brief description of what this story delivers]

**Independent Test**: [How to verify this story works on its own]

### Tests for User Story 2 (OPTIONAL - only if tests requested) ⚠️

- [ ] T018 [P] [US2] Contract test for [endpoint] in tests/contract/test_[name].py
- [ ] T019 [P] [US2] Integration test for [user journey] in tests/integration/test_[name].py

### Implementation for User Story 2

- [ ] T021 [P] [US2] Create [Entity] model in `src/SentenceStudio/Data/Models/[Entity].cs`
- [ ] T022 [US2] Implement [Repository] in `src/SentenceStudio/Data/Repositories/[Repository].cs`
- [ ] T023 [US2] Implement [Service] in `src/SentenceStudio/Services/[Service].cs`
- [ ] T024 [US2] Create [Page] component in `src/SentenceStudio/Pages/[Page].cs`
- [ ] T025 [US2] Add localization and theme styling
- [ ] T026 [US2] Integrate with User Story 1 components (if needed)
- [ ] T027 [US2] Test on all platforms: iOS, Android, macOS, Windows

**Checkpoint**: At this point, User Stories 1 AND 2 should both work independently on all platforms

---

## Phase 5: User Story 3 - [Title] (Priority: P3)

**Goal**: [Brief description of what this story delivers]

**Independent Test**: [How to verify this story works on its own]

### Tests for User Story 3 (OPTIONAL - only if tests requested) ⚠️

- [ ] T028 [P] [US3] Contract test for [endpoint] in tests/contract/test_[name].py
- [ ] T029 [P] [US3] Integration test for [user journey] in tests/integration/test_[name].py

### Implementation for User Story 3

- [ ] T030 [P] [US3] Create [Entity] model in `src/SentenceStudio/Data/Models/[Entity].cs`
- [ ] T031 [US3] Implement [Repository] in `src/SentenceStudio/Data/Repositories/[Repository].cs`
- [ ] T032 [US3] Implement [Service] in `src/SentenceStudio/Services/[Service].cs`
- [ ] T033 [US3] Create [Page] component in `src/SentenceStudio/Pages/[Page].cs`
- [ ] T034 [US3] Add localization and theme styling
- [ ] T035 [US3] Test on all platforms: iOS, Android, macOS, Windows

**Checkpoint**: All user stories should now be independently functional on all platforms

---

[Add more user story phases as needed, following the same pattern]

---

## Phase N: Polish & Cross-Cutting Concerns

**Purpose**: Improvements that affect multiple user stories

- [ ] TXXX [P] Documentation updates in `docs/specs/[feature]/`
- [ ] TXXX Code cleanup and refactoring
- [ ] TXXX Performance optimization across all stories
- [ ] TXXX [P] Additional unit tests (if requested) in `tests/SentenceStudio.Tests/`
- [ ] TXXX Security hardening (API key protection, data encryption)
- [ ] TXXX Accessibility review (screen reader support, high contrast)
- [ ] TXXX Final cross-platform testing on iOS, Android, macOS, Windows
- [ ] TXXX Run quickstart.md validation

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies - can start immediately
- **Foundational (Phase 2)**: Depends on Setup completion - BLOCKS all user stories
- **User Stories (Phase 3+)**: All depend on Foundational phase completion
  - User stories can then proceed in parallel (if staffed)
  - Or sequentially in priority order (P1 → P2 → P3)
- **Polish (Final Phase)**: Depends on all desired user stories being complete

### User Story Dependencies

- **User Story 1 (P1)**: Can start after Foundational (Phase 2) - No dependencies on other stories
- **User Story 2 (P2)**: Can start after Foundational (Phase 2) - May integrate with US1 but should be independently testable
- **User Story 3 (P3)**: Can start after Foundational (Phase 2) - May integrate with US1/US2 but should be independently testable

### Within Each User Story

- Tests (if included) MUST be written and FAIL before implementation
- Models before services
- Services before endpoints
- Core implementation before integration
- Story complete before moving to next priority

### Parallel Opportunities

- All Setup tasks marked [P] can run in parallel
- All Foundational tasks marked [P] can run in parallel (within Phase 2)
- Once Foundational phase completes, all user stories can start in parallel (if team capacity allows)
- All tests for a user story marked [P] can run in parallel
- Models within a story marked [P] can run in parallel
- Different user stories can be worked on in parallel by different team members

---

## Parallel Example: User Story 1

```bash
# Launch all tests for User Story 1 together (if tests requested):
Task: "Contract test for [endpoint] in tests/contract/test_[name].py"
Task: "Integration test for [user journey] in tests/integration/test_[name].py"

# Launch all models for User Story 1 together:
Task: "Create [Entity1] model in src/models/[entity1].py"
Task: "Create [Entity2] model in src/models/[entity2].py"
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup
2. Complete Phase 2: Foundational (CRITICAL - blocks all stories)
3. Complete Phase 3: User Story 1
4. **STOP and VALIDATE**: Test User Story 1 independently
5. Deploy/demo if ready

### Incremental Delivery

1. Complete Setup + Foundational → Foundation ready
2. Add User Story 1 → Test independently → Deploy/Demo (MVP!)
3. Add User Story 2 → Test independently → Deploy/Demo
4. Add User Story 3 → Test independently → Deploy/Demo
5. Each story adds value without breaking previous stories

### Parallel Team Strategy

With multiple developers:

1. Team completes Setup + Foundational together
2. Once Foundational is done:
   - Developer A: User Story 1
   - Developer B: User Story 2
   - Developer C: User Story 3
3. Stories complete and integrate independently

---

## Notes

- [P] tasks = different files, no dependencies
- [Story] label maps task to specific user story for traceability
- Each user story should be independently completable and testable
- Verify tests fail before implementing
- Commit after each task or logical group
- Stop at any checkpoint to validate story independently
- Avoid: vague tasks, same file conflicts, cross-story dependencies that break independence
