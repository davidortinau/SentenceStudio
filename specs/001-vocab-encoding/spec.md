# Feature Specification: Vocabulary Encoding Enhancements

**Feature Branch**: `001-vocab-encoding`  
**Created**: 2025-12-11  
**Status**: Draft  
**Input**: User description: "Add vocabulary encoding features to support memorization with lemmas, tags, mnemonics, images, and example sentences"

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Add Memory Aids to Vocabulary Words (Priority: P1)

As a language learner, I want to add mnemonics, images, and tags to my vocabulary words so that I can create stronger memory associations and improve retention using proven encoding techniques.

**Why this priority**: Memory encoding is the core improvement that directly addresses vocabulary retention challenges. Without effective encoding, learners struggle to move words from short-term to long-term memory. This is the foundation that enables all other encoding features.

**Independent Test**: Can be fully tested by editing an existing vocabulary word, adding a mnemonic story (e.g., "단풍 sounds like 'don't pong' - leaves that don't smell bad in fall"), adding an image URL, and adding tags like "nature,season,visual". The word should display these fields when viewed and the encoding strength indicator should update to reflect the added information.

**Acceptance Scenarios**:

1. **Given** I am viewing a vocabulary word detail page, **When** I click "Add Mnemonic" and enter a silly story or memory association, **Then** the mnemonic is saved and displayed with the word
2. **Given** I am editing a vocabulary word, **When** I add tags separated by commas (e.g., "nature,season,visual"), **Then** the tags are saved and displayed as individual badges or chips
3. **Given** I am editing a vocabulary word, **When** I provide an image URL or select an image for the mnemonic, **Then** the image is associated with the word and displayed on the vocabulary detail page
4. **Given** I have added mnemonics, tags, and an image to a word, **When** I view the word, **Then** I see an encoding strength indicator showing "Strong" (67-100% complete)
5. **Given** I have only added the target and native language terms, **When** I view the word, **Then** I see an encoding strength indicator showing "Basic" (0-33% complete)

---

### User Story 2 - Add Example Sentences with Context (Priority: P2)

As a language learner, I want to add multiple example sentences showing how vocabulary words are used in context, with translations and audio, so that I can learn words through meaningful usage rather than isolated memorization.

**Why this priority**: Example sentences bridge the gap between knowing a word and using it naturally. This implements the "learning inside" principle from the transcript where learners build understanding through authentic context. It's lower priority than basic encoding (P1) because it requires the foundational data structure improvements first.

**Independent Test**: Can be fully tested by selecting a vocabulary word, adding 2-3 example sentences in the target language with native translations, marking one as "core" (primary teaching example), and optionally generating audio. The sentences should appear in a list on the vocabulary detail page with playable audio buttons.

**Acceptance Scenarios**:

1. **Given** I am viewing a vocabulary word, **When** I click "Add Example Sentence" and enter a sentence in the target language with translation, **Then** the sentence is saved and appears in the example sentences list
2. **Given** I am adding an example sentence, **When** I mark it as "Core Example", **Then** it is visually highlighted as the primary teaching sentence for that word
3. **Given** I have added an example sentence, **When** I click the audio button next to the sentence, **Then** audio is generated (using existing app audio generation) and plays the sentence
4. **Given** I have multiple example sentences for a word, **When** I view the vocabulary detail page, **Then** I see all sentences listed with target language text, native translation, and audio playback option
5. **Given** I have added at least one example sentence to a word, **When** I view the encoding strength indicator, **Then** it reflects the presence of example sentences in the score calculation

---

### User Story 3 - Browse and Filter Words by Encoding Metadata (Priority: P3)

As a language learner, I want to browse vocabulary words by tags (e.g., "season", "nature") and see which words have weak encoding, so that I can focus my study efforts on words that need better memory associations.

**Why this priority**: Discovery and filtering tools help learners maintain their vocabulary effectively over time. This is lower priority because it provides value only after learners have populated words with encoding metadata (from P1 and P2). It's a quality-of-life improvement rather than a core learning feature.

**Independent Test**: Can be fully tested by navigating to a vocabulary list view, filtering by a tag like "nature", and seeing only words with that tag. Additionally, sorting by "encoding strength" should show words with fewer memory aids first, allowing learners to identify which words need improvement.

