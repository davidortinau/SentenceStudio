# Session Log: Vocabulary Hierarchy Analysis
**Date:** 2026-03-17T16:23:37Z  
**Session Topic:** Vocabulary Hierarchy Team Analysis & Consensus Building  
**Requested by:** David Ortinau  

---

## Team Composition
- **Zoe** (Lead) — Architecture Framework
- **River** (AI/Prompt) — Import Strategy & Prompt Design
- **Wash** (Backend) — Data Model Evaluation
- **SLA Expert** — Second-Language Acquisition Research
- **Learning Design Expert** — UX & Motivation

---

## Work Completed

### Zoe: Architecture Framework
Identified four design pillars: Data Model, Mastery Propagation, AI Import, SRS Scheduling. Recommended conservative MVP approach starting with self-referential FK and independent mastery.

### River: Import Strategy
Proposed hierarchical JSON schema with `relationshipType`, `relatedTerms`, and `linguisticMetadata` fields. Designed multi-pass prompt engineering for vocabulary extraction with parent-child mapping.

### Wash: Data Model
Evaluated three schema options (self-referential FK, junction table, lemma groups). Recommended self-referential FK for minimal migration and future extensibility.

### SLA Expert: Research Integration
Applied SLA principles validating independent mastery tracking, morphological awareness benefits, and spacing effect optimization.

### Learning Design Expert: UX Analysis
Proposed progressive disclosure, tiered mastery with inheritance boost, and engagement tracking for vocabulary relationships.

---

## Deliverables
- `docs/vocabulary-hierarchy-consensus-plan.md` — Team consensus and recommended MVP
- `docs/zoe-vocab-relationships-architecture.md` — Complete architecture analysis
- `docs/vocabulary-hierarchy-prompt-design.md` — AI import specification
- `docs/wash-vocabulary-hierarchy-proposal.md` — Data model and EF Core implementation
- `docs/vocabulary-hierarchy-learning-design.md` — Learning design recommendations

---

## Decision Status
**Status:** PROPOSED — Awaiting Captain approval before implementation

**Recommended MVP Path:**
1. Self-referential FK (ParentWordId) on Vocabulary entity
2. Independent mastery tracking (no automatic transfer)
3. Hierarchical AI prompts with relationship detection
4. UI hints and relationship preview
5. Engagement metrics for future SRS coordination

---

## Next Steps (Awaiting Captain Decision)
- Approve/refine consensus plan
- Prioritize implementation phase
- Assign backend and frontend work
- Plan database migration strategy
