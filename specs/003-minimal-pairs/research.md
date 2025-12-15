# Research: Minimal Pairs Listening Activity

**Feature**: 003-minimal-pairs  
**Date**: 2025-12-14

This document resolves open technical/design questions from the feature spec and records key decisions.

## 1) Practice Sequencing (Warm-up → Mixed/Adaptive)

**Decision**: Default sessions start with a short warm-up (blocked practice on the selected pair) and then transition to mixed/adaptive practice.

- Warm-up: first 5 trials use the chosen pair (focus pair)
- Mixed/adaptive: subsequent trials select pairs with a simple error-weighted sampler

**Rationale**:
- Blocked practice reduces task confusion early (especially for near-homophones)
- Interleaving improves discrimination and generalization once the learner understands the task
- A lightweight weighting function is easy to implement and test

**Alternatives considered**:
- Pure blocked practice: simpler but weaker generalization
- Full Bayesian/IRT learner model: better personalization but overkill for MVP

## 2) Audio Playback + Caching Pattern

**Decision**: Reuse the existing app pattern used by Vocabulary Quiz and Edit Vocabulary Word:
- Generate audio via `ElevenLabsSpeechService.TextToSpeechAsync()`
- Cache and reuse audio via `StreamHistoryRepository.GetStreamHistoryByPhraseAndVoiceAsync()`
- Play audio via `Plugin.Maui.Audio` (`IAudioPlayer`)
- Track playback state with a boolean in component state and reflect it in the play/replay button icon/state

**Rationale**:
- Matches requirement FR-016: consistent play/replay experience across audio-enabled pages
- Uses existing cache mechanism to reduce API calls and latency
- Cross-platform compatible and already proven in this codebase

**Alternatives considered**:
- Add a new audio cache store specifically for minimal pairs: duplicates existing history/caching
- Use `GenerateTimestampedAudioAsync`: great for long transcripts; unnecessary for single-word clips

## 3) Global Voice Preference (Settings) + Migration

**Decision**:
- Introduce a small “global speech voice preference” service (backed by MAUI `Preferences`) that stores `VoiceId`.
- Use this global voice preference as the default for:
  - Minimal Pairs audio generation
  - Edit Vocabulary Word audio generation
  - Vocabulary Quiz audio generation
- Keep "How do you say" voice selection UI unchanged, but initialize its default voice to the global voice preference.

**Migration approach**:
- One-time migration: if a legacy vocab-quiz voice preference exists and the global voice is not set yet, copy it to global.

**Rationale**:
- Meets FR-015 / FR-017 / FR-026 with minimal impact
- Centralizes the choice while preserving existing per-page voice pickers where explicitly required

**Alternatives considered**:
- Store voice in `UserProfile` (SQLite) for sync: heavier than needed for MVP, and existing preferences already use `Preferences`

## 4) Home Entry Point

**Decision**: Add a "Minimal Pairs" entry in the Dashboard "Choose My Own" activities list.

**Rationale**:
- Meets FR-023 with minimal surface area
- Reuses the existing navigation + selection model (`ActivityProps`)

**Alternatives considered**:
- Add to Today’s Plan generation: desirable later, but not required by the current spec

## 5) Persistence Model for Minimal Pairs + Stats

**Decision**: Persist Minimal Pairs and attempts in SQLite via EF Core (same `ApplicationDbContext` as the rest of the app).

- `MinimalPair`: defines the pair (links two `VocabularyWord` records)
- `MinimalPairSession`: one run/session of the activity
- `MinimalPairAttempt`: one trial within a session, storing correctness and chosen/correct word ids

**Rationale**:
- Meets FR-008 / FR-021 / FR-022
- Aligns with existing repository + EF Core patterns

**Alternatives considered**:
- Store attempts in `UserActivity`: not expressive enough for per-pair analytics and history

## 6) Resource/Skill Selection in "Choose My Own" Mode

**Decision**: Minimal Pairs pages will accept `ActivityProps` so the feature can use selected resources/skill context when available.

**Rationale**:
- Keeps navigation consistent with existing dashboard gating
- Enables filtering vocabulary/pairs by selected learning resources (custom curriculum support)

**Alternatives considered**:
- Bypass `ActivityProps` entirely: would require a new navigation pattern and separate dashboard tile type
