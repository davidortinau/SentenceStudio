# Feature Specification: Microsoft Agent Framework Refactor

**Feature Branch**: `001-agent-framework-refactor`  
**Created**: 2026-01-27  
**Status**: Complete  
**Input**: User description: "refactor Microsoft.Extensions.AI implementation to use Microsoft Agent Framework instead"

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Seamless AI-Powered Learning Experience (Priority: P1)

As a language learner, I want my learning activities (vocabulary quizzes, translation exercises, cloze tests, conversation practice) to continue working seamlessly after the AI system migration, so that my learning experience is uninterrupted and potentially improved.

**Why this priority**: This is the core value proposition - all existing AI-powered features must continue to function correctly. Without this, the app loses its primary functionality.

**Independent Test**: Can be fully tested by completing a full learning session (vocabulary quiz + translation exercise) and verifying AI responses are accurate, contextually appropriate, and delivered within acceptable time limits.

**Acceptance Scenarios**:

1. **Given** a user has selected a vocabulary resource with words to learn, **When** they start a cloze test activity, **Then** the system generates contextually appropriate fill-in-the-blank sentences using the specified vocabulary within 5 seconds.

2. **Given** a user completes a translation exercise, **When** they submit their answer, **Then** the system provides graded feedback with corrections and explanations within 3 seconds.

3. **Given** a user is in a conversation practice session, **When** they send a message, **Then** the AI responds naturally and contextually within 3 seconds.

---

### User Story 2 - Multi-Modal Content Generation (Priority: P2)

As a language learner, I want the app to generate images for scene descriptions and convert text to speech for pronunciation practice, so that I have a rich, multi-sensory learning experience.

**Why this priority**: Multi-modal learning (visual + audio) is a differentiator for this app. Image and speech generation must continue to work reliably.

**Independent Test**: Can be tested by requesting a scene image for a vocabulary word and playing back text-to-speech audio for a Korean sentence.

**Acceptance Scenarios**:

1. **Given** a user is in a scene description activity, **When** they request an image for a vocabulary concept, **Then** the system generates a relevant image within 10 seconds.

2. **Given** a user wants to hear pronunciation of a Korean sentence, **When** they tap the audio button, **Then** the system generates and plays natural-sounding Korean speech within 5 seconds.

---

### User Story 3 - Intelligent Content Import (Priority: P3)

As a content creator/learner, I want to import YouTube videos and have the system extract vocabulary and generate learning materials, so that I can learn from real-world content I enjoy.

**Why this priority**: Content import is a growth feature that expands the app's usefulness but is not critical for core learning activities.

**Independent Test**: Can be tested by importing a YouTube video URL and verifying vocabulary extraction and shadowing materials are generated.

**Acceptance Scenarios**:

1. **Given** a user provides a YouTube video URL, **When** they initiate import, **Then** the system extracts vocabulary, generates transcriptions, and creates shadowing exercises within 60 seconds.

---

### Edge Cases

- What happens when the AI service is unavailable or rate-limited?
  - System displays a user-friendly offline message and queues requests for retry
  - Previously cached responses can be shown where applicable
  
- How does the system handle malformed or unexpected AI responses?
  - Graceful degradation with error logging and user notification
  - Retry logic with exponential backoff for transient failures

- What happens when device is offline?
  - AI features are disabled with clear messaging
  - Local-only features (vocabulary browsing, progress viewing) remain functional
  
- How does this handle long AI response times?
  - Loading indicators show progress
  - Timeout after 30 seconds with user option to retry
  - Cancel option available during loading

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST replace all Microsoft.Extensions.AI `IChatClient` usages with Microsoft Agent Framework equivalents
- **FR-002**: System MUST maintain all existing AI capabilities (text generation, structured output parsing, image analysis)
- **FR-003**: System MUST preserve existing prompt templates (Scriban-based) and their rendering
- **FR-004**: System MUST continue supporting structured JSON responses via DTOs with `[Description]` attributes
- **FR-005**: System MUST maintain text-to-speech functionality for Korean language learning
- **FR-006**: System MUST maintain image generation capabilities for scene descriptions
- **FR-007**: System MUST handle network connectivity changes gracefully with appropriate user feedback
- **FR-008**: System MUST log AI interactions using the existing ILogger infrastructure
- **FR-009**: All AI services MUST be registered via dependency injection in MauiProgram.cs
- **FR-010**: System MUST maintain backward compatibility with existing data models and database schema
- **FR-011**: Feature MUST work on iOS, Android, macOS, and Windows (cross-platform requirement)

### Assumptions

- The Microsoft Agent Framework provides equivalent functionality to Microsoft.Extensions.AI for:
  - Chat completions with structured output
  - Image input/analysis
  - Extensibility for audio/image generation via OpenAI clients
- API keys and configuration will remain in the existing appsettings.json format
- No changes to the UI layer are required; this is a backend/service layer refactor only
- Existing prompt templates will work with the new framework without modification

### Key Entities

- **AiService**: Central service coordinating AI interactions; will be refactored to use Agent Framework
- **TranslationService**: Service generating translation exercises; uses IChatClient for structured responses
- **ClozureService**: Service generating cloze (fill-in-blank) exercises; uses IChatClient for structured responses
- **ConversationService**: Service managing AI conversation sessions
- **ShadowingService**: Service generating shadowing (listen-and-repeat) exercises
- **LlmPlanGenerationService**: Service generating personalized learning plans via AI

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: All existing AI-powered learning activities complete successfully with 95%+ success rate (matching or exceeding current baseline)
- **SC-002**: AI response times remain within 5 seconds for text generation and 10 seconds for image generation (matching current performance)
- **SC-003**: Zero regression in existing automated test suite coverage
- **SC-004**: All 8 affected service files compile and run without errors on all target platforms
- **SC-005**: Application builds successfully for iOS, Android, macOS, and Windows
- **SC-006**: Memory usage and battery consumption remain within 10% of current baseline during AI operations
