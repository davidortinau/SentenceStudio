# Feature Specification: Fuzzy Text Matching for Vocabulary Quiz

**Feature Branch**: `001-fuzzy-text-matching`  
**Created**: 2025-12-14  
**Status**: Draft  
**Input**: User description: "the @src/SentenceStudio/Pages/VocabularyQuiz/VocabularyQuizPage.cs needs to be able to handle fuzzy matching of some kind on the text entry answers. If I answer 'take' and the expected answer is 'take (a photo)' then it should be accepted as correct. Other examples: 'ding' should be accepted if the expected answer is 'ding~ (a sound)', 'to choose' if the expected answer is 'choose', and vice versa."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Core Word Matching with Annotations (Priority: P1)

Users practicing vocabulary in text entry mode can answer with the core word without needing to include annotations (parenthetical clarifications, tildes with sound descriptors, etc.). The system recognizes that "take" matches "take (a photo)" and marks it correct.

**Why this priority**: This is the most common frustration - users know the correct word but fail because they forgot to include parenthetical notes. This directly impacts learning flow and user satisfaction.

**Independent Test**: Can be fully tested by entering text answers in vocabulary quiz and verifying that core words without annotations are accepted. Delivers immediate value by reducing false negatives for correct answers.

**Acceptance Scenarios**:

1. **Given** user is shown "take (a photo)" as the expected answer, **When** user types "take", **Then** answer is marked correct
2. **Given** user is shown "ding~ (a sound)" as the expected answer, **When** user types "ding", **Then** answer is marked correct
3. **Given** user is shown "choose" as the expected answer, **When** user types "to choose", **Then** answer is marked correct
4. **Given** user is shown "to choose" as the expected answer, **When** user types "choose", **Then** answer is marked correct
5. **Given** user is shown "안녕하세요 (hello)" as the expected answer, **When** user types "안녕하세요", **Then** answer is marked correct

---

### User Story 2 - Whitespace and Punctuation Tolerance (Priority: P2)

Users can answer correctly even with minor formatting differences like extra spaces, missing punctuation, or case differences. The system normalizes inputs before comparison.

**Why this priority**: Common typing errors shouldn't penalize users who know the correct answer. This improves user experience without changing core learning objectives.

**Independent Test**: Can be tested by entering answers with various spacing/capitalization patterns and verifying acceptance. Delivers value by reducing frustration from technical formatting issues.

**Acceptance Scenarios**:

1. **Given** user is shown "take" as the expected answer, **When** user types " take " (with extra spaces), **Then** answer is marked correct
2. **Given** user is shown "Hello" as the expected answer, **When** user types "hello" (different case), **Then** answer is marked correct
3. **Given** user is shown "don't" as the expected answer, **When** user types "dont" (missing apostrophe), **Then** answer is marked correct

---

### User Story 3 - Feedback on Fuzzy Matches (Priority: P3)

When users provide a fuzzy match that's accepted as correct, they see feedback indicating their answer was accepted AND what the complete/preferred form is. This helps reinforce the full vocabulary entry.

**Why this priority**: While accepting fuzzy matches is valuable, showing the complete form helps users learn the full context. This is educational enhancement rather than core functionality.

**Independent Test**: Can be tested by entering partial answers and verifying feedback message shows both acceptance and the complete form. Delivers learning reinforcement value.

**Acceptance Scenarios**:

1. **Given** user types "take" for "take (a photo)", **When** answer is evaluated, **Then** feedback shows "✓ Correct! Full form: take (a photo)"
2. **Given** user types "ding" for "ding~ (a sound)", **When** answer is evaluated, **Then** feedback shows "✓ Correct! Full form: ding~ (a sound)"
3. **Given** user types exact match, **When** answer is evaluated, **Then** feedback shows standard "✓ Correct!" without additional note

---

### Edge Cases

- What happens when user input contains special characters that appear in the expected answer (e.g., parentheses, tildes)?
  - System should extract and normalize core text from both user input and expected answer
- How does system handle multiple valid forms (e.g., "choose" vs "to choose")?
  - Both directions should be supported: "choose" matches "to choose" and vice versa
- What happens when annotation itself is critical to meaning (e.g., "take" might mean different things with different annotations)?
  - Current spec accepts core word matches; future enhancement could flag potentially ambiguous matches
- Cross-platform considerations:
  - Works identically on iOS, Android, macOS, and Windows (text comparison is platform-agnostic)
  - No special permissions needed (offline functionality using local processing)
  - Korean input methods (IME) should work seamlessly (input is evaluated after user submits)
  - Mobile keyboards may have different punctuation layouts - fuzzy matching accommodates this

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST extract core words from vocabulary entries by removing annotations in parentheses, tildes with descriptors, and other common formatting patterns
- **FR-002**: System MUST normalize user input by trimming whitespace, converting to lowercase for comparison, and removing punctuation marks
- **FR-003**: System MUST compare normalized user input against normalized expected answer core word
- **FR-004**: System MUST accept answer as correct if normalized forms match, regardless of annotation presence
- **FR-005**: System MUST support bidirectional matching: "choose" matches "to choose" and "to choose" matches "choose"
- **FR-006**: System MUST handle Korean characters (Hangul) correctly in fuzzy matching
- **FR-007**: System MUST preserve original vocabulary entry format in database (fuzzy matching is evaluation-only)
- **FR-008**: System MUST work offline using client-side text processing (no API calls required)
- **FR-009**: System MUST apply fuzzy matching only in text entry mode (Production phase), not multiple choice (Recognition phase)
- **FR-010**: Feature MUST work on iOS, Android, macOS, and Windows (cross-platform requirement)
- **FR-011**: UI feedback MUST use localized strings for "Correct!" messages
- **FR-012**: Fuzzy matching logic MUST be centralized in a reusable method for potential use in other quiz types

### Key Entities

- **VocabularyWord**: Contains both TargetLanguageTerm and NativeLanguageTerm with potential annotations
  - TargetLanguageTerm: The word in the language being learned (e.g., "찍다 (to take a photo)")
  - NativeLanguageTerm: The translation with possible annotations (e.g., "take (a photo)", "ding~ (a sound)")
- **UserInput**: Text entered by user during quiz, subject to normalization and fuzzy matching
- **FuzzyMatchResult**: Evaluation result containing IsCorrect flag, MatchType (exact vs fuzzy), and CompletedForm for feedback

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Users achieve 95% accuracy on text entry questions where they previously failed due to missing annotations
- **SC-002**: Text entry quiz completion time improves by 20% as users spend less time remembering exact formatting
- **SC-003**: User frustration incidents (reported via feedback or support) related to "I knew the answer but got it wrong" decrease by 80%
- **SC-004**: Fuzzy matching evaluation completes in under 10ms per answer on all platforms (imperceptible to users)
- **SC-005**: Zero false positives where incorrect answers are accepted due to over-aggressive fuzzy matching

## Assumptions *(mandatory)*

- Vocabulary entries follow consistent annotation patterns: parentheses for clarifications, tildes for descriptive sounds
- Users understand that core word meaning is being tested, not exact formatting memorization
- Korean language vocabulary uses similar annotation patterns to English
- Text entry mode is already implemented and functional in VocabularyQuizPage.cs
- Existing word database schema accommodates terms with annotations
- Current answer evaluation logic is replaceable/extendable without breaking existing quiz flows

## Dependencies

- Existing VocabularyQuizPage.cs implementation and state management
- Current text entry UI and input handling
- Localization system for feedback messages (LocalizationManager)
- VocabularyWord model with TargetLanguageTerm and NativeLanguageTerm properties
