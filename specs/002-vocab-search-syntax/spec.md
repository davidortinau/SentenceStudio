# Feature Specification: Vocabulary Search Syntax

**Feature Branch**: `002-vocab-search-syntax`  
**Created**: 2025-12-12  
**Status**: Draft  
**Input**: User description: "the search functionality on src/SentenceStudio/Pages/VocabularyManagement/VocabularyManagementPage.cs has gotten weird and complex. I want to replace the different UI options with a search syntax approach that let's me do things like \"tag:nature tag:environment resource:general cloud\" similar to what we can do searching issues and prs on github.com."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Simple Text Search (Priority: P1)

As a language learner, I want to type words directly into the search box to find vocabulary entries by their target or native language terms, so I can quickly locate specific words without navigating through filters.

**Why this priority**: Core search functionality - users expect text search to work immediately. This is the foundation that must work before advanced syntax features.

**Independent Test**: Type "cloud" in search box and verify only vocabulary items containing "cloud" in either language field are displayed. No other UI interaction required.

**Acceptance Scenarios**:

1. **Given** I have vocabulary words including "cloud" and "구름", **When** I type "cloud" in the search box, **Then** I see all vocabulary entries containing "cloud" in either target or native language terms
2. **Given** I type "구름" in the search box, **When** the search executes, **Then** I see vocabulary entries containing Korean "구름" 
3. **Given** I have typed a search term, **When** I clear the search box, **Then** all vocabulary items are displayed again

---

### User Story 2 - Tag-Based Filtering (Priority: P2)

As a language learner, I want to search using `tag:nature` syntax to find all vocabulary related to specific topics, so I can review themed vocabulary groups efficiently.

**Why this priority**: Enables targeted study sessions by topic. Builds on P1 search infrastructure while adding structured filtering capability.

**Independent Test**: Type "tag:nature" in search box and verify only vocabulary items tagged with "nature" are displayed. Can combine with text: "tag:nature cloud" shows only nature-tagged items containing "cloud".

**Acceptance Scenarios**:

1. **Given** I have vocabulary tagged with "nature" and "environment", **When** I search "tag:nature", **Then** I see only vocabulary items tagged with "nature"
2. **Given** I search "tag:nature tag:environment", **When** results display, **Then** I see vocabulary items tagged with EITHER "nature" OR "environment" (OR logic)
3. **Given** I search "tag:nature cloud", **When** results display, **Then** I see vocabulary tagged with "nature" AND containing "cloud" in either language field

---

### User Story 3 - Resource-Based Filtering (Priority: P2)

As a language learner, I want to search using `resource:general` syntax to find vocabulary from specific learning resources, so I can review words from a particular lesson or book.

**Why this priority**: Critical for users who organize vocabulary by source material. Equal priority to tag search as both enable structured filtering.

**Independent Test**: Type "resource:general" and verify only vocabulary associated with "General Vocabulary" resource are displayed.

**Acceptance Scenarios**:

1. **Given** I have vocabulary associated with "General Vocabulary" and "Business Vocabulary" resources, **When** I search "resource:general", **Then** I see only vocabulary from "General Vocabulary" resource
2. **Given** I search "resource:general resource:business", **When** results display, **Then** I see vocabulary from EITHER resource (OR logic)
3. **Given** I search "resource:general tag:nature", **When** results display, **Then** I see vocabulary from "General Vocabulary" resource AND tagged with "nature"

---

### User Story 4 - Status-Based Filtering (Priority: P3)

As a language learner, I want to search using `status:learning` or `status:known` to filter by my learning progress, so I can focus on words at specific mastery levels.

**Why this priority**: Useful for targeted review but less critical than topic/resource filtering. Users can still achieve similar results through the existing UI.

**Independent Test**: Type "status:learning" and verify only vocabulary marked as "learning" in progress are displayed.

**Acceptance Scenarios**:

