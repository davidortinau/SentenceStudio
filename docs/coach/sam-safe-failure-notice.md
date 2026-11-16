# Sam's safe-failure notice: what shows it and how to tell which

Written 2026-11-16 while working issue 227, where a learner asked Sam to create a new plan for
today and got this notice with no reason:

> I couldn't finish that reply safely, so nothing was changed. Please ask again — saying it a
> little differently often helps.

It is `CoachDeterministicCopy.ValidationFailedNeutral`
(`src/SentenceStudio.Api/Coach/Application/CoachDeterministicCopy.cs`). The learner sees one
sentence; the server has five different reasons for it.

## The five endings

All are in `src/SentenceStudio.Api/Coach/Application/CoachSessionService.cs` unless noted.

| # | What happened | Where | Stop reason | Log line (`[Coach] Session {id}: ...`) |
|---|---|---|---|---|
| 1 | The model's reply failed a shape or grounding rule (too long, a stray plan change on a general reply, evidence cited with no read) and its kind is not a plan kind. A plan kind shows "Today's Plan is unchanged." instead. | `ReduceAgentResultAsync` | `validation_failed` | warning: `the intent failed N validation rule(s); codes: [kind:code, ...]` |
| 2 | A plan preview named a resource the learner does not own. | `RejectUnownedPreviewAsync` | `validation_failed` | error: `a preview failed the ownership check` |
| 3 | The reply repeated a word that is due for review. | `BuildAnswerLeakResponseAsync` | `validation_failed` | warning: `the answer repeated N embargoed item(s)` |
| 4 | The model asked a clarifying question after the session's clarifying questions were used up. | `ReduceClarificationAsync` | `clarification_requested` | warning: `the model asked a clarifying question after the session's N clarification(s) were used` |
| 5 | The model's output could not be read as a turn intent. | `IncompleteAsync`; logged in `Agents/CoachAgentTurnRunner.cs` | `validation_failed` | warning: `the model answer did not match the turn-intent schema. FinishReason=...` |

## Telling them apart

- **The report's stop reason** (`scripts/admin-api.sh sam-show <reportId>`, `report.stopReason`)
  separates ending 4 from the other four. Until the fix that came with this document the report
  never had one: it looked for the stop reason at the root of the stored turn outcome, and the
  response has been inside the outcome's `answer` section since outcome version 2. Every report
  filed before that fix has an empty stop reason, issue 227's included.
- **The log line** separates the rest, and its codes say which rule. Ending 4 logged nothing
  before the same fix. The logs are in production's Application Insights, so reading them is the
  Captain's (no Azure commands without his go).

## The clarification budget (ending 4)

Observed in code and by test (`CoachSessionStateTransitionTests.TheClarificationCap_*`,
`CoachResponseReportStopReasonTests`):

- `CoachConstraintLimits.MaxClarificationsPerSession` is 2. `CoachSession.ClarificationCount`
  only goes up; nothing resets it. A session lives on a 24-hour sliding expiry, so a
  conversation that is used every day keeps its count.
- Every clarifying question counts, including the ones the application asks itself (for example
  "Do you want information about Today's Plan, or do you want to propose a change to it?").
- Once two have been asked, any turn where the model asks a question ends in the notice above.
  The question is withheld. "Saying it a little differently" does not help, because the count
  does not come back.
- The model is told `Clarifying questions remaining: 0` and is not told what to do at zero.

The cap was set when Sam only negotiated Today's Plan and a session was one short exchange. Sam
is now a general assistant in a long conversation (`docs/coach/sam-single-runtime.md`). What the
cap should be, and what Sam says when it is reached, changes what Sam says to a learner, so it
is the Captain's decision and goes through the Learning Value Gate.

## Creating a plan

Sam cannot create or regenerate Today's Plan: there is no tool or write handler for it
(`docs/coach/sam-capability-gap.md`, "Today's Plan and dashboard"). `CoachWriteAuthority`
reads "create a new plan for today" as a mention of the plan with no supported action
("create", "generate" and "new" are not action words), so the model gets the plan's constraints
and nothing it can do with them. The instructions tell it to ask a question when a plan request
does not fit the fixed constraint fields, which leads to ending 4 once the budget is spent.
