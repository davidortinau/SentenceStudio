# Sam foundation gate — soak runbook

**Owner:** W9 (`sam-enforce-foundation-gate`)
**Status:** condition (a) **not approved — awaiting final review.** The engineering suite is green,
Zoe's Learning Value Gate sign-off on the refusal surface is recorded (§8), and P5/P6 repair
disclosure — the last open path — closed during this pass (§9). What remains is the review itself,
which is not engineering's to declare. Condition (b) **still blocked**: not started and not
startable by engineering.
**Companion query:** [`sam-foundation-gate-soak-query.kql`](./sam-foundation-gate-soak-query.kql)

---

## 0. The one thing this document exists to prevent

> **A local green run does not close the foundation gate.**

The plan closes `sam-enforce-foundation-gate` **on two conditions and no others**:

> **(a)** AC-F1 to AC-F8 pass as synthetic test cases in tiers 1 to 6, against a synthetic client
> handshake — no gate item requires a shipped post-gate client capability; **and**
> **(b)** the twelve foundation invariants in section 16.1 read zero on production across the soak
> window in Q15.

Condition (a) is a **code** question. It is answered by running the test suite, and the suite is
green on every path the gate names. It is not *approved* until the review that reads this document
says so. Condition (b) is an **operations** question about real traffic on a deployed revision. No test
run, no fixture, no simulated corpus, and no amount of engineering diligence can answer it. Anyone
who reads a green `Coach.Gate` run as "the gate is closed" has closed the gate on half its terms.

The two conditions are kept in separate sections below for exactly that reason. Do not merge them
in a status report.

---

## 1. Condition (a) — synthetic acceptance (engineering-owned, **awaiting final review**)

The test evidence below is green and complete, Zoe's Learning Value Gate sign-off on the refusal
surface is recorded (§8), and the repair-disclosure path closed during this pass (§9). Condition (a)
is nonetheless recorded as **not approved** until the final review is run — the tests say the code
is right, and only the review says the gate is passed. Condition (a) is also **necessary and not
sufficient** for the foundation gate even once approved: it says nothing about production behaviour
over real traffic, which is condition (b) and remains blocked.

### 1.1 What proves it

```bash
dotnet build tests/SentenceStudio.Api.Tests/SentenceStudio.Api.Tests.csproj
dotnet test  tests/SentenceStudio.Api.Tests/SentenceStudio.Api.Tests.csproj \
  --no-build --filter "FullyQualifiedName~Coach.Gate"
```

The `Coach/Gate/` suite carries the acceptance matrix. It is the only place AC-F1..AC-F8 are
asserted; other suites carry component behaviour and are traited, not duplicated.

| Trait | Values | Meaning |
| --- | --- | --- |
| `Tier` | `1`, `2`, `3`, `4`, `5`, `6` | Plan §14 evaluation tier |
| `Acceptance` | `AC-F1`..`AC-F8`, `Case-A`..`Case-D` | The §14.1 gate case or §14.2 foundation bar |
| `Evidence` | `soak-measured`, `build-time`, `structurally-absent`, `inactive-until-c1` | Which of the three buckets (§3 below) an invariant test belongs to |

> **`--filter` matches the trait *value*, not the C# constant name.** `Evidence=SoakMeasured`
> selects nothing and exits zero; `Evidence=soak-measured` selects 43 tests. The census test
> guards the vocabulary, but it cannot guard your shell history.

Filter one tier at a time:

```bash
dotnet test ... --filter "Tier=2"                      # 62
dotnet test ... --filter "Acceptance=AC-F4"
dotnet test ... --filter "Evidence=structurally-absent" # 7
```

Counts on this build, as `dotnet test` reports them (runtime cases, so a `[Theory]` contributes one
line per row):

| Filter | Cases | Carrier |
| --- | --- | --- |
| `Tier=1` Semantics and scope | 35 | scope/order suites, Learning Value Gate closure register (§8), repair-disclosure closure guard (§9.3) |
| `Tier=2` Claim rules and repair | 62 | rule + repair suites, case bars, trace conflation |
| `Tier=3` Agreement | 4 | cross-seam agreement, incl. the real-PG gate |
| `Tier=4` Replay | 5 | `CoachFoundationGateReplayTests` |
| `Tier=5` Capability | 13 | legal matrix, derived availability, handshake |
| `Tier=6` Host parity | 10 | `CoachFoundationGateHostParityTests` |

By bucket: `soak-measured` 43, `build-time` 15, `structurally-absent` 7, `inactive-until-c1` 3.

**Two suites, one gate.** The §8 register cites closures that live in `tests/SentenceStudio.UI.Tests`
as well as the API suite, because the refusal surface is proved partly at the wire and partly at the
render. `Every_closed_blocker_in_the_register_names_tests_that_exist` resolves the UI-side names by
reading those files rather than by referencing the assembly — a project reference would drag a Blazor
test harness into the API suite to check three names. Run both:

```bash
dotnet test tests/SentenceStudio.UI.Tests/SentenceStudio.UI.Tests.csproj --no-build
```

> **`dotnet test --filter` exits zero when nothing matches.** A tier that selects no tests looks
> identical to a tier that passes. `CoachFoundationGateTierCensusTests` is the tripwire. It asserts
> that **every** plan tier 1..6 has a carrier, that every AC-F case and §14.2 case bar has one, and
> — because "at least one" is satisfied by a tier that quietly thins from four carriers to one — it
> also pins the **exact** per-tier and per-bar carrier counts. Those exact counts are measured in
> test *methods*, so they are smaller than the runtime-case numbers in the table above; a `[Theory]`
> with four rows is one carrier and four lines.

### 1.2 Tiers 4 and 6, and why they are no longer empty

For one build, `--filter "Tier=4"` and `--filter "Tier=6"` printed **"No test matches"** and exited
**zero**. The reasoning at the time was that §14 names `FeedbackPreviewTokenReplayTests` and
`SamHostParityTests` as the carriers for those tiers, and both belong to later workstreams.

