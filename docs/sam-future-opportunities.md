# Sam — Future Opportunities Log

**Owner:** Zoe (Product/UX)
**Created:** 2026-08-20
**Status:** Living document — reviewable product backlog

## Purpose

This log captures product opportunities and conversational-quality gaps observed while using Sam
(the in-app learning coach), so they are not lost between sessions and are not silently folded into
whichever bug fix happened to be in flight when they were noticed.

**This is a backlog, not an implementation plan.** An entry here records *what was observed* and
*what capability appears to be missing* — it does not commit anyone to build it, does not specify
an API shape, and does not bypass the RFC / Learning Value Gate / Captain-approval gates that
already govern Sam's write surface (see `.squad/decisions/archive/archive-2026-08-18T1627-sam-phase1.md`
and `src/SentenceStudio.Api/Coach/Operations/Handlers/CoachPreferenceHandler.cs` for the current
policy: RFC §6.5 keeps every preference-change field unapproved — an empty `SettableNames`
allowlist — until Captain approves a specific field).

**Out of scope for this log:** defects with a known owner and an active fix in flight. As of this
writing that includes the Sam panel scroll behavior, the panel maximize control, a Korean-label
translation issue, and an account-database issue — those are tracked as ordinary bugs, not
opportunities, and should stay there. Only add an entry here for something that needs a net-new
capability, tool, or product decision, backed by evidence, not by hunches.

## Process — how an entry moves

1. **Capture.** Anyone (Captain, a squad member, or an autonomous session) adds an entry using the
   template below. Cite real evidence — a session screenshot, an E2E report path, a source file and
   line, or a decision doc. No entry without a citation.
2. **Zoe triage.** Zoe reviews new entries, checks the evidence, assigns a likely domain owner, and
   confirms the safety/risk class. Entries that turn out to duplicate an active bug fix are removed
   from this log and pointed at the existing fix instead.
