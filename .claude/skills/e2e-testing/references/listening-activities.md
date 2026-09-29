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
**Services:** ElevenLabsSpeechService, IAudioManager, StreamHistoryRepository

Use only an isolated test database and the canonical Squad test accounts. Do
not run owner-switch or deletion scenarios against a personal/production DB.

| Step | Action | Verify |
|------|--------|--------|
| 1 | Navigate to `/how-do-you-say` | Text input + voice selector |
| 2 | Type text, click Speak | Audio plays |
| 3 | As test user A, inspect history; then sign in as test user B | Each user sees only their own recordings; neither sees a legacy null-owner entry |
| 4 | As test user B, play and delete one of B's recordings | Only B's row is removed; A's DB row and all cached audio files remain intact |
| 5 | While a B recording is playing, delete it and force browser audio stop to reject in the isolated test | The owned DB row still disappears; an audio-stop failure does not block authorized deletion |
| 6 | With no active user or an unknown profile, invoke history reads and a write in the isolated test | Nothing is returned, inserted, or deleted; warning is logged |

On an isolated SQLite fixture, delete test user A's profile while leaving an
unowned recording, then create test user B's profile on the same day. Even with
only B's profile now present, the original row must remain null-owner and
invisible to B; its audio file must remain intact. No native or server legacy
row is automatically claimed without durable per-row provenance.
