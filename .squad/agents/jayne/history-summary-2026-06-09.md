# Jayne History Summary (2026-05-04 to 2026-06-09)

**Agent:** Jayne — Testing, Validation, E2E Verification  
**Role:** Test scaffolding, regression coverage, E2E gate-keeping  
**History Size:** 38644 bytes → summarized to preserve test patterns & decisions

## Key Delivery Areas

1. **NumberDrill Test Matrix (Phase 1–4)**
   - 16 authored test cases (Phase 1 focus vocabulary contract)
   - 10 E2E scenarios (A–J) + 7 edge cases in e2e-testing-workspace
   - Tests cover: plan-level focus IDs, preview-word projection, deterministic ordering, vocabulary-aligned items, min-5/max-20 gates, route-parameter propagation, stable item IDs
   - Focus on regression: once a bug surfaces, write test so it can't resurface

2. **Test Infrastructure & Patterns**
   - PlanService SQLite round-trip + SQLite/PostgreSQL EF model storage
   - Legacy ProgressService reconstruction
   - Conditional repository multi-tenant scoping (validate userId non-empty)
   - Baseline: 534/535 passing (pre-existing known failure documented)

3. **E2E Verification (Playwright + Aspire)**
   - Webapp testing workflow: Aspire health check, Playwright snapshots/clicks/verification
   - Server DB verification: Postgres query via `docker exec` + psql
   - Mobile data: `~/Library/Application Support/sentencestudio/server/sentencestudio.db` is sync cache, NOT webapp DB
   - Three verification levels: UI state, database records, Aspire logs

4. **V1.1–1.2 Import Testing**
   - v1.1: 10 scenarios covering vocab CSV, phrases import, transcript import, auto-detect tiers, checkbox validation
   - v1.2: Bug reproduction (phrase+pipe import), LexicalUnitType mapping, migration state (server Postgres HAS column, mobile SQLite does NOT)
   - Pattern: always verify at DB level when UI is stale (Playwright CDP fallback via debug port 64185)

5. **Recent Sessions**
   - 2026-05-04: Phase 1 focus vocabulary test scaffolding (20 tests, 19 pass, 1 fail on ordering tiebreaker)
   - 2026-05-04: Wave 4 E2E shipped (Playwright verification, all 3 deliverables working)
   - 2026-04-25–05-04: v1.1 import test matrix (10 scenarios + 7 edge cases authored)
   - 2026-04-26–27: v1.2 import bug reproduction & verification
   - 2026-06-09: Code-review follow-up (Issue 3 regression proven non-vacuous via revert-and-rerun)

## Standing Test Patterns

- **Known-failing baseline:** `ResourceUsed15DaysAgo_ShouldNotBeTreatedAsNeverUsed` (14-day lookback window treats ≥15 days as "never used")
- **Non-vacuity proof:** Revert the fix, run test, verify it fails correctly → proves test exercises the code path
- **Regression writing discipline:** If a bug surfaces twice, a test must prevent third occurrence
- **Multi-tenant audit:** Every repository method with userId filter must be tested with empty/null userId → should return empty, not all rows
- **Playwright cache invalidation:** Browser closed state → use DB verification as fallback
- **E2E tooling checklist:** CacheService.InvalidateVocabSummary() after attempts, restart webapp after structural HTML changes, confirm Aspire health before UI tests

## Known E2E Blockers (Pre-Existing)

- Windows XamlCompiler exit 126 on macOS (whole-solution build blocked)
- Android minSdk mismatch
- AppLib test project TFM mismatch
- iOS Sim Xcode version mismatch (26.3 vs 26.2 requirement) — workaround: temporary net11p3 swap or Mac Catalyst substitute
- Playwright MCP can go stale between sessions

## Cross-Agent Context

- Works with Kaylee (UI verification), River (grading logic validation), Wash (backend integration)
- E2E is the final gate before SHIP — Captain's directive: "confirm fix via iOS simulator (iPhone 17 pro, iOS 26.2) before push to DX24"
- Recent focus: Phase 1 focus vocabulary end-to-end validation, import content workflow testing, regression test non-vacuity proof

