# Feature Specification: Vocabulary Quiz Preferences

**Feature Branch**: `001-vocab-quiz-preferences`  
**Created**: 2025-12-13  
**Status**: Draft  
**Input**: User description: "I want to be able to configure the VocabularyQuizPage and have my preferences remembered: to present the target language vocabulary vs the native language vocabulary and have to answer the opposite, to have the audio for the vocabulary play automatically for the target language word, and to optionally have one of the sample sentences audio played as well after the word is played, and to display the mnemonic image with the confirmation of the correct answer."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Configure Quiz Display Direction (Priority: P1)

As a language learner, I want to choose whether vocabulary quiz questions show the target language word (requiring native language answer) or native language word (requiring target language answer), so I can practice recognition or production based on my learning needs.

**Why this priority**: This is the core functionality that fundamentally changes how the quiz presents information. Without this, users cannot customize their learning approach, making it the foundational preference.

**Independent Test**: Can be fully tested by opening vocabulary quiz preferences, toggling the display direction setting, starting a quiz, and verifying that questions show the selected language format. Delivers immediate value by allowing users to choose their preferred study direction.

**Acceptance Scenarios**:

1. **Given** I am viewing vocabulary quiz preferences, **When** I select "Show target language word" option, **Then** quiz questions display the target language word and I must provide the native language translation
2. **Given** I am viewing vocabulary quiz preferences, **When** I select "Show native language word" option, **Then** quiz questions display the native language word and I must provide the target language translation
3. **Given** I have configured a display direction preference, **When** I return to preferences later, **Then** my previously selected option is still active
4. **Given** I am taking a vocabulary quiz, **When** I navigate to preferences and change the display direction, **Then** the next quiz session uses the new direction

---

### User Story 2 - Configure Audio Playback (Priority: P2)

As a language learner, I want to control whether target language vocabulary audio plays automatically when a word appears, so I can hear proper pronunciation while learning vocabulary.

**Why this priority**: Audio playback is critical for pronunciation learning but depends on having the display direction configured first. It's a key learning enhancement that supports both visual and auditory learners.

**Independent Test**: Can be tested independently by configuring audio preferences, starting a quiz, and observing whether audio plays automatically when vocabulary words appear. Delivers value by supporting auditory learning preferences.

**Acceptance Scenarios**:

1. **Given** I enable "Auto-play vocabulary audio" in preferences, **When** a vocabulary quiz question appears with a target language word, **Then** the audio for that word plays automatically
2. **Given** I disable "Auto-play vocabulary audio" in preferences, **When** a vocabulary quiz question appears, **Then** no audio plays automatically
3. **Given** I have audio playback enabled, **When** audio is already playing and a new word appears, **Then** the previous audio stops and new audio plays
4. **Given** audio is playing automatically, **When** I manually trigger audio playback, **Then** the audio restarts from the beginning

---

### User Story 3 - Configure Sample Sentence Audio (Priority: P3)

As a language learner, I want to optionally have one sample sentence audio play automatically after the vocabulary word audio, so I can hear the word used in context to improve comprehension.

**Why this priority**: This enhances the learning experience but is not essential for basic vocabulary study. It provides contextual learning for users who want deeper immersion.

**Independent Test**: Can be tested by enabling sample sentence audio in preferences, ensuring vocabulary audio is enabled, starting a quiz, and verifying that a sample sentence plays after the word audio. Delivers value by providing contextual usage examples.

**Acceptance Scenarios**:

1. **Given** I enable "Play sample sentence audio" in preferences and vocabulary audio is enabled, **When** a vocabulary word audio finishes playing, **Then** one sample sentence audio plays automatically
2. **Given** I disable "Play sample sentence audio" in preferences, **When** vocabulary audio finishes playing, **Then** no sample sentence audio plays
3. **Given** sample sentence audio is enabled but vocabulary audio is disabled, **When** a quiz question appears, **Then** no audio plays automatically
4. **Given** sample sentence audio is playing, **When** I submit an answer or navigate to the next question, **Then** the audio stops playing
5. **Given** a vocabulary word has multiple sample sentences, **When** the sample sentence audio should play, **Then** the system selects one sentence to play

---

### User Story 4 - Display Mnemonic Image (Priority: P3)

As a language learner, I want to see the mnemonic image when the correct answer is confirmed, so I can reinforce my memory association with visual cues.

**Why this priority**: Mnemonic images enhance memory retention but are supplementary to the core quiz functionality. This is a valuable feature for visual learners but not required for basic quiz operation.