3. **Captain review.** Any entry classed **Policy-gated** or touching the write surface, preference
   allowlist, or a graded/pedagogical toggle requires Captain's explicit sign-off before it can move
   to implementation — this mirrors the existing RFC §6.5 and Learning Value Gate rules, it does not
   create a new gate. Captain (or Zoe on Captain's behalf) records the decision in the entry's
   **Decision notes** column: accept, defer (with reason), or reject.
4. **Learning Value Gate.** Any accepted entry that touches an activity's modes, directions,
   prompts/responses, toggles, or defaults must also pass `.squad/skills/learning-value-gate/SKILL.md`
   before it is scheduled — this is a blocking review, not optional.
5. **Promotion to actionable work.** Once accepted:
   - If the work is scoped enough to hand to a specific squad member, Zoe (or the accepting agent)
     writes a spec under `docs/specs/` (see existing examples in that folder) and opens a
     `squad/{issue-number}-{slug}` branch per `.squad/issue-lifecycle.md`, or a GitHub issue labeled
     `squad:{member}` if this repo's issue tracker is connected.
   - If the work is small enough to start immediately in an active session, it becomes a todo in
     that session's task list, referencing this log entry by date and title so the link back to the
     backlog is not lost.
   - A decision affecting other team members is additionally recorded in
     `.squad/decisions/inbox/` per standard Squad protocol.
6. **Close the loop.** When an entry ships, is explicitly rejected, or is superseded, its **Decision
   notes** column is updated in place (with a date) rather than deleting the row — this log is a
   history of what Sam could become, not just a queue of open items.

## Entry template

| Field | Meaning |
|---|---|
| **Date** | When the entry was captured. |
| **Learner scenario** | What the learner was trying to do, in plain language. |
| **Observed response** | What Sam actually said or did. |
| **Missing capability/tool** | The specific gap — a tool, a permission, a piece of conversational state — not a fix. |
| **User value** | Why this would matter to a learner if solved. |
| **Safety/risk class** | One of: **No risk** (read-only / conversational only, fails closed today); **Low risk** (behavior/prompt tuning, no new permission or data surface); **Policy-gated** (touches the write surface, a preference allowlist, or a pedagogical default — needs Captain approval, and Learning Value Gate review if it touches an activity). |
| **Likely domain owner** | Best guess at who picks this up per `.squad/team.md` (River = AI/Prompt, Wash = Backend/API, Kaylee = UI, Zoe = Policy/LVG review, Captain = final approval on policy-gated items). |
| **Evidence/status** | File paths, report citations, or a description of the session evidence (never raw credentials or PII — see rule below). Include whether the underlying root cause has been investigated yet. |
| **Fingerprint** | *(optional)* The runtime ledger's content-free problem identity, e.g. `coach-opportunity://a41f8c2b91d7…`. Present when the entry was raised from — or has since been linked to — a `CoachOpportunity` row. Omit for an entry captured purely by hand. |
| **Decision notes** | Accept / defer / reject, who decided, and when. Blank until reviewed. |

**Evidence rule:** link to evidence by path (e.g. `e2e-evidence/.../REPORT.md:203`,
`src/.../File.cs:85`) rather than pasting file contents, and never copy credentials, tokens, or a
learner's personal data into this log. Test-account identifiers should be referenced the way
`.squad/test-accounts.md` and the E2E reports already do (masked or by role, e.g. "learner A").

---

## Where the signal comes from: the runtime ledger

**The `CoachOpportunity` table is the source telemetry. This markdown log is the human decision
record.** The two are deliberately different artifacts and neither replaces the other.

| | Runtime ledger (`CoachOpportunity`) | This log |
|---|---|---|
| **What it holds** | Content-free rows: a kind, a capability code, a tool name, a failure code, counts, timestamps, and — for individually reviewable rows — pointers into the learner's own encrypted messages. | Prose: what a learner was trying to do, why it matters, what we decided. |
| **Who writes it** | The server, automatically, at three authoritative outcome boundaries. | A human, after triage. |
| **What it answers** | *How often* does this happen, to *how many* learners, and *is it still happening*. | *Should we build this*, and *what did we decide*. |
| **Retention** | 180 days for rows nobody decided on (`New`) and rows explicitly set aside (`Dismissed`). `Reviewed`, `Accepted`, and `Deferred` are kept, because each records work a reviewer did. | Forever, updated in place. |

**No bot writes this file.** The operator API renders a paste-ready markdown block from
content-free fields and returns it in the response; a human pastes it, reviews it, and commits it.
Automating that step would bypass the Zoe-triage and Captain-approval gates this log exists to
enforce (RFC §6.5, Learning Value Gate) — which is the whole reason the log is prose in git rather
than a table in a database.

**How an entry gets a fingerprint:**

1. Open `/operator/sam-opportunities` (Development only) or `GET /api/v1/coach/operator/opportunities/rollup`.
2. Find the line for the problem: capability code, kind, occurrences, distinct learners, last seen.
3. `POST /{id}/review` with a status, a `reviewerNoteCode` from the closed set, and a
   `linkedSpecPath` pointing at this file or a `docs/specs/*.md`.
4. Paste the returned block here as a new entry (or add its **Fingerprint** row to an existing
   entry), fill in the prose fields, and commit.

**Review statuses and what they mean for retention.** A status is not only a label — it decides
whether the row survives:

| Status | Meaning | Retention |
|---|---|---|
| `New` | Recorded, nobody has looked. | Ages out after 180 days. |
| `Reviewed` | Somebody read it and has not decided yet. | **Kept.** Deleting it would silently return the problem to the pool as though nobody had looked. |
| `Accepted` | Real product work; something downstream points at this row. | **Kept.** Terminal — see below. |
| `Deferred` | Real, but not now. | **Kept.** |
| `Dismissed` | Not worth carrying. Recurrence still bumps the counters and refreshes the window. | Ages out after 180 days of not recurring. |

**Transitions are monotonic out of `Accepted` only.** An accepted row cannot be moved back to
`New`, `Reviewed`, `Dismissed`, or `Deferred`: the artifacts pointing at it — a spec, a branch, an
entry in this file — do not go away when its status changes, and two of those targets are
retention-eligible, so the move would hand a decision to the sweep. `Deferred` and `Dismissed` may
be reopened to any status, because nothing has been claimed on their behalf yet and a wrong
dismissal must not be permanent. Re-recording the same status with a different note code or spec
path is an edit, not a transition, and is always allowed. A refused transition answers `409` and
changes nothing.

**Filtering the listing.** `GET /api/v1/coach/operator/opportunities` accepts `status`, `kind`,
`capabilityCode`, `since`, `skip`, and `take`. There is deliberately **no `disposition` filter**:
the listing is already fixed to `Product` rows, so the only settings such a control could have are
"what you already have" and "nothing at all". Aggregate-only signal lives in `/rollup`.

**Reading the export.** `GET /api/v1/coach/operator/opportunities/export` streams the *rollup* as
newline-delimited JSON with the same camelCase property names as the JSON `/rollup` route, so one
consumer does not have to know which route it read from. Every operator response is
`Cache-Control: no-store`.

### The fourth source: a learner pressing Report

Three of the ledger's sources are the server observing itself refuse. The fourth is a learner
disagreeing with a turn the server considered a success — and that is the gap the other three are
structurally blind to. A fluent, well-formed, `Completed` answer to the wrong question leaves no
trace anywhere else in this table.

Every one of Sam's responses carries an inline flag control. Pressing it opens a five-choice panel
— *did not answer my request*, *incorrect or misleading*, *expected an app action*, *confusing*,
*other* — with **no free-text field**, and reporting writes two rows:

| Row | What it is | Identity |
|---|---|---|
| `CoachResponseReport` | The per-artifact fact. Carries the turn's closed-code metadata: stop reason, turn status and attempt count, the registered tool names invoked, the write proposal's state and failure code. | One per **(learner, coach response)**, forever. A unique index, so two instances racing produce one row. |
| `CoachOpportunity` | The product signal, exactly like the automatic rows. `Kind = UserReportedResponse`, always `Product`, capability code per reason, both message pointers. | One per **(learner, problem, UTC day)**, so the daily rollup answers "how many learners reported responses as incorrect" unchanged. |

Two tables because there are two identities. Folding the per-response fact into the ledger would
have forced the fingerprint to be per-response, which silently changes what `GROUP BY Fingerprint`
means for every existing consumer.

**The switch is its own.** `Coach:Reports:Enabled` governs reporting; `Coach:Opportunities:Enabled`
governs automatic capture. Turning capture off does **not** suppress a report: the learner was told
the report goes somewhere a person looks, and quietly discarding it would make that untrue while
every test still passed. Reports off means the routes 404 and the control is withheld — never shown
and then rejected.

**What "one of Sam's responses" means.** Anything Sam said to the learner in their own words,
including a **notice**. A notice that answers a request is an answer, however short, and refusing
feedback on it refuses it exactly where the product failed: *"There is no plan for today yet"* is a
notice, and it is the response a learner is most likely to want to complain about. That case
shipped with Copy and no flag, which is the defect this rule now names.

Two things stay out, for two different reasons:

| Excluded | Why | Enforced by |
|---|---|---|
| A **change receipt** | Not Sam talking — the record of a change applied to the learner's own data. The quarrel is with the change, and the plan surface can undo it; a review queue cannot. | `CoachResponseReportability.IsReportableKind`, read by both the client and `ReportAsync`, so the two cannot drift. |
| A notice the server wrote **outside a learner's turn** | There is no request it answered, so there is nothing to pair it to. | The existing fail-closed pairing: `FindPairedRequestAsync` correlates by the turn's operation id and **refuses** rather than reaching for the nearest learner message. No adjacency guess, so no list of "internal" notices has to be maintained. |

The client applies one further refinement the server deliberately does not: it withholds the flag on
a notice whose reason code marks the turn as having changed nothing — cancelled, timed out, rate
limited, validation failed. Those read as bookkeeping rather than as an answer, and filing them
under "the response was unsatisfactory" would bury the reports about what Sam actually said. That is
an affordance choice, not a refusal. The reason code lives inside the encrypted payload, so a
server-side gate on it would have to rely on a best-effort outcome lookup that returns `default` on
failure — which would make reportability nondeterministic. A report that does arrive against one is
owned, paired, and worth reading.

**No model tool can reach it.** The routes are learner actions on an authenticated request, the
owner is derived from the request scope, and nothing in the tool registry names them. That is what
makes a `UserReportedResponse` row evidence that a person pressed a button.

**What the ledger deliberately does not capture,** so nobody goes looking for it:

- **Prompt-injection detections.** Never recorded at all. Recording them would give an attacker who
  can place text in a corpus Sam reads a channel into a screen an operator reads.
- **Unauthorized tool failures.** A security event, not a product gap.
- **Cross-tenant probes.** A refusal naming an operation, conversation, or identity that does not
  resolve is counted with no conversation id and no pointers, so it can never become an existence
  oracle for another learner's identifiers.
- **Unknown tool names.** A model asking for a tool that does not exist is not detectable today —
  see "Known gaps" at the end of this file. **Do not assume the absence of such an entry means it
  is not happening.**

### Who reviews it in Production, and how

`Coach:Reports:Enabled` ships **true** in Production. That is a promise: a learner who presses the
flag control is told the report goes somewhere a person looks. The operator surface is not that
person's tool — it can decrypt learner messages and stays Development-only until this codebase has
an admin authorization primitive. The production reviewer path is an out-of-band, content-free
**digest** instead.

| Role | Who | What they do |
|---|---|---|
| **Operational owner** | River (Backend/Operations) | Owns the digest tool, the script, the workflow, and the guard tests. Keeps the path working. |
| **Review owner** | Zoe (Product/UX) | Reads the digest weekly (Mondays), triages, and records decisions in this file. |
| **Approver** | Captain | Signs off anything policy-gated, per RFC §6.5 and the Learning Value Gate. |

```bash
./scripts/sam-opportunity-digest.sh --days 7
```

The digest carries counts, closed-vocabulary codes, review statuses, timestamps, distinct-learner
counts, and content-free fingerprints — and nothing else, by construction rather than by redaction.
No owner, conversation, message, turn, or write identifier; no learner text; no decrypted evidence.
`CoachOpportunityDigestTests` asserts that against the SQL the provider emits and against a seeded
fixture, not against a comment.

Full runbook, credentials, CI prerequisites, and the weekly scheduled-workflow prompt:
[`docs/sam-opportunity-digest.md`](./sam-opportunity-digest.md).

**The digest does not write this file.** It renders lines a human reads; the human decides, pastes,
and commits — the same rule the operator markdown block follows, and for the same reason.

The three entries below predate the ledger and were recorded by hand from session evidence. They
are kept as-is: they are the historical record of what was observed, and they are also the
acceptance test for whether the ledger's taxonomy is the right shape — all three are reproducible
from live signal (`CoachOpportunityTriggerMappingTests.TheTaxonomyReproducesEveryHandSeededBacklogEntry`).

---

## Entries

### 1. Durable conversational action-intent / referent resolution across turns

| Field | Value |
|---|---|
| **Date** | 2026-08-20 |
| **Learner scenario** | Learner asks Sam what their current daily study duration is. Sam correctly reads and reports 10 minutes. Sam then offers, unprompted, to propose changing it to 45 minutes and asks the learner whether to do so. The learner replies `yes`. |
| **Observed response** | Sam loses the referent: it replies that it cannot tell what the learner wants changed. Today's Plan and the setting are left unchanged. |
| **Missing capability/tool** | A durable conversational referent — a short confirmation ("yes") needs to resolve back to Sam's own immediately-preceding offer (which setting, which proposed value) across the turn boundary, independent of whether a formal tracked proposal object exists yet. |
| **User value** | Learners naturally answer short confirmations to an assistant's own question. Every such natural exchange failing — even when the underlying change would be safe once approved — reads as a bug and erodes trust in Sam as a conversational partner, separate from whatever the eventual write policy is. |
| **Safety/risk class** | No risk. Sam fails closed today: no setting is changed and no plan mutation occurs when the referent is lost. The risk is entirely UX/trust, not data safety. |
| **Likely domain owner** | River (AI/Prompt — conversational/session state). Possible Wash involvement if the fix requires a server-tracked "pending conversational offer" concept rather than a purely prompt-side one. |
| **Evidence/status** | Session screenshot from this Captain session (2026-08-20) showing the four-turn exchange described above. Corroborating: `e2e-evidence/sam-account-boundary/REPORT.md:22` confirms the read path correctly reports "preferred session length 10 minutes" for a test account, matching the scenario's starting value. `src/SentenceStudio.Api/Coach/Operations/Handlers/CoachPreferenceHandler.cs:85` confirms `session_minutes` proposals are refused outright today (empty `SettableNames` allowlist) — so no tracked proposal object could have existed for the "yes" to bind to, meaning this defect is reachable even before Entry 2 is decided. Root cause on the conversational side (chat-history rebuild, Sam's own instructions, or a missing intent-tracking field) has not yet been investigated — this entry flags the gap, it does not diagnose it. |
| **Decision notes** | _(pending Zoe/Captain review)_ |

