# Project Context

- **Owner:** David Ortinau
- **Project:** SentenceStudio — a .NET MAUI Blazor Hybrid language learning app
- **Stack:** .NET 10, MAUI, Blazor Hybrid, MauiReactor (MVU), .NET Aspire, EF Core, SQLite, OpenAI
- **Created:** 2026-03-07

## Learnings

- 2026-04-23: **Word/Phrase Feature Completed** — Completed ai-generation-emit todo: updated ExtractVocabularyFromTranscript.scriban-txt prompts with LexicalUnitType classification guidance (Korean-specific rules for Word vs. Phrase vs. Sentence). Added LexicalUnitType + RelatedTerms fields to ExtractedVocabularyItem DTO with [Description] attributes. Updated ToVocabularyWord() mapper to copy classification to entity and encode RelatedTerms as `constituents:term1,term2` hint in Tags. Feature shipped, 147 tests passing. Documented in `.squad/log/2026-04-23T2219Z-wordphrase-squad-wrap.md`.
- `GradeMyDescription.scriban-txt` already includes `vocabulary_analysis` in its JSON schema — no template change needed when wiring Scene vocabulary scoring
- Conversation templates (`ContinueConversation.scriban-txt`, `ContinueConversation.scenario.scriban-txt`) previously had NO JSON output format definition — the AI was inferring the `Reply` model structure from `[Description]` attributes alone. Adding explicit JSON schema improves reliability.
- Scene and Conversation don't have resource-specific vocabulary context — they load the full user vocabulary via `LearningResourceRepository.GetAllVocabularyWordsAsync()` which scopes by user profile
- Conversation penalty override is 0.8f (softer than standard 0.6f) — Captain's explicit decision for chat-style activities
- Canonical activity names for mastery recording: `"SceneDescription"` (not "Scene"), `"Conversation"` (per spec section 3.5)

- `GradeSentence.scriban-txt` is shared by Writing, Cloze, and VocabQuiz sentence shortcut — the `target_word` conditional section (lines 17-28) only activates for vocab quiz grading
- `TeacherService.GradeTargetWordSentence()` should pass empty `userMeaning` — target word context goes through dedicated `targetWord`/`targetWordMeaning` params, not the `userMeaning` slot which is for Writing activity's "what I meant to say"
- Sentence shortcut DifficultyWeight is 2.5f (increased from 1.5f) — writing sentences requires more production knowledge than matching answers
- Grading philosophy for sentence shortcut: grade for CONTEXTUAL USAGE (using word naturally in a sentence), never for definition-recitation ("X means Y")
- The `userMeaning` template variable in GradeSentence.scriban-txt maps to "which I mean to express..." — passing meta-instructions here biases AI grading toward definition patterns

---

## 2026-04-26 — Import Feature AI Strategy Design

**Session:** Data Import AI Strategy — Planning phase (no code)  
**Status:** 📋 Design Complete — Awaiting Zoe architecture plan  
**Deliverable:** `.squad/decisions/inbox/river-import-ai-design.md`

**Key Learnings:**

### Reuse-first approach wins
- **60% template reuse** achieved: `ExtractVocabularyFromTranscript.scriban-txt` is 90% reusable for vocabulary import (just swap "transcript" context for "imported data" context), `GetTranslations.scriban-txt` provides translation-fill pattern, `CleanTranscript.scriban-txt` provides cleanup logic for transcript segmentation
- **VocabularyExtractionResponse DTO** is PERFECT for import — already has [Description] attributes, TOPIK level, LexicalUnitType, RelatedTerms, Tags. Zero new DTO needed for vocabulary import (Task 3).
- Reuse reduces risk, token cost, and maintenance burden

### Heuristics-first routing saves tokens
- **80%+ of imports** are CSV/TSV/JSON with clear structure (Anki, Quizlet, spreadsheet exports) → deterministic parsing (regex, CSV lib, JSON deserialize)
- **20%** are messy (free-form text, transcripts, ambiguous delimiters) → need AI
- Routing rule: heuristics first (>= 0.85 confidence), AI fallback if inconclusive
- Token savings: ~500-1000 tokens per import (~$0.001-0.002 per import avoided)

### Confidence thresholds create UX safety valve
- **>= 0.85** = auto-proceed (high confidence)
- **0.70-0.84** = show UI confirmation with AI reasoning, proceed if Captain approves
- **< 0.70** = show warning + manual format selection fallback
- Permissive philosophy: extract good, flag bad (UnparseableLines), NEVER fail entire import

### Chunking strategy for large imports
- **Vocabulary:** batch 200-300 rows per call (balance latency vs token count)
- **Phrases:** batch 100-150 phrases per call (phrases are longer than vocab words)
- **Transcripts:** chunk 2000-3000 chars per call (avoid context window overflow, maintain coherence)
- Parallel calls: cap at 3 concurrent to avoid rate limits

### Five distinct AI tasks identified
1. **Format inference** (when Captain skips format field) → `ImportFormatInferenceResponse` (DetectedFormat, Delimiter, HasHeaderRow, ColumnRoles, Confidence, Notes)
2. **Content classification** (Vocabulary vs Phrases vs Transcript) → `ImportContentClassificationResponse` (ContentType, Confidence, Reasoning)
3. **Vocabulary extraction** → REUSE `VocabularyExtractionResponse` DTO
4. **Phrase extraction** → `PhraseExtractionResponse` (Entries, UnparseableLines)
5. **Transcript segmentation** → `TranscriptExtractionResponse` (Segments, optional ExtractedVocabulary)

