### 2026-04-29: Vocabulary Quiz Learning Details panel — streak-truth allowlist
**By:** Kaylee (per Jayne's directive on #189)
**What:** The Learning Details panel in `VocabQuiz.razor` now renders ONLY
the streak-based truth fields. Legacy metadata readouts (`IsKnown`,
`IsUserDeclared`, `VerificationState`) are stripped from the rendering.
Schema fields remain on `VocabularyProgress` for sync/back-compat.

Allowlist (canonical for this panel going forward):
- TotalAttempts, CorrectAttempts, Accuracy
- CurrentStreak, ProductionInStreak, EffectiveStreak
- MasteryScore
- status badge

**Why:** Jayne's service-side repro tests for #189 pass on `main`, so the
"2 attempts / 50% accuracy" confusion was a UI artifact: the panel was
showing two competing mental models of "is this word known" side-by-side.
Tightening to the streak-truth allowlist resolves user confusion.

**Audit (also captured in this PR):** `RecordPendingAttemptAsync` call
sites in `VocabQuiz.razor` are NOT double-invoked. The method is
idempotent via `pendingAttempt` null-guard. No fix needed there.

**Scope:** UI-only. Closes #189 alongside the four Stream A fixes in
PR #196 (`fix/vocab-quiz-ui-cluster-189-194`).
