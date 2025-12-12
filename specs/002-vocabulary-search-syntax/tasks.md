# Tasks: Vocabulary Search Syntax

**Input**: Design documents from `/specs/002-vocabulary-search-syntax/`
**Prerequisites**: plan.md, spec.md, research.md, data-model.md

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3)
- Include exact file paths in descriptions

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Project initialization and basic structure

- [X] T001 Create feature branch `002-vocabulary-search-syntax` from main
- [X] T002 [P] Install UXD.Popups NuGet package in `src/SentenceStudio/SentenceStudio.csproj` (Note: UXD Popups are native MAUI controls, no package needed)
- [X] T003 [P] Create `SearchQueryParser.cs` class stub in `src/SentenceStudio/Services/`
- [X] T004 [P] Add search syntax localization keys to `src/SentenceStudio/Resources/Strings/AppResources.resx` and `AppResources.ko.resx` (To be added in Phase 2)

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core infrastructure that MUST be complete before ANY user story can be implemented

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

- [X] T005 Create EF Core migration `AddVocabularySearchIndexes` in `src/SentenceStudio.Shared/Migrations/`
- [X] T006 Add SQLite indexes for Tags, Lemma, TargetLanguageTerm, NativeLanguageTerm columns
- [X] T007 Implement `SearchQueryParser` class with regex-based tokenization in `src/SentenceStudio/Services/SearchQueryParser.cs`
- [X] T008 [P] Create `FilterToken` model in `src/SentenceStudio/Models/FilterToken.cs`
- [X] T009 [P] Create `ParsedQuery` model in `src/SentenceStudio/Models/ParsedQuery.cs`
- [X] T010 [P] Create `AutocompleteSuggestion` model in `src/SentenceStudio/Models/AutocompleteSuggestion.cs`
- [X] T011 [P] Create `FilterChip` model in `src/SentenceStudio/Models/FilterChip.cs`
- [X] T012 Add state properties to `VocabularyManagementPageState` for search query management
- [ ] T013 Apply and test migration on all platforms (iOS, Android, macOS, Windows)

**Checkpoint**: Foundation ready - user story implementation can now begin in parallel

---

## Phase 3: User Story 1 & 6 - Basic Text Search + Filter Combination (Priority: P1) 🎯 MVP

**Goal**: Users can search vocabulary by entering free text that matches target term, native term, or lemma. Results update in real-time with 300ms debounce. Users can combine filters and clear them individually or all at once.

**Why combined**: These stories are tightly coupled - basic search needs the infrastructure that filter combination provides (query parser, chip display, clear functionality). Implementing separately would require throwaway code.

**Independent Test**: Type text into search box and verify results match. Type `tag:nature cloud` and verify both filter and text apply. Tap X on filter chip and verify it clears. Tap "Clear all" and verify all filters clear.

### Implementation for US1 & US6

- [X] T014 [P] [US1] Remove existing filter icon buttons (status, resource) from `VocabularyManagementPage.cs` UI
- [X] T015 [P] [US1] Update `VocabularyManagementPage` state to use `RawSearchQuery` instead of separate filter properties
- [X] T016 [US1] Implement `OnSearchTextChanged` handler with 300ms debounce timer in `VocabularyManagementPage.cs`
- [X] T017 [US1] Integrate `SearchQueryParser.Parse()` into search text changed handler
- [X] T018 [US1] Implement free-text query method `GetVocabularyByFreeText()` in `src/SentenceStudio/Data/VocabularyRepository.cs`
- [X] T019 [US1] Optimize free-text SQLite query to use indexed columns with LIKE operators
- [X] T020 [US6] Implement `GenerateFilterChips()` method to convert ParsedQuery to FilterChip list in `VocabularyManagementPage.cs`
- [X] T021 [US6] Add filter chips CollectionView UI above search Entry in `VocabularyManagementPage.cs`
- [X] T022 [US6] Style filter chips using `.ThemeKey(MyTheme.CardStyle)` with X button
- [X] T023 [US6] Implement `RemoveFilter()` handler for individual chip removal in `VocabularyManagementPage.cs`
- [X] T024 [US6] Implement `ClearAllFilters()` handler and "Clear all" button in `VocabularyManagementPage.cs`
- [X] T025 [US1] Add `ILogger<VocabularyManagementPage>` logging for search operations
- [ ] T026 [US1] Test free-text search on all platforms: iOS, Android, macOS, Windows
- [ ] T027 [US6] Test filter chip display and removal on all platforms

