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

