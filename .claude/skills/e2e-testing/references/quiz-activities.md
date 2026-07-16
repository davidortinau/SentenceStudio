# Quiz Activity E2E Tests

## 1. Vocabulary Quiz (`/vocab-quiz`)

**Prereqs:** Resource with ≥4 vocab words
**URL:** `/vocab-quiz?resourceIds=<id>&skillId=<id>`
**Services:** VocabularyProgressService, ElevenLabs (audio), ProgressCacheService

### 1.1 Happy path (defaults)

| Step | Action | Verify |
|------|--------|--------|
| 1 | Navigate to quiz URL | Word in target language; 4 MCQ buttons |
| 2 | Click correct answer | "Correct!" feedback; score increments |
| 3 | Wait 2s (don't click Next) | Auto-advances to next word |
| 4 | Click wrong answer | "The answer is: X" feedback |
| 5 | Click speaker 🔊 | Spinner → done (audio plays) |
| 6 | Complete 10 questions | Summary screen with accuracy % |
| 7 | Navigate to Dashboard | "Learning" count > 0; "New" decreased |

**DB:** `SELECT COUNT(*) FROM VocabularyProgress WHERE UserId = '<userId>' AND TotalAttempts > 0`
**Pitfalls:**
- UserId must be GUID from `active_profile_id`, never `"1"`
- Must call `CacheService.InvalidateVocabSummary()` after `RecordAttemptAsync`
- Audio uses JS interop fallback on webapp; `WebAudioManagerProxy.CreatePlayer()` returns null

### 1.2 Prompt-direction × modality acceptance matrix (Learning Value Gate)

**Why this matrix exists:** 2026-07-15 incident — a photo-prompt toggle shipped that could hide the target-language term in `TargetToNative` direction, leaving learners matching a picture to their own native language. See `.squad/skills/learning-value-gate/SKILL.md`.

**Prep for each row:** seed a resource where every vocab word has `MnemonicImageUri` populated (so `HasPromptImage` is true and the photo controls are reachable). Toggle preferences via Settings → Vocab Quiz Preferences.

Symbols under test (do not invent new ones — use these exact fields):

- `VocabularyQuizPreferences.DisplayDirection` — `"TargetToNative" | "NativeToTarget" | "Mixed"` (`src/SentenceStudio.AppLib/Services/VocabularyQuizPreferences.cs:47-54, 209-216`)
- `VocabularyQuizPreferences.UsePhotoPrompt` — bool (default `false`) — same file, `KEY_USE_PHOTO_PROMPT`.
- `UserProfile.VocabQuizShowTextWithPhoto` — bool (per-user, persisted) — `src/SentenceStudio.Shared/Migrations/20260714173027_AddVocabQuizShowTextWithPhoto.cs`.
- `VocabQuiz.razor` per-turn flag: `promptUsesNativeLanguage` (line 603); computed by `ShouldUseNativePrompt()` (lines 2011-2015); consumed by `GetPromptText`, `GetAnswerText`, `GetPromptAudioText`, `GetPromptAudioLanguage`, and (must be) `ShouldHideTermForPhoto` (line 1940).

| # | DisplayDirection | UsePhotoPrompt | ShowTextWithPhoto | Expected on-screen prompt | Expected response set | Learning verdict | Pass criteria |
|---|------------------|----------------|-------------------|---------------------------|-----------------------|------------------|---------------|
| A | TargetToNative | false | — | Target term (text) | Native-language options | ✅ recognition of meaning | Target term visible; response is native; correct answer scores. |
| B | TargetToNative | true | true | Target term (text) + photo | Native-language options | ✅ recognition of meaning + visual anchor | Target term visible next to photo; response is native. |
| C | TargetToNative | true | **false** | Photo only, **no target text** | Native-language options | ❌ **BLOCKED — no L2 on screen** | The "Hide text with photo" toggle **must be unreachable** in this direction — either not rendered, or rendered but disabled with an aria-labeled explanation. If reachable and pressed, the target term must not be hidden. Failing this row is a Learning Value Gate violation and blocks release. |
| D | NativeToTarget | false | — | Native-language cue (text) | Target-language options | ✅ recall of form | Native prompt visible; response is target; correct answer scores. |
| E | NativeToTarget | true | true | Native cue + photo | Target-language options | ✅ recall + visual anchor | Both visible; response is target. |
| F | NativeToTarget | true | false | Photo only before grading; native text during feedback | Target-language options | ✅ picture → target recall | Native prompt is hidden before submission, then revealed for both correct and incorrect feedback until auto/manual advance. |
| G | Mixed | true | false | Per turn: rows C or F | Per turn | Depends per turn | Every native-prompt turn behaves like F, including feedback reveal and next-item reset. Every target-prompt turn behaves like B even though `ShowTextWithPhoto=false`. Rejects if any target-prompt turn hides the target term. |

**Executable checks per row:**

1. Set preferences to the row's `DisplayDirection` and `UsePhotoPrompt`. For row-C and row-G, also set `VocabQuizShowTextWithPhoto=false`.
2. Load `/vocab-quiz` with a photo-bearing resource.
3. Read the DOM prompt region:
   - Row A/B/D/E/G-target: assert the target-language term text is present in the prompt heading region (`.ss-display` or equivalent).
   - Row C: assert the target-language term is **not** present in the DOM. If it is not present, this row is a **failure**, because such a state must be unreachable. Passing this row requires either that the toggle producing it is not rendered, or that clicking it does not remove the target term.
   - Row F/G-native: assert the native prompt may be absent from the DOM (permitted), and assert the response buttons contain target-language text.
4. For row F and native-prompt turns in row G:
   - Before grading, assert the native prompt text is absent.
   - Submit a correct answer and assert the native prompt text appears immediately, remains visible during the feedback interval, then is hidden again if the next item is another native photo prompt.
   - Repeat with an incorrect answer and assert the same feedback reveal and next-item reset.
   - With `ShowTextWithPhoto=true`, assert the prompt remains continuously visible through grading without flicker.
   - Row F default-text subcase: set `UseTextPrompt=true`, `UsePhotoPrompt=true`, and `ShowTextWithPhoto=true`; press **Hide Text**, assert the native term is absent before grading, present during feedback, and absent again on the next native photo item.
5. Answer-leakage sub-checks (all rows):
   - `img[alt]` on prompt photo does not contain the correct answer term.
   - The speaker button, when pressed, plays audio in the **prompt** language, never the answer language (per #193 anti-cheat, `GetPromptAudioLanguage` in `VocabQuiz.razor:1969`).
   - No MC option element contains substring-matched target-form leakage in an aria-label if the prompt is native and text is hidden.
6. Empty-state check: with a **brand-new profile** (no preferences persisted, `VocabQuizShowTextWithPhoto` defaults from `AddVocabQuizShowTextWithPhoto` migration = `false`), toggle `UsePhotoPrompt=true` and verify the resulting state is **not** row C. This is the exact incident-repro test.

**Pitfalls specific to this matrix:**

- The `ShowTextWithPhoto` field defaults to `false` at the DB level. Combined with `TargetToNative` default direction, first-time-use of the photo prompt lands in row C unless UI gates it.
- `Mixed` mode randomizes direction per turn via `ShouldUseNativePrompt()` — a single test navigation must cycle enough turns to observe both direction outcomes, or the test must seed the RNG.
- Toggling `showTextWithPhoto` persists via `SaveVocabQuizShowTextWithPhotoAsync`. Reset the field between test runs or the state leaks across cases.

### 1.3 Target-language sample-sentence hints

**Learning objective:** On target-language prompt turns, the learner can opt into additional target-language context that supports comprehension without revealing the native-language answer.

**Seed fixture:**

1. Use the canonical Squad test account and an owned resource mapped to a quiz word.
2. Give the profile `TargetCEFRLevel=B1`.
3. Seed four eligible, unflagged example sentences for that exact owned word/resource mapping:

| ExampleSentenceId | DifficultyLevel | IsCore | Status | CreatedAt order | TargetSentence |
|-------------------|-----------------|--------|--------|-----------------|----------------|
| 9101 | 3 | true | Curated | third | `LEVEL-3-CORE target sentence` |
| 9102 | 3 | false | Verified | second | `LEVEL-3-VERIFIED target sentence` |
| 9103 | 2 | false | Verified | first | `LEVEL-2 target sentence` |
| 9104 | 5 | false | Verified | fourth | `LEVEL-5 target sentence` |

Set every row's `NativeSentence` to the sentinel `DO-NOT-RENDER-native-translation`. Add a photo to the word for the fullscreen case. Seed a second owned word with no eligible sentences.

**Executable checks:**

1. Set `DisplayDirection=TargetToNative`, navigate to the seeded word, and assert:
   - `#quiz-sentence-hint-button` exists and is enabled.
   - Its localized `title` equals its `aria-label`.
   - `aria-expanded="false"` and `aria-controls="quiz-sentence-hint-panel"`.
   - `#quiz-sentence-hint-panel` is absent from the DOM.
2. Click `#quiz-sentence-hint-button` and assert:
   - The button now has `aria-expanded="true"` and uses `bi-chat-quote-fill`.
   - `#quiz-sentence-hint-panel[role="region"]` exists.
   - The panel contains one semantic `ul` with 1-3 `li[data-example-sentence-id]` rows.
   - The IDs are exactly `9101,9102,9103` in that order for B1.
   - The list has the target-language `lang` attribute when the word's language metadata is recognized.
   - The panel text contains only the three `TargetSentence` values. Assert `document.body.innerText` does not contain `DO-NOT-RENDER-native-translation`, and no hint control exposes translation or answer-side audio.
3. Click the toggle again. Assert `aria-expanded="false"` and the panel is removed from the DOM rather than CSS-hidden.
4. Open the panel, submit a correct answer, and assert the panel stays open with the same target-only rows throughout feedback. Repeat on another target-prompt turn with an incorrect answer.
5. Advance with Next or wait for auto-advance. Assert the next turn starts collapsed and the panel is absent.
6. Reach the second target-prompt word with zero eligible hints. Assert `#quiz-sentence-hint-button` is absent, not disabled.
7. Set `DisplayDirection=NativeToTarget`. On every turn assert the hint button and panel are absent, including during correct and incorrect feedback.
8. Set `DisplayDirection=Mixed` and cycle until both prompt directions occur:
   - Target-prompt turn with eligible hints: button is present and opt-in.
   - Native-prompt turn for the same word: button and panel are absent.
   - Returning to a target-prompt turn starts collapsed.
9. Resume reset:
   - On a target-prompt turn, open the panel, navigate away without completing the session, return, choose Resume, and assert the restored turn starts collapsed.
   - Inspect the saved `VocabQuizSessionSnapshot` and assert it contains no sentence-hint expansion state or sentence content.
10. Fullscreen reset:
    - Open the panel, then click `#quiz-photo-thumbnail`.
    - Assert the hint panel is removed before `#quiz-fullscreen-viewer` appears.
    - Close fullscreen and assert the hint panel remains collapsed.
11. Missing-level fallback:
    - Set `TargetCEFRLevel` to null, start a fresh batch, open the panel, and assert IDs are `9101,9103,9102`.
    - Repeat with an unrecognized level such as `B3`; assert the same deterministic fallback order.
12. Ownership fail-closed:
    - Add an otherwise eligible sentence linked only to another user's resource or without an exact resource mapping.
    - Assert its ID never appears in any `data-example-sentence-id`.

**Pass criteria:** The feature performs one scoped prefetch for the batch, exposes no button on native-prompt or empty-hint turns, renders at most three target-only sentences, preserves expansion only through feedback on the same turn, and resets on every turn/session/fullscreen boundary.

## 2. Vocabulary Matching (`/vocab-matching`)

**Prereqs:** Resource with ≥6 vocab words
**URL:** `/vocab-matching?resourceIds=<id>`
**Services:** VocabularyProgressService, ProgressCacheService

| Step | Action | Verify |
|------|--------|--------|
| 1 | Navigate to matching URL | Grid of tiles (target + native) |
| 2 | Click target tile | Highlights as selected |
| 3 | Click matching native tile | Both mark as matched |
| 4 | Click wrong native tile | Both reset; mismatch count increments |
| 5 | Match all pairs | Completion summary |

**DB:** Verify `VocabularyProgress` records with correct GUID `UserId`.

## 3. Cloze (`/cloze`)

**Prereqs:** Resource with vocabulary + sentences
**URL:** `/cloze?resourceId=<id>&skillId=<id>`
**Services:** VocabularyProgressService, ProgressCacheService

| Step | Action | Verify |
|------|--------|--------|
| 1 | Navigate to cloze URL | Sentence with blank; MCQ or text input |
| 2 | Select/type correct answer | "Correct!"; auto-advances after 1.2s |
| 3 | Select/type wrong answer | Correct answer revealed |
| 4 | Complete round | Summary with accuracy |

**DB:** `VocabularyProgress` records with `Activity = 'Clozure'`.

## 4. Writing (`/writing`)

**Prereqs:** Resource with vocabulary
**URL:** `/writing?resourceId=<id>&skillId=<id>`
**Services:** TeacherService (AI via API), VocabularyProgressService, ProgressCacheService

| Step | Action | Verify |
|------|--------|--------|
| 1 | Navigate to writing URL | Vocab words displayed as prompts |
| 2 | Type a sentence using vocab | Text in input |
| 3 | Click Grade | AI accuracy %; toast shows result (10–30s) |

**DB:** `VocabularyProgress` records with `Activity = 'Writing'`.
**Pitfall:** Requires working AI gateway chain (webapp → API → OpenAI).

## 5. Translation (`/translation`)

**Prereqs:** Resource with vocabulary
**URL:** `/translation?resourceId=<id>&skillId=<id>`
**Services:** TeacherService (AI), TranslationService

| Step | Action | Verify |
|------|--------|--------|
| 1 | Navigate to translation URL | Source sentence displayed |
| 2 | Type translation | Text in input |
| 3 | Submit | AI feedback with corrections |
