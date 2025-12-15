
# Implementation Plan: Minimal Pairs Listening Activity

**Branch**: `003-minimal-pairs` | **Date**: 2025-12-14 | **Spec**: `specs/003-minimal-pairs/spec.md`
**Input**: Feature specification from `specs/003-minimal-pairs/spec.md`

This plan follows the SpecKit `/speckit.plan` workflow and stops after Phase 2 planning.

## Summary

Add a Minimal Pairs listening activity (initial scope: Korean) that plays a short word audio prompt and asks the user to choose between exactly two confusable options, with immediate correctness feedback and an end-of-session summary. Persist per-pair history and accuracy over time. Also centralize speech voice preference in Settings and remove the Vocabulary Quiz in-activity preferences sheet (migrating its settings into Settings and making global voice the default for relevant pages).

## Technical Context

**Language/Version**: .NET 10.0 (net10.0-android, net10.0-ios, net10.0-maccatalyst, net10.0-windows10.0.19041.0)

**App Architecture**:
- UI: MauiReactor (Reactor.Maui) MVU
- Persistence: EF Core + SQLite (DbContext in `src/SentenceStudio.Shared/Data/ApplicationDbContext.cs`)
- Audio playback: `Plugin.Maui.Audio` (`IAudioPlayer`)
- TTS: ElevenLabs service + existing stream history cache (`StreamHistoryRepository`)
- Localization: `LocalizationManager` + RESX resource keys

**Constraints**:
- Cross-platform (iOS/Android/macOS/Windows)
- Offline-safe behavior: play cached audio when available; otherwise show a clear availability error
- Theme-first styling and centralized icons
- Use `ILogger<T>` for production logs

**NEEDS CLARIFICATION**: None (resolved in `research.md`).

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

Verify alignment with SentenceStudio Constitution (`.specify/memory/constitution.md`):

