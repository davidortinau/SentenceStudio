# W7 Learning Value Gate — refusals, alternatives and the hint ladder

Gate artifact for `AC-S15` and `AC-S16`, following `.squad/skills/learning-value-gate/SKILL.md`.
Scope is the W7 limitation surface: `CoachLimitationDto` and `CoachLimitationCard`.

## 1. Learning objective

**S16 keeps a stuck learner producing the target language from memory by lowering the retrieval cue
rather than supplying the answer; S15 has no learning objective, because refusing to delete data is
not a learning activity.**

## 2. Reachable path matrix

The gate's usual axes are direction × prompt modality × response modality. This surface has no
prompt and no response of its own — it renders beside an activity — so the reachable paths are the
states the card can put a learner in, and the question for each is whether it removes, preserves or
is orthogonal to the target-language retrieval the activity was already asking for.

### S15 — refusal to make a large or destructive change

| # | State | Learner sees | SLA action | Where L2 retrieval happens |
|---|---|---|---|---|
| S15.1 | Refusal, counted | Reason, count, consequence | — | None. No L2 path |
| S15.2 | Refusal, uncounted or zero | Reason, consequence; **no count line** | — | None. No L2 path |
| S15.3 | Refusal + destination | Reason, screen name, its side effect | — | None. No L2 path |
| S15.4 | Refusal + alternatives | Reason, up to three reversible alternatives | — | None. No L2 path |
| S15.5 | Unknown code / route / effect | Neutral heading; destination dropped; "consequences not stated" | — | None. No L2 path |

**S15 is LVG green with no L2 path, and that is the correct outcome, not an exemption.** The gate
blocks states where a learner *believes they are practising* but no target-language retrieval
occurs. S15 makes no such claim: it is account and data management, the learner is not mid-retrieval,
and no row presents itself as practice. A row here would only fail the gate if it *replaced* a
retrieval opportunity with a non-retrieval one, and none does — S15 is reached by asking Sam to
change data, never by asking for help with a word.

### S16 — hint ladder and shorter session

| # | State | Learner sees | SLA action | Where L2 retrieval happens |
|---|---|---|---|---|
| S16.1 | Rung 1, `Category` | "What kind of word it is" | Recall, narrowed | Learner still produces the L2 form; the cue removes a semantic class, not the token |
| S16.2 | Rung 2, `Cloze` | "The sentence with it missing" | Recall in context | Learner produces the L2 token into an L2 frame — the frame is itself L2 input, and none of the form is given |
| S16.3 | Rung 3, `FormCue` | "How it starts and how long it is" | Recall, narrowed | Learner still produces the L2 form; the cue gives orthographic shape, not the word |
| S16.4 | Unknown rung | "Not available" | — | Nothing offered; no fallback to a neighbour |
| S16.5 | Shorter session, `PreservesRetrieval` true | "Shorter set today: 5" + "Still full practice, just fewer words." | Unchanged from the activity | Unchanged. Fewer items, same retrieval per item |
| S16.6 | Shorter session, `PreservesRetrieval` false | Offer without the retrieval reassurance | Unchanged from the activity | Unchanged; the card simply does not claim otherwise |

**Every rung remains retrieval.** No rung supplies the target term, its gloss, or a translation. The
ladder narrows the search space and never crosses into supplying the answer.

**The order is pedagogical, and it is `Category` → `Cloze` → `FormCue`.** The ladder ascends in how
much of the written *form* it discloses: a category names none of it, a cloze supplies surrounding
context and none of it, and a form cue supplies part of the form itself. That is why the form cue is
rung 3 and not rung 2 — in Korean an initial syllable block plus a length is very nearly the answer,
so it is the most form-revealing help on offer rather than a gentle nudge. **There is deliberately no
rung 4**, because the only step above the most form-revealing rung is the term itself, which is the
thing S16 refuses. S16.4 refuses rather than falling back to a neighbour for the same reason: an
unrecognised rung of unknown support must not be rendered as a known one.

**The order is bound by test, not by convention.**
`CoachLimitationWiringContractTests.The_rung_order_in_the_acceptance_cases_matches_the_shipped_hint_ladder`
parses the English rung row in §35.3 and the Korean rung row in §35.4 and requires both to appear in
the same order as `CoachLimitations.HintLadder`. A transposition in this matrix, in either acceptance
row, or in the shipped ladder fails the build rather than shipping a ladder that discloses the form
one rung early.

