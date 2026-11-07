# Other Activity E2E Tests

## 1. Conversation (`/conversation`)

**Prereqs:** None (AI-driven)  
**URL:** `/conversation?resourceId=<id>&skillId=<id>`  
**Services:** ConversationService (AI chat via API), optional ElevenLabs TTS

| Step | Action | Verify |
|------|--------|--------|
| 1 | Navigate to conversation URL | Chat interface loads |
| 2 | Type a message, press send | AI response appears (10–30s) |
| 3 | Continue conversation | Context maintained across turns |

**Pitfall:** Requires working AI gateway chain.

### Native iOS composer and voice mode (2026-11-06)

Gate record: `docs/conversation-voice-learning-value-gate.md`. Voice needs the API's
`/api/v1/realtime/client-secret` and the `gpt-realtime` / `gpt-4o-transcribe` deployments; the
waveform only shows when the app has an API origin.

| Step | Action | Verify |
|------|--------|--------|
| 1 | Open Conversation | No Scenario row above the chat; the toolbar has the masks (scenario) and translate (language) menus |
| 2 | Open the masks menu, pick another scenario | Checkmark on the current one; the chat restarts with the new persona's opening line |
| 3 | Look at the composer | Round quick-phrases button, then a capsule field with a waveform button inside; no Send button, no card |
| 4 | Type a message, press Return | The message is sent, the reply arrives (the keyboard closes) |
| 5 | Tap the quick-phrases button | The eight Korean phrases open above the composer; tapping one fills the field |
| 6 | Tap the waveform (grant the microphone) | Voice panel replaces the composer: Connecting, then "{persona} is speaking"; the partner reads its opening line aloud and no new bubble appears (the app log shows "Realtime voice is reading the partner's last message aloud"). Its later spoken turns appear with a microphone marker and no Listen |
| 7 | When the partner stops | Status "Listening. Speak when you are ready." and the level bar moves while speaking |
| 8 | Speak a target-language sentence (phone only) | A learner bubble shows "Transcribing…", then the transcript; the partner answers aloud |
| 9 | Ask what a word means (phone only) | The partner's turn shows "Checked your vocabulary: word → target = native" (or "word is not in your list") |
| 10 | Tap End voice | Composer returns; spoken turns stay in the chat; typing continues the same thread |
| 11 | Deny the microphone (Settings), tap the waveform | Notice "Voice mode needs microphone access."; text chat unchanged |
| 12 | Start voice, tap Back | Returns to the dashboard; the app log shows "Voice audio stopped" |
| 13 | During voice, open the sliders menu | Two titled groups, "Speaking speed" (100%, 90%, 80%, 70%) and "Pause before reply" (0.5 s, 1 s, 1.5 s, 2.5 s), with the current choices checked; VoiceOver reads "Voice pace, 100%, 0.5 s" |
| 14 | Pick 80%, then 1.5 s | The checkmarks move; the app log shows "Realtime voice pace: speed 0.8, pause 500 ms", then "speed 0.8, pause 1500 ms"; on a phone the partner's next turn is slower and it waits longer after you stop speaking |
| 15 | End voice, quit the app, reopen Conversation, start voice | The session opens with the saved pace ("speed 0.8, pause 1500 ms"); another account on the device has its own pace |

**Pitfalls:** granting a simulator permission terminates the app (relaunch). The simulator's
microphone is the Mac's default input and can be silent, so steps 8-9 need a phone. iPhone Duo
simulators cannot be screenshotted; use an iPhone 18 Pro (iOS 27.0).

## 2. Reading (`/reading`)

**Prereqs:** Resource with sentences  
**URL:** `/reading?resourceId=<id>&skillId=<id>`  
**Services:** ElevenLabsSpeechService (optional audio)

| Step | Action | Verify |
|------|--------|--------|
| 1 | Navigate to reading URL | Sentences displayed |
| 2 | Toggle translation | Translation shows/hides |
| 3 | Click audio (if available) | Sentence audio plays |

## 3. Scene Description (`/scene`)

**Prereqs:** Resource with scene images (optional)  
**URL:** `/scene?resourceId=<id>`  
**Services:** SceneService (AI), ElevenLabsSpeechService

| Step | Action | Verify |
|------|--------|--------|
| 1 | Navigate to scene URL | Image displayed |
| 2 | Type description, submit | AI feedback returned |

## 4. Video Watching (`/video-watching`)

**Prereqs:** Resource with YouTube video URL  
**URL:** `/video-watching?resourceId=<id>`  
**Services:** None (embedded YouTube player)

| Step | Action | Verify |
|------|--------|--------|
| 1 | Navigate to video URL | Embedded player loads |
| 2 | Play video | Video plays; transcript visible if available |
