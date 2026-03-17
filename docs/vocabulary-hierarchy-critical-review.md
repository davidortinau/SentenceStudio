# Critical Review: Vocabulary Hierarchy Plan

**Reviewer:** Copilot (requested by Captain as devil's advocate)  
**Document Under Review:** `docs/vocabulary-hierarchy-consensus-plan.md`  
**Core Question:** Is the value worth the effort and complexity?

---

## TL;DR Verdict

**The plan is over-engineered for the problem described.** The Captain's pain point is about *import-time duplication and wasted re-proving* — but the plan proposes a full hierarchical data model, mastery inheritance, new prompt schema, UI overhaul, and gamification. Most of that complexity can be avoided by solving the actual problem more directly.

---

## What the Captain Actually Said

> "This is leading to growing amount of overlap and duplication in the vocabulary which further confusing the learning tracking. I know a word, and have proven I know that word in some context, but now when it's in a sentence or phrase I need to prove all over again that I know it."

**The core pain is:**
1. Importing a transcript creates entries that overlap with words I already know
2. I have to re-prove knowledge I already demonstrated
3. The *tracking* gets confusing — too many entries for the same semantic concept

**The core pain is NOT:**
1. I need a tree visualization of word families
2. I need gamification badges for "Root Master"
3. I need adaptive review presentation by mastery tier
4. I need a 6-value RelationType enum (Inflection, Phrase, Idiom, Compound, Synonym, Antonym)

---

## Critical Issues with the Plan

### 1. The `Lemma` field already exists — and is unused

The `VocabularyWord` model already has a `Lemma` property (line 30). It was designed exactly for grouping inflected forms. It's used in `VocabularyLookupTool` for search but is never populated during import.

**Before building a ParentWordId hierarchy, why not just populate `Lemma` during import?** The AI can assign lemmas with zero schema changes. Then you can:
- Group by lemma in the UI
- Skip re-importing forms of words you already have the lemma for
- Query "do I already know any form of this root?"

This is a prompt change + import logic change. No migration. No new columns. No CoreSync risk.

### 2. The "90% single-parent" claim is wrong for Korean

Korean agglutinative morphology means most phrases derive from *multiple* roots:

| Phrase | Components |
|--------|-----------|
| 자주 마시는 | 자주 (often) + 마시다 (to drink) + -는 (modifier) |
| 피자를 주문하는 게 어때요 | 피자 (pizza) + 주문하다 (to order) + 어떻다 (how about) |
| 대학교 때 | 대학교 (university) + 때 (time/when) |

Even "대학교 때" has two meaningful roots. The plan acknowledges this as an "edge case" in Open Question #2 — but it's actually the *common* case for the Captain's Korean learning. A single-parent FK will misrepresent the majority of real data from day one, and migrating from single-parent to junction table later is a painful schema change that also breaks CoreSync state.

### 3. Mastery inheritance is solving a problem that doesn't exist yet

The plan proposes `newProgress.MasteryScore = Math.Min(0.30f, parentProgress.MasteryScore * 0.4f)` — giving derived words a head start.

But the Captain's complaint isn't "derived words feel too hard." It's "I have too many duplicate entries cluttering my vocabulary." The mastery boost adds complexity to VocabularyProgressService (which is already intricate with streaks, production tracking, grace periods, and SM-2) to address a *motivational* concern that might not even exist once deduplication is solved.

**Risk:** The mastery inheritance interacts with the wrong-answer penalty (`*= 0.6`), the streak system, the "Trust but Verify" user-declared flow, and the 14-day grace period. The plan doesn't analyze any of these interactions. Getting one of them wrong silently corrupts learning data.

### 4. Pre-fetching all user vocabulary into the prompt is expensive

The plan says "Pre-fetch user's existing vocabulary for target language" and pass it to the AI. A learner with 500+ words would add thousands of tokens to every prompt. This contradicts the work we *just did* to fix timeouts by chunking transcripts. We'd be making prompts bigger again.

### 5. The LinguisticMetadata is scope creep

`partOfSpeech`, `frequency`, `difficulty`, `morphology` — none of these are consumed by any proposed feature. They're speculative future data that adds prompt complexity, response parsing, and storage for zero current benefit.

### 6. Five phases / 7-10 days for what could be a 1-2 day change

The plan estimates 7-10 days across 5 phases with 5 team members. If we focus on the actual problem (import dedup + smarter grouping), the solution is much smaller.

---

## What I'd Propose Instead

### The Actual Problem, Restated
When importing vocabulary from a transcript, the system creates too many entries that overlap with words the user already knows. The user wants the system to be *smarter* about recognizing "I already know the root of this."

### A Simpler Solution (1-2 days, no migration)

**Step 1: Populate Lemma during import (prompt change only)**

Update the `GenerateVocabulary` prompt to return `lemma` (dictionary root form) for each word. The field already exists on the model.

```
For each word, also provide its dictionary root form as "lemma".
Examples: 주문하다 → lemma: 주문, 대학교 때 → lemma: 대학교
```

**Step 2: Smarter deduplication at import time**

Before adding a new word, check:
1. Exact match on `TargetLanguageTerm` → skip (already exists)
2. Lemma match → the root form already exists in the user's vocabulary. Still add the new form, but show a note: "Related to 주문 (already known)" in the import results

**Step 3: Group-by-lemma in the vocabulary list UI**

Instead of a tree hierarchy with ParentWordId, just group the flat list by `Lemma` when displaying. No schema change needed — it's a presentation concern:

```
주문 family (3 forms)
  주문 ⬤⬤⬤⬤⬤ Known
  주문하다 ⬤⬤◯◯◯ Learning  
  피자를 주문하는 게 어때요 ◯◯◯◯◯ New
```

**Step 4: Optional — "I already know this root" quick action**

When a new word is imported that shares a lemma with a Known word, offer a one-tap "I know this" that triggers the existing "Trust but Verify" flow (sets UserDeclared + 14-day grace period). No new mastery inheritance logic needed — the existing mechanism handles it.

### What This Avoids
- No migration (Lemma column already exists)
- No ParentWordId FK (avoids single-parent vs. multi-parent debate entirely)
- No CoreSync risk (no new columns to sync)
- No VocabularyProgressService changes (no mastery inheritance interactions to debug)
- No new enum, no new response models, no LinguisticMetadata
- Works for all languages, not just Korean

### What This Defers (Intentionally)
- Mastery inheritance (wait to see if users actually want it)
- Tree navigation UI (wait to see if grouping-by-lemma is sufficient)
- Relationship types (if needed later, a junction table is the right choice — not a premature single-parent FK)

---

## Answering the Open Questions (If You Go with the Full Plan)

If you still want the full plan, here's my take on the open questions:

1. **Mastery boost %** → Don't do it yet. Solve dedup first, see if users even ask for mastery inheritance.
2. **Multi-parent** → If you must pick, go junction table from day one. Single-parent is wrong for Korean and you'll regret migrating later.
3. **Backfill** → Backfill lemmas only (cheap AI call). Don't backfill ParentWordId relationships (expensive, error-prone).
4. **User control** → Definitely needed. AI will get relationships wrong 10-20% of the time.

---

## Summary

| | Full Plan | Simpler Alternative |
|---|---|---|
| **Schema changes** | 2 new columns + index + migration | None (Lemma already exists) |
| **CoreSync risk** | Medium (new FK, orphan handling) | None |
| **AI prompt changes** | New template + response model + pre-fetch | Add "lemma" field to existing prompt |
| **Progress system changes** | Mastery inheritance + interaction testing | None |
| **UI changes** | Tree view, badges, tooltips | Group-by-lemma display |
| **Effort** | 7-10 days, 5 phases | 1-2 days |
| **Solves core pain?** | Yes, plus a lot more | Yes |
| **Risk of breaking things** | Medium | Very low |
| **Future flexibility** | Locked into single-parent FK | Can add any relationship model later |

**Recommendation:** Start with the simpler alternative. If after living with lemma-based grouping for a few weeks you still want hierarchy, relationship types, and mastery inheritance — *then* build the full plan with the benefit of real usage data informing the design.