That reasoning was wrong about the consequence. It left two of six gate tiers unevidenced while the
gate still reported six rows, and an empty filter is indistinguishable from a passing one in a CI
log. Both tiers now carry real tests, asserting what §14 says the tier proves, at the layer this
project can reach:

- **Tier 4, Replay** — `CoachFoundationGateReplayTests`. §14: *"recorded results plus traces compose
  a deterministic answer with no model call."* One recorded turn replayed twenty times through one
  evaluator, and again through freshly constructed evaluators, must produce a byte-identical verdict
  across every field the soak or the report can read. Plus a structural scan proving no chat client
  is reachable from the evaluator or the rule engine, by constructor or by field — *"no model was
  called today"* is an observation about one run; *"no model is reachable"* is a property of the
  type, and only the second survives someone wiring one in later. A third test pins that the
  replayed fixture actually composes findings, so the determinism claim is not made over an empty
  verdict twenty times.
- **Tier 6, Host parity** — `CoachFoundationGateHostParityTests`. §14: *"every capability acceptance
  case passes in both Sam hosts."* Host identity never reaches the capability layer; a host
  influences resolution through exactly one channel, the handshake, which carries a `Version` and a
  set of `Codes`. Parity is therefore the statement that resolution depends on the advertised codes
  alone and **does not vary with handshake version** — the one field two hosts genuinely differ on,
  because the native and web heads ship on independent cadences. AC-F1/F2/F3/F5 each run against
  both host-shaped handshakes (seven versions apart, so an off-by-one comparison cannot pass by
  luck), plus a sweep across every capability stage and a non-vacuity test proving the sweep runs
  against a resolver that distinguishes stages rather than a constant.

Neither file stands in for the later end-to-end guards, and both say so in their own remarks. What
was **not** done: hanging a tier-4 or tier-6 trait on a loosely related existing suite. That turns
the filter green while proving nothing, which is the failure the census exists to catch.

### 1.3 AC-F1/F2/F3/F5 are synthetic by design

These four cases use a **synthetic handshake and synthetic registrations**. No shipped client
advertises a client capability until C1, which is *after* this gate. Requiring a post-gate client
to close a pre-gate gate makes the gate circular. Plan §14.1 says so directly: *"These eight cases
are synthetic test gates. None of them is a production precondition."*

AC-F4/P1 and AC-F8/P3 rerun existing behaviour as regression rather than introducing new assertions.

### 1.4 Trace conflation — **closed**, and what it unblocked

A defect found during W9 review is now fixed and fenced. Recorded here because it changes how
invariant 2 should be read in the soak evidence pack.

**What the defect was.** `CoachTurnTraceProjection.Project` returned `null` both when the
observation buffer was absent and when it was present holding zero recorded tool calls. Those are
different facts. An absent buffer means *we do not know* whether tools ran. A present buffer holding
zero calls means *nothing was read*, positively recorded. Two honesty rules — `FabricatedCheck` and
`UnverifiedLearnerStateClaim` — deliberately bail on a null trace, and `CoachFabricatedCheckRule`
states the reasoning in its own source: *"No trace is no evidence of absence. Only a recorded turn
can prove a check did not run."* The reasoning was correct; the projection defeated it, because the
recorded turn the comment asks for arrived as `null` anyway.

**Why it mattered.** An empty-but-present buffer is every turn the model answered from its own head
instead of calling a tool — exactly the population those two rules exist to catch. While the
collapse stood, **invariant 2** would have read zero across the window whether or not the
behaviour was occurring, and rule code 1 `UnverifiedLearnerStateClaim` — a live honesty rule that
is not one of the seven soak-measured codes — was blinded the same way. **A zero produced by a
blind spot is worse than a missing measurement, because it is reported with the same confidence as
a real one.**

**Scope, precisely.** Exactly two rules gate on the trace: `CoachUnverifiedLearnerStateClaimRule`
(`Trace is null`) and `CoachFabricatedCheckRule` (`Trace is null || TraceShowsASuccessfulRead`). No
other rule reads it, so no other invariant was affected. An earlier draft of this section said
"invariants 2 and 3"; invariant 3 is `FalseLimitation`, which never consults the trace. Corrected
here rather than carried forward.

**The fix.** `Project` now returns a zero-length trace when the buffer is present and keeps `null`
only for a null buffer, so both rules can tell "read nothing" from "unobserved" and ask their
question. No rule logic changed.

**Consequence for condition (b): invariant 2 is now observable.** A zero on it across the soak
window is evidence of the behaviour's absence rather than evidence of the layer's silence. Before
the fix it should have been recorded as *unproven*; from this build forward it carries the same
weight as the other five soak-measured invariants. Nothing else in §2 or §3 changes.

**The fence.** `CoachTraceConflationGateTests` (tier 2, 7 cases, all green) holds the property in
both directions. The collapse is a cheap-looking optimisation — "why allocate an empty array" — and
would be easy to reintroduce as a tidy-up, which is why the file stays after the fix.

| Test | Holds |
| --- | --- |
| `An_empty_but_present_buffer_does_not_project_to_null` | the seam itself, asserted directly |
| `Enforce_catches_a_fabricated_check_when_the_recorded_turn_read_nothing` | invariant 2 is reachable |
| `Enforce_catches_an_unverified_learner_state_claim_on_an_empty_trace` | rule code 1 is reachable |
| `An_absent_buffer_still_projects_to_null` | unknown stays unknown |
| `An_unobserved_turn_is_not_convicted_on_evidence_that_was_never_collected` | both rules stay silent pre-W4 |
| `The_fixture_text_is_recognised_by_the_rules_when_a_trace_exists` | non-vacuity — the fixtures really do carry claims |

The last three are the guard rail: they are why the strict half cannot be satisfied by simply
dropping the trace gate. Treating unknown as guilty would rewrite honest historical answers on no
evidence at all, which is a different dishonesty and a worse one.

---

## 2. Condition (b) — production soak (Captain-owned, not started)

**Engineering cannot start, shorten, simulate, or substitute for this.** Everything in this section
is a checklist for the person who runs the soak, not a task list for the person who wrote the tests.

