## Active Decisions

(Most recent decisions below. Archived decisions in `decisions/archive/scribe-archive-2026-06-26.md` and earlier archive/processed folders.)

---

### 2026-06-26 — Quick-add existing vocabulary to a Learning Resource

**Session:** Quick-add existing vocabulary to a Learning Resource
**Surface:** Blazor WebApp, `ResourceEdit.razor`
**Requested by:** Captain (David Ortinau)
**Status:** Implemented and E2E verified by Squad agents.

#### Decision

Learning Resource editing now supports quick-add of existing vocabulary through a debounced typeahead backed by a user-scoped repository search. The search is intentionally strict on `Language` and excludes vocabulary words already mapped to the target resource. Inline create-on-miss stamps new `VocabularyWord.Language` from the resource language so newly-created words become eligible for future strict-language lookup.

#### Backend

Wash added `LearningResourceRepository.SearchVocabularyWordsForResourceAsync(query, language, resourceId, limit = 20)` at `src/SentenceStudio.Shared/Data/LearningResourceRepository.cs:737`. The method is user-scoped, applies a strict language filter, excludes words already mapped to the resource, returns best matches first, and is covered by `LearningResourceRepositoryVocabularyLookupTests` (5/5 passing per handoff).

#### WebApp UI

Kaylee updated `ResourceEdit.razor` with debounced typeahead search, keyboard navigation, input refocus after add, inline create-on-miss, per-word remove, a language guard, and a collapsed Bulk import toggle. The UI uses minimal local CSS and new localized resource strings in `AppResources.resx` and `AppResources.ko.resx`.

#### E2E verification

Jayne verified the flow through Aspire WebApp + Playwright + Postgres: quick-add matched existing Korean words, exact attached words were excluded from subsequent results, inline create persisted and mapped a new word with `Language=Korean`, Bulk import collapsed/expanded correctly, remove persisted, browser console had no errors, and Aspire structured logs had no exceptions.

#### Language=NULL ruling

Bulk-import-seeded vocabulary words can have `VocabularyWord.Language = NULL`. The quick-add search's strict language filter intentionally excludes those rows. This is accepted behavior, not a bug: inline create stamps the resource language and will gradually backfill language-stamped vocabulary through normal use. Do not relax the strict language filter to include NULL-language rows without a separate product decision and multi-tenant/data-quality review.

#### Source notes merged

##### jayne-quickadd-e2e.md

# Jayne quick-add E2E finding

Date: 2026-06-26
Surface: local Blazor WebApp via Aspire (`https://localhost:7071`) using `squad-jayne@sentencestudio.test`.

## Result
PASS for Resource details quick-add vocabulary flow on target resource `Jayne QuickAdd Target 20260626-2113` (`b44b34cb-4086-4e39-8e00-2bd64d770994`).

## Evidence
- Existing Korean word quick-add: attached `열쇠-1782074474` and `보다`; input cleared and refocused after add.
- Exclusion: after `보다` was attached, searching `보다` offered `눈여겨보다` and `사용해보다`, not the attached exact `보다` row.
- Inline create: created `새제인단어` with translation `new Jayne word`; DB shows `Language=Korean` and mapping exists.
- Bulk import controls were collapsed by default and visible after toggle.
- Remove: removed `보다`; DB shows all `보다` rows are `not_mapped` to target resource after removal.
- Browser console had 0 errors; Aspire structured logs search for `Exception` returned 0 entries.

## Note
The old bulk import flow on an edit resource seeded `검증단어`, `테스터말`, and `분리단어`, but those words had `Language=NULL` in `VocabularyWord`, so the strict Korean quick-add correctly did not return them. Used existing Korean-stamped words for strict-language quick-add verification instead.

##### kaylee-quickadd-ui.md

# Kaylee quick-add UI decisions

Date: 2026-06-26
Surface: Blazor WebApp ResourceEdit page

## Decisions

- Quick-add search uses a `CancellationTokenSource` debounce with a 250 ms delay before calling `LearningResourceRepository.SearchVocabularyWordsForResourceAsync`.
- Bulk import uses a bound `showBulkImport` Razor toggle rather than Bootstrap JavaScript collapse so the page stays Blazor-state-driven and avoids new JS dependencies.
- The quick-add dropdown uses minimal local CSS (`resource-quick-add-dropdown`, `resource-quick-add-spinner`) and otherwise reuses `card-ss`, `form-control-ss`, Bootstrap list-group, and button classes.

## Rationale

The CTS debounce keeps the repository lookup responsive while cancelling stale searches as Captain types. The Razor toggle keeps existing import handlers and state intact while making the bulky import UI collapsed by default. Minimal CSS keeps the Resource details page decluttered without creating a new component or style system.

---

### 2026-06-28 — Sentence grading uses gpt-5 minimal reasoning effort

**Session:** Optimize Writing/sentence grading latency
**Surface:** Writing, Translation, and Description grading AI calls
**Requested by:** Captain (David Ortinau)
**Status:** Implemented, reviewed, committed, and pushed to `main` as `4d7ce37b`.

#### Decision

