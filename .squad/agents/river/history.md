# Project Context

- **Owner:** David Ortinau
- **Project:** SentenceStudio — a .NET MAUI Blazor Hybrid language learning app
- **Stack:** .NET 10, MAUI, Blazor Hybrid, MauiReactor (MVU), .NET Aspire, EF Core, SQLite, OpenAI
- **Created:** 2026-03-07

## 2026-05-04 — Korean Number Generation & Grading (Phase 1)

**Session:** Numbers Activity — Generator + Grader Phase 1  
**Status:** ✅ Shipped — All tests passing (33/33)  
**Deliverable:** `src/SentenceStudio.AppLib/Services/Numbers/`

### Implementation Summary

Created pure deterministic (NO LLM) Korean number item generator and grader for Phase 1 contexts:

**Generator (`KoreanNumberItemGenerator`):**
- 3 contexts: Counting (Native + 5 counters), Time (Mixed 12-hour), Age (Native + 살)
- Sound-change rules encoded:
  - 하나→한, 둘→두, 셋→세, 넷→네 (ONLY standalone before counter, NOT in compounds like 스물하나)
  - 스물→스무 (ONLY at exactly 20, NOT 21-29)
- Phase 1 range: 1–99 (buckets "1-10", "11-99")
- Deterministic via optional `RandomSeed` for testability

**Grader (`KoreanNumberAnswerGrader`):**
- Permissive normalization: whitespace, full-width digits, spacing tolerance
- 7 error classes: `SinoNativeSwap`, `CounterMismatch`, `SoundChangeMissed`, `MagnitudeOff10x`, `Typo`, `WrongFormat`, `Unknown`
- Every wrong answer gets specific `ErrorClass` + pedagogical `Tip`
- Levenshtein distance for typo detection
- Counter mismatch prioritized over Sino/Native swap (prevents false positives)

**Tests (33 passing):**
- Generator: each context happy path, all sound-change rules, bucket assignment, determinism
- Grader: exact match, alternates, all error classes, normalization, edge cases

**DI Registration:** Added to `CoreServiceExtensions.cs`

**Coordination:** Wash's `NumberSystem` enum (Native/Sino/Mixed/Lexical) reused; added `Mixed` variant.

### Learnings

1. **Korean sound-change rules are CONTEXT-DEPENDENT:**
   - 둘→두 applies standalone (두 명), NOT compounded (스물둘 살, 열둘 시)
   - 스물→스무 ONLY at exactly 20 (스무 살), NOT 21-29 (스물하나 살)
   - Compounds (열하나, 스물하나) keep full forms

2. **ErrorClass taxonomy for grading:**
   - `SinoNativeSwap`: wrong number system (Sino vs Native)
   - `CounterMismatch`: wrong counter (개 vs 명)
   - `SoundChangeMissed`: missed obligatory sound change (둘 명 → 두 명)
   - `MagnitudeOff10x`: magnitude error (520 vs 5200)
   - `Typo`: single-character Levenshtein distance
   - `WrongFormat`: Hangul when digits expected or vice versa
   - `Unknown`: catch-all with canonical answer

3. **Grader error-classification priority order matters:**
   - Check `CounterMismatch` FIRST (before Sino/Native) to avoid false positives when both counter and system are wrong
   - Then `SinoNativeSwap`, then `SoundChangeMissed`, then magnitude/typo/format/unknown

4. **Generalization path for Japanese/Mandarin/Spanish:**
   - Same `INumberItemGenerator` interface, language-specific strategies
   - Japanese: 音/訓 dual systems (similar to Korean), ~150 counters, euphonic changes (一本/三本/六本 rendaku)
   - Mandarin: single system + classifiers, 一/不 tone sandhi as automaticity bottleneck
   - Spanish: single system, gender/number agreement on ordinals + uno, apocope (uno→un, ciento→cien)
   - Core grader logic reusable; error-class taxonomy stays the same (swap sound-change rules for tone sandhi, agreement, etc.)

### Architecture



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


## Core Context (Summarized History)

[Earlier entries (prior to 2026-04-25) have been reviewed and consolidated. Key patterns:]

- Agent is primary domain specialist for this project area
- Responsibilities established through prior charter/assignment cycles
- Cross-agent coordination pattern: decision inbox → decisions.md → history broadcast
- Team cadence: 3-agent orchestrations typical for major feature work
- Velocity: multiple cycles per week with focus on validation/ship gates

