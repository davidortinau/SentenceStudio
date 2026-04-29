# Kaylee's History

## Core Context

**2025–2026 Overview:**
- Shipped Activity Log feature: Strava-inspired Practice Calendar UI (ActivityLog.razor, ActivityDot, PlanSummaryCard), Bootstrap-based responsive styling, weekly pagination with expandable day details
- Platforms: Both Blazor (SentenceStudio.UI) and native MAUI (HelpKit samples)
- Key patterns: Responsive dot sizing by time (sm/md/lg), filtering via service layer (not client-side), CardStyle + Typography (ss-body1, ss-caption1), Bootstrap utilities for mobile/desktop
- Role in HelpKit: Primary architect of native chat UI — `HelpKitPage` (CollectionView, streaming mutation, auto-scroll), `HelpKitMessageViewModel` (immutable user, mutable assistant messages), `DefaultPresenterSelector` (Transient for freshness), Shell/Window/MauiReactor presenters
- HelpKit platform detection: Dynamic `Type.GetType()` reflection (NavMenu.razor) keeps UI portable; NavMenu.razor Help button shown only in MAUI via `IHelpKit` availability check
- **2025-01-24:** Vocabulary Classification & Constituents — Added Word/Phrase/Sentence picker and autocomplete constituent editor to VocabularyWordEdit.razor and bulk-add preview table to ResourceAdd.razor. Applied heuristic classification on paste/import. EF Core-based PhraseConstituent persistence. See `.squad/decisions/inbox/kaylee-ui-import-edit.md`

## Learnings