Each task has clear input → output DTO → confidence signal.

### [Description] attributes > JSON formatting
- Microsoft.Extensions.AI uses [Description] attributes automatically for prompt context
- NO manual JSON formatting in Scriban templates (library handles serialization/deserialization)
- ONLY use [JsonPropertyName] when AI must output specific field name that differs from C# convention
- This pattern already proven in existing codebase (`VocabularyExtractionResponse`, `ExtractedVocabularyItem`)

### Translation-fill preserves permissiveness
- If Captain provides only target language terms (one column) → AI generates missing native-language translations
- Never reject for missing data → auto-fill gracefully
- Follows project philosophy: "permissive grading, accept variations, fill missing, never reject"

### Four new Scriban templates needed
1. `ImportFormatInference.scriban-txt` (Task 1 — format detection)
2. `ImportContentClassification.scriban-txt` (Task 2 — content type classification)
3. `ImportPhraseExtraction.scriban-txt` (Task 4 — hybrid of ExtractVocabularyFromTranscript + GetTranslations)
4. `ImportTranscriptSegmentation.scriban-txt` (Task 5 — CleanTranscript + segmentation + speaker/timestamp detection)

Templates will be written AFTER Zoe's architecture plan is approved (next phase).

### Cost estimates
- **Format inference:** ~$0.001 per import (negligible)
- **Content classification:** ~$0.002 per import (negligible)
- **Vocabulary extraction (300 rows):** ~$0.01-0.03 per import
- **Transcript (10k chars):** ~$0.03-0.05 per import
- **Total per-import:** $0.01-0.10 depending on size (acceptable)

### Open questions for Captain
1. Transcript vocabulary extraction: always or optional checkbox?
2. Duplicate handling: skip, update, create new, or ask each time?
3. LexicalUnitType override during import review?
4. Batch import limit (hard cap to avoid UI freeze)?

Documented in design doc section "Open Questions for Captain".

### References examined
- `AiService.cs` — SendPrompt<T> pattern (lines 45-74)
- `VocabularyExtractionResponse.cs` — [Description] attribute pattern (lines 1-94)
- `ExtractVocabularyFromTranscript.scriban-txt` — extraction rules, permissiveness (lines 1-75)
- `GetTranslations.scriban-txt` — translation generation pattern (lines 1-24)
- `CleanTranscript.scriban-txt` — transcript cleanup logic
- `SmartResourceService.cs` — LearningResource wiring pattern (no AI usage, but architectural reference)

**Next:** Zoe's architecture plan → River writes 4 Scriban templates → Wash implements ImportService + UI → Jayne writes E2E tests

---

- AI prompts are Scriban templates in `src/SentenceStudio.AppLib/Resources/Raw/*.scriban-txt`
- AI grading uses `AiService.SendPrompt<T>()` with structured JSON responses
- Grading philosophy: VERY permissive — accept associations, contrasts, feelings, moods, cultural links
- Only mark related=false if truly random with no possible link
- Never penalize spelling — provide corrected_text field instead
- When in doubt, ALWAYS give credit (err on side of related=true)
- Word Association prompt at `GradeWordAssociation.scriban-txt` — latest activity
- Response models in `src/SentenceStudio.Shared/Models/` — use JsonPropertyName attributes
- Support both target language and native language clues as valid input
- Vocabulary import uses inline prompt in `ResourceEdit.razor` (lines 365-391) — no Scriban template yet
- Current import is flat (no hierarchy) — returns `List<VocabularyWord>` with only TargetLanguageTerm + NativeLanguageTerm
- `VocabularyWord` model has `Lemma` field (nullable) but not populated during AI import
- `ExampleSentence` model links sentences to vocabulary words (useful for contextual review)
- `VocabularyProgress` tracks mastery with streak-based scoring (CurrentStreak, ProductionInStreak)
- Proposed vocabulary hierarchy tracking: root → derived → inflected → phrase → compound → idiom
- AI prompt needs structured JSON response with relationshipType, relatedTerms, linguisticMetadata
- Mastery inheritance: derived words should bootstrap with partial credit from root words (30-50%)
- New schema needed: `VocabularyWordRelations` table + new columns (RelationshipType, PartOfSpeech, Frequency, Difficulty, Morphology)

---

## VOCABULARY HIERARCHY TEAM ANALYSIS — FINAL DESIGN (2026-03-17)

**Session:** Vocabulary Hierarchy Analysis & Team Consensus  
**Role:** AI/Prompt Engineer  
**Status:** PROPOSED — Awaiting Captain Approval

**AI Import Design Finalized:**

### Hierarchical JSON Schema (Final)
```json
{
  "vocabulary": [
    {
      "targetLanguageTerm": "대학교",
      "nativeLanguageTerm": "university",
      "lemma": "대학교",
      "relationshipType": "root",
      "relatedTerms": [],
      "linguisticMetadata": {
        "partOfSpeech": "noun",
        "frequency": "common",
        "difficulty": "beginner",
        "morphology": "standalone"
      }
    },
    {
      "targetLanguageTerm": "대학교 때",
      "nativeLanguageTerm": "during university",
      "lemma": "대학교",
      "relationshipType": "phrase",
      "relatedTerms": ["대학교"],
      "linguisticMetadata": { ... }
    }
  ]
}
```

