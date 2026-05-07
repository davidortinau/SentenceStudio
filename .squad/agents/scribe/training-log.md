# Scribe — Skill Training Log

Append-only log of training decisions made on skills in this repo (and adjacent user-global skills). Each entry follows the template from skill-trainer-knowledge: assessment → hypothesis → change → validation → rationale.

---

## 2026-05-07 — Session: NumberDrill Phase 1 Publishes #5–#9 retrospective

**Trainer:** SkillTrainer (delegated by Coordinator)
**Trigger:** Captain requested skill review after 3 of 5 publishes were rejected on visual inspection AFTER automated validation passed.
**Source assessment:** `.squad/decisions/inbox/skill-trainer-recent-review.md`
**Captain approval:** All 5 findings + index update.

---

### Entry 1 — `maui-ios-dx24-install`: bump confidence + preemptive procedure

- **Assessment:** Skill existed but said "observed once" with `Status: ⚠️ Medium confidence`. NWError 57 has now recurred on publishes #6, #7, #8, #9 — same root cause (deep-sleep tunnel teardown), same fix (wake + retry once). Recipe was reactive ("if you see NWError 57, do X") rather than preemptive ("expect 1 retry, here's how to minimize").
- **Hypothesis:** Bumping status to ✅ High confidence and adding a preemptive procedure will move the recipe from "diagnose-after-failure" to "expect-and-budget" — saving ~60s + Captain attention each publish.
- **Change:** 3 edits to `.squad/skills/maui-ios-dx24-install/SKILL.md`:
  1. Status line: `⚠️ Medium confidence (observed once...)` → `✅ High confidence (observed 4+ times across publishes #6–#9, 2026-05-06 / 2026-05-07)`
  2. Replaced single "Evidence (Publish #6)" with a "Preemptive Procedure" section + multi-publish evidence list (#6, #7, #8, #9).
  3. Demoted validated items 1 and 2 in "Future Investigations".
- **Cross-links added:** `.claude/skills/maui-ai-debugging/SKILL.md` (new "iOS Device Install on DX24 (SentenceStudio-specific)" subsection); `docs/deploy-runbook.md` (callout at Step 2c).
- **Validation:** No eval run — this is a recipe correction, not a model-behavior test. Measurable success = next publish does not surprise on NWError 57. Pre-applied; observe organically.
- **Rationale:** Per `AGENTS.md` "if it blocks a Squad member twice, treat it as a bug" — this has blocked 4 publishes. Confidence bump is overdue. Cross-links fix the discoverability gap (the squad-local skill was invisible to general-purpose publish agents).

---

### Entry 2 — NEW skill: `blazor-activity-layout-shell`

- **Assessment:** No skill covered Blazor activity page layout. Three publishes (#7, #8, #9) were rejected for layout defects against VocabQuiz, the canonical reference. Each rebuild from scratch produced a DIFFERENT defect: footer not pinned, card wrapper, empty input-bar div as chrome strip. Pattern: agents are not converging on the canonical VocabQuiz shell.
- **Hypothesis:** A skill that says "VocabQuiz is canonical; copy verbatim; only swap inner content" + documents the 4 observed anti-patterns will eliminate the rebuild-from-scratch failure mode.
- **Change:** Created `.squad/skills/blazor-activity-layout-shell/SKILL.md` per `.squad/templates/skill.md` format:
  - Frontmatter: `domain: blazor-hybrid-ui, confidence: medium, source: earned (publishes #5–#9)`
  - Patterns section: canonical razor template, CSS contract table, decision tree.
  - 4 anti-patterns documented with code examples and root causes:
    1. Empty `.activity-input-bar` div (chrome strip).
    2. Outer card wrapper (breaks flex chain).
    3. Footer not pinned / safe-area mishandled.
    4. Inventing a new wrapper class.
  - Examples cite `VocabQuiz.razor` (canonical) + `NumberDrill.razor` (now-correct).
- **Validation:** No eval run yet. Validation = next new activity page (Phase 2 of NumberDrill or similar) ships without footer/chrome rejection on first publish.
- **Rationale:** Three failures of the same shape = explicit skill. Anti-pattern #1 (empty div) is the most subtle and most worth capturing — the CSS class name `.activity-input-bar` describes purpose, not behavior, so the "empty div is harmless" assumption is wrong.
- **Future eval idea:** Test "given a request to build a new activity page, does the model copy VocabQuiz's shell or invent its own?" — but defer until we have a concrete request to use as an eval scenario.

---

### Entry 3 — `maui-visual-review`: broaden triggers

- **Assessment:** Skill existed at user-global level (`~/.copilot/skills/maui-visual-review/`) and was almost a perfect fit for the Publish #9 visual-diagnosis case (Captain provided IMG_4275 NumberDrill vs IMG_4276 VocabQuiz, asked which was wrong). Coordinator did NOT invoke it because trigger phrasing (line 8: "compare to design", "screenshot vs design") was keyed to design-vs-implementation, not implementation-vs-implementation.
- **Hypothesis:** Adding triggers like "layout parity", "spot the difference", "compare two pages" will route the skill into Coordinator's choice set for the implementation-vs-implementation case. Workflow doesn't need to change — the comparison logic is identical, only the framing differs.
- **Change:** 2 edits to `~/.copilot/skills/maui-visual-review/SKILL.md`:
  1. Frontmatter `description`: broadened opening line ("Compare a design reference image" → "Compare two images — a target reference (design mockup OR a screenshot of an existing reference page/implementation)") and added 8 new trigger phrases.
  2. "When to Use" section: added bullet for "two implementation screenshots side by side" + a callout note explaining the implementation-vs-implementation case treats the canonical as the design reference.
- **Validation:** No eval run (Captain explicitly deferred). The eval would test "given two app screenshots and 'why are these different?', does the model invoke `maui-visual-review`?" — currently no, expected yes after edit. **Recommended Arena eval candidate** once one publish has happened with the new triggers in place to confirm organic invocation.
- **Rationale:** Most surgical possible change. Did not restructure workflow. Did not invent a new sibling skill. Just broadened the doorway.

---

### Entry 4 — CSS comment: empty-div chrome trap

- **Assessment:** Not a skill edit per se, but a code-level safety net for anti-pattern #1 from Entry 2. The CSS class `.activity-input-bar` paints visible chrome unconditionally (border-top + safe-area-inset-bottom = ~50px on iPhone), so an empty div renders. This is a naming bug (purpose vs behavior mismatch) but renaming was deferred.
- **Hypothesis:** A warning comment in the CSS file will catch the next agent who reads `app.css` looking for what the class does.
- **Change:** Added a multi-line comment block in `src/SentenceStudio.UI/wwwroot/css/app.css` immediately above the `.activity-input-bar` selector, citing the Publish #9 incident and pointing at `.squad/skills/blazor-activity-layout-shell/SKILL.md` anti-pattern #1.
- **Validation:** Passive — caught by future code review or by an agent reading the CSS file.
- **Rationale:** Belt-and-suspenders with the new layout-shell skill. Two trip-wires (skill + CSS comment) for a defect that recurred 3 times.

---

### Entry 5 — NEW skill: `agent-progress-diagnostic`

- **Assessment:** No skill covered "is this background agent hung?" diagnosis. Wash Publish #9 looked stuck at ~83 tool calls; Coordinator improvised a `write_agent` status ping which immediately confirmed Wash was alive (writing the inbox decision file). Improvisation worked but was guessed correctly. n=1.
- **Hypothesis:** A short, ordered rubric (envelope check → status ping → filesystem check → log check) will give Coordinator a defensible diagnostic procedure instead of an anxiety-driven kill reflex.
- **Change:** Created `.squad/skills/agent-progress-diagnostic/SKILL.md` per `.squad/templates/skill.md`:
  - Frontmatter: `domain: squad-orchestration, confidence: low, source: observed (Wash publish #9, n=1)`
  - Tool-call envelope table per agent role.
  - 5-step diagnostic rubric.
  - 4 anti-patterns (premature kill, escalation without ping, wrong-envelope comparison, killing during inbox write).
  - Wash Publish #9 documented as the founding case in Examples.
- **Validation:** None yet (n=1, can't validate the rubric on a single case). Confidence intentionally set to `low` in frontmatter. Upgrade to `medium` after 3+ observations across different agent roles.
- **Rationale:** Captain explicitly flagged the confusion. Encoding the rubric now (even at low confidence) gives Coordinator a starting point and creates a place to add observations as they accrue. Worse to leave undocumented and re-improvise next time.

---

### Entry 6 — Cross-cutting: `available-copilot-skills` index

- **Assessment:** The 3-tier skill ecosystem (`~/.copilot/skills/`, `.claude/skills/`, `.squad/skills/`) doesn't cross-reference. The `available-copilot-skills` index listed user-global skills only, so squad-local ones were invisible to publish agents. This is the structural cause of why `maui-ios-dx24-install` existed but didn't get used.
- **Hypothesis:** Adding a "Squad-local skills" section to the index makes them discoverable. Agents that load the index now see all three tiers.
- **Change:** Edited `.squad/skills/available-copilot-skills/SKILL.md` — added a new "Squad-local skills (project-specific, in `.squad/skills/`)" section listing all 30+ skills currently in `.squad/skills/`, each with a one-line "use for" description. Includes the 2 newly-created skills (entries 2 and 5) and the existing `maui-ios-dx24-install`.
- **Validation:** Next time an agent is spawned with `available-copilot-skills` referenced, it should see and recognize squad-local skills.
- **Rationale:** Lowest-cost highest-leverage edit. Without this, all the other fixes have a discoverability problem.

---

## Summary stats

- **Sessions:** 1 (this retrospective)
- **Skills edited:** 4 (`maui-ios-dx24-install`, `maui-ai-debugging`, `maui-visual-review`, `available-copilot-skills`)
- **Skills created:** 2 (`blazor-activity-layout-shell`, `agent-progress-diagnostic`)
- **Code/doc files edited:** 2 (`docs/deploy-runbook.md`, `src/SentenceStudio.UI/wwwroot/css/app.css`)
- **Arena evals filed:** 0 (per Captain's constraint — ship + observe)
- **Pending eval candidates:** 1 (`maui-visual-review` trigger broadening — recommended after next visual-diff scenario)

## Next training session prompts

- After next publish: validate Entry 1 (DX24 retry didn't surprise) and Entry 5 (rubric was applied if anyone wondered "is X hung?").
- After next new activity page: validate Entry 2 (canonical shell was used; zero footer/chrome rejections).
- After next "compare these screenshots" scenario: validate Entry 3 (`maui-visual-review` was invoked organically).
