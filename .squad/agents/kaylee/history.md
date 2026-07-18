# Kaylee — History Summary

**Summarized by Scribe:** 2026-07-18T11:06:00-05:00
**Project:** SentenceStudio — .NET MAUI Blazor Hybrid language learning app
**Role focus:** Blazor/MAUI UI, activity interaction design, visual consistency, accessibility, import workflow UI.

## UI conventions to preserve

- No emoji in UI. Use Bootstrap icons or plain text.
- Prefer established theme/bootstrap patterns before inventing markup or styling.
- VocabQuiz is the canonical activity shell reference: flat active-session content, `activity-page-wrapper`, `page-header`, `activity-content`, `activity-footer`, and `activity-input-bar` placement matter.
- Activity pages use footer-based progress (`X / Y` plus success badge), not top dots. Dots are reserved for wizards/onboarding.
- Active drill content should be flat, not wrapped in card chrome. Cards are for setup and summary screens.
- Bottom-pinned activity layout requires the complete flex chain: wrapper flex column, header `flex-shrink: 0`, content `flex: 1`, footer `flex-shrink: 0` with safe-area handling.
- Accessibility rule: feedback must not rely on text color readability. Use borders, icons, and status structure.

## Durable feature learnings

### NumberDrill UI and grading

- Tap-the-Counter uses `NounCue` plus shuffled `CounterChoices`, border-only feedback, and auto-advance after feedback.
- Disambiguate uses vertically stacked paired prompts with independent choice strips and paired grading after both answers are submitted.
- Picker gating Option A filters invalid context/sub-mode combinations at the UI layer: TapTheCounter only for Counting, ListenAndPlace only for Time, and `Any` hides modes that could randomly choose invalid contexts.
- Listen & Type audio follows the VocabQuiz pattern: ElevenLabs TTS, stream cache, native audio playback, and JS fallback for WebApp.
- System-aware grading accepts bare digits and system-matching Korean forms, but rejects wrong-system Korean forms as pedagogical errors.
- Grader override pattern is human-in-the-loop correction plus telemetry; avoid broad fuzzy matching without evidence.
- Internal digit commas such as `15,000원` are accepted through a regex that strips commas only between digits. Double-tap override protection uses an `_overriding` guard and disabled button state.

### Import UI

- Preview-to-commit DTO mapping must account for every source property explicitly to avoid silent default-value data loss.
- Import detail views use borderless list-group rows from Vocabulary as the canonical pattern; structural HTML changes require a full WebApp resource restart for reliable Blazor verification.
- Import result state preservation uses a singleton store keyed by query parameter with TTL so back navigation can restore the completed view.
- Type badges use Bootstrap badge classes and opacity utilities; clickable non-buttons use `role="button"` rather than inline cursor styles.
- Harvest defaults: Vocabulary => Words; Phrases => Phrases + Words; Sentences => Sentences + Words; Transcript => Transcript + Words; Auto => none preselected. Preview and commit must both send harvest flags.

### Vocabulary and ResourceEdit

- Quick-add existing vocabulary uses debounced typeahead, keyboard navigation, refocus after add, inline create-on-miss stamping `Language=resource.Language`, per-word remove, language guard, localized strings, minimal CSS, and collapsed Bulk import.
- Strict-language lookup means NULL-language bulk-import rows are intentionally not suggested.
- Vocabulary no-results Add flow uses explicit `initialTargetTerm` only when `filteredWords.Count == 0` plus parsed free-text search terms. Filter syntax and Edit paths are not affected.
- `VocabularyWordEdit.razor` prefills `targetLanguageTerm` for new words and resolves `wordLanguage` from active profile `TargetLanguage`, with Korean fallback.

## VocabQuiz activity shell and photo/text policy

- Session/resume integration uses `IActivitySessionService` and `VocabQuizSessionSnapshot`. Resume must re-fetch words/progress fresh, overlay session-local counters, and suppress dispose-time saves after `CompleteAsync` so completed sessions are not recreated as in-progress.
- Photo text hiding gates on `promptUsesNativeLanguage`; target-language prompts never hide text, and the toolbar button does not render for target-prompt turns.
- Fullscreen viewer uses pure CSS + Blazor overlay, position fixed, `z-index: 1100`, close by button/backdrop/Escape, safe-area close placement, and focus into the overlay with restoration to thumbnail.
- `VocabQuizPhotoTextPolicy` is pure/static/testable and mirrors Razor inline logic.
- Native text reveal during feedback must preserve generic heading precedence before grading, then reveal hidden native text synchronously for both correct and incorrect feedback.
- Sentence hints integrate through `GetQuizHintsForWordsAsync`, target-language prompt gate, optional max-3 toggle, reset on state changes, and localization-aligned strings.
- Cross-profile UI hardening requires per-profile Choose My Own keys, ownership verification before render and launch, resume refusal on ownership mismatch, nullable-safe userId propagation, and localized errors.

## VocabQuiz footer layout regression — deployed

On 2026-07-18, Kaylee's footer fix was deployed to DX24 and Azure production. Commit `d1600720` restored the `.vocab-quiz-modal-host` height chain with a full-height flex passthrough in `src/SentenceStudio.UI/wwwroot/css/app.css` near line 1435. WebApp Playwright verified footer-flush mobile, desktop, photo Look-and-answer, wrong-answer reveal, info offcanvas, and sentence-shortcut behavior. iOS Release was installed over the existing app on DX24 with data preserved and launched successfully. Azure production deploy passed post-deploy validation and live CSS confirmed the rule.

Carry-forward: any wrapper inserted between the page host and `.activity-page-wrapper` must define its own full-height passthrough behavior or the percentage-height chain can collapse and un-anchor the footer. Do not add padding, margin, or transform to modal host wrappers because fixed-position photo modals and offcanvas surfaces must remain viewport-anchored.

## Platform/build lessons

- iOS release builds under net10 can bypass Xcode version assertion with `-p:ValidateXcodeVersion=false`; prior net11 preview swap produced Razor compilation issues in this repo.
- For security bumps such as Scriban, validate builds/tests and spot-check template syntax; keep package-audit results in the decision record.
- The requested `-f net10.0` WebApp build does not match the current `SentenceStudio.WebApp.csproj` target (`net11.0`); actual-target WebApp build passed with 0 errors.
- DevFlow stale-agent collision can make migration validation false-pass when another DevFlow app is running; UI/behavior verification should confirm the attached app is actually SentenceStudio.

## Cross-agent/review process learnings

- Rejection handoff to an independent revision owner works when blocker scope is unrelated to the original owner or requires fresh default-state analysis.
- Zoe's exact default-state tests resolved a native-text-feedback reveal vulnerability after Kaylee lockout; carry this approach forward for layered feature work.
- Simon's DEBUG-only native photo viewer prototype may affect Kaylee integration if fullscreen overlay z-order or safe-area handling conflicts with existing WebView layers.