1. **Given** I have vocabulary in different progress states, **When** I search "status:learning", **Then** I see only vocabulary currently in "learning" state
2. **Given** I search "status:known", **When** results display, **Then** I see only vocabulary marked as "known"
3. **Given** I search "status:unknown tag:nature", **When** results display, **Then** I see unknown vocabulary tagged with "nature"

---

### User Story 5 - Orphaned Word Search (Priority: P3)

As a language learner, I want to search using `is:orphaned` to find vocabulary not associated with any learning resource, so I can organize unassigned words.

**Why this priority**: Administrative function for maintaining vocabulary organization. Less urgent than learning-focused features.

**Independent Test**: Type "is:orphaned" and verify only vocabulary with no resource associations are displayed.

**Acceptance Scenarios**:

1. **Given** I have orphaned and associated vocabulary, **When** I search "is:orphaned", **Then** I see only vocabulary not associated with any resource
2. **Given** I search "is:orphaned tag:nature", **When** results display, **Then** I see orphaned vocabulary tagged with "nature"

---

### User Story 6 - Encoding Strength Search (Priority: P3)

As a language learner, I want to search using `encoding:basic`, `encoding:good`, or `encoding:strong` to find vocabulary by encoding strength, so I can identify which words need more context added.

**Why this priority**: Supports vocabulary enrichment workflow but not essential for daily learning. Can be deferred if needed.

**Independent Test**: Type "encoding:basic" and verify only vocabulary with basic encoding strength are displayed.

**Acceptance Scenarios**:

1. **Given** I have vocabulary with different encoding strengths, **When** I search "encoding:basic", **Then** I see vocabulary with basic encoding (missing mnemonics/examples)
2. **Given** I search "encoding:strong", **When** results display, **Then** I see vocabulary with strong encoding (complete metadata)
3. **Given** I search "encoding:basic tag:nature", **When** results display, **Then** I see nature-tagged vocabulary with basic encoding

---

### Edge Cases

- **Empty search**: When search box is empty, all vocabulary items are displayed (no filters applied)
- **Invalid syntax**: When user types unrecognized qualifiers (e.g., "invalid:value"), treat entire string as text search and show no results if no matches found
- **Case sensitivity**: All search qualifiers (`tag:`, `resource:`, `status:`, `is:`, `encoding:`) are case-insensitive
- **Whitespace handling**: Multiple spaces between search terms are treated as single space; leading/trailing spaces are trimmed
- **Special characters**: Search handles Korean characters, emojis, and punctuation in text search terms
- **Non-existent values**: Searching for `tag:nonexistent` or `resource:missing` returns zero results gracefully (no error)
- **Multiple values for same qualifier**: `tag:nature tag:environment` uses OR logic (show items matching either tag)
- **Cross-platform considerations**:
  - Works identically on iOS, Android, macOS, and Windows (single search entry field)
  - Search persists when navigating away and returning to vocabulary management page
  - Search box size adapts to screen width (mobile vs desktop)
  - No platform-specific keyboard shortcuts required (standard text entry)
  - Offline-capable (all searches execute against local SQLite database)

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST parse search text to identify qualifiers (`tag:`, `resource:`, `status:`, `is:`, `encoding:`) and extract their values
- **FR-002**: System MUST treat text without qualifiers as simple text search against target and native language terms
- **FR-003**: System MUST support multiple occurrences of the same qualifier with OR logic (e.g., `tag:nature tag:environment` matches either tag)
- **FR-004**: System MUST support combining different qualifier types with AND logic (e.g., `tag:nature resource:general` matches items with nature tag AND from general resource)
- **FR-005**: System MUST support combining qualifiers with free text search with AND logic (e.g., `tag:nature cloud` matches nature-tagged items containing "cloud")
- **FR-006**: System MUST update results as user types with debouncing (300ms delay to avoid excessive queries)
- **FR-007**: System MUST persist search query when user navigates away and returns to vocabulary management page
- **FR-008**: System MUST display clear visual feedback when search returns zero results
- **FR-009**: System MUST handle all searches against local SQLite database (offline-capable)
- **FR-010**: System MUST replace existing filter UI controls (status picker, resource picker, tag picker, encoding sort button) with single search entry field
- **FR-011**: Feature MUST work on iOS, Android, macOS, and Windows (cross-platform requirement)
- **FR-012**: Feature MUST work offline (SQLite local storage required)
- **FR-013**: UI MUST use MauiReactor MVU pattern with semantic alignment methods
- **FR-014**: All user-facing strings MUST be localized (English + Korean support)
- **FR-015**: Styling MUST use `.ThemeKey()` or MyTheme constants (no hardcoded values)

