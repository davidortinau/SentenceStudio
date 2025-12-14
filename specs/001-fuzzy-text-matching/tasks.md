# Tasks: Fuzzy Text Matching for Vocabulary Quiz

**Input**: Design documents from `/specs/001-fuzzy-text-matching/`
**Prerequisites**: plan.md, spec.md, research.md, data-model.md
**Branch**: `001-fuzzy-text-matching`
**Date**: 2025-12-14

**Tests**: No explicit test tasks requested - focus on implementation with manual validation

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3)
- Include exact file paths in descriptions

## Path Conventions

- **MAUI project**: `src/SentenceStudio/`, `tests/` at repository root
- **Pages**: `src/SentenceStudio/Pages/` (MauiReactor components)
- **Services**: `src/SentenceStudio/Services/` (business logic, AI integration)
- **Resources**: `src/SentenceStudio/Resources/Strings/` (localization)

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Project initialization and basic structure

- [X] T001 Review existing VocabularyQuizPage.cs implementation in `src/SentenceStudio/Pages/VocabularyQuiz/VocabularyQuizPage.cs` to understand current answer evaluation logic (line 1395-1396)
- [X] T002 Review existing VocabularyWord model structure in `src/SentenceStudio/Data/Models/` to confirm TargetLanguageTerm and NativeLanguageTerm properties

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core infrastructure that MUST be complete before ANY user story can be implemented

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

- [X] T003 Create FuzzyMatchResult model in `src/SentenceStudio/Models/FuzzyMatchResult.cs` with IsCorrect, MatchType, and CompleteForm properties
- [X] T004 Create FuzzyMatcher static utility class in `src/SentenceStudio/Services/FuzzyMatcher.cs` with Evaluate() method signature
- [X] T005 Implement compiled regex patterns in FuzzyMatcher: ParenthesesPattern, TildePattern, PunctuationPattern using RegexOptions.Compiled
- [X] T006 Verify build succeeds on at least one platform (macOS/iOS) with new files

**Checkpoint**: Foundation ready - user story implementation can now begin in parallel

---

## Phase 3: User Story 1 - Core Word Matching with Annotations (Priority: P1) 🎯 MVP

**Goal**: Users can answer with core words without annotations. "take" matches "take (a photo)", "ding" matches "ding~ (a sound)"

**Independent Test**: Enter text answers in vocabulary quiz without annotations and verify they're accepted as correct

### Implementation for User Story 1

- [X] T007 [US1] Implement NormalizeText() private method in FuzzyMatcher with Unicode NFC normalization for Korean support
- [X] T008 [US1] Add parentheses removal logic to NormalizeText() using ParenthesesPattern regex
- [X] T009 [US1] Add tilde descriptor removal logic to NormalizeText() using TildePattern regex
- [X] T010 [US1] Add punctuation removal logic to NormalizeText() using PunctuationPattern regex
- [X] T011 [US1] Add "to " prefix removal for English infinitives in NormalizeText()
- [X] T012 [US1] Implement core matching logic in Evaluate() method: normalize both inputs, compare case-insensitively
- [X] T013 [US1] Add exact vs fuzzy match detection logic in Evaluate() to populate MatchType and CompleteForm
- [X] T014 [US1] Replace existing answer evaluation in VocabularyQuizPage.cs CheckAnswer() method (line 1395-1396) with FuzzyMatcher.Evaluate() call
- [X] T015 [US1] Store matchResult in CheckAnswer() method scope for later use in feedback
- [X] T016 [US1] Add ILogger debug logging in FuzzyMatcher.Evaluate() showing normalized forms for both user and expected inputs
- [X] T017 [US1] Add ILogger info logging when fuzzy match is accepted showing CompleteForm
- [X] T018 [US1] Test on macOS/iOS: verify "take" matches "take (a photo)"
- [X] T019 [US1] Test on macOS/iOS: verify "ding" matches "ding~ (a sound)"
- [X] T020 [US1] Test on macOS/iOS: verify Korean annotations work: "안녕하세요" matches "안녕하세요 (hello)"

**Checkpoint**: At this point, User Story 1 should be fully functional on at least one platform with core annotation removal working

---

## Phase 4: User Story 2 - Whitespace and Punctuation Tolerance (Priority: P2)

**Goal**: Users can answer correctly with minor formatting differences like extra spaces, missing punctuation, or case differences

**Independent Test**: Enter answers with various spacing/capitalization patterns and verify acceptance

### Implementation for User Story 2

- [X] T021 [US2] Verify NormalizeText() already handles trim and case normalization (from US1 implementation)
- [X] T022 [US2] Test whitespace tolerance: " take " matches "take"
- [X] T023 [US2] Test case tolerance: "Take" matches "take"
- [X] T024 [US2] Test punctuation tolerance: "dont" matches "don't"
- [X] T025 [US2] Test bidirectional infinitive matching: "choose" matches "to choose" AND "to choose" matches "choose"
- [X] T026 [US2] Test on Android platform if available
- [X] T027 [US2] Test on Windows platform if available

**Checkpoint**: At this point, User Stories 1 AND 2 should both work with comprehensive normalization across platforms

---

## Phase 5: User Story 3 - Feedback on Fuzzy Matches (Priority: P3)

**Goal**: When users provide a fuzzy match, show feedback indicating acceptance AND the complete/preferred form for learning reinforcement

**Independent Test**: Enter partial answers and verify feedback message shows both acceptance and the complete form

### Implementation for User Story 3

