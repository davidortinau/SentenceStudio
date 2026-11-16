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
| 4 | The model asked a clarifying question after its clarifying questions were used up. Since the change described under "The clarification budget" this ending shows its own notice, not the sentence above. | `ReduceClarificationAsync` | `clarification_requested` | warning: `the model asked a clarifying question after N of the M clarification(s) allowed in a row were used` |
| 5 | The model's output could not be read as a turn intent. (A timeout, a cancel or the output limit ends in a different notice.) | `IncompleteAsync`; logged in `Agents/CoachAgentTurnRunner.cs` | `validation_failed` | warning: `the model answer did not match the turn-intent schema. FinishReason=...` |

## Telling them apart

- **The report's stop reason** (`scripts/admin-api.sh sam-show <reportId>`, `report.stopReason`)
  separates ending 4 from the other four: a flag whose `responseKind` is `notice` and whose
  report says `clarification_requested` is ending 4. (A question Sam did ask has the same stop
  reason, with `responseKind` `clarification`.) Until the fix that came with this document the
  report never had a stop reason: it looked for it at the root of the stored turn outcome, and
  the response has been inside the outcome's `answer` section since outcome version 2. Every
  report filed before that fix has an empty stop reason, issue 227's included.
- **The log line** separates the rest, and its codes say which rule. Ending 4 logged nothing
  before the same fix. The logs are in production's Application Insights, so reading them is the
  Captain's (no Azure commands without his go).

## The clarification budget (ending 4)

**As it was when issue 227 was filed** (observed in code and by test):

- `CoachConstraintLimits.MaxClarificationsPerSession` is 2. `CoachSession.ClarificationCount`
  only went up; nothing reset it. A session lives on a 24-hour sliding expiry, so a
  conversation that is used every day kept its count.
- Every clarifying question counts, including the ones the application asks itself (for example
  "Do you want information about Today's Plan, or do you want to propose a change to it?").
- Once two had been asked, any turn where the model asked a question ended in the safe-failure
  notice. The question was withheld. "Saying it a little differently" did not help, because the
  count did not come back.
- The model was told `Clarifying questions remaining: 0` and was not told what to do at zero.

The cap was set when Sam only negotiated Today's Plan and a session was one short exchange. Sam
is now a general assistant in a long conversation (`docs/coach/sam-single-runtime.md`).

**As changed on the same branch** (`CoachSessionStateTransitionTests`, `CoachPromptFenceTests`):

- The cap is on questions asked **in a row**. A turn that ends in a reply (any turn recorded
  with no stop reason) sets the count back to zero, in `RecordTurnOutcomeAsync`. A refused or
  cut-off turn leaves it, with one exception found in review: a vocabulary review whose word
  set is refused for naming a due word (ending 3 through `AttachVocabularySetProposalAsync`)
  has already been recorded as a reply by then, so it starts the count again. No column changed: `ClarificationCount` now holds the run, not the
  session's total.
- At zero the turn context reads `Clarifying questions remaining: 0 (do not use
  AskClarification on this turn)`, and the instructions say what to do instead: no question and
  no guessed change; a plain reply saying what Sam understood and the one detail it needs, or
  what it cannot do and the screen that does it.
- If the model asks anyway, the question is still withheld, but the notice is its own
  (`CoachDeterministicCopy.ClarificationLimitReached`): "I still can't tell what you'd like, so
  nothing was changed. Tell me the one thing you want me to do, as plainly as you can." Ending 4 therefore no longer
  shows the sentence at the top of this document; the other four still do.

The cap, what Sam is told at zero and the wording of the notice change what Sam says to a
learner. They are the Captain's to approve, which he does by merging the pull request that
carries them; the Learning Value Gate was read and its matrix does not apply, because no
learning activity's prompt, response, mode or default changes.

Not changed: the clients show a card reading "Choose one option." (`Coach_ClarificationLimit`)
whenever a turn stops for clarification with none remaining, including when there is no open
suggestion to choose about. That card was written for the plan negotiation. It is in the web,
iOS and Android clients and is left for the Captain to decide.

## Creating a plan

Sam cannot create or regenerate Today's Plan: there is no tool or write handler for it
(`docs/coach/sam-capability-gap.md`, "Today's Plan and dashboard", and item 9 of its list).
`CoachWriteAuthority` reads "create a new plan for today" as a mention of the plan with no
supported action ("create", "generate" and "new" are not action words), so the model gets the
plan's constraints and nothing it can do with them. The instructions told it to ask a question
when a plan request does not fit the fixed constraint fields, which led to ending 4 once the
budget was spent.

The instructions now name this request ("Making a new plan" in `CoachInstructions`): no
question and no plan kind; a plain reply that Sam cannot make a new plan yet, that Regenerate
Plan in the dashboard's menu does, and that Sam can change the current plan when told what to
change. `CoachSessionStateTransitionTests.APlainReply_ToARequestForANewPlan_IsShown` holds the
server's side: such a reply is shown and nothing about the request refuses it. What the model
does with the instruction is checked by asking a running Sam, not by a test.

Giving Sam the ability to regenerate the plan is the real answer and is not in this change: it
needs a `DailyPlan` write handler and a proposal the learner approves.
