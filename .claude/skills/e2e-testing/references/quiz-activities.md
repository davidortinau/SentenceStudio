# Quiz Activity E2E Tests

## 1. Vocabulary Quiz (`/vocab-quiz`)

**Prereqs:** Resource with ≥4 vocab words
**URL:** `/vocab-quiz?resourceIds=<id>&skillId=<id>`
**Services:** VocabularyProgressService, ElevenLabs (audio), ProgressCacheService

These WebApp source cases also define native Quiz behavior; assigned MC/text
completion alone does not satisfy native fidelity. Run direction × prompt
modality × response modality × toggle, including audio-on/off and photo-on/off,
and verify the first-run default path. Native photo/details/shortcut/settings
remain incomplete until their running-platform journeys are proved.

### 1.1 Happy path (defaults)

| Step | Action | Verify |
|------|--------|--------|
| 1 | Navigate to quiz URL | Word in target language; 4 MCQ buttons |
| 2 | Click correct answer | "Correct!" feedback; score increments |
| 3 | Wait 2s (don't click Next) | Auto-advances to next word |
| 4 | Click wrong answer | "The answer is: X" feedback |
| 5 | Click speaker 🔊 | Spinner → done (audio plays) |
| 6 | Complete 10 questions | Summary shows correct/total/mastered/round counts and per-word outcomes |
| 7 | Navigate to Dashboard | "Learning" count > 0; "New" decreased |

**DB:** `SELECT COUNT(*) FROM VocabularyProgress WHERE UserId = '<userId>' AND TotalAttempts > 0`
**Pitfalls:**
- UserId must be GUID from `active_profile_id`, never `"1"`
- Must call `CacheService.InvalidateVocabSummary()` after `RecordAttemptAsync`
- Audio uses JS interop on webapp; `WebAudioPlayerFactory.CanPlayLocally` is false and
  `CreatePlayer()` throws if browser code incorrectly attempts server-side playback

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
| C | TargetToNative | true | **false** | Target term (text) + photo | Native-language options | ✅ target-script recognition | The hide-text control is unavailable on target-prompt turns; the target text remains visible even when the persisted preference is false. |
| D | NativeToTarget | false | — | Native-language cue (text) | Target-language options | ✅ recall of form | Native prompt visible; response is target; correct answer scores. |
| E | NativeToTarget | true | true | Native cue + photo | Target-language options | ✅ recall + visual anchor | Both visible; response is target. |
| F | NativeToTarget | true | false | Photo only before grading; native text during feedback | Target-language options | ✅ picture → target recall | Native prompt is hidden before submission, then revealed for both correct and incorrect feedback until auto/manual advance. |
| G | Mixed | true | false | Per turn: rows C or F | Per turn | ✅ per-turn target exposure/retrieval | Every native-prompt turn behaves like F, including feedback reveal and next-item reset. Every target-prompt turn behaves like C with the target term visible despite `ShowTextWithPhoto=false`. |

**Executable checks per row:**

1. Set preferences to the row's `DisplayDirection` and `UsePhotoPrompt`. For row-C and row-G, also set `VocabQuizShowTextWithPhoto=false`.
2. Load `/vocab-quiz` with a photo-bearing resource.
3. Read the DOM prompt region:
   - Row A/B/D/E/G-target: assert the target-language term text is present in the prompt heading region (`.ss-display` or equivalent).
   - Row C: assert the target-language term **is visible in the prompt heading** and the Hide Text control is absent or disabled for that target-prompt turn. A stored `ShowTextWithPhoto=false` value must not remove the target term.
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

### 1.2a Native fallback, feedback, and timing acceptance

| Setup/action | Required observation |
|--------------|----------------------|
| For each direction (including both Mixed turn directions), disable text and make audio/photo unavailable | A readable prompt and target-language-engaging response remain; no nonexistent audio/photo is reported as played or rendered; answer-side audio never substitutes for the prompt |
| Enable real audio, leave the first-run `AutoPlayVocabAudio=true`, then advance/restart | Prompt-side word plays once per eligible turn in the prompt language, never the answer language; manual play and offline failure have truthful fallback |
| NativeToTarget or Mixed/native MC with audio-answer letters A–D | Before playing, each letter's Choose action is disabled and its separate Play action speaks the target-language option; after successful playback that letter is enabled, selecting it grades exactly once. If playback fails, all options show their target-language text instead of offering selectable native-only letters. Repeat with photo on/hidden native text and ensure target-language engagement remains possible. Accessibility labels announce only the option letter before playback, never the correct-answer term. |
| Native-language photo with `ShowTextWithPhoto=false`, answer correctly and incorrectly | Hidden native text reveals for both feedback outcomes, then resets on the next item and on resume; target-language prompt text remains visible in TargetToNative and Mixed turns |
| Light/Dark selected from the normal Dashboard → Settings action, then navigate through Dashboard/Quiz/Resources/edit and restart | Both palettes and font scale persist from the normal `AppAppearance` preference, without Ailoha/fixture preference injection, and changing only the mode does not reset the theme/scale or recreate a focused input/NavigationStack |
| Correct and incorrect MC/Text feedback, then wrong Text correction | Correct-answer term and status alert both appear before choices/typed input; wrong Text clears and reuses the same field for Type Correct/Submit/Enter; no second correction field. Auto-advance progress moves at start/mid/selected 1–10s endpoint, cancels on manual Next/Back/override/restart and never double-scores. Footer counter stays START, correct-count pill END, and a real divider stays above both with keyboard visible. |
| Correct feedback with `ShowMnemonicImage=true` while `UsePhotoPrompt=false`; then answer incorrectly on another turn | Only correct feedback exposes mnemonic text and an image candidate; a platform image load must succeed before counting it as rendered or fullscreen-capable |
| Open Learning Details before and after grading, dismiss via Back/close | Show only owner-scoped status, attempts, accuracy, streak, mastery, SRS timing and mode reason; no target/native answer term appears in the panel or accessibility metadata |
| Type a fuzzy-accepted response with a complete form; complete a round; Back from ResumeChoice | Full form appears only after grading and survives resume; summary distinguishes correct/incorrect/rotated-out rows and totals; ResumeChoice Back preserves the snapshot without another write |
| Quiz Ready → More → Use in a sentence, with an empty editor and then entered text | The real multiline editor has a visible source-style border and readable text. Grade is disabled while empty, enables as text is entered without remounting the editor or losing focus, and remains reachable when the keyboard is up. Cancel returns to the same turn without grading. Submit an owned target-language sentence through the real grader and verify one persisted owner-scoped result, feedback and Continue. Submit a native-language-only sentence against a target-language term (for example, English `I eat fruit.` against Korean `과일`): even if the AI proposes a Korean translation, production mastery credit must remain zero. An unavailable grade shows a truthful retry/Continue path without claiming success. |
| Select auto-advance duration 1, 5, 5.5 and 10 seconds; restart | The selected half-second values persist, and observed feedback dwell time follows the chosen duration; do not copy the old silent 5-second clamp |

### 1.2b Native exhaustive preference cells

For each **effective** turn direction `TargetToNative`, `NativeToTarget`,
`Mixed-target` and `Mixed-native`, each response mode `MC` and `Text`, and
each `UseTextPrompt`/`UseAudioPrompt`/`UsePhotoPrompt` bit pattern `000`,
`001`, `010`, `011`, `100`, `101`, `110`, `111`, exercise the **64**
`M:{direction}:{mode}:{bits}` cells from the approved iOS screen-state
matrix. Configure settings through the normal Settings screen; do not inject
preference values or mutate a protected owner's quiz attempts. Source data
conditions (owned image present/absent, audio playable/cached/offline) are
recorded alongside each result, not treated as an excluded row.

| Per-cell condition | Target-language action to verify |
|--------------------|----------------------------------|
| TargetToNative or Mixed-target, any mode/bits | Written target prompt stays visible whenever photo is on; if all three modalities are off or configured media fail, written target prompt appears as fallback. Recognition/comprehension of the target form must be possible before revealing native meaning. |
| NativeToTarget or Mixed-native, Text, any bits | Learner produces the target-language form in the same TextField; hidden native photo text returns for **both** feedback outcomes. Audio on the prompt must play only native-language input, not answer-side target audio. |
| NativeToTarget or Mixed-native, MC, audio available | Learner hears a target-language option from its separate Play button before that letter may be selected; failed/unavailable playback falls back to visible target-language option text. The correct target term must never be exposed as a prompt hint or alt label. |
| NativeToTarget or Mixed-native, MC, audio off/unavailable | Response buttons visibly contain target-language candidate forms, including photo-only native prompt cells; choosing the correct form exercises target retrieval. |
| Photo bit=1, no owned image | Stay in the text/audio portion of the same cell with source-conditional image absence, no fabricated image/thumbnail or false N/A for the overall cell. |
| `000` | Settings normalizes to a text-prompt fallback on save. Before any saved preference exists, the source default `TargetToNative` + text/audio on and photo off must still expose the written target form. |

For every cell also check generic photo accessibility labels, Info's
answer-free owner statistics, prompt-language audio, cancellation/return and
same-turn save exactly once. Run correct **and** incorrect Text correction in
both effective directions, and explicitly observe both Mixed turn directions
before recording a Mixed result. If a requested media/destination bridge is
not implemented, record the cell as blocked—not passing or not applicable.

### 1.2c Native Quiz prompt-circle controls

Pinned `VocabQuiz.razor:255-288` sets the prompt-row audio, photo, Info and
sentence-hint icon buttons to `44px × 44px`, `rounded-circle` and `p-0`.
On a data-safe native Quiz session, use Ailoha's live tree and screenshots:

| State | Check |
|-------|-------|
| Ready and feedback, Light and Dark, default and accessibility-extra-large text | Audio (`native.quiz.play-prompt`) and Info (`native.quiz.details`) each paint a 44×44 circle and retain a 44×44 button frame. Both SF Symbols remain wholly within their painted circles at the selected app font scale, including 150%, while the prompt, answer and accessibility text continue to scale normally. Preserve accessible names, actions and source-derived outline colors. |
| Owned photo present or target-language sentence hints available | Photo toggle (`native.quiz.photo.toggle`) and hints toggle (`native.quiz.hints.toggle`) have the same 44×44 painted circle and hit bounds before and after the existing icon changes; absent optional controls stay absent. |
| Existing busy/disabled cases | During audio, do not start overlapping playback; photo remains disabled when busy. Info still opens the same owner-scoped, answer-free details sheet. The answer-choice Play buttons and Quiz More menu keep their distinct source geometry. |

Never interrupt or mutate Captain's active canonical Quiz to run this case.
Use only root-approved isolation or a separately authorized data-safe journey.

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
