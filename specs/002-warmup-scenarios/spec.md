# Feature Specification: Warmup Conversation Scenarios

**Feature Branch**: `002-warmup-scenarios`  
**Created**: 2026-01-24  
**Status**: Draft  
**Input**: User description: "Predefined conversation scenarios for Warmup activity with conversational scenario management"

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Select Predefined Scenario (Priority: P1)

A language learner wants to practice specific real-world conversational situations. When starting or during a warmup conversation, they can choose from predefined scenarios like "ordering coffee" or "asking for directions" to focus their practice on relevant vocabulary and situations.

**Why this priority**: Core value proposition - gives users targeted practice instead of only generic "getting acquainted" conversations. Without this, the activity remains single-purpose.

**Independent Test**: User can select a scenario from a list, and the AI conversation partner immediately adopts the appropriate role and context for that scenario.

**Acceptance Scenarios**:

1. **Given** user opens Warmup activity, **When** they tap a "Choose Scenario" option, **Then** they see a list of available scenarios with descriptions
2. **Given** user views scenario list, **When** they select "Ordering Coffee", **Then** a new conversation starts with the AI playing a barista role and initiating an appropriate greeting
3. **Given** user is mid-conversation, **When** they select a different scenario, **Then** the current conversation ends and a new one begins with the new scenario context

---

### User Story 2 - Scenario-Aware Conversations (Priority: P1)

The AI conversation partner behaves appropriately for the selected scenario - using relevant vocabulary, maintaining the correct role/persona, and either continuing indefinitely (open-ended) or naturally concluding (finite scenarios).

**Why this priority**: Directly tied to P1 - scenarios are meaningless if the AI doesn't adapt behavior accordingly. Must be implemented together.

**Independent Test**: Select "ordering dinner" scenario and verify AI acts as restaurant staff, uses food/ordering vocabulary, and conversation naturally progresses through ordering flow.

**Acceptance Scenarios**:

1. **Given** scenario "Ordering Coffee" is active, **When** AI responds, **Then** responses use cafe/coffee vocabulary and maintain barista persona
2. **Given** finite scenario "Ordering Dinner" is active, **When** user completes the order, **Then** AI naturally concludes the interaction (e.g., "Here's your receipt!")
3. **Given** open-ended scenario "Weekend Plans" is active, **When** conversation continues, **Then** AI keeps asking follow-up questions without forcing an ending

---

### User Story 3 - Create New Scenario via Conversation (Priority: P2)

Users want to create custom scenarios for situations not covered by predefined options. Rather than filling out forms, they can describe what they want conversationally (e.g., "I want to practice ordering at a pharmacy").

**Why this priority**: Extends functionality beyond predefined options. Important for personalization but system is useful without it initially.

**Independent Test**: User types "I want to create a scenario about returning an item at a store" and system guides them through setup, then the scenario appears in their list.

**Acceptance Scenarios**:

1. **Given** user is in Warmup activity, **When** they say "I want to create a new scenario", **Then** the system asks clarifying questions (who they'll talk to, what situation, open/finite)
2. **Given** user describes "practicing at a Korean pharmacy", **When** they confirm details, **Then** a new scenario "Pharmacy Visit" is saved to their scenario list
3. **Given** user creates a scenario, **When** they return to scenario selection, **Then** their custom scenario appears alongside predefined ones

---

### User Story 4 - Edit Existing Scenario via Conversation (Priority: P3)

Users may want to modify scenarios - changing the persona, adjusting the situation description, or switching between open-ended and finite mode.

**Why this priority**: Enhancement feature - creates a complete scenario management experience but users can delete and recreate if needed.

**Independent Test**: User says "I want to change my pharmacy scenario to be about buying cold medicine specifically" and the scenario updates accordingly.

**Acceptance Scenarios**:

1. **Given** user has a custom scenario "Pharmacy Visit", **When** they say "edit pharmacy scenario", **Then** system shows current settings and asks what to change
2. **Given** user wants to change scenario type, **When** they say "make it finite", **Then** the scenario updates to end naturally after completing the task
3. **Given** user modifies scenario description, **When** confirmed, **Then** future conversations use the updated context

---

### Edge Cases

- What happens when user switches scenarios mid-conversation?
  - Current conversation is saved and marked as ended; new conversation starts with new scenario
- How does the system handle ambiguous scenario creation requests?
  - AI asks clarifying questions until it has: persona, situation, and open/finite designation
- What if user tries to edit a predefined scenario?
  - Predefined scenarios cannot be modified; user can duplicate to custom and then edit
- Cross-platform considerations:
  - Scenario selection UI must work on mobile (bottom sheet) and desktop (dialog or sidebar)
  - Scenarios sync across devices via existing CoreSync infrastructure
  - Offline: users can access previously synced scenarios; new scenarios save locally and sync when online

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST provide at least 5 predefined scenarios: "Ordering Coffee", "Ordering Dinner", "Asking for Directions", "Weekend Plans Discussion", "First Meeting/Introductions" (current default)
- **FR-002**: Each scenario MUST have: display name, who the AI plays (persona), situation description, and conversation type (open-ended or finite)
- **FR-003**: System MUST allow users to select a scenario before starting a new conversation
- **FR-004**: AI conversation partner MUST adapt persona, vocabulary, and behavior based on active scenario
- **FR-005**: Finite scenarios MUST conclude naturally when the transactional goal is achieved
- **FR-006**: Open-ended scenarios MUST continue until user explicitly ends or switches scenarios
- **FR-007**: Users MUST be able to create new scenarios through natural language conversation
- **FR-008**: Users MUST be able to edit their custom scenarios through conversation
- **FR-009**: System MUST persist scenarios locally and sync via CoreSync
- **FR-010**: Feature MUST work on iOS, Android, macOS, and Windows (cross-platform requirement)
- **FR-011**: Feature MUST work offline (SQLite local storage required)
- **FR-012**: UI MUST use MauiReactor MVU pattern with semantic alignment methods
- **FR-013**: All user-facing strings MUST be localized (English + Korean support)
- **FR-014**: Styling MUST use `.ThemeKey()` or MyTheme constants (no hardcoded values)
- **FR-015**: Users MUST NOT be able to modify predefined scenarios (read-only)
- **FR-016**: Users MUST be able to delete their custom scenarios

### Key Entities

- **ConversationScenario**: Represents a conversation practice scenario with name, persona description, situation context, conversation type (open-ended/finite), and whether it's predefined or user-created
- **Conversation**: Existing entity - extended to reference the active scenario when started
- **ScenarioPersona**: The role the AI plays (e.g., "barista", "waiter", "stranger on the street")

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Users can start a scenario-based conversation within 3 taps from the Warmup activity
- **SC-002**: AI maintains consistent persona throughout an entire conversation (no role breaks)
- **SC-003**: Users can create a new custom scenario in under 2 minutes of conversational interaction
- **SC-004**: 90% of users successfully select and complete a scenario-based conversation on first attempt
- **SC-005**: Custom scenarios persist and appear correctly after app restart
- **SC-006**: Finite scenarios conclude within expected interaction count (e.g., coffee order completes in 4-8 exchanges)

## Assumptions

- The current `Conversation.system.scriban-txt` prompt can be extended/replaced per-scenario
- AI (via existing `IChatClient`) can reliably follow persona and scenario instructions
- Predefined scenarios will use Korean personas consistent with current "김철수" default
- Question banks will be scenario-specific (cafe vocabulary vs. directions vocabulary)
- "First Meeting/Introductions" becomes the default scenario, preserving current behavior for users who don't select a specific scenario
