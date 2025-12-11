# Implementation Plan: [FEATURE]

**Branch**: `[###-feature-name]` | **Date**: [DATE] | **Spec**: [link]
**Input**: Feature specification from `/specs/[###-feature-name]/spec.md`

**Note**: This template is filled in by the `/speckit.plan` command. See `.specify/templates/commands/plan.md` for the execution workflow.

## Summary

[Extract from feature spec: primary requirement + technical approach from research]

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

- [ ] **User-Centric AI-Powered Learning**: Does this feature support custom curriculum or AI-powered learning?
- [ ] **Cross-Platform Native**: Will this work on iOS, Android, macOS, and Windows?
- [ ] **MauiReactor MVU**: Does UI use semantic methods (`.HStart()`, `.VCenter()`, etc.) not `HorizontalOptions`?
- [ ] **Theme-First UI**: Does styling use `.ThemeKey()` or theme constants (no hardcoded colors/sizes)?
- [ ] **Localization by Default**: Are all strings using `$"{_localize["Key"]}"` pattern?
- [ ] **Observability**: Is `ILogger<T>` used for production logging?
- [ ] **Documentation in docs/**: Are specs/guides placed in `docs/` folder?

**Violations requiring justification**: [List any principle violations and rationale, or state "None"]

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
