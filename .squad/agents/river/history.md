# River — History Summary

**Summarized by Scribe:** 2026-06-10T03:35:00Z
**Project:** SentenceStudio — .NET MAUI Blazor Hybrid language learning app
**Role focus:** AI prompts, content generation, language data modeling, NumberDrill generation/grading inputs.

## Durable prompt and AI lessons

- Word/Phrase/Sentence extraction was completed by adding LexicalUnitType guidance to transcript extraction prompts and mapping classification into `ExtractedVocabularyItem`. Korean-specific rules and related-term hints are part of the prompt contract.
- Import pipeline convergence: Phrases and Sentences need dedicated extraction prompts, while response shape stays stable as `{ "vocabulary": [...] }` for Wash's backend wire-up. Pipe-delimited Korean|English examples from Captain are canonical few-shot anchors.
- AI classifier confidence calibration requires concrete score bands and examples, not abstract labels such as strong or mixed. Use full-range rubrics, short-sample caps, garbage-line caps, and `[Description]` attributes that explicitly discourage 0.85 clustering.
- Microsoft.Extensions.AI and related SDK claims must be verified against Microsoft sources. `UseRateLimitRetry()` was a hallucinated API; VectorData was real; Realtime/TTS were experimental and not enough to drive immediate adoption.

## NumberDrill content and generation patterns

- Phase 1 Korean number generation/grading shipped with 33/33 tests passing. Generator/grader work owns the language rules behind UI modes.
- Phase 2 seed expansion added Money, Date, and Ordinal contexts via `lib/content/numbers/ko.json` and a backward-compatible `contextNotes` schema. The schema captures context-specific metadata such as place values, irregular months, ordinal patterns, and currency particles.
- Day-counts were explicitly deferred as lexical, not productive pattern work.
- Context notes should be designed for future language portability: Japanese phonetic variants, Mandarin currency variants, and Spanish agreement are examples of the generalization path.

## Coordination patterns

- River hands off prompt/seed contracts to Wash for backend implementation, Kaylee for UI, and Jayne for E2E/test matrices.
- Keep prompt response schemas stable unless all downstream consumers are updated in the same batch.
- Dogfooding directive applies: if framework/tooling friction blocks validation, document the root cause and workaround rather than silently narrowing test scope.

---

Team update (2026-06-17T15:10:57-05:00): Mastery calibration + plan staleness dual RCA completed — decided by Zoe.

Concern #1: CALIBRATION BUG in VocabularyProgressService.cs:27. The /12 divisor for streak-based mastery score was tuned for in-session rotation pacing but also governs the lifetime IsKnown gate and displayed mastery. Fix: SRS-interval-aware IsKnown pathway. Words with ReviewInterval >= 60 days, Accuracy >= 0.80, and at least 1 production attempt are now Known. Display mastery adds srsBonus using the prior interval.

Concern #2: TIMEZONE BUG in Webapp's IPlanDateContext. Azure Linux server resolves TimeZoneInfo.Local as UTC, which is 5 hours ahead of Captain (CDT). Between 7pm-midnight CDT, the server pre-generates next-day's plan with stale vocabulary, pins it in Postgres, and the morning short-circuit serves it without freshness validation. Fix pending Captain's Query 4 confirmation from production Postgres.

Baseline: 636/636 tests passing. New test file (19 tests): tests/SentenceStudio.UnitTests/Services/MasteryScoring/MasteryCalibrationCharacterizationTests.cs — untracked, awaiting Captain's commit.