- [X] T028 [P] [US3] Add localization key "QuizFuzzyMatchCorrect" to `src/SentenceStudio/Resources/Strings/AppResources.resx` with value: "✓ Correct! Full form: {0}"
- [X] T029 [P] [US3] Add localization key "QuizFuzzyMatchCorrect" to `src/SentenceStudio/Resources/Strings/AppResources.ko-KR.resx` with value: "✓ 정답! 전체 형태: {0}"
- [X] T030 [US3] Update feedback display logic in VocabularyQuizPage.cs CheckAnswer() method to check matchResult.MatchType
- [X] T031 [US3] For MatchType == "Fuzzy", use string.Format with QuizFuzzyMatchCorrect localization key and matchResult.CompleteForm
- [X] T032 [US3] For MatchType == "Exact", use existing QuizCorrect localization key (no changes to exact match feedback)
- [X] T033 [US3] Test fuzzy match feedback: "take" for "take (a photo)" shows "✓ Correct! Full form: take (a photo)"
- [X] T034 [US3] Test exact match feedback: "take (a photo)" for "take (a photo)" shows "✓ Correct!" (no full form note)
- [X] T035 [US3] Test Korean fuzzy match feedback with Korean localization

**Checkpoint**: All user stories should now be independently functional with proper feedback across platforms

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: Improvements that affect multiple user stories

- [X] T036 [P] Add XML documentation comments to FuzzyMatcher.cs public API
- [X] T037 [P] Add XML documentation comments to FuzzyMatchResult.cs properties
- [X] T038 Performance validation: Add timestamp logging around FuzzyMatcher.Evaluate() to verify <1ms execution time
- [X] T039 Test edge cases: empty strings, null inputs (should handle gracefully without crashes)
- [X] T040 Test edge cases: multiple parentheses like "word (context) (more context)"
- [X] T041 Test edge cases: only annotations like "(annotation)" with no core word
- [X] T042 Final cross-platform testing on iOS, Android, macOS, Windows (all platforms where available)
- [X] T043 Security review: Confirm no user input is logged that could contain sensitive data
- [X] T044 Run through acceptance scenarios from spec.md User Stories 1-3 on primary platform
- [X] T045 Document fuzzy matching behavior in `docs/features/fuzzy-text-matching.md` (if documentation needed)

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies - can start immediately
- **Foundational (Phase 2)**: Depends on Setup completion - BLOCKS all user stories
- **User Stories (Phase 3+)**: All depend on Foundational phase completion
  - User stories can then proceed in parallel (if staffed)
  - Or sequentially in priority order (P1 → P2 → P3)
- **Polish (Phase 6)**: Depends on all desired user stories being complete

### User Story Dependencies

- **User Story 1 (P1)**: Can start after Foundational (Phase 2) - No dependencies on other stories
- **User Story 2 (P2)**: Can start after US1 OR in parallel if NormalizeText() is comprehensive from the start
- **User Story 3 (P3)**: Depends on US1 and US2 for matchResult being properly populated

### Within Each User Story

- NormalizeText() implementation before Evaluate() logic
- Evaluate() logic before VocabularyQuizPage integration
- Core implementation before platform-specific testing
- Story complete before moving to next priority

### Parallel Opportunities

- All Setup tasks can run in parallel (T001-T002: different review activities)
- Foundational tasks T003-T005 can be started in parallel (different files)
- Within US3, localization tasks T028-T029 can run in parallel (different language files)
- Polish tasks T036-T037, T045 can run in parallel (documentation tasks)

---

## Parallel Example: Foundational Phase

```bash
# Launch foundational infrastructure together:
Task T003: "Create FuzzyMatchResult model in src/SentenceStudio/Models/FuzzyMatchResult.cs"
Task T004: "Create FuzzyMatcher static utility class in src/SentenceStudio/Services/FuzzyMatcher.cs"
Task T005: "Implement compiled regex patterns in FuzzyMatcher"
```

## Parallel Example: User Story 3

```bash
# Launch localization for both languages together:
Task T028: "Add QuizFuzzyMatchCorrect to AppResources.resx"
Task T029: "Add QuizFuzzyMatchCorrect to AppResources.ko-KR.resx"
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup (T001-T002)
2. Complete Phase 2: Foundational (T003-T006) - CRITICAL
3. Complete Phase 3: User Story 1 (T007-T020)
4. **STOP and VALIDATE**: Test User Story 1 independently on at least one platform
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
   - Developer B: User Story 2 (can start in parallel if NormalizeText is comprehensive)
   - Developer C: User Story 3 (starts after US1/US2 merge)
3. Stories complete and integrate independently

---

## Success Criteria Validation

After completing all phases, verify:

- **SC-001**: Users achieve 95% accuracy on text entry where they previously failed due to missing annotations (manual QA tracking)
- **SC-002**: Text entry quiz completion time improves by 20% (measure before/after with timer logs)
- **SC-003**: User frustration incidents decrease by 80% (feedback tracking)
- **SC-004**: Fuzzy matching evaluation completes in under 10ms per answer on all platforms (T038 validates <1ms, exceeding requirement)
- **SC-005**: Zero false positives where incorrect answers are accepted (comprehensive edge case testing in Phase 6)

---

## Notes

- [P] tasks = different files, no dependencies
- [Story] label maps task to specific user story for traceability
- Each user story should be independently completable and testable
- Commit after each task or logical group
- Stop at any checkpoint to validate story independently
- Focus on cross-platform consistency: iOS, Android, macOS, Windows
- Use ILogger for all production logging, never System.Diagnostics.Debug.WriteLine for production code
- Korean language support is CRITICAL - test Korean IME input on mobile platforms
