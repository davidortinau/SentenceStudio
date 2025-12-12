# Implementation Plan: Vocabulary Search Syntax

**Branch**: `002-vocabulary-search-syntax` | **Date**: 2025-12-12 | **Spec**: [spec.md](./spec.md)
**Input**: Feature specification from `/specs/002-vocabulary-search-syntax/spec.md`

## Summary

Replace the complex UI filter controls (status dialog, resource dropdown) on VocabularyManagementPage with a unified GitHub-style search syntax that supports: `tag:nature`, `resource:general`, `lemma:가다`, `status:learning`, and free-text search. The search query parser will extract filter tokens and apply them using optimized SQLite queries. Autocomplete suggestions powered by UXD popups will guide users through available filter values. Filter chips will provide visual feedback for active filters with individual removal capability.

## Technical Context

<!--
  ACTION REQUIRED: Replace the content in this section with the technical details
  for the project. The structure here is presented in advisory capacity to guide
  the iteration process.
-->

**Language/Version**: .NET 10.0 (net10.0-android, net10.0-ios, net10.0-maccatalyst, net10.0-windows10.0.19041.0)  
**Primary Dependencies**: .NET MAUI 10.0.20+, MauiReactor, SQLite, Microsoft.Extensions.AI, ElevenLabs SDK  
**Storage**: SQLite local database with optional CoreSync synchronization  
**Testing**: xUnit for unit tests, platform-specific testing for UI  
**Target Platform**: iOS 12.2+, Android API 21+, macOS 15.0+, Windows 10.0.17763.0+  
**Project Type**: Multi-platform MAUI application (mobile + desktop)  
**Performance Goals**: <3s startup, <100ms UI response, <500ms AI API calls with loading indicators  
**Constraints**: Must work offline, all platforms tested, ILogger for production logs, Theme-first styling  
**Scale/Scope**: Single-user language learning app with custom curriculum support

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

Verify alignment with SentenceStudio Constitution (`.specify/memory/constitution.md`):

- [x] **User-Centric AI-Powered Learning**: Improves user workflow for finding vocabulary - supports custom curriculum by making large vocabulary sets more discoverable
- [x] **Cross-Platform Native**: Pure MAUI UI components (Entry, CollectionView) work identically across all platforms
- [x] **MauiReactor MVU**: All UI uses semantic methods (`.HStart()`, `.VCenter()`, etc.)
- [x] **Theme-First UI**: Styling uses `.ThemeKey()` or MyTheme constants
- [x] **Localization by Default**: All strings use `$"{_localize["Key"]}"` pattern  
- [x] **Observability**: `ILogger<VocabularyManagementPage>` already injected
- [x] **Documentation in docs/**: Spec is in `specs/002-vocabulary-search-syntax/`

**Violations requiring justification**: None

## Project Structure

### Documentation (this feature)

```text
docs/specs/[###-feature]/
├── spec.md              # Feature specification
├── plan.md              # This file (/speckit.plan command output)
├── research.md          # Phase 0 output (/speckit.plan command)
├── data-model.md        # Phase 1 output (/speckit.plan command)
├── quickstart.md        # Phase 1 output (/speckit.plan command)
├── contracts/           # Phase 1 output (/speckit.plan command)
└── tasks.md             # Phase 2 output (/speckit.tasks command - NOT created by /speckit.plan)
```

### Source Code (repository root)

```text
src/
├── SentenceStudio/              # Main MAUI application
│   ├── Pages/                   # MauiReactor page components
│   ├── Services/                # Business logic and AI integration
│   ├── Data/                    # SQLite repositories and models
│   ├── Models/                  # Domain models and DTOs
│   ├── Resources/               # Assets, localization, themes
│   │   ├── Strings/             # Resources.resx, Resources.ko.resx
│   │   └── Styles/              # MyTheme.cs
│   └── Platforms/               # Platform-specific code
├── SentenceStudio.Shared/       # Shared models and utilities
└── SentenceStudio.ServiceDefaults/ # Service configuration

tests/
├── SentenceStudio.Tests/        # Unit tests (xUnit)
└── SentenceStudio.UITests/      # Platform-specific UI tests
```

**Structure Decision**: MAUI multi-platform application with shared business logic and platform-specific implementations. All features must work across iOS, Android, macOS, and Windows.

## Complexity Tracking

No constitution violations - all principles followed.