### 2.1 Preconditions — all required before the window opens

| # | Precondition | Who | Why it matters |
| --- | --- | --- | --- |
| B1 | **Q15 answered** — the soak window length is chosen and written down | Captain | Q15 is an open question in the plan (§ open questions, Q15: *"the length of the production soak window for the section 16.1 foundation invariants — Captain, before W9 promotes to Enforce"*). Until it is answered there is no defined window, so there is nothing to read zero *across*. |
| B2 | **Deployed SHA and revision recorded** | Captain | A zero read against an unknown build proves nothing. Record the commit SHA and the container revision serving traffic for the whole window. |
| B3 | **Window start and end recorded in UTC** | Captain | Both bounds, explicitly. "Recently" is not a window. |
| B4 | **`Coach:Grounding:Stage` = `Enforce` in production** | Captain | Invariants 4 and 5 are defined *after Enforce*. Read at `Observe` or `Repair` they are measuring a different system. |
| B5 | **`Coach:CorrectionState:Enabled` = `true` in production** | Captain | Invariant 6 (disputed-claim repeat rate) has no denominator without dispute state recorded. |
| B6 | **Real traffic has elapsed inside the window** | time | See §2.2. A window with no turns reads zero for the wrong reason. |

> Engineering does not set B4 or B5. Configuration is external to this repository and outside the
> W9 file ownership boundary. This runbook names the settings so the operator can find them; it does
> not change them.

### 2.2 The denominator is the whole game

Every one of the seven measured rule classes is a **rate with `coach.grounding.turns_evaluated` as
its denominator**. A window where that denominator is zero produces seven zeros that mean *"nothing
happened"*, not *"nothing went wrong"*. Those two readings are indistinguishable in the numerator
alone, and the second is the only one that closes a gate.

**Therefore, before reading any numerator:**

1. **`coach.grounding.turns_evaluated` must be strictly positive** over the window.
2. **`grounding_stage` must be `Enforce`** on those turns. Turns evaluated at `Off` emit nothing at
   all — the evaluator returns unchanged and never records — so an `Off` window is
   indistinguishable from an outage in the metric alone.
3. **The canary must have been delivered exactly once**, proving the pipeline from meter to backing
   store is live for this revision. `coach.grounding.canary` exists for no other purpose. A silent
   exporter and a clean system look identical without it.

The local suite pins all three properties as non-vacuity tests (`CoachGroundingNonVacuityTests`):
the denominator is strictly positive on evaluated turns, exactly one increment per evaluated turn,
zero at `Off`, each of the seven classes has both a positive and a zero fixture, and the canary is
emitted exactly once and received by a listener while a production-source scan for callers stays at
zero. Those tests prove the *instrument* is honest. They cannot prove the *window* is.

### 2.3 Reading `turns_suppressed` — a real trap

`coach.grounding.turns_suppressed` counts **policy in force**, not **repairs withheld**.

`SuppressRepairForLanguage(stage, answer)` returns true for any non-English language tag at
`stage >= Repair`, and it is evaluated **before the rules run**. A perfectly clean Korean turn with
zero findings still increments `turns_suppressed`.

For a Korean-majority Enforce window, `turns_suppressed / turns_evaluated` trends toward **1.0**.
Read alone, that looks like "the grounding layer suppressed everything". It is not a defect — the
flag correctly records that substitution was unavailable for the turn's language — but it must
never be read alone.

> **Rule: read `turns_suppressed` beside `findings` and `turns_altered`, never on its own.**
> High suppression + zero findings = a clean non-English window.
> High suppression + positive findings + zero altered = the refusal path doing its job.

### 2.4 What the soak must produce

A complete condition (b) evidence pack is **all** of:

- [ ] Q15 answer, window start and end in UTC, deployed SHA, container revision (B1–B3)
- [ ] Stage confirmed `Enforce` and correction state confirmed `true` for the whole window (B4, B5)
- [ ] `coach.grounding.turns_evaluated` **strictly positive**, with the actual number
- [ ] **Exactly one** `coach.grounding.canary` delivery observed
- [ ] **Seven measured zeros, each with its denominator printed beside it** (§3, bucket 1)
- [ ] **Two build-time results, dated**, from the run that produced the deployed SHA (§3, bucket 2)
- [ ] **Three absence proofs** against the registry and configuration as deployed (§3, bucket 3)
- [ ] **Re-arm conditions C1 and C3 restated** so the next reader knows the absences are conditional
- [ ] Invariant 9 reported as **inactive**, not as a measured zero (§3.4)

A zero without a denominator beside it is not evidence. Print both.

---

## 3. The twelve invariants come in three buckets — plus one inactive

Plan §8.2 lists twelve foundation invariants and §16.1 rows 1–12 restate them as zero-tolerance
metrics. They are **not** twelve of the same kind of thing, and treating them as twelve soak
readings will produce three fabricated zeros and one that is a lie.

`CoachStructuralZeroInvariantTests` pins this classification so the census cannot drift.

### 3.1 Bucket 1 — seven rule classes, six invariants, measured by the soak

| Rule code | §8.2 invariant |
| --- | --- |
| `NegativeClaimWithoutCoverage` | 1 — unbounded negative claims about learner state |
| `FabricatedCheck` | 2 — fabricated-check proxy events |
| `FalseLimitation` | 3 — false limitations against a capability that resolves `Present` |
| `OrderClaimMismatch` | 4 — order mismatches after Enforce |
| `CountClaimMismatch` | **5** — count mismatches after Enforce |
| `WithheldNotDisclosed` | **5** — *"including withheld not disclosed"* |
| `RepeatedDisputedClaim` | 6 — repeated disputed claims |

**Seven rule codes map onto six invariants.** §16.1 row 5 reads *"Count-mismatch rate after Enforce,
**including withheld not disclosed**"* — one metric, two rule codes. The soak query groups them
accordingly, and the local suite asserts the grouping so a future split cannot happen silently.