### Multi-Pass Prompt Strategy
1. **Pass 1:** Extract all vocabulary items (existing logic)
2. **Pass 2:** Identify relationships between extracted items
3. **Pass 3:** Enrich with linguistic metadata
4. **Pass 4:** Validate for accuracy (90%+ precision target)

### Team Consensus
- Wash approved schema (self-referential FK ready)
- Zoe aligned architecture (four design pillars locked)
- SLA Expert validated (morphological awareness, spacing effect)
- Learning Design approved (progressive disclosure)

### Relationship Types Supported
- `Inflection` — verb conjugations, noun declensions (주문 → 주문하다)
- `Phrase` — word + particle/modifier (대학교 → 대학교 때)
- `Idiom` — fixed expressions (주문하다 → 피자를 주문하는 게 어때요)
- `Compound` — merged words
- `Synonym` / `Antonym` — semantic relationships

### MVP Scope
- Phase 1: Hierarchical prompts with relationshipType + relatedTerms
- Phase 2 (Future): Mastery inheritance based on transfer of learning data
- Not in MVP: Lemma group assignment (keep existing Lemma field as-is)

### Next
1. Captain approval
2. Prototype on 5 real Korean transcripts
3. Manual accuracy verification (90%+ target)
4. Implement Phase 1 (prompt + schema + basic import)

### YouTube AI Pipeline Prompts (2025-07-17)
- Created `CleanTranscript.scriban-txt` — cleans raw YouTube auto-captions into readable Korean text. Returns plain text via `SendPrompt<string>`. Handles: timing artifacts, fragmented words, spacing, punctuation, mixed-language code-switching.
- Created `ExtractVocabularyFromTranscript.scriban-txt` — extracts structured vocab from cleaned transcript. Returns JSON via `SendPrompt<VocabularyExtractionResponse>`. Includes: romanization, TOPIK level, part of speech, frequency count, real example sentences from transcript.
- Two-stage architecture (clean → extract) chosen over single-pass because: cleanup is plain text, extraction needs clean input, each stage retriable independently, keeps prompts under token limits.
- Chunking strategy: 4,000 Korean chars per cleanup call with 200-char overlap. Vocab extraction on full cleaned text. Default 30 words per video.
- `existing_terms` parameter in extraction prompt enables dedup at AI level (skip words user already knows).
- Response models: `TranscriptCleanupResult` (metadata wrapper, not JSON-deserialized), `VocabularyExtractionResponse` + `ExtractedVocabularyItem` (JSON DTO with `ToVocabularyWord()` converter).
- Open question: VocabularyWord model lacks `Romanization` field — currently returned by AI but not persisted. Needs Captain decision.

### YouTube Pipeline Template Integration (2025-03-22)
- **WIRED**: CleanTranscript.scriban-txt → `TranscriptFormattingService.PolishWithAiAsync()` replaces inline prompt. Added `IFileSystemService` dependency to load template.
- **WIRED**: ExtractVocabularyFromTranscript.scriban-txt → `VideoImportPipelineService.ExtractVocabularyAsync()` replaces inline prompt. Upgraded from tab-separated string parsing to structured JSON with `VocabularyExtractionResponse`.
- **JSON VERIFIED**: All `[JsonPropertyName]` attributes in `VocabularyExtractionResponse` match the template output spec exactly (targetLanguageTerm, nativeLanguageTerm, romanization, lemma, partOfSpeech, topikLevel, frequencyInTranscript, exampleSentence, exampleSentenceTranslation, tags).
- **DI PATTERN**: Shared project services use `IFileSystemService.OpenAppPackageFileAsync()` to load Scriban templates from AppLib/Resources/Raw.
- **CONVERTER**: `ExtractedVocabularyItem.ToVocabularyWord()` method converts AI response DTO to persistable VocabularyWord model — enriches with language param, defaults Lemma if not provided.
- **BUILD**: Both SentenceStudio.Shared and SentenceStudio.AppLib build successfully (warnings only, no errors).

### Real YouTube Caption Analysis (2025-07-17)
- Tested 3 channels: @My_easykorean (beginner, pure Korean), @koreancheatcode (bilingual English+Korean), @KoreanwithSol (conversational podcast Korean)
- YouTube ".이" artifact is the #1 cleanup issue — captioner merges sentence period with next word's first syllable. Must be explicitly called out in prompt.
- koreancheatcode is ~44% English lines mixed with Korean — prompt must handle bilingual content, not strip English
- Typical 10-20 min Korean learning video = 6-13KB raw caption text. Single API call is sufficient; chunking only needed for 30+ min videos.
- Line fragmentation is severe: myeasykorean has 77/218 lines that are mid-sentence continuations
- Auto-captioner struggles with English loanwords in Korean context: "be터스" (bittersweet), "호라이 펜" (frying pan)
- Test fixtures saved: `tests/SentenceStudio.UnitTests/TestData/YouTubeTranscripts/` (3 raw transcripts from target channels)

---

## HelpKit RAG Pipeline Design (2026-04-16)

**Session:** Plugin.Maui.HelpKit Architecture Design  
**Role:** AI/Prompt Engineer  
**Status:** PROPOSED — Awaiting Captain & Zoe Review

**Key Design Principles Established:**