#### Dual-harvest pattern
A reusable Scriban pattern emerged: use `{{ if harvest_X }}` conditionals to gate extraction steps. This lets a single prompt serve multiple harvest configurations without needing separate prompts per combination. The pattern:
1. Wrap each extraction step in `{{ if harvest_TYPE }}`
2. Number steps dynamically based on which flags are active
3. Adjust dedup rules based on which types are active
4. Add a CRITICAL rule for the primary type (must emit at least one entry per input line)

### Coordination
- Wrote `.squad/decisions/inbox/river-import-prompt-shape.md` with locked JSON contract for Wash.
- Key action for Wash: wire `ExtractVocabularyFromPhrases.scriban-txt` into the Phrases branch (replace `ParseFreeTextContentAsync` call), add `ContentType.Sentences` enum value, add Sentences branch routing to `ExtractVocabularyFromSentences.scriban-txt`.
- Key action for Wash: classifier now returns `"Sentences"` — add case to classifier switch (`"sentences" => ContentType.Sentences`).

- 2026-04-27: **TEAM CONVERGENCE: v1.2 Import Prompts Locked** — Three independent agents diagnosed identical root cause: Phrases branch called wrong prompt. River's dedicated `ExtractVocabularyFromPhrases.scriban-txt` had been written + deployed but never wired in (TODO at line 191). Fixed phrase prompt with harvest_phrases/harvest_words conditionals + pipe-delimited handling + Captain's exact Korean|English few-shot format + CRITICAL rule: each input line MUST emit a Phrase entry. Created NEW `ExtractVocabularyFromSentences.scriban-txt`. Updated `ClassifyImportContent.scriban-txt` to four-class output (Vocabulary/Phrases/Sentences/Transcript). Locked JSON response shape — identical `{ "vocabulary": [...] }` across all 3 extraction prompts for clean Wash wire-up. Wash: implement 2-step pipeline. Kaylee: add Sentences UI. Jayne: execute 7-section test plan.

- 2026-04-27: **M.E.AI 10.5.0 Claim Verification** — Verified Captain's brief against Microsoft sources. Key finding: `UseRateLimitRetry()` is hallucinated (does not exist); VectorData confirmed via PR #7434; Realtime+TTS real but shipped in 10.4.1 (not 10.5.0), all experimental. Output synthesized by Zoe into strategic recommendation. (See: `.squad/orchestration-log/2026-04-27T19-06-10Z-river.md` and merged decision in `.squad/decisions.md`.)


## Team Update: M.E.AI 10.5 Debt-Paydown Complete (2026-04-27 → 2026-04-28)

**Status**: SHIPPED ✅

Zoe's M.E.AI 10.5 strategic recommendations executed via three-agent orchestration (Wash Phase 1 + Phase 2, Jayne validation):

**What shipped:**
- **CPM (Central Package Management)**: Directory.Packages.props created; ~95 packages centralized; 178 Version= attributes stripped from 22 csprojs
- **Polly Resilience**: All 5 OpenAI sites now route via HttpClientPipelineTransport with Polly policies (120s attempt / 300s total / 300s circuit-breaker). Zero retry storms in validation.
- **Config Extraction**: gpt-4o-mini, tts-1, text-embedding-3-small, and ElevenLabs voice IDs moved to appsettings.json with ?? fallback defaults. Single point of change.
- **SKU Assessment**: AppLib stays on Agents.AI (ConversationAgentService + ConversationMemory use orchestration types with no M.E.AI equivalent). Waiting for M.E.AI agent layer.
- **RetrievalService Defused**: NotImplementedException → no-op stub + [Obsolete]. Zero callers verified.

**Validation results** (6/6 gates PASS):
- Build matrix: 13/13 buildable projects green; 626 tests passing
- Aspire runtime: Clean start, all resources Running
- AI end-to-end: Conversation + comprehension scoring + ElevenLabs TTS working; clean Polly pass-through (no retry storms)
- Config sanity: All 4 projects (Api, WebApp, Workers, AppLib) read from appsettings.json with correct fallback defaults