**Invariant 2 is observable as of the trace-conflation fix.** `FabricatedCheck` gates on the trace,
so until an empty observation buffer stopped projecting to `null` the rule could not see the turns
it exists to catch. It can now. Held by `CoachTraceConflationGateTests` — see §1.4.

**Invariant 4 is measured by `OrderClaimMismatch`, and is readable.** It was not, until recently.
The rule used to early-return whenever the evidence itself stated an order
(`context.EvidenceStatesAnOrder`), and over a corpus where reads declare their ordering — which most
of ours do — that made it structurally unable to fire. A zero on this row would have meant "the rule
did not look", not "no answer misdescribed an order": the same blind-spot-zero failure as §1.4, and
the reason condition (a) was rejected on the previous build.

The rule now resolves the claimed ranking to a measure and a direction and compares it against the
recorded order, so contradicting a stated order is caught and describing it accurately is not. The
bar it landed against, and the fence around it, are four Case C tests in
`CoachFoundationCaseBarsTests`:

| Test | Holds |
| --- | --- |
| `Case_C_all_three_mismatch_rules_fire_on_the_recorded_shape` | the §14.2 bar — order, count and withheld all fire on the recorded `MasteryDescending` shape |
| `Case_C_the_order_rule_catches_a_contradicted_order_and_an_unstated_one` | both paths, so widening did not cost the case already handled |
| `Case_C_an_answer_that_states_the_order_the_read_used_is_not_a_mismatch` | the honest answer survives — a bare deletion of the early return would not have passed |
| `Case_C_the_honest_answer_over_the_same_read_fires_none_of_the_three` | the zero fixture over the same read |

The third of those is the one worth keeping in mind on any future change to the rule. The cheap fix
here — delete the early return — turns the rule into "any order word near any evidence is a finding"
and starts rewriting answers that correctly describe their own evidence. That is a worse failure than
the gap it closes, because it punishes the honest answer.

**No caveat on invariant 4.** The earlier `WEAK` annotation in this section and in the `Caveat`
column of §3 of the soak query has been removed, its condition having been met: the bar passes and
the full real-PG suite is green. A zero on invariant 4 now carries the same weight as the others.

### 3.2 Bucket 2 — two structural invariants, proven at build time

| # | Invariant | Proven by |
| --- | --- | --- |
| 11 | Registrations passing startup outside the legal matrix | A build-time legal-matrix assertion |
| 12 | Types under `Coach/Tools/**` referring to `ApplicationDbContext` | A build-time boundary scan |

These are **not** soak readings. They are properties of the artefact. The evidence pack needs the
**dated result of the build that produced the deployed SHA** — not a re-run on a later commit, which
would be evidence about a different artefact.

### 3.3 Bucket 3 — three invariants structurally absent, with named re-arm conditions

| # | Invariant | Why it cannot fire today | Re-arms at |
| --- | --- | --- | --- |
| 7 | Embargoed term disclosure in a quiz cohort or a launch payload | No launch payload is constructed; the embargo scanner has no launch surface to scan | **C3** |
| 8 | Unauthorized navigation | No navigation is issued from the coach surface | **C1** |
| 10 | Cross-user or cross-circuit presentation writes | No presentation write path exists | **C1** |

These read zero because **the code path does not exist**, not because it exists and behaved. The
evidence is an **absence proof over the current registry and configuration** — the local suite
asserts the registry contains no such registration — not a metric query. Reporting them as soak
zeros would claim the soak exercised something it structurally could not.

> **Re-arm:** when C1 lands, invariants 8 and 10 become live and must move to bucket 1 or gain their
> own guards. When C3 lands, the same for invariant 7. The absence proofs expire on those dates.

### 3.4 Invariant 9 is inactive — do not report it as a measured zero

