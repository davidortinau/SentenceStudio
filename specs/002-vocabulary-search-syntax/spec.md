# Feature Specification: Vocabulary Search Syntax

**Feature Branch**: `002-vocabulary-search-syntax`  
**Created**: 2025-12-12  
**Status**: Draft  
**Input**: User description: "Replace the different UI options with a search syntax approach that lets me do things like 'tag:nature tag:environment resource:general cloud' similar to what we can do searching issues and prs on github.com. I also want to be able to search by lemma"

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Basic Text Search (Priority: P1)

Users can search vocabulary by entering free text that matches against the target language term, native language term, or lemma. The search updates results in real-time as they type.

**Why this priority**: This is the foundation of the search feature and provides immediate value - users need to find words quickly using natural text input before advanced filtering is useful.

**Independent Test**: Can be fully tested by typing text into the search box and verifying that results show words matching the text in any of the searchable fields. Delivers immediate word-finding value without any other search features.

**Acceptance Scenarios**:

1. **Given** user is on vocabulary management page, **When** they type "단풍" in search box, **Then** all words containing "단풍" in target term, native term, or lemma appear in results
2. **Given** user has typed a search term, **When** they clear the search box, **Then** all vocabulary words reappear
3. **Given** search results are displayed, **When** user types additional characters, **Then** results update in real-time without delay

---

### User Story 2 - Tag-Based Filtering (Priority: P2)

Users can filter vocabulary by tags using the syntax `tag:tagname`. Multiple tag filters can be combined (e.g., `tag:nature tag:season`), and tag filters work alongside free text search.

**Why this priority**: Tag filtering is the most commonly requested filter type and enables users to study themed vocabulary sets (nature words, food words, etc.).

**Independent Test**: Can be fully tested by entering `tag:nature` and verifying only words with the "nature" tag appear. Delivers value for users who organize vocabulary by topic.

**Acceptance Scenarios**:

1. **Given** vocabulary has tags, **When** user types "tag:nature", **Then** only words with the "nature" tag appear
2. **Given** user has entered one tag filter, **When** they add "tag:season", **Then** only words with BOTH tags appear (AND logic)
3. **Given** user has entered "tag:nature", **When** they add text "가을", **Then** results show words that match the tag AND contain the text
4. **Given** user types "tag:nat", **When** they continue typing, **Then** autocomplete suggestions show available tags starting with "nat"

---

### User Story 3 - Resource-Based Filtering (Priority: P2)

Users can filter vocabulary by learning resource using the syntax `resource:resourcename`. Multiple resource filters can be combined, and resource filters work alongside text and tag searches.

**Why this priority**: This replaces the existing resource filter dropdown with a more flexible syntax that users can combine with other filters.

**Independent Test**: Can be fully tested by entering `resource:general` and verifying only words from the "general" resource appear. Delivers value for users studying specific materials.

**Acceptance Scenarios**:

1. **Given** vocabulary is associated with resources, **When** user types "resource:general", **Then** only words from the "general" resource appear
2. **Given** user has entered one resource filter, **When** they add "resource:textbook", **Then** words from EITHER resource appear (OR logic for same filter type)
3. **Given** user types "resource:gen", **When** they continue typing, **Then** autocomplete suggestions show available resources starting with "gen"

---

### User Story 4 - Lemma-Based Search (Priority: P3)

Users can search specifically by lemma (dictionary form) using the syntax `lemma:단어` or by including lemmas in free text search.

**Why this priority**: This enables more precise searching for verb and adjective forms, but is less commonly used than tags and resources.

**Independent Test**: Can be fully tested by entering `lemma:가다` and verifying words with that lemma appear (including conjugated forms like "갔어요", "가요"). Delivers value for grammar-focused study.

**Acceptance Scenarios**:

1. **Given** vocabulary has lemmas defined, **When** user types "lemma:가다", **Then** all words with that lemma appear (including conjugated forms)
2. **Given** user enters free text without prefix, **When** text matches a lemma, **Then** words with that lemma appear in results
3. **Given** user has entered "lemma:가다", **When** they add "tag:motion", **Then** only words matching both filters appear

---

### User Story 5 - Status-Based Filtering (Priority: P3)

Users can filter vocabulary by learning status using the syntax `status:known`, `status:learning`, or `status:unknown`.

**Why this priority**: This replaces the existing status filter dialog with syntax, but is less critical than topical filtering (tags/resources).

**Independent Test**: Can be fully tested by entering `status:learning` and verifying only words in learning phase appear. Delivers value for focused review sessions.

**Acceptance Scenarios**:

1. **Given** user has vocabulary in different learning states, **When** they type "status:known", **Then** only words marked as known appear
2. **Given** user has entered "status:learning", **When** they add "tag:nature", **Then** only learning-phase words with the nature tag appear

---

### User Story 6 - Filter Combination and Clearing (Priority: P1)

Users can combine multiple filter types in one search query. The system clearly shows active filters and provides easy ways to clear individual filters or the entire search.

