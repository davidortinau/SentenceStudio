# Implementation Plan: Warmup Conversation Scenarios

**Branch**: `002-warmup-scenarios` | **Date**: 2026-01-24 | **Spec**: [spec.md](./spec.md)
**Input**: Feature specification from `specs/002-warmup-scenarios/spec.md`

## Summary

Enable users to select predefined conversation scenarios (ordering coffee, asking directions, etc.) or create custom scenarios conversationally for the Warmup activity. The AI conversation partner adapts persona, vocabulary, and behavior based on the active scenario, supporting both open-ended and finite conversation types.

**Technical Approach**: Extend the existing `Conversation` model with a scenario reference, create a new `ConversationScenario` entity stored in SQLite, and modify the conversation prompts (Scriban templates) to dynamically include scenario-specific context.

## Technical Context

**Language/Version**: .NET 10.0 (net10.0-android, net10.0-ios, net10.0-maccatalyst, net10.0-windows10.0.19041.0)  
**Primary Dependencies**: .NET MAUI 10.0.20+, MauiReactor, SQLite, Microsoft.Extensions.AI, ElevenLabs SDK  
**Storage**: SQLite local database with optional CoreSync synchronization  
**Testing**: xUnit for unit tests, platform-specific testing for UI  
**Target Platform**: iOS 12.2+, Android API 21+, macOS 15.0+, Windows 10.0.17763.0+  
**Project Type**: Multi-platform MAUI application (mobile + desktop)  
**Performance Goals**: <3s startup, <100ms UI response, <500ms AI API calls with loading indicators  
**Constraints**: Must work offline, all platforms tested, ILogger for production logs, Theme-first styling  
**Scale/Scope**: Single-user language learning app with custom curriculum support

**Existing Components to Extend**:
- `Conversation` model (add ScenarioId FK)
- `ConversationService` (scenario-aware prompts)
- `WarmupPage` (scenario selection UI)
- `Conversation.system.scriban-txt` (dynamic persona/context)

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

Verify alignment with SentenceStudio Constitution (`.specify/memory/constitution.md`):

- [X] **User-Centric AI-Powered Learning**: Enables personalized conversation practice with user-created scenarios
- [X] **Cross-Platform Native**: UI uses standard MAUI components (SfBottomSheet works cross-platform)
- [X] **MauiReactor MVU**: All UI will use semantic methods (`.HStart()`, `.VCenter()`, Grid layouts)
- [X] **Theme-First UI**: Scenario list will use `.ThemeKey()` and MyTheme constants
- [X] **Localization by Default**: All new strings will use `$"{_localize["Key"]}"` pattern
- [X] **Observability**: `ILogger<T>` will be used in ConversationService and scenario management
- [X] **Documentation in docs/**: Specs in `specs/002-warmup-scenarios/`, guides will go in `docs/`

**Violations requiring justification**: None

## Project Structure

### Documentation (this feature)

```text
specs/002-warmup-scenarios/
├── spec.md              # Feature specification ✓
├── plan.md              # This file ✓
├── research.md          # Phase 0 output
├── data-model.md        # Phase 1 output
├── quickstart.md        # Phase 1 output
├── contracts/           # Phase 1 output (internal APIs)
│   └── scenario-service.md
├── checklists/          # Quality checklists
│   └── requirements.md  # ✓
└── tasks.md             # Phase 2 output
```

### Source Code Changes

```text
src/SentenceStudio.Shared/
├── Models/
│   ├── ConversationScenario.cs    # NEW: Scenario entity
│   └── Conversation.cs            # MODIFY: Add ScenarioId
├── Data/
│   └── ApplicationDbContext.cs    # MODIFY: Add DbSet<ConversationScenario>
└── Migrations/
    └── AddConversationScenario.cs # NEW: EF migration

src/SentenceStudio/
├── Pages/Warmup/
│   └── WarmupPage.cs              # MODIFY: Add scenario selection UI
├── Services/
│   ├── ConversationService.cs     # MODIFY: Scenario-aware prompts
│   └── ScenarioService.cs         # NEW: Scenario CRUD + conversational creation
├── Data/
│   └── ScenarioRepository.cs      # NEW: Scenario persistence
└── Resources/
    ├── Raw/
    │   └── Conversation.scenario.scriban-txt  # NEW: Dynamic scenario prompt
    └── Strings/
        ├── AppResources.resx      # MODIFY: Add scenario strings
        └── AppResources.ko.resx   # MODIFY: Add Korean translations
```

**Structure Decision**: Extend existing architecture - no new projects needed. Scenarios are a natural extension of the Conversation domain.

## Complexity Tracking

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| None | - | - |