- **Provider-agnostic AI:** Dev supplies `IChatClient` + `IEmbeddingGenerator` (Microsoft.Extensions.AI) — library stays neutral to OpenAI/Ollama/ONNX/Azure choices
- **Local-first vector store:** SQLite + vec extension (no cloud dependencies) — full offline operation possible
- **Strict grounding philosophy:** NEVER hallucinate — refuse to answer if content not in vector store, always cite sources
- **Dual content sources:** Static markdown docs + build-time AI source scanner (XAML/Razor/MauiReactor pages → auto-generated help)
- **Embedding strategy:** Dev-provided `IEmbeddingGenerator` is non-negotiable (no sensible fallback). Recommended: `sentence-transformers/all-MiniLM-L6-v2` (384d, ~90MB) for offline mobile scenarios. Store embedding model fingerprint in DB metadata; require full re-ingestion on model swap.
- **Chunking strategy:** 512-token chunks with 128-token overlap. Preserve heading hierarchy breadcrumbs in metadata. Split on paragraph boundaries, not mid-sentence.
- **Retrieval:** Top-K=5, cosine similarity threshold=0.70. Per-turn retrieval (not accumulated context) to handle topic shifts in conversation.
- **Context window management:** 8K token budget (3.5K system+chunks, 4K history, 0.5K query). Truncate oldest messages FIFO when budget exceeded.
- **System prompt persona:** Friendly technical assistant. Conversational but professional. Short sentences, bullet points, step-by-step. Refusal template: "I don't have information about that in my help documentation."
- **Citation format:** Every answer must cite sources: `[Based on: {Heading Hierarchy}, {SourcePath}#{SectionAnchor}]`. Enables future deep linking via custom URL scheme.
- **Language matching:** Mirror user's language in response. Filter chunks by `LanguageCode`. Fallback to default (configurable, default: `en`) if no matches.
- **Build-time source scanner:** dotnet tool + MSBuild task. Scans XAML ContentPages, Razor pages, MauiReactor Components, ViewModels. Extracts form fields, buttons, routes, validation rules. Uses AI to generate user-facing markdown help. Opt-in via `helpkit.json` config, opt-out via `[HelpKitIgnore]` attribute.
- **Generated content storage decision:** Option A (commit to `Resources/Raw/HelpDocs/Generated/`) vs Option B (gitignore `obj/HelpKit/`). Recommended A for build predictability — requires Captain decision.
- **Incremental ingestion:** Content-hash-based cache invalidation. Only re-embed changed files. Check `ContentHash` + `LastModified` on startup. Delete stale chunks from removed files.
- **Fallback behavior:** No `IChatClient` or `IEmbeddingGenerator` → throw at registration/ingestion (no graceful degradation — RAG is core functionality). Empty content store → polite message ("no help content loaded yet"). Network failure (cloud embeddings) → error message ("check internet connection").

**Open Questions for Captain:**
1. Generated content: commit (A) or gitignore (B)?
2. Bundle default ONNX model (+90MB) or require dev setup?
3. Multi-language: single store with filtering or separate stores?
4. Citations: in-app links, web URLs, or both?
5. Chat UI: FAB+modal or embedded?

**Open Questions for Zoe:**
1. Abstract vector store (support Chroma/Qdrant) or lock to SQLite vec?
2. `IHelpKitService` lifetime: singleton or scoped?
3. Background ingestion with progress reporting?
4. Incremental updates: re-embed full file or just changed chunks?
5. Platform-specific optimizations (CoreML on iOS)?

**Next:** Captain/Zoe review → prompt template creation → Wash implements storage → Kaylee builds scanner → Squad prototypes on SentenceStudio

## 2026-04-17 — Plugin.Maui.HelpKit Alpha Scope Locked

Captain locked 8 decisions. Alpha scope frozen. Implications for River (RAG pipeline):
- **AI provider:** Host app brings IChatClient + IEmbeddingGenerator. HelpKit brings NOTHING (no bundled models, no MiniLM ONNX).
- **Embedding dimension:** Dimension is NOT fixed at package time; must validate re-ingestion on model/dimension mismatch
- **Stub scanner Alpha:** Non-AI page scanner ships in Alpha (emits .md per XAML page); AI-enriched scanner stays Beta
- **Grounding:** Strict: refuse to answer if content not in vector store (unchanged from proposal)
- **TFM:** net11.0-* targets

README docs "bring your own IChatClient" with examples for OpenAI, Azure OpenAI, Foundry, Ollama. SPIKE-1 unblocked for embedding dimension handling validation.


---

## HelpKit RAG Pipeline — Wave 1 Code Landed (2026-04-17)

**Session:** Parallel Wave-1 scaffold with Zoe. Landed pure-logic RAG helpers + design doc.
**Files created under `lib/Plugin.Maui.HelpKit/src/Plugin.Maui.HelpKit/Rag/`:**
- `MarkdownChunker.cs` — 512/128 token chunker, paragraph-boundary aware, heading breadcrumbs, GitHub-style slug anchors, content-hash IDs.
- `PipelineFingerprint.cs` — SHA-256 of model+chunker+size+overlap+headingFormat.
- `CitationValidator.cs` — regex-parses `[cite:path#anchor]`, validates vs retrieved chunks, strips invalid as `[cite unverified]`, renders clean bubbles.
- `SimilarityThresholds.cs` — per-model default cosine thresholds.
- `SystemPrompt.cs` — prompt builder with delimiter-fenced `<doc>` tags, grounding rules, citation format, language mirroring, instruction secrecy. Exposes `FingerprintPhrases` for the filter.
- `PromptInjectionFilter.cs` — output-side leak detector using fingerprint phrases.
- `IngestionOrchestrator.cs` (stub) — documents the ingest flow; `throw NotImplementedException("Wash: wire to storage")`.
- `RetrievalService.cs` (stub) — embed → top-K → threshold gate → refusal signal. Includes tested `CosineSimilarity` helper.