### 2. Guarded preference-change tool — enable daily study duration (`session_minutes`)

| Field | Value |
|---|---|
| **Date** | 2026-08-20 |
| **Learner scenario** | Same screenshot as Entry 1 — the learner wants to change daily study duration to 45 minutes conversationally, through Sam, instead of opening the Settings screen. |
| **Observed response** | `propose_preference_change` is registered and reachable, but every setting name it is given — including `session_minutes` — is refused before the profile is even loaded, because the tool's settable-fields allowlist is intentionally empty. No proposal is created, nothing is written, and no confirmation card appears. |
| **Missing capability/tool** | Not a new tool — a **policy decision** to approve `session_minutes` as a settable field, lifting it out of the closed allowlist so a learner can accept a Sam-proposed duration change the same way they already accept vocabulary/resource proposals (propose → review → confirm/undo). |
| **User value** | Lets learners adjust practice cadence in the same conversation where they're already discussing their plan, instead of leaving Sam to find a settings screen. `session_minutes` is not in the handler's `ProtectedNames` (unlike target/native language), so its blast radius is narrow, and range validation (5–180 minutes) plus a normalizer/applier already exist in code per the handler's own comments — the remaining gap is the policy decision itself, not new engineering. |
| **Safety/risk class** | **Policy-gated.** RFC §6.5 requires explicit Captain approval per field before any name is added to the allowlist (by design — "an empty allow-list is the strongest form of that rule"). This also changes plan-generation constraints, so it needs a Learning Value Gate pass per `.squad/skills/learning-value-gate/SKILL.md` before it ships, even though it is not itself an activity toggle. |
| **Likely domain owner** | Wash (backend — the allowlist entry + its tests) with Zoe as LVG/process reviewer and Captain as final approver. |
| **Evidence/status** | `src/SentenceStudio.Api/Coach/Operations/Handlers/CoachPreferenceHandler.cs:85` (`SettableNames = []`) and its RFC §6.5 citation at lines 37 and 68 of the same file. `e2e-evidence/sam-write-e2e/REPORT.md:203` — the `SAM-PREF` acceptance test confirms a `session_minutes` request today produces "no proposal at all" and no `UserProfile` column changes. Session screenshot from 2026-08-20 shows the learner-facing symptom of this closed gate. |
| **Decision notes** | Blocked on Captain approval + Learning Value Gate sign-off. **Distinct from Entry 1:** approving this field does not by itself fix referent loss — Entry 1 also has to be resolved, or a learner's "yes" still will not reach this tool even once it is unlocked. _(pending Zoe/Captain review)_ |