**Implications for all agents going forward:**
- All future OpenAI HTTP traffic flows through Polly automatically
- Model names are now config-driven, not code-driven
- AppLib remains on Microsoft.Agents.AI; this is not a blocker (transitive M.E.AI dependency exists)
- MAUI+CPM gotchas are documented for future package maintenance

**Decision artifacts**: .squad/decisions.md merged (3 entries); inbox cleaned; decisions-archive-2026-04-28.md created

**Orchestration logs**: .squad/orchestration-log/2026-04-28T00:06:30Z-{agent}.md (3 entries)

**Session log**: .squad/log/2026-04-28T00:06:30Z-meai-debt-paydown.md

**SHIP IT verdict**: All validation gates pass; zero regressions introduced. Production-ready.


- 2026-05-01: **Import Confidence Calibration Fix (bug4-ai-confidence)** — Fixed AI classifier always returning ≥0.85 confidence. Root cause: dual-prompt situation where inline `BuildClassificationPrompt()` was active but my comprehensive `ClassifyImportContent.scriban-txt` was written but never wired. The inline prompt lacked concrete confidence anchors — it told the AI "≥0.85 = strong signals, 0.70-0.84 = mixed, <0.70 = ambiguous" but didn't define what "strong" or "mixed" means with concrete examples. Solution: (1) wired the Scriban template to replace inline prompt, (2) added explicit 5-band rubric with concrete signal examples per band (0.95+ = perfect CSV, 0.85-0.94 = minor ambiguity, 0.70-0.84 = mixed, 0.50-0.69 = uncertain, <0.50 = noise/code snippets), (3) strengthened DTO `[Description]` attributes to emphasize "USE THE FULL RANGE — do NOT cluster at 0.85+", (4) added guard rails (short samples cap at 0.80, any garbage lines cap at 0.60). Key learning: **AI prompt calibration requires CONCRETE EXAMPLES not abstract qualitative guidance.** Vague rubrics like "strong signals" cause score clustering because the model has no anchors. Decision drop at `.squad/decisions/inbox/river-import-confidence-calibration.md`.

### Phase 1 Korean Number Generator & Grader (2026-05-04)

**Scope:** Deterministic rule-based generator + 7-class grader (Wave 4)

**Key Patterns:**

1. **Rule-based Generation > LLM** — Korean numbers are procedural (Sino/Native system selection, sound morphology, counter association). LLM would add latency/cost/hallucination. Deterministic rules + offline capability essential. Generator designed for future Japanese/Mandarin/Spanish plug-ins by isolating language-specific logic.

2. **Sound-Change Rule Context Dependency** — Korean morphophonology is subtle:
   - `둘→두` applies ONLY standalone (두 명), NOT in compounds (스물둘, 열둘)
   - `스물→스무` ONLY at exactly 20 (스무 살), NOT 21-29 (스물하나 살)
   - Compounds keep full forms
   - Ruleset must encode context predicates, not just target → replacement mappings

3. **Error-Class Taxonomy Enables Pedagogy** — 7 classes (SinoNativeSwap, CounterMismatch, SoundChangeMissed, MagnitudeOff10x, Typo, WrongFormat, Unknown) with prioritized detection order. CounterMismatch checked FIRST to prevent false positives when both counter and system are wrong. Every error class has a pedagogical tip (e.g., "Native is used with counters" for SinoNativeSwap).

4. **Permissive Normalization + Exact Validation** — Grader accepts spacing variants, full-width digit conversion, case-insensitive matching. BUT accepts associations (alternates list), never penalizes spelling without corrected_text via CanonicalAnswer field. Typo detection via Levenshtein distance.

5. **Interface-First Design for Generalization** — `INumberItemGenerator` and `INumberAnswerGrader` interfaces keep language-specific implementations isolated. Core grader, normalization, and error taxonomy reused. Per-language plug-ins override sound-change rules (tone sandhi for Mandarin, rendaku for Japanese, apocope for Spanish).

6. **Determinism Via RandomSeed** — Same seed → same output. Enables testability: iteration over seeds 0–1000 to find specific values (e.g., "find a seed that produces 스물 in Age context").

---

### Phase 2 NumberDrill Seed Expansion (2026-05-04)

**Scope:** Extended `lib/content/numbers/ko.json` with Money, Date, Ordinal contexts

**Deliverables:**
1. Three new context entries: Money (Sino, 💰), Date (Sino, 📅), Ordinal (Native, 🏆)
2. `contextNotes` metadata section with irregular month flags and ordinal pattern documentation