`SideEffectNotDisclosed` (§8.2 #9, §16.1 row 9) **cannot fire in this build**.
`ProposedCapabilities` is hardcoded to `Array.Empty<string>()` in `CoachSessionService`, so the rule
has no input and is never reached.

Report it as **inactive until C1**, carrying the `Evidence=InactiveUntilC1` trait. Reporting it as a
measured zero would assert the soak observed a rule that never ran.

### 3.5 The arithmetic

**6 measured invariants + 2 build-time + 3 structurally absent = 11 of 12.**
The twelfth is invariant 9, inactive. There is no combination of these buckets that yields twelve
soak readings, and any report claiming twelve is wrong.

### 3.6 §16.3 substitution take-up is **inactive / not measurable** — never zero

Plan §16.3 monitors *"substitution take-up after an answer refusal — at least 30 percent, monitored,
no baseline"*, on the reasoning that **a refusal that becomes a retrieval is worth more than a
refusal that ends the turn**.

**The substitute is wired.** As of the W9 typed-refusal change, a refusal is no longer a dead end:
it carries the evidence the turn honestly read, a typed `CoachLimitationDto` whose destination names
one of the six bound routes when a real one follows from what was read, and copy that resolves from
the client resource files in the learner's own language on both hosts. The product half of §16.3 is
done, and §8 records it closed.

**What is missing is the event, not the feature.**

| What the metric needs | What the build has |
| --- | --- |
| A substitute the learner can act on | Present. Typed destination on the limitation, rendered as a real navigation on both hosts |
| An offer with somewhere to go | Present. `CoachRefusalLimitationProjection.DestinationFor` maps each definition to a bound route, or to `null` where no real screen exists — it never invents one |
| A take-up event to count | **None is emitted.** Nothing records that a learner followed a destination out of a refusal. There is no numerator |
| A refusal-with-destination population to divide by | **None is emitted.** `CoachGroundingMetrics` exposes six instruments and none of them counts offers. There is no denominator |

So the metric has **no numerator and no denominator** — not because the substitute is absent, but
because nothing counts the offer or the follow-through.

**Report it as `inactive / not measurable`, in the same class as invariant 9 — never as a measured
zero, and never as "0 percent take-up".** A zero would assert that learners were offered a
substitute and declined it. The offer is now real, but nobody is counting either side of the ratio,
so a zero would be an unfounded claim about learner behaviour rather than a reading. That is the
difference between a product finding and a slander of the learner, and it is exactly the reading the
§0 rule exists to prevent.

**Becomes measurable when** an instrument pair exists: one event when a refusal ships carrying a
non-null destination, and one when a learner follows it. Neither requires further product work on
the refusal surface. Until that pair exists, §16.3 take-up is absent from the KQL by design — see
SECTION 5 of the query.

`No_take_up_or_destination_offered_instrument_exists` (tier 1) pins the metric surface, so the
instrument cannot appear without this section being revisited. `The_runbook_reports_substitution_take_up_as_inactive`
and `The_query_omits_substitution_take_up_and_says_why` hold this wording and the query's omission
together.

---

---

## 4. Engineering recommendation on the Q15 window — **a recommendation only**

Q15 is Captain's to answer. The following is offered as input and **binds nothing**:

> **Recommended: 14 days, at least 500 evaluated turns, and at least 2 weekends.**

Reasoning, so the recommendation can be argued with rather than merely accepted:

- **500 evaluated turns** is a floor on the denominator, not a target. Below a few hundred turns a
  zero numerator is consistent with a rate the gate would not accept; the window stops discriminating.
- **2 weekends** because study traffic is weekday-shaped. A window that samples only weekdays misses
  the long-session and catch-up behaviour where multi-claim answers concentrate — the answers most
  likely to trip a count or order mismatch.
- **14 days** is what it takes to contain two weekends with margin for a deployment or an outage
  mid-window without restarting.

If Captain answers Q15 with a shorter window, **the recommendation does not override the answer** —
but the evidence pack should record the denominator actually achieved so the reader can judge the
strength of the zeros for themselves.

---

## 5. Running the query

The companion file is [`sam-foundation-gate-soak-query.kql`](./sam-foundation-gate-soak-query.kql).

**Do not run it against production from this repository or from an engineering session.** It is
written for the operator who holds the soak, has the deployed revision in hand, and is authorised to
query the production telemetry store. It carries no hostname, no resource identifier, no
subscription, no key, and no connection string, and none must be added to it in the repo.

To use it, the operator:

1. Fills in the window bounds and the revision at the top of the file.
2. Runs it in whatever workspace holds the production metric export.
3. Copies the resulting table into the evidence pack **with denominators included**.

The query deliberately returns the denominator on every row. A version that returns only numerators
should be treated as a defect in the query, not a convenience.

---

## 6. Rollback ladder

If any invariant reads non-zero, **stop the soak and step down**. Do not "wait and see" — the window
is already invalid, and a partially-invalid window cannot be repaired by extending it.

**Grounding stage, one rung at a time, verifying between each:**

```
Enforce  →  Repair  →  Observe  →  Off
```

| Step | Setting | What it stops | What it keeps |
| --- | --- | --- | --- |
| 1 | `Coach:Grounding:Stage` = `Repair` | Refusals. Structurally-unrepairable findings no longer refuse the turn | Substitution, all findings, all metrics |
| 2 | `Coach:Grounding:Stage` = `Observe` | Substitution. Answers ship unaltered | Findings and metrics — full visibility, zero intervention |
| 3 | `Coach:Grounding:Stage` = `Off` | Everything. The evaluator returns unchanged, no scan, no record, **no metric** | Nothing |

**Correction state, independently:**

```
Coach:CorrectionState:Enabled = false
```

This disables dispute tracking. Invariant 6 (`RepeatedDisputedClaim`) loses its input and must then
be reported as **inactive**, exactly like invariant 9 — not as a zero.

**Two properties of this ladder worth knowing before you need it:**

- **`Observe` is the safe diagnostic rung.** It keeps every finding and every metric while
  intervening in nothing. If the question is "is the layer wrong, or is the traffic wrong?", drop to
  `Observe` rather than to `Off` — `Off` destroys the evidence you need to answer it.
- **`Off` is not a quieter `Observe`.** At `Off` the evaluator short-circuits before the scan. The
  denominator goes to zero and stays there. An `Off` window and a broken exporter produce identical
  telemetry, which is why the canary is checked before any numerator is read.

**Rolling back invalidates the window.** After any step down, condition (b) restarts from B1 with a
new window, a new SHA, and a new revision. There is no partial credit.

---

## 7. Status summary

| Condition | Owner | State |
| --- | --- | --- |
| (a) AC-F1..AC-F8 synthetic acceptance, tiers 1–6 | Engineering | **Not approved — awaiting final review.** The suite half is green and complete: `Coach.Gate` green on real PostgreSQL, all six plan tiers carried with exact counts pinned by the census. No engineering item is outstanding; the approval itself is the review's to give, not engineering's to declare |
| W9 Learning Value Gate on the refusal surface (L1–L6, LVG-W9-8) | Product + Zoe | **Sign-off recorded by Zoe.** Every refusal blocker closed; §8.2 and §8.3 name the test that holds each one, and the register test resolves those names on every run |
| **P5/P6 repair disclosure** | Product + Zoe | **Closed during this pass.** An answer the layer rewrote now announces itself. `CoachRepairDisclosureNotice.razor` reads the wire enum on both hosts, `en` + `ko`, polite status region, count-free; R1–R6 held by 18 green cases in `CoachRepairDisclosureWiringTests` — see §9. Not covered by Zoe's refusal sign-off, which was scoped to §8 |
| §16.3 substitution take-up | — | **Inactive / not measurable.** The substitute is wired; what is missing is the event. No take-up numerator and no refusal-with-destination denominator is emitted — see §3.6. Must never be reported as zero |
| Invariant 4 blindness (`OrderClaimMismatch` early-returned on a stated order) | Zoe | **Closed.** The rule now resolves the claimed ranking against the recorded order. Four Case C tests hold the bar and fence the false-positive edge; invariant 4 is readable and its `WEAK` caveat is removed — see §3.1 |
| Trace conflation (`Project(empty buffer)` collapsed to null) | Zoe | **Closed.** Fix landed; `CoachTraceConflationGateTests` green holds it in both directions. Invariant 2 is now observable rather than unproven, as is rule code 1 — see §1.4 |
| (b) Twelve foundation invariants read zero across the Q15 window | Captain | **Not started, still blocked.** Blocked on B1 (Q15 unanswered), B4, B5, and elapsed real traffic |

**The gate is not closed. Condition (a) is not approved, and condition (b) has not started.**

The engineering half of condition (a) is done. Every bar the plan names has a real carrier, the
counts are pinned against silent drift, both blind-spot defects found during W9 — trace conflation
and invariant 4 — are fixed and fenced rather than annotated, and the refusal surface is no longer
an English dead end that discards the evidence it honestly read. A refusal now preserves its
evidence, names a real destination when one genuinely follows from what was read, and says so in the
learner's own language on both hosts.

**P5/P6 closed while this document was being reconciled** (§9). A refusal announced itself and a
silent repair did not; now both do. That was the last engineering reason to withhold approval, and
it is gone — but its removal is not the approval. Condition (a) is marked approved by the review
that reads this document, on the strength of a full green run recorded at the bottom of §1.1, and
not by the absence of open items. The distinction matters here for the same reason §0 exists:
"nothing is red" and "this was reviewed" are different claims, and only one of them is evidence.

**None of that is condition (b).** Local green proves the synthetic half only. It says nothing about
whether the twelve foundation invariants read zero across real traffic in a production window, and
it cannot: no window has been chosen, no revision has been deployed with `Grounding=Enforce`, and no
traffic has elapsed. Reporting §1 as if it closed the gate is the single failure mode §0 exists to
prevent.

---

## 8. W9 Learning Value Gate — refusal objective/path matrix

**Status: Zoe's sign-off is recorded for the refusal surface.** Every refusal blocker raised during
the W9 review — L1 through L6 — is closed, LVG-W9-8 is closed, and each closure below names the test
that holds it. The sign-off record is `.squad/decisions/inbox/wash-w9-typed-refusal.md`.

**P5/P6, the repair disclosure, was the last open path and closed during this pass.** A refusal
announced itself; an answer the grounding layer *rewrote and shipped anyway* did not. That is a
different surface from the one Zoe signed, and it is recorded separately in **§9**, now closed.
Condition (a) is still recorded as **not approved** pending the final review — see §7.

The Learning Value Gate asks one question of any learner-facing change: **does the learner end the
turn with more target-language contact than they started it with?** A refusal is the hardest case,
because the honest thing to do — declining to assert something — is also the thing most likely to
end the session.

**Objective.** A refusal must be *a redirection, not a dead end*. Concretely, a refused turn must
preserve the evidence that was legitimately read, and must offer a real retrieval or destination
path the learner can actually take, in their own language, on either host, reachable by assistive
technology, and without leaking the answer the refusal exists to protect.

### 8.1 The path matrix

Every cell must hold. A cell that cannot be reached is a gap, not a pass.

| # | Path | Requirement | Held by |
| --- | --- | --- | --- |
| P1 | Refusal, `en`, web host | Evidence preserved; typed destination present and resolvable; no answer leak | `An_Enforce_refusal_preserves_the_turns_real_evidence`, `L3_a_refusal_names_a_real_screen_the_learner_can_go_to` |
| P2 | Refusal, `ko`, web host | As P1, and the refusal copy is Korean | `L1_the_refusal_copy_resolves_per_language`, `A_korean_learner_reads_no_english_in_the_refusal` |
| P3 | Refusal, `en`, MAUI Blazor Hybrid host | As P1, same destination resolves on the device host | `Both_hosts_render_a_no_read_refusal_identically` |
| P4 | Refusal, `ko`, MAUI Blazor Hybrid host | As P2 and P3 together | `The_no_evidence_copy_reads_in_both_languages`, `Both_hosts_render_a_no_read_refusal_identically` |
| P5 | Substituted (repaired) answer, `en`, both hosts | Learner is told the answer was altered; announcement is polite, count-free, and not a compliance notice | `An_english_answer_the_layer_rewrote_says_so` (wire), `An_altered_answer_says_so_and_announces_it_politely`, `The_notice_is_a_polite_status_with_a_name` (§9) |
| P6 | Substituted answer, `ko`, both hosts | As P5, in Korean, or repair is suppressed *and the suppression is disclosed* | `A_korean_learner_reads_the_disclosure_in_korean`, `A_suppressed_repair_says_the_wording_was_left_alone` (§9); the Korean *refusal* half stays with `L4_korean_disclosure_suppresses_withheld_and_korean_silence_still_fires` |
| P7 | Any of P1–P6, assistive technology | Refusal, offer and announcement are all exposed to a screen reader, not conveyed by styling alone | `The_refusal_region_is_a_polite_status_not_an_alert` |
| P8 | Any of P1–P6, answer-leak check | Neither the refusal, the offer, the announcement, nor any alt/aria/title text restates the withheld content | `The_refusal_leaks_no_terms_or_learner_text`, `A_no_read_refusal_leaks_no_learner_content`, `No_evidence_string_carries_a_term_a_gloss_or_an_example` |

### 8.2 Blocker register — **L1–L6 all closed**

Each row records what was wrong, what changed, and the test that will fail if it regresses. The
register is executable: `Every_closed_blocker_in_the_register_names_tests_that_exist` (tier 1)
resolves every name below against the suite that owns it, so a renamed or deleted closure turns this
table red rather than quietly making it fiction. `The_register_scan_can_fail` is its non-vacuity
control.

> **L6 absorbs former L7.** The earlier draft listed six closure tests plus a seventh for assistive
> technology. The signed register is L1–L6, with L6 defined as *refusal-artifact safety and
> reachability* — it carries both the answer-leak bar (P8) and the assistive-technology bar (P7).
> Nothing was dropped; the two were merged because a single artifact scan covers both, and the merge
> is recorded here so this register stays auditable against the older numbering.

| # | Blocker as found | Closed by | Tests |
| --- | --- | --- | --- |
| L1 | **Refusal copy was hardcoded English.** `ReviewAnswerRefusal` and both offers were `const string` English literals with no language parameter and no resource key | The refusal ships a typed `CoachLimitationDto` with no string-typed member at all. Every learner-visible sentence resolves from the client resource files | `An_Enforce_refusal_carries_a_typed_limitation_and_no_server_prose`, `The_refusal_payload_carries_no_prose_in_any_language`, `L1_the_refusal_copy_resolves_per_language`, `Every_learner_visible_string_on_the_card_comes_from_the_client_resx`, `The_shipped_limitation_carries_no_free_text` |
| L2 | **Evidence was discarded on refusal.** The rows honestly read were thrown away with the sentence that was not | The refusal branch builds real evidence instead of an empty list | `An_Enforce_refusal_preserves_the_turns_real_evidence`, `L2_a_refused_turn_still_shows_the_evidence_it_read` |
| L3 | **No typed destination.** The offers were prose; nothing named a route, so no client could turn a refusal into a retrieval | `CoachRefusalLimitationProjection.DestinationFor` maps each definition to one of the six bound routes, or to `null` where no real screen exists. It never points at the nearest thing | `Every_definition_code_has_a_decided_mapping`, `No_destination_names_a_route_outside_the_six`, `L3_a_refusal_names_a_real_screen_the_learner_can_go_to`, `An_unknown_route_is_dropped_rather_than_guessed` |
| L4 | **Korean `WithheldNotDisclosed` disclosure regex was English-only.** A Korean answer that *did* disclose withholding could not match seven English verbs, so the rule fired on an honest answer | Disclosure is now structural: a visible evidence item carrying a withheld count *and* a known reason is the disclosure, because the client renders that pair in the learner's language | `L4_korean_disclosure_suppresses_withheld_and_korean_silence_still_fires` (tier 1, this suite — four halves, including the answer that must still fire) |
| L5 | **No polite announcement of withholding.** Withheld spans were dropped from the answer silently | The withheld pair is rendered as a polite status region, in the learner's language | `L5_a_withheld_answer_announces_that_it_was_withheld`, `The_refusal_region_is_a_polite_status_not_an_alert` |
| L6 | **Refusal-artifact safety and reachability** (absorbs former L7). No scan proved the refusal, its offer, its announcement or any alt/aria/title text failed to restate the withheld content, nor that any of it reached assistive technology | Artifact scans over every learner-visible string, plus the polite-status requirement | `The_refusal_leaks_no_terms_or_learner_text`, `The_pair_carries_no_text_of_any_kind`, `A_no_read_refusal_leaks_no_learner_content`, `No_evidence_string_carries_a_term_a_gloss_or_an_example`, `The_refusal_region_is_a_polite_status_not_an_alert` |

> **What L5 does and does not cover.** L5 closes the announcement on the surface Zoe reviewed: a
> turn where content was *withheld* and the learner is shown the withheld pair. It does not cover
> the turn where the grounding layer *rewrote* a claim and shipped the answer anyway — the learner
> sees a fluent, altered answer with nothing marking it as altered. That is P5/P6, closed separately
> in §9. The distinction matters because the second case is the one where the learner had no signal
> at all that anything happened, and because Zoe signed the refusal register and did not sign the
> repair surface — the two are kept apart so neither borrows the other's approval.

### 8.3 LVG-W9-8 — closed

LVG-W9-8 was raised separately during the review: the refusal surface had to be honest not only in
the turn that produced it, but across the turns and reloads around it. Four parts, all closed.

| Part | What it requires | Tests |
| --- | --- | --- |
| **Turn-scoped evidence** | A refusal shows the evidence *this* turn read, and none of the rows an earlier answered turn left on screen. The list takes its rows from the caller, never from workspace state | `A_refusal_after_an_answered_turn_shows_none_of_the_earlier_rows`, `The_evidence_list_takes_its_rows_from_the_caller_and_never_from_the_workspace` |
| **No-evidence copy** | A turn that read nothing says so, in both languages, identically on both hosts, and never promises evidence that is not there. Coverage is `Unknown`, and no count and no destination are rendered | `A_no_read_refusal_never_promises_evidence_that_is_not_there`, `The_no_evidence_copy_reads_in_both_languages`, `Both_hosts_render_a_no_read_refusal_identically`, `A_refusal_that_read_nothing_states_no_coverage_and_no_number` (tier 1, this suite) |
| **Persisted coherent withheld pair** | The withheld count and reason are all-or-nothing. `None` and `Unknown` are not reasons. Two reads produce nothing rather than a sum even when the reason matches, because no read computed a union and the sets may overlap. One incoherent read poisons the whole turn's picture. The pair survives the protected outcome round trip | `One_read_that_held_rows_back_states_the_exact_count_and_reason`, `Two_reads_with_the_same_reason_still_state_nothing`, `One_incoherent_read_poisons_the_whole_turns_withheld_picture`, `The_pair_survives_a_protected_outcome_round_trip`, `The_stored_limitation_round_trips_exactly_at_version_three` |
| **Latest-only restore** | A resumed conversation shows the *latest* completed outcome's limitation and nothing older. Lookback is one row. An unreadable or unknown-version latest row yields null rather than revealing an older refusal. A refusal never crosses conversations or learners | `A_later_normal_turn_clears_the_refusal_on_reload`, `An_unreadable_latest_outcome_fails_closed_rather_than_revealing_an_older_refusal`, `The_session_read_is_the_only_caller_and_the_lookback_is_one`, `A_refusal_does_not_cross_between_two_conversations_of_one_learner`, `A_refusal_does_not_cross_between_two_learners` |

Two real defects surfaced while closing this item and are fixed in the same change: recent outcomes
were ordered by a fencing version that ties at one across operations and are now ordered by
completion time, and an open dispute was being resolved on a refused turn the learner never saw.

### 8.4 Re-arm conditions

**§16.3 substitution take-up.** No longer gated on the product — the substitute is wired (§3.6). It
re-arms when an **instrument pair** exists: one event when a refusal ships carrying a non-null
destination, and one when a learner follows it. Until both exist the metric is `inactive / not
measurable` and must never be reported as a zero.

**P5/P6 repair disclosure.** Closed during this pass (§9). It re-opens on any change to the
disclosure enum, the projection that sets it, or the notice component that renders it — the notice
is the only reader, so a rename on either side is a silent regression rather than a build break.

**This register.** Any change to the refusal surface re-opens it. The register test resolves every
name above on each run, so a closure that is renamed away is caught at the gate rather than at the
next review. §9 is deliberately *not* in the register: the register holds closed items with landed
tests, and folding an open item into it would make the scan fail for the wrong reason.

---

## 9. P5/P6 repair disclosure — closed

**Status: closed during this pass.** This was the last Learning Value Gate path not held. It closed
while this document was being reconciled, so it is recorded here in full rather than folded into §8:
Zoe signed the *refusal* register, and did not sign the repair surface. Keeping them apart stops one
borrowing the other's approval.

**The defect.** At `Repair`, the grounding layer can rewrite a claim it could not support and ship
the corrected answer. The answer that arrived was fluent, confident, and visually identical to one
the layer never touched, so the learner had no way to know the assistant had revised itself.
Pedagogically that is the inverse of the refusal case: a refusal is honest and risks ending the
session, while a silent repair keeps the session going by concealing that the first attempt was
wrong — and the learner loses the correction, which is the part with the teaching value in it.

### 9.1 How it was found and closed

The wire half landed first: `CoachRepairDisclosure` rides on `CoachTurnResponse` and
`CoachSessionResponse` as a nullable, closed enum with a `SafeZero` fallback, projected once and only
on the ship branch, absent on refusal, neutral when defaulted, and carrying no text. It is covered by
`tests/SentenceStudio.Api.Tests/Coach/Claims/CoachRepairDisclosureTests.cs`. The server could say
what it did; nothing asked it.

That gap was pinned by a tier-1 scan,
`The_repair_disclosure_is_on_the_wire_but_not_yet_on_the_learners_screen`, which swept the whole UI
source tree and both resource sets rather than naming a type or member. That shape was deliberate:
the four superseded tripwires described in §8.2 all stayed green through their own fix because they
asserted absence at a seam the fix did not use. A whole-tree scan has no such seam to miss. It went
red within minutes of the client wiring landing, and was then correctly deleted in favour of the real
closure suite below. **A tripwire that survives the fix it was written for is worse than no
tripwire** — this one could not, and did not.

### 9.2 What landed

`src/SentenceStudio.UI/Shared/Coach/CoachRepairDisclosureNotice.razor`, read by
`CoachChatPane.razor` through `Services/CoachWorkspaceState.cs`. One closed state in, one localized
sentence out — no count, no rule code, no span, no server prose. Strings resolve from
`AppResources.resx` / `AppResources.ko.resx` (`Coach_Repair_AnswerAltered`,
`Coach_Repair_SuppressedForLanguage`, `Coach_Repair_Unknown`, `Coach_Repair_RegionLabel`). The
component takes no base URI, no navigation manager and no platform flag, so the two hosts cannot
drift.

Three behaviours are worth stating because they are easy to get backwards:

- **`None` and `null` are silent, and are not the same thing.** `None` means checked and clean;
  `null` means not checked at all. Neither is news to a learner, and a notice that renders on every
  ordinary turn is a notice nobody reads.
- **`Unknown` is *not* silent.** A state this build cannot name renders a neutral note saying only
  that it cannot describe what happened, never that anything changed. This is the one branch where
  silence is the dangerous option: one of the two real states means part of the answer was
  rewritten, so staying quiet would hide a rewrite behind a version gap.
- **Never beside a refusal.** A refused turn produced no answer, so there is nothing to disclose
  about; the server sends null and the workspace suppresses it independently.

### 9.3 The closure tests

`tests/SentenceStudio.UI.Tests/Coach/CoachRepairDisclosureWiringTests.cs` — 18 cases, all green.

| # | Required | Tests |
| --- | --- | --- |
| R1 | An announcement on a repaired turn, `en`, polite and count-free | `An_altered_answer_says_so_and_announces_it_politely`, `The_notice_carries_no_counts_rule_codes_or_learner_content` |
| R2 | The same announcement in `ko`, no English reaching a Korean learner | `A_korean_learner_reads_the_disclosure_in_korean`, `The_korean_neutral_note_carries_no_english` |
| R3 | Both hosts identical | `Both_hosts_render_the_disclosure_identically`, `The_neutral_note_is_a_polite_status_with_a_name_in_both_hosts` |
| R4 | Exposed to assistive technology as a polite status, not by styling alone | `The_notice_is_a_polite_status_with_a_name` |
| R5 | No answer leak — the notice restates none of the content the repair removed | `The_notice_carries_no_counts_rule_codes_or_learner_content` |
| R6 | Suppression is disclosed — the P6 half | `A_suppressed_repair_says_the_wording_was_left_alone` |

Beyond R1–R6, the suite holds the silence and lifetime rules that make the notice trustworthy:
`A_clean_or_unchecked_answer_says_nothing`, `A_clean_turn_shows_no_disclosure_in_either_language`,
`A_refused_turn_shows_the_limitation_and_no_disclosure`, `The_next_ordinary_turn_clears_the_disclosure`,
`A_disclosure_is_restored_on_resume`, `A_session_whose_latest_turn_disclosed_nothing_restores_nothing`,
`An_undescribable_state_gets_a_neutral_note_rather_than_silence`,
`An_undescribable_state_never_claims_the_answer_changed`,
`A_future_state_off_the_wire_collapses_to_the_neutral_note`, and
`An_old_client_payload_without_the_property_renders_nothing`.

> **Observation, not owned here.** The header comment block in `CoachRepairDisclosureNotice.razor`
> and the `Disclosure` parameter summary both still read "Unknown renders nothing", which the `Key`
> switch and `An_undescribable_state_gets_a_neutral_note_rather_than_silence` contradict. The code
> and the tests agree with each other; only the two prose comments are stale. Flagged for the owning
> agent rather than edited here.

**This section does not by itself approve condition (a).** It removes the last engineering reason to
withhold approval. The approval is the review's to give — see §7.
