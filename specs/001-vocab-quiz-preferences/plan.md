# Implementation Plan: Vocabulary Quiz Preferences

**Branch**: `001-vocab-quiz-preferences` | **Date**: 2025-12-13 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `/specs/001-vocab-quiz-preferences/spec.md`

**Note**: This template is filled in by the `/speckit.plan` command. See `.specify/templates/commands/plan.md` for the execution workflow.

## Summary

Add configurable preferences to VocabularyQuizPage allowing users to:
1. Choose display direction (target→native or native→target language)
2. Enable/disable automatic vocabulary audio playback (target language)
3. Enable/disable automatic sample sentence audio playback (after vocabulary audio)
4. Show/hide mnemonic images on correct answer confirmations

Technical approach: Extend existing UserProfile model with VocabularyQuizPreferences properties, persist via SQLite, integrate with existing Plugin.Maui.Audio playback infrastructure and ElevenLabs TTS service, display preferences UI via SfBottomSheet (following app patterns).

## Technical Context

**Language/Version**: .NET 10.0 (net10.0-android, net10.0-ios, net10.0-maccatalyst, net10.0-windows10.0.19041.0)  
**Primary Dependencies**: .NET MAUI 10.0.20+, MauiReactor, SQLite, Microsoft.Extensions.AI, ElevenLabs SDK, Plugin.Maui.Audio, Syncfusion.Maui.Popup  
**Storage**: SQLite local database (UserProfile table extension)  
**Testing**: xUnit for unit tests, manual testing on all platforms for audio/UI  
**Target Platform**: iOS 12.2+, Android API 21+, macOS 15.0+, Windows 10.0.17763.0+  
**Project Type**: Multi-platform MAUI application (mobile + desktop)  
**Performance Goals**: <100ms preference load, <500ms audio playback start, <1s mnemonic image load  
**Constraints**: Must work offline, all platforms tested, ILogger for production logs, Theme-first styling  
**Scale/Scope**: Single-user preference storage (4 new UserProfile columns), existing audio infrastructure reused

**Key Integration Points**:
- UserProfileRepository: Load/save preferences (no new repository needed)
- Plugin.Maui.Audio: IAudioManager/IAudioPlayer for playback (already integrated)
- ElevenLabsSpeechService: TTS generation with StreamHistoryRepository caching (already integrated)
- VocabularyQuizPage: Extend existing page with preferences UI and audio logic
- ExampleSentenceRepository: Query sample sentences for auto-play (already integrated)

**No New External Dependencies**: All required packages already in project.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

Verify alignment with SentenceStudio Constitution (`.specify/memory/constitution.md`):

- [x] **User-Centric AI-Powered Learning**: Preferences enhance personalized learning by allowing users to customize quiz presentation and audio support
- [x] **Cross-Platform Native**: Feature uses SQLite (works offline), Plugin.Maui.Audio (cross-platform), will be tested on all platforms
- [x] **MauiReactor MVU**: Preferences UI will use MauiReactor with semantic alignment methods (`.HStart()`, `.VCenter()`, etc.)
- [x] **Theme-First UI**: All UI elements will use `.ThemeKey()` or MyTheme constants
- [x] **Localization by Default**: All preference labels and UI strings will use `$"{_localize["Key"]}"` pattern
- [x] **Observability**: Will use `ILogger<T>` for preference loading/saving, audio playback events
- [x] **Documentation in docs/**: All specs are in `docs/specs/001-vocab-quiz-preferences/`

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

## Phase 0-1 Completion Summary

### Phase 0: Research (Completed)

**Artifact**: [research.md](research.md)

**Key Decisions Made**:
1. ✅ Store preferences in UserProfile table (not separate table)
2. ✅ Use Plugin.Maui.Audio PlaybackEnded event for audio chaining
3. ✅ Display preferences via SfBottomSheet overlay (not separate page)
4. ✅ Select sample sentences by IsCore flag + CreatedAt ordering
5. ✅ Handle missing audio/images gracefully (log warning, continue quiz)

**Research Outcomes**:
- All technical unknowns resolved
- Existing service patterns identified and will be reused
- No new external dependencies required
- Cross-platform audio playback patterns documented
- Performance and error handling strategies defined

### Phase 1: Design & Contracts (Completed)

**Artifacts**:
- [data-model.md](data-model.md) - Database schema and entity relationships
- [contracts/service-contracts.md](contracts/service-contracts.md) - Component interfaces and event flows
- [quickstart.md](quickstart.md) - Implementation guide with step-by-step checklist

**Data Model Changes**:
- UserProfile extended with 4 new columns (VocabQuizDisplayDirection, VocabQuizAutoPlayVocabAudio, VocabQuizAutoPlaySampleAudio, VocabQuizShowMnemonicImage)
- Migration: 20251213_AddVocabularyQuizPreferences.cs
- No changes to VocabularyWord, ExampleSentence (existing schema sufficient)

**New Components**:
- VocabularyQuizPreferencesBottomSheet (MauiReactor component)
- VocabularyQuizPage extensions (state fields, audio methods, preference UI integration)
- 12+ new localization keys (English + Korean)

**Service Contracts**:
- UserProfileRepository: Extended behavior (no new methods)
- Audio playback: Plugin.Maui.Audio IAudioPlayer pattern with event chaining
- ExampleSentenceRepository: Existing methods reused with filtering logic

**Ready for Phase 2**: All design artifacts complete, implementation path clear, estimated 3-4 hours development time per quickstart guide.

### Next Steps (Phase 2 - Not in this command scope)

Run `/speckit.tasks` to generate task breakdown for implementation. Then proceed with implementation following quickstart.md checklist.

---

**Phase 0-1 Gate Status**: ✅ PASSED - All research completed, design artifacts generated, ready for task breakdown and implementation.

## Complexity Tracking

> **Fill ONLY if Constitution Check has violations that must be justified**

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| None | N/A | N/A |

**Justification**: This feature fully complies with the SentenceStudio Constitution. No architectural complexity added. Uses existing patterns (UserProfile for preferences, Plugin.Maui.Audio for playback, SfBottomSheet for UI overlay).
