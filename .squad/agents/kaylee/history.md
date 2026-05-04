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

- 2026-04-29: **iOS Release build for Xcode 26.3 mismatch — net10 SDK + `ValidateXcodeVersion=false` is canonical, NOT the net11p3 swap.** During the import-content production deploy, the deploy-runbook.md Step 2a prescription (swap `global.json` to `11.0.100-preview.3.26209.122` so the SDK knows about Xcode 26.3) failed: building `SentenceStudio.iOS` under net11p3 produced **31 errors in `src/SentenceStudio.UI/Pages/ImportContent.razor`** (CS9348, CS0246, CS0426 — Razor SDK on net11p3 cannot compile `@inject` directives + `LexicalUnitType.Phrase` references). The clean recipe: stay on the net10 GA SDK (`10.0.101`) and pass `-p:ValidateXcodeVersion=false` to skip the Xcode version assertion. This shipped iOS to DX24 cleanly. Action item: rewrite `docs/deploy-runbook.md` Step 2a to drop the net11p3 swap and document the `ValidateXcodeVersion=false` flag instead (tracked in `.squad/followups.md`). Cross-ref: orchestration log `.squad/orchestration-log/2026-04-29T014444Z-publish-import-content.md`.
- 2026-04-29: **Round 3 Import Styling Iteration Cost + Blazor Render Cache Lesson** — Three-round refinement cycle on Import Complete Details section revealed two key learnings: (a) **Canonical pattern as starting point:** Vocabulary.razor:549-570 is the ratified borderless list-group row pattern; structural choices (table vs. list-group, card wrappers) should reference existing pages first, not iterate style-first. Rounds 1–2 added code that had to be removed. (b) **Blazor markup cache invalidation:** Structural changes to HTML element trees (converting `<table>` to `.list-group`) require full webapp resource restart to flush Blazor's render cache; component-level hot reload insufficient for Playwright verification of new markup. Lesson: after HTML tree restructuring, restart the webapp resource, not just recompile. Commit: `13435f9` "style(import): refactor Import Details to borderless list-group matching Vocabulary pattern". Orchestration: `.squad/orchestration-log/2026-04-29T01-18-17Z-kaylee-round3-import-styling.md`.
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

### First-Sync Routing Fix Implementation (2026-05-02)