**Checkpoint**: At this point, free-text search with visual filter management should be fully functional on all platforms

---

## Phase 4: User Story 2 - Tag-Based Filtering (Priority: P2)

**Goal**: Users can filter vocabulary by tags using syntax `tag:tagname`. Multiple tag filters combine with AND logic. Autocomplete suggests available tags as user types.

**Independent Test**: Enter `tag:nature` and verify only words with "nature" tag appear. Enter `tag:nature tag:season` and verify only words with BOTH tags appear. Start typing `tag:nat` and verify autocomplete shows tags starting with "nat".

### Implementation for User Story 2

- [X] T028 [P] [US2] Implement `GetDistinctTags()` query method in `src/SentenceStudio/Data/VocabularyEncodingRepository.cs` (implemented as `GetAllTagsAsync()`)
- [X] T029 [P] [US2] Implement `GetVocabularyByTags()` query method with indexed LIKE search in `src/SentenceStudio/Data/VocabularyEncodingRepository.cs` (implemented as `FilterByTagAsync()`)
- [X] T030 [US2] Add tag filter detection logic to `OnSearchTextChanged` handler in `VocabularyManagementPage.cs`
- [X] T031 [US2] Implement `GetTagAutocomplete()` method for suggestions in `VocabularyManagementPage.cs` (via `ShowTagAutocompletePopup()` using `State.AvailableTags`)
- [X] T032 [US2] Create UXD Popup component for tag autocomplete dropdown in `VocabularyManagementPage.cs` (using `ListActionPopup`)
- [X] T033 [US2] Implement autocomplete item selection handler that inserts tag filter in `VocabularyManagementPage.cs` (`OnTagSelected()`)
- [X] T034 [US2] Add localization keys for tag filter UI in `Resources.resx` and `Resources.ko.resx` (SelectTag, SelectResource, SelectStatus)
- [X] T035 [US2] Style autocomplete popup using `.ThemeKey()` methods (styled with MyTheme.HighlightDarkest, ComponentSpacing, Size constants)
- [ ] T036 [US2] Test tag filtering with single and multiple tags on all platforms
- [ ] T037 [US2] Test tag autocomplete interaction on all platforms

**Checkpoint**: Tag filtering with autocomplete should work independently on all platforms

---

## Phase 5: User Story 3 - Resource-Based Filtering (Priority: P2)

**Goal**: Users can filter vocabulary by learning resource using syntax `resource:resourcename`. Multiple resource filters combine with OR logic. Autocomplete suggests available resources.

**Independent Test**: Enter `resource:general` and verify only words from "general" resource appear. Enter `resource:general resource:textbook` and verify words from EITHER resource appear. Start typing `resource:gen` and verify autocomplete shows resources starting with "gen".

### Implementation for User Story 3

- [X] T038 [P] [US3] Implement `GetVocabularyByResources()` query method with JOIN in `src/SentenceStudio/Data/VocabularyEncodingRepository.cs` (implemented as `FilterByResourcesAsync()` and `FilterByResourceNamesAsync()`)
- [X] T039 [US3] Add resource filter detection logic to `OnSearchTextChanged` handler in `VocabularyManagementPage.cs` (added resource filter check in `OnSearchQueryChanged`)
- [X] T040 [US3] Implement `GetResourceAutocomplete()` method using existing `State.AvailableResources` in `VocabularyManagementPage.cs` (implemented in `ShowResourceAutocompletePopup`)
- [X] T041 [US3] Extend autocomplete popup to handle resource suggestions in `VocabularyManagementPage.cs` (implemented with `ListActionPopup` and `OnResourceSelected`)
- [X] T042 [US3] Add localization keys for resource filter UI in `Resources.resx` and `Resources.ko-KR.resx` (added `UnnamedResource` key)
- [ ] T043 [US3] Test resource filtering with single and multiple resources on all platforms
- [ ] T044 [US3] Test resource autocomplete interaction on all platforms

**Checkpoint**: Resource filtering with autocomplete should work independently and combine with tags on all platforms

---

## Phase 6: User Story 4 - Lemma-Based Search (Priority: P3)

**Goal**: Users can search by lemma (dictionary form) using syntax `lemma:단어`. Lemma matches also work in free-text search. Autocomplete suggests available lemmas.

