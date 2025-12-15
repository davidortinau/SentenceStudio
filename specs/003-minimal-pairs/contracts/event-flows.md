# Event Flows: Minimal Pairs

**Feature**: 003-minimal-pairs

## Start Session (Focus)

1. User selects a minimal pair
2. User selects mode = Focus
3. App creates `MinimalPairSession`
4. App selects the first prompt (random side of selected pair)
5. App plays audio and renders two choices

## Start Session (Mixed)

1. User selects mode = Mixed and a set of pairs
2. App creates `MinimalPairSession`
3. App runs warm-up on the selected focus pair (first 5 trials)
4. App selects next pair using error-weighted selection

## Record Attempt

1. User answers
2. App computes `IsCorrect`
3. App persists `MinimalPairAttempt`
4. App updates in-memory counters immediately

## End Session

1. User ends session (or reaches planned trial count)
2. App marks session ended
3. App shows summary: correct/incorrect/accuracy/duration
