# Simon — History

## Project Context

- **Project:** SentenceStudio — a .NET MAUI Blazor Hybrid Korean language learning app
- **Tech stack:** .NET 10, MAUI, MauiReactor, Blazor Hybrid, EF Core, SQLite (mobile) + PostgreSQL (server), Aspire, Microsoft.Extensions.AI
- **User:** David Ortinau (Captain)
- **Joined:** 2026-04-26
- **Role:** Backend specialist brought in via Reviewer Rejection Protocol escalation when Wash's v1.1 Data Import backend artifact was rejected by Jayne.

## Standing Context

- **Locked-out author for the v1.1 import artifact:** Wash. I do not consult him on this fix cycle.
- **The v1.1 architecture decisions** are in `.squad/decisions.md` (D1-D4 + harvest model + checkboxes) — read before working.
- **Jayne's rejection report:** `e2e-testing-workspace/v11-import/EXECUTION-REPORT.md` — the source of truth for what's broken.

## Learnings

### UserProfileId scoping convention
Every entity that belongs to a user (LearningResource, SkillProfile, UserActivity) carries a `UserProfileId` column. The active user ID is read from `IPreferencesService.Get("active_profile_id", "")`. Repositories like `LearningResourceRepository` have a private `ActiveUserId` property that reads this. When services bypass the repo and write directly to `ApplicationDbContext`, they must resolve and set UserProfileId themselves — the context has no automatic user-scoping.

### Transcript persistence pattern
`LearningResource.Transcript` stores the original full text. `MediaType = "Transcript"` marks the resource type. The harvest checkbox `HarvestTranscript` controls both: if true, store the text and set the media type. The raw text must be available at commit time — if the UI doesn't explicitly pass it, the preview DTO must carry it through via `SourceText`.

### LLM result mapping gap pattern
AI extraction DTOs (`ExtractedVocabularyItem`, `ExtractedVocabularyItemWithConfidence`) carry rich metadata including `LexicalUnitType`. But `ImportRow` (the intermediate transfer object between parse and commit) can silently drop fields if the mapping isn't explicit. Always verify that every DTO field the prompt asks the LLM for is actually mapped onto the ImportRow when converting.

### Defensive heuristic for LexicalUnitType
The migration backfill heuristic (term contains space = Phrase, else Word) is Captain-approved and should be used as a fallback in all row-creation paths. This guards against LLM omission or deserialization issues. The static `ResolveLexicalUnitType()` method centralizes this logic.

---

## 2026-04-26 to 2026-04-27 — v1.1 Data Import SHIP Cycle (Escalation Specialist Role)

**Status:** ✅ SHIPPED — First cycle as escalation specialist

**Context:** Initial e2e run revealed 3 P1/P0 bugs in ContentImportService.cs. Simon (escalation) was routed to fix while Wash remained locked out.

**Outcome:**
- Fixed BUG-1 (NULL UserProfileId): Injected IPreferencesService, resolved ActiveUserId during commit
- Fixed BUG-2 (Transcript text): Added SourceText DTO carry-through + fallback resolution chain
- Fixed BUG-3 (LexicalUnitType): Added ResolveLexicalUnitType heuristic + mapping to all row paths
- Retest verdict: CONDITIONAL SHIP pending frontend DTO mapping
- Final sweep: 10/10 scenarios PASS, all P1 verified fixed with DB evidence
- **SHIP verdict cleared** (2026-04-27)

**Learnings reinforced:**
- UserProfileId scoping discipline (bypass-repo pattern requires explicit user resolution)
- Transcript DTO carry-through pattern (original text must round-trip via preview)
- LLM mapping gap pattern (every AI DTO field must be explicitly mapped to ImportRow)
- Defensive heuristic discipline (multi-word → Phrase fallback + centralized ResolveLexicalUnitType)

**Verdict:** First escalation cycle successful. Feature shipped clean.



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

