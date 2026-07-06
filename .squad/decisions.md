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


---

### 2026-07-02 — Vocab Quiz exact session resume via ActivitySession

**Session:** Vocab Quiz Session & Resume feature
**Surface:** Blazor WebApp Vocab Quiz, reusable Shared data layer, PostgreSQL and SQLite migrations
**Requested by:** Captain (David Ortinau)
**Status:** Implemented and verified by Squad agents.

#### Decision

SentenceStudio now has a reusable `ActivitySession` foundation for exact activity resume. `ActivitySession` stores activity-agnostic session rows with serialized `StateJson`; Vocab Quiz is the first consumer through `VocabQuizSessionSnapshot`, which stores launch IDs, ordered word IDs, and session-local counters rather than full vocabulary/progress graphs.

Vocab Quiz computes a deterministic launch context key from query parameters, checks for an in-progress resumable snapshot, and gates startup behind localized Resume / Start fresh choices. Resume re-fetches vocabulary and progress fresh, then overlays only session-local counters onto matching quiz items. Start fresh abandons the old session.

#### Data and migrations

Wash added matched dual-provider migration `20260702145959_AddActivitySession` for PostgreSQL and SQLite. `ActivitySession.Status` is persisted as text via `HasConversion<string>()`. `IActivitySessionService` fails closed on empty user IDs, returns null/no-op rather than falling through to unfiltered multi-tenant queries, and enforces one in-progress row per `(UserId, ActivityType, LaunchContextKey)` by updating the newest row while abandoning older duplicates.

#### UI integration

Kaylee wired `src/SentenceStudio.UI/Pages/VocabQuiz.razor` to `IActivitySessionService` and `VocabQuizSessionSnapshot`. Snapshot persistence runs best-effort from answer progression, new-round setup, and disposal; failures are logged and do not block quiz behavior. Completion marks the active session completed and suppresses dispose-time flush so completed sessions are not recreated as in-progress. The prompt uses localized strings in English and Korean and Bootstrap icons only.

#### Test coverage

Jayne added 11 focused unit tests across `VocabQuizSessionSnapshotTests` and `ActivitySessionServiceTests`: launch-context key determinism/normalization, null/empty equivalence, snapshot round-trip with ordered lists, save insert/update behavior, duplicate in-progress cleanup, resumable lookup scoping, completed/abandoned exclusion, empty-user guard, and user isolation.

#### E2E verification

Squad verified through the WebApp with Aspire and Playwright: answer-time saves, resume prompt, exact resume of word/turn/stats, continued quiz progression, Start fresh plus abandon behavior, no console errors, and live PostgreSQL migration application. SQLite migration was validated deterministically against a copy of the real `sstudio.db3` schema: table/index creation, CRUD, index usage, and drop path all succeeded.

#### DevFlow friction captured

During mobile migration validation, `maui devflow wait` attached to a stale `FoundryStudio` DevFlow agent instead of the freshly-launched SentenceStudio app, making `scripts/validate-mobile-migrations.sh` report a false pass while validating no SentenceStudio native logs. This is dogfooding tooling friction: DevFlow needs project/app filtering for attach, and the migration validation script must fail when logs are unavailable or the attached agent does not match the built project.

#### Source notes merged

##### wash-activity-session.md

# Wash activity session data-layer decision

Date: 2026-07-02
Author: Wash

## Decision

Add a reusable `ActivitySession` table and `IActivitySessionService` contract for exact activity resume. Activity-specific state is serialized into `StateJson`; the first consumer is `VocabQuizSessionSnapshot`, which stores only launch IDs, ordered word IDs, and session-local counters rather than full vocabulary/progress graphs.

## Rationale

The table is activity-agnostic so Reading/Writing/etc. can adopt persistent exact resume without another schema change. `ActivitySession.Status` is a small enum in code and is persisted as text via `HasConversion<string>()`, matching the existing NumberContext enum conversion style. Empty user IDs fail closed with warnings and return null/no-op so resume queries never fall through to an unfiltered multi-tenant query.

## Assumptions

`SaveSnapshotAsync` returns `ActivitySession?` instead of a non-null `ActivitySession` because the required empty-userId guard must no-op and return null rather than creating an invalid row. The service enforces one in-progress row per `(UserId, ActivityType, LaunchContextKey)` by updating the newest matching row and abandoning older matching in-progress rows.

## Migration

