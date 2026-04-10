# Vocab Quiz: Sentence Production Shortcut

**Status:** Idea  
**Date logged:** 2026-04-04  
**Activity:** VocabQuiz  

## Summary

Add a secondary action in Vocab Quiz that lets the user demonstrate production-level mastery of a word by writing original sentences using it, graded by AI.

## User Flow

1. During a vocab quiz item (e.g., the target word is "Cloud"), the user taps a secondary button (e.g., "Use in a sentence").
2. The current quiz UI is replaced with a text entry area.
3. The user writes one or more sentences that use the target word. For example: *"The clouds in the sky look like cotton."*
4. If the mastery model requires multiple demonstrations, the user can enter multiple sentences at once — similar to the Writing activity.
5. On submit, AI grades each sentence and the user sees feedback for every sentence provided.

## Grading Rules

- **Pass/fail is determined solely by whether the target word was used correctly.** This is the only criterion that affects the mastery score.
- AI should still provide full-sentence feedback (grammar, naturalness, etc.), but errors outside of the target word usage do **not** count against the user.
- Correct usage of the word counts as a production demonstration toward the app's mastery score, just as answering a quiz question correctly would.

## Design Notes

- The sentence production UI should feel lightweight — remove the quiz chrome and focus on writing.
- After submission, show per-sentence AI feedback clearly, with a visual indicator for pass/fail on the target-word criterion.
- This is conceptually the same as what happens in the Writing activity, scoped to a single word and integrated into the quiz flow.

## Open Questions

- Should the user be able to cancel and return to the normal quiz item without penalty?
- Does each sentence count as one "exposure" toward mastery, or does the whole batch count as one?
- Should there be a minimum sentence length or complexity requirement?
