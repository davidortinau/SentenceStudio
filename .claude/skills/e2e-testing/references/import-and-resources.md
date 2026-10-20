# Import & Resource E2E Tests

The previous native iOS Resources browse/detail/three-field implementation
was a partial smoke-test subset, not accepted source parity. The Add/Delete,
import, vocabulary generation, quick-add, and full Resource Edit cases below
remain required for native source-fidelity acceptance when their journeys are
implemented; they also continue to apply to the WebApp. Do not expand Android
Resources by copying the partial iOS subset. Already-created Android screens
can be corrected in parallel from the same original source contract.

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

**Prereqs:** A saved, owned, editable LearningResource; a verified owner-bound AI gateway

**Services:** Owner-bound AiGatewayClient → API `/api/v1/ai/chat` → OpenAI

| Step | Action | Verify |
|------|--------|--------|
| 1 | Open the owned resource; change transcript, language, title, and another scalar without pressing Save | Draft remains editable and AI has not been called |
| 2 | Click "Generate from Transcript" | Spinner appears; AI receives the **current unsaved** transcript and language, not the previously persisted values |
| 3 | Generation completes | Draft scalars persist with the new vocabulary mappings; report actual generated/created/reused/skipped counts, not guessed duplicates |
| 4 | Change the stored resource while a second AI response is in flight | Optimistic conflict preserves the newer row and draft; no stale vocabulary is attached |
| 5 | Retry with another signed-in owner | Foreign resource and vocabulary stay invisible; no pending write or prompt is sent under the wrong bearer |

**DB:**
```sql
SELECT COUNT(*) FROM ResourceVocabularyMapping WHERE ResourceId = '<id>';
```
**Pitfall:** Non-Latin languages use different generation logic. `IsLatinScriptLanguage` must normalize names.
The native iOS Save-first Generate gate remains a **temporary, non-parity** state until
the head enables this current-draft contract after Shared integration; a passing
SQLite test alone does not satisfy this running-app case.

### 2.1 Native resource file and vocabulary boundaries

- On create and edit, import comma/tab `.txt` or `.csv` with an empty native gloss.
  Reopen after restart: the owned word and mapping retain the empty gloss.
- Import on an owned resource whose Language is `Other`; paste/file remain
  available even though quick-add search/create is unavailable. Foreign or
  smart resources refuse the same mutation.
- Unsupported extension, a file over 1 MB (including an underestimated stream),
  empty file, and read failure show distinct localized, non-parameterized errors.
  No file path or exception text is displayed or logged.
- Import only duplicates: no new mapping and **no new pending-sync revision**.
  Confirmed sync requires an owner-bound operation and a server-row receipt,
  not an ambient `TriggerSyncAsync` or a local pending indicator.

## 3. Vocabulary Detail (`/vocabulary/edit/{id}`)

**Prereqs:** Existing VocabularyWord  
**Services:** ElevenLabsSpeechService, StreamHistoryRepository

| Step | Action | Verify |
|------|--------|--------|
| 1 | Navigate to edit URL | All fields populated |
| 2 | Click speaker 🔊 next to target term | Spinner → done (audio plays) |
| 3 | Edit a field, click Save | Toast confirms save |

**DB:** `SELECT * FROM VocabularyWord WHERE Id = '<id>'`
