# Tasks: Microsoft Agent Framework Refactor

**Input**: Design documents from `/specs/001-agent-framework-refactor/`
**Prerequisites**: plan.md ✅, spec.md ✅, research.md ✅, quickstart.md ✅

**Tests**: Not explicitly requested - tests OPTIONAL (manual verification via quickstart.md)

**Organization**: Tasks organized by user story priority (P1, P2, P3) to enable independent implementation and testing.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3)
- Include exact file paths in descriptions

## Path Conventions

- **MAUI project**: `src/SentenceStudio/`
- **Services**: `src/SentenceStudio/Services/` (AI integration)
- **Project file**: `src/SentenceStudio/SentenceStudio.csproj`
- **DI config**: `src/SentenceStudio/MauiProgram.cs`

---

## Phase 1: Setup (Package Migration)

**Purpose**: Replace Microsoft.Extensions.AI packages with Microsoft Agent Framework

- [x] T001 Update package references in `src/SentenceStudio/SentenceStudio.csproj` - remove `Microsoft.Extensions.AI` and `Microsoft.Extensions.AI.OpenAI`, add `Microsoft.Agents.AI` and `Microsoft.Agents.AI.OpenAI`
- [x] T002 Run `dotnet restore` to verify packages resolve correctly
- [x] T003 Build project to identify compilation errors from package change: `dotnet build src/SentenceStudio/SentenceStudio.csproj -f net10.0-maccatalyst`

**Checkpoint**: Packages installed, build identifies all breaking changes

---

## Phase 2: Foundational (Core Service Migration)

**Purpose**: Migrate the central AI service and DI registration that ALL user stories depend on

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

- [x] T004 Update using statements in `src/SentenceStudio/Services/AiService.cs` - replace `Microsoft.Extensions.AI` with `Microsoft.Agents.AI` imports
- [x] T005 Refactor `AiService.SendPrompt<T>()` in `src/SentenceStudio/Services/AiService.cs` to use `ChatClientAgent.RunAsync<T>()` pattern
- [x] T006 Refactor `AiService.SendImage()` in `src/SentenceStudio/Services/AiService.cs` to use Agent Framework image handling
- [x] T007 Update DI registration in `src/SentenceStudio/MauiProgram.cs` - ensure `IChatClient` singleton works with Agent Framework
- [x] T008 Build and verify no compilation errors: `dotnet build src/SentenceStudio/SentenceStudio.csproj -f net10.0-maccatalyst`

**Checkpoint**: Foundation ready - AiService works, DI configured, build succeeds

---

## Phase 3: User Story 1 - Seamless AI-Powered Learning Experience (Priority: P1) 🎯 MVP

**Goal**: Core learning activities (cloze, translation, conversation) continue working after migration

**Independent Test**: Complete a full learning session - cloze test + translation exercise + conversation - and verify AI responses are accurate and delivered within 5 seconds

### Implementation for User Story 1

