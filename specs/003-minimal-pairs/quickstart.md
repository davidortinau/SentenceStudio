# Quickstart: Minimal Pairs Listening Activity

**Feature**: 003-minimal-pairs  
**Audience**: Implementer / reviewer

## Goal

Add a Minimal Pairs listening activity that:
- Plays a short audio prompt (single word)
- Shows exactly 2 answer choices
- Gives immediate correctness feedback
- Tracks right/wrong during the session and shows an end summary
- Persists per-pair history and success rate
- Uses global voice preference from Settings

## Prerequisites

- Build using a TFM (example): `dotnet build -f net10.0-maccatalyst`  
- Run using: `dotnet build -t:Run -f net10.0-maccatalyst`

## Implementation Order (recommended)

### 1) Global voice preference
- Add a small preferences service (backed by MAUI `Preferences`) to store a global `VoiceId`.
- Update:
  - Vocabulary Quiz audio generation to use global voice
  - Edit Vocabulary Word audio generation to use global voice by default
  - HowDoYouSay default `SelectedVoiceId` to global voice (keep voice picker UI)

### 2) Settings UI
- Update Settings page to include:
  - Global voice selection
  - Vocabulary Quiz settings section (migrated from in-quiz bottom sheet)
- Remove the Vocabulary Quiz preferences bottom sheet UI and its open/close entry points.

### 3) Data model + repository
- Add `MinimalPair`, `MinimalPairSession`, `MinimalPairAttempt` models and migration.
- Add a repository/service that supports:
  - Create/delete pairs
  - List pairs (optionally filtered by selected resources)
  - Record attempts
  - Query per-pair stats and history

### 4) Minimal Pairs UI
- Add pages:
  - Pair list + start session
  - Pair details (history + stats)
  - Session page (trial loop)
- Ensure:
  - Play/replay uses existing audio pattern + caching
  - Running counters update immediately
  - Feedback does not rely on text color alone (use icons/backgrounds)

### 5) Home entry point
- Add "Minimal Pairs" to Dashboard "Choose My Own" activity list.
- Register the new page route(s) in `MauiProgram.RegisterRoutes()`.

## Testing Checklist

- Manual smoke tests:
  - Start session and answer/replay rapidly (no double-count)
  - End session mid-way (navigation/back) and verify cleanup
  - Offline with cached audio: playback works
  - Offline without cached audio: clear error message
- Platform build checks:
  - `net10.0-maccatalyst`, `net10.0-ios`, `net10.0-android`, `net10.0-windows10.0.19041.0`

## Done When

- User can create a minimal pair from two vocabulary words
- User can practice focus and mixed sessions with immediate feedback
- Session summary and per-pair history are visible and persisted
- Voice selection is consistent via Settings (except explicit per-page pickers like HowDoYouSay)