### 3. Resolve-entity-by-name before write proposals

| Field | Value |
|---|---|
| **Date captured** | 2026-08-20 (underlying evidence from the 2026-08-19 write-surface gate) |
| **Learner scenario** | Learner asks Sam to change, link, or remove a vocabulary word/skill/resource by its title only (e.g. by name), not by its internal id. |
| **Observed response** | The `propose_*` write handlers require an entity id (`ResourceId`/`WordId`). Asked by title, the model often answers "I can't send the proposal from here" instead of first calling the matching read tool to resolve the title to an id. Given an id directly, every tool worked on the first try. |
| **Missing capability/tool** | A reliable "resolve this name to an id first" step — either a prompt/tool-description change that makes the model call the read tool before the write tool, or a small dedicated lookup affordance — so a title-only request does not stall on a generic refusal. |
| **User value** | Learners refer to vocabulary and resources by name in ordinary conversation; removing this friction lets more everyday requests succeed on the first turn instead of requiring the learner to somehow supply an internal id. |
| **Safety/risk class** | Low risk. No new permission and no new data surface — purely conversational orchestration. The original tester's own assessment was "prompt or tool-description tuning; no server change is warranted from this evidence," which this entry preserves rather than overstates. |
| **Likely domain owner** | River (AI/Prompt). |
| **Evidence/status** | `e2e-evidence/sam-write-e2e/REPORT.md` (2026-08-19 write-surface gate), defects-and-observations finding #3. Flagged here for visibility because it recurred across multiple write tools in that run, not because the original evidence argues for a new tool. |
| **Decision notes** | Likely low priority; may resolve with prompt tuning alone. _(pending Zoe/Captain review)_ |

