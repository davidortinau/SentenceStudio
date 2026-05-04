# Project Context

- **Owner:** David Ortinau
- **Project:** SentenceStudio — a .NET MAUI Blazor Hybrid language learning app
- **Stack:** .NET 10, MAUI, Blazor Hybrid, MauiReactor (MVU), .NET Aspire, EF Core, SQLite, OpenAI
- **Created:** 2026-03-07

## Learnings

- 2026-04-17: **Help Flyout Wiring Pattern** — Dynamic IHelpKit reflection to keep UI project portable (net10.0-browser, no MAUI refs). Runtime type detection via `Type.GetType()` + method invocation; graceful degrade if HelpKit absent (WebApp). Used in NavMenu.razor for both MAUI (Help visible) and WebApp (Help hidden).
- 2026-04-17: **HelpKit Alpha shipped** — library, RAG pipeline, storage, 3 samples, eval harness, CI, docs all delivered.
- 2026-05-03: **AGENTS.md updated with auth-persistence cycle lessons** — Added three entries from May 2-3 auth fix cycle: (1) Mac Catalyst keychain-access-groups entitlement gotcha (NSPOSIXErrorDomain 163 from `$(AppIdentifierPrefix)` not substituting under ad-hoc Debug signing — solution: omit the entitlement entirely), (2) Mandatory `scripts/post-deploy-validate.sh` step after `azd deploy` (inserted as Step 2 in Publish Workflow), (3) References to single-flight async pattern (`.squad/skills/single-flight-async/SKILL.md`), EF dual-provider migrations, and async single-flight testing skills in new "Async Patterns" section. Skipped Catalyst bundle symlink (already handled by permanent MSBuild target per zoe-maccatalyst-symlink-permanent.md).

## Core Context (Summarized from Sessions)

**Architecture:**
- Multi-target csproj on Shared project for migrations: hand-write migrations + update Designer/Snapshot manually
- Post-deploy validation requires: revision health check (active = latest), indirect DB check (login test), 4-phase validation (Wash: infra + smoke + change-specific + regression)
- Phase 0-3 quiz behavior finalized: global streak-based mode selection, PendingRecognitionCheck flag, tiered rotation, cumulative session counters
- Cross-activity mastery pipeline via ExtractAndScoreVocabularyAsync (shared on VocabularyProgressService), dedup by DictionaryForm, scoring loop → probe collection AFTER
- Activity taxonomy: recognition vs production (Writing/Translation/Scene/Conversation production), DifficultyWeights established (VocabQuiz=1.0–2.5, Writing/Translation=1.5, Conversation=1.2)

**Database & Migrations:**
- All CoreSync-synced entities: string GUID PKs (ValueGeneratedNever), UserProfileId for multi-user isolation, singular table names
- Migrations: `dotnet ef migrations add <Name> --project src/SentenceStudio.Shared --startup-project src/SentenceStudio.Shared`
- NEVER delete data; fix migrations instead. Both API and WebApp call MigrateAsync() on startup.
- NarrativeJson added to DailyPlanCompletion for plan narrative storage

**Auth & Config:**
- JWT + refresh token flow: API endpoints in AuthEndpoints.cs (API), AccountEndpoints.cs (WebApp), JwtTokenService.cs
- Captain's preference: never show login unless explicitly logged out; mobile auth should keep people signed in weeks

## Core Context (Summarized History)

[Earlier entries (prior to 2026-04-25) have been reviewed and consolidated. Key patterns:]

- Agent is primary domain specialist for this project area
- Responsibilities established through prior charter/assignment cycles
- Cross-agent coordination pattern: decision inbox → decisions.md → history broadcast
- Team cadence: 3-agent orchestrations typical for major feature work
- Velocity: multiple cycles per week with focus on validation/ship gates

- **Existing Infrastructure:** When proposing schema changes, always audit the existing model classes. `LexicalUnitType` was already designed into the system — Squad missed it because it didn't cross-reference the codebase thoroughly during autonomous decision-making.
- **AI Prompt Alignment:** The AI templates already populated `lexicalUnitType` fields correctly. This alignment was a signal that the infrastructure was intentionally designed to support Word/Phrase/Sentence distinctions.
- **Backfill Pattern:** When existing rows have a default value (Unknown=0), a backfill migration is needed. Pattern: EF migration + async backfill method post-migration.

### Architecture Decisions That Held
- Transcript: Both store + extract (justified by: field already exists, prompt already exists, no migration needed)
- Auto-detect: Confidence thresholds + always-visible banner (justified by: data preservation rule, user-visible decisions)
- Same branch, drop -mvp: Avoids rebasing; work is isolated on feature branch by design

### Open Questions Captured
Six open questions for Captain in appendix (LexicalUnitType default value, confidence thresholds, transcript chunking >50KB, paired-line heuristic, dedup scope, naming) — documented for Captain review during unblock.

**Next:** Captain confirms, Scribe merges, implementation team (River → Wash → Kaylee → Jayne) unblocked per spec Section J.

- 2026-04-27: **M.E.AI 10.5.0 Adoption Strategy — Defer Features, Ship Debt** — Synthesized Wash's audit + River's verification into strategic recommendation: defer all three 10.5.0 headline features; ship three unrelated debt actions (Polly resilience, Directory.Packages.props + SKU unification, config for model/voice IDs). Pre-wrap decision: NO — bundle ElevenLabs + `ITextToSpeechClient` wrap with Realtime adoption when both stable (v11 timeframe). (See: `.squad/orchestration-log/2026-04-27T19-06-10Z-zoe.md` and merged decision in `.squad/decisions.md`.)


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


- 2026-05-02: **MSBuild post-build symlink for Aspire.Hosting.Maui bundle-name mismatch** — Hooked `AfterTargets="Build"` on `SentenceStudio.MacCatalyst.csproj`, gated to `net10.0-maccatalyst`. Used `$(_AppBundleName)` (from MAUI/Xamarin Shared SDK, populated by `_GenerateBundleName`) as the source-of-truth for the produced bundle name. Quirk: `$(OutputPath)` for Mac Catalyst already includes the RID segment (`bin/$(Config)/net10.0-maccatalyst/maccatalyst-arm64/`), so do NOT append `$(RuntimeIdentifier)` — first attempt produced a doubled `maccatalyst-arm64/maccatalyst-arm64/` path and the `Exists()` guard silently skipped the `Exec`. Verified diag-level MSBuild log to catch this. Final target uses `ln -sfn` for idempotence and skips when names match. Survives `dotnet clean` + rebuild.
