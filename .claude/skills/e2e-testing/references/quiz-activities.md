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
| F | NativeToTarget | true | false | Photo only, native text hidden | Target-language options | ✅ picture → target recall | Native prompt may be hidden because response set is still target-language; learner retrieves target form from picture. |
| G | Mixed | true | false | Per turn: rows C or F | Per turn | Depends per turn | Every native-prompt turn behaves like F. Every target-prompt turn must behave like B (target term visible) even though `ShowTextWithPhoto=false`. Rejects if any target-prompt turn hides the target term. |

**Executable checks per row:**

1. Set preferences to the row's `DisplayDirection` and `UsePhotoPrompt`. For row-C and row-G, also set `VocabQuizShowTextWithPhoto=false`.
2. Load `/vocab-quiz` with a photo-bearing resource.
3. Read the DOM prompt region:
   - Row A/B/D/E/G-target: assert the target-language term text is present in the prompt heading region (`.ss-display` or equivalent).
   - Row C: assert the target-language term is **not** present in the DOM. If it is not present, this row is a **failure**, because such a state must be unreachable. Passing this row requires either that the toggle producing it is not rendered, or that clicking it does not remove the target term.
   - Row F/G-native: assert the native prompt may be absent from the DOM (permitted), and assert the response buttons contain target-language text.
4. Answer-leakage sub-checks (all rows):
   - `img[alt]` on prompt photo does not contain the correct answer term.
   - The speaker button, when pressed, plays audio in the **prompt** language, never the answer language (per #193 anti-cheat, `GetPromptAudioLanguage` in `VocabQuiz.razor:1969`).
   - No MC option element contains substring-matched target-form leakage in an aria-label if the prompt is native and text is hidden.
5. Empty-state check: with a **brand-new profile** (no preferences persisted, `VocabQuizShowTextWithPhoto` defaults from `AddVocabQuizShowTextWithPhoto` migration = `false`), toggle `UsePhotoPrompt=true` and verify the resulting state is **not** row C. This is the exact incident-repro test.

**Pitfalls specific to this matrix:**

- The `ShowTextWithPhoto` field defaults to `false` at the DB level. Combined with `TargetToNative` default direction, first-time-use of the photo prompt lands in row C unless UI gates it.
- `Mixed` mode randomizes direction per turn via `ShouldUseNativePrompt()` — a single test navigation must cycle enough turns to observe both direction outcomes, or the test must seed the RNG.
- Toggling `showTextWithPhoto` persists via `SaveVocabQuizShowTextWithPhotoAsync`. Reset the field between test runs or the state leaks across cases.

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
