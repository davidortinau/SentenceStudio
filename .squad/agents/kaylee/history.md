# Kaylee — History Summary

**Summarized by Scribe:** 2026-06-10T03:35:00Z
**Project:** SentenceStudio — .NET MAUI Blazor Hybrid language learning app
**Role focus:** Blazor/MAUI UI, activity interaction design, visual consistency, accessibility, import workflow UI.

## UI conventions to preserve

- No emoji in UI. Use Bootstrap icons or plain text.
- Use theme/bootstrap patterns already established in the app before inventing new markup or styling.
- VocabQuiz is the canonical activity shell reference: flat active-session content, `activity-page-wrapper`, `page-header`, `activity-content`, `activity-footer`, and `activity-input-bar` placement matter.
- Activity pages use footer-based progress (`X / Y` plus success badge), not top dots. Dots are reserved for wizards/onboarding.
- Active drill content should be flat, not wrapped in card chrome. Cards are for setup and summary screens.
- The complete flex pattern for bottom-pinned activity layout is: wrapper flex column, page header `flex-shrink: 0`, content `flex: 1`, footer `flex-shrink: 0` with safe-area handling.
- Accessibility rule: feedback should not rely on text color readability. Use borders, icons, and status structure instead.

## NumberDrill UI and grading learnings

- Tap-the-Counter added `NounCue` and `CounterChoices`, with shuffled counter chips, border-only feedback, and auto-advance after feedback.
- Disambiguate uses vertically stacked paired prompts with independent choice strips and paired grading after both answers are submitted.
- Picker gating Option A filters invalid context/sub-mode combinations at the UI layer: TapTheCounter only for Counting, ListenAndPlace only for Time, and `Any` hides modes that could randomly choose invalid contexts.
- Listen & Type audio follows the VocabQuiz pattern: ElevenLabs TTS, stream cache, native audio playback, and JS fallback for WebApp.
- System-aware grading fixed over-permissive normalization: bare digits are always accepted, system-matching Korean forms are accepted, wrong-system Korean forms are rejected as pedagogical errors.
- Grader override pattern shifted from endless fuzzy rules to human-in-the-loop correction plus telemetry. Keep normalizer additions narrow and evidence-driven; Captain rejected fuzzy matching.
- Internal digit commas such as `15,000원` are accepted through a regex that strips commas only between digits. Double-tap override protection uses an `_overriding` guard and disabled button state.

## Import UI learnings

- Preview-to-commit DTO mapping must account for every source property explicitly to avoid silent default-value data loss.
- Import detail views use borderless list-group rows from Vocabulary as the canonical pattern; structural HTML changes require a full WebApp resource restart for reliable Blazor verification.
- Import result state preservation uses a singleton store keyed by query parameter with TTL so back navigation can restore the completed view.
- Type badges use Bootstrap badge classes and opacity utilities; clickable non-buttons use `role="button"` rather than inline cursor styles.
- Harvest defaults: Vocabulary => Words; Phrases => Phrases + Words; Sentences => Sentences + Words; Transcript => Transcript + Words; Auto => none preselected. Preview and commit must both send harvest flags.

## Platform/build lessons

- iOS release builds under net10 can bypass Xcode version assertion with `-p:ValidateXcodeVersion=false`; prior net11 preview swap produced Razor compilation issues in this repo.
- For security bumps such as Scriban, validate builds/tests and spot-check template syntax; keep package-audit results in the decision record.

---

Team update (2026-06-17T16:08:31-05:00): Concern #2 per-user timezone fix — LANDED AND APPROVED.

Kaylee's work (commit fa2a25d4): wired `<TimeZoneCapture />` into `src/SentenceStudio.WebApp/Components/AppRoutes.razor` (immediately before `<Router>`), resolving Zoe blocker #1. AppRoutes.razor chosen over shared MainLayout because MainLayout is compiled into MAUI heads — placing a WebApp-project component there would break MAUI builds. AppRoutes.razor is webapp-only; inherits InteractiveServer from App.razor:23; CascadingAuthenticationState cascades auth context from App.razor:22. Component is headless, one-shot per circuit, multi-tenant guarded. Build: 0 errors, MAUI heads unaffected. Zoe re-review: AppRoutes placement explicitly approved (prior MainLayout suggestion retracted).

No carry-forward items for Kaylee from this session.

---

Team update (2026-06-26T21:30:56-05:00): Quick-add existing vocabulary feature — Kaylee updated ResourceEdit with debounced typeahead, keyboard navigation, refocus after add, inline create-on-miss stamping `Language=resource.Language`, per-word remove, language guard, localized strings, minimal CSS, and collapsed Bulk import. Carry-forward: strict-language lookup means NULL-language bulk-import rows are intentionally not suggested.

---

Team update (2026-07-02T15:08:45-05:00): Vocab Quiz Session & Resume Wave 2 — Kaylee wired `VocabQuiz.razor` to `IActivitySessionService` using `VocabQuizSessionSnapshot`. Carry-forward: resume must re-fetch words/progress fresh, overlay only session-local counters, and suppress dispose-time saves after `CompleteAsync` so completed sessions are not recreated as in-progress. The requested `-f net10.0` WebApp build does not match the current `SentenceStudio.WebApp.csproj` target (`net11.0`); actual-target WebApp build passed with 0 errors.


---

Team update (2026-07-02T15:30-05:00): Vocab Quiz Session & Resume shipped — Kaylee's `VocabQuiz.razor` integration is now part of the delivered feature: localized Resume / Start fresh gate, best-effort snapshot saves, exact resume by re-fetching vocabulary/progress then overlaying session-local counters, and completion/abandon guards to avoid reviving completed sessions. Wash supplied the reusable `ActivitySession` foundation and Jayne added 11 unit tests; Squad verified WebApp resume behavior end-to-end. Carry-forward for Kaylee: DevFlow stale-agent collision can make migration validation false-pass when another DevFlow app is running, so UI/behavior verification should confirm the attached app is actually SentenceStudio.
