# End-to-End Test Scripts

Concise verification scripts for every activity. Use with Aspire + Playwright (webapp) or maui-ai-debugging (native apps).

## Global Setup

```
# Start the stack
cd src/SentenceStudio.AppHost && aspire run

# Wait for webapp
curl -sk -o /dev/null -w "%{http_code}" https://localhost:7071/  # expect 200

# Known test users (DB: sentencestudio.db)
# David  (Korean)  f452438c-b0ac-4770-afea-0803e2670df5
# Jose   (Spanish) 8d5f7b4a-7710-4882-af45-a550145dad4b
# Gunther(German)  c3bb57f7-e371-43d4-b91f-32902a9f9844
```

**Prerequisite check:** Each user needs at least 1 LearningResource with vocabulary mapped via ResourceVocabularyMapping. Verify:
```sql
SELECT lr.Id, lr.Title, COUNT(rvm.VocabularyWordId) as words
FROM LearningResource lr
JOIN ResourceVocabularyMapping rvm ON lr.Id = rvm.ResourceId
WHERE lr.UserProfileId = '<userId>'
GROUP BY lr.Id;
```

---

## 1. Import (`/import`)

**Prereqs:** None  
**Services:** YouTubeImportService, TranscriptFormattingService (AI), API `/api/v1/ai/chat`

| Step | Action | Verify |
|------|--------|--------|
| 1 | Navigate to `/import` | Page loads with URL input |
| 2 | Paste YouTube URL, click Fetch | Transcript text appears; language dropdown populated |
| 3 | Select language | Transcript updates to selected language |
| 4 | Click "Polish with AI" | Transcript reformatted (may take 30–60s); no 503 errors in Aspire logs |
| 5 | Click Save | Redirects to `/resources/edit/{id}` |

**DB check:** `SELECT Id, Title, Language FROM LearningResource ORDER BY CreatedAt DESC LIMIT 1`  
**Known pitfall:** YouTube language names include suffixes like "(auto-generated)" — must be stripped on save.

---

## 2. Resource Edit — Generate Vocabulary (`/resources/edit/{id}`)

**Prereqs:** A saved LearningResource with transcript  
**Services:** AiGatewayClient → API `/api/v1/ai/chat` → OpenAI

| Step | Action | Verify |
|------|--------|--------|
| 1 | Navigate to `/resources/edit/{id}` | Resource fields populated; transcript visible |
| 2 | Click "Generate from Transcript" | Spinner appears; wait up to 120s |
| 3 | Generation completes | Vocabulary list appears below transcript |

**DB check:**
```sql
SELECT COUNT(*) FROM ResourceVocabularyMapping WHERE ResourceId = '<id>';
SELECT COUNT(*) FROM VocabularyWord WHERE Id IN (
  SELECT VocabularyWordId FROM ResourceVocabularyMapping WHERE ResourceId = '<id>'
);
```
**Known pitfall:** Non-Latin languages (Korean, Japanese) use different generation logic. Latin-script language detection must normalize names.

---

## 3. Vocabulary Quiz (`/vocab-quiz`)

**Prereqs:** Resource with ≥4 vocab words mapped  
**URL:** `/vocab-quiz?resourceIds=<id>&skillId=<id>`  
**Services:** VocabularyProgressService, ElevenLabs (audio), ProgressCacheService