---

## Known gaps in the runtime ledger

These are stated so nobody mistakes an empty rollup for an absence of problems.

| Gap | Why it exists | Status |
|---|---|---|
| **A model asking for a tool that does not exist is not detected.** `AIFunctionFactory` functions are resolved by name from the supplied set, so an unknown name never reaches server code and neither the turn runner nor the harness options expose an unknown-function callback. There is **no capability code for it and no code path that could emit one** — it is not a rare case, it is unreachable. It surfaces indirectly, and unattributably, as an out-of-scope row or a `turn_tool_failure_unattributed` row at the turn boundary. | Deliberate v1 scope. Closing it needs a `FunctionInvokingChatClient` interception layer. | **v2 — coverage is explicitly not claimed.** |
| **Referent loss is detected only when the turn asked the learner what they meant.** The detector fires on `CoachStopReason.ClarificationRequested` and nothing else. A turn that *completed* is treated as having used the learner's answer, because the server has no authoritative signal saying otherwise — and the alternative, assuming loss, classified every ordinary "coach asks, learner answers, coach teaches" exchange as a defect and attached decryptable evidence pointers to it. | A turn that dropped an answer and completed anyway is genuinely indistinguishable from one that used it, without a new server-side signal (an intent naming the offer, or a failed binding attempt). | **Known under-count, deliberately. Widening needs a new signal, not a looser predicate.** |
| **A turn-level tool failure cannot be attributed to a tool.** `CoachStopReason.ToolFailure` is counted as `turn_tool_failure_unattributed`; the tool boundary records the tool name and failure kind on its own row. The two counts are comparable but not joinable. | The turn boundary genuinely does not know which tool failed. Labelling it `tool_data_access` invented a cause and double-counted the tool boundary's row. | By design. |
| **Aggregate-only rows cannot be traced to a session.** A harmful, out-of-scope, capacity, or protocol-error refusal is counted with no conversation id and no pointers. | Trading forensics for the guarantee that a refusal never becomes an inspectable dossier. | By design. |
| **The operator review surface is Development-only.** It can decrypt learner messages and this codebase has no admin authorization primitive. | Shipping it to Production would mean inventing one under time pressure. | **Covered in Production by the content-free digest** (`./scripts/sam-opportunity-digest.sh`, `docs/sam-opportunity-digest.md`), which answers what/how often/how many but never who. Revisit the surface itself when an admin authorization primitive exists. |
| **Production capture is off.** | Capture is provably response-neutral (it runs after the response is computed, inside `try/catch` that swallows every exception including cancellation, with no value a caller can branch on) but "provably" means "after the proof has been reviewed". | Awaiting Captain's approval after `SAM-OPP-01`…`10`. Unchanged by the reports flip. |
| **Production reports are on, so the ledger is not empty there.** `Coach:Reports:Enabled` is `true` in Production while `Coach:Opportunities:Enabled` is `false`, and the recorder admits `UserReportedResponse` on the report switch alone. | The learner was told the report goes somewhere a person looks; discarding it because automatic capture is off would make that untrue while every test still passed. | By design. Retention is therefore mandatory on **both** sections in Production — a `RetentionSweepEnabled: false` beside an enabled switch fails startup. |

