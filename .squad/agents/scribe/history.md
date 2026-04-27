# Project Context

- **Project:** SentenceStudio
- **Created:** 2026-03-07

## Core Context

Agent Scribe initialized and ready for work.

## Recent Updates

📌 Team initialized on 2026-03-07
📌 Squad — Word/Phrase Plan Review checkpoint logged 2026-07-26 (5-agent consensus, 3 Captain decisions, implementation ready)
📌 **2026-04-24** Bookkeeping pass: Merged 3 Wash decisions from inbox (DB reconcile Option A + smart-resource idempotency + wireup), logged session for WoC push (7cddc6e, ff0bb25), pushed to origin/main
📌 **2026-07-27 v1.4 cycle** — Logged dual decisions (Wash preview duplicates + Kaylee style cleanup) + captured team learning on integration-gap coordination

## Learnings

Initial setup complete.
- For Azure publish logs, capture environment + region, successful resource list, public webapp URL, Aspire dashboard URL, and note custom-domain follow-up separately from deploy status.
- **BlazorHybrid NavigateTo patterns (2026-04-18):** `LoginPage.razor` and `RegisterPage.razor` use `forceLoad: true`, which works for Web (cookie-backed) but breaks MAUI (loses in-memory auth state). Platform-gating needed via `isWeb` pattern. See `NavMenu.razor:106-107` for existing pattern to borrow.

---

## 2026-04-24 — Data Import Architecture Session Orchestration

Scribed multi-agent session for data-import-architecture-plan. Deployed Zoe (architect), Wash (data scout), River (AI strategy), Kaylee (UI scout). Recorded Captain directive on scope separation.

**Orchestration tasks completed:**
1. ✅ 5 orchestration logs (Zoe, Wash, River, Kaylee, Copilot directive)
2. ✅ Session log with key outcomes + next steps
3. ✅ Merged 6 inbox decisions into decisions.md, deleted inbox files
4. ✅ Updated all 4 agent histories with import-plan context
5. ✅ Decisions.md now 169KB (will archive if >20KB trigger next run)
6. ✅ Git staged for commit with proper trailers

**Key outcomes recorded:**
- MVP architecture: `/import-content` page, ContentImportService in Shared, no new DB tables
- Placement: separate Video Subscriptions from generic import (per Captain directive)
- AI strategy: heuristics-first, 5 prompt templates, confidence thresholds
- UI patterns: reuse ResourceAdd card, InputFile, preview table
- Data integrity: dedup by TargetLanguageTerm (case-insensitive trimmed)

**Next:** Implementation team begins service + UI build. River engineers prompts.


---

## 2026-05-01 — Data Import MVP Wave 3 Final Merge

**Status:** ✅ Complete — MVP ready for Captain's `/review` gate

**Final merge tasks completed:**
1. ✅ Verified 4 inbox files exist (wash, jayne x3)
2. ✅ Merged Wave 3 decisions into `.squad/decisions.md` with comprehensive sections:
   - Wash: IAiService extraction rationale + dual DI registration safety
   - Jayne: 18/18 unit tests passing, ~95% coverage, 0 bugs found
   - MVP completion milestone: all 11 todos shipped
3. ✅ Deleted merged inbox files (4 files removed)
4. ✅ Added Wave 3 decision section to decisions.md
5. ✅ Prepared commit with comprehensive message + trailers

**Wave 3 Summary:**
- IAiService extraction: unblocked unit testing with zero behavioral impact (16 existing consumers untouched via dual DI)
- ContentImportService: 18 unit tests + 15-scenario E2E script, 0 bugs
- Quality bar: 100% test pass rate, ~95% API coverage, comprehensive E2E scenarios
- MVP complete: 11/11 todos shipped on `feature/import-content-mvp`
- Ready for: Captain's `/review` gate → merge → production

**Next:** Captain runs `/review`, resolves any feedback, merges to main.


---

## 2026-07-27 — v1.4 Preview Duplicates + Restyle Cycle

**Status:** ✅ Logged — decisions merged, learnings captured, committed

**Commits:**
- `3130810` (Wash + Kaylee) — Preview duplicate detection + style cleanup
- `5d98a27` (Kaylee) — History update
- `7d9b5c3` (Jayne) — Caught missing integration wire, fixed EnrichPreviewWithDuplicateInfoAsync call

**Decisions merged:**
1. ✅ `wash-preview-duplicate-flag.md` — EnrichPreviewWithDuplicateInfoAsync contract, IsDuplicate + DuplicateReason DTO properties, 4 new unit tests
2. ✅ `kaylee-import-style-cleanup.md` — Style audit (replaced custom purple, cursor:pointer, inline font-size), badge styling pattern

**Learning captured:** Integration gap between backend (Wash) and frontend (Kaylee). Wash delivered enrichment method, Kaylee added badge rendering, but neither verified the bridge call. Unit tests passed in isolation; E2E caught the missing `EnrichPreviewWithDuplicateInfoAsync()` call in ImportContent.razor code-behind. Jayne's Round 2 test (re-import) showed zero duplicate badges despite rows being flagged — diagnosed root cause, identified commit 7d9b5c3 fix. **Future pattern:** When two agents handoff, explicitly verify the integration call exists and is wired before rendering.

**Team coordination notes:**
- Wash → Kaylee handoff needs explicit verification step
- E2E testing is the guard against integration gaps (unit tests test pieces, not contracts)
- Both agents' decisions are now in decisions.md with full rationale for future reference

**Status:** Complete — merged 10 inbox decisions, wrote 4 orchestration logs, 1 session log, updated 4 agent histories.

**Orchestration tasks:**
1. Archived decisions.md (229KB → decisions-archive-2026-04-25.md)
2. Merged 10 inbox files (river-v11-prompts, wash-v11-backend, kaylee-v11-ui, jayne-v11-test-matrix, jayne-phrase-import-gap, 5 captain confirmations)
3. Wrote orchestration logs for River, Wash, Kaylee, Jayne
4. Logged session to `.squad/log/2026-04-25T1357-import-v11-implementation.md`
5. Updated agent histories for River, Wash, Kaylee, Jayne
6. Git committed `.squad/` changes