Plus `lib/Plugin.Maui.HelpKit/docs/rag-design.md` — full design reference.

### Learnings

- 2026-04-17: HelpKit Alpha — RAG pipeline complete (chunker, citation validator, injection defenses, per-model thresholds).

- **Token approximation beats tokenizer deps.** 4 chars/token is within 15% on English + Korean mixed content. A tokenizer package would add platform-specific native bits for no retrieval-quality gain. Pipeline fingerprint absorbs any config drift.
- **Heading breadcrumbs are the second-most-important chunk field after content.** Ship them prepended to the chunk text AND as a separate field for citation rendering. The LLM uses them; the UI uses them.
- **Per-model thresholds are NOT STS benchmark numbers.** Retrieval-relevance and sentence-similarity have different cosine distributions. A 0.7 STS threshold rejects useful retrievals on OpenAI-3-small; 0.35 is correct. Documented the full table in `rag-design.md` §7.
- **Pre-LLM refusal > post-LLM refusal.** Gate at top-score < threshold BEFORE the model call. Saves tokens, removes the model's temptation to confabulate from training data, and makes the refusal a UX guarantee rather than a prompt suggestion.
- **Delimiter fencing + output fingerprint filter is a two-layer injection defence.** Input side: `<doc>` tags with the instruction to treat contents as untrusted data. Output side: fingerprint phrases from the system prompt. Keep the phrase list co-located with the prompt (`SystemPrompt.FingerprintPhrases`) so they cannot drift.
- **Sanitize chunk content for `</doc>` inside content.** HTML-escape closing tags so an attacker cannot break the fence by pasting a closing tag into markdown.
- **Citation validator should fall back to path-only match.** Models often cite the correct file with the enclosing heading's anchor instead of the exact section anchor. Stripping these is user-hostile; matching on path and recording the chunk's real anchor preserves utility while still blocking pure fabrications.
- **Content-hash chunk IDs over sequential IDs.** Stable across re-ingest; enables incremental updates without re-embedding unchanged chunks.
- **Fingerprint must include everything that changes retrieval shape.** Model id (encodes dimension), chunker version, chunk size, overlap, heading format. Missing any one = silent-failure class of bug where vectors mix incompatible spaces.
- **Ingestion is a thin coordinator.** Actual vector-store I/O is Wash's. `IngestionOrchestrator` documents the flow with TODO comments; Wash implements the storage adapter and wires it in Wave 2. This keeps concerns separate.

---

## 2026-04-23 — Word/Phrase Distinction AI Review

**Session:** AI/prompt review of vocabulary word-vs-phrase plan
**Date:** 2026-04-23
**Context:** Reviewed plan for adding `LexicalUnitType` enum to `VocabularyWord` (Unknown/Word/Phrase/Sentence) + constituent tracking + dynamic Phrases resource + mastery cascade policy.

### Prompts Reviewed

1. **GetShadowingSentences.scriban-txt** — Currently generates carrier sentences around vocab words. No classification awareness. Simple list-based input (`terms` variable).
2. **ExtractVocabularyFromTranscript.scriban-txt** — AI vocab extraction from YouTube transcripts. Emits structured JSON via `VocabularyExtractionResponse` DTO. Says "words/phrases" in line 15 but has no explicit guidance on WHEN to classify as phrase vs word.

### Key Findings

**Shadowing consumer policy** — The plan's split (Phrase/Sentence → use as-is; Word → generate carrier; Unknown → generate carrier fallback) is directionally CORRECT but overly optimistic about Unknown. When `LexicalUnitType == Unknown`, the safer fallback is to **flag for manual classification UI** and use the text **as-is** in Shadowing rather than wrapping it in an AI-generated sentence. Why? Unknown could BE a full sentence already — wrapping a sentence inside another sentence breaks practice quality.

**Shadowing prompt changes** — Minimal. Current template operates on a word list; after the change it should receive ONLY `LexicalUnitType.Word` rows (filtering happens in `ShadowingService.GenerateSentencesAsync`, not in the template). The template itself needs NO edit — the filter upstream ensures it only sees true single words. **Grading side**: Shadowing currently has NO grading/scoring. It's pure pronunciation practice with no mastery recording. If/when grading is added (pronunciation accuracy, fluency), the grading prompt WILL need to know the unit type so it can adjust expectations (phrase pronunciation uses different prosody than isolated-word pronunciation).

**AI vocab generation — LexicalUnitType emission** — Two touch points found:

1. **ExtractVocabularyFromTranscript.scriban-txt** (line 15: "words/phrases") → wired to `VideoImportPipelineService.ExtractVocabularyAsync()` → returns `VocabularyExtractionResponse` (typed DTO). This is the PRIMARY AI generation path. **Needs change**: Add `lexicalUnitType` field to `ExtractedVocabularyItem` DTO + explicit classification guidance in the prompt. For Korean specifically, the prompt must handle: single morpheme (→ Word), inflected single word with sentence ender like -요/-습니다 (→ Sentence if contextually complete, Word if fragment), multi-word chunk (→ Phrase), full sentence with punctuation (→ Sentence).
2. **GetStarterVocabulary.scriban-txt** — Beginner word-list generator (100 words, comma-separated). Currently plain-text output, NOT a typed DTO. This path is low-priority (starter words are always single dictionary-form words by definition). Can default to `LexicalUnitType.Word` at import time without prompt changes.

