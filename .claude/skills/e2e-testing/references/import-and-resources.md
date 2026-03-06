# Import & Resource E2E Tests

## 1. Import (`/import`)

**Prereqs:** None  
**Services:** YouTubeImportService, TranscriptFormattingService (AI), API `/api/v1/ai/chat`

| Step | Action | Verify |
|------|--------|--------|
| 1 | Navigate to `/import` | Page loads with URL input |
| 2 | Paste YouTube URL, click Fetch | Transcript text appears; language dropdown populated |
| 3 | Select language | Transcript updates to selected language |
| 4 | Click "Polish with AI" | Transcript reformatted (30–60s); no 503 in Aspire logs |
| 5 | Click Save | Redirects to `/resources/edit/{id}` |

**DB:** `SELECT Id, Title, Language FROM LearningResource ORDER BY CreatedAt DESC LIMIT 1`  
**Pitfall:** YouTube language names include "(auto-generated)" — must be stripped on save.

## 2. Resource Edit — Generate Vocabulary (`/resources/edit/{id}`)

**Prereqs:** A saved LearningResource with transcript  
**Services:** AiGatewayClient → API `/api/v1/ai/chat` → OpenAI

| Step | Action | Verify |
|------|--------|--------|
| 1 | Navigate to `/resources/edit/{id}` | Resource fields populated; transcript visible |
| 2 | Click "Generate from Transcript" | Spinner appears; wait up to 120s |
| 3 | Generation completes | Vocabulary list appears below transcript |

**DB:**
```sql
SELECT COUNT(*) FROM ResourceVocabularyMapping WHERE ResourceId = '<id>';
```
**Pitfall:** Non-Latin languages use different generation logic. `IsLatinScriptLanguage` must normalize names.

## 3. Vocabulary Detail (`/vocabulary/edit/{id}`)

**Prereqs:** Existing VocabularyWord  
**Services:** ElevenLabsSpeechService, StreamHistoryRepository

| Step | Action | Verify |
|------|--------|--------|
| 1 | Navigate to edit URL | All fields populated |
| 2 | Click speaker 🔊 next to target term | Spinner → done (audio plays) |
| 3 | Edit a field, click Save | Toast confirms save |

**DB:** `SELECT * FROM VocabularyWord WHERE Id = '<id>'`