| Step | Action | Verify |
|------|--------|--------|
| 1 | Navigate to quiz URL | Word displayed in target language; 4 MCQ buttons visible |
| 2 | Click correct answer | "Correct!" feedback; score increments |
| 3 | Wait 2s (don't click Next) | Auto-advances to next word (question counter increments) |
| 4 | Click wrong answer | "The answer is: X" feedback; score unchanged |
| 5 | Click speaker button 🔊 | Button shows spinner → returns to normal (audio plays) |
| 6 | Complete 10 questions | Summary screen with accuracy % |
| 7 | Navigate to Dashboard `/` | "Learning" count > 0; "New" count decreased |

**DB check:**
```sql
SELECT COUNT(*) FROM VocabularyProgress
WHERE UserId = '<userId>' AND TotalAttempts > 0;
```
**Known pitfalls:**
- `UserId` must be GUID from `active_profile_id`, never `"1"`
- Must call `CacheService.InvalidateVocabSummary()` after `RecordAttemptAsync`
- Audio uses JS interop fallback on webapp (`audioInterop.js`); `WebAudioManagerProxy.CreatePlayer()` returns null

---

## 4. Vocabulary Matching (`/vocab-matching`)

**Prereqs:** Resource with ≥6 vocab words  
**URL:** `/vocab-matching?resourceIds=<id>`  
**Services:** VocabularyProgressService, ProgressCacheService

| Step | Action | Verify |
|------|--------|--------|
| 1 | Navigate to matching URL | Grid of tiles (target + native terms) |
| 2 | Click a target-language tile | Tile highlights as selected |
| 3 | Click matching native-language tile | Both tiles fade/mark as matched |
| 4 | Click wrong native tile | Both tiles reset; mismatch count increments |
| 5 | Match all pairs | Completion summary |

**DB check:** Same as Vocab Quiz — verify `VocabularyProgress` records with correct `UserId`.

---

## 5. Cloze (`/cloze`)

**Prereqs:** Resource with vocabulary + sentences  
**URL:** `/cloze?resourceId=<id>&skillId=<id>`  
**Services:** VocabularyProgressService, ProgressCacheService

| Step | Action | Verify |
|------|--------|--------|
| 1 | Navigate to cloze URL | Sentence with blank displayed; MCQ or text input |
| 2 | Select/type correct answer | "Correct!" shown; auto-advances after 1.2s |
| 3 | Select/type wrong answer | Correct answer revealed |
| 4 | Complete round | Summary with accuracy |

**DB check:** `VocabularyProgress` records with `Activity = 'Clozure'`.

---

## 6. Writing (`/writing`)

**Prereqs:** Resource with vocabulary  
**URL:** `/writing?resourceId=<id>&skillId=<id>`  
**Services:** TeacherService (AI grading via API), VocabularyProgressService, ProgressCacheService

| Step | Action | Verify |
|------|--------|--------|
| 1 | Navigate to writing URL | Vocab words displayed as prompts |
| 2 | Type a sentence using vocab words | Text appears in input |
| 3 | Click Grade | AI returns accuracy %; toast shows result (may take 10–30s) |

**DB check:** `VocabularyProgress` records with `Activity = 'Writing'`.  
**Known pitfall:** Requires working AI gateway chain (webapp → API → OpenAI).

---

## 7. Shadowing (`/shadowing`)

**Prereqs:** Resource with sentences/transcript  
**URL:** `/shadowing?resourceId=<id>&skillId=<id>`  
**Services:** ElevenLabsSpeechService, IAudioManager, FileSystemService (cache)

| Step | Action | Verify |
|------|--------|--------|
| 1 | Navigate to shadowing URL | Sentence displayed with play controls |
| 2 | Click play ▶ | Audio plays; waveform/progress visible |
| 3 | Change speed (0.6x) | Audio replays at slower speed |
| 4 | Click next sentence | New sentence loads |
| 5 | Toggle translation | Translation text appears/hides |

**Verify:** No stray `}` in footer. No console errors on audio playback.

---

## 8. Minimal Pairs (`/minimal-pairs/session/{id}`)

**Prereqs:** MinimalPair records in DB  
**URL:** `/minimal-pairs/session/{id}?pairIds=<ids>&mode=Focus&trials=20`  
**Services:** ElevenLabsSpeechService, StreamHistoryRepository, MinimalPairRepository

| Step | Action | Verify |
|------|--------|--------|
| 1 | Navigate to session URL | Two answer tiles displayed |
| 2 | Click play 🔊 | Audio of one word plays |
| 3 | Click a tile (answer) | Correct/incorrect feedback |
| 4 | Complete all trials | Session summary with accuracy % and duration |

**DB check:** `SELECT * FROM MinimalPairAttempt WHERE SessionId = '<id>' ORDER BY CreatedAt DESC LIMIT 5`

---

## 9. Conversation (`/conversation`)

**Prereqs:** None (AI-driven)  
**URL:** `/conversation?resourceId=<id>&skillId=<id>`  
**Services:** ConversationService (AI chat via API), optional ElevenLabs TTS

| Step | Action | Verify |
|------|--------|--------|
| 1 | Navigate to conversation URL | Chat interface loads |
| 2 | Type a message, press send | AI response appears (10–30s) |
| 3 | Continue conversation | Context maintained across turns |

**Known pitfall:** Requires working AI gateway chain.

---

## 10. Reading (`/reading`)

**Prereqs:** Resource with sentences  
**URL:** `/reading?resourceId=<id>&skillId=<id>`  
**Services:** ElevenLabsSpeechService (optional audio)

| Step | Action | Verify |
|------|--------|--------|
| 1 | Navigate to reading URL | Sentences displayed |
| 2 | Toggle translation | Translation shows/hides |
| 3 | Click audio (if available) | Sentence audio plays |

---

## 11. Translation (`/translation`)

**Prereqs:** Resource with vocabulary  
**URL:** `/translation?resourceId=<id>&skillId=<id>`  
**Services:** TeacherService (AI grading), TranslationService

| Step | Action | Verify |
|------|--------|--------|
| 1 | Navigate to translation URL | Source sentence displayed |
| 2 | Type translation | Text appears in input |
| 3 | Submit | AI feedback with corrections |

---

## 12. Vocabulary Detail (`/vocabulary/edit/{id}`)

**Prereqs:** Existing VocabularyWord record  
**Services:** ElevenLabsSpeechService (TTS), StreamHistoryRepository

| Step | Action | Verify |
|------|--------|--------|
| 1 | Navigate to edit URL | All fields populated |
| 2 | Click speaker 🔊 next to target term | Audio plays (spinner → done) |
| 3 | Edit a field, click Save | Toast confirms save |

**DB check:** `SELECT * FROM VocabularyWord WHERE Id = '<id>'`

---

## 13. How Do You Say (`/how-do-you-say`)

**Prereqs:** None  
**Services:** ElevenLabsSpeechService, IAudioManager

| Step | Action | Verify |
|------|--------|--------|
| 1 | Navigate to `/how-do-you-say` | Text input + voice selector |
| 2 | Type text, click Speak | Audio plays |

---

## 14. Scene Description (`/scene`)

**Prereqs:** Resource with scene images (optional)  
**URL:** `/scene?resourceId=<id>`  
**Services:** SceneService (AI), ElevenLabsSpeechService

| Step | Action | Verify |
|------|--------|--------|
| 1 | Navigate to scene URL | Image displayed |
| 2 | Type description, submit | AI feedback returned |

---

## Cross-Cutting Verifications

Run after **any** activity to confirm shared concerns:

| Concern | How to verify |
|---------|---------------|
| **Correct UserId** | `SELECT DISTINCT UserId FROM VocabularyProgress ORDER BY LastPracticedAt DESC LIMIT 5` — must be GUID, never `"1"` |
| **Cache freshness** | Dashboard Learning/Known counts update immediately after activity |
| **Audio on webapp** | Browser console has no `audioInterop` errors; speaker button cycles spinner→idle |
| **Audio on native** | `maui-devflow MAUI logs --limit 10` shows no NPE from AudioManager |
| **AI calls via Aspire** | Aspire dashboard structured logs show no 503/401 on `/api/v1/ai/chat` |
| **Auto-advance (quiz)** | After answering, next question loads in ~2s without clicking Next |

---

## Quick Smoke Test (5 min)

Fastest path to verify core functionality end-to-end:

1. **Dashboard** → Confirm Learning Resources loaded, vocab stats display
2. **Vocab Quiz** → Answer 2 questions correctly → verify auto-advance + dashboard update
3. **Import** → Paste `https://www.youtube.com/watch?v=TfNAo3OWXkI` → Fetch → verify transcript appears
4. **Resource Edit** → Open an existing resource → Generate vocabulary → verify words appear
5. **Audio** → Click any 🔊 button → verify no errors
