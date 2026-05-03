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


## Team Update: M.E.AI 10.5 Debt-Paydown Complete (2026-04-27 → 2026-04-28)

**Status**: SHIPPED ✅

Zoe's M.E.AI 10.5 strategic recommendations executed via three-agent orchestration (Wash Phase 1 + Phase 2, Jayne validation):

**What shipped:**
- **CPM (Central Package Management)**: Directory.Packages.props created; ~95 packages centralized; 178 Version= attributes stripped from 22 csprojs
- **Polly Resilience**: All 5 OpenAI sites now route via HttpClientPipelineTransport with Polly policies (120s attempt / 300s total / 300s circuit-breaker). Zero retry storms in validation.
- **Config Extraction**: gpt-4o-mini, tts-1, text-embedding-3-small, and ElevenLabs voice IDs moved to appsettings.json with ?? fallback defaults. Single point of change.
- **SKU Assessment**: AppLib stays on Agents.AI (ConversationAgentService + ConversationMemory use orchestration types with no M.E.AI equivalent). Waiting for M.E.AI agent layer.
- **RetrievalService Defused**: NotImplementedException → no-op stub + [Obsolete]. Zero callers verified.

**Validation results** (6/6 gates PASS):
- Build matrix: 13/13 buildable projects green; 626 tests passing
- Aspire runtime: Clean start, all resources Running
- AI end-to-end: Conversation + comprehension scoring + ElevenLabs TTS working; clean Polly pass-through (no retry storms)
- Config sanity: All 4 projects (Api, WebApp, Workers, AppLib) read from appsettings.json with correct fallback defaults

**Implications for all agents going forward:**
- All future OpenAI HTTP traffic flows through Polly automatically
- Model names are now config-driven, not code-driven
- AppLib remains on Microsoft.Agents.AI; this is not a blocker (transitive M.E.AI dependency exists)
- MAUI+CPM gotchas are documented for future package maintenance

**Decision artifacts**: .squad/decisions.md merged (3 entries); inbox cleaned; decisions-archive-2026-04-28.md created

**Orchestration logs**: .squad/orchestration-log/2026-04-28T00:06:30Z-{agent}.md (3 entries)

**Session log**: .squad/log/2026-04-28T00:06:30Z-meai-debt-paydown.md

**SHIP IT verdict**: All validation gates pass; zero regressions introduced. Production-ready.


---

## 2026-04-29 — net11p3 Narrative Correction Cycle

Bookkeeping pass to correct earlier overstated framing ("net11p3 broken for our app") with the verified narrow facts: a Razor SG regression on switch-expression-of-RenderFragment-with-inline-markup. Captain's clean `~/work/PeeThreeRegression` MAUI Blazor project builds fine on net11p3 — falsifying the SDK-wide claim. Wash isolated the trigger pattern, packaged a minimal repro (`~/work/peethree-repro-artifacts/peethree-net11p3-repro.zip`), and filed upstream at https://github.com/dotnet/razor/issues/13117. Kaylee refactored the only offending file (`src/SentenceStudio.UI/Pages/ImportContent.razor`) to tuple-meta helpers (`GetTypeBadgeMeta`, `GetStatusBadgeMeta`).

**Bookkeeping actions:**
1. ✅ decisions.md — added new 2026-04-29T21:00Z entry with corrected facts at top; surgically corrected the 2026-04-29T14:32Z entry's two overreaching conclusions (added pointer to corrected entry, softened the "broken on main" line, added archived-decision references, marked upstream issue filed in follow-ups).
2. ✅ Moved both inbox decisions (`kaylee-renderfragment-switch-pattern-banned.md`, `wash-net11p3-razor-sg-repro.md`) → `.squad/decisions/archive/`. (Created the archive/ dir — it didn't exist.)
3. ✅ followups.md — FU-4 reframed and marked RESOLVED with verified path forward (net10 + `-p:ValidateXcodeVersion=false`); kept original framing preserved at end of entry for history.
4. ✅ store_memory recorded for the trigger pattern + upstream issue.

## Learnings

- **2026-04-29 — When an SDK swap produces a wall of errors, look at error LINE NUMBERS first.** `CS9348` ("compilation unit cannot directly contain members") on `@inject` directive lines (4–10), combined with `CS0101`/`CS0102` with **empty** type/member names, is the fingerprint of a Razor source generator that bailed on the file → it's a pattern-specific bug, NOT an SDK-wide regression. Don't conclude "the SDK is broken" without (a) verifying with a clean `dotnet new` project and (b) isolating the trigger pattern in a minimal repro.
- **Bookkeeping correction pattern:** when correcting an earlier decisions.md entry whose conclusions overreached, prefer **adding a new dated entry at the top with the corrected facts** + surgically softening the obsolete claims in the original entry (with a forward pointer), over rewriting the original wholesale. Preserves the diagnostic trail; future readers see how the team's understanding evolved.

## 2026-05-03 — Vocab Quiz Bug Cluster Ship Logged (#189–#194)

Bookkeeping pass after Stream A (PR #196, Kaylee) and Stream B (PR #198, Wash) both squash-merged to `main`. PR #195 (Jayne's draft repro tests) closed as superseded — commits absorbed into #198's squash.

**Actions:**
1. ✅ `.squad/decisions.md` — appended new section dated 2026-05-03 "Vocab Quiz bug cluster (#189–#194) shipped" near top, citing PR #196, PR #198, PR #195, and follow-up issues #197 / #199.
2. ✅ Moved processed inbox files to `.squad/decisions/processed/2026-05-03/`:
   - `jayne-vocab-quiz-scoring-repro-189-191.md`
   - `kaylee-189-panel-cleanup.md`
   - `kaylee-vocab-quiz-stream-a.md`
3. ✅ **Preserved** `.squad/decisions/inbox/wash-vocab-quiz-scoring-proposal-191.md` at its current path — issue #197 body and PR #198 description both link to it. Do not move without updating those references.
4. ✅ Appended one-line "merged" entries to `kaylee/history.md`, `jayne/history.md`, `wash/history.md` covering PR merge SHAs, branch deletion, and cross-issue links (#197, #199).

**Convention reinforced:** When a referenced inbox artifact is cited from a public issue/PR, leave it at its current path even after its decision is merged into `decisions.md`. Treat it as a referenced artifact, not a transient inbox file. Path stability matters more than inbox cleanliness.