Sentence grading for Writing, Translation, and Description now runs `gpt-5` with `reasoning_effort = minimal`. The model stays `gpt-5`; only the per-call reasoning effort changes for these grading paths.

#### Rationale

River found the root cause of grading latency was `gpt-5` running at the provider default reasoning effort, effectively medium, because no `reasoning_effort` override was configured. Real Azure OpenAI/Foundry measurements using the production grading prompt showed median latency improving from 16.4s on `gpt-5` default to 7.2s on `gpt-5` minimal across three runs, about a 2.3x speedup, with no spot-check quality regression. `gpt-5-mini` default measured slower than `gpt-5` minimal at 12.8s median and was not adopted.

#### Scope

River added explicit reasoning-effort plumbing through `AiChatOptionsFactory`, `IAiService.SendPrompt`, `AiGatewayClient`, the `ChatRequest` wire contract, and `/api/v1/ai/chat`. Unknown or blank effort values fall back to the provider default. Vision/`SendImage` and Fast-tier callers remain unchanged, and there is no global default change.

#### Validation

Code review verified both AI paths reach Foundry, parsing is safe, and the change is caller-scoped. Unit tests passed: 735/735.

#### Source notes merged

##### river-grade-perf.md

# River grading performance measurement

Date: 2026-06-28
Author: River

## Decision

Reasoning effort plumbing is added as an explicit optional override, but production defaults remain unchanged. The override flows through `IAiService.SendPrompt`, `TeacherService.GradeSentence`, `IAiGatewayClient.SendPromptAsync`, `ChatRequest.ReasoningEffort`, and the API `/api/v1/ai/chat` endpoint. Null means the provider default, preserving current behavior.

## Measurement

Harness: throwaway console harness (deleted after run) calling the real Azure OpenAI/Foundry endpoint directly via `AzureOpenAIClient` and `Microsoft.Extensions.AI`, using the production `GradeSentence.scriban-txt` prompt with sample `저는 매일 아침에 한국어를 공부해요.` meaning `I study Korean every morning.` Three runs per arm.

| Arm | Runs wall-clock ms | Median wall-clock | Output tokens | Reasoning tokens | Quality |
| --- | ---: | ---: | ---: | ---: | --- |
| gpt-5 default | 16931, 16443, 14943 | 16443 ms | 2063, 2343, 2388 | 1472, 1728, 1728 | Coherent; accuracy 100, fluency 100; vocabulary present |
| gpt-5 minimal | 7155, 7158, 6777 | 7155 ms | 574, 569, 537 | 0, 0, 0 | Coherent; accuracy 100, fluency 100; vocabulary present |
| gpt-5-mini default | 11903, 12822, 13625 | 12822 ms | 1690, 1830, 1901 | 1024, 1216, 1280 | Coherent; accuracy 100, fluency 95-100; vocabulary present |

## Recommendation

Recommend switching grading to `gpt-5` with `reasoning_effort = minimal` after Captain approval. It preserved quality on the spot-check and cut median latency from 16.4s to 7.2s, faster than `gpt-5-mini` default while keeping the stronger model.

## Adoption note

The one-line production adoption is to pass `reasoningEffort: "minimal"` from the Writing grading call (or set the equivalent configuration/caller override once Captain approves). Do not change model defaults yet.
## Validation

- Added unit coverage for `AiChatOptionsFactory` supported effort parsing and OpenAI raw options creation.
- Built affected projects sequentially with 0 errors: Shared net10.0, AppLib net11.0, API net10.0, WebApp net11.0.
- Ran `dotnet test tests/SentenceStudio.UnitTests/SentenceStudio.UnitTests.csproj -f net10.0 --no-restore`: 735/735 passed.

---

### 2026-06-29 — ElevenLabs interactive TTS uses Flash v2.5 and skips per-utterance voice lookup

**Session:** Optimize ElevenLabs audio playback + upgrade the NuGet lib
**Surface:** MAUI in-process speech service, API speech gateway, WebApp HTML5 audio playback
**Requested by:** Captain (David Ortinau)
**Status:** Implemented, reviewed, committed, and pushed to `main` as `de195915` after the NuGet patch bump commit `7f707aec`.

#### Decision

ElevenLabs speech support intentionally lives in both the in-process MAUI service and the API speech gateway. The app path uses `ElevenLabsSpeechService` with `Plugin.Maui.Audio` playback, while the API path serves `/api/v1/speech/synthesize*` for gateway clients and WebApp playback through HTML5 audio JavaScript interop. Keep both surfaces aligned when changing speech behavior.

Interactive short-form TTS now uses configurable `AI:ElevenLabs:InteractiveModel`, defaulting in code to `Model.FlashV2_5` / `eleven_flash_v2_5`. Long-form Reading synthesis stays on `Model.MultiLingualV2` for quality. The short-form paths now construct or cache a `Voice` from the known voice id instead of calling `GetVoiceAsync` for every utterance.

`appsettings.json` is gitignored in this repository, so `AI:ElevenLabs:*` values are local-only unless provided by deployment environment. Any new ElevenLabs config key must have a safe code-side default; production relies on those defaults unless explicitly configured.