Migration timestamp: `20260702145959`. PostgreSQL and SQLite migration files were created with matching table/index definitions and provider-specific type casing.

##### kaylee-vocab-quiz-resume.md

# Kaylee Vocab Quiz resume integration

Date: 2026-07-02
Surface: `src/SentenceStudio.UI/Pages/VocabQuiz.razor`

## Decision

Vocab Quiz resume is wired at the Razor page boundary using Wash's reusable `IActivitySessionService` and `VocabQuizSessionSnapshot` contract. The page computes a deterministic launch context key from the query parameters once during initialization, skips resume entirely when `active_profile_id` is empty, and shows a localized Resume/Start fresh gate before loading vocabulary when a parseable in-progress snapshot exists.

## Implementation notes

- Resume re-fetches vocabulary and progress fresh, then overlays only session-local counters from the snapshot onto matching `VocabularyQuizItem`s. Missing word ids are dropped defensively.
- Snapshot persistence is best-effort and non-blocking for quiz behavior: failures are logged with `ILogger<VocabQuiz>` and do not interrupt the quiz.
- Completion marks the active session completed before summary Done navigation or natural all-mastered exit, and suppresses the dispose-time snapshot flush after completion so completed sessions are not recreated as in-progress.
- The prompt uses Bootstrap icons only and localized strings in `AppResources.resx` / `AppResources.ko.resx`.

## Validation

- Requested build command `dotnet build src/SentenceStudio.WebApp/SentenceStudio.WebApp.csproj -f net10.0` could not run because `SentenceStudio.WebApp.csproj` currently targets `net11.0`, producing `NETSDK1005` for missing `net10.0` assets.
- Actual WebApp target build passed with `dotnet build src/SentenceStudio.WebApp/SentenceStudio.WebApp.csproj`: Build succeeded, 0 errors.

##### jayne-session-tests.md

# Jayne session-resume unit test decision

Date: 2026-07-02
Surface: Vocab Quiz Session & Resume data layer
Author: Jayne

## Decision

Add focused unit coverage for the reusable Vocab Quiz session data layer in `tests/SentenceStudio.UnitTests/` rather than waiting for Razor UI tests. Use the existing SQLite in-memory `ApplicationDbContext` harness pattern from data-layer tests so `ActivitySessionService` is exercised against real EF Core behavior.

## Coverage added

- `VocabQuizSessionSnapshotTests`: launch-context key determinism and normalization, null/empty equivalence, and full snapshot serialize/deserialize round-trip with list order preserved.
- `ActivitySessionServiceTests`: save insert/update behavior, duplicate in-progress cleanup by abandoning older rows, resumable lookup scoping, completed/abandoned exclusion, empty user no-op/read-null guard, and user A/user B isolation.

## Validation

- Targeted new tests: 11/11 passed.
- Full unit test project: 746/746 passed on rerun.
- One earlier full run hit an unrelated transient failure in `SharedIngestProcessorTests.YouTubeUrlItem_VideoImportKickedOff_ItemRemoved_NotifierVideoImportStarted`; rerun passed without code changes.

##### squad-devflow-stale-agent-friction.md

### 2026-07-02: MAUI DevFlow stale-agent collision breaks migration validation gate

**By:** Squad (Coordinator), on behalf of Captain
**Context:** Verifying the Vocab Quiz Session/Resume feature (ActivitySession EF migration, dual-provider).

**What happened:** `scripts/validate-mobile-migrations.sh` builds the SentenceStudio Mac Catalyst head and launches it, then calls `maui devflow wait` to attach and scan native logs. Instead of attaching to the freshly-launched SentenceStudio app, DevFlow attached to a DIFFERENT already-running app — `FoundryStudio` (net11.0-macos, port 9223, sessionId `dwppfoundrystudioappcsproj`). Because it attached to the wrong agent, it could not fetch SentenceStudio native logs, yet the script still exited 0 with "no errors found" — a FALSE PASS. Wash hit the identical failure earlier in the same session (attached to a FoundryStudio agent, logs unavailable).

**Impact:** The mobile SQLite migration gate cannot be trusted when any other DevFlow-enabled MAUI app is running on the machine. The script reports success while validating nothing.

**Root cause (suspected):** DevFlow broker/`maui devflow wait` selects the first/any connected agent rather than the agent for the project/TFM under test. No project or app-name filter on attach. The script also treats "logs unavailable" as success instead of failure.