**Independent Test**: Enter `lemma:가다` and verify words with that lemma appear (including conjugated forms). Start typing `lemma:가` and verify autocomplete shows lemmas starting with "가". Enter "가다" without prefix and verify it matches lemma field.

### Implementation for User Story 4

- [X] T045 [P] [US4] Implement `GetDistinctLemmas()` query method with partial match in `src/SentenceStudio/Data/VocabularyEncodingRepository.cs` (implemented as `GetAllLemmasAsync()` and `GetLemmasByPartialAsync()`)
- [X] T046 [P] [US4] Implement `GetVocabularyByLemma()` query method with exact match in `src/SentenceStudio/Data/VocabularyEncodingRepository.cs` (already exists as `SearchByLemmaAsync()`)
- [X] T047 [US4] Add lemma filter detection logic to `OnSearchTextChanged` handler in `VocabularyManagementPage.cs` (added in `OnSearchQueryChanged`)
- [X] T048 [US4] Implement `GetLemmaAutocomplete()` method for suggestions in `VocabularyManagementPage.cs` (implemented in `ShowLemmaAutocompletePopup`)
- [X] T049 [US4] Extend autocomplete popup to handle lemma suggestions in `VocabularyManagementPage.cs` (implemented with `ListActionPopup`, `LemmaSelectionItem`, and `OnLemmaSelected`)
- [X] T050 [US4] Update free-text search to include lemma field in LIKE query (already implemented in `ApplyParsedQueryFilters`)
- [X] T051 [US4] Add localization keys for lemma filter UI in `Resources.resx` and `Resources.ko-KR.resx` (added `SelectLemma` key)
- [ ] T052 [US4] Test lemma filtering with exact syntax on all platforms
- [ ] T053 [US4] Test lemma matching in free-text search on all platforms
- [ ] T054 [US4] Test lemma autocomplete with Korean text input on all platforms

**Checkpoint**: Lemma search should work independently and combine with other filters on all platforms

---

## Phase 7: User Story 5 - Status-Based Filtering (Priority: P3)

**Goal**: Users can filter vocabulary by learning status using syntax `status:known`, `status:learning`, or `status:unknown`. Status filters combine with other filter types.

**Independent Test**: Enter `status:learning` and verify only words in learning phase appear. Enter `status:learning tag:nature` and verify both filters apply. Autocomplete suggests known/learning/unknown values.

### Implementation for User Story 5

- [X] T055 [P] [US5] Implement `GetVocabularyByStatus()` query method with VocabularyProgress JOIN in `src/SentenceStudio/Data/VocabularyEncodingRepository.cs` (status filtering already implemented in `ApplyParsedQueryFilters` using ViewModel properties)
- [X] T056 [US5] Add status filter detection logic to `OnSearchTextChanged` handler in `VocabularyManagementPage.cs` (added in `OnSearchQueryChanged`)
- [X] T057 [US5] Implement `GetStatusAutocomplete()` method with fixed enum values in `VocabularyManagementPage.cs` (implemented in `ShowStatusAutocompletePopup` with StatusSelectionItem)
- [X] T058 [US5] Extend autocomplete popup to handle status suggestions in `VocabularyManagementPage.cs` (implemented with `ListActionPopup`, icon binding, and `OnStatusSelected`)
- [X] T059 [US5] Add localization keys for status filter UI in `Resources.resx` and `Resources.ko-KR.resx` (added StatusKnown, StatusLearning, StatusUnknown)
- [ ] T060 [US5] Test status filtering with each status value on all platforms
- [ ] T061 [US5] Test status filter combination with tags and resources on all platforms

**Checkpoint**: All filter types should work independently and in combination on all platforms

---

## Phase 8: Polish & Cross-Cutting Concerns

**Purpose**: Improvements that affect multiple user stories