**Scope:** Fixed LoginPage routing to wait on initial sync before deciding onboarding vs. dashboard (PR #188 `fix/firstsync-routing-overlay`).

**Problem:** On fresh Mac Catalyst installs, signing into an existing account routed to `/onboarding` because `is_onboarded` Preferences flag was false. The flag is a device cache, not source of truth; server-side profile state is the real authority. Fix required extracting routing logic into a testable service + adding sync-aware state transitions.

**Implementation:**
- **New files:** `PostLoginRouter` service (routing decision logic), `PostLoginRouterTests` (9 unit tests with regression comment blocks), `SyncOverlay` component (reusable sync-in-progress spinner)
- **Modified files:** LoginPage (simplified to call PostLoginRouter), Index (added sync-aware new-user check), MainLayout (single routing gate), SyncService (added IsInitialSyncInProgress flag + InitialSyncCompleted event), IdentityAuthService (synchronously flips flag before Task.Run), MauiProgram (DI registration), IPreferencesService (test abstraction)
- **Test coverage:** 7 routing paths (existing account, new account, in-progress sync, error handling, edge cases, stuck-overlay prevention)
- **Build:** 509 tests passing, no warnings, no regressions

**Decision:** `.squad/decisions.md` — "Post-Login Routing Must Wait on Initial Sync" + "First-Sync Routing Implementation"

**Status:** Code review in flight; awaiting approval before merge.

---

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


### Import Complete Style Fidelity Rework (2026-04-29)

**Cycle:** Style Fidelity Fix — Captain directive enforcement  
**Work:** Fixed three visual issues on ImportContent.razor Import Complete view to align with app-wide patterns.

**Key Finding:** Coordinator's commit 7321d48 on `feature/import-content` invented styling that drifted:
- Stat tiles had outer card wrapper (darkening subtiles into background)
- Filter pills used `btn-{status}` classes (different shades than in-table `bg-{status}` badges)
- Table header used `table-light` class (white on white in dark mode)

**Solution:** Applied canonical patterns from Index.razor (tiles) and Vocabulary.razor (tabular lists):
- Removed outer card wrapper — tiles now sit directly on page background (matches Dashboard)
- Switched filter pills to use `bg-{status}` classes (exact match with in-table badges)
- Removed `table-light` class from thead

**Code Review Issue:** First pass failed WCAG AA contrast check:
- `bg-success` + black text: 2.44:1 (fails AA, needs 4.5:1)
- `bg-danger` + black text: 3.88:1 (fails AA)

**Fix:** Coordinator added `text-white` to all 8 color-critical elements (4 buttons + 4 in-table badges). New contrast:
- `bg-success` + white: 5.89:1 ✅
- `bg-danger` + white: 4.78:1 ✅

**Lesson:** WCAG checks must be part of color+contrast decisions. Process: (1) Match pattern, (2) Check contrast ratio, (3) Add `text-white` or `text-dark` for 4.5:1+ AA compliance.

**Directive Locked:** New rule binding all Blazor agents: "When styling Blazor pages, ALWAYS reference existing pages as canonical (Dashboard for tiles, Vocabulary for lists). Do NOT invent new card structures, header rows, or color treatments." Decision: `2026-04-29T00:23Z-copilot-directive-style-fidelity`.

**Committed:** 437eaac

**Files Changed:** src/SentenceStudio.UI/Pages/ImportContent.razor (lines 33-74, 82-116, 121)

**Build:** ✅ 0 errors, 107 pre-existing warnings


## Learnings

### Razor source generator regression (net11p3) — switch-expr-of-RenderFragment-with-inline-markup
**Date:** $(date +%Y-%m-%d)
**File:** `src/SentenceStudio.UI/Pages/ImportContent.razor`

**The broken pattern:** A `static RenderFragment Helper(T x) => x switch { Case => (__builder) => { <span>...</span> }, ... };` — switch expression returning multiple `RenderFragment` lambdas with inline Razor markup in each arm. Builds clean on net10 GA, but on net11 Preview 3 the Razor SG miscompiles and emits 31 errors (CS9348 on every `@inject`, CS0101/CS0102 with empty type/member names, cascading CS0246/CS0426). Wash repro'd it in a clean MAUI Blazor project (PeeThreeRegression) for the upstream issue.

**The fix:** Replace each switch-expression-of-RenderFragment with a tuple-returning meta helper, then inline the markup at the call site:

```csharp
private static (string CssClass, string IconClass, string Label) GetTypeBadgeMeta(LexicalUnitType type) => type switch
{
    LexicalUnitType.Word => ("bg-primary bg-opacity-10 text-primary", "bi-fonts", "Word"),
    ...
};
```

```razor
@{ var typeMeta = GetTypeBadgeMeta(item.Type); }
<span class="badge @typeMeta.CssClass"><i class="bi @typeMeta.IconClass me-1"></i>@typeMeta.Label</span>
```

**Don't do this in Razor again:** switch expression whose arms are `(__builder) => { <markup/> }` lambdas. Pure data tuples + inline markup is safer and cleaner regardless of SG bugs.

- 2026-04-29: **Blazor RenderFragment Conditionals Pattern** — When hiding content inside Blazor RenderFragments like `<PrimaryActions>` or `<SecondaryActions>`, the `@if` MUST go inside the RenderFragment, not wrapped around it. Wrapping the entire RenderFragment causes a compile error "Unrecognized child content inside component". Correct pattern: always define the RenderFragment tags, then conditionally render children inside. Applied in ResourceEdit.razor for smart resource read-only mode (hide Save/Delete buttons for system-managed resources).

- 2026-04-29: **Smart Resource Read-Only UI Pattern** — Implemented read-only mode for `ResourceEdit.razor` when `resource.IsSmartResource == true` (DailyReview, NewWords, Struggling, Phrases, Sentences). Key points: (a) Disable inputs with `disabled="@resource.IsSmartResource"`, don't hide them — keeps screen-reader accessible and shows existing values; (b) Hide mutation buttons (Save/Delete/Generate/Import) using `@if (!resource.IsSmartResource)` INSIDE RenderFragments; (c) Server-side guards in all handlers (`SaveResource`, `ImportVocabulary`, `HandleFileImport`, `GenerateVocabulary`, `RequestDelete`) with warning logs; (d) Keep view-only features working (vocabulary list display, info banner, navigation). No new localization strings added — reused existing `ResourceEdit_SmartResource` and `ResourceEdit_AutoUpdated`. Pattern is reusable for any system-managed entity (SkillProfile, activity history, reports). Decision doc: `.squad/decisions/inbox/kaylee-resourceedit-readonly.md`.

## 2026-04-29 — ResourceEdit Read-Only + net11p3 Workaround + Upstream Policy

**Shipped in PR #183 (commit f8b4567):**
- ResourceEdit.razor read-only for IsSmartResource: 8 disabled inputs, mutating buttons hidden, 6 server-side guards
- Page title changes to "View Smart Resource" when IsSmartResource=true
- Bootstrap info banner explains auto-management
- Defense-in-depth pattern: UI disabled inputs → hidden buttons → server-side guards
- Post-code-review addition: ConfirmDelete() server-side guard (caught by reviewer)
- Pattern reusable for any system-managed entity

**net11p3 Workaround Details:**
- ImportContent.razor refactored from RenderFragment switch expressions to tuple-returning meta helpers
- Commit 2359da8 refactor: `RenderTypeBadge`/`RenderStatusBadge` → `GetTypeBadgeMeta`/`GetStatusBadgeMeta`
- File reduction: 1168→1145 lines
- Outcome: builds clean on both net10 and net11p3 (was 31 errors on net11p3)
- This workaround unblocked iOS device build with net11p3 SDK (new canonical recipe)

**Upstream Policy Directive (Codified):**
- Captain's policy: Default = workaround in our code + comment referencing filed issue + recheck on each upstream release
- Exception: If upstream is a codebase we have locally (maui-labs, maui), unblock by PR'ing the fix
- When uncertain about whether to upstream, ask Captain and remember the choice
- Applied to dotnet/razor#13117: workaround applied (not PR'd upstream); recheck trigger documented in code

**Key Learnings:**
- When refactoring to work around upstream issues, include a comment referencing the upstream URL
- "Recheck on each upstream release" reminder creates a natural cleanup trigger when the issue is fixed
- Defense-in-depth pattern (UI + server guard) is essential for production-critical contracts like "smart resources are read-only"

## 2026-05-02 — Blazor Hybrid FirstRender JS-Init Pattern (Reusable Pattern)

**Documented by:** Troubleshooter; pattern via Kaylee history  
**Issue:** Dashboard doesn't show Skill Profile / vocab stats on cold post-login start

**Pattern:**
When JS interop code (e.g., Tom Select initialization) is gated on `OnAfterRenderAsync(firstRender:true)` AND that component has a conditional mode flag (like `isTodaysPlanMode`), **deferred/async mode changes can skip the firstRender gate entirely**. On cold start with `SyncService.IsInitialSyncInProgress==true`, the mode might remain at default (true) during first render, causing the JS init gate to fire when unwanted. When sync completes and the mode flips, the second render has `firstRender:false`, so JS init never runs.

**Solution (Index.razor pattern):**
After mode-changing operations in lifecycle methods (e.g., `OnInitialSyncCompleted` after `LoadDashboardAsync()`), explicitly re-trigger JS init if conditions now allow it:

```csharp
private async Task OnInitialSyncCompleted()
{
    await LoadDashboardAsync();
    StateHasChanged();
    
    // Re-init JS if mode flip now allows it
    if (displayMode == DashboardDisplayMode.ChooseOwn && jsModule != null)
    {
        await Task.Delay(50); // Allow DOM to settle
        await InitChooseOwnSelectorsAsync();
    }
}
```

**Why this works:**
- Decouples firstRender gate from mode-dependent initialization
- Explicit re-trigger guarantees init runs when conditions are met, regardless of render sequence
- 50ms delay gives the browser time to populate the DOM with new elements
- Reusable for any Blazor Hybrid component with conditional JS-init logic

**Applied in:**
- `src/SentenceStudio.UI/Pages/Index.razor` — Tom Select dropdowns, post-sync re-init
- Skill: `.squad/skills/blazor-hybrid-firstrender-jsinit/SKILL.md`

**For future Blazor work:**
If you encounter "my JS component initialized on nav-back but not on first-load," check for async mode flags + firstRender gates. This pattern resolves it.

## Learnings — 2026-05-02 — Vocab Quiz UI cluster (Stream A) — PR #196

Shipped four UI fixes in `src/SentenceStudio.UI/Pages/VocabQuiz.razor` as a single PR.
- #190 (distractor scope), #192 (Submit button), #193 (prompt audio direction), #194 (anti-cheat info panel).
- New field: `distractorScope: List<VocabularyWord>`. Populated in `LoadVocabulary` after dedup. Source of truth for MC distractor sampling.
- Helpers added: `GetPromptAudioText` / `GetPromptAudioLanguage` (line ~1559). Switch on `promptUsesNativeLanguage`. They are the canonical way to ask "what should the prompt-side audio say." `GetTargetAudioText` / `GetTargetAudioLanguage` are LEFT in place but are dead code — safe to delete in a follow-up.
- Method split: `CreateChoiceOption(item)` is now a thin wrapper over `CreateChoiceOptionForWord(VocabularyWord)`. The Word-level overload is what allows distractor sampling from raw scope.
- Resource keys live at `src/SentenceStudio.Shared/Resources/Strings/AppResources*.resx`. Key prefix `VocabQuiz_` is the convention. `Designer.cs` MUST be committed alongside resx changes.
- Aspire dev loop gotcha: restarting `webapp-rkmtvzgr` does NOT recompile. To pick up Razor changes, you MUST `dotnet build src/SentenceStudio.WebApp/SentenceStudio.WebApp.csproj` first, then `resource-stop` + `resource-start` the webapp resource. A `resource-restart` while the app is in `Unknown` state throws "Unhandled exception" — use start instead. The `webapp-rebuilder-ntjtbzbg` resource has no commands exposed from the MCP perspective.
- Verifying the right binary is loaded: `strings src/SentenceStudio.WebApp/bin/Debug/net10.0/SentenceStudio.UI.dll | grep -E "GetPromptAudioText|distractorScope"` is a fast sanity check before re-clicking through Playwright.
- Direct nav to `/vocab-quiz?...` after a webapp restart redirects to `/`. Always re-enter through the dashboard's "Vocabulary Review" tile so the planItem context attaches.
- Korean: "Submit" → "제출" is standard (Naver, gov forms). Used directly without a localize agent round-trip.

### 2026-04-29 — #189 follow-up appended to PR #196

Jayne flagged service-side is clean (her repro tests pass on `main`); the
"2 attempts / 50% accuracy" confusion in #189 is purely UI rendering.

**Panel change (`VocabQuiz.razor` ~line 412–448):** Replaced the two stat
grids with one streak-truth grid. Stripped legacy metadata readouts:
`IsKnown`, `IsUserDeclared`, `VerificationState`. Added `EffectiveStreak`
(was missing from rendering despite being on the model as a computed
prop: `CurrentStreak + ProductionInStreak * 0.5f`). Final allowlist:
TotalAttempts, CorrectAttempts, Accuracy, CurrentStreak,
ProductionInStreak, EffectiveStreak, MasteryScore, status badge.

Schema fields on `VocabularyProgress` (incl. `RecognitionAttempts`,
`ProductionAttempts`, `IsKnown`, `VerificationState`) untouched —
sync/back-compat preserved.

**Attempt-recording audit:** Inspected all four call sites in
`VocabQuiz.razor`:
- L980 `RecordAttemptAsync` — sentence-shortcut, intentionally records
  one attempt per credited sentence (loop body). Correct.
- L1282 `RecordPendingAttemptAsync` in `NextItem` — records pending
  attempt as-is. Correct.
- L1394 `RecordPendingAttemptAsync` in override-to-correct — mutates
  `pendingAttempt.WasCorrect = true` then records. Correct.
- L1525 `RecordPendingAttemptAsync` in `DisposeAsync` — final flush.
  Correct.

The method is idempotent: `if (pendingAttempt == null) return;` guard +
`pendingAttempt = null;` after the snapshot. Second call no-ops. The
override-to-correct path then NextItem path is the worst case; second
record is the no-op. **No double-invocation.**

**Convention learned:** Resource keys can become orphans when fields are
stripped from a panel (`VocabQuiz_IsKnown` is now unused). I noted this
in PR description under "Out of scope" — low-priority cleanup, not worth
churn on resx files. Future janitor pass should grep for unreferenced
`VocabQuiz_*` keys.

**Build verify only — no e2e re-run.** Justification: the change is
mechanical rearrangement on the same offcanvas that I already screen-
shotted for #194, all rendered values are existing properties on
`p` (the progress reference), and the grid layout is identical bootstrap
class structure. Risk of visual regression is negligible.

PR #196 body amended via `gh pr edit` to add `Closes #189` and a #189
section explaining the panel cleanup + audit outcome.

### 2026-05-03 — PR #196 merged
Squash-merged to `main` (commit `c996299`). Closes #189, #190, #192, #193, #194. Branch `fix/vocab-quiz-ui-cluster-189-194` deleted. Follow-up issues filed by team: #197 (decouple MasteryScore from SessionRotationReady) and #199 (`MakeAttempt` test helper missing `DifficultyWeight`).

## 2026-07-29: Auth Persistence Client-Side Fixes

**Scope:** Implemented client-side fixes for auth persistence bugs that caused spurious logouts.

**Problem:** Users were being logged out unexpectedly after app restart and on cold start. Root causes: (1) concurrent refresh-token races when two callers (MauiAuthenticationStateProvider + AuthenticatedHttpMessageHandler) both hit the API at startup, causing the first to succeed and the second to destroy the session with a 401; (2) empty `_cachedToken` at startup widening the race window; (3) Mac Catalyst Debug builds using Preferences fallback (wiped on reinstall) instead of Keychain.

**Implementation:**
- **Fix A — Single-flight refresh:** Added `SemaphoreSlim _refreshLock` + `Task<AuthResult?>? _inflightRefresh` to `IdentityAuthService`. Both `SignInAsync()` and `GetAccessTokenAsync()` now lock, check for in-flight task, and await the same refresh if one exists. Prevents concurrent `/api/auth/refresh` POSTs with the same token.
- **Fix F — 2 consecutive 401s:** Added `_consecutiveAuthFailures` counter. Only clear refresh token after 2 consecutive 401/403 responses. Reset counter on success or transient failure. Defends against fluke server errors and race-induced single failures.
- **Fix G — Pre-load token cache:** Added fire-and-forget `Task.Run(() => authService.SignInAsync())` in `SentenceStudioAppBuilder.InitializeApp` after database init. Ensures `_cachedToken`/`_cachedExpires` are populated before the first HTTP request, reducing the race window.
- **Fix D — Log SecureStorage fallback:** Injected `ILogger<MauiSecureStorageService>` and log warning when `_usePreferencesFallback` flips: "SecureStorage unavailable on this platform — falling back to Preferences. Tokens will NOT survive app reinstall."
- **Catalyst Debug Keychain Entitlements:** Added `keychain-access-groups` entitlement to `Entitlements.plist` and wired `<CodesignEntitlements>` in csproj for both Debug and Release. Fixes SecureStorage so Debug builds also persist tokens across restarts.

**Build Verification:** ✅ Both AppLib and MacCatalyst projects build successfully (0 errors, warnings are pre-existing OpenTelemetry CVEs).

**Learnings:**
- **Single-flight async pattern:** Use `SemaphoreSlim` + cached `Task<T>?` to collapse concurrent callers to a single async operation. Lock-check-start-await-finally-null pattern prevents double-fire. Reusable for any service method where concurrent calls should collapse (config refresh, feature flag fetch, token refresh).
- **Catalyst entitlements for Debug:** Debug builds need the same keychain entitlements as Release builds to access SecureStorage on Mac. Without `keychain-access-groups` entitlement, MAUI silently falls back to Preferences which are wiped on uninstall. Apply entitlements unconditionally for both configs.
- **Preferences fallback warning policy:** When platform APIs degrade to insecure fallbacks, log once at the transition point with clear user-impact consequences. Helps diagnose persistence issues in bug reports (check logs for "falling back to Preferences").

**Files Changed:**
- `src/SentenceStudio.AppLib/Services/IdentityAuthService.cs` — single-flight locking, 2-401 gate
- `src/SentenceStudio.AppLib/Abstractions/MauiSecureStorageService.cs` — logger injection, fallback warning
- `src/SentenceStudio.AppLib/Setup/SentenceStudioAppBuilder.cs` — startup token cache preload
- `src/SentenceStudio.MacCatalyst/Platforms/MacCatalyst/Entitlements.plist` — keychain-access-groups
- `src/SentenceStudio.MacCatalyst/SentenceStudio.MacCatalyst.csproj` — CodesignEntitlements wiring

**Decision:** `.squad/decisions/inbox/kaylee-auth-single-flight.md`

**Skill:** `.squad/skills/single-flight-async/SKILL.md` — reusable pattern for collapsing concurrent async calls