**Workarounds used this session:** (1) Verified the feature + Postgres migration live on the webapp (Aspire+Playwright). (2) Deterministically validated the SQLite migration DDL by applying it to a copy of the real `sstudio.db3` production schema (CREATE TABLE + indexes + CRUD + index-usage + DROP all clean). (3) Confirmed both mobile heads (net11.0-maccatalyst, net11.0-macos) compile the migration.

**Recommended follow-ups (dogfooding — tooling friction takes priority):**
1. `maui devflow wait` / broker should accept a `--project` or `--app-name`/`--tfm` filter and attach only to the matching agent.
2. `scripts/validate-mobile-migrations.sh` should FAIL (non-zero) when native logs cannot be fetched or when the attached agent's project != the one it built, instead of printing "no errors found".
3. Consider filing upstream (microsoft/dotnetdevflow) with this repro: two DevFlow apps running, `wait` attaches to the wrong one.


---

### 2026-07-03 — Vocab Quiz uses persistent demonstration counters

**Session:** Vocab Quiz persistent demonstration counters
**Surface:** Shared vocabulary progress model/service, dual-provider EF migrations, Blazor WebApp Vocab Quiz
**Requested by:** Captain (David Ortinau)
**Status:** Implemented, tested, and verified by Squad agents.

#### Decision

Vocab Quiz focus-word graduation now uses dedicated lifetime counters on `VocabularyProgress`: `QuizRecognitionDemonstrations` and `QuizProductionDemonstrations`. Correct `VocabularyQuiz` attempts increment the matching counter by input mode; wrong answers do not reset or decrement either counter. Non-quiz activity attempts do not affect these quiz-specific counters.

Focus vocabulary words that were already known at quiz-session load time (`IsKnown`, `CurrentStreak >= 3`, or `MasteryScore >= 0.50`) capture `UseKnownWordShortcut = true`, skip recognition, and rotate after one text/production confirmation in the current session. Capturing this baseline prevents a new or weak focus word from earning recognition turns in the current session and accidentally switching into the known-word shortcut; new/weak focus words still rotate only when persistent recognition and production demonstration counters both reach 3 and no pending recognition check remains.

The shortcut baseline is stored in `VocabQuizBatchItemSnapshot` so resume preserves whether the word was already-known at session start while still re-hydrating current `VocabularyProgress` counters from the database.

#### Migration note

The requested `dotnet ef migrations add AddQuizDemonstrationCounters --project src/SentenceStudio.Shared/SentenceStudio.Shared.csproj --startup-project src/SentenceStudio.Shared/SentenceStudio.Shared.csproj` command failed with the known multi-TFM `ResolvePackageAssets` metadata error, even when retried with `--framework net10.0`. Following the dual-provider migration skill, the PostgreSQL and SQLite migrations were created manually with inline `[DbContext(typeof(ApplicationDbContext))]` and `[Migration("20260703190310_AddQuizDemonstrationCounters")]` attributes, and both model snapshots were updated.

#### Test coverage

Jayne added persistent-demonstration regression coverage in `tests/SentenceStudio.UnitTests/Models/VocabQuizPersistentDemonstrationTests.cs` and `tests/SentenceStudio.UnitTests/Services/VocabQuizPersistentDemonstrationServiceTests.cs`, plus fixture updates in `RepairTaintedVocabularyProgressTests`. The 9 added cases cover snapshot round-trip, persistent recognition/production readiness, correct-attempt increments, wrong-answer non-reset behavior, non-quiz isolation, and known-word shortcut behavior.

#### Verification

- Migration attributes guard passed and confirmed discoverable `[Migration]` / `[DbContext]` attributes.
- Native SQLite and PostgreSQL both discovered and applied migration `20260703190310_AddQuizDemonstrationCounters`.
- Full unit suite passed: 764/764.
- WebApp Playwright E2E verified the label renders honestly as `Focus word — 0/3 recognitions before Text` and persistent recognition counters incremented 0 to 1 through the real recording path for both focus (`보내다`) and non-focus (`데이트`) words.
- macOS SQLite backup retained at `~/Library/sstudio.db3.pre-quizcounters-*.bak`.

#### Source notes merged

##### wash-persistent-demonstration-counters.md

# Wash persistent demonstration counters

Date: 2026-07-03
Author: Wash

## Decision

