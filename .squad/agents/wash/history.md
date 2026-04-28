# Project Context

- **Owner:** David Ortinau
- **Project:** SentenceStudio — a .NET MAUI Blazor Hybrid language learning app
- **Stack:** .NET 10, MAUI, Blazor Hybrid, MauiReactor (MVU), .NET Aspire, EF Core, SQLite, OpenAI
- **Created:** 2026-03-07

## Learnings

- 2026-07-28: **P2 Orphaned Resource Bug Fix** — Fixed atomic save issue in `ContentImportService.CommitImportAsync()` for new-resource import path. Problem: early `SaveChangesAsync` at line 1365 followed by second save at 1631 could orphan empty resources if vocab save failed. Solution: Chose Option B (remove early save) over Option A (transaction) because `LearningResource.Id` is set in code (`Guid.NewGuid().ToString()`) with `ValueGeneratedNever()` EF config, so FK relationships don't need DB-generated ID. Added `bool isNewResource` flag to conditionally skip `Update()` call for new resources (prevents `DbUpdateConcurrencyException`). Single `SaveChangesAsync` at line 1631 now persists resource + vocab + mappings atomically. All 36 ContentImport tests passing (0 failures). Builds: Api ✅, UI ✅. Audited codebase for similar patterns — `LearningResourceRepository.SaveResourceAsync` has two saves but is safe (associates pre-existing vocab, not creating new). Decision drop: `wash-import-tx-new-resource.md`.
- 2026-07-28: **AiClient Polly Integration** — Refactored `AIClient` (Shared) to route through Polly-backed HttpClient. Call-site audit: ONLY `AiService.cs:145` used AIClient as TTS fallback (non-Aspire mode). Chose Option A (refactor over delete) — changed AIClient constructor to accept `HttpClient` parameter, built `OpenAIClient` with `HttpClientPipelineTransport`, used `.GetChatClient()/.GetAudioClient()/.GetImageClient()` pattern from Wave 2 sites. Updated `AiService.cs` to inject `IHttpClientFactory` and call `CreateClient("openai")` before constructing AIClient. Config reading: TTS/image model names already read from `AI:OpenAI:TtsModel/ImageModel` with fallback defaults (inherited from Wave 2). Key pattern: All Shared types that use HttpClient must accept it via ctor (Shared targets `net10.0` plain, has no DI registration site, no access to IHttpClientFactory directly). Build green (Shared, Api); 626 tests passing (1 pre-existing auth failure). Decision drop: `wash-aiclient-polly.md`.
- 2025-07-25: **Preview Duplicate Detection** — Added `IsDuplicate` (bool) and `DuplicateReason` (string?) to `ImportRow`. New interface method `EnrichPreviewWithDuplicateInfoAsync` runs a single batched DB query (`WHERE IN` on normalized target terms) and marks each preview row before commit. Extracted `NormalizeTargetTerm()` as the single source of truth for the matching predicate (trimmed, case-sensitive ordinal) — used by both preview enrichment and `CommitImportAsync`. Two reason keys: `"AlreadyInVocabulary"` (term exists in VocabularyWord table), `"DuplicateWithinBatch"` (same term appears earlier in preview). Intra-batch detection uses a `HashSet<string>` seen-tracker. 4 new tests (36 total): exact dup flagged, near-miss not flagged, batch query structure, and round-trip invariant (Preview IsDuplicate matches Commit Skipped/Created). Decision drop: `wash-preview-duplicate-flag.md`. Kaylee integration: call `EnrichPreviewWithDuplicateInfoAsync` after `ParseContentAsync`, render badge per `DuplicateReason` key.
- 2025-07-25: **Per-Item Import Result Detail** — Added `ImportItemStatus` enum (Created/Updated/Skipped/Failed), `ContentImportItemResult` class (VocabularyWordId?, Lemma, NativeLanguageTerm, Type, Status, Reason), and `ContentImportResult.Items` (`IReadOnlyList<ContentImportItemResult>`). Every branch in `CommitImportAsync` that increments a count now also appends a detail item. Curated reasons: "Already exists in resource" (DB skip), "Duplicate within batch" (intra-batch skip), "Target language term is empty" / "Native language term is empty..." (failures). Failed branches call `_logger.LogError(...)` with structured fields for Aspire retrieval. Invariant: `Items.Count == CreatedCount + UpdatedCount + SkippedCount + FailedCount`. 8 new tests (32 total ContentImportService tests passing). No schema migration needed. Decision drop: `wash-import-item-result.md`. Skill: `structured-import-results/SKILL.md`. Kaylee integration: Items order matches selectedRows, VocabularyWordId linkable for non-failed rows, Reason shown inline for Skipped/Failed.
- 2026-07-21: **Phrase-Save Bug Fix + Sentence Content Type** — ROOT CAUSE: Phrases branch in ParseContentAsync called `ParseFreeTextContentAsync` (generic FreeTextToVocab prompt) which decomposed input into individual words, silently dropping phrase/sentence entries. River's `ExtractVocabularyFromPhrases.scriban-txt` was deployed but never wired in (TODO comment at line 191). FIX: Rewrote Phrases branch with two-step pipeline: (1) parse delimited lines first to create primary phrase/sentence entries preserving user's original content, (2) run River's AI phrase extraction for constituent words, (3) combine with dedup by target term, (4) filter by harvest flags. Added `ContentType.Sentences` enum value, `HarvestSentences` boolean on both DTOs. Refined ResolveLexicalUnitType heuristic: terminal punctuation (. ! ? 。 ！ ？) + whitespace → Sentence; whitespace only → Phrase; else Word. Updated classifier prompt to recognize Sentences as a fourth type. Updated stale test `ParseContentAsync_ThrowsNotSupportedException_ForPhrasesAndTranscript` → `ParseContentAsync_PhrasesAndTranscript_NoLongerThrow`. No schema migration needed (LexicalUnitType.Sentence already exists). Build green, 138+472 tests passing (1 pre-existing auth failure). Decisions inbox: `wash-sentence-type-plumbing.md`. Kaylee needs: Sentences button in content type selector, Sentences harvest checkbox, `ContentTypeToString` case.
- 2026-04-25: **v1.1 Content Import Backend** — Implemented three new import branches (Phrase, Transcript, Auto-detect) in ContentImportService plus checkbox harvest model. Migration `SetDefaultLexicalUnitType` backfills Unknown→Word/Phrase via space heuristic (dual-provider: Postgres POSITION, SQLite INSTR). Auto-detect uses three-tier confidence gate (>=0.85 auto, 0.70-0.84 suggest, <0.70 manual) with classification running BEFORE any DB persistence. Transcript branch reuses `ExtractVocabularyFromTranscript.scriban-txt` with word-biased extraction. Phrase branch reuses `FreeTextToVocab.scriban-txt` (awaiting River's dedicated prompt). Zero-vocab: persist resource + warning. Chunking: reject >30KB, v1.2 follow-up. DTOs updated with harvest booleans and LexicalUnitType per row. UI adapted (DetectContentType→ClassifyContentAsync). Build green: Shared, MacCatalyst, API. Doc: `.squad/decisions/inbox/wash-v11-backend.md`
- 2026-04-23: **Word/Phrase Feature Completed** — Delivered 9 todos: model-enum (LexicalUnitType), model-constituent (PhraseConstituent), migration-schema (dual-provider), backfill-classification (heuristic), backfill-constituents (lemma tokenization), progress-cascade (passive exposure), shadowing-consumer (LexicalUnitType branching), smart-resource-phrases (new type), smart-resource-phrases-fix (scope bug). Total: 147 tests passing, feature complete, e2e blocked on SQLite migration history mismatch (Captain decision needed). Documented in `.squad/log/2026-04-23T2219Z-wordphrase-squad-wrap.md`.
- 2026-05-20: **Smart Resource: Phrases** — Added `Phrases` smart resource type for practicing all phrase/sentence vocabulary. Uses `LexicalUnitType.Phrase | Sentence` filter with user scoping via `VocabularyProgress.UserId` join (VocabularyWord has no UserProfileId). Intent-driven like Struggling (excluded from planner via `.Where(r => !r.IsSmartResource)` in DeterministicPlanBuilder). Initialization creates 4th smart resource (DailyReview, NewWords, Struggling, Phrases). ResourceVocabularyMapping population via same refresh/bulk-associate pattern. Empty on new users (populates after backfill classification). Build green (Shared, MacCatalyst, Api). Doc: `.squad/decisions/inbox/wash-smart-resource-phrases.md`
- 2025-01-24: **Shadowing LexicalUnitType Consumer** — Modified `ShadowingService.GenerateSentencesAsync()` to branch on `VocabularyWord.LexicalUnitType`: only `Word` entries trigger AI carrier-sentence generation via Scriban template; `Phrase | Sentence | Unknown` use `TargetLanguageTerm` as-is (no AI round-trip). Unknown entries emit structured log `ShadowingUnknownTerm` (Information level, WordId+Term fields) for downstream UI reclassification. As-is sentences populate same `ShadowingSentence` DTO shape (TargetLanguageText=term, NativeLanguageText=translation, PronunciationNotes=null). No public API changes, no Scriban template changes. All target projects (Shared, MacCatalyst, Api) build green. No external call sites — all routing internal to ShadowingService. Doc: `.squad/decisions/inbox/wash-shadowing-consumer.md`
- 2025-01-21: **Phrase Constituent Backfill Service** — Extended `VocabularyClassificationBackfillService` with `BackfillPhraseConstituentsAsync()` to populate `PhraseConstituent` join rows for existing phrases/sentences. Key discovery: VocabularyWord is NOT user-scoped directly — must query through `VocabularyProgress.UserId` with `.Include(vp => vp.VocabularyWord)` to get user-specific vocabulary. Tokenization with Korean particle stripping (`이, 가, 을, 를, 은, 는, 에, 의, 로, 으로, 와, 과, 에서, 에게, 도, 만, 부터, 까지`). Lemma dictionary pre-built once per user (no N+1). Idempotent via existing-constituent guard. Substring fallback for unmatched tokens 2+ chars. Wired into startup after classification backfill in API/WebApp/MAUI (SyncService). Public static `TokenizePhrase(string, string)` for unit testing. Doc: `.squad/decisions/inbox/wash-backfill-constituents.md`
- 2026-04-17: **Help Flyout Integration Pattern (MAUI Hybrid)** — HelpKit library (Plugin.Maui.HelpKit 0.1.0-alpha) now wired into SentenceStudio UI as Help menu item in NavMenu.razor. Used dynamic reflection pattern (Type.GetType() + method invocation) to keep UI project browser-only. MAUI apps see Help button (invokes HelpKit overlay), WebApp doesn't (graceful degrade). Reflects HelpKit portability: library complete, UI trigger now operational.

## Core Context (Summarized from Sessions)

**Backend Architecture:**
- Aspire orchestrates: api, cache (Redis), db (PostgreSQL), marketing, workers, webapp (CoreSync server)
- Production deploy: `azd deploy -e sstudio-prod --no-prompt` publishes to Azure Container Apps (Central US)
- Post-deploy validation critical: active revision must = latest revision (traffic can auto-route to old healthy revision while new crashes)
- Service discovery: `https+http://api` URI resolves via Aspire env vars, falls back to config Services section
- DB migrations: both API (Program.cs:213) and WebApp (Program.cs:151) call MigrateAsync() on startup → auto-apply

**Database & Models:**
- Server DB: PostgreSQL in Aspire (Production: Azure Container Apps managed); mobile: SQLite with CoreSync sync

## Core Context (Summarized History)

[Earlier entries (prior to 2026-04-25) have been reviewed and consolidated. Key patterns:]

- Agent is primary domain specialist for this project area
- Responsibilities established through prior charter/assignment cycles
- Cross-agent coordination pattern: decision inbox → decisions.md → history broadcast
- Team cadence: 3-agent orchestrations typical for major feature work
- Velocity: multiple cycles per week with focus on validation/ship gates

- `src/SentenceStudio.AppLib/SentenceStudio.AppLib.csproj` — added `Microsoft.Extensions.Http.Resilience` PackageReference
- `src/Shared/HelpKitIntegration.cs` — embedding model from config
- `src/SentenceStudio.Shared/Services/AiClient.cs` — constructor accepts model name params
- `src/SentenceStudio.Shared/Services/AiService.cs` — reads tts/image models from config
- `src/SentenceStudio.Shared/Services/Speech/VoiceDiscoveryService.cs` — reads fallback voices from config
- `src/SentenceStudio.AppLib/Services/ElevenLabsSpeechService.cs` — updated Voices class comment
- All 4 `appsettings.json` files — added `AI` config section

### Learnings
- OpenAI SDK 2.8.0 requires `new ApiKeyCredential(string)` — raw string constructor was removed
- `ConfigureHttpClientDefaults` in ServiceDefaults adds `AddStandardResilienceHandler` to ALL factory clients — must NOT double-wrap
- AppLib targets `net10.0` (plain), not MAUI TFMs — build via head projects (MacCatalyst, iOS)
- AppLib has `ImplicitUsings=disable` — must fully qualify `System.Net.Http.IHttpClientFactory`
- AppLib cannot reference ServiceDefaults (Aspire server deps) — inline resilience registration
- `AddChatClient(Func<IServiceProvider, IChatClient>)` overload works in M.E.AI 10.x, returns `ChatClientBuilder` for chaining `.UseLogging()`


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

## 2026-04-27 (Follow-Up): AiClient Polly Routing

**Cycle:** Code Review Follow-Up Fixes  
**Work:** Refactored AIClient.cs to route OpenAI SDK through Polly-backed HttpClient.

**Key Finding:** AIClient was the ONLY remaining naked-constructor site for OpenAI SDK traffic. Code review flagged retry-storm risk on DX24 production path (AiService.cs:145 TTS fallback, non-Aspire mode).

**Solution:** Constructor now accepts `HttpClient httpClient` parameter (no `_apiKey` field). Built `OpenAIClient` with `HttpClientPipelineTransport(httpClient)` + `.GetChatClient()/.GetAudioClient()/.GetImageClient()` pattern (same as Wave 2 sites).

**Integration:** AiService.cs injected `IHttpClientFactory`, calls `CreateClient("openai")` before AIClient construction. Config reading (TTS/image model names) inherited from Wave 2 work — no hardcoded strings.

**Pattern Learned:** Shared types needing HttpClient must accept via ctor (Shared targets `net10.0` plain, no DI site, no access to IHttpClientFactory directly — callers must provide).

**Validation:** Shared + Api builds green, 626 tests passing (1 pre-existing auth failure). Zero regressions. Decision: `wash-aiclient-polly.md`.

**Implication:** All OpenAI SDK traffic in the codebase is now Polly-backed. Production DX24 path (standalone mode) gains retry/circuit-breaker protection on critical TTS fallback surface.