- [X] T062 [P] Update quickstart.md with search syntax examples and validation steps (Added 15 validation scenarios)
- [X] T063 [P] Add unit tests for `SearchQueryParser` in `tests/SentenceStudio.UnitTests/Services/SearchQueryParserTests.cs`
- [ ] T064 Performance profiling: measure query execution time for complex filter combinations
- [ ] T065 Accessibility review: verify screen reader announces filter chips and autocomplete
- [X] T066 [P] Document search syntax in user-facing help documentation (created docs/search-syntax-guide.md)
- [ ] T067 Final cross-platform testing with complex search scenarios on iOS, Android, macOS, Windows
- [ ] T068 Run quickstart.md validation scenarios
- [X] T069 Code cleanup: remove old filter button handlers and unused state properties (Review: handlers still in use for backward compatibility, SearchText synced with RawSearchQuery - no obsolete code found)
- [X] T070 Verify no hardcoded strings remain (all use `$"{_localize["Key"]}"`) (Fixed: "Delete", "Associate", and "selected" strings in RenderBulkActionsBar now use localization)

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies - can start immediately
- **Foundational (Phase 2)**: Depends on Setup completion - BLOCKS all user stories
- **User Stories 1 & 6 (Phase 3)**: Depends on Foundational phase completion - MVP foundation
- **User Story 2 (Phase 4)**: Depends on Phase 3 completion (needs search infrastructure)
- **User Story 3 (Phase 5)**: Depends on Phase 3 completion (needs search infrastructure)
- **User Story 4 (Phase 6)**: Depends on Phase 3 completion (needs search infrastructure)
- **User Story 5 (Phase 7)**: Depends on Phase 3 completion (needs search infrastructure)
- **Polish (Phase 8)**: Depends on all desired user stories being complete

### User Story Dependencies

- **User Stories 1 & 6 (P1)**: Foundation for all other stories - MUST complete first
- **User Stories 2-5 (P2-P3)**: Can proceed in parallel AFTER Phase 3 completes (if team capacity allows)
- Each filter type (tag, resource, lemma, status) is independent - no cross-dependencies

### Within Each User Story

- Models before services
- Services before UI
- Core implementation before integration
- Story complete before moving to next priority

### Parallel Opportunities

- Setup tasks T002-T004 can run in parallel
- Foundational model creation T008-T011 can run in parallel
- User Story 2-5 filter types can be developed in parallel by different team members
- All localization tasks marked [P] can run in parallel
- Polish tasks T062-T063, T065-T066, T070 can run in parallel

---

## Parallel Example: User Story 2 (Tag Filtering)

```bash
# Launch repository methods together:
Task: "Implement GetDistinctTags() query method"
Task: "Implement GetVocabularyByTags() query method"

# UI and localization in parallel:
Task: "Add localization keys for tag filter UI"
```

---

## Implementation Strategy

### MVP First (Phase 3 Only)

1. Complete Phase 1: Setup
2. Complete Phase 2: Foundational (CRITICAL - blocks all stories)
3. Complete Phase 3: User Stories 1 & 6 (Basic text search + filter management)
4. **STOP and VALIDATE**: Test search independently on all platforms
5. Deploy/demo if ready

### Incremental Delivery

1. Complete Setup + Foundational → Foundation ready
2. Add Phase 3 (US1 & US6) → Test independently → Deploy/Demo (MVP!)
3. Add Phase 4 (US2 - Tags) → Test independently → Deploy/Demo
4. Add Phase 5 (US3 - Resources) → Test independently → Deploy/Demo
5. Add Phase 6 (US4 - Lemmas) → Test independently → Deploy/Demo (optional)
6. Add Phase 7 (US5 - Status) → Test independently → Deploy/Demo (optional)
7. Each story adds value without breaking previous stories

### Parallel Team Strategy

With multiple developers:

1. Team completes Setup + Foundational together
2. Complete Phase 3 (US1 & US6) together - MVP foundation
3. Once Phase 3 is done:
   - Developer A: User Story 2 (Tags)
   - Developer B: User Story 3 (Resources)
   - Developer C: User Story 4 (Lemmas)
   - Developer D: User Story 5 (Status)
4. Stories complete and integrate independently

---

## Notes

- [P] tasks = different files, no dependencies
- [Story] label maps task to specific user story for traceability
- Phase 3 combines US1 + US6 because they share infrastructure (parser, chips, clear functionality)
- Phases 4-7 are independent filter types that can be developed in parallel
- All queries must use indexes for mobile performance (<200ms requirement)
- Verify tests pass after each task or logical group
- Commit after each task or logical group
- Stop at any checkpoint to validate story independently
- Avoid: vague tasks, same file conflicts, cross-story dependencies that break independence

---

## Success Metrics

- **SC-001**: Users find specific vocabulary using combined filters in under 5 seconds
- **SC-002**: Search results update in real-time with less than 200ms delay
- **SC-003**: 90% of users successfully use at least one filter syntax within first usage
- **SC-004**: Autocomplete suggestions appear within 100ms of typing filter prefix
- **SC-005**: Search functionality works identically on all platforms without platform-specific bugs
