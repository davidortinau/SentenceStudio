# Vocabulary Quiz Round-Based Session Structure

## Overview

The VocabularyQuizPage has been restructured to use a round-based session model for more predictable and structured learning.

## Session Structure

### Constants
- **ActiveWordCount**: 10 words active at any time
- **TurnsPerRound**: 10 turns per round (1 turn per active word)

### Round Flow

1. **Round Start**: All active words are shuffled into a randomized order (`RoundWordOrder`)
2. **Turn Progression**: Each turn presents the next word in `RoundWordOrder` order
3. **Word Mastery**: When a word is mastered (both Recognition and Production phases complete), it's removed and a replacement is **queued** for the next round (not added mid-round)
4. **Round End**: When `CurrentTurnInRound >= RoundWordOrder.Count`, a new round starts
5. **New Round**: Pending replacement words are added, active words are re-shuffled

### Session End Conditions
- Pool exhausted AND all active words mastered
- No maximum round limit (unlimited duration)
- User can exit at any time

## State Properties

### New Round-Based State
```csharp
// Constants
public const int ActiveWordCount = 10;
public const int TurnsPerRound = 10;

// Round tracking
public int CurrentTurnInRound { get; set; }     // 0-based index into RoundWordOrder
public List<int> RoundWordOrder { get; set; }   // Shuffled indices of VocabularyItems
public List<VocabularyQuizItem> PendingReplacements { get; set; } // Words queued for next round

// Session statistics
public int RoundsCompleted { get; set; }
public int WordsMasteredThisSession { get; set; }
public int TotalTurnsCompleted { get; set; }
```

## Key Methods

### Round Management
- **InitializeFirstRound()**: Called after `LoadVocabulary`, sets up first round with shuffled order
- **StartNewRound()**: Adds pending replacements, shuffles words, increments round counter
- **GetCurrentRoundWord()**: Returns the current word based on round order and turn

### Word Rotation
- **HandleMasteredWordsForNextRound()**: Removes mastered words, queues replacements
- **QueueReplacementWordsForNextRound(count)**: Gets new words from pool, adds to `PendingReplacements`
- **RefreshRoundOrderIfNeeded()**: Updates RoundWordOrder when words are removed mid-round

### Session Flow
- **NextItem()**: Advances turn, handles round completion, gets next word
- **CompleteSession()**: Shows summary when pool exhausted + all mastered

## Session Summary UI

The summary screen now displays round-based statistics:
- **Rounds Completed**: Number of full rounds played
- **Words Mastered**: Total words mastered this session
- **Total Turns**: Total turns taken across all rounds

## Localization Keys Added

### English (AppResources.resx)
- `RoundStarted`: "🎯 Round {0} started!"
- `RoundsCompleted`: "Rounds"
- `WordsMastered`: "Mastered"
- `TotalTurns`: "Turns"
- `SessionProgress`: "Session Progress"

### Korean (AppResources.ko-KR.resx)
- `RoundStarted`: "🎯 라운드 {0} 시작!"
- `RoundsCompleted`: "라운드"
- `WordsMastered`: "마스터"
- `TotalTurns`: "턴"
- `SessionProgress`: "세션 진행 상황"

## Usage Contexts

### From Daily Plan
- Pool is limited to words selected by `DeterministicPlanBuilder`
- `Props.TargetWordCount` may be set (e.g., 20 words)
- Activity timer enabled

### Manual Selection
- Pool includes all vocabulary from selected resources
- Filtered by SRS due dates
- No timer

## Implementation Notes

1. **Word Order Consistency**: Words appear in the same shuffled order throughout a round. Only at round boundaries is the order re-randomized.

2. **Mid-Round Mastery**: When a word is mastered during a round, it's immediately removed from `VocabularyItems`, but its replacement only appears in the next round. This maintains the "10 turns per round" structure.

3. **Pool Exhaustion**: If no replacement words are available when a word is mastered, the active set simply shrinks. Session continues until all remaining words are mastered.

4. **Backward Compatibility**: Legacy methods like `RotateOutMasteredWordsAndAddNew()` and `AddNewTermsToMaintainSet()` now delegate to the new round-based system.
