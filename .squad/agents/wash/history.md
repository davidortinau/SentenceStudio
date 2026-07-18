# Wash — History Summary

**Summarized by Scribe:** 2026-07-17T20-10-00-0500
**Project:** SentenceStudio — .NET MAUI Blazor Hybrid language learning app
**Role focus:** backend services, repositories, EF migrations, data safety, API/data-layer foundations.

## Durable backend patterns

- Treat mobile-vs-API asymmetry as a recurring failure mode. When both paths persist the same entity, audit both before shipping; DailyPlan focus vocabulary/narrative/rationale work proved CoreSync can propagate mobile omissions back to Postgres.
- Multi-tenant repository reads and writes fail closed on empty or foreign `userId`; return empty/null/false and log rather than falling through to unscoped queries.
- Dual-provider migrations require PostgreSQL and SQLite copies with matching IDs; SQLite hand-written migrations must include discovery attributes and be verified through EF discovery/application, not only DDL validity.
- For activity resume/progress, prefer explicit user/context keys, database-enforced uniqueness where possible, race-safe update paths, and monotonic merge semantics for lifetime counters.

## Key delivered foundations

- NumberDrill Phase 1 data model, grading v2 support, Korean number normalization, TTS audio cache with concurrent prewarm, and ElevenLabs integration.
- Atomic content import fix in `CommitImportAsync`, eliminating orphaned resources by consolidating persistence into one save.
- Focus vocabulary persistence across deterministic plan generation, DailyPlan JSON facts, CoreSync, and route plumbing.
- Per-user timezone plan-staleness fix: `UserProfile.IanaTimeZoneId`, dual-provider migration, WebAppPlanDateContext, TimeZoneCapture, UTC normalization, and freshness checks.
- Quick-add existing vocabulary lookup: user-scoped strict-language search excluding already-mapped words.
- ElevenLabs latency improvement: Flash v2.5 for short-form synthesis, Reading remains long-form model, and cached known voice construction.
- ActivitySession entity/service foundation with user/context-scoped save, complete, abandon, duplicate handling, and exact Vocab Quiz resume support.
- Quiz demonstration counters persisted on `VocabularyProgress`, dual migrations, monotonic duplicate merge/replay, and focus-word rotation updates.
- Transcript example sentence persistence and retroactive harvesting via `ExampleSentenceRepository.CreateFromReadingIfNewAsync` and `TranscriptSentenceHarvestService`.
- Vocab Quiz sentence hint data foundation: `GetQuizHintsForWordsAsync` explicit-user batch projection through exact `ResourceVocabularyMapping` ownership, target-only hints, 20-ID limit, and deterministic CEFR ranking.
- Cross-profile disclosure data-layer fix: explicit-user resource/skill reads, exact-ID ownership rejection, caller propagation, and two-profile SQLite regression coverage.

## Carry-forward

- File the dotnet/efcore issue for `--framework` not isolating TFM evaluation in multi-targeted projects with conditional Compile Remove.
- Do not relax strict language filtering for quick-add `Language=NULL` rows without a product/data-quality decision.
- Future migration gates must fail closed when DevFlow attaches to the wrong agent or logs are unavailable.
- Preserve quiz demonstration counters as monotonic lifetime history during duplicate merge/replay.
