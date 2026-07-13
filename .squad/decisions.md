## Active Decisions

(Most recent decisions below. Archived decisions in `decisions/archive/scribe-archive-2026-07-12.md` and earlier archives.)

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

---

### 2026-07-11 — Vocabulary Add preselects no-results search term and profile language

**Session:** Vocabulary no-results Add prefill
**Surface:** Blazor WebApp Vocabulary pages
**Requested by:** Captain (David Ortinau)
**Status:** Implemented and E2E verified by Kaylee; pending Captain review/commit.

#### Decision

When a Vocabulary search has no results and the parsed query contains free-text terms, the Add flow carries the searched term into the Add Vocabulary page through an explicit `initialTargetTerm` query parameter. The new-word form prefills the target-language term from that value and preselects `wordLanguage` from the active user profile `TargetLanguage`, with Korean as a fallback.

The behavior is gated to the no-results Add path only: existing Edit remains unchanged, and empty-search Add continues to open a blank form.

#### Rationale

This matches the learner flow of searching for a missing word and immediately adding it, while avoiding accidental prefill from raw search/filter syntax or from edits of existing vocabulary. Using the active profile target language keeps the new entry aligned with the learner's current language context.

#### Validation

Kaylee validated the WebApp through Aspire E2E with the `squad-jayne@sentencestudio.test` account: searching `zzzqqqxyz` produced no results, Add opened `/vocabulary/edit/0?...initialTargetTerm=zzzqqqxyz` with the target term prefilled and Language set to Korean; empty-search Add opened blank; existing Edit was unaffected. Build validation: `dotnet build` of `SentenceStudio.UI` passed.

#### Source notes merged

##### kaylee-vocab-add-prefill.md

# Vocabulary Add Prefill

- Author: Kaylee
- Date: 2026-07-11

## Decision

When launching Add Vocabulary from the Vocabulary list, carry over free-text search terms only for the no-results scenario: `filteredWords.Count == 0 && parsedQuery?.FreeTextTerms.Count > 0`.

## Rationale

This matches the primary user flow where a learner searches for a missing term, sees no matches, then taps Add. It avoids changing Edit behavior and avoids attaching raw search/filter syntax such as `tag:foo` or `language:Korean` to the new word form. The edit URL receives an explicit `initialTargetTerm` query parameter only from the Add path.

---

### 2026-07-12 — Vocab quiz fuzzy grading tightened: length-gated Levenshtein bypass prevents short-word collisions

**Author:** River (AI/Prompt Engineer)
**Requested by:** Captain (David Ortinau)
**Status:** Implemented + E2E verified

#### Decision

Added `MIN_LENGTH_FOR_DISTANCE_BYPASS = 5` to `FuzzyMatcher.EvaluateSingle`. The acceptance condition is now:

```
similarity >= 0.75 (length-relative, always active)
OR (distance <= 2 AND maxLength >= 5) (absolute distance bypass only for 5+ char words)
```

**Effect:** Words 1–4 chars must pass the 75% similarity threshold (prevents "day"→"buy", "big"→"bag" false accepts). Words 5+ chars retain the lenient distance bypass (valid for real typos like "fone"→"phone").

**No phonetic algorithm added:** Levenshtein already handles close phonetic typos on 5+ char words; far rewrites (nite→night) are intentionally rejected to enforce correct spelling in a learning app. Korean would need a separate algorithm — disproportionate complexity.

**Files:** `src/SentenceStudio.Shared/Services/FuzzyMatcher.cs`, `tests/SentenceStudio.UnitTests/Services/FuzzyAnswerMatcherTests.cs` (20 new adversarial tests, 140/140 pass).

---

### 2026-07-12 — E2E verification: FuzzyMatcher length-gate fix confirmed on live Blazor WebApp

**Author:** Jayne (Tester)
**Surface:** Blazor WebApp via Aspire + Playwright
**Status:** PASS

#### Verification Results

| Assertion | Input | Expected | Result |
|-----------|-------|----------|--------|
| Wrong short answer REJECTED | "lark" for 링크/link (4ch) | Reject | PASS — similarity 0.50 < 0.75, bypass blocked |
| Typo on long word ACCEPTED | "to wirte code" for 코드를 짜다 (13ch) | Accept | PASS — distance=2, length>=5, bypass active |
| Exact answer ACCEPTED | "code" for 코드 | Accept | PASS |

Final quiz result: 2/3 Correct. No regressions. DB restored after testing.