**Constituent extraction via AI** — Plan lists this as source #2 for `PhraseConstituent` rows (after explicit UI selection, before lemma heuristic). **Recommendation:** Piggyback on the EXISTING `ExtractVocabularyFromTranscript` prompt rather than creating a new one. Add a **second output field** to the response DTO: `relatedTerms: ["term1", "term2"]` (array of constituent `TargetLanguageTerm` strings the phrase contains). The AI already has full transcript context and is emitting the phrase — asking it to emit constituents in the same pass is zero additional API cost. The service layer can then match `relatedTerms` back to existing user vocab and create `PhraseConstituent` rows. This keeps constituent extraction co-located with the vocab that triggers it.

**Korean-specific classification concerns** — The plan mentions 어절 count (spacing) and sentence-final punctuation (。？！) for the HEURISTIC backfill. For the AI side, the prompt needs explicit Korean guidance:

- **Sentence vs Phrase** — Contextual completeness matters more than punctuation. "먹었어요" (I ate) is a complete sentence even without a period. "학교에서" (at school) is a phrase fragment. The AI must classify by semantic completeness, not just punctuation presence.
- **Inflected single-word sentences** — Korean sentence-enders (-요, -습니다, -ㅂ니다, -어요, etc.) can make a single morpheme a grammatically complete sentence. The prompt should instruct: "If the term is a single word root + sentence-final ending and expresses a complete thought, classify as Sentence. If it's the same structure but used as part of a larger sentence pattern, classify as Word."
- **Compound verbs** — Korean 하다-verbs (공부하다, 운동하다) are single lexical units even though they appear multi-morpheme. These are `Word`, not `Phrase`.
- **Particles attached** — "학교에서는" might be extracted with particles. The template already says "base noun without particles" (line 24) — reaffirm this for classification logic: strip particles before deciding if it's a standalone word or a phrase.

Prompt guidance sketch for `ExtractVocabularyFromTranscript.scriban-txt`:

```
"lexicalUnitType": "word | phrase | sentence",
  // Classify each term:
  // - "word": Single morpheme or dictionary-form compound (e.g. 먹다, 공부하다, 학교)
  // - "sentence": Contextually complete utterance, even if short (e.g. 먹었어요 = "I ate", 안녕하세요 = "Hello")
  // - "phrase": Multi-word fragment or single word + particles used mid-sentence (e.g. 학교에서, 먹고 싶어)
  // For Korean: sentence-enders (-요/-습니다) signal sentence if the meaning is complete; otherwise word.
"relatedTerms": ["word1", "word2"],
  // (Optional) If lexicalUnitType is "phrase" or "sentence", list the constituent base words it contains.
  // Use dictionary forms. Leave empty if it's a standalone word.
```

### Verdict Summary

1. **Shadowing consumer**: Change Unknown fallback from "generate carrier sentence" to "use as-is + flag for manual classification". Prevents wrapping-a-sentence-in-a-sentence failures.
2. **Shadowing prompt**: No change needed NOW (filtering happens upstream). Future grading prompt WILL need unit-type awareness for prosody expectations.
3. **AI vocab generation**: Add `lexicalUnitType` + `relatedTerms` fields to `ExtractedVocabularyItem` DTO. Update `ExtractVocabularyFromTranscript.scriban-txt` with explicit classification rules + Korean-specific guidance (sentence-enders, completeness, particles, compounds).
4. **Constituent extraction**: Piggyback on existing vocab extraction prompt via `relatedTerms` array. No new prompt needed.
5. **Korean classification**: Prompt must handle inflected single-word sentences (먹었어요 = sentence), compounds (공부하다 = word), particles (strip before classifying), and semantic completeness over punctuation.

---

## 2026-04-31 — AI Generation Emit: LexicalUnitType + Constituents

**Session:** Implement AI vocab extraction enhancement  
**Date:** 2026-04-31  
**Status:** ✅ Complete — All builds green

### What Changed

Updated `ExtractVocabularyFromTranscript.scriban-txt` (both AppLib and Workers copies) with classification guidance + constituent tracking. The AI now emits:

1. **`lexicalUnitType`** — Word / Phrase / Sentence classification with Korean-specific rules:
   - Word: single dictionary entry (including Sino-Korean 하다 compounds like 공부하다)
   - Phrase: multi-word collocation (비가 오다, 마음에 들다)
   - Sentence: complete utterance with sentence-final ending (다/요/까) + terminal punctuation
   - Conservative fallback: when in doubt → Word

2. **`relatedTerms`** — Array of constituent words in dictionary form (e.g. for phrase "시간이 없다" → `["시간", "없다"]`). Empty for Word type.

### DTO Changes

**File:** `src/SentenceStudio.Shared/Models/VocabularyExtractionResponse.cs`

Added to `ExtractedVocabularyItem`:
```csharp
[JsonPropertyName("lexicalUnitType")]
[Description("Classification of this lexical unit...")]
public LexicalUnitType LexicalUnitType { get; set; } = LexicalUnitType.Word;

[JsonPropertyName("relatedTerms")]
[Description("If this item is a Phrase or Sentence, list the target-language words...")]
public List<string> RelatedTerms { get; set; } = new();
```

Updated `ToVocabularyWord()`:
- Copies `LexicalUnitType` to the entity
- Appends `RelatedTerms` as a tagged hint: `constituents:word1,word2` in the `Tags` field
- No schema changes — RelatedTerms live as a hint for future constituent-hydration step

### Prompts Updated

