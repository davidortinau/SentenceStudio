# Sam: one runtime, general tutor and app assistant

**Decision:** Captain, 2026-11-07.
**Replaces:** `docs/coach/legacy-runtime-freeze.md` and
`tests/SentenceStudio.Api.Tests/Coach/Architecture/LegacyCoachGrowthFreezeTests.cs`, added by a
Copilot session in commit `5e590e30` (2026-09-26) with no recorded product decision.

## Decision

> "We certainly don't need multiple runtimes for Sam. There doesn't need to be any side by side
> development. We need to get Sam working correctly." — Captain, 2026-11-07

- Sam has one runtime: the Coach runtime in `src/SentenceStudio.Api/Coach/`. It grows. New
  abilities are added as tools in `CoachToolRegistry`, with write abilities as approve-first
  `propose_` tools and handlers.
- Sam is a language tutor and an assistant for the whole app: anything the learner can do in the
  app, Sam should be able to do from natural language. Today's Plan is one ability among many,
  never the default destination.
- The vocabulary review built on the AppOperation ledger
  (`CoachVocabularySetApplicationService`) keeps working as it is. It is part of the one runtime,
  not a separate "replacement".
- `docs/coach/coach-product-policy-v1.md` stays as reference for behavior (approval before any
  effect, receipts before claiming success, owner scope, no due-answer leaks). Its "clean
  architecture" framing no longer applies.

## Why the freeze was removed

The freeze test failed on any new tool, write handler, capability, intent member, or Coach route.
Every attempt to broaden Sam away from Today's Plan therefore had nowhere to add abilities, which
matches the Captain's experience that Sam "has never behaved as I expected it to".

## What still protects Sam

The safety properties the freeze sat beside are enforced by their own tests and remain in place:
write tools only propose (`SamWriteProposalTool`, write handler catalog validation), the output
embargo scan (`CoachOutputContract`), owner scoping, the due-item leak validator, and
`CoachIntentKind` ordinal stability (persisted in `CoachPlanRevision.IntentKind`; append only,
never reorder).

## Way forward (Captain-approved order, 2026-11-07)

1. Lift the freeze (this document).
2. Finish feedback filing through Sam (`propose_feedback`).
3. Inventory: everything the learner can do in the app vs Sam's tools, as a ranked gap table.
4. Reshape the turn: a general answer/result reply, no plan data on every turn, shorter plan rules.
5. Add abilities in the Captain's ranked order, with tests.
6. Qualify with a corpus of real phrasings run against the live model.