Vocab Quiz focus-word graduation now uses dedicated lifetime counters on `VocabularyProgress`: `QuizRecognitionDemonstrations` and `QuizProductionDemonstrations`. Correct `VocabularyQuiz` attempts increment the matching counter by input mode; wrong answers do not reset or decrement either counter. Non-quiz activity attempts do not affect these quiz-specific counters.

For focus vocabulary, words already known at quiz-session load time (`IsKnown`, `CurrentStreak >= 3`, or `MasteryScore >= 0.50`) capture `UseKnownWordShortcut = true`, skip recognition, and rotate after one text/production confirmation in the current session (`SessionTextCorrect >= 1`) with no pending recognition check. Capturing the shortcut baseline prevents a new/weak focus word from earning three recognition turns in the current session and accidentally switching into the known-word shortcut; new/weak focus words still rotate only when persistent recognition and production demonstration counters both reach 3 and no pending recognition check remains.

The shortcut baseline is stored in `VocabQuizBatchItemSnapshot` so resume preserves whether the word was already-known at session start while still re-hydrating the current `VocabularyProgress` counters from the database.

## Migration note

The requested `dotnet ef migrations add AddQuizDemonstrationCounters --project src/SentenceStudio.Shared/SentenceStudio.Shared.csproj --startup-project src/SentenceStudio.Shared/SentenceStudio.Shared.csproj` command failed with the known multi-TFM `ResolvePackageAssets` metadata error, even when retried with `--framework net10.0`. Following the dual-provider migration skill, the PostgreSQL and SQLite migrations were created manually with inline `[DbContext(typeof(ApplicationDbContext))]` and `[Migration("20260703190310_AddQuizDemonstrationCounters")]` attributes, and both model snapshots were updated.

## Validation

`bash scripts/validate-migration-attributes.sh` passed and confirmed all migrations carry discoverable `[Migration]` / `[DbContext]` attributes. Shared and WebApp builds passed with 0 errors. Targeted Vocab Quiz tests passed after adding regression coverage for persistent counters and focus-word rotation rules.

##### jayne-persistent-demonstration-tests.md

Jayne added 9 persistent-demonstration regression cases across `VocabQuizPersistentDemonstrationTests` and `VocabQuizPersistentDemonstrationServiceTests`, updated the `RepairTaintedVocabularyProgressTests` fixture, and reran the full unit suite successfully at 764/764. These tests lock the persistent counter model and service behavior so the session-local focus-word regression cannot recur.


---

### 2026-07-02 — ActivitySession enforces scoped in-progress uniqueness

**Session:** ActivitySession review fixes
**Surface:** Shared data layer, dual-provider migrations, Vocab Quiz WebApp integration
**Author:** Wash
**Status:** Implemented and validated before the Vocab Quiz resume ship.

#### Decision

ActivitySession now enforces one in-progress row per `(UserId, ActivityType, LaunchContextKey)` at the database boundary, not only in service code. The resumable lookup index `(UserId, ActivityType, Status)` is retained, and the former `(UserId, LaunchContextKey)` index is replaced by a unique filtered index on `(UserId, ActivityType, LaunchContextKey)` where `Status = 'InProgress'`.

`SaveSnapshotAsync` still cleans up older duplicate in-progress rows when seen. If a concurrent insert hits the unique index, it catches `DbUpdateException`, detaches the failed insert, re-queries the existing in-progress row for the same scoped context, and updates it with last-writer-wins semantics.

`CompleteAsync` is context-scoped and user-scoped: `CompleteAsync(string userId, string activityType, string launchContextKey)`. It marks all matching in-progress rows completed so stale duplicate rows cannot survive. `AbandonAsync` is `AbandonAsync(string userId, int sessionId)` and filters by user id before mutating.

#### Validation

- Shared, API, and WebApp builds completed with 0 errors.
- Targeted `ActivitySessionServiceTests`: 11/11 passed.
- Full unit test rerun: 751/751 passed after one unrelated transient failure passed on rerun.

---

### 2026-07-02 — RCA: dual-provider SQLite migrations must prove EF discovery

**Session:** Dual-provider migration recurrence RCA
**Surface:** EF Core migrations, SQLite mobile heads, PostgreSQL web/API
**Author:** Squad Coordinator for Captain
**Severity:** High; this class shipped to production devices twice.

#### Decision

Dual-provider migrations must prove that EF discovers and applies the SQLite migration, not just that the hand-written SQL parses. Every migration needs both provider copies unless intentionally provider-specific, and hand-written SQLite migrations must include inline `[DbContext(typeof(ApplicationDbContext))]` and `[Migration("<id>")]` attributes.