**The shorter session changes quantity, not answer availability.** It reduces item count. It does
not switch direction, does not convert production into recognition, and does not make any answer
visible. "Fewer words, same kind of practice" is a claim the card is allowed to make only because
nothing in the offer alters the retrieval demand per item.

## 3. Default path

A first-time learner with no preference overrides **never reaches either state**. Both are
server-emitted responses to a specific request: S15 to a destructive or oversized change, S16 to
asking for the answer mid-activity. There is no default, no toggle and no setting that lands a
learner here, and no empty state to trace — the card renders nothing at all when `Limitation` is
null.

Beyond that, **no production screen mounts `CoachLimitationCard` today.** W7 ships the contract and
the renderer only; the hint-delivery stage has not shipped, so no rung can be executed even if it
were offered. `CoachLimitationWiringContractTests` fails the build if a production caller appears
before that stage.

## 4. Answer-leakage checklist

Checklist items are the gate's, answered for this surface.

- [x] **Prompt title/heading key does not embed the answer form.** No heading on this card names a
      term. Headings are fixed resource keys (`Coach_Limitation_HintLadderHeading` and siblings) and
      carry no interpolated content.
- [x] **`alt` / `aria-label` on prompt image do not name the answer.** The card renders no image. Its
      only accessible names are `aria-labelledby` references to the refusal sentence and the two list
      headings — all fixed resource strings, all identical to the visible text.
- [x] **Prompt audio matches prompt direction, never the answer side.** The card emits no audio and
      has no audio hook.
- [x] **Cached audio filename / TTS URL doesn't expose the answer term.** Not applicable; no audio.
- [x] **MC distractor generation cannot include the correct answer.** Not applicable; the card offers
      no choices. Its lists are closed enum members (`CoachAlternativeCode`, `CoachHintKind`), not
      generated content.

Two additions specific to this surface:

- [x] **`data-` attributes carry codes, never content.** `data-coach-limitation-hint` carries a
      `CoachHintKind`; `data-coach-limitation-rung` carries an integer. A leak here would be invisible
      to every visual check, which is why `SAM-LIM-16a` sweeps the DOM explicitly.
- [x] **Route parameters carry no term.** Destinations are a closed `CoachRouteName` plus typed
      parameters; an unresolvable route is dropped rather than rendered.

**Structural backstop:** `CoachLimitationDto` has no term, gloss, example or query field. There is no
content on this wire to leak. Any future failure of this checklist is a contract change and must be
reviewed as one.

## 5. Acceptance cases

`.claude/skills/e2e-testing/references/learning-coach.md` §35:

- §35.1 `SAM-LIM-15` (`AC-S15`) — refusal proposes nothing destructive; counts, coverage, routes.
- §35.2 `SAM-LIM-16a` (`AC-S16a`) — answer-leakage sweep across five channels.
- §35.3 `SAM-LIM-16b` (`AC-S16b`) — three rungs without a lecture; shorter session preserves retrieval.
- §35.4 `SAM-LIM-17` — Korean, both hosts, screen-reader path.
- §35.5 — the automated coverage backing all of the above.

## 6. Sentence ownership

The **client resx owns every learner-visible W7 sentence.** `CoachLimitationCard` renders only
`Coach_*` resource lookups; `CoachLimitationWiringContractTests` fails if a literal appears in its
markup.

`CoachDeterministicCopy` holds server-side English for the same concepts, and it is **not
learner-visible at the shipped grounding stage.** `CoachOptions.Stage` ships defaulted to
`CoachGroundingStage.Off` (no scan at all); the ladder's own remarks describe W6 promoting
production to `Observe`, which scans and records but never alters the answer. Either way,
**substitution begins only at `Repair`**, so no string from that class currently reaches a learner.

Promotion to `Repair` must not happen until those strings have a localization path, because they
are `const string` English with no resource lookup and no Korean. That constraint is pinned by
`CoachLimitationWiringContractTests.Server_authored_copy_only_reaches_a_learner_at_Repair_or_above`,
which asserts both the ladder's semantics and that the configured default is below `Repair`.
