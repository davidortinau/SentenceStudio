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


---

## 2026-04-29: Aspire Mac Catalyst Environment Variable Injection Analysis

**Cycle:** Local-Dev Infrastructure Diagnosis (no code changes)  
**Work:** Root cause analysis for Captain's report: Aspire→Mac Catalyst launch hits production Azure API + UI freezes.

**Key Finding:** Aspire.Hosting.Maui 13.3.0-preview does NOT implement environment variable injection for Mac Catalyst.
- Android/iOS use custom MSBuild targets files (documented in XML annotations)
- Mac Catalyst uses `dotnet run` (which *should* support env vars natively) but no injection machinery exists in the package
- Result: `services__api__https__0` env var never reaches app → falls back to `appsettings.Production.json` (Azure)

**Secondary Finding:** `InitializeDatabaseAsync().Wait()` in SentenceStudioAppBuilder.cs (line 147) blocks main thread during boot. If API call hangs, UI deadlocks before rendering.

**Evidence Chain:**
1. AppHost.cs: `.WithReference(api)` is correct but doesn't inject env vars for Mac Catalyst
2. Service Discovery: Precedence is env vars > appsettings.Production.json. Missing env var → Azure fallback
3. appsettings.Production.json: Correctly points to Azure. Loaded when `ASPNETCORE_ENVIRONMENT != "Development"`
4. Aspire.Hosting.Maui: Has `MauiAndroidEnvironmentAnnotation`, `MauiiOSEnvironmentAnnotation`, but NO equivalent for Mac Catalyst

**Hypothesis Confirmed:** Aspire assumes Mac Catalyst can receive env vars through `dotnet run`'s process environment, but Mac Catalyst .app bundles launched via `dotnet run` do NOT inherit environment variables when using NSWorkspace or xcrun internally.

**Fix Options for Captain:**

| Option | Approach | Pros | Cons | Recommend |
|--------|----------|------|------|-----------|
| **A** | Wait for Aspire team | Zero local code | Blocks dev NOW; unknown ETA | Long-term: file bug |
| **B** | Custom MSBuild targets (mirrors Android/iOS) | Works NOW; proven | Custom Aspire code; maintenance | Medium-term: Wash implements |
| **C** | Launch script with env vars | Dead simple; works TODAY | Two-step launch; loses dashboard | Short-term: verify hypothesis |
| **D** | Hardcode localhost in appsettings.Development.json | No Aspire code | Port hardcoded; fragile | Band-aid only |

**Recommended Path:**
1. Short-term (today): Captain tests Option C (5 minutes) to confirm hypothesis
2. Medium-term (week): Wash implements Option B (custom targets file)
3. Long-term: File bug with Aspire team

**Verification Steps:**
```bash
# Terminal 1
aspire run --no-launch-profile  # Note API port

# Terminal 2
export services__api__https__0="https://localhost:7234"
dotnet build -t:Run -f net10.0-maccatalyst -c Debug src/SentenceStudio.MacCatalyst/SentenceStudio.MacCatalyst.csproj
```

Expected: App loads local test data (not Azure). If UI still freezes, check Console.app logs for checkpoint messages from SentenceStudioAppBuilder.cs.

**Decision:** Awaiting Captain's fix option call. Full analysis in `.squad/decisions/inbox/wash-aspire-maccatalyst-env-investigation.md`.



---

## 2026-04-29: Verified iOS Release Build Recipe — net11p3 Swap is Broken

**Cycle:** Build verification (post-publish truth check)  
**Triggered by:** Captain's challenge — Coordinator's claim that "net11p3 is broken" was unverified because no `dotnet clean` was performed between SDK swaps. Captain (correctly) suspected obj/ contamination.

### Verification

Performed a clean re-test:
1. Swap to net11p3 (`11.0.100-preview.3.26209.122`)
2. **Full wipe** of `obj/` and `bin/` from `src/SentenceStudio.UI/` and `src/SentenceStudio.iOS/` (not just `dotnet clean`)
3. Build iOS Release with the runbook command
4. **Result: 31 errors, identical signatures to Coordinator's earlier failed build.** Net11p3 is genuinely incompatible with `ImportContent.razor`.
5. Restore net10 GA.