#### RCA

Two confirmed instances had SQLite copies that lacked discovery attributes: `20260702145959_AddActivitySession` and `20260503221947_AddRefreshTokenReplacedBy`. EF therefore never discovered those migrations on mobile, while PostgreSQL/web continued to work. The recurrence came from skill examples and repo guidance that under-described the hand-written SQLite half, tests that use `EnsureCreated()` and exclude SQLite migrations from compilation, and a mobile validation gate that false-passed when DevFlow attached to the wrong app.

Raw-DDL/schema-copy validation has a blind spot: it proves SQL is valid, not that EF invokes the migration. Missing attributes are invisible to that validation.

#### Defenses

- Added `[DbContext]` and `[Migration]` to both broken SQLite migrations.
- Added `scripts/validate-migration-attributes.sh` and CI `migration-guard` to fail missing discovery attributes.
- Updated runbook, AGENTS.md, Copilot instructions, and dual-provider migration skill guidance to require static guard plus real SQLite discovery/application verification.
- Follow-up remains for unpaired migration drift: `20260404185452_AddNarrativeJson` and `20260621193737_AddVocabularyDuplicateLookupIndexes` need confirmation for mobile relevance.

---

### 2026-07-03 — Vocab Quiz persistent counters hardened after deep review

**Session:** Vocab Quiz persistent-counter adversarial review and hardening
**Surface:** Shared vocabulary progress, duplicate merge/replay repair, Vocab Quiz focus rotation
**Authors:** Deep-review agents, Wash, Coordinator
**Status:** Critical findings fixed, full suite/builds passed, merged to main at `4da42e87` and pushed.

#### Decision

Captain ruled that a regressed focus word should be re-drilled before leaving the focus batch. A non-known focus word with banked lifetime quiz demonstrations may keep cumulative mode graduation, but it must earn production progress in the current session before rotating out.

The hardened rotation rule keeps cumulative recognition graduation while adding a session floor for non-known focus words. `RecognitionDemonstrationsBaseline` and `ProductionDemonstrationsBaseline` are captured at session start and round-tripped through `VocabQuizBatchItemSnapshot`. `ReadyToRotateOut` for non-known focus words now requires `SessionTextCorrect >= Math.Max(1, 3 - ProductionDemonstrationsBaseline)`, so a regressed banked-3/3 word must earn at least one in-session production confirmation and cannot be evicted unseen. Known-word shortcut behavior remains cumulative plus one in-session production confirmation.

Duplicate vocabulary merge reconciliation treats `QuizRecognitionDemonstrations` and `QuizProductionDemonstrations` as monotonic lifetime history. Merges preserve the summed source counters with a `Math.Max` anti-regression floor, and replay recalculation starts from zero then reconstructs counters from correct `VocabularyQuiz` contexts so repair does not wipe quiz history.

Quiz counter increments now live in `VocabularyProgressService.UpdateQuizDemonstrationCounters`, not in obsolete `UpdateLegacyFields`, so the live service path owns the behavior explicitly.

#### Deep-review findings fixed

- DR-001 High: rotate-on-entry with zero in-session practice could evict a focus word with banked >=3/>=3 counters before showing it. Fixed with the session-floor baseline and snapshot persistence described above.
- DR-002 High/Medium: duplicate merge could drop new quiz counters. Fixed with merge reconciliation that preserves summed counter floors and repair replay that rebuilds counters from quiz attempts.
- DR-003 Medium: counter increment was buried in obsolete `UpdateLegacyFields`. Fixed by moving increment responsibility to the non-obsolete quiz-counter update method.

#### Verification

- Independent full unit suite: 767/767 passed.
- Shared and WebApp builds completed cleanly.
- Coordinator merged and pushed `main` from `63bf7ddc` to `4da42e87`.

---

### 2026-07-05 — Transcript example sentence capture and retroactive harvest

**Session:** Capture example sentences from transcripts at import time plus retroactive Settings utility
**Surface:** Shared data/import services, YouTube pipeline, Blazor Settings page, unit + WebApp E2E validation
**Requested by:** Captain (David Ortinau)
**Status:** Implemented and verified by Squad agents; pending Captain review/commit.

#### Decision

