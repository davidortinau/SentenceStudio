# Contracts: Minimal Pairs (Services + Repositories)

**Feature**: 003-minimal-pairs

This document defines the service/repository surface needed to implement the feature.

## Repository Contracts

### MinimalPairRepository

Responsibilities:
- CRUD for minimal pair definitions
- Provide per-pair aggregates (correct/incorrect/accuracy)

Proposed methods:
- `Task<List<MinimalPair>> GetAllAsync(int userId = 1)`
- `Task<MinimalPair?> GetAsync(int id, int userId = 1)`
- `Task<MinimalPair> CreateAsync(int wordAId, int wordBId, string? contrastLabel, int userId = 1)`
- `Task DeleteAsync(int minimalPairId, int userId = 1)`
- `Task<MinimalPairStats> GetStatsAsync(int minimalPairId, int userId = 1)`

### MinimalPairSessionRepository

Responsibilities:
- Create/close sessions
- Record attempts
- Query session summaries and per-pair history

Proposed methods:
- `Task<MinimalPairSession> StartSessionAsync(string mode, int plannedTrialCount, int userId = 1)`
- `Task EndSessionAsync(int sessionId, DateTime endedAt, int userId = 1)`
- `Task RecordAttemptAsync(MinimalPairAttempt attempt, int userId = 1)`
- `Task<List<MinimalPairSessionSummary>> GetHistoryForPairAsync(int minimalPairId, int maxSessions = 20, int userId = 1)`

## Audio + Voice Preference Contracts

### SpeechVoicePreferences

Responsibilities:
- Provide a global default voice id for learning activities

Proposed members:
- `string VoiceId { get; set; }`
- `void MigrateFromLegacyVocabQuizVoiceIfNeeded()`

## UI Event Flows

### Trial Answer

1. Session page plays prompt audio (word A or word B)
2. User taps one of two choices
3. UI immediately displays correct/incorrect feedback
4. Attempt is persisted
5. Counters update (correct/incorrect/accuracy)

### End Session

1. User taps End
2. Session is marked ended
3. Summary overlay/page shows totals + accuracy + duration
