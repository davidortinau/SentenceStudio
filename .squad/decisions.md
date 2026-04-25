## Active Decisions

(Most recent decisions below. Archived decisions in `decisions-archive-2026-04-25.md`)

---


# Phrase/Sentence Import Gap — Import Content MVP

**Date:** 2026-04-25  
**Reporter:** Jayne (Tester)  
**Branch:** `feature/import-content-mvp` (commit 04053f2)  
**Status:** ⚠️ GAP IDENTIFIED — DECISION REQUIRED

## Summary

The Import Content MVP **cannot handle paired-line phrase/sentence format** (alternating target language / native language on adjacent lines). The AI free-text fallback triggers but splits sentences into individual vocabulary words instead of preserving full phrases.

**Comma-delimited format works** as a workaround, but users must know to manually add commas between phrase pairs, and phrases get stored in the `VocabularyWord` table (semantically misleading but functionally usable).

## Test Results

### Variant 1: Paired Lines, No Delimiter ❌ BROKEN

**Input:**
```
마고는 눈하고 귀가 안 좋아요. 잘 못 보고, 잘 못 들어요.
Margo's eyes and ears are not good. (She) can't see well and can't hear well.
이 강아지는 갈색이에요. 다리가 짧아요.
This dog is brown. (Its) legs are short.
시간이 없어요. 빨리 가야 해요.
(I) don't have time. (I) need to go quickly.
```

**Parser:** Free-text AI (detected as "Free-form text (AI-extracted)")  
**Preview:** 14 individual words (눈/eye, 귀/ear, 좋다/to be good, 잘/well, 보다/to see, 듣다/to hear, etc.)  
**Commit:** 10 created, 4 skipped (dedup)  
**Database:** 14 `VocabularyWord` rows, each a single word with AI translation  

**Problems:**
- User pastes **phrases**, gets **individual words** instead
- No warning that "Vocabulary" mode doesn't support phrases
- AI badge correctly applied, but entire output is wrong
- Full sentences lost — only individual vocabulary extracted

**Verdict:** ❌ **BROKEN** — The Captain's Margo example does NOT work in MVP.

---

### Variant 2: Comma-Delimited Paired Sentences ✅ WORKS (WITH CAVEATS)

**Input:**
```
마고는 눈하고 귀가 안 좋아요. 잘 못 보고 잘 못 들어요.,Margo's eyes and ears are not good. She can't see or hear well.
이 강아지는 갈색이에요. 다리가 짧아요.,This dog is brown. Its legs are short.
시간이 없어요. 빨리 가야 해요.,I don't have time. I need to go quickly.
```

**Parser:** CSV (detected as "Comma-delimited (CSV)")  
**Preview:** 3 phrase pairs, full sentences preserved  
**Commit:** 3 created, 0 skipped  
**Database:** 3 `VocabularyWord` rows with full sentences in `TargetLanguageTerm` and `NativeLanguageTerm`

**Sample DB Row:**
```
TargetLanguageTerm: 마고는 눈하고 귀가 안 좋아요. 잘 못 보고 잘 못 들어요.
NativeLanguageTerm: Margo's eyes and ears are not good. She can't see or hear well.
```

**Problems:**
- User must know to add commas (not intuitive for paired-line phrase format)
- Still stored in `VocabularyWord` table (semantically wrong — these are sentences, not words)
- No "Phrases" content type selectable in dropdown (marked `[v2]`)
- Embedded commas in Korean text (e.g., "잘 못 보고, 잘 못 들어요") don't break CSV parsing (good)

**Verdict:** ✅ **WORKS** as workaround, but requires manual delimiter addition and stores phrases as "words"

---

## Root Cause

1. **Content Type dropdown** shows only "Vocabulary" enabled; "Phrases" disabled
2. **AI free-text fallback** (`ParseFreeTextContentAsync`) extracts individual vocabulary words, not full sentences
3. **No phrase-aware parser** exists yet — CSV path treats each row as a single "word" (which happens to contain full sentences when comma-delimited)
4. **Table schema** uses `VocabularyWord` for everything — no separate `Phrase` table (yet?)

## Recommendations

### Option 1: Enable Phrases Mode Now (Recommended)

