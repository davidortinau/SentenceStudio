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

---

## 2026-05-01 — P2 Bug Fix: Wire Import Classifier Scriban Template

**Status:** ✅ STAGED — Awaiting Scribe P2 batch commit

**Context:** River was assigned to recalibrate import classifier confidence (P2 `bug4-ai-confidence`). River staged the updated Scriban template but the service-code changes (template wiring + DTO description updates) never landed. Reviewer rejected River's batch. Per Reviewer Rejection Protocol, River is locked out and I was assigned to land the missing service-code piece.

**Work completed:**
1. **Wired Scriban template** (lines 878-893): Replaced inline `BuildClassificationPrompt()` call with canonical loader pattern matching line 747 pattern (OpenAppPackageFileAsync → StreamReader → Template.Parse → Render)
2. **Deleted dead code** (lines 927-955): Removed the now-unused `BuildClassificationPrompt()` private method (28 lines)
3. **Strengthened DTO descriptions** (lines 1996, 2004): Updated both `ContentClassificationResult.Confidence` and `ContentClassificationAiResponse.Confidence` to emphasize full range usage ("USE THE FULL RANGE — do NOT cluster at 0.85+") with brief band guidance

**Build & Test results:**
- ✅ Shared build: 656 warnings, 0 errors (5.76s)
- ✅ UI build: 192 warnings, 0 errors (15.21s)
- ✅ ContentImport tests: 36 passed, 0 failed (1s)

**Coordination notes:**
- River's `ClassifyImportContent.scriban-txt` was already staged (not in my scope, syntax-checked for safety — no issues found)
- Wash's `isNewResource` transaction fix is included in my staged changes (part of P2 batch, marked "already approved" in task brief)
- Kaylee's razor/resx changes already staged (no overlap with my work)

**Decision:** `.squad/decisions/inbox/simon-wire-classifier-scriban.md`

**Learnings:**
- **Scriban loader pattern**: `_fileSystem.OpenAppPackageFileAsync` is the canonical way to load .scriban-txt templates in this codebase. DI convention: `IFileSystemService` is already injected into most services. Always match the existing pattern (search for `Template.Parse` to find examples) rather than inventing a new one.
- **Reviewer Rejection Protocol escalation**: When a staged artifact is rejected and the original author is locked out, another agent completes only the missing pieces without duplicating work. My scope was service-code only — River's template was already staged and correct.
- **DTO description strengthening**: Microsoft.Extensions.AI 10.5 uses `[Description]` attributes to guide structured output. Strengthening descriptions (especially range directives like "USE THE FULL RANGE") directly impacts AI behavior without changing prompt logic.

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


## 2026-04-10: Polly-backed OpenAI Client Wiring Completion (Reviewer Rejection Lockout)

**Status:** ✅ COMPLETE — Awaiting Scribe commit workflow  
**Branch:** `feature/import-content`  
**Task:** Complete Polly/IHttpClientFactory wiring for remaining naked OpenAI client instantiations

### Context

Code review on commit 183e4e3 (Wash's initial Polly refactor of `AiClient.cs`) identified two remaining naked OpenAI client sites that bypassed Polly resilience. **Wash locked out** under Reviewer Rejection Lockout — Simon assigned as independent backend specialist to complete the wiring.

### Work Completed

Fixed two remaining naked OpenAI client sites:

1. **`AiService.cs` (lines 50-51):** Refactored `AudioClient` and `ImageClient` construction to use the already-injected `_httpClientFactory`, applying Wave 2 pattern (HttpClientPipelineTransport → OpenAIClientOptions → OpenAIClient → Get*Client methods)

2. **`HelpKitIntegration.cs` (line 91-93):** Fixed naked `OpenAIClient` in `IEmbeddingGenerator` DI registration by resolving `IHttpClientFactory` from service provider and applying same Wave 2 pattern

### Key Learnings

1. **DI Factory Lambda Pattern:** When registering services via `TryAddSingleton<T>(sp => ...)`, the lambda has access to the full service provider. Can resolve dependencies like `IHttpClientFactory` via `sp.GetRequiredService<T>()` even though the factory itself doesn't declare them as constructor parameters.

2. **Named HttpClient Availability:** The `"openai"` named client is registered in all three host entry points (`Program.cs` in Api, WebApp, and via `SentenceStudioAppBuilder` for MAUI). HelpKitIntegration is called AFTER `UseSentenceStudioApp()`, so the named client is guaranteed to be available when HelpKit's DI registrations run.

3. **Wave 2 Pattern Universality:** The four-line HttpClientPipelineTransport pattern works identically for ChatClient, AudioClient, ImageClient, and EmbeddingClient. The `OpenAIClient` base instance handles the transport, then the `Get*Client()` methods return typed clients that inherit the transport configuration.

4. **Field Type Preservation:** When refactoring constructors, the field types (`AudioClient`, `ImageClient`) don't change — they're the return types of the `Get*Client()` methods. Only the construction mechanism changes (naked ctor → factory method on properly-configured base client).

### Verification

- Grep confirmed zero naked OpenAI constructors remain in codebase
- All matches show proper `OpenAIClientOptions { Transport = transport }` wiring
- SentenceStudio.Shared and SentenceStudio.Api build clean
- 488 unit tests passed (100%)
- 138/139 API tests passed (1 pre-existing auth test failure, unrelated to OpenAI changes)

### Reference Patterns Used

- `src/SentenceStudio.Shared/Services/AiClient.cs:35-42` (Wash's Wave 2 refactor)
- `src/SentenceStudio.Api/Program.cs:248-251`
- `src/SentenceStudio.AppLib/Setup/SentenceStudioAppBuilder.cs:55-59`
- `src/SentenceStudio.WebApp/Program.cs:145-149`

### Deliverables

- Staged changes: `AiService.cs`, `HelpKitIntegration.cs`
- Decision file: `.squad/decisions/inbox/simon-polly-completion.md`
- This history entry

**Verdict:** Task complete. All OpenAI clients now route through Polly. Zero naked constructors remain.


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


---

Team update (2026-06-17T16:08:31-05:00): Concern #2 per-user timezone fix — LANDED AND APPROVED.

Simon's work (commit 03750fad): fixed cross-tenant freshness leak (Zoe blocker #2). Added `VocabularyProgressRepository.GetByWordIdsForUserAsync` — applies UserId filter server-side before word-id filter; empty-userId safe default returns empty list (freshness refinement, not gate); retains 500-row batch optimization; warning doc on old `GetByWordIdsAsync`. Switched `ProgressService.ApplyFocusVocabularyFreshnessAsync:985` to the new scoped method. Build: 0 errors. Jayne pinned the fix with a multi-tenant isolation test (real in-memory SQLite, 2 users, shared word id).

Carry-forward for Simon:
- Switch `VocabularyProgressService.cs:280` from `GetByWordIdsAsync` to `GetByWordIdsForUserAsync` (perf improvement — avoids loading other tenants' rows into memory before post-filter at :284). Not a correctness fix (safe today), but the right long-term pattern.