- [x] **User-Centric AI-Powered Learning**: Supports personalized learning by building minimal pairs from user vocabulary.
- [x] **Cross-Platform Native**: Uses existing MAUI + Plugin.Maui.Audio patterns.
- [x] **MauiReactor MVU**: UI will use semantic layout methods and avoid prohibited patterns.
- [x] **Theme-First UI**: Uses `.ThemeKey()` and theme constants; no hardcoded sizes/colors.
- [x] **Localization by Default**: All user strings use `$"{_localize["Key"]}"`.
- [x] **Observability**: Use `ILogger<T>` in new services.
- [x] **Documentation in docs/**: SpecKit artifacts in `specs/` per established workflow; public docs in `docs/`.

**No constitution violations.**

## Project Structure

### Documentation (this feature)

```text
specs/003-minimal-pairs/
├── spec.md
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
└── contracts/
   ├── service-contracts.md
   ├── ui-contracts.md
   └── event-flows.md
```

### Source Code (repository root)

```text
src/
├── SentenceStudio/                    # Main MAUI application
│   ├── Pages/                         # MauiReactor page components
│   ├── Services/                      # Business logic and audio/TTS integration
│   ├── Resources/Strings/             # AppResources.resx, AppResources.ko-KR.resx
│   └── Resources/Styles/              # MyTheme.cs + ApplicationTheme.Icons.cs
└── SentenceStudio.Shared/
   ├── Data/ApplicationDbContext.cs
   ├── Models/
   └── Migrations/

tests/
└── SentenceStudio.Tests/              # Unit tests (xUnit)
```

## Complexity Tracking

No complexity violations requiring justification.

---

## Phase 0: Outline & Research (Complete)

### Research tasks executed

1. Practice sequencing defaults (blocked warm-up → mixed/adaptive)
2. Audio playback/caching pattern alignment with existing app flows
3. Global voice preference + migration strategy
4. Persistence model for per-pair stats and history

### Output

- `specs/003-minimal-pairs/research.md`

---

## Phase 1: Design & Contracts (Complete)

### Data model

- `specs/003-minimal-pairs/data-model.md`

### Contracts

- `specs/003-minimal-pairs/contracts/service-contracts.md`
- `specs/003-minimal-pairs/contracts/ui-contracts.md`
- `specs/003-minimal-pairs/contracts/event-flows.md`

### Quickstart

- `specs/003-minimal-pairs/quickstart.md`

### Post-design constitution check

- UI remains MVU, theme-first, localized.
- Global settings migration aligns with user-centric experience.
- Documentation location remains the only noted deviation.

---

## Phase 2: Implementation Plan (Planned)

This phase is the actionable sequence for implementation (code changes are **not** performed by `/speckit.plan`).

### 2.1 Global voice preference service + Settings UI

1. Add a global speech voice preference service (backed by `Preferences`) that exposes `VoiceId`.
2. Add migration logic:
  - If global voice not set and legacy VocabularyQuiz voice exists, copy it to global.
3. Update Settings UI to include:
  - Global voice selection UI
  - Vocabulary Quiz preference configuration UI (migrated from in-quiz sheet)
4. Remove the Vocabulary Quiz preferences bottom sheet UI and its entry points.

**Touch points (expected)**:
- `src/SentenceStudio/Pages/AppSettings/SettingsPage.cs`
- `src/SentenceStudio/Services/VocabularyQuizPreferences.cs` (may remain for non-voice settings, but voice should be sourced from the global preference)
- `src/SentenceStudio/Pages/VocabularyQuiz/VocabularyQuizPage.cs`

### 2.2 Update existing pages to use global voice defaults

1. Vocabulary Quiz: use global voice for audio generation.
2. Edit Vocabulary Word: default to global voice for generated word audio.
3. HowDoYouSay: keep in-page voice selection UI, but initialize default from global voice.

**Touch points (expected)**:
- `src/SentenceStudio/Pages/HowDoYouSay/HowDoYouSayPage.cs`
- Edit vocabulary page(s) where audio generation occurs

### 2.3 Minimal pairs persistence (EF Core)

1. Add models:
  - `MinimalPair`
  - `MinimalPairSession`
  - `MinimalPairAttempt`
2. Update `ApplicationDbContext`:
  - Add `DbSet<>`s
  - Configure table names, indexes, unique constraint `(UserId, VocabularyWordAId, VocabularyWordBId)`
3. Add EF Core migration (e.g., `AddMinimalPairs`).
4. Add repositories/services:
  - Pair CRUD + stats queries
  - Session creation/end + attempt recording

### 2.4 Minimal pairs UI (MauiReactor)

1. Create pages:
  - MinimalPairs landing/list page
  - Pair details page (history + stats)
  - Session page (trial loop + summary)
2. Ensure the session page:
  - Plays prompt audio and supports replay without advancing
  - Accepts exactly one answer per prompt (debounced)
  - Shows immediate feedback (not text-color-dependent)
  - Updates counters immediately
  - Properly disposes audio player on leave
3. Implement selection logic:
  - Focus mode: chosen pair only
  - Mixed mode: warm-up first, then error-weighted sampling

### 2.5 Navigation + home entry

1. Add "Minimal Pairs" to dashboard "Choose My Own" activities.
2. Register routes in `MauiProgram.RegisterRoutes()`.

### 2.6 Localization + icons + styling

1. Add required localization keys in both resource files.
2. Add any required icons to `ApplicationTheme.Icons.cs` (no inline `FontImageSource`).
3. Use theme keys and theme constants (no hardcoded colors/sizes).

### 2.7 Testing & validation

1. Add unit tests for:
  - Pair normalization (ordering, uniqueness)
  - Stats aggregation from attempts
  - Sampler weighting logic
2. Manual cross-platform validation checklist:
  - Audio playback behavior matches existing pages
  - Offline behavior works as specified
  - Settings migration does not break existing quiz behavior

---

## Stop & Report

**Branch**: `003-minimal-pairs`

**Implementation plan**: `specs/003-minimal-pairs/plan.md`

**Generated artifacts**:
- `specs/003-minimal-pairs/research.md`
- `specs/003-minimal-pairs/data-model.md`
- `specs/003-minimal-pairs/quickstart.md`
- `specs/003-minimal-pairs/contracts/service-contracts.md`
- `specs/003-minimal-pairs/contracts/ui-contracts.md`
- `specs/003-minimal-pairs/contracts/event-flows.md`