- 2026-07-28: **Scriban CVE Bump 6.5.2 → 7.1.0** — Resolved 10 Scriban CVEs (1 critical, 7 high, 2 moderate) via Directory.Packages.props version bump. All builds pass (Api, WebApp, Workers, Shared, AppLib); unit & API tests run (487 + 138 passed; pre-existing failures unrelated to bump). Verified Scriban template syntax compatibility (checked GetClozures.scriban-txt — no breaking changes). NuGet audit shows Scriban now clean; remaining vulns: 3 moderate in OpenTelemetry (GHSA-g94r-2vxg-569j, GHSA-mr8r-92fq-pj8p, GHSA-q834-8qmm-v933) not in scope. Decision doc: `.squad/decisions/inbox/kaylee-scriban-cve-bump.md`.
- 2026-07-27: **Import Content Style Normalization** — Audited and cleaned all bespoke inline styles from ImportContent.razor. Key fixes: replaced custom purple hex badge (`#6f42c1` + inline CSS vars) with `bg-secondary bg-opacity-10 text-secondary`; replaced inline `cursor:pointer` with `role="button"` (matching app.css:1360 generic rule); replaced inline `font-size:0.75rem` with Bootstrap `small` class; removed dead empty class expression on mobile card. Also added Duplicate badge column to preview table using `bg-warning bg-opacity-10 text-warning` after Wash landed `IsDuplicate`/`DuplicateReason` on `ImportRow`. 5 new resx keys (EN+KO). Decision doc in `.squad/decisions/inbox/kaylee-import-style-cleanup.md`. Pattern: type badges always use `badge bg-{color} bg-opacity-10 text-{color}`; clickable non-buttons always use `role="button"` not inline cursor; no hex colors in Razor markup.
- 2026-07-14: **Import Complete Per-Row Detail Table + State Preservation** — Built the detailed Import Complete view with per-row results table (Lemma, Native, Type badge, Status pill, Reason column), filter pills (All/Created/Updated/Skipped/Failed), mobile-responsive card fallback, and clickable rows linking to `/vocabulary/edit/{id}`. State preservation across back-nav via `IImportResultStore` (Singleton, ConcurrentDictionary, 30-min TTL) + URL query param `?completed={guid}`. Key pattern: save result to in-memory store on commit, navigate with key in URL, hydrate from store on init if key present. Reusable skill written to `.squad/skills/blazor-nav-state-preservation/SKILL.md`. Decision doc in `.squad/decisions/inbox/kaylee-import-result-store.md`. 8 new resx keys (en + ko).
- 2026-04-27: **Preview-to-Commit DTO Mapping Discipline** -- When constructing a DTO from an API response for a round-trip (preview -> user edit -> commit), EVERY property on the source DTO must be accounted for in the object initializer. Properties that default to a value (like enums defaulting to their first member) will silently swallow data from the backend. Going forward: when writing or reviewing any DTO-to-DTO mapping in ImportContent.razor (or similar round-trip flows), enumerate all source properties and explicitly decide map-or-skip for each one. This prevents the "silent default" class of bug that caused BUG-2 and BUG-3 to persist despite correct backend fixes.
- 2026-04-25: **v1.1 Import Harvest Checkboxes + Auto-detect Banner** — Replaced disabled v2 badges on Phrases/Transcript/Auto options with full enablement. Added three independent harvest checkboxes (Transcript/Phrases/Words) with defaults per content type (Captain's directive). Implemented three-tier confidence gate for auto-detect (high/medium/low bands at 0.85/0.70 thresholds) with always-visible banner and [Change] override. Confidence gate runs BEFORE any DB persistence per D3. Added `HarvestTranscript`/`HarvestPhrases`/`HarvestWords` booleans to `ContentImportCommit` DTO for Wash integration. No emojis — Bootstrap icons only. Documented in `.squad/decisions/inbox/kaylee-v11-ui.md`.
- 2026-04-23: **Word/Phrase Feature Completed** — Completed ui-import-edit todo: added LexicalUnitType dropdown (Word/Phrase/Sentence) to VocabularyWordEdit.razor with constituent word editor (search-as-you-type autocomplete, badge display, remove buttons). Updated ResourceAdd.razor with preview table showing classification + override per row. Applied VocabularyClassificationBackfillService.ClassifyHeuristic() on paste/import. 14 new UI strings pending localization. Feature shipped, 147 tests passing. Documented in `.squad/log/2026-04-23T2219Z-wordphrase-squad-wrap.md`.
- 2026-04-19: **Phase 2 Localization Review Lockout (Reviewer Rejection Protocol)** — After code review rejection, you were frozen from the fix cycle per protocol. Lead (Zoe) took ownership and found 30 missing resx keys + Skills.razor missing IDisposable/CultureChanged. **Key takeaway:** Run grep-all-keys verification before declaring resx batches complete. Verify all Blazor components with `@inject BlazorLocalizationService` have `@implements IDisposable` + event subscription pattern. This would have caught both blockers pre-review.
- 2026-04-17: **Dynamic Platform Detection in Shared UI** — When wiring platform-specific features (HelpKit) into portable UI projects, use runtime type resolution via `Type.GetType()` + reflection to invoke methods. Keeps UI project browser-only (no MAUI refs), works in both MAUI and WebApp contexts, graceful degrade on missing types. Applied in NavMenu.razor for Help button.
- 2026-04-17: HelpKit Alpha — native chat UI + 3 samples (Shell/Plain/MauiReactor) shipped.
- **2025-01-24:** **Blazor Autocomplete Pattern** — Reused Vocabulary.razor's search-as-you-type autocomplete pattern for constituent word picker. Key: debounce on `oninput`, clear suggestions on select, `showAutocomplete` flag for dropdown visibility, scoped queries (language filter + exclude self + already-selected).

## Recent Work

### Import Complete Theme Alignment (2026-04-29)

**Scope:** Shipped dark theme + WCAG contrast alignment for Import Complete view.

**Changes:**
- Stat tiles: Dashboard card-ss pattern (fs-3 fw-bold + theme color tokens + text-secondary-ss subtitles)
- Filter pills: btn-ss-secondary inactive, btn-{status} (success/info/warning/danger) active to match in-table badges
- Count badges: status-colored bg-{status} instead of hardcoded bg-light
- Contrast fix: text-dark on bg-info Skipped badges (tab pill, count badge, in-table status) for WCAG AA

**Pattern Locked:** bg-info badges require text-dark; other status colors already contrast-compliant. (Decision: `.squad/orchestration-log/2026-04-29T00-05-47Z-code-review.md`)

### Import Wizard P2 Bug Fixes (2025-01-26)

**Scope:** Fixed two parallel-safe bugs in import wizard — silent title validation and Korean harvest localization

**Bug A (silent-title-validation):**
- Added inline Bootstrap validation to new resource title field
- Applied `is-invalid` class + `invalid-feedback` div pattern
- Added `showTitleValidationError` state + `ClearTitleError()` callback
- Modified `CommitImport()` and `SetTargetMode()` to manage error state
- Pattern choice: show on commit attempt + clear on input (more responsive than disabled button)
- New key: `Import_NewResourceTitleRequired` (EN + KO)

**Bug B (kr-localize-harvest):**
- Replaced 8 hard-coded English strings in harvest section with `@Localize["..."]` keys
- Added 11 total keys to both `AppResources.resx` and `AppResources.ko.resx`
- Korean translation approach: "추출" (extract) for all harvest actions, polite imperative tone, technical precision preserved
- Keys: HarvestTitle, HarvestDescription, 4x Labels (Transcript/Sentences/Phrases/Words), 4x Hints, HarvestValidationError

**Also fixed:** Pre-existing syntax error in `ContentImportService.cs` (missing closing brace after staged removal of BuildClassificationPrompt)

**Build:** ✅ Successful (0 errors, 346 pre-existing warnings)

**Decision:** `.squad/decisions/inbox/kaylee-import-p2-ui-fixes.md`

**Status:** Staged, ready for Scribe's batch commit. Visual verification deferred to Captain's hot-reload session.

---

### Vocabulary Classification & Constituents (2025-01-24)

**Scope:** Blazor-only UI update for Word/Phrase/Sentence classification + constituent word editing.


## Core Context (Summarized History)

[Earlier entries (prior to 2026-04-25) have been reviewed and consolidated. Key patterns:]

- Agent is primary domain specialist for this project area
- Responsibilities established through prior charter/assignment cycles
- Cross-agent coordination pattern: decision inbox → decisions.md → history broadcast
- Team cadence: 3-agent orchestrations typical for major feature work
- Velocity: multiple cycles per week with focus on validation/ship gates

7. **RunPreview**: Wired all four harvest flags into `ContentImportRequest` so backend can filter preview rows
8. **StartNewImport**: Resets `harvestSentences = false`

## Learnings

- 2026-07 **Harvest Defaults per Content Type**: Vocabulary = Words only; Phrases = Phrases + Words; Sentences = Sentences + Words; Transcript = Transcript + Words; Auto = all unchecked. The pattern is: primary harvest flag matches content type, Words rides along as secondary (except Auto). User can always override.
- 2026-07 **Validation rule extension**: `ValidateHarvestCheckboxes` uses OR across all four flags (Transcript, Sentences, Phrases, Words). Backend `CommitImportAsync` enforces the same rule independently. Both must stay in sync.
- 2026-07 **RunPreview needs harvest flags too**: The preview request sends harvest flags so the backend's `FilterRowsByHarvestFlags` can filter the preview table. Without this, the preview would show rows the user didn't ask for.
- 2026-04-27: **TEAM CONVERGENCE: Type Filter + Import UI** — Three-agent spawn diagnosed phrase-save bug (generic prompt wired instead of River's dedicated prompt). Kaylee completed Round 1: `Vocabulary.razor` Type filter dropdown added (All/Word/Phrase/Sentence pattern matching Association/Status/Encoding filters). VocabularyWordEdit.razor already supported all types — no changes needed. Client-side filter on loaded list. Round 2 pending: add Sentences button to import content type selector + Sentences harvest checkbox + `ContentTypeToString` case. Team pattern: convergent diagnosis (Wash backend, River prompts, Jayne reproduction) enabled fast Round 1 completion; UI now ready for backend integration.


## Cross-Agent Updates

- 2026-04-27: **Jayne v1.3 Import Detail E2E — SHIPPED** — Jayne validated the Import Complete redesign (commits 35e0ba1, 111418f) with 7/7 E2E tests PASS. Summary cards render correctly, per-row table works, filter pills functional, back-nav state preserved, vocab links navigate correctly, failed rows resilient, zero errors in logs. Feature shipped on feature/import-content. (See: `.squad/log/2026-04-27T14:53:00Z-v13-import-detail.md`)



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

## 2026-04-27 (Follow-Up): Scriban CVE Security Bump

**Cycle:** Code Review Follow-Up Fixes  
**Work:** Bumped Scriban 6.5.2 → 7.1.0 in Directory.Packages.props.

**Key Finding:** Scriban 6.5.2 carries 10 known CVEs (1 critical, 7 high, 2 moderate) affecting template rendering. Import flow uses Scriban templates — vulnerability class real.

**Solution:** Bumped to 7.1.0 (latest NuGet release, all Scriban vulns resolved).

**Validation:** All builds pass (Api, WebApp, Workers, Shared, AppLib). Spot-checked Scriban template syntax — no breaking changes (GetClozures.scriban-txt verified). `dotnet list package --vulnerable` confirms zero Scriban vulns post-bump. 487 + 138 tests pass, no regressions.

**Bonus Finding:** During audit, surfaced 3 moderate OpenTelemetry CVEs (GHSA-g94r-2vxg-569j, GHSA-mr8r-92fq-pj8p, GHSA-q834-8qmm-v933) for separate backlog cycle. Not auto-bumped to keep blast radius tight; recommend pairing with feature release for batch validation. Logged in decisions.md follow-ups.

**Decision:** `kaylee-scriban-cve-bump.md`.