**Acceptance Scenarios**:

1. **Given** I am viewing my vocabulary list, **When** I filter by a tag (e.g., "season"), **Then** I see only vocabulary words that have that tag
2. **Given** I am viewing my vocabulary list, **When** I sort by "Encoding Strength", **Then** words with lower encoding scores appear first (Basic → Good → Strong)
3. **Given** I have words with various encoding levels, **When** I view the vocabulary list, **Then** each word displays its encoding strength indicator (Basic/Good/Strong) inline
4. **Given** I am viewing vocabulary filtered by tag, **When** I select a word, **Then** the word detail page opens showing all encoding metadata (mnemonics, images, tags, example sentences)
5. **Given** I want to find related words, **When** I click on a tag badge within a word's detail page, **Then** the vocabulary list filters to show all words with that tag

---

### User Story 4 - Store Lemma Forms for Dictionary Lookup (Priority: P3)

As a language learner studying Korean (or other inflected languages), I want the system to store the dictionary form (lemma) of words alongside conjugated forms, so that I can better organize and search my vocabulary by root forms.

**Why this priority**: Lemma support is important for linguistic accuracy and future-proofing (conjugation systems, better search), but it provides less immediate learner value than memory encoding features. Most learners won't notice or use this field directly in the MVP, making it lower priority than the user-facing encoding tools.

**Independent Test**: Can be fully tested by editing a vocabulary word with a conjugated form (e.g., "가면" - if you go), adding the lemma "가다" (to go), and verifying it's saved. Later, searching for "가다" should help locate related conjugated forms.

**Acceptance Scenarios**:

1. **Given** I am editing a vocabulary word with a conjugated form, **When** I fill in the "Lemma" field with the dictionary form, **Then** the lemma is saved alongside the word
2. **Given** I have stored lemmas for multiple conjugated words, **When** I search by lemma, **Then** I see all related word forms grouped together
3. **Given** I am viewing a vocabulary word detail page, **When** a lemma exists, **Then** it is displayed clearly as the "Dictionary Form" or "Base Form"

---

### Edge Cases

- What happens when a user adds a mnemonic image URL that is invalid or the image fails to load?
  - Display a placeholder image and allow the user to update the URL
- What happens when a user adds more than 50 tags to a single word?
  - Limit tags to a reasonable number (e.g., 10 tags per word) and display a validation message
- What happens when example sentences exceed typical length limits (e.g., 500+ characters)?
  - Display full sentences with scrolling or truncation; audio generation may have provider-specific limits (handle gracefully with error message)
- What happens when a user tries to generate audio for an example sentence while offline?
  - Queue the audio generation request and process when connectivity is restored, or display an error message prompting the user to retry when online
