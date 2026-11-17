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
  cut-off turn leaves it, including a vocabulary review whose word set is refused for naming a
  due word: that turn is recorded only after its set has passed. No column changed: `ClarificationCount` now holds the run, not the
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

The clients' card "Choose one option." (`Coach_ClarificationLimit`) is shown only when an offer
is open to choose about. Without one the last question shows as a question, and a withheld one
leaves the learner ready to type (web `CoachStateMachine`, native `NativeSamPresenter`).

## Creating a plan

**As it was when issue 227 was filed.** Sam could not create or regenerate Today's Plan: there
was no tool or write handler for it. `CoachWriteAuthority` reads "create a new plan for today"
as a mention of the plan with no supported action ("create", "generate" and "new" are not action
words), so the model got the plan's constraints and nothing it could do with them. The
instructions told it to ask a question when a plan request did not fit the fixed constraint
fields, which led to ending 4 once the budget was spent.

**Now.** Sam makes the plan. The learner can already do this in one tap with Regenerate Plan on
the dashboard, and the Captain's direction (2026-11-16) is that Sam acts whenever a suitable
action exists and the learner is permitted to take it:

- `propose_plan_regeneration` (`CoachPlanRegenerationHandler`) is one of Sam's write tools. The
  model calls it with no arguments; the learner sees a card ("Replace today's plan with a new
  one", how much of the current plan is done, that logged practice stays in history, and that
  it cannot be undone) and accepts it in one step. With no plan for today the card offers to
  make one.
- Accepting runs `IPlanService.RegenerateTodayAsync`: the same reset and generate the plans
  endpoints already had, as one unit, so a generation that fails takes the reset back with it.
  When no plan can be built (a learner with nothing to plan from) nothing is offered, and if
  that only shows at the moment of accepting, the current plan is left as it was.
- It is not reversible, as the dashboard's control is not. The replaced plan's rows are
  archived, never deleted.
- The instructions ("Making a new plan", and "Lean toward doing" under DOING THINGS IN THE APP
  FOR THE LEARNER) tell the model to call the tool and not to ask or to explain how the learner
  could do it themselves. Where the write tools are switched off Sam says plainly that it
  cannot and names Regenerate Plan.
- When the change lands the clients read the plan again: the web dashboard through
  `CoachWorkspaceState.TodaysPlanReplaced`, the native dashboards through the presenter's
  `PlanChanged`, which a plan revision already raised.

Tests: `PlanServiceTests.RegenerateToday_*` (a real database: the plan is replaced at zero,
history is archived, nothing changes when no plan can be built, another learner's plan and ad
hoc rows are untouched), `CoachPlanRegenerationHandlerTests`, and the client tests named for
the plan reload in `CoachWriteWorkspaceStateTests` and `NativeSamPresenterTests`.