#### Rationale

The removed `GetVoiceAsync` call was an avoidable network round-trip before every short utterance. Wash measured interactive time-to-audio improving from about 1090 ms to 274 ms, roughly 4x faster, after switching to Flash v2.5 and dropping per-utterance voice lookup. Reading long-form remains on MultiLingualV2 because narration quality matters more than first-audio latency there.

#### Review follow-up

Code review found a low-severity race in cached voice storage. The coordinator changed `_cachedVoices` to `ConcurrentDictionary`, then committed and pushed the reviewed fix.

#### Source notes merged

##### wash-tts-flash.md

# Wash TTS Flash decision

Date: 2026-06-29
Author: Wash

## Decision

Interactive short-form ElevenLabs TTS uses configurable `AI:ElevenLabs:InteractiveModel`, defaulting to `eleven_flash_v2_5` / `Model.FlashV2_5`. Long-form timestamped Reading synthesis remains on `Model.MultiLingualV2` for quality.

The interactive paths now construct `new Voice(voiceId, voiceId)` (or reuse an already-cached voice in the MAUI in-process service) instead of calling `GetVoiceAsync` for every utterance. ElevenLabs-DotNet 3.7.2 exposes a public `Voice(string id, string name)` constructor and `TextToSpeechRequest(Voice voice, ...)`, so the TTS call only needs the known voice id. API gateway voice slug handling remains allow-listed: unknown requested slugs fall back to the configured default voice instead of being forwarded to ElevenLabs.

## Rationale

`GetVoiceAsync` added a network round-trip to every short utterance before synthesis. Interactive activities need lowest latency more than long-form narration quality, and ElevenLabs `FlashV2_5` is the low-latency multilingual model that supports Korean. Keeping the model configurable lets Captain dial back to `eleven_multilingual_v2` if ear-testing finds a quality regression without rebuilding.

---

### 2026-06-29 — ElevenLabs interactive TTS adopts eleven_v3 for Korean fidelity

**Session:** Improve ElevenLabs audio fidelity/consistency for Korean
**Surface:** MAUI in-process speech service and API speech gateway interactive synthesis
**Requested by:** Captain (David Ortinau)
**Status:** Implemented, reviewed, and committed as `c8f28792`.

#### Decision

Interactive short-form ElevenLabs TTS now defaults to `eleven_v3` in both `ElevenLabsSpeechService.TextToSpeechAsync` and the API gateway `/api/v1/speech/synthesize` path. Captain ear-tested the generated fidelity matrix and selected v3: "v3 wins everywhere." Fidelity is prioritized over higher latency and cost for interactive Korean TTS.

The model remains configurable through `AI:ElevenLabs:InteractiveModel`; set it to `eleven_multilingual_v2` to flip back without a code change if v3 creates unacceptable upstream behavior.

#### Reading long-form/timestamped scope

Reading long-form timestamped synthesis remains on `eleven_multilingual_v2` for this round. `eleven_v3` has a 5,000 character limit and timestamped endpoint support was not confirmed by a live Reading path test. Follow-up: evaluate v3 for Reading separately, including chunking behavior and timestamp support.

#### ElevenLabs guidance captured

- Official guidance recommends `style = 0`; the SDK constructor default of `0.45` is not the desired app default. Non-zero style can cause instability, inconsistent speed, mispronunciation, and extra sounds.
- Flash v2.5 is unsuitable for Korean fidelity because text normalization is disabled and the smaller model hallucinated Korean in ear tests.
- `language_code` is not supported on `eleven_multilingual_v2`.
- `seed` is best-effort determinism, useful for comparisons but not a hard reproducibility guarantee.
- The ElevenLabs-DotNet SDK model enum lacks v3, so app code constructs `new Model("eleven_v3")`.

#### Source notes merged

##### river-v3-adopt.md

Interactive short-form ElevenLabs TTS should default to `eleven_v3` in both the MAUI in-process `ElevenLabsSpeechService.TextToSpeechAsync` path and the API gateway `/api/v1/speech/synthesize` path. Reading long-form timestamped synthesis stays on `eleven_multilingual_v2` for this round because of the documented `eleven_v3` 5,000 character limit and unverified timestamped endpoint support. Captain ear-tested the TTS fidelity matrix and chose v3: "v3 wins everywhere." ElevenLabs-DotNet 3.7.2 does not expose a static v3 model value, so code constructs `new Model("eleven_v3")`.

##### river-tts-fidelity-matrix.md

River generated the ElevenLabs TTS fidelity ear-test matrix with a throwaway console harness that called the real ElevenLabs REST API directly, outside app code. The HyunBin voice `s07IwTCOrCDCaETjUVjx` was used for all clips, output went to `.copilot-scratch/tts-fidelity-matrix/`, and 23 MP3 files plus `MANIFEST.md` were produced. Fixed seed `12345` was used for all `eleven_multilingual_v2` arms so Captain compared settings rather than random variation. Arms covered baseline style 0.45, style 0, punctuation, `next_text`, higher stability, and v3.