### Truth

- **Coordinator's claim was correct.** The 31 errors are real, not contamination.
- **But Coordinator's process was wrong.** Without `dotnet clean` (or full obj/bin wipe) between SDK swaps, the claim was unverified. Captain caught this.
- **Canonical iOS Release recipe going forward:** stay on net10 GA + `-p:ValidateXcodeVersion=false`. Do NOT swap global.json.

### Root cause of net11p3 failure

Razor source-generator regression in net11 Preview 3:
- `@inject` directives parsed as raw C# (CS9348) instead of being lifted into the component partial class
- Generator emits two colliding `partial class ImportContent` halves (CS0101/CS0102 with empty member names)
- Cascades into CS0246 type-not-found on every injected service and CS0426 / CS0535 downstream

### Process Rule Captured (new for all agents)

**When swapping SDK versions via `global.json`, always wipe `obj/` AND `bin/` from affected projects — `dotnet clean` is NOT sufficient.** Razor source-gen output and incremental build state can survive `dotnet clean` and masquerade as SDK incompatibility.

```
find <project-dirs> -name obj -type d -exec rm -rf {} +
find <project-dirs> -name bin -type d -exec rm -rf {} +
```

This is the kind of hygiene rule that distinguishes "I think it's broken" from "I've verified it's broken."

### Artifacts

- Decision drop: `.squad/decisions/inbox/wash-ios-build-recipe-verified.md`
- Build log: `.squad/orchestration-log/2026-04-29-wash-net11p3-clean-build.log`
- FU-4 updated in `.squad/followups.md` with verified facts and revised runbook-rewrite guidance

### Implication

`docs/deploy-runbook.md` Step 2a still needs rewriting (FU-4 owns that task). The replacement is the `-p:ValidateXcodeVersion=false` recipe, NOT a "use cleaner SDK swap" recipe — net11p3 is dead until the upstream Razor regression is fixed.

## Learnings — net11p3 Razor SG repro (2026-04-28)

Confirmed Captain's hypothesis: net11.0.100-preview.3.26209.122 is NOT broadly broken — the regression is pattern-specific. The failing pattern is a **switch expression returning `RenderFragment` lambdas with inline Razor markup**, used inside an `@code` block of a `.razor` file.

### Reproduction recipe
1. Standard `dotnet new maui-blazor` solution (PeeThreeRegression). Builds clean on net11p3 by default.
2. Add a single page `Pages/RazorSgRepro.razor` with `@inject NavigationManager Nav` plus a `RenderFragment` switch expression containing inline `<span>` markup in each arm.
3. `dotnet build PeeThreeRegression.Shared` — fails.

### Diagnostic fingerprint
- `CS0101: The namespace 'X' already contains a definition for ''` (empty member name)
- `CS0102: The type 'Y' already contains a definition for ''` (empty member name)
- Cascading `CS0246` on every type referenced after the switch — including `@inject` services (NavigationManager) and inline-defined enums (SampleType).
- In files with **multiple** `@inject` directives (like ImportContent.razor in SentenceStudio), this also triggers `CS9348: The compilation unit cannot directly contain members`. The minimal repro only has 1 `@inject`, which is enough to surface CS0246 cascades but not CS9348.

### Root cause hypothesis
The Razor SG synthesizes private members for each switch arm's RenderFragment but fails to assign them stable identifiers (emits empty `""` names). The duplicate empty names collide → CS0101/CS0102 → entire compilation unit becomes invalid → cascading CS0246 on everything else. CS9348 surfaces when the SG additionally misplaces injected fields at compilation-unit scope.

### Workaround
Refactor each switch arm to delegate to a separate named `RenderFragment` helper method (single-line `@<span ...>` body). This avoids the inline-markup-inside-switch-arm pattern entirely.

### Artifacts
- Repro zip: `~/work/peethree-repro-artifacts/peethree-net11p3-repro.zip` (252 KB)
- Build log: `~/work/peethree-repro-artifacts/peethree-net11p3-repro.log`
- Both are outside SentenceStudio so they survive any repo cleanup.
