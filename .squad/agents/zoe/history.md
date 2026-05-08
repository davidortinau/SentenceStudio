# Project Context

- **Owner:** David Ortinau
- **Project:** SentenceStudio — a .NET MAUI Blazor Hybrid language learning app
- **Stack:** .NET 10, MAUI, Blazor Hybrid, MauiReactor (MVU), .NET Aspire, EF Core, SQLite, OpenAI
- **Created:** 2026-03-07

## Learnings

- 2026-05-08: **One-shot migration removal vs. gating — `MigrateToStreakBasedScoringAsync`** — Wash flagged commit 398a7690's `/migrate-streak` endpoint as a multi-tenant boundary violation (auth required but ignored claims, mutated all users' progress). Decision: REMOVE entirely, not gate. Criteria: (a) endpoint never shipped (only on blocked branch), (b) Flutter has zero callers (docstring claim was false), (c) only consumer was a `#if DEBUG` Settings.razor button whose purpose was served when 377275e7 originally shipped, (d) migration is non-idempotent — re-derives `CurrentStreak` from `[Obsolete]` legacy fields and would clobber post-migration streak progress, (e) no-op for new users (legacy fields empty). Captain confirmed "won't use it again." If a future operator needs to repair a specific user, the right tool is a one-off scoped EF script, not a permanent endpoint. Decision file at `.squad/decisions/inbox/zoe-migrate-streak-removed.md` includes a forward-looking guard for the blocked-branch rebase: when 398a7690 is reworked, MaintenanceEndpoints.cs and the `app.MapMaintenanceEndpoints()` line must NOT be reintroduced. Branch `squad/wash-398a7690-fixes-maintenance`. Reusable skill candidate: `deprecating-one-shot-migrations` (decision tree: idempotent? per-user filter? still needed? remove vs. gate vs. operator-script).

- 2026-05-08: **Multi-agent git race — use `git worktree add` for isolated parallel work** — While editing files for the migrate-streak fix, parallel Squad agents (Wash/Kaylee) ran `git checkout` on the shared working tree TWICE, silently destroying my uncommitted edits. The repo is one working directory shared across all agents — concurrent branch ops are unsafe. Solution: `git worktree add ../SentenceStudio-<agent>-<task> <branch>` creates an isolated checkout that other agents cannot disturb. Re-applied edits there, built clean, committed safely. Caveats: (1) `appsettings.json` is gitignored under `src/SentenceStudio.AppLib/` — fresh worktrees fail with CS1566 until you copy it from the main worktree; (2) `.squad/` lives in main worktree only (different path inside the new worktree if it shares HEAD); (3) `dotnet workload restore` may need to run once per new SDK band — ios/android/maccatalyst packs ~2 min download. **This pattern should become standard for any squad task that touches code while other agents are active.**

- 2026-05-05: **NumberDrill Context × Sub-Mode Gating Policy — SupportedSubModes Matrix** — Designed gating mechanism for incomplete activity sub-mode combinations. CHOICE: Add `SupportedSubModes` JSON list field to `NumberContext` model (data-driven, not code-driven; preserves shipped seed rows; survives re-seeding; no feature-flag infrastructure). Picker filters dynamically at load time. Re-enable via 1-line JSON edit (no migration). Quality gate: 7 acceptance criteria (picker, start, render, success turn, failure turn, audio if applicable, no stubs) × 3 platforms (Mac Catalyst, iOS sim, webapp). Ownership routing: UI/Razor → Kaylee, Generator → River/Wash, Audio → Kaylee, Progress → Wash. N/A vs Deferred guidance: N/A = pedagogically incompatible for this language (hide permanently for Korean, revisit for Japanese in Phase 4); Deferred = unshipped feature dependency (ASR, Phase 3). Pattern is reusable for any multi-mode activity (Quiz variants, Shadowing contexts, Pronunciation drills). Estimated velocity: schema + picker + seed = 1 day, audit = 2 days, gate-to-ship = 3 days parallelized. Prevents stub leakage while preserving low-friction re-enable path. (See `.squad/decisions/inbox/zoe-numberdrill-gating-policy.md` for full spec.)

- 2026-05-05: **DeterministicPlanBuilder slot replacement architecture for NumberDrill Phase 2** — Existing STEP 4 "closer" slot (VocabularyMatching) uses `ResourceId = null` + `SkillId` pattern. NumberDrill replaces it when `NumberMasteryProgress.DueDate <= DateTime.UtcNow.AddDays(1)`. Found non-determinism issue: `SelectInputActivity()` uses `Guid.NewGuid()` tiebreaker (line 745) while `SelectOutputActivity()` uses deterministic `HashCode.Combine(DateTime.Today, a)` (line 769) — inconsistency documented, fix deferred per Captain guidance "keep scope tight." 4-layer ResourceId decoupling pattern now applied to 3 activities (Quiz, Matching, Cloze) with NumberDrill as 4th; pattern is reusable skill candidate. Enum PlanActivityType has no exhaustive switches (all use `_ =>` default), so adding value 11 is safe. PlanConverter uses enum-to-key mapping (compile-time) to avoid AI snake_case localization mismatches.
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

- 2026-05-05: **NumberDrill Phase 2 Wave 1 — Plan Integration Architecture** — Led architecture decision for Phase 2 plan integration: NumberDrill replaces VocabularyMatching in DeterministicPlanBuilder STEP 4 when numbers are due (within +1 day). Key findings: (1) PlanActivityType enum has no exhaustive switches (all use `_ =>` defaults) — safe to add value 11, (2) 4-layer ResourceId decoupling pattern extends to 3rd activity (follows Quiz/Matching/Cloze), (3) Non-determinism in SelectInputActivity (uses Guid.NewGuid() vs SelectOutputActivity HashCode.Combine) — defer per Captain guidance, (4) Backward compatibility verified (new enum won't collision). 4-layer pattern extracted to `.squad/skills/resourceid-decoupling/SKILL.md` for future vocab-driven activities. Handoff: Wash (plan integration + resource decouple), Kaylee (Disambiguate UI Wave 2), Jayne (test suite Wave 3), Localize (keys). Architecture decision document: `.squad/decisions/inbox/zoe-numberdrill-plan-integration-arch.md` (6 sections + 2 appendices + open questions for Captain).

---

## 2026-05-05: Dogfooding Directive Established

**Status:** DIRECTIVE (Cross-team impact)

Per Captain's directive captured in AGENTS.md under "Project Purpose: Dogfooding .NET MAUI", **tooling friction investigations are HIGHER priority than app features**. When .NET MAUI SDK/DX friction surfaces during normal work:

1. Root-cause systematically (layer elimination, reproduction steps)
2. File upstream with high-quality issue body
3. Document workarounds for team reuse
4. Continue app validation using workaround

This directive was established during NumberDrill Phase 1 work when DevFlow CLI bugs blocked automation. Investigation yielded dotnet/maui-labs#232 — a real upstream issue capturing tooling friction that benefits the .NET MAUI team.

**Impact on your work:** Prioritize tooling investigations over feature delays. Document findings as skills for team reuse.

---