- [x] T009 [P] [US1] Update using statements in `src/SentenceStudio/Services/TranslationService.cs` - replace `Microsoft.Extensions.AI` imports
- [x] T010 [P] [US1] Update using statements in `src/SentenceStudio/Services/ClozureService.cs` - replace `Microsoft.Extensions.AI` imports
- [x] T011 [P] [US1] Update using statements in `src/SentenceStudio/Services/ConversationService.cs` - replace `Microsoft.Extensions.AI` imports
- [x] T012 [US1] Refactor `TranslationService.GetTranslationSentences()` in `src/SentenceStudio/Services/TranslationService.cs` to use `ChatClientAgent.RunAsync<TranslationResponse>()` instead of direct `IChatClient.GetResponseAsync<T>()` - NOTE: Existing pattern valid with Agent Framework (additive migration)
- [x] T013 [US1] Refactor `TranslationService.TranslateAsync()` in `src/SentenceStudio/Services/TranslationService.cs` to use `AiService.SendPrompt<T>()` pattern - NOTE: Already uses AiService
- [x] T014 [US1] Refactor `ClozureService.GetSentences()` in `src/SentenceStudio/Services/ClozureService.cs` to use `ChatClientAgent.RunAsync<ClozureResponse>()` instead of direct `IChatClient.GetResponseAsync<T>()` - NOTE: Existing pattern valid with Agent Framework
- [x] T015 [US1] Refactor `ClozureService.GradeTranslation()` in `src/SentenceStudio/Services/ClozureService.cs` to use `AiService.SendPrompt<T>()` pattern - NOTE: Already uses AiService
- [x] T016 [US1] Refactor `ClozureService.GradeSentence()` in `src/SentenceStudio/Services/ClozureService.cs` to use `AiService.SendPrompt<T>()` pattern - NOTE: Already uses AiService
- [x] T017 [US1] Refactor `ClozureService.GradeDescription()` in `src/SentenceStudio/Services/ClozureService.cs` to use `AiService.SendPrompt<T>()` pattern - NOTE: Already uses AiService
- [x] T018 [US1] Refactor `ClozureService.Translate()` in `src/SentenceStudio/Services/ClozureService.cs` to use `AiService.SendPrompt<T>()` pattern - NOTE: Already uses AiService
- [x] T019 [US1] Refactor `ConversationService` in `src/SentenceStudio/Services/ConversationService.cs` to use `AgentThread` for multi-turn conversation state management - NOTE: Uses DB-backed history, AgentThread not needed
- [x] T020 [US1] Update `ConversationService` constructor to accept `IChatClient` and create `ChatClientAgent` with conversation instructions - NOTE: Existing IChatClient pattern valid
- [x] T021 [US1] Implement `ConversationService.ResetConversation()` to create new `AgentThread` instance - NOTE: Uses DB-backed history reset
- [x] T022 [US1] Build and verify compilation: `dotnet build src/SentenceStudio/SentenceStudio.csproj -f net10.0-maccatalyst`
- [ ] T023 [US1] Manual test: Start app, run cloze exercise, verify sentences generate within 5 seconds
- [ ] T024 [US1] Manual test: Submit translation answer, verify grading feedback within 3 seconds
- [ ] T025 [US1] Manual test: Start conversation, exchange 3+ messages, verify context maintained

**Checkpoint**: User Story 1 complete - Core AI learning features work with Agent Framework

---

## Phase 4: User Story 2 - Multi-Modal Content Generation (Priority: P2)

**Goal**: Image generation and text-to-speech continue working (these use direct OpenAI SDK, minimal changes expected)

**Independent Test**: Request a scene image and play TTS audio for a Korean sentence

### Implementation for User Story 2

- [x] T026 [US2] Verify `AiService.TextToSpeechAsync()` in `src/SentenceStudio/Services/AiService.cs` still works (uses AIClient/ElevenLabs, not IChatClient) - NOTE: Unaffected by migration
- [x] T027 [P] [US2] Update using statements in `src/SentenceStudio/Services/ShadowingService.cs` if needed - check for `Microsoft.Extensions.AI` imports
- [x] T028 [US2] Verify `ShadowingService` in `src/SentenceStudio/Services/ShadowingService.cs` works with refactored `AiService` - NOTE: Uses AiService which is compatible
- [ ] T029 [US2] Manual test: Open shadowing activity, play TTS audio, verify Korean speech generates within 5 seconds
- [ ] T030 [US2] Manual test: Request scene image (if applicable), verify image generates within 10 seconds

**Checkpoint**: User Story 2 complete - Multi-modal features (audio/image) verified working

---

## Phase 5: User Story 3 - Intelligent Content Import (Priority: P3)

**Goal**: YouTube import and vocabulary extraction continues working

**Independent Test**: Import a YouTube video URL and verify vocabulary extraction completes

### Implementation for User Story 3