**Effort:** Small (1-2 hours)  
**Impact:** Fixes P0 use case (Captain's Margo example)  

**Changes:**
1. Enable "Phrases" option in Content Type dropdown
2. Add phrase-specific validation: enforce 1 delimiter per line, warn on malformed input
3. Route to CSV parser (skip AI fallback)
4. Store in `VocabularyWord` table for now (schema migration deferred to post-MVP)

**Pros:**
- Unblocks paired-line phrase import (Captain's original ask)
- Clear UX: user selects "Phrases" → knows to format as paired lines or CSV
- Minimal code change (UI + routing logic)

**Cons:**
- Still stores in `VocabularyWord` table (misleading name, but functionally usable)
- Doesn't address AI fallback behavior (low priority if Phrases mode exists)

---

### Option 2: Document the Gap and Ship

**Effort:** Trivial (add warning to docs)  
**Impact:** MVP ships with limitation; users must use comma-delimited workaround

**Changes:**
1. Add help text to Import wizard: "For phrases/sentences, use comma-delimited format: `한국어 문장,English sentence`"
2. Document in user guide: "Phrase import requires comma delimiter; paired-line format not supported in MVP"
3. Note AI fallback limitation: "Free-text mode extracts individual words only"

**Pros:**
- Zero code change
- Ships MVP on schedule
- Workaround (Variant 2) already verified working

**Cons:**
- Captain's Margo example (Variant 1) doesn't work
- Poor UX — users must manually add commas
- Phrases stored as "VocabularyWord" entries (semantically wrong)

---

### Option 3: Fix AI Free-Text Path (Deferred)

**Effort:** Large (AI prompt engineering + testing)  
**Impact:** Makes Variant 1 work without commas

**Changes:**
1. Update `ParseFreeTextContentAsync` prompt to detect sentence structure
2. Preserve full sentences instead of extracting individual words
3. Add phrase-vs-vocabulary classification logic
4. Test with various phrase formats (paired lines, paragraphs, etc.)

**Pros:**
- Makes Captain's original Margo example (Variant 1) work without manual commas
- Better UX — AI infers structure automatically

**Cons:**
- Large effort (prompt engineering is unpredictable)
- AI output reliability uncertain (may still produce garbage for edge cases)
- Not P0 if Option 1 or 2 ships first

**Recommendation:** Defer to v2 or post-MVP

---

## Schema Question (Deferred)

**Current:** All content stored in `VocabularyWord` table  
**Question:** Should phrases/sentences live in separate `Phrase` table?  

**Arguments FOR separate table:**
- Clearer semantics (VocabularyWord = single words, Phrase = multi-word units)
- Enables phrase-specific features (constituent tracking, grammar rules, etc.)
- Better data integrity (constraints on word length, tokenization, etc.)

**Arguments AGAINST:**
- Adds complexity (two tables to query, join logic, duplication risk)
- Current unified table works functionally (LexicalUnitType enum already distinguishes Word vs Phrase)
- Migration effort non-trivial (backfill existing data, update all queries)

**Recommendation:** Keep unified table for MVP, revisit post-launch when phrase feature set is clearer

---

## Captain's Decision Required

**Question:** Which option for MVP merge?

1. **Enable Phrases mode now** (1-2 hours, unblocks Margo example)
2. **Document gap and ship** (zero code change, Variant 2 workaround documented)
3. **Defer phrases to v2** (ship Vocabulary-only MVP)

**Jayne's Recommendation:** Option 1 (enable Phrases mode) — minimal effort, high user value, no schema change required

---

## Evidence

**Screenshots:**
- `phrase-test-variant1-preview-result.png` — Shows AI extraction of 14 individual words from 3 phrase pairs
- `phrase-test-variant2-preview.png` — Shows CSV parser preserving 3 full phrase pairs

**Database Queries:**
```sql
-- Variant 1: Individual words extracted by AI
SELECT "TargetLanguageTerm", "NativeLanguageTerm" FROM "VocabularyWord" vw
JOIN "ResourceVocabularyMapping" rvm ON rvm."VocabularyWordId" = vw."Id"
JOIN "LearningResource" lr ON rvm."ResourceId" = lr."Id"
WHERE lr."Title" = 'Phrase Import Probe - Variant 1';
-- Result: 14 rows (눈/eye, 귀/ear, 좋다/to be good, etc.)

-- Variant 2: Full phrases preserved by CSV
SELECT "TargetLanguageTerm", "NativeLanguageTerm" FROM "VocabularyWord" vw
JOIN "ResourceVocabularyMapping" rvm ON rvm."VocabularyWordId" = vw."Id"
JOIN "LearningResource" lr ON rvm."ResourceId" = lr."Id"
WHERE lr."Title" = 'Phrase Import Probe - Variant 2';
-- Result: 3 rows (full sentences in both columns)
```

**Aspire Logs:**
- Variant 1: "Detected Free-form text (AI-extracted), 14 rows"
- Variant 2: "Detected Comma-delimited (CSV), 3 rows"

---

## Next Steps

1. **Captain reviews** this report
2. **Captain decides** Option 1, 2, or 3
3. If Option 1: Kaylee implements Phrases mode (small task)
4. If Option 2: Scribe documents limitation in user guide
5. If Option 3: Squad closes this issue, reopens post-MVP

**Reported by:** Jayne (Tester)  
**Date:** 2026-04-25  
**Branch:** `feature/import-content-mvp` (commit 04053f2)

---


### 2026-04-25: Jayne — v1.1 Import Test Matrix Authored

**By:** Jayne (Tester) via Squad  
**What:** Test matrix for v1.1 import features authored and ready to execute once Wash + Kaylee complete implementation.

## Matrix Summary

| ID | Scenario | Priority | Covers |
|----|----------|----------|--------|
| A | Vocabulary CSV regression | P0 | v1.0 still works, LexicalUnitType=1 |
| B | Phrases import (Korean) | P0 | Words + Phrases created, Captain's Margo example |
| C | Transcript import (prose) | P0 | LearningResource.Transcript populated, Words primarily |
| D | Auto-detect high confidence | P1 | >=0.85 auto-routes, banner + [Change] |
| E | Auto-detect medium confidence | P1 | 0.70-0.84 forces confirmation, no premature DB writes |
| F | Auto-detect low confidence | P1 | <0.70 shows manual picker, no auto-routing |
| G | Checkbox zero-checked | P1 | Validation blocks advance |
| H | Checkbox override multiple | P1 | Transcript + Phrases + Words all honored |
| I | Confidence gate pollution | P0 | Cancel produces ZERO new DB rows |
| J | Backfill migration | P0 | Space heuristic, zero Unknown remaining |

**Edge cases (7):** Empty input, >30KB, Korean-only, mixed language, zero extraction, duplicate import, special characters.

**Fixtures (5):** phrase-list-korean.txt, transcript-korean.txt, vocab-csv.csv, ambiguous-blob.txt, low-confidence-noise.txt.

## Gaps Flagged

1. **Zero-vocab extraction behavior undefined.** Captain's confirms-d2 notes this is an open sub-question ("rollback the resource, or keep and warn"). Wash must decide and document; I'll update Edge 5 accordingly.

2. **>30KB handling unclear.** Zoe deferred chunking to v1.2; v1.1 "currently rejects." Wash needs to implement the rejection message. If chunking lands in v1.1 instead, Edge 2 must be rewritten.

3. **Auto-detect confidence thresholds are classifier-dependent.** The fixtures I authored (ambiguous-blob.txt, low-confidence-noise.txt) are designed to hit medium/low bands, but actual confidence scores depend on River's classifier prompt. If the classifier returns unexpected confidence for these inputs, fixtures may need tuning.

4. **Checkbox UI exact rendering unknown.** Kaylee's Blazor implementation will determine exact element selectors, CSS classes, and interaction patterns. Playwright steps will need selector updates once the UI lands.

5. **Transcript fixture size.** The transcript-korean.txt fixture is ~440 bytes — well under the 30KB limit. This is intentional for Scenario C. The >30KB test (Edge 2) will need a generated blob at runtime.

## Status

All 10 scenarios + 7 edge cases + 5 fixtures: **AUTHORED, NOT YET RUN.**  
Execution blocked on Wash (backend) + Kaylee (UI) completing v1.1 implementation.

---


# Kaylee v1.1 UI — Import Content Harvest Checkboxes + Auto-detect Banner

**Date:** 2026-04-25  
**Author:** Kaylee (Full-stack Dev)  
**Branch:** `feature/import-content-mvp`  
**Status:** Implemented, awaiting Wash backend integration + Jayne e2e

---

## Changes Made

### 1. Removed v2 disabled state
Lines 104-107 of the old ImportContent.razor had Phrases, Transcript, and Auto-detect options disabled with `<span class="badge bg-secondary ms-1">v2</span>`. All three are now fully enabled in the content type dropdown.

### 2. Harvest checkbox step (Captain's directive)
Replaced the implicit single-pick content-type-determines-harvest model with three independent checkboxes:

- **This is a Transcript** — stores full text on the learning resource
- **Harvest Phrases** — extracts phrase-level entries (LexicalUnitType=Phrase)
- **Harvest Words** — extracts individual vocabulary words (LexicalUnitType=Word)

**Validation:** At least one checkbox must be checked. Inline `alert-danger` displayed if user attempts to commit with all unchecked. Also validated via toast on commit attempt.

**Default presets by scenario:**

| User picks / Auto-detects | Transcript | Phrases | Words |
|---|---|---|---|
| Vocabulary | off | off | ON |
| Phrases | off | ON | ON |
| Transcript | ON | off | ON |
| Auto-detect | set from classifier result | | |

User can override any combination after defaults are applied.

### 3. Auto-detect confidence banner (D3)
Added a three-tier confidence gate that runs BEFORE any DB persistence:

- **High (>=85%):** `alert-info` banner with `bi-stars` icon showing detected type + percentage. [Change] button opens type chooser overlay. Preview runs immediately.
- **Medium (70-84%):** `alert-warning` banner asking user to confirm or pick a different type. Preview is gated — won't run until user confirms or overrides.
- **Low (<70%):** `alert-secondary` banner with "Couldn't auto-detect" message. Three manual type buttons (Vocabulary/Phrases/Transcript). Classifier hint shown as soft suggestion. Preview is gated.

The [Change] action re-opens a type picker card with the current detection pre-selected via highlighted button state.

### 4. Display polish
- All Bootstrap classes (`form-check`, `btn`, `alert`, `badge`)
- Confidence displayed as percentage (industry-standard, Captain didn't specify otherwise)
- No emojis anywhere — `bi-stars`, `bi-pencil`, `bi-check-lg`, `bi-question-circle`, `bi-exclamation-circle`, `bi-exclamation-triangle-fill` only
- Clear, friendly copy throughout

### 5. Backend contract assumptions (for Wash)
Added three boolean fields to `ContentImportCommit` DTO:

```csharp
public bool HarvestTranscript { get; set; }
public bool HarvestPhrases { get; set; }
public bool HarvestWords { get; set; } = true;  // default: always harvest words
```

**Wash integration notes:**
- `CommitImportAsync` should read these three booleans to determine what to persist
- When `HarvestTranscript=true`, store `rawText` in `LearningResource.Transcript` and set `MediaType="Transcript"`
- When `HarvestPhrases=true`, create VocabularyWord rows with `LexicalUnitType=Phrase`
- When `HarvestWords=true`, create VocabularyWord rows with `LexicalUnitType=Word`
- The existing `DetectContentType()` method returns `ContentTypeDetectionResult` with `ContentType`, `Confidence`, `Note` — unchanged

### 6. ImportStep enum change
Added `Harvest` step between `Source` and `Preview`:
```csharp
enum ImportStep { Source, Harvest, Preview, Commit, Complete }
```

---

## Known limitations
- The `DetectContentType()` method is currently a stub that always returns Vocabulary with 1.0 confidence. River's classifier prompt needs to be wired in for the auto-detect path to be truly functional.
- Pre-existing build errors from missing `ContentClassificationResult` type (River/Wash parallel work) prevent a full build. My Razor file compiles clean — no Razor-specific errors.
- Harvest checkbox labels are hardcoded English. Localization keys should be added in a follow-up pass.

---


### 2026-04-25: River — v1.1 prompt deliverables

**By:** River (AI/Prompt Engineer) via Squad
**Scope:** Data Import v1.1 prompt work — three prompts authored/revised per Captain's directives.

---

#### 1. ClassifyImportContent.scriban-txt (NEW)

**File:** `src/SentenceStudio.AppLib/Resources/Raw/ClassifyImportContent.scriban-txt`

**Template variables:** `{{ content }}`, `{{ format_hint }}` (optional)

**Response DTO needed:** A new `ImportContentClassificationResponse` with fields:
- `type` (string: "Vocabulary" | "Phrases" | "Transcript")
- `confidence` (float: 0.0-1.0)
- `reasoning` (string)
- `signals` (string array)

**Continuity heuristic (Captain's directive):** Given HIGHEST WEIGHT in the classification procedure. The prompt instructs the LLM to read 5-10 consecutive lines and determine whether they form flowing narrative (Transcript) or stand alone with no shared referents (Phrases). This is step 3 of 5 in the procedure, but explicitly labeled as highest weight. The few-shot examples demonstrate the heuristic in action — the Phrases example shows topic shifts between lines, the Transcript example traces anaphoric references across sentences.

**Confidence calibration:**
- >= 0.85: clear single type (auto-proceed)
- 0.70-0.84: borderline (show confirmation UI)
- < 0.70: ambiguous (manual selection fallback)

**Few-shot examples included:**
1. Korean vocabulary (tab-delimited CSV shape) → Vocabulary, 0.95
2. Korean phrase list (Captain's Margo example + others) → Phrases, 0.88
3. Korean transcript (prose about Korean food/kimchi) → Transcript, 0.93

---

#### 2. ExtractVocabularyFromTranscript.scriban-txt (REVISED)

**File:** `src/SentenceStudio.AppLib/Resources/Raw/ExtractVocabularyFromTranscript.scriban-txt`

**Changes:**
- **Word-biased extraction** per Captain's harvest model: prompt now says "aim for 90%+ Word-type entries" and marks Phrase as "RARE — only for genuinely fixed multi-word expressions."
- **Dropped Sentence type** from transcript extraction — the response format now only accepts "Word | Phrase" (not "Word | Phrase | Sentence"). Transcripts harvest words, not sentences.
- Removed the old Sentence classification section entirely. The LexicalUnitType=Sentence concept still exists in the enum and in FreeTextToVocab, but transcript extraction no longer produces them.
- **Generalized system role** — changed hardcoded "Korean" to `{{ target_language }}` for language-agnostic use.
- Common verb-object pairs (비가 오다, 시간이 없다) are now explicitly excluded from Phrase classification — extract verb and noun as separate Words instead.
- Added final IMPORTANT reminder: "Word-bias reminder: aim for 90%+ Word entries."

**Reachability from generic pipeline:** The template uses `{{ transcript }}`, `{{ video_title }}`, `{{ channel_name }}` variables. The YouTube-specific variables (`video_title`, `channel_name`) are already wrapped in `{{ if }}` guards, so they gracefully degrade to empty when called from the generic ContentImportService pipeline. **No plumbing change needed** — Wash can call this template from ContentImportService by passing `transcript = content` and leaving `video_title`/`channel_name` null. The existing `_fileSystem.OpenAppPackageFileAsync("ExtractVocabularyFromTranscript.scriban-txt")` pattern works identically.

---

#### 3. ExtractVocabularyFromPhrases.scriban-txt (NEW)

**File:** `src/SentenceStudio.AppLib/Resources/Raw/ExtractVocabularyFromPhrases.scriban-txt`

**Template variables:** `{{ source_text }}`, `{{ target_language }}`, `{{ native_language }}`, `{{ existing_terms }}` (optional), `{{ topik_level }}` (optional)

**Response DTO:** Can reuse `FreeTextVocabularyExtractionResponse` — same shape (vocabulary array with confidence, notes, partOfSpeech, lexicalUnitType, relatedTerms). No new DTO needed.

**Extraction strategy:** Produces BOTH Word and Phrase entries per Captain's directive:
- Phrase entries: core expressions normalized to dictionary form, with relatedTerms populated
- Word entries: individual content words extracted from the phrases
- Deduplication across lines, but a word AND a phrase containing it both appear (they serve different learning purposes)

**Captain's test case handled:** "마고는 눈하고 귀가 안 좋아요. 잘 못 보고, 잘 못 들어요." — the worked example in the prompt explicitly demonstrates extracting "눈이 안 좋다", "귀가 안 좋다", "잘 못 보다", "잘 못 듣다" as phrases, plus "눈", "귀", "보다", "듣다", "좋다" as individual words.

---

#### 4. AiService pipeline reachability (read-only consult)

**AiService.SendPrompt<T>()** is fully generic — it takes a rendered string prompt and deserializes into any DTO. No new methods needed.

**What Wash needs to plumb:**

1. **ClassifyImportContent:** Add a method in ContentImportService (or upgrade `DetectContentType`) that:
   - Loads `ClassifyImportContent.scriban-txt`
   - Renders with `{ content, format_hint }`
   - Calls `SendPrompt<ImportContentClassificationResponse>(prompt)`
   - A new `ImportContentClassificationResponse` DTO is needed (type, confidence, reasoning, signals)

2. **ExtractVocabularyFromPhrases:** Add a method (parallel to `ParseFreeTextContentAsync`) that:
   - Loads `ExtractVocabularyFromPhrases.scriban-txt`
   - Renders with `{ source_text, target_language, native_language, existing_terms, topik_level }`
   - Calls `SendPrompt<FreeTextVocabularyExtractionResponse>(prompt)` — reuses existing DTO
   - Remove the `NotSupportedException("Phrase import is not yet supported")` guard in ParseContentAsync

3. **ExtractVocabularyFromTranscript (generic path):** Add a transcript parsing method that:
   - Loads `ExtractVocabularyFromTranscript.scriban-txt` (same template used by VideoImportPipelineService)
   - Passes `transcript = content`, `video_title = null`, `channel_name = null`
   - Calls `SendPrompt<VocabularyExtractionResponse>(prompt)`
   - Remove the `NotSupportedException("Transcript import is not yet supported")` guard

**No AiService changes needed.** All three prompts work through the existing `SendPrompt<T>()` pipeline.

---

**Status:** All three prompts authored and ready for integration. Wash owns the plumbing.

---


### 2026-04-25T13:34Z: Captain confirms checkbox UX in v1.1

**By:** David (Captain), via Squad
**What:** v1.1 import wizard ships independent checkboxes for content harvesting:
- ☐ This is a Transcript (store full text on LearningResource.Transcript, MediaType="Transcript")
- ☐ Harvest Phrases (LexicalUnitType=Phrase entries)
- ☐ Harvest Words (LexicalUnitType=Word entries)

This replaces the radio-button content-type selector. Decouples "what is this content" from "what do I want extracted."

**Default checkbox states by detected/selected scenario** (Kaylee + River to design):
- User selects/auto-detects "Vocabulary": ☐ Transcript, ☐ Phrases, ☑ Words
- User selects/auto-detects "Phrases": ☐ Transcript, ☑ Phrases, ☑ Words
- User selects/auto-detects "Transcript": ☑ Transcript, ☐ Phrases, ☑ Words
- User can override any combination before commit.

**Open follow-up:** validation rule — at least one harvest checkbox (Phrases or Words) must be checked, OR Transcript must be checked. All-unchecked is invalid.

**Status:** Decisions #2, #3, and checkbox UX confirmed. #1 LexicalUnitType (Zoe-corrected, no new enum) and #4 branch strategy still pending.

---


### 2026-04-25: Captain confirms D1 — LexicalUnitType backfill (heuristic)

**By:** Captain (David Ortinau) via Squad
**What:** Backfill assumption + migration validation gate for v1.1.

**Decisions:**

1. **Backfill heuristic, NOT blanket assignment.** When the `SetDefaultLexicalUnitType` migration runs against existing `VocabularyWord` rows where `LexicalUnitType = 0` (Unknown):
   - **If the term contains a space** → assign `LexicalUnitType = 2` (Phrase)
   - **Otherwise** → assign `LexicalUnitType = 1` (Word)
   - Korean terms with spaces (e.g., "잘 못 들어요") will correctly become Phrase entries.
   - Single-token Korean words (e.g., "마고") will correctly become Word entries.
   - Edge case to watch: terms with leading/trailing whitespace — migration should `TRIM` before checking for space, OR we accept the rare false positive.

2. **Migration validation gate ENFORCED.** Per repo's standing rule (`scripts/validate-mobile-migrations.sh`), Wash MUST run the validation script after generating the migration and BEFORE the v1.1 PR opens. If the script fails, Wash fixes the migration — no deploy until green.

**Implementation note for Wash:**
```sql
-- The Up() body should look something like:
UPDATE VocabularyWords 
SET LexicalUnitType = CASE 
  WHEN INSTR(TRIM(Term), ' ') > 0 THEN 2  -- Phrase
  ELSE 1                                   -- Word
END
WHERE LexicalUnitType = 0;
```

But: **never hand-write the migration**. Use `dotnet ef migrations add SetDefaultLexicalUnitType` to scaffold, then edit the `Up()` body in the generated file to perform the heuristic UPDATE. EF will generate the schema-side scaffolding correctly; the data backfill SQL we author inside the generated file.

**Down() migration:** safe — no-op or leave the values as-is (downgrading shouldn't reset LexicalUnitType to Unknown; that would be data loss).

---


### 2026-04-25T13:01Z: Captain confirms Decision #2 (transcripts) — Option C

**By:** David (Captain), via Squad
**Decision:** Transcript imports persist BOTH the full text on `LearningResource.Transcript` AND run `ExtractVocabularyFromTranscript` to harvest VocabularyWord rows mapped via ResourceVocabularyMapping.
**Rationale:** A transcript is dual-purpose — readable source material plus a vocab/phrase mine for study. Storing only one forces a re-import for the other. All required pieces (Transcript field, MediaType="Transcript", ExtractVocabularyFromTranscript prompt) already exist; v1.1 wires the existing extraction prompt into the generic ContentImportService pipeline.
**Open sub-questions still outstanding:**
- Chunking strategy for transcripts >50KB (Zoe deferred to v1.2; v1.1 currently rejects). Captain to confirm.
- Behavior on zero-vocab extraction: rollback the resource, or keep the transcript and surface a warning. Captain to confirm.
- River must verify `ExtractVocabularyFromTranscript` is reachable from the generic pipeline (not just YouTube path).
**Status:** Decision #2 confirmed. Decisions #1, #3, #4 still pending Captain confirmation. Implementation remains gated.

---


### 2026-04-25: Captain confirms D4 — single branch, ship when complete

**By:** Captain (David Ortinau) via Squad
**What:** Branch strategy for v1.1 import work — Option A confirmed.
**Why:** "There's no reason to ship this until it's right." Captain prefers a complete, polished feature over partial v1.0 in production.

**Decision:**
- Continue v1.1 work on `feature/import-content-mvp` branch (do NOT push v1.0 yet, do NOT cut a v1.1 branch from main).
- Build phrases, transcripts, auto-detect, and checkbox harvest UX on top of the existing v1.0 commits.
- Before opening the final PR, rename the branch: `feature/import-content-mvp` → `feature/import-content` (drop the `-mvp` suffix — feature is no longer "minimum").
- Single PR encompassing v1.0 + v1.1. Title: *"Data Import: text/file/CSV import with auto-detect, phrases, transcripts, and vocabulary harvest"*.

**Implications:**
- v1.0 remains unmerged during v1.1 work — that's fine, Captain's call.
- PR will be larger (~1500+ lines) — Captain accepts the review burden in exchange for shipping a complete feature.
- Reviewers (and Captain's `/review` gate) get one cohesive review of the full import surface, not two scoped passes.
- No production exposure of partial functionality — users get the full Vocabulary/Phrases/Transcript/Auto-detect experience on day one.

**Branch rename happens at PR-open time**, not now. While v1.1 is in flight, branch stays `feature/import-content-mvp` so existing tooling, checkpoints, and references don't break.

---


### 2026-04-25T13:19Z: Captain refines harvest model + confirms Decision #3

**By:** David (Captain), via Squad
**What:** The import type the user picks determines what gets harvested into VocabularyWord rows:

| User picks | Harvests | Notes |
|---|---|---|
| **Vocabulary** | Words only (LexicalUnitType=Word) | Pure vocab list import |
| **Phrases** | Both Words AND Phrases (LexicalUnitType=Word + Phrase) | User input is standalone sentences with no sentence-to-sentence continuity |
| **Transcript** | Words primarily (not phrases in most cases) | Continuous prose; phrase extraction is the wrong tool here |

**Phrase-vs-Transcript classifier signal (CRITICAL for auto-detect):**
"Phrases" content = standalone sentences with NO continuity sentence-to-sentence. Each sentence stands alone. If you read it as a passage, it doesn't flow. The classifier prompt must check for continuity — if continuity exists, it's a Transcript; if missing, it's a Phrase list.

**Decision #3 (auto-detect confidence gate): CONFIRMED.**
River's three-tier model + always-visible banner + override before commit is approved. Confidence gate must run before any DB persistence — Captain's words: "have the user confirm before the import potentially pollutes the database."

**Decision #2 adjustment (transcripts):**
Previous: "run ExtractVocabularyFromTranscript to harvest vocab/phrases."
Corrected: "run ExtractVocabularyFromTranscript to harvest vocabulary WORDS primarily, not phrases." Phrase extraction from prose is the wrong tool. River's prompt may need adjustment to bias toward Word-type extraction when MediaType=Transcript.

**Open UX enhancement (Captain raised optionally):**
Independent checkboxes on the import wizard: ☐ Transcript ☐ Phrase ☐ Word — let the user explicitly state what they expect the import to harvest, instead of inferring from a single content-type radio. This decouples "what is this content" from "what do I want extracted." Captain to confirm: ship checkboxes in v1.1, or stick with radio-button content-type and ship checkboxes in v1.2?

**Status:** Decisions #2 (refined) and #3 confirmed. Decision #4 (branch) and the checkbox UX question still pending.

---


# Wash v1.1 Backend Implementation

**Date:** 2026-04-25
**Author:** Wash (Backend Dev)
**Branch:** `feature/import-content-mvp`

## Migration: SetDefaultLexicalUnitType

- **File:** `20260425134549_SetDefaultLexicalUnitType.cs` (both Postgres and SQLite variants)
- **Purpose:** Heuristic backfill of existing Unknown (0) LexicalUnitType entries
- **Logic:** `TRIM(TargetLanguageTerm)` checked for space → Phrase (2), else Word (1)
- **Down():** No-op (Captain D1: resetting to Unknown = data loss)
- **Postgres SQL:** Uses `POSITION(' ' IN TRIM("TargetLanguageTerm"))` with quoted identifiers
- **SQLite SQL:** Uses `INSTR(TRIM(TargetLanguageTerm), ' ')` with bare identifiers
- **Build verification:** Shared (net10.0), MacCatalyst (net10.0-maccatalyst), API all pass
- **validate-mobile-migrations.sh:** Requires running app + maui devflow — deferred to Captain's manual gate (script requires interactive device connection)

## ContentImportService v1.1 Branch Summary

### Phrase Branch
- Routes through existing `FreeTextToVocab.scriban-txt` which already classifies LexicalUnitType per entry
- Harvest both Words AND Phrases per Captain's harvest matrix
- TODO: Replace with River's dedicated `ExtractPhrasesFromContent.scriban-txt` when available
- Filters results by harvest checkbox flags (harvestWords, harvestPhrases)

### Transcript Branch
- Stores full text on `LearningResource.Transcript`, sets `MediaType="Transcript"`
- Extracts vocabulary using existing `ExtractVocabularyFromTranscript.scriban-txt`
- Word-biased extraction per Captain's D2 refinement
- Respects harvest checkboxes independently

### Auto-detect Branch
- AI classification prompt built inline (River's `ClassifyImportContent.scriban-txt` not yet landed)
- Three-tier confidence gate (Captain D3):
  - >= 0.85: auto-route, no user confirmation
  - 0.70-0.84: return to UI for user confirmation, no DB writes
  - < 0.70: return to UI, user must pick manually
- Classification runs BEFORE any DB persistence (Captain's directive)
- `ContentClassificationResult` DTO carries type, confidence, reasoning, and signals

### Checkbox Harvest Model (DTO contract)
- `ContentImportRequest` gains: `HarvestTranscript`, `HarvestPhrases`, `HarvestWords` booleans
- `ContentImportCommit` gains: same three booleans + `TranscriptText` string
- Backend validates at least one must be true
- `ImportRow` gains `LexicalUnitType` field for per-row classification
- `ContentImportPreview` gains `Classification` and `RequiresUserConfirmation` fields

## Edge Case Decisions

### Zero-vocab extraction
**Decision:** Persist the LearningResource (if transcript was requested) with empty vocab set + clear warning message. Do NOT silently succeed and do NOT error/rollback.
**Rationale:** A transcript is valuable even without extracted vocab. The user explicitly asked for transcript storage. Surfacing a warning lets UI show "Transcript stored, no vocabulary extracted" which is truthful and actionable.

### Transcript chunking >30KB
**Decision:** Reject with clear error message. Limit is 30KB (not the original 50KB) for transcript extraction because LLM context windows work better with shorter inputs.
**Rationale:** Captain hasn't decided on chunking strategy. v1.1 processes the whole text in one prompt up to 30KB. Anything larger gets a clear rejection message pointing to v1.2 chunking support.
**Follow-up:** v1.2 should implement sliding-window or semantic chunking with merge-dedup.

## Blockers Waiting on River
1. `ClassifyImportContent.scriban-txt` — using inline prompt as bridge; replace when River lands it
2. `ExtractPhrasesFromContent.scriban-txt` — using FreeTextToVocab as bridge (it already classifies LexicalUnitType)
3. River's transcript prompt word-bias adjustment — current `ExtractVocabularyFromTranscript.scriban-txt` already handles LexicalUnitType; may need refinement to suppress Phrase extraction for transcript context

## Files Changed
- `src/SentenceStudio.Shared/Migrations/20260425134549_SetDefaultLexicalUnitType.cs` (new)
- `src/SentenceStudio.Shared/Migrations/20260425134549_SetDefaultLexicalUnitType.Designer.cs` (new)
- `src/SentenceStudio.Shared/Migrations/Sqlite/20260425134549_SetDefaultLexicalUnitType.cs` (new)
- `src/SentenceStudio.Shared/Migrations/Sqlite/20260425134549_SetDefaultLexicalUnitType.Designer.cs` (new)
- `src/SentenceStudio.Shared/Services/ContentImportService.cs` (updated)
- `src/SentenceStudio.UI/Pages/ImportContent.razor` (updated — adapted DetectContentType → ClassifyContentAsync)

---

