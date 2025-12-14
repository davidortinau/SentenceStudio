# Vocabulary Quiz Session Management Consolidation

**Date**: December 13, 2025  
**Status**: ✅ Complete

## Overview

Consolidated legacy session management properties with round-based session management to maintain a single, consistent set of rules and properties throughout VocabularyQuizPage.

## Problem

The page maintained two parallel session tracking systems:

**Legacy System** (for progress bar compatibility):
- `CurrentTurn` (1-based turn counter)
- `MaxTurnsPerSession = 20` (conflicted with "unlimited rounds" rule)
- `ActualWordsInSession` (session size tracking)
- `WordsCompleted` (words reviewed counter)
- `IsSessionComplete` (session end flag)

**Round-Based System** (new game rules):
- `CurrentTurnInRound` (0-based index into round order)
- `RoundWordOrder` (10 shuffled items per round)
- `RoundsCompleted`, `WordsMasteredThisSession`, `TotalTurnsCompleted`

This dual system caused:
- Conflicting rules (MaxTurnsPerSession vs. unlimited rounds)
- Confusion about which counter to use
- Session completion logic that didn't respect round structure

## Solution

### 1. Removed Legacy Properties

Deleted from `VocabularyQuizPageState`:
- ❌ `CurrentTurn`
- ❌ `MaxTurnsPerSession` 
- ❌ `ActualWordsInSession`
- ❌ `WordsCompleted`
- ❌ `IsSessionComplete`

### 2. Updated Progress Bar

Changed from showing "words completed / session size" to showing "current turn / turns per round":

**Before**:
```csharp
// Left: WordsCompleted, Right: ActualWordsInSession
Label($"{State.WordsCompleted}")
Label($"{State.ActualWordsInSession}")
```

**After**:
```csharp
// Left: Current turn (1-based for display), Right: Always 10
Label($"{State.CurrentTurnInRound + 1}")
Label($"{VocabularyQuizPageState.TurnsPerRound}")
Progress: (double)State.CurrentTurnInRound / VocabularyQuizPageState.TurnsPerRound
```

### 3. Removed Session Completion Logic

**Before**:
```csharp
if (State.CurrentTurn > effectiveMaxTurns)
{
    await CompleteSession();
    return;
}
```

**After**:
Session ends naturally when pool exhausted + all active words mastered (checked in `StartNewRound()`).

### 4. Updated Turn Tracking

**Before**:
```csharp
SetState(s => s.CurrentTurn++);  // Legacy 1-based counter
```

**After**:
```csharp
SetState(s => 
{
    s.CurrentTurnInRound++;
    s.TotalTurnsCompleted++;
});
```

### 5. Fixed Round Completion Check

**Before** (CRITICAL BUG):
```csharp
if (State.CurrentTurnInRound >= State.RoundWordOrder.Count)  // Could be < 10!
```

**After**:
```csharp
if (State.CurrentTurnInRound >= VocabularyQuizPageState.TurnsPerRound)  // Always 10
```

### 6. Updated Session Goal Tracking

**Before**:
```csharp
var wordGoalMet = State.WordsCompleted >= Props.TargetWordCount.Value;
```

**After**:
```csharp
var wordGoalMet = State.WordsMasteredThisSession >= Props.TargetWordCount.Value;
```

## Final Session Properties

**Round-Based Management** (only system now):
```csharp
// Constants
public const int ActiveWordCount = 10;
public const int TurnsPerRound = 10;

// Round tracking
public int CurrentTurnInRound { get; set; } = 0;
public List<VocabularyQuizItem> RoundWordOrder { get; set; } = new();
public List<VocabularyQuizItem> PendingReplacements { get; set; } = new();

// Session statistics
public int RoundsCompleted { get; set; } = 0;
public int WordsMasteredThisSession { get; set; } = 0;
public int TotalTurnsCompleted { get; set; } = 0;
```

## Game Rules Enforced

✅ **10 active words** at any time (`ActiveWordCount`)  
✅ **10 turns per round** (one per word, `TurnsPerRound`)  
✅ **Unlimited rounds** (no MaxTurnsPerSession cap)  
✅ **Replacements queued** for next round (`PendingReplacements`)  
✅ **Session ends naturally** when pool exhausted + all active words mastered

## Benefits

1. **Single source of truth** for session tracking
2. **Consistent UI** showing round progress (turn X of 10)
3. **No conflicting rules** between legacy and round-based systems
4. **Proper enforcement** of 10-turns-per-round rule
5. **Unlimited rounds** support without artificial caps
6. **Cleaner codebase** with fewer redundant properties

## Testing Checklist

- [ ] Progress bar shows "1" through "10" for each round
- [ ] Round ends after exactly 10 turns
- [ ] New round starts with shuffled word order
- [ ] Mastered words removed and replacements added at round start
- [ ] Session continues until pool exhausted
- [ ] Session statistics (RoundsCompleted, WordsMasteredThisSession) update correctly
- [ ] Daily plan progress tracks correctly
- [ ] Manual resource selection works correctly

## Related Files

- `src/SentenceStudio/Pages/VocabularyQuiz/VocabularyQuizPage.cs` (all changes)

## Next Steps

With consolidated session management in place, next priorities:
1. Fix `StartNewRound()` to always create 10-entry round orders (repeat words if < 10 available)
2. Add constants for hardcoded values (0.50f mastery threshold, 5000ms auto-advance)
3. Test round-based gameplay thoroughly
