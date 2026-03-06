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
