# Implementation Plan: Fuzzy Text Matching for Vocabulary Quiz

**Branch**: `001-fuzzy-text-matching` | **Date**: 2025-12-14 | **Spec**: [spec.md](./spec.md)
**Input**: Feature specification from `/specs/001-fuzzy-text-matching/spec.md`

**Note**: This template is filled in by the `/speckit.plan` command. See `.specify/templates/commands/plan.md` for the execution workflow.

## Summary

Add fuzzy text matching to VocabularyQuizPage text entry mode to accept core words without requiring exact annotation formatting. Users can answer "take" for "take (a photo)", "ding" for "ding~ (a sound)", or "choose" for "to choose" (bidirectional). System normalizes input by extracting core words, trimming whitespace, and handling punctuation/case differences. Implementation uses client-side text processing (offline), works cross-platform, and provides educational feedback showing complete forms when fuzzy matches are accepted.

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

- [x] **User-Centric AI-Powered Learning**: ✅ Improves learning experience by accepting correct answers without formatting barriers
- [x] **Cross-Platform Native**: ✅ Client-side text processing works identically on iOS, Android, macOS, Windows
- [x] **MauiReactor MVU**: ✅ No UI changes required (logic only); existing UI follows semantic methods
- [x] **Theme-First UI**: ✅ Feedback uses existing theme styles; no new styling added
- [x] **Localization by Default**: ✅ Feedback messages use `$"{_localize["Key"]}"` pattern (e.g., "QuizFuzzyMatchCorrect")
- [x] **Observability**: ✅ Matching logic uses `ILogger<VocabularyQuizPage>` for debugging fuzzy match decisions
- [x] **Documentation in docs/**: ✅ Spec already placed in `docs/specs/001-fuzzy-text-matching/`

**Violations requiring justification**: None

## Post-Design Constitution Re-Check

*Re-evaluated after Phase 1 design completion:*

- [x] **User-Centric AI-Powered Learning**: ✅ No conflicts. FuzzyMatcher improves learning by reducing false negatives.
- [x] **Cross-Platform Native**: ✅ Static utility class, no platform-specific code. Works identically on all platforms.
- [x] **MauiReactor MVU**: ✅ No UI changes required. Integration is minimal (2-line change in CheckAnswer()).
- [x] **Theme-First UI**: ✅ Feedback messages use existing localization system. No new styling.
- [x] **Localization by Default**: ✅ One new key (`QuizFuzzyMatchCorrect`) added to both English and Korean resources.
- [x] **Observability**: ✅ ILogger used for debugging fuzzy match decisions. No Debug.WriteLine in production.
- [x] **Documentation in docs/**: ✅ All artifacts in `specs/001-fuzzy-text-matching/` folder.

**Design Complexity**: MINIMAL
- No new database tables
- No new dependencies (uses built-in Regex and string methods)
- No new UI components
- Single static class (FuzzyMatcher.cs)
- Two-line integration change in VocabularyQuizPage.cs

**Result**: ✅ All gates PASSED. Ready for Phase 2 (Tasks).

---

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

> **Fill ONLY if Constitution Check has violations that must be justified**

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| [e.g., 4th project] | [current need] | [why 3 projects insufficient] |
| [e.g., Repository pattern] | [specific problem] | [why direct DB access insufficient] |