**Why this priority**: This is essential for the feature to be usable - users must be able to combine filters and understand/modify what filters are active.

**Independent Test**: Can be fully tested by entering `tag:nature resource:general status:learning 가을` and verifying all filters apply correctly, then clearing filters one at a time. Delivers immediate usability value.

**Acceptance Scenarios**:

1. **Given** user has entered "tag:nature resource:general 가을", **When** search executes, **Then** results match all three criteria (tag AND resource AND text)
2. **Given** multiple filters are active, **When** user views search box, **Then** filter chips appear showing each active filter
3. **Given** filter chips are displayed, **When** user taps the X on a chip, **Then** that specific filter is removed and results update
4. **Given** multiple filters are active, **When** user taps "Clear all" button, **Then** all filters are removed and full vocabulary list appears

---

### Edge Cases

- **Empty search with filters**: What happens when user enters only filter prefixes with no values (e.g., "tag:")?
  - System should show autocomplete suggestions or ignore invalid filter
- **Nonexistent tags/resources**: How does system handle "tag:xyz" when "xyz" tag doesn't exist?
  - System should show zero results and indicate no matches found for that tag
- **Malformed filter syntax**: What happens when user types "tag nature" (missing colon)?
  - System treats entire string as free text search, no special filter applied
- **Case sensitivity**: Should "tag:Nature" match "tag:nature"?
  - All filter comparisons should be case-insensitive
- **Partial matches**: Should "resource:gen" match "general" resource?
  - In autocomplete yes, in actual filter only exact matches after selection
- **Cross-platform considerations**:
  - How does this work on small screens (mobile) vs large screens (desktop)?
    - Filter chips should wrap on mobile, inline on desktop
    - Autocomplete dropdown should be full-width on mobile
  - What happens when device is offline?
    - All search functionality works offline (SQLite local storage)
  - How does this handle platform-specific limitations (e.g., iOS/Android permissions)?
    - No special permissions needed for search functionality
  - What about platform-specific UI patterns (iOS navigation vs Android back button)?
    - Back button/gesture should close autocomplete if open, otherwise navigate back

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST parse search query into filter tokens and free text components
- **FR-002**: System MUST support filter syntax: `tag:value`, `resource:value`, `lemma:value`, `status:value`
- **FR-003**: System MUST treat text without prefix as free-text search against target term, native term, and lemma
- **FR-004**: System MUST apply AND logic when combining different filter types (tag + resource + text)
- **FR-005**: System MUST apply OR logic when combining multiple filters of the same type (resource:A resource:B)
- **FR-006**: System MUST provide real-time autocomplete suggestions for tags, resources, and lemmas as user types filter prefix
- **FR-007**: System MUST display active filters as removable chips above or within the search input
- **FR-008**: System MUST update search results in real-time as user types or modifies filters
- **FR-009**: System MUST preserve search query when user navigates away and returns to page
- **FR-010**: System MUST perform all search operations against local SQLite database (offline support)
- **FR-011**: Feature MUST work on iOS, Android, macOS, and Windows (cross-platform requirement)
- **FR-012**: UI MUST use MauiReactor MVU pattern with semantic alignment methods
- **FR-013**: All user-facing strings MUST be localized (English + Korean support)
- **FR-014**: Styling MUST use `.ThemeKey()` or MyTheme constants (no hardcoded values)
- **FR-015**: System MUST handle case-insensitive matching for all filter values
- **FR-016**: System MUST remove existing filter icon buttons (status filter, resource filter) from UI
- **FR-017**: System MUST maintain tag filter bar for encoding metadata display (separate from search syntax)

### Key Entities

- **SearchQuery**: Represents parsed user input containing filter tokens (tag, resource, lemma, status) and free text components
- **FilterToken**: Individual filter extracted from query (type, value), e.g., (tag, "nature")
- **AutocompleteSuggestion**: Available filter values displayed to user while typing (tag names, resource names, lemmas)
- **FilterChip**: Visual representation of active filter that can be removed by user

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Users can find specific vocabulary using combined filters in under 5 seconds
- **SC-002**: Search results update in real-time with less than 200ms delay as user types
- **SC-003**: 90% of users successfully use at least one filter syntax (tag, resource, lemma) within first usage
- **SC-004**: Search query parsing handles 100% of valid syntax combinations without errors
- **SC-005**: Autocomplete suggestions appear within 100ms of user typing filter prefix
- **SC-006**: Users can clear individual filters or entire search with single tap/click
- **SC-007**: Search functionality works identically on all platforms (iOS, Android, macOS, Windows) without platform-specific bugs

## Assumptions

- Users are familiar with GitHub-style search syntax or can learn it through autocomplete suggestions
- Existing vocabulary data model already includes Tags and Lemma fields (from previous encoding feature)
- Current search implementation uses LINQ queries that will be replaced with optimized SQLite queries
- Autocomplete suggestion lists are small enough to display without pagination (estimated <50 items per type)
- Users prefer unified search syntax over separate filter controls for improved workflow efficiency