---

## Change log

- **2026-08-20** — Log created by Zoe. Seeded with the daily-study-duration referent-loss screenshot
  (Entries 1–2) and one evidence-backed follow-up from the 2026-08-19 write-surface gate (Entry 3).
- **2026-08-20** — River added the runtime opportunity ledger (`CoachOpportunity`). This log is now
  the human decision record over that telemetry rather than the only record of what was observed.
  Entry template gains an optional **Fingerprint** field; Entries 1–3 are retained unchanged as
  manually recorded historical evidence.
- **2026-08-20** — Wash corrected the ledger after review. The referent-loss detector no longer
  fires on a completed turn, so ordinary successful tutoring is not recorded (Entry 1's own
  screenshot flow is unchanged — it stops with a clarification). A denied run now records a
  content-free `daily_run_limit` count at the rate-limit boundary. A turn-level tool failure is
  counted as `turn_tool_failure_unattributed` rather than being mislabelled `tool_data_access`.
  Retention ages out `New` and `Dismissed` only; `Reviewed` joins `Accepted` and `Deferred` as
  kept. `Accepted` is now a terminal review status. A cross-owner evidence request answers `404`
  rather than `403`, and every operator response is `no-store`. The "Known gaps" table above is
  updated with the two under-counts this creates, stated explicitly so an empty rollup is not read
  as an absence of problems.
- **2026-08-21** — River added the **production reviewer path** so `Coach:Reports:Enabled` could
  ship `true` in Production without the evidence-decrypting operator surface going with it. The
  path is an out-of-band, content-free digest (`./scripts/sam-opportunity-digest.sh` →
  `docs/sam-opportunity-digest.md`): counts, closed codes, review statuses, timestamps,
  distinct-learner counts, and fingerprints, with no owner, conversation, message, turn, or write
  identifier and no decrypted evidence. Reviewer roles are named above (River operational, Zoe
  review, Captain approval). Production configuration now states every coach ledger switch
  explicitly rather than inheriting a default — automatic capture stays **off**, the operator
  surface stays **off** and is deliberately not forwardable as an environment variable, and
  retention is **on** for both sections because reports raise ledger rows even with capture off.
  Also corrected an attribution defect Mal flagged: the operator detail card no longer attaches the
  earliest report's turn facts to a daily-dedup row that several reports landed on. It attaches the
  facts for the response the row's own evidence points at, and always reports the aggregate
  (`ReportCount`, `ReportedResponseCount`, reason breakdown, and whether the facts belong to the
  reported response) so "one turn" is never read as "the row".
