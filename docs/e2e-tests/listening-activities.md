# Listening Activity E2E Tests

## 1. Shadowing (`/shadowing`)

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

**Verify:** No stray characters in footer. No console errors on audio playback.

## 2. Minimal Pairs (`/minimal-pairs/session/{id}`)

**Prereqs:** MinimalPair records in DB  
**URL:** `/minimal-pairs/session/{id}?pairIds=<ids>&mode=Focus&trials=20`  
**Services:** ElevenLabsSpeechService, StreamHistoryRepository, MinimalPairRepository

| Step | Action | Verify |
|------|--------|--------|
| 1 | Navigate to session URL | Two answer tiles displayed |
| 2 | Click play 🔊 | Audio of one word plays |
| 3 | Click a tile (answer) | Correct/incorrect feedback |
| 4 | Complete all trials | Summary with accuracy % and duration |

**DB:** `SELECT * FROM MinimalPairAttempt WHERE SessionId = '<id>' ORDER BY CreatedAt DESC LIMIT 5`

## 3. How Do You Say (`/how-do-you-say`)

**Prereqs:** None  
**Services:** ElevenLabsSpeechService, IAudioManager

| Step | Action | Verify |
|------|--------|--------|
| 1 | Navigate to `/how-do-you-say` | Text input + voice selector |
| 2 | Type text, click Speak | Audio plays |