Example sentences from reading/transcript resources are now captured through one centralized FromReading path. Import-time capture persists AI-returned `ExampleSentence`/translation values from YouTube and content import flows instead of dropping them during `ToVocabularyWord` mapping. Retroactive capture is handled by `TranscriptSentenceHarvestService`, which deterministically segments transcript-backed resources for the active user, matches vocabulary by term/lemma/light Korean dictionary-form stem, batch-translates candidates through the existing AI service, and persists through the same repository helper.

All FromReading examples are stored as `Source=FromReading`, `Status=Curated`, never core, resource-linked where available, deduplicated on vocabulary word plus normalized target sentence, and capped at two FromReading examples per word/resource. Empty user identifiers fail closed, and resource scans are scoped to the active user's resources. No migration was added because the existing `ExampleSentence` schema already supports the feature.

#### Segmenter ruling

`TranscriptSentenceSegmenter.Split` is now the canonical sentence splitter. The default `splitOnNewlines: false` preserves Reading sentence-index behavior by collapsing newlines before punctuation splitting. Transcript harvesting opts into `splitOnNewlines: true` so caption-style punctuation-less lines become boundaries without changing Reading callers.

#### Settings utility

Settings > Data Management exposes a localized "Harvest example sentences from my resources" action. It resolves the active user via `IPreferencesService` key `active_profile_id`, which works for both MAUI preferences and WebApp claim/circuit-backed preferences.

#### Validation

Jayne added repository, segmenter, harvest service, and import-capture tests. Coordinator E2E verified the WebApp Settings harvest through Aspire + Playwright: FromReading examples grew from 0 to 97 with AI translations, rows were Curated and resource-linked, rerun stayed idempotent at 97 with no duplicates, the cap held, and multi-tenant scoping left other users and other sources untouched. Full unit suite passed: 781/781.

#### Source notes merged

##### wash-example-sentence-harvest-foundation.md

# Example sentence harvest foundation

Date: 2026-07-05
Author: Wash
Surface: Shared data layer and transcript services

## Decision

`ExampleSentenceRepository` now has `CreateFromReadingIfNewAsync` helpers for transcript/reading harvest. The helpers store new examples as `Source=FromReading`, never mark them core, validate that the vocabulary term or lemma appears in the target sentence, deduplicate by normalized target sentence across all sources for the same word, and cap harvested reading examples per word/resource.

Transcript sentence splitting now has one canonical utility: `TranscriptSentenceSegmenter.Split` in `src/SentenceStudio.Shared/Services/`. `SentenceTimingCalculator.SplitIntoSentences` delegates to it for compatibility, and `TranscriptSentenceExtractor` calls the shared utility directly.

## Rationale

The next wave needs safe backfill/import code that can call into repository logic without creating duplicate examples or overfilling one resource. Keeping segmentation centralized avoids drift between timed transcript playback and transcript sentence extraction.

## Scope

No schema/model changes and no migrations were added.

##### wash-example-sentence-import-and-backfill.md

# Wash example sentence import and backfill

Date: 2026-07-05
Author: Wash
Surface: Shared import services, YouTube import pipeline, transcript harvest backfill

## Decision

YouTube and content import now carry AI-provided source sentences from extraction through persistence. YouTube keeps each `ExtractedVocabularyItem` beside its `VocabularyWord` until the resource, word, and mapping rows are saved; content import stores `SourceSentence` and `SourceSentenceTranslation` on preview rows and commits them after each row resolves to the mapped word. Both paths persist through `ExampleSentenceRepository.CreateFromReadingIfNewAsync`, so normalization, word-in-sentence validation, deduplication, FromReading source stamping, non-core status, and the per-word/resource cap stay centralized.

A new `ITranscriptSentenceHarvestService` provides retroactive transcript harvesting for a user. It scans only that user's transcript-backed resources, maps resource vocabulary, segments transcripts with `TranscriptSentenceSegmenter`, finds up-to-two candidate sentences per word using target term, lemma, and a light Korean `-다` stem fallback, batch-translates candidate sentences through the existing `IAiService.SendPrompt<BulkTranslationResponse>` path, then persists through the same repository helper.

## Transaction and data-safety notes

YouTube resource creation now uses a database transaction around resource/word/mapping creation and example insertion. Content import wraps its existing resource/word/mapping save and example insertion in a transaction as well. The backfill service fails closed on empty `userId`, scopes resource reads to `LearningResource.UserProfileId == userId`, and only examines mappings for those owned resources.

## Scope

No EF schema changes, model snapshot edits, or migrations were added. `ExampleSentenceRepository` validation now also accepts a Korean dictionary-form lemma stem when `Lemma` ends with `다`, matching the backfill matching rule while preserving centralized validation.