**Independent Test**: Can be tested by enabling mnemonic image display in preferences, taking a quiz, answering correctly, and verifying the mnemonic image appears in the confirmation screen. Delivers value by supporting visual memory techniques.

**Acceptance Scenarios**:

1. **Given** I enable "Show mnemonic image" in preferences, **When** I answer a vocabulary question correctly, **Then** the mnemonic image appears alongside the correct answer confirmation
2. **Given** I disable "Show mnemonic image" in preferences, **When** I answer a vocabulary question correctly, **Then** no mnemonic image appears in the confirmation
3. **Given** a vocabulary word does not have a mnemonic image, **When** I answer correctly, **Then** the confirmation displays without an image placeholder
4. **Given** I am viewing the correct answer confirmation with mnemonic image, **When** I proceed to the next question, **Then** the image is cleared from view

---

### Edge Cases

- What happens when a user changes preferences mid-quiz session? Preferences should apply from the next question forward, not retroactively affect current question.
- How does the system handle missing audio files? If vocabulary audio or sample sentence audio files are not available, the system should skip audio playback gracefully without errors.
- What if a vocabulary word has no sample sentences? The sample sentence audio option should be ignored for that word.
- What if a vocabulary word has no mnemonic image? The confirmation screen should display without attempting to show a missing image.
- Cross-platform considerations:
  - Audio playback must work consistently on iOS, Android, macOS, and Windows
  - Preferences storage must work offline using SQLite on all platforms
  - UI controls must adapt to different screen sizes (mobile vs desktop)
  - Audio permissions must be handled per platform requirements (especially iOS/Android)
  - Audio playback should handle platform-specific audio session management (pausing when app backgrounds)

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST provide a preferences screen accessible from the vocabulary quiz page or app settings
- **FR-002**: System MUST allow users to select display direction: "Show target language word" or "Show native language word"
- **FR-003**: System MUST persist vocabulary quiz preferences across app sessions
- **FR-004**: System MUST apply saved preferences to all subsequent vocabulary quiz sessions until changed
- **FR-005**: System MUST allow users to toggle automatic vocabulary audio playback on/off
- **FR-006**: System MUST allow users to toggle automatic sample sentence audio playback on/off
- **FR-007**: System MUST only play sample sentence audio if vocabulary audio is also enabled
- **FR-008**: System MUST play sample sentence audio after vocabulary audio completes (not simultaneously)
- **FR-009**: System MUST allow users to toggle mnemonic image display on/off for correct answer confirmations
- **FR-010**: System MUST display mnemonic images only when correct answers are confirmed (not during question presentation)
- **FR-011**: System MUST select one sample sentence when multiple are available for a vocabulary word
- **FR-012**: System MUST handle missing audio files gracefully without disrupting the quiz flow
- **FR-013**: System MUST handle missing mnemonic images gracefully without displaying broken image placeholders
- **FR-014**: System MUST stop any playing audio when user navigates to next question
- **FR-015**: Feature MUST work on iOS, Android, macOS, and Windows (cross-platform requirement)
- **FR-016**: Feature MUST work offline (SQLite local storage required)
- **FR-017**: UI MUST use MauiReactor MVU pattern with semantic alignment methods
- **FR-018**: All user-facing strings MUST be localized (English + Korean support)
- **FR-019**: Styling MUST use `.ThemeKey()` or MyTheme constants (no hardcoded values)
- **FR-020**: Preferences changes made during an active quiz session MUST take effect on the next question, not the current one

### Key Entities

- **VocabularyQuizPreferences**: User-specific settings controlling vocabulary quiz behavior
  - DisplayDirection: Enum indicating whether to show target language or native language first
  - AutoPlayVocabularyAudio: Boolean flag for automatic vocabulary word audio playback
  - AutoPlaySampleSentenceAudio: Boolean flag for automatic sample sentence audio playback  
  - ShowMnemonicImage: Boolean flag for displaying mnemonic images on correct answers
  - UserId: Association with the current user profile (for multi-user support)
  - LastModified: Timestamp for tracking preference updates

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Users can access and configure all four preference options in under 30 seconds
- **SC-002**: Preference changes persist across app restarts with 100% reliability
- **SC-003**: Audio playback starts within 500 milliseconds of vocabulary word display
- **SC-004**: Users can switch between display directions and see the change reflected in the next quiz question within 2 seconds
- **SC-005**: Mnemonic images load and display within 1 second of correct answer confirmation
- **SC-006**: 90% of users successfully configure their preferred quiz settings on first attempt
- **SC-007**: Audio playback works reliably across all target platforms (iOS, Android, macOS, Windows) without platform-specific failures
