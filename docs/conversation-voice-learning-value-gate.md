# Learning Value Gate: Conversation voice mode (native iOS, 2026-11-06)

Gate: `.squad/skills/learning-value-gate/SKILL.md`. Change: the native Conversation activity gains
a voice mode (spoken partner, spoken learner turns) started from the waveform button in the
message field, and its composer becomes Messages-style (no Send button; Return sends). Source of
the mode: the Techorama demo (`techorama/next-gen-demos`, `Conversation.Voice.razor.cs`). Departures
46-48 in `docs/native-port-departures.md`.

## 1. Learning objective

The learner understands a native-speed spoken partner in the target language and answers aloud in
the target language, in a role-play scenario, so listening comprehension and spontaneous spoken
production are practised together.

## 2. Language-role matrix

Text mode is unchanged by this work except that Return replaces the Send button.

| Mode | Partner prompt | Partner audio | Learner response | L2 exposure | L2 retrieval | Verdict |
|---|---|---|---|---|---|---|
| Text (default) | target text | target (Listen) | typed, any language | yes: partner text and audio | production when the learner types the target language; the partner's reply and the grammar corrections stay in the target language either way | ship (unchanged) |
| Text + quick phrases | target text | target (Listen) | target phrase put into the field | yes | recognition of a target phrase | ship (unchanged) |
| Voice | target transcript of the partner's speech | target, live | spoken, transcribed as the target language | yes: live target speech and its transcript | spoken production of the target language | ship |
| Voice, learner speaks the native language | target transcript | target, live | spoken native | yes: the partner answers in the target language ("If you did not understand the learner, say so kindly in {{ target_language }}") | none for that turn | ship: the partner side keeps every turn in the target language; no native-only state |

Voice has no toggles. Quick phrases, typing and Listen are unavailable while voice runs (the voice
panel replaces the composer; Listen is refused). The target language comes from the profile or the
toolbar language menu; changing it ends voice and restarts the chat.

The partner is instructed to speak only the target language (`Conversation.voice.scriban-txt`:
"Speak only {{ target_language }}"). Input transcription is pinned to the target language
(`ConversationVoiceSession.ToIsoLanguage`).

## 3. Learner action and L2 evidence

- Voice, partner turn: comprehension. The learner parses live target speech; the transcript appears
  as it is spoken.
- Voice, learner turn: production. The learner generates a novel spoken target utterance; the
  transcript shows what was heard.
- Vocabulary notes: when the learner asks what a word means, the partner looks it up in the
  learner's own saved words and the note shows "word → target = native". This is scaffolding inside
  the conversation, not an assessed answer.

## 4. Empty-state and default review

A first-time learner lands in text mode, as before: the persona's target-language opening line,
Listen, and the composer. Voice is off until the learner taps the waveform. On first use the system
asks for the microphone; if the learner refuses, a notice says voice needs the microphone and the
text chat stays as it was. Without an API origin, or on a head with no microphone port (Android
today), the waveform is not shown. Every default path lands in a ship row.

## 5. Answer-leakage checklist

- [x] No graded answer exists in voice mode: voice turns are not graded or scored, and nothing is
  saved (as in the demo), so nothing can leak an answer or inflate mastery.
- [x] The vocabulary note shows a meaning only after the partner looked the word up, which happens
  when the learner asks or uses the word; it is a gloss, not a quiz answer.
- [x] Accessibility labels name the controls (Talk with voice, End voice, the status), not content.
- [x] Audio is streamed, not cached; no file names carry learner text.

## 6. Acceptance cases

`.claude/skills/e2e-testing/references/other-activities.md` §1 (Conversation) lists the native iOS
composer and voice cases. Verified 2026-11-06 on an iPhone 18 Pro simulator (iOS 27.0) against the
local API, except the learner's spoken turn: the simulator's microphone gave silence, so that row
needs a phone.
