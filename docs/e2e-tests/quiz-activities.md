# Quiz Activity E2E Tests

## 1. Vocabulary Quiz (`/vocab-quiz`)

**Prereqs:** Resource with ≥4 vocab words  
**URL:** `/vocab-quiz?resourceIds=<id>&skillId=<id>`  
**Services:** VocabularyProgressService, ElevenLabs (audio), ProgressCacheService

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
