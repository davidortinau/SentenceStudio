# Project Context

- **Owner:** David Ortinau
- **Project:** SentenceStudio — a .NET MAUI Blazor Hybrid language learning app
- **Stack:** .NET 10, MAUI, Blazor Hybrid, MauiReactor (MVU), .NET Aspire, EF Core, SQLite, OpenAI
- **Created:** 2026-03-07

## Learnings

- 2026-04-23: **Word/Phrase Feature Completed** — Completed 4 todos: tests-backfill (120 tests, classification + constituent backfill), tests-mastery-cascade (10 tests, cascade logic), tests-regression (5 tests, word-only unaffected), tests-smart-resource (12 tests, Phrases smart resource). Total: 147 tests passing, feature code-complete. E2E blocked on pre-existing SQLite migration history mismatch (Captain decision needed on reconciliation). One bug surfaced & fixed: SmartResourceService.GetPhrasesVocabularyIdsAsync scope bug (was circular, fixed by Wash). Documented in `.squad/log/2026-04-23T2219Z-wordphrase-squad-wrap.md`.
- 2026-04-17: HelpKit Alpha — 30 golden Q/A + eval gate (85%/0%) + cross-platform validation plan + 7 unit-test files.

- 2026-04-25: **v1.1 Import Test Matrix Authored** — 10 scenarios (A-J) + 7 edge cases + 5 test fixtures in `e2e-testing-workspace/v11-import/`. Covers vocab CSV regression, phrases import, transcript import, auto-detect at all 3 confidence tiers, checkbox validation, checkbox override, DB pollution check on cancel, and LexicalUnitType backfill migration. All marked AUTHORED NOT YET RUN. Decision file at `.squad/decisions/inbox/jayne-v11-test-matrix.md`. Gaps flagged: zero-extraction behavior undefined, >30KB handling needs Wash confirmation, classifier confidence thresholds depend on River's prompt, Kaylee's UI selectors TBD.

<!-- Append new learnings below. Each entry is something lasting about the project. -->

- 2026-04-26: **v1.2 Import Bug Reproduction** — Captain's Phrases+Pipe import confirmed broken. Root cause: Phrases branch in `ContentImportService.ParseContentAsync()` (line 176-196) bypasses delimiter-aware `ParseDelimitedContent()` and sends everything to `ParseFreeTextContentAsync()`, which decomposes phrases into individual words via AI. DB evidence: 8 Word entries, 0 Phrase entries from Captain's 3-sentence input. Second issue: `ParseDelimitedContent()` hardcodes `LexicalUnitType.Word` (line 485). Evidence at `e2e-testing-workspace/v12-import-bug/`. LexicalUnitType enum mapping confirmed: Unknown=0, Word=1, Phrase=2, Sentence=3. Server Postgres has the LexicalUnitType column (migration applied); mobile SQLite does NOT (migration `20260423` never applied to server SQLite).
- Server DB is Postgres via Aspire (not SQLite). The SQLite file at `~/Library/Application Support/sentencestudio/server/sentencestudio.db` is the mobile sync DB, not the webapp DB. Query Postgres via: `docker exec -e PGPASSWORD='...' <container> psql -U dbadmin -d sentencestudio`
- Playwright MCP can go stale (browser closed state) between sessions. Have a fallback to DB-level verification when Playwright is unresponsive. Workaround: connect Python Playwright directly via CDP to the existing Chromium debug port (64185). Requires page reload to re-establish Blazor SignalR circuit.

- 2026-04-27: **v1.2 Import Fix Verified (Round 4)** — Wash's fix at commit `3c7a4cc` verified. Test 1 (Phrases regression): 3 pipe-delimited lines imported as LexicalUnitType=2 (Phrase) + 5 harvested words — PASS. Test 2 (Sentences fix): 3 pipe-delimited lines imported as LexicalUnitType=3 (Sentence) — was 0 before fix, now 4 — PASS. All 24 unit tests pass. Verdict: SHIP. Evidence at `e2e-testing-workspace/v12-import-fix-r4/`.

- E2E testing skill at `.claude/skills/e2e-testing/SKILL.md` — follow it religiously
- Aspire stack: `cd src/SentenceStudio.AppHost && aspire run`
- Webapp at `https://localhost:7071/`
- Playwright for browser testing: snapshots, clicks, form fills, verification
- Server DB: `/Users/davidortinau/Library/Application Support/sentencestudio/server/sentencestudio.db`
- Three verification levels: UI state, database records, Aspire logs
- "It compiles" is NOT sufficient — must verify in running app
- Must call `CacheService.InvalidateVocabSummary()` after recording attempts or dashboard is stale

## Core Context (Summarized History)

[Earlier entries (prior to 2026-04-25) have been reviewed and consolidated. Key patterns:]

- Agent is primary domain specialist for this project area
- Responsibilities established through prior charter/assignment cycles
- Cross-agent coordination pattern: decision inbox → decisions.md → history broadcast
- Team cadence: 3-agent orchestrations typical for major feature work
- Velocity: multiple cycles per week with focus on validation/ship gates

### Lesson: Always check the bridge between backend and frontend

When two agents deliver backend (Wash) and frontend (Kaylee) pieces, the integration call between them is a prime spot for gaps. E2E caught what unit tests couldn't — the unit tests tested the enrichment method in isolation, and the Razor markup rendered correctly when `IsDuplicate=true`, but nobody verified the full chain.

### Aspire orphan cleanup

DCP processes orphaned again after `stop_bash`. Two-pass kill (SIGTERM parent PIDs 51006, 51012) cleaned up. Port 22070 verified free before exiting.

### Style audit results

- No bespoke purple hex remaining
- All inline `cursor:pointer` replaced with `role="button"`
- All inline `font-size` replaced with Bootstrap `small` class
- Remaining inline `style=` are justified functional widths + text truncation
- Mobile preview table (390px) is functional but cramped — preview table lacks the card layout the results table has


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