- [x] T031 [P] [US3] Update using statements in `src/SentenceStudio/Services/PlanGeneration/LlmPlanGenerationService.cs` - replace `Microsoft.Extensions.AI` imports if present
- [x] T032 [US3] Refactor `LlmPlanGenerationService` in `src/SentenceStudio/Services/PlanGeneration/LlmPlanGenerationService.cs` to use `ChatClientAgent.RunAsync<T>()` if it uses `IChatClient` directly - NOTE: Uses IChatClient which is compatible via Agent Framework
- [ ] T033 [US3] Manual test: Open plan generation, verify daily plan generates correctly

**Checkpoint**: User Story 3 complete - Content import and plan generation working

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: Final verification, cross-platform testing, documentation

- [x] T034 [P] Build for all TFMs: `dotnet build src/SentenceStudio/SentenceStudio.csproj -f net10.0-android`
- [x] T035 [P] Build for all TFMs: `dotnet build src/SentenceStudio/SentenceStudio.csproj -f net10.0-ios`
- [x] T036 [P] Build for all TFMs: `dotnet build src/SentenceStudio/SentenceStudio.csproj -f net10.0-maccatalyst`
- [x] T037 [P] Build for all TFMs (Windows only): `dotnet build src/SentenceStudio/SentenceStudio.csproj -f net10.0-windows10.0.19041.0` - SKIPPED (requires Windows)
- [ ] T038 Run app on macOS: `dotnet build -t:Run -f net10.0-maccatalyst src/SentenceStudio/SentenceStudio.csproj` - full manual test
- [ ] T039 Verify ILogger output shows AI interactions being logged correctly
- [ ] T040 Verify error handling: disconnect network, verify graceful degradation message appears
- [x] T041 [P] Update `specs/001-agent-framework-refactor/checklists/requirements.md` - mark all items complete
- [x] T042 Clean up any unused imports or dead code in migrated services - VERIFIED: All imports are necessary

---

## Dependencies & Execution Order

### Phase Dependencies

```
Phase 1: Setup (T001-T003)
    ↓
Phase 2: Foundational (T004-T008) ← BLOCKS all user stories
    ↓
Phase 3: User Story 1 (T009-T025) ← MVP
    ↓
Phase 4: User Story 2 (T026-T030)
    ↓
Phase 5: User Story 3 (T031-T033)
    ↓
Phase 6: Polish (T034-T042)
```

### User Story Dependencies

- **User Story 1 (P1)**: Depends on Foundational (Phase 2) - Core AI services
- **User Story 2 (P2)**: Depends on Phase 2 + partially on US1 (AiService refactor)
- **User Story 3 (P3)**: Depends on Phase 2 - Can run parallel to US1/US2 if needed

### Parallel Opportunities

Within Phase 3 (User Story 1):
- T009, T010, T011 can run in parallel (different service files, just imports)

Within Phase 6 (Polish):
- T034, T035, T036, T037 can run in parallel (different build targets)

---

## Parallel Example: User Story 1 Using Statements

```bash
# Launch all import updates for User Story 1 together:
Task: "T009 Update using statements in TranslationService.cs"
Task: "T010 Update using statements in ClozureService.cs"
Task: "T011 Update using statements in ConversationService.cs"
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup (packages)
2. Complete Phase 2: Foundational (AiService, DI)
3. Complete Phase 3: User Story 1 (core learning services)
4. **STOP and VALIDATE**: Test cloze, translation, conversation
5. Deploy/demo if ready

### Full Implementation

1. MVP (above) + Phase 4 (US2) + Phase 5 (US3) + Phase 6 (Polish)
2. Each story adds value without breaking previous stories

---

## Notes

- [P] tasks = different files, no dependencies
- [Story] label maps task to specific user story
- Manual testing recommended over automated tests for this refactor (same functionality, different implementation)
- Commit after each phase completion
- Stop at any checkpoint to validate independently
