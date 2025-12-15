# Data Model: Minimal Pairs Listening Activity

**Feature**: 003-minimal-pairs  
**Date**: 2025-12-14

This feature adds persistent minimal-pair definitions and attempt/session tracking to support per-pair history and success rate.

## Entities

### 1) MinimalPair

Represents a defined minimal pair linking exactly two vocabulary words.

**Table**: `MinimalPair` (singular table naming to align with CoreSync conventions)

**Fields**
- `Id` (int, PK)
- `UserId` (int, default 1)
- `VocabularyWordAId` (int, FK → `VocabularyWord.Id`)
- `VocabularyWordBId` (int, FK → `VocabularyWord.Id`)
- `ContrastLabel` (string?, optional; e.g. "ㅅ vs ㅆ")
- `CreatedAt` (DateTime)
- `UpdatedAt` (DateTime)

**Validation / invariants**
- A and B must be different
- Store in normalized order (e.g., ensure `VocabularyWordAId < VocabularyWordBId`) so duplicates are prevented

**Indexes / constraints**
- Unique index: `(UserId, VocabularyWordAId, VocabularyWordBId)`

---

### 2) MinimalPairSession

Represents one session/run of Minimal Pairs practice.

**Table**: `MinimalPairSession`

**Fields**
- `Id` (int, PK)
- `UserId` (int, default 1)
- `Mode` (string, e.g. `Focus`, `Mixed`)
- `PlannedTrialCount` (int, optional)
- `StartedAt` (DateTime)
- `EndedAt` (DateTime?, null until ended)
- `CreatedAt` (DateTime)
- `UpdatedAt` (DateTime)

**Notes**
- Sessions do not need an explicit relationship to pairs; pair participation is derivable from `MinimalPairAttempt.PairId`.

---

### 3) MinimalPairAttempt

Represents one answered trial.

**Table**: `MinimalPairAttempt`

**Fields**
- `Id` (int, PK)
- `UserId` (int, default 1)
- `SessionId` (int, FK → `MinimalPairSession.Id`)
- `PairId` (int, FK → `MinimalPair.Id`)
- `PromptWordId` (int, FK → `VocabularyWord.Id`) — which side was played (the correct answer)
- `SelectedWordId` (int, FK → `VocabularyWord.Id`) — what the user chose
- `IsCorrect` (bool)
- `SequenceNumber` (int) — trial number within the session
- `CreatedAt` (DateTime)

**Indexes**
- `(PairId, CreatedAt)` for per-pair history queries
- `(SessionId, SequenceNumber)` for session reconstruction

## Relationships

- `MinimalPair` ↔ `VocabularyWord` (two FKs)
- `MinimalPairAttempt` → `MinimalPairSession` (many attempts per session)
- `MinimalPairAttempt` → `MinimalPair` (many attempts per pair)

## EF Core Integration

Planned changes (implementation phase):
- Add entity classes under `src/SentenceStudio.Shared/Models/`.
- Extend `ApplicationDbContext` in `src/SentenceStudio.Shared/Data/ApplicationDbContext.cs`:
  - Register table names in `OnModelCreating` with `.ToTable("MinimalPair")`, etc.
  - Add `DbSet<MinimalPair>`, `DbSet<MinimalPairSession>`, `DbSet<MinimalPairAttempt>`
  - Add uniqueness and indexes
- Add an EF Core migration (e.g., `AddMinimalPairs`) under `src/SentenceStudio.Shared/Migrations/`.

## Derived Metrics

Computed (not stored) from attempts:
- Per-pair totals: `Correct`, `Incorrect`, `Accuracy`
- Per-session summary: totals and accuracy
- Simple “recent trend”: last N sessions or last X days (group attempts by session)
