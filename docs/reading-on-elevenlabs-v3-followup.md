# Follow-up: Evaluate `eleven_v3` for the Reading (long-form, timestamped) audio path

**Status:** Deferred (not started). Captain decision 2026-06-29: ship `eleven_v3` for
interactive TTS now, leave Reading on `eleven_multilingual_v2` for now.

## Context

As of 2026-06-29 the app's **interactive** short-form TTS (Shadowing, Conversation,
NumberDrill, VocabQuiz, HowDoYouSay, and the `/api/v1/speech/synthesize` gateway) runs on
`eleven_v3` — the Captain ear-tested a fidelity matrix and chose v3 across the board
(commit `c8f28792`). See `.squad/decisions.md` (2026-06-29 ElevenLabs v3 entry) and the
ElevenLabs research findings for the full rationale.

**Reading deliberately stayed on `eleven_multilingual_v2`.** Reading uses
`ElevenLabsSpeechService.GenerateTimestampedAudioAsync` (and the
`/api/v1/speech/synthesize-timestamped` gateway) with `withTimestamps: true` to drive
character-level audio↔text synchronization for the whole resource transcript.

## Why Reading is not on v3 yet (the two blockers)

1. **5,000-character limit on `eleven_v3`.** Reading synthesizes the *entire*
   `resource.Transcript`, which can exceed 5,000 characters. `eleven_multilingual_v2`
   allows ~10,000. Moving Reading to v3 would require **chunking** long transcripts and
   concatenating the audio.

2. **Character-timestamp support on `eleven_v3` is unconfirmed.** Reading depends on the
   `/with-timestamps` endpoint returning per-character end times to build the sync map.
   ElevenLabs docs (as of 2026-06-29) accept a `model_id` on the timestamps endpoint but
   do **not** explicitly confirm v3 compatibility, and v3 explicitly does **not** support
   request stitching — which is the usual tool for stitching chunked long-form audio while
   preserving prosody. So chunking v3 cleanly is non-trivial.

## What to investigate when we pick this up

1. **Confirm v3 + `/with-timestamps`.** Make a real API call: `eleven_v3` against
   `POST /v1/text-to-speech/{voice_id}/with-timestamps`. Does it return a valid
   `alignment.character_end_times_seconds` array for Korean Hangul? If it errors or returns
   empty alignment, v3-for-Reading is blocked at the timestamp layer — stop here.
2. **Chunking strategy for the 5K limit.** If timestamps work: split the transcript on
   sentence boundaries into <5K chunks, synthesize each, and stitch. Because v3 has **no
   request stitching**, evaluate prosody seams between chunks by ear. Consider
   `previous_text` / `next_text` (supported) as a softer continuity hint.
3. **Per-chunk timestamp offset math.** When concatenating chunk audio, the
   `character_end_times_seconds` of chunk N must be offset by the cumulative duration of
   chunks 0..N-1 so the global sync map stays correct. Verify against the existing
   `SentenceTimingCalculator` / `TimestampedAudioManager` consumers.
4. **Cache key + cost.** The Reading cache key is
   `timestamped_{resource.Id}_{voiceId}_{speed:F1}` in
   `ElevenLabsSpeechService.GenerateTimestampedAudioAsync`. If the model becomes a
   variable, add the model to the key so v2 and v3 audio don't collide. Note v3 costs more
   per character than v2 — Reading is the longest content, so this is the highest-cost path.
5. **Ear-test.** Same gate as the interactive switch: generate a Reading sample on v3 vs
   v2 and let the Captain judge. Fidelity is the deciding factor, not latency.

## Acceptance criteria for adopting v3 in Reading

- v3 returns valid character timestamps for Korean on `/with-timestamps`.
- Long transcripts (>5K chars) are chunked + stitched with no audible prosody seams and a
  correct global sync map (highlight follows audio accurately end-to-end).
- Captain ear-test confirms v3 Reading quality beats the current v2 Reading.
- Cache keyed by model; cost reviewed and accepted.

If any of those fail (especially #1), document the blocker and keep Reading on
`eleven_multilingual_v2`.

## Relevant code

- `src/SentenceStudio.AppLib/Services/ElevenLabsSpeechService.cs` —
  `GenerateTimestampedAudioAsync` (Reading; line ~291 hardcodes `Model.MultiLingualV2`).
- `src/SentenceStudio.Api/Program.cs` — `/api/v1/speech/synthesize-timestamped`
  (line ~889 hardcodes `Model.MultiLingualV2`).
- Interactive (already on v3, for reference): `TextToSpeechAsync` + `/api/v1/speech/synthesize`.
- Model is constructed as `new Model("eleven_v3")` because the ElevenLabs-DotNet 3.7.2
  `Model` set has no v3 entry.