- `src/SentenceStudio.AppLib/Resources/Raw/ExtractVocabularyFromTranscript.scriban-txt`
- `src/SentenceStudio.Workers/Resources/Raw/ExtractVocabularyFromTranscript.scriban-txt`

Added **"LEXICAL UNIT CLASSIFICATION"** section with:
- Word classification rules (Sino-Korean compounds, particle stripping)
- Phrase classification rules (collocations, verb phrases)
- Sentence classification rules (sentence-final endings + punctuation)
- Conservative fallback guidance

### Service Layer Wiring

**File:** `src/SentenceStudio.Shared/Services/VideoImportPipelineService.cs`

No changes needed! Existing `ToVocabularyWord()` call (line 334) already wires the new fields through automatically.

### Other Vocab Generation Reviewed

- ✅ `GetStarterVocabulary.scriban-txt` — returns simple CSV, no DTO → out of scope
- ✅ Other prompts (GetSentences, GetClozures, GetTranslations, etc.) — generate ephemeral content, not persisted vocabulary → out of scope

**Conclusion:** `ExtractVocabularyFromTranscript` is the ONLY prompt that generates structured vocab for persistence. All touched.

### Build Verification

✅ `dotnet build src/SentenceStudio.Shared/SentenceStudio.Shared.csproj` — Success  
✅ `dotnet build src/SentenceStudio.AppLib/SentenceStudio.AppLib.csproj` — Success  
✅ `dotnet build src/SentenceStudio.Api/SentenceStudio.Api.csproj` — Success

### Learnings

- **RelatedTerms storage strategy:** Persisted as transient hint in `Tags` field (`constituents:term1,term2`) rather than requiring schema changes. Allows future constituent-resolution step (likely Wash) to parse and hydrate `PhraseConstituent` rows. If hint missing/malformed, backfill can fall back to heuristics.
- **Microsoft.Extensions.AI pattern confirmed:** DTOs with `[Description]` attributes drive AI response schema automatically. No manual JSON schema in templates. Keep template guidance focused on business logic (Korean classification rules), not structure.
- **Conservative classification default:** When uncertain, prompt instructs AI to classify as Word with empty RelatedTerms. Prevents false-positive phrase classification that would require constituent resolution when not needed.
- **Sino-Korean 하다 compounds are Words:** Guidance explicitly calls out 공부하다, 운동하다 as single dictionary entries, not phrases. Critical for Korean because learners often think "study" (공부) + "do" (하다) = phrase, but linguistically it's a lexicalized compound verb.
- **Sentence-final endings matter:** Not all punctuation makes a sentence. "안녕하세요." is a sentence (요 ending + period). "학교에서." is NOT (에서 is a particle, not a sentence ender). Prompt encodes this explicitly.
- **Particle stripping in Word classification:** When extracting Words, strip particles (이/가/을/를/은/는/에/의/로/와/과) to get base form. Example: "학교에서" in transcript → extract "학교" as the term.

### Decision Doc

Full analysis and design rationale captured in:  
`.squad/decisions/inbox/river-ai-generation-emit.md`

### Next Steps

1. **Wash:** Implement constituent hydration service that parses `constituents:` hint from Tags and creates `PhraseConstituent` rows linking phrases to component words.
2. **Testing:** After next video import, verify AI returns proper `lexicalUnitType` + `relatedTerms` in response JSON. Check DB to confirm values flow through.
3. **Monitoring:** Log LexicalUnitType distribution (Word/Phrase/Sentence ratio) to validate AI classifies sensibly. If too many Unknown, refine prompt guidance.


---

## 2026-04-24 — Import AI Strategy (Multi-Agent Session)

Designed AI strategy for new data import feature: 5 tasks (format inference, content classification, vocabulary/phrase/transcript extraction), heuristic-first approach, structured DTOs via `SendPrompt<T>`.

