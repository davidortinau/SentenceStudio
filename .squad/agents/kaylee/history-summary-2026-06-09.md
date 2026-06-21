# Kaylee History Summary (2026-05-04 to 2026-06-09)

**Agent:** Kaylee — UI/UX & Activity UI Implementation  
**Role:** Activity interface design, state management, responsive patterns  
**History Size:** 81972 bytes → summarized to preserve decisions & patterns

## Key Delivery Areas

1. **NumberDrill Activity UI (Wave 1–4)**
   - Phase 1: Text-entry (ListenAndType, ReadAndProduce) with system-aware grading, feedback styling
   - Phase 2: TapTheCounter (border-only feedback, auto-advance, chip UI pattern)
   - Phase 3: ListenAndPlace (digital time matcher MVP)
   - Phase 4: DisambiguateTheSystem (paired-prompt stacking, multi-choice strips)

2. **UI Accessibility & Pattern Library**
   - NO EMOJI in any UI — use Bootstrap icons (bi-check-circle-fill, bi-x-circle-fill)
   - Border-only feedback (green/red) with pulse/shake animations
   - Responsive: vertical stacking on mobile (<640px), grid on desktop
   - Reusable chip UI pattern (counters, particles, tense suffixes)

3. **Import Content Workflow**
   - Preview-to-commit DTO mapping discipline (enumerate all properties)
   - State preservation via `IImportResultStore` singleton + URL query params
   - Per-row detail table with filter pills (All/Created/Updated/Skipped/Failed)
   - Type badges: `badge bg-{color} bg-opacity-10 text-{color}`

4. **Core UI Patterns**
   - Activity page shell: `.activity-page-wrapper` (card, progress dots, TTS cache)
   - Feedback panels: `alert alert-success` / `alert alert-danger` (single unified alert, no nesting)
   - System colors: purple Native (#7c3aed), teal Sino (#0d9488)
   - Clickable non-buttons: `role="button"` not inline `cursor:pointer`

5. **Recent Sessions**
   - 2026-05-04: Phase 1 & 2 shipped (text-entry + counter-tapping)
   - 2026-05-05: iOS trim fix (JsonSerializerContext), UI redesign (Bootstrap tokens)
   - 2026-05-12: Override UX (1.5s double-tap protection flag, telemetry capture)
   - 2026-05-12–06-09: Normalizer refinements (strip punctuation, fullwidth digits), NumberDrill picker gating
   - 2026-07-27–28: Import styling cleanup, Scriban CVE bump

## Standing Conventions

- Canonical activity names: "SceneDescription" (not "Scene"), "Conversation" (per spec 3.5)
- Double-tap: `_overriding` flag on first click, gates button `disabled` + method body, reset in `NextItemAsync()`
- Normalizer rules (pre-grader): strip trailing punctuation, normalize fullwidth digits (nothing fuzzier)
- Paired-prompt UI: hide system badges pre-submit, stacked vertical layout, multiple-choice strips

## Known Patterns & Reusable Skills

- **Grader Override Pattern** — mirrored from VocabQuiz, telemetry shape captures errorClass + user input + canonical answer
- **Counter Chip UI** — generalizes to particle selection (이/가, 은/는), tense suffixes, honorific endings
- **Paired-Prompt UI** — reusable pattern for any pedagogy requiring simultaneous contrast
- **Blazor Markup Cache** — structural HTML tree changes require full webapp restart (not just hot reload)
- **Keyboard Shortcut + Text Input** — bare digits as shortcut (e.g., `46` for "마흔여섯 개")

## Cross-Agent Context

- Works with Jayne (E2E testing/Playwright), River (prompt/grading strategies), Wash (backend integration)
- UI → test cycle: E2E blocking pre-existing platform issues (Xcode 26.3 mismatch, Android minSdk, AppLib TFM)
- Recent code-review: Part of PlanFactsSerializer hoist + fallback plan symmetry validation

