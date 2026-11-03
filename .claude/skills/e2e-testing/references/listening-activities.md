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

WebApp/MAUI regression (docs/native-port-backports.md B26-B28; same Learning
Value Gate matrix as the native heads below: the target sentence is always
shown and the audio is always the target sentence):

| Step | Action | Verify |
|------|--------|--------|
| W1 | Tap Slow, then Normal | Slow plays the same clip at 0.6x (browser `playbackRate`, device player speed); no second synthesis request; Normal replays it at 1.0x |
| W2 | While playing, tap a point on the waveform (or focus it and press arrow keys) | Playback jumps to that point; the playhead moves there (device player also seeks while paused) |
| W3 | Go offline before the next sentence's audio is made, or make synthesis fail | `shadowing-audio-status` alert (offline / could not be played) stays under the sentence; Play tries again and clears it |

### Native heads (iOS SwiftUI / Android Compose)

Entry: Today's Plan card of type Shadowing **or Listening** (both open this
screen), or the Choose My Own `shadowing` tile with a resource selected.
Automation IDs: `native.shadowing.*` (target, translation, translation-toggle,
pronunciation, waveform, elapsed, duration, previous, slow, play, normal, next,
counter, audio-status, empty, empty.back, error).

Learning Value Gate matrix (objective: hear and repeat target-language
sentences with the written target form always visible):

| Content | Translation | Audio | Speed | On screen | Verdict |
|---|---|---|---|---|---|
| vocabulary (AI / as-is) | hidden (default) | target TTS | Normal (default) | target text + target audio | ship |
| vocabulary | shown | target TTS | Slow / Normal | target text + native gloss + target audio | ship |
| transcript | none exists (toggle absent) | target TTS | Slow / Normal | target text + target audio | ship |
| any | any | offline uncached / failed | any | target text + audio alert; Play retries | ship (text-only exposure) |

No toggle hides the target text; audio is always the target sentence.

| Step | Action | Verify |
|------|--------|--------|
| N1 | Open a Listening plan card | `native.shadowing.target` shows Korean; timer row ActivityType = Listening |
| N2 | Wait for audio | waveform + `0:00.0 / m:ss.f`; Play enabled; no autoplay |
| N3 | Tap Play | Pause shown; elapsed advances; ends on Play with elapsed = duration |
| N4 | Tap Slow | Slow selected; audio reloads and autoplays at 0.6x |
| N5 | Show translation, then Next | translation hidden again; counter `2 / n`; Prev enabled |
| N6 | Back | Dashboard; plan card minutes saved; no VocabularyProgress or UserActivity rows written |
| N7 | Restart, reopen | cached clip plays without a synthesize request |

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
| 7 | Speak the same phrase twice with the same voice | One `StreamHistory` row; the second play makes no synthesis request (docs/native-port-backports.md B31) |
| 8 | Sync an entry made on a phone, then play it in the WebApp | The clip is synthesized again in the entry's own voice; the same row (same Id) now points at the new clip; no second row (B38) |
| 9 | On iOS MAUI, reinstall on a simulator and play an older entry | The clip is found by file name under the new container's AudioCache and plays without synthesis (B35) |
| 10 | Read the history timestamps; make synthesis fail | Times are in the learner's time zone; the failure toast is the localized message, never exception text (B32) |

On an isolated SQLite fixture, delete test user A's profile while leaving an
unowned recording, then create test user B's profile on the same day. Even with
only B's profile now present, the original row must remain null-owner and
invisible to B; its audio file must remain intact. No native or server legacy
row is automatically claimed without durable per-row provenance.
