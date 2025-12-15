# Contracts: Minimal Pairs (UI)

**Feature**: 003-minimal-pairs

## Pages

### MinimalPairsPage (Landing)

Responsibilities:
- List existing minimal pairs
- Create new minimal pair from vocabulary
- Choose mode (Focus/Mixed)
- Start a session

Key UI elements:
- Pair list items: show both terms + optional contrast label
- "Create Pair" button
- "Start" button

### MinimalPairDetailsPage

Responsibilities:
- Show per-pair stats (correct/incorrect/accuracy)
- Show recent session history
- Start focus session on this pair

### MinimalPairSessionPage

Responsibilities:
- Play prompt audio
- Show exactly two answer options
- Immediate feedback + running counters
- Replay prompt
- End session and show summary

Constraints:
- Feedback must not rely on text color alone (use icon/background)
- Must clean up audio player on navigation away

## Localization Keys (new)

Expected additions (English + Korean):
- `MinimalPairsTitle`
- `MinimalPairsCreatePair`
- `MinimalPairsStart`
- `MinimalPairsModeFocus`
- `MinimalPairsModeMixed`
- `MinimalPairsReplay`
- `MinimalPairsCorrect`
- `MinimalPairsIncorrect`
- `MinimalPairsSessionSummary`
