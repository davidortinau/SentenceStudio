# Jayne — History Summary

**Summarized by Scribe:** 2026-06-10T03:35:00Z
**Project:** SentenceStudio — .NET MAUI Blazor Hybrid language learning app
**Role focus:** Test design, E2E validation, regression reproduction, ship/no-ship verdicts.

## Import validation history

- v1.1 Import Test Matrix covered vocabulary CSV regression, phrases import, transcript import, auto-detect confidence tiers, checkbox validation/override, cancel pollution checks, and LexicalUnitType backfill migration. Authored scenarios A-J plus edge cases and fixtures.
- v1.2 Phrases+Pipe import bug was reproduced: Phrases branch bypassed delimiter-aware parsing and fell into free-text AI extraction, yielding Word rows and zero Phrase rows. `ParseDelimitedContent()` also hardcoded Word. Evidence was captured with DB counts and enum mapping.
- v1.2 import fix at commit `3c7a4cc` was verified: pipe-delimited phrases imported as Phrase plus harvested words; sentences imported as Sentence; 24 unit tests passed; verdict ship.
- Import Complete redesign was E2E validated in prior work: summary cards, row table, filter pills, back-nav state, vocabulary links, failed-row resilience, and clean logs.

## NumberDrill validation history

- Combo audit covered all 30 context/sub-mode combinations. Findings: TapTheCounter only implemented for Counting; ListenAndPlace hardcoded to Time; ListenAndType and ReadAndProduce work across all contexts; Disambiguate works universally. Result: 12 ship-visible combos, 14 hide, 3 not applicable, 1 UI placeholder fix.
- Phase 2 test scope includes irregular month forms, ordinal pattern disambiguation, Korean place-value grouping, paired Disambiguate grading, explanation panel rendering, and audio replays.
- Phase 2 E2E initially produced NO-SHIP due to infrastructure blockers: Aspire instability, missing NumberMasteryProgress schema in PostgreSQL, and misleading zero-byte SQLite files. Build fix used `NullLogger<T>.Instance` for static utility construction.
- Wave 4 E2E later shipped via Aspire + Playwright: Listen-and-place rendered and gave feedback, picker showed six contexts with Bootstrap icons and no emoji, Disambiguate kept both prompt selections visually active, console had zero errors.
- iOS sim Gate 3 was partially blocked by UI automation/CDP readiness, though registration and app launch paths were exercised.
- NumberDrill Phase 1 final verification covered webapp and iOS build/launch gates; ship-with-caveats used when login blocked deeper iOS validation but startup crash class was resolved.

## Regression-test patterns

- Recurring bugs need tests before closure. Examples include IdentityAuthService concurrency, grader normalization, override flow, Focus Vocabulary contract propagation, and timezone cache key regressions.
- Non-vacuous tests are preferred: prove the test fails on a simulated bug before trusting it as a guard.
- For UI correctness, screenshots are ground truth when accessibility snapshots disagree with visual active state timing.
- Keep E2E references current when activity flows change so future agents can validate without re-deriving steps.

## Recent Focus Vocabulary and timezone context

- Phase 1 Focus Vocabulary test scaffolding covered plan-level focus IDs, preview-word projection, deterministic ordering, activity propagation, min/max gates, route params, stable item IDs, storage round-trips, legacy reconstruction, and multi-tenant scoping.
- Timezone fix added seven ProgressCacheService timezone regression tests; three are discriminating and fail if the cache key ignores the explicit date argument.

---

- 2026-06-11: **Vocab bootstrap E2E (Mac Catalyst clean-build)** — Initial E2E failed: running old pre-bootstrap code from stale MonoBundle DLL. Root cause: partial clean left AppLib/bin with old code. After nuking MacCatalyst/bin, MacCatalyst/obj, Shared/bin, Shared/obj, **AppLib/bin, AppLib/obj**, rebuilt and verified correct Jun 11 13:49 binary (UTF-16 scan: "need 5+" absent, "Bootstrapping vocab" present). E2E PASSED 6/6 criteria: vocab activity on dashboard, FocusVocabularyFacts populated (15 words), preview matches activity, resource footer wording updated ("Last used 8 days ago"), Study Insight VocabInsight section present. Key learning: build cache — partial cleans leave stale shared DLLs in MonoBundle. Add AGENTS.md checklist for "nuke ALL bin/obj before release rebuild". Decisions inbox: vocab-bootstrap-e2e.md, vocab-bootstrap-cleanbuild-e2e.md.