- Cross-platform considerations:
  - How does this work on small screens (mobile) vs large screens (desktop)?
    - Mobile: Use collapsible sections for mnemonics, tags, and example sentences; desktop can show expanded views side-by-side
  - What happens when device is offline?
    - All encoding metadata (mnemonics, tags, lemmas, example sentences text/translations) must be stored locally in SQLite and accessible offline. Only audio generation requires connectivity.
  - How does this handle platform-specific limitations (e.g., iOS/Android permissions)?
    - Image selection for mnemonics may require photo library permissions on mobile; provide clear permission prompts and fallback to URL input
  - What about platform-specific UI patterns (iOS navigation vs Android back button)?
    - Editing flows should respect platform navigation patterns; use MauiReactor navigation best practices

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST extend the VocabularyWord data model to store lemma (dictionary form), tags (comma-separated), mnemonic text, and mnemonic image URI
- **FR-002**: System MUST provide a vocabulary word editing interface where users can add and edit lemma, tags, mnemonics, and images
- **FR-003**: System MUST calculate an encoding strength score (0-1.0) based on the presence of: target term, native term, mnemonic text, mnemonic image, audio pronunciation, and example sentences
- **FR-004**: System MUST display encoding strength as a user-friendly indicator: "Basic" (0-0.33), "Good" (0.34-0.66), "Strong" (0.67-1.0)
- **FR-005**: System MUST create a new ExampleSentence data model with relationships to VocabularyWord and LearningResource, storing target sentence, native translation, audio URI, and core example flag
- **FR-006**: System MUST provide an interface for users to add, edit, and delete example sentences for each vocabulary word
- **FR-007**: System MUST allow users to mark one or more example sentences as "Core" to indicate primary teaching examples
- **FR-008**: System MUST generate audio for example sentences using the existing app audio generation infrastructure (ElevenLabs integration)
- **FR-009**: System MUST allow users to filter vocabulary words by tags (e.g., show only words tagged "nature")
- **FR-010**: System MUST allow users to sort vocabulary words by encoding strength (ascending/descending)
- **FR-011**: System MUST display tags as individual badges or chips in the vocabulary list and detail views
- **FR-012**: System MUST support clicking on a tag badge to filter the vocabulary list by that tag
- **FR-013**: Feature MUST work on iOS, Android, macOS, and Windows (cross-platform requirement)
- **FR-014**: Feature MUST work offline (SQLite local storage required) except for audio generation which requires connectivity
- **FR-015**: UI MUST use MauiReactor MVU pattern with semantic alignment methods
- **FR-016**: All user-facing strings MUST be localized (English + Korean support)
- **FR-017**: Styling MUST use `.ThemeKey()` or MyTheme constants (no hardcoded values)
- **FR-018**: System MUST persist all encoding metadata (lemmas, tags, mnemonics, images, example sentences) in SQLite database
- **FR-019**: System MUST display example sentences in a list format showing target language text, native translation, core indicator, and audio playback button
- **FR-020**: System MUST handle invalid image URLs gracefully by displaying placeholder images

### Key Entities

- **VocabularyWord (Extended)**: Represents a vocabulary term with encoding enhancements including lemma (dictionary form for inflected words), tags (comma-separated keywords for categorization), mnemonic text (silly story or memory association), mnemonic image URI (optional visual aid), and audio pronunciation URI. Maintains relationships to LearningResource and tracks creation/update timestamps.

- **ExampleSentence (New)**: Represents a contextual usage example for a vocabulary word. Contains target language sentence, native language translation, optional audio URI, core example flag (for primary teaching examples), and relationships to VocabularyWord and optionally LearningResource (source attribution). Tracks creation and update timestamps.

- **Encoding Strength (Derived)**: Calculated metric (not stored) representing completeness of memory encoding for a vocabulary word. Computed from presence of: target term, native term, mnemonic text, mnemonic image, audio pronunciation, and associated example sentences. Expressed as 0-1.0 float and displayed as Basic/Good/Strong categories.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Users can add mnemonics, tags, and images to a vocabulary word in under 2 minutes
- **SC-002**: Vocabulary words with "Strong" encoding (67-100% complete) are created at a rate 3x higher than baseline after feature launch (measured over 30 days)
- **SC-003**: Users can find and filter vocabulary by tags, with results appearing instantly (under 1 second)
- **SC-004**: 80% of vocabulary words with example sentences include at least one "Core" example marked by the user
- **SC-005**: Users can add 2-3 example sentences to a vocabulary word in under 3 minutes including audio generation
- **SC-006**: Encoding strength indicators display correctly and update in real-time as users add metadata to words
- **SC-007**: Tag-based filtering reduces time to find related vocabulary words by 50% compared to manual scrolling (measured via user timing studies)
- **SC-008**: Audio generation for example sentences succeeds at 95% rate when device has connectivity

### Assumptions

- Users are motivated to improve vocabulary retention and will invest time in adding encoding metadata
- The existing ElevenLabs audio generation infrastructure can handle example sentences of typical length (under 200 characters)
- Users will add 3-10 tags per word on average (not requiring advanced tag management UI)
- Comma-separated tag storage is sufficient for MVP; tag autocomplete/suggestions are future enhancements
- Image URLs are provided by users via paste/input; native image picker and upload are future enhancements
- The encoding strength calculation using equal weighting for all fields provides reasonable guidance; advanced weighting algorithms are future enhancements
- Lemma field is primarily for data organization and future-proofing; conjugation systems and smart grouping are future enhancements