**Key Learnings:**

1. **Korean Date Irregularities Must Be Explicit** — Month 6 is 유월 (NOT 육월), month 10 is 시월 (NOT 십월). These irregular forms must be flagged in seed metadata so the generator can:
   - Use correct forms in item generation
   - Detect learner errors (e.g., "육월" when "유월" expected) as a dedicated error class
   - Provide pedagogical tips ("June uses irregular form 유월")

2. **Money Place-Value Grouping Is Cultural** — Korean groups by 4 digits (만 = 10,000; 억 = 100,000,000) vs. Western 3-digit grouping (thousand, million). Seed includes explicit place-value mappings (만: 10000, 억: 100000000) and sample ranges with conversational contexts (커피 = 3천 원, 월세 = 백만 원). This enables generator to teach Korean-native thinking patterns, not transliterated Western ones.

3. **Ordinal Dual-Pattern Requires Contextual Selection** — Korean ordinals have two productive patterns:
   - **Native + 째** (첫째, 둘째, 셋째…) for ranking/birth-order/sequence
   - **Native + 번째** (첫 번째, 두 번째, 세 번째…) for occurrences/"Nth time"
   - 첫째 is irregular (NOT 하나째) — similar to Time's 한 시 irregularity
   - Generator needs to bias by sub-mode: Ranking contexts → 째; Occurrence contexts → 번째

4. **Schema Extension via `contextNotes` Section** — Phase 1 seed had no extensibility for context-specific metadata (only generic `counters` array). Phase 2 adds `contextNotes` as a top-level object keyed by context code. This allows:
   - Irregular forms (Date.irregularMonths)
   - Sample data with semantic context tags (Money.ranges[].context)
   - Multi-pattern documentation (Ordinal.patterns)
   - Notes field for generator implementer guidance
   - NO DTO change required — seeder ignores unknown fields, generator reads raw JSON for context-specific logic

5. **Embedded Resource Verification Pattern** — After extending seed:
   - Validate JSON syntax (python3 -m json.tool)
   - Build Shared project to confirm embedded resource inclusion
   - Check for deserialization errors in seeder (would manifest as "Failed to deserialize" log warning)
   - Phase 2 extension passed all three gates — no schema regression

**Coordination:**
- Wash will implement Money/Date/Ordinal generation logic in `KoreanNumberItemGenerator.cs` (next Phase 2 todo)
- Jayne will test irregular month detection and ordinal pattern disambiguation (Phase 2 validation suite expansion)
- Captain approved icon choices (💰/📅/🏆) and defaultSystem assignments (Sino/Sino/Native)

**Implications:**
- NumberDrill is now A1-A2 complete for Korean numbers (Counting/Time/Age/Money/Date/Ordinal = 6 contexts)
- Phase 3 day-counts (하루/이틀/사흘) deferred per Captain's decision — dual-home with VocabularyWord, not a number context
- Generalization path to Japanese/Mandarin/Spanish now has ordinal exemplar (Korean 째/번째 ≈ Spanish -o/-a gender + placement variants)

---



- 2026-05-05: **NumberDrill Phase 2 Wave 1 — Content Seed Expansion** — Extended `lib/content/numbers/ko.json` with three new contexts for Phase 2: Money (💰, Sino, sortOrder 40), Date (📅, Sino, sortOrder 50), Ordinal (🏆, Native, sortOrder 60). Introduced `contextNotes` schema extension (top-level object with context-specific metadata) for generator/grader consumption — backward-compatible (seeder ignores unknown fields). Money: place values (만, 억), ranges (100원–1M원), particle (원). Date: irregular months (유월/시월), all 12 months with romanization, holidays, year format. Ordinal: dual patterns (째 for ranking, 번째 for occurrences), irregularity (첫째). Build validated ✓. Generalization path: contextNotes schema reusable for Japanese (phonetic variants), Mandarin (currency variants), Spanish (gender agreement). Handoff: Wash implements generators + graders (Money/Date/Ordinal item generation + new error classes), Kaylee implements Disambiguate generator for paired prompts. Day-counts explicitly deferred to Phase 3 (lexical, not productive pattern). Decision drop: `.squad/decisions/inbox/river-numberdrill-phase2-seed.md`.
