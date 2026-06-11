# Project Context

- **Owner:** David Ortinau
- **Project:** SentenceStudio — a .NET MAUI Blazor Hybrid language learning app
- **Stack:** .NET 10, MAUI, Blazor Hybrid, MauiReactor (MVU), .NET Aspire, EF Core, SQLite, OpenAI
- **Created:** 2026-03-07

## Learnings

### Archive Summary (2026-01-29 to 2026-04-30)

Earlier work included:
- **NumberDrill Phase 1 Complete:** Data model with 5 new entities, grading system v2 (system-aware), Sino-compound normalization, Korean myriad chunking (십만/백만/천만), TTS audio cache with concurrent prewarm (3-job semaphore), ElevenLabs integration.
- **Content Import Atomicity:** Fixed orphaned resource issue in `CommitImportAsync` by consolidating to single atomic SaveChangesAsync (safe for `ValueGeneratedNever()` FK entities).
- **DevFlow Blazor Hybrid Automation:** Diagnosed two DevFlow CLI bugs affecting WebView: (1) `Runtime.evaluate` race condition (mitigated by `--verbose`), (2) `snapshot` unhandled exception in ref enrichment. Documented upstream issue.
- **Mobile-vs-API Asymmetry Pattern:** Identified recurring gap where ProgressService (mobile) and PlanService (API) diverge on entity persistence, causing CoreSync data loss. Key insight: always audit both paths when adding new columns.
- **P2 Orphaned Resource + AIClient Polly Refactoring:** Fixed atomic save in ImportService; refactored AIClient to use Polly-backed HttpClient for consistency.
- **15+ Deployments:** Wave 1–4 of NumberDrill phases, multiple publish cycles with Azure `azd deploy` (2m per cycle) + iOS to DX24 validation, package version management (Npgsql/EF Core alignment critical).

---

### Recent Sessions (2026-05-04 to 2026-06-09)

- 2026-05-04: **NumberDrill Phase 1 Data Model** — Created entity framework with dual migrations (PostgreSQL + SQLite), enum-backed storage for NumberSystem, unique indexes for multi-user filtering. 
- 2026-05-04: **TTS Audio Cache Service** — Implemented SHA-256 caching, concurrent dedup via `_pendingGenerations`, 3-job semaphore for ElevenLabs throttle, retry-once pattern for transient failures.
- 2026-05-05: **DevFlow CDP Runtime.evaluate Bug** — Root cause: race condition in DevFlow CLI response parsing; workaround: `--verbose` flag. Investigation products: upstream issue draft, skill documentation with Blazor patterns.
- 2026-05-05: **DevFlow Snapshot Bug (Second Investigation)** — Two distinct bugs: (1) Runtime.evaluate race + (2) snapshot ref enrichment exception. Workaround: use `webview source` instead of `snapshot`. Dogfooding signal: DevFlow WebView automation not production-ready for Blazor Hybrid.
- 2026-05-10: **DevFlow Snapshot Bug (Second Investigation)** — Verified both bugs with full repro; updated upstream issue and skills documentation; recommended manual validation on DX24 (code review passed, automation blocked by CLI bugs not app bugs).
- 2026-06-08: **Phase 1 Focus Vocabulary Implementation** — Added `FocusVocabularyIds` contract across deterministic plan generation, DTOs, persistence on `DailyPlan`, CoreSync registration, route plumbing. Canonical storage: `DailyPlan.FocusVocabularyFacts` (JSON). Dual migrations with matching timestamp. 556/557 tests passing.
- 2026-06-09: **Phase 2 Focus Vocabulary — NarrativeFacts/RationaleFacts Persistence Asymmetry Fix** — Mobile path (ProgressService) was not persisting narrative/rationale to DailyPlan row while API path (PlanService) already did. After mobile regen, CoreSync propagated NULLs to Postgres, destroying Preview button. Solution: Added 6 DTOs + 2 helpers, updated insert/update branches. **End-to-end verified:** Mac Catalyst → SQLite → CoreSync → Postgres (byte-identical), 100% focus vocabulary overlap, Preview button renders. 564/565 tests (+1 new). **Key learning:** Mobile-vs-API asymmetry surfaces when two paths persist same entity — always audit both.
- 2026-06-09: **Code-Review Follow-Up — Fallback Plan Rationale Symmetry** — Code review (Opus xhigh) caught asymmetric RationaleFacts protection in `GenerateFallbackPlanAsync`. Fallback path passed hardcoded sentinel Rationale string, bypassing `?? planRow.X` coalesce and silently overwriting LLM-generated rationale. Fix: pass `Rationale: null`, making all 3 facts columns symmetric in update branch. Test rewritten to prove non-vacuity: delete DailyPlanCompletion rows between generations (force LLM path), assert fallback entered (not bypassed via cache). Outcome: 566/567 tests pass. Issue 3 regression proven non-vacuous. Captain pre-push review gate ready.

---


- 2026-06-10T03:35:00Z: Timezone fix via `IPlanDateContext` completed in continuous-loop mode while Captain slept; staged work is ready for Captain's `/review`, PR split decision, and signed-in E2E.

---

- 2026-06-11: **Unseen vocabulary bootstrap + wording cascade fix** — Deterministic planner now detects zero SRS-due words and queries ResourceVocabularyMapping (multi-tenant safe) for 15-word capped bootstrap cohort. DaysSinceLastUse changed from int (999 sentinel) to int? (null = never used). Wording: 4-branch cascade with boundary guards at 0/1/30 days. Unit-test-vs-runtime gap risk: passing tests can hide data-distribution mismatches. Live diagnosis is expensive — clarify target surface upfront (Mac Catalyst? iOS? WebApp?) before digging. 583/583 tests passing (999-sentinel bug fixed as side effect). Commit 386b4550.