##### wash-segmenter-newline-mode.md

# Wash segmenter newline mode

Date: 2026-07-05
Author: Wash

## Decision

`TranscriptSentenceSegmenter.Split` keeps its default Reading-safe behavior and adds an opt-in `splitOnNewlines` parameter for transcript harvesting.

## Rationale

Reading sentence/audio index mapping depends on the existing default behavior that collapses `\r\n`, `\n`, and `\r` to spaces before punctuation-based splitting. Changing the default would desync `SentenceTimingCalculator.SplitIntoSentences`, which delegates to the segmenter and must match Reading sentence indices.

Transcript harvesting has a different data shape: line-per-caption transcripts can omit terminal punctuation. In that mode, newline runs should be hard sentence boundaries so captions do not collapse into one oversized candidate sentence.

## Scope

- Default `Split(transcript)` output is preserved for Reading and existing callers.
- `Split(transcript, splitOnNewlines: true)` first splits on newline runs, then applies the existing punctuation-based splitter to each non-empty line and concatenates the results.
- `TranscriptSentenceHarvestService` opts into newline-aware segmentation.

##### kaylee-settings-harvest-utility.md

# Kaylee settings harvest utility

Date: 2026-07-05
Surface: Blazor shared Settings page

## Decision

The Settings harvest utility resolves the active user through the injected `SentenceStudio.Abstractions.IPreferencesService` with the `active_profile_id` key, matching the established Blazor UI pattern used by pages such as `VocabQuiz.razor`.

## Rationale

`MauiPreferencesService` reads the active profile id from device preferences on MAUI heads. `WebPreferencesService` special-cases `active_profile_id` and resolves it from authenticated HTTP claims during SSR or `CircuitUserStateAccessor` during the InteractiveServer circuit, so the same UI code works on both WebApp and MAUI without hardcoding a user id.

##### jayne-example-sentence-harvest-tests.md

# Jayne example sentence harvest tests

Date: 2026-07-05
Author: Jayne
Surface: Shared example sentence repository, transcript segmentation, transcript harvest, content import capture

## Decision

Added focused unit coverage for the new FromReading example sentence capture path:

- `ExampleSentenceRepository.CreateFromReadingIfNewAsync` covers valid inserts, blank rejection, no-term rejection, normalized deduplication across sources, per-resource cap behavior, batch overload checks against preloaded examples, and forced non-core creation.
- `TranscriptSentenceSegmenter.Split` covers Korean/ASCII sentence-final punctuation and newline segmentation.
- `TranscriptSentenceHarvestService.HarvestForUserAsync` covers scoped user harvesting, AI translation population through a fake `IAiService`, idempotency, no-match counting, empty-user guard, user isolation, and per-word/resource cap behavior.
- `ContentImportService.CommitImportAsync` covers committing a row with `SourceSentence`/`SourceSentenceTranslation` into a persisted FromReading example.

## Follow-up after Wash segmenter fix

Wash fixed `TranscriptSentenceSegmenter.Split` by adding optional newline-boundary behavior while preserving the default Reading-safe behavior.

Jayne updated `TranscriptSentenceSegmenterTests` to cover both modes:

- Default `Split(text)` / `Split(text, splitOnNewlines: false)` collapses newline boundaries to spaces and does not split punctuation-less newline-separated text.
- `Split(text, splitOnNewlines: true)` treats newline runs as hard boundaries, drops empty segments, and still splits sentence-final punctuation within a line.
- Existing ASCII/Korean punctuation coverage remains in place for `.`, `?`, `!`, `。`, `！`, `？`.

Jayne also updated `TranscriptSentenceHarvestServiceTests` so the seeded transcript relies on newline-mode harvesting: one punctuation-less line, one line with punctuation splitting inside it, and one no-match line. The test now asserts the exact harvested sentences:

- `저는 학교에 가요.`
- `내일도 학교에 가고 싶어요!`
- `친구와 같이 가서 책을 읽어요.`

## Final validation

Targeted segmenter + harvest subset passed.

Full unit suite command:

```bash
dotnet test tests/SentenceStudio.UnitTests/SentenceStudio.UnitTests.csproj
```

Result: 781 passed, 0 failed, 0 skipped, 781 total.

Known-baseline deterministic plan builder failure: not observed in this run.

Remaining real failures: none.