### Key Entities

- **VocabularyWord**: Core entity representing vocabulary entries with target/native terms, tags, lemma, mnemonics, and encoding metadata
- **LearningResource**: Resources associated with vocabulary (e.g., "General Vocabulary", "Business Vocabulary")
- **VocabularyProgress**: User's learning progress for each vocabulary word (known, learning, unknown states)
- **ExampleSentence**: Sentences demonstrating vocabulary usage (contributes to encoding strength)
- **SearchQuery**: Parsed representation of user's search input containing qualifiers and text terms (runtime only, not persisted)

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Users can find vocabulary using search syntax in under 5 seconds from page load
- **SC-002**: Search results update within 300ms of typing (debounce delay)
- **SC-003**: 95% of searches complete without user needing to refer to syntax help documentation
- **SC-004**: Search functionality reduces number of UI controls on vocabulary management page by at least 4 (replacing status picker, resource picker, tag picker, encoding sort button with single search field)
- **SC-005**: Users can combine 3+ search criteria (e.g., tag + resource + text) in a single query
- **SC-006**: Search syntax works identically across all platforms (iOS, Android, macOS, Windows) with zero platform-specific code paths

## Assumptions

- **Search syntax follows GitHub-style patterns**: Users familiar with GitHub search will find syntax intuitive
- **OR logic for repeated qualifiers**: Multiple tags or resources are combined with OR (show items matching ANY), consistent with GitHub
- **AND logic across qualifier types**: Different qualifier types are combined with AND (show items matching ALL), consistent with GitHub
- **Case-insensitive matching**: All searches are case-insensitive for better user experience
- **Debouncing prevents performance issues**: 300ms debounce delay is sufficient to avoid excessive database queries while feeling responsive
- **No complex boolean syntax**: Users do NOT expect parentheses, NOT operators, or complex boolean expressions in MVP
- **Existing data structure supports all searches**: Current VocabularyWord, LearningResource, and VocabularyProgress models contain all necessary fields for filtering
- **No search history or suggestions**: Initial version does NOT persist search history or provide autocomplete suggestions (could be future enhancement)
- **Help text is discoverable**: Users can access syntax help through standard platform help mechanisms (e.g., placeholder text, info button)

## Dependencies

- **VocabularyEncodingRepository**: Provides access to encoding strength calculations and tag filtering
- **LearningResourceRepository**: Provides access to vocabulary-resource associations
- **VocabularyProgressService**: Provides access to user's learning progress for status filtering
- **Existing debouncing mechanism**: Current implementation already uses 300ms timer for search debouncing

## Out of Scope

The following are explicitly NOT included in this feature:

- **Search history or recent searches**: No persistence of previous search queries
- **Autocomplete or search suggestions**: No dropdown of suggested search terms as user types
- **Boolean NOT operator**: Cannot exclude terms (e.g., `-tag:nature` or `NOT tag:nature`)
- **Complex boolean expressions**: No support for parentheses or complex boolean logic
- **Saved search filters**: Cannot save frequently-used searches as named filters
- **Search syntax help UI**: No in-app tutorial or interactive syntax guide (assumes users refer to documentation or discover through experimentation)
- **Fuzzy matching**: Exact text matching only (no typo tolerance or similarity search)
- **Search within specific fields**: Cannot specify to search only target or only native language term
- **Date-based filtering**: Cannot filter by created date, modified date, or review dates
- **Numeric range queries**: Cannot use ranges like `attempts:>5` or `mastery:0..50`
