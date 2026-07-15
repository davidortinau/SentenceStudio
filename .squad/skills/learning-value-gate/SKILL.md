---
name: "learning-value-gate"
description: "Blocking checklist for any learning activity change. Ensures every prompt/response combination produces target-language learning value. Earned from the 2026-07-15 Vocab Quiz photo-hide-text miss."
domain: "product-pedagogy"
confidence: "high"
source: "earned (Vocab Quiz photo-hide-text incident, 2026-07-15 — Captain directive)"
owner: "Zoe (Lead) — must be applied before merging any activity UX change"
---

## When to Use This Skill

**Trigger this skill whenever any of the following occur.** Route to Zoe for review; product/scope discussions may spawn River (AI prompts) or Kaylee (UI) but the gate must be signed off before commit.

- A new learning activity page is added under `src/SentenceStudio.UI/Pages/` or a new MauiReactor activity page under `src/SentenceStudio.AppLib/`.
- An existing activity gains a new **mode**, **direction**, **prompt modality** (text / audio / photo / mnemonic), or **response modality** (MC / text / speech / matching).
- An existing activity gains a **preference or toggle** that changes what the learner sees or how they answer (e.g., "show/hide text", "use photo prompt", "swap direction", "audio-only").
- A **default value** for any of the above changes, or a **new empty-state** appears (e.g., first-run behavior when no user preference is set).
- A grader change alters what counts as a correct answer (e.g., accepting native-language input for a target-language slot).
- An SRS or plan builder change causes items to be surfaced in a new modality.

**Trigger phrases** — coordinator should route on any of these:
`"add mode"`, `"show/hide"`, `"prompt direction"`, `"native to target"`, `"target to native"`, `"photo prompt"`, `"audio prompt"`, `"toggle text"`, `"mnemonic image"`, `"empty state"`, `"default when"`, `"new activity"`, `"activity picker"`, `"input mode"`, `"response mode"`.

## The Rule (non-negotiable)

**SentenceStudio exists to teach a target language. Every state the learner can reach in every activity must produce measurable target-language exposure, retrieval, or production.**

Concretely, in every reachable (direction × prompt-modality × response-modality × toggle) combination:

1. **Either the prompt or the response** must be in the target language. Both being native-language, or neither being linguistic (photo-only prompt → native-only choices), is **pedagogically empty** and must be blocked.
2. **The target-language artifact must not be reducible to guessing from context alone.** A photo + native options with no target text anywhere on screen is a picture-matching game, not language learning.
3. **When the prompt is in the target language, target-language text may not be hidden by any toggle.** The target-language form is the whole point of that direction. Hiding it strips the learning value.
4. **When the prompt is in the native language, hiding the native prompt is permitted** *only if* another target-language cue (photo → produce target word, or audio in target) is present AND the response set is in the target language. Hiding the native prompt while the response set is also native is empty-state (see #1).
5. **Answer-leakage review** — no prompt-side asset (text, aria-label, audio, alt text, tooltip, cache filename) may reveal the answer. Cross-check localization keys, `GetPromptAudioText`, image `alt`, and header/title strings.

If a combination cannot satisfy #1–#4, **it must be unreachable** — hide the toggle, disable it with a tooltip, or gate the picker. Do not ship a reachable dead-language state.

## Required Artifacts Before Merge

The author must attach the following to the decision inbox note (or PR description if a PR is opened for @copilot work) **before Zoe will approve**. Zoe blocks merges without all six.

### 1. Explicit learning objective

One sentence. Example: *"Given a native-language cue (photo + English gloss suppressed), learner produces the target-language form from a set of target-language options — recall of form given meaning."*

Not acceptable: *"users can hide the text"*, *"cleaner UI"*, *"parity with X"*. Those describe the mechanism, not the learning outcome.

### 2. Language-role matrix

A table covering **every reachable combination**. Any combination that resolves to `native → native` (row 1 below, marked ❌) must be marked **unreachable in code** and the mechanism cited.

| Direction | Prompt Text | Prompt Audio | Prompt Photo | Response Set | L2 Exposure? | L2 Retrieval? | Verdict |
|-----------|-------------|--------------|--------------|--------------|--------------|---------------|---------|
| TargetToNative | target | target | *may show* | native | ✅ prompt | recognition of meaning | ✅ ship |
| TargetToNative | **hidden** | none | shown | native | ❌ none | ❌ none | ❌ block — must be unreachable |
| NativeToTarget | native | native | *may show* | target | ✅ response | ✅ production of form | ✅ ship |
| NativeToTarget | **hidden** | none | shown | target | ✅ response | ✅ picture→target | ✅ ship (target still on-screen in options) |
| Mixed | per-turn | per-turn | per-turn | per-turn | per-turn eval | per-turn eval | ✅ ship only if each turn satisfies its row |

The matrix must enumerate audio-off / audio-on and photo-off / photo-on for the actual defaults the app ships.

### 3. Learner action & evidence of L2 engagement

For each row of the matrix, name the SLA action: **recognition** (choose meaning), **recall** (produce form), **production** (generate a novel utterance), **comprehension** (parse input for meaning). Say where the target-language token is retrieved from memory or parsed from input.

### 4. Empty-state and default review

Explicitly answer: *"If a first-time user opens this activity with no preference overrides, what direction / modality / toggle state do they land in? Trace that path through the matrix. Is it row-valid?"*

The 2026-07-15 miss was exactly this: defaults `DisplayDirection = "TargetToNative"` + `UsePhotoPrompt = false` + `VocabQuizShowTextWithPhoto = false` combined so that if a learner enabled photo prompts, they'd land in the ❌ row above. Nobody traced the default path.

### 5. Answer-leakage checklist

- [ ] Prompt title/heading key does not embed the answer form.
- [ ] `alt` / `aria-label` on prompt image do not name the answer.
- [ ] Prompt audio (`GetPromptAudioText`, `GetPromptAudioLanguage`) matches prompt direction — never the answer side. (See VocabQuiz.razor #193 anti-cheat comment.)
- [ ] Cached audio filename / TTS URL doesn't expose the answer term in logs or DevTools.
- [ ] MC distractor generation cannot include the correct answer.

### 6. Acceptance tests for every supported direction

Add or update executable cases in `.claude/skills/e2e-testing/references/quiz-activities.md` (or the sibling references file for the activity) covering **each row** of the matrix. Direction × modality is not optional coverage. See §3 of `quiz-activities.md` for the required Vocab Quiz matrix.

## Reviewer Blocking Criteria (Zoe)

Zoe will **block** the change on any of the following:

- Any matrix row lands in `native prompt → native response` with no target-language element on screen or in audio.
- A hide/show toggle can hide the target-language artifact in a target-prompt direction.
- The default preference path (no user overrides) lands in a blocked row.
- Matrix omits `Mixed` when the activity supports it.
- No acceptance test exists for the newly reachable rows.
- Author cannot state the learning objective in one non-mechanistic sentence.

Rejection is issued as a comment on the decision note plus a `blocked-by:learning-value-gate` label if the work came through GitHub issues.

## Why This Skill Exists (post-mortem)

**Incident:** 2026-07-15. A "Show/Hide text with photo" toggle shipped for Vocab Quiz with a per-user persisted default of `false` (text hidden). The migration `AddVocabQuizShowTextWithPhoto` (`src/SentenceStudio.Shared/Migrations/20260714173027_AddVocabQuizShowTextWithPhoto.cs`) added the field; `VocabQuiz.razor` line 1940 gated hiding on `HasPromptImage && showPhotoPromptControl && showMnemonicImage && !showTextWithPhoto` — **with no check on `promptUsesNativeLanguage`.** For `DisplayDirection = "TargetToNative"` (the app default), enabling the photo prompt hid the Korean term and left only a picture and English answer options. That combination provides zero L2 exposure and zero L2 retrieval.

**Why product review didn't catch it:** The feature was framed as a UX affordance ("declutter when a photo is shown") rather than a pedagogical mode change. There was no requirement to enumerate `direction × modality × toggle` combinations. `blazor-activity-layout-shell` skill governs layout parity; `activity-audit-checklist` governs mode × context coverage; nothing governed **language-role coverage** — the actual pedagogical dimension. This skill fills that gap and is now the earned Zoe review requirement for all activity changes.

**Why review didn't catch it:** Zoe reviewed for architecture (preference schema, migration correctness, EF dual-provider parity). Nothing in the review protocol asked *"in each language direction, what is the learner actually retrieving?"* That question is now #3 of the required artifacts and cannot be skipped.

**Why testing didn't catch it:** The E2E references for `/vocab-quiz` (`.claude/skills/e2e-testing/references/quiz-activities.md` §1) enumerated only the happy-path `TargetToNative` direction with default toggles. Section §3 (see below) now requires per-direction × per-modality cases, including the pedagogical empty-state check.

## Cross-References

- `AGENTS.md` — Task Validation Requirements references this gate.
- `.squad/routing.md` — activity UX changes route through Zoe with this gate.
- `.claude/skills/e2e-testing/references/quiz-activities.md` — executable acceptance matrix for Vocab Quiz direction × modality.
- `.squad/skills/activity-audit-checklist/SKILL.md` — sibling skill for mode × context coverage; run **both** gates when a change touches modes AND language direction.
- `.squad/skills/paired-prompt-ui/SKILL.md`, `.squad/skills/blazor-activity-layout-shell/SKILL.md` — layout/UX siblings; do not substitute for the pedagogical gate.