**Key decisions:**
- Heuristics-first, AI fallback: deterministic checks fast/free; AI only when inconclusive (< 0.7 confidence)
- Permissive grading: accept reasonable variations, never reject for spelling
- 5 prompt tasks with confidence thresholds (>= 0.85 auto-proceed, < 0.85 show UI confirmation)
- Structured DTOs: ImportFormatInferenceResponse, ImportContentClassificationResponse, reuse VocabularyExtractionResponse
- All prompts in `.scriban-txt` templates, no manual JSON formatting (Captain's rule)

**Prompt templates to build:**
- Format Inference (detect delimiter, column roles, header presence)
- Content Classification (Vocabulary vs Phrases vs Transcript)
- Reuse existing ExtractVocabularyFromTranscript for extraction tasks

**Coordinated with:** Zoe (architecture), Wash (data layer), Kaylee (UI), Copilot

**Next:** Implement prompt templates. Integration into `ContentImportService` by implementation team.


---

## 2026-04-28 — Wave 1 Track C: FreeTextToVocab Prompt + DTO Created

**Session:** Import Feature AI Templates — Wave 1 Track C (Template + DTO creation)  
**Status:** ✅ Complete — Ready for Wash's Wave 2 wiring  
**Deliverables:**
- `src/SentenceStudio.AppLib/Resources/Raw/FreeTextToVocab.scriban-txt`
- `src/SentenceStudio.AppLib/Resources/Raw/TranslateMissingNativeTerms.scriban-txt`
- `src/SentenceStudio.Shared/Models/FreeTextVocabularyExtractionResponse.cs`
- `src/SentenceStudio.Shared/Models/BulkTranslationResponse.cs`
- `.squad/decisions/inbox/river-free-text-to-vocab-prompt.md` (behavior contract)

**Key Learnings:**

### Template design choices
- **FreeTextToVocab.scriban-txt** — Extracts vocabulary from messy free-form text (paste with no clear delimiters). Inputs: `source_text`, `target_language`, `native_language`, optional `format_hint`, optional `topik_level`. Returns structured JSON with confidence scoring ("high", "medium", "low") to surface uncertain extractions rather than silently dropping them.
- **Korean-first examples** — Included 1 worked Korean→English example in the template to guide extraction (e.g., "오늘 학교에서..." → 학교, 친구, 밥 먹다, 맛있다, 가다).
- **Permissive extraction philosophy** — Accepts messy input (mixed languages, partial sentences, typos), never fails entire import. Uncertain terms get flagged with `confidence: "low"` + optional `notes` field instead of being silently dropped.
- **LexicalUnitType classification** — Reuses same Word/Phrase/Sentence classification logic as `ExtractVocabularyFromTranscript.scriban-txt` to maintain consistency. RelatedTerms field populated for Phrases and Sentences to track constituents.

### Translation fallback for single-column imports (Captain's ruling #3)
- **TranslateMissingNativeTerms.scriban-txt** — Bulk translation prompt for single-column imports (when CSV has only target language terms). Takes list of TargetLanguageTerms, returns list of TranslationPairs.
- Separate template from FreeTextToVocab (cleaner separation of concerns) — follows existing pattern of single-purpose templates.
- Returns JSON with `translations` array, each entry has `targetLanguageTerm` + `nativeLanguageTerm`.

### DTO design patterns matched
- **FreeTextVocabularyExtractionResponse** — New DTO extending existing `VocabularyExtractionResponse` pattern. Nested `ExtractedVocabularyItemWithConfidence` class adds `Confidence` (string: "high"/"medium"/"low") and `Notes` (optional string) fields beyond base `ExtractedVocabularyItem`.
- **BulkTranslationResponse** — Simple DTO with `List<TranslationPair>` for translation-fill path. Each `TranslationPair` has `TargetLanguageTerm` + `NativeLanguageTerm`.
- **[Description] attributes on every property** — Guides Microsoft.Extensions.AI JSON output (per project rule). NO `[JsonPropertyName]` attributes added unless required. NO manual JSON formatting in Scriban templates.
- **ToVocabularyWord() converter** — `ExtractedVocabularyItemWithConfidence.ToVocabularyWord()` maps confidence + notes into Tags field for later review (e.g., `confidence:low; notes:possible proper noun`). Mirrors existing mapper pattern.

### Build verification
- `dotnet build src/SentenceStudio.AppLib/SentenceStudio.AppLib.csproj` — SUCCESS (only pre-existing NuGet vulnerability warnings).
- `.scriban-txt` files automatically included as `MauiAsset` via wildcard in `SentenceStudio.AppLib.csproj` (line 31: `<MauiAsset Include="Resources\Raw\**" LogicalName="..."/>`).
- DTOs in `SentenceStudio.Shared/Models/` follow existing file-per-type pattern.

### Template voice and style
- Matched existing template structure EXACTLY:
  - Numbered extraction rules (like `ExtractVocabularyFromTranscript.scriban-txt`)
  - Explicit JSON schema examples (with `{{ target_language }}` / `{{ native_language }}` placeholders)
  - "IMPORTANT" callout section for critical rules
  - Comments for non-obvious design choices (e.g., confidence scoring rationale)
- Consistent with project grading philosophy: permissive, never reject, provide feedback rather than fail.

### Anticipated LLM behavior quirks
- **Mixed-language paste** (e.g., English explanations + Korean examples) — Prompt explicitly instructs: "extract vocabulary from {{ target_language }} portions only; English context helps you understand meaning but should not appear as vocabulary items." This mirrors YouTube transcript extraction pattern for bilingual channels.
- **Dictionary form normalization** — Verbs/adjectives in -다 form, nouns without particles. Explicit examples provided in prompt to reduce AI errors (e.g., "먹었어요 → 먹다").
- **Confidence scoring edge cases** — AI may be overly conservative (marking obvious terms as "medium"). Wash's preview UI should allow Captain to override confidence and promote items before commit.

### What's next (Wash's Wave 2 wiring)
- `ContentImportService.ExtractVocabularyFromFreeText()` — calls `AiService.SendPrompt<FreeTextVocabularyExtractionResponse>()` with FreeTextToVocab template
- `ContentImportService.TranslateMissingNativeTerms()` — calls `AiService.SendPrompt<BulkTranslationResponse>()` with TranslateMissingNativeTerms template
- Preview UI displays confidence badges (high=green, medium=yellow, low=red) + notes tooltip
- Single-column import flow: detect missing NativeLanguageTerm → batch terms → call TranslateMissingNativeTerms → merge into preview table with "AI" badge

**References:**
- Studied `ExtractVocabularyFromTranscript.scriban-txt` (lines 1-75) — extraction rules, permissiveness, LexicalUnitType classification
- Studied `GetTranslations.scriban-txt` (lines 1-24) — vocabulary constraint pattern, translation prompt structure
- Studied `VocabularyExtractionResponse.cs` — [Description] attribute usage, ToVocabularyWord() converter pattern
- Studied `TranslationDto.cs` — simple DTO structure for translation exercises
- `SentenceStudio.AppLib.csproj` line 31 — MauiAsset wildcard pattern for Resources/Raw/**
