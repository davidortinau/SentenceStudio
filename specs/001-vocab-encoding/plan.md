# Implementation Plan: Vocabulary Encoding Enhancements

**Branch**: `001-vocab-encoding` | **Date**: 2025-12-11 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `specs/001-vocab-encoding/spec.md`

**Note**: This plan emphasizes SQLite performance optimization for mobile devices, avoiding LINQ query performance pitfalls.

## Summary

Add vocabulary encoding features to improve memorization through proven cognitive techniques: lemmas for dictionary forms, tags for categorization, mnemonics with images for memory associations, and example sentences for contextual learning. Technical approach extends existing VocabularyWord table with 4 new fields (single migration), adds new ExampleSentence table with foreign key relationships, and implements derived encoding strength calculation. **Critical focus**: Optimize SQLite queries for mobile performance by using compiled queries, proper indexing, and avoiding N+1 patterns that plague LINQ lazy evaluation.

## Technical Context

**Language/Version**: .NET 10.0 (net10.0-android, net10.0-ios, net10.0-maccatalyst, net10.0-windows10.0.19041.0)  
**Primary Dependencies**: .NET MAUI 10.0.20+, MauiReactor, SQLite-net-pcl 1.9.x, Entity Framework Core 9.0+, Microsoft.Extensions.AI, ElevenLabs SDK  
**Storage**: SQLite local database (ApplicationDbContext) with CoreSync synchronization support  
**Testing**: xUnit for unit tests, manual platform-specific testing for UI  
**Target Platform**: iOS 12.2+, Android API 21+, macOS 15.0+, Windows 10.0.17763.0+  
**Project Type**: Multi-platform MAUI application (mobile + desktop)  
**Performance Goals**: <3s startup, <100ms UI response, <500ms AI API calls, **<50ms tag filtering**, **<200ms encoding strength calculation for 1000+ words**  
**Constraints**: Must work offline, all platforms tested, ILogger for production logs, Theme-first styling, **SQLite query optimization mandatory** (avoid LINQ N+1, use compiled queries, proper indexes)  
**Scale/Scope**: Single-user language learning app supporting 5000+ vocabulary words with encoding metadata

### SQLite Performance Requirements (Mobile-First)

**CRITICAL**: This feature adds filtering and calculated fields that can easily cause performance issues on mobile devices if not optimized properly.

**Mandatory Optimizations:**
1. **Indexes**: Create SQLite indexes on VocabularyWord.Tags (for LIKE queries), ExampleSentence.VocabularyWordId (foreign key), ExampleSentence.IsCore (filtering)
2. **Compiled Queries**: Use EF Core compiled queries for repeated tag filtering and encoding strength calculations
3. **Avoid N+1**: Never use `.Include()` in loops; batch load related data with single queries using IN clauses
4. **Projection**: Select only needed columns for list views (avoid loading full entities with navigation properties)
5. **Pagination**: Implement skip/take for large vocabulary lists (mobile devices struggle with rendering 1000+ items)
6. **Async All The Way**: All database operations must be async to avoid UI thread blocking

**Known LINQ Performance Anti-Patterns to Avoid:**
- ❌ `.Where(w => w.Tags.Contains(tag))` - causes table scan without index
- ✅ `.Where(w => EF.Functions.Like(w.Tags, $"%{tag}%"))` with index on Tags column
- ❌ Loading vocabulary words then `.Include(w => w.ExampleSentences)` in a loop
- ✅ Single batch query: `db.ExampleSentences.Where(es => wordIds.Contains(es.VocabularyWordId))`
- ❌ Calculating encoding strength in C# for every word in a 1000+ word list
- ✅ Materialized calculated column or in-memory caching for recently accessed words

**Benchmarking Targets:**
- Tag filtering on 5000 words: <50ms (with index)
- Loading 50 vocabulary words with example sentence counts: <100ms (batch query)
- Encoding strength calculation for 100 words: <30ms (cached or compiled query)

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

Verify alignment with SentenceStudio Constitution (`.specify/memory/constitution.md`):

- [x] **User-Centric AI-Powered Learning**: ✅ Enhances custom vocabulary with proven encoding techniques (mnemonics, tags, context sentences)
- [x] **Cross-Platform Native**: ✅ SQLite-based offline storage works identically on iOS, Android, macOS, Windows
- [x] **MauiReactor MVU**: ✅ UI components will use semantic methods (`.HStart()`, `.VCenter()`, etc.) and theme-based styling
- [x] **Theme-First UI**: ✅ All new UI elements (tag badges, encoding indicators, example sentence cards) will use `.ThemeKey()` or MyTheme constants
- [x] **Localization by Default**: ✅ All UI strings (field labels, encoding strength indicators, buttons) will use `$"{_localize["Key"]}"` pattern
- [x] **Observability**: ✅ `ILogger<T>` will be used for database operations, query performance tracking, and encoding calculations
- [x] **Documentation in docs/**: ✅ All specs, plans, and guides are in `specs/001-vocab-encoding/` (docs/ equivalent)

**Violations requiring justification**: None

**Performance Considerations**: Constitution requires <100ms UI response. Tag filtering and encoding strength calculations could violate this without proper SQLite optimization (indexes, compiled queries, batching). This plan addresses performance proactively with mandatory optimization guidelines.

## Project Structure

### Documentation (this feature)

```text
specs/001-vocab-encoding/
├── spec.md              # Feature specification (completed)
├── plan.md              # This file (in progress)
├── research.md          # Phase 0 output: SQLite optimization patterns, tag storage strategies
├── data-model.md        # Phase 1 output: Entity schemas, relationships, indexes
├── quickstart.md        # Phase 1 output: Developer guide for encoding features
├── contracts/           # Phase 1 output: Repository interfaces, DTOs
│   ├── IVocabularyEncodingRepository.cs.md
│   └── EncodingStrengthCalculator.cs.md
└── checklists/
    └── requirements.md  # Specification validation (completed)
```

### Source Code (repository root)

```text
src/
├── SentenceStudio.Shared/
│   ├── Models/
│   │   ├── VocabularyWord.cs          # EXTEND: Add Lemma, Tags, MnemonicText, MnemonicImageUri
│   │   └── ExampleSentence.cs         # NEW: Target/native sentences, audio, core flag
│   └── Data/
│       └── ApplicationDbContext.cs     # EXTEND: Add ExampleSentences DbSet, configure indexes
│
├── SentenceStudio/
│   ├── Data/
│   │   ├── VocabularyEncodingRepository.cs      # NEW: Optimized queries for tags, encoding
│   │   └── ExampleSentenceRepository.cs         # NEW: CRUD for example sentences
│   │
│   ├── Services/
│   │   ├── EncodingStrengthCalculator.cs        # NEW: Derived encoding score (0-1.0)
│   │   └── VocabularyFilterService.cs           # NEW: Tag filtering with compiled queries
│   │
│   ├── Pages/
│   │   ├── VocabularyDetail/
│   │   │   ├── VocabularyDetailPage.cs          # EXTEND: Show encoding metadata
│   │   │   ├── VocabularyEditPage.cs            # EXTEND: Edit tags, mnemonics, lemmas
│   │   │   └── ExampleSentenceEditor.cs         # NEW: Add/edit example sentences
│   │   │
│   │   └── VocabularyList/
│   │       ├── VocabularyListPage.cs            # EXTEND: Tag filter, encoding sort
│   │       └── VocabularyListViewModel.cs       # EXTEND: Filtering logic
│   │
│   └── Resources/Strings/
│       ├── Resources.resx                        # NEW: Encoding UI strings (EN)
│       └── Resources.ko.resx                     # NEW: Encoding UI strings (KO)
│
└── Migrations/
    └── AddVocabularyEncodingFields.cs            # NEW: Migration for schema changes

tests/
├── SentenceStudio.Tests/
│   ├── EncodingStrengthCalculatorTests.cs        # NEW: Unit tests for scoring
│   ├── VocabularyEncodingRepositoryTests.cs     # NEW: Query performance tests
│   └── TagFilteringPerformanceTests.cs          # NEW: Benchmark tag queries
```

**Structure Decision**: MAUI multi-platform application. Database changes use Entity Framework Core migrations. Performance-critical tag filtering implemented with compiled queries and SQLite indexes. Encoding strength calculation is a derived service (not persisted) to avoid schema complexity.

**UI Changes**:
- **Extend EditVocabularyWordPage**: Add fields for Lemma, Tags, Mnemonic Text, Mnemonic Image URI to existing `RenderWordForm()` section
- **New Section in EditVocabularyWordPage**: `RenderExampleSentences()` component to display, add, edit, delete example sentences inline
- **Encoding Strength Indicator**: Display calculated encoding strength badge in vocabulary detail views
- **Tag Badges**: Render clickable tag badges in vocabulary list and detail views (filter on click)

## Complexity Tracking

> **Fill ONLY if Constitution Check has violations that must be justified**

**No violations detected.** All constitution principles are satisfied by this feature design.

## Phase 0: Research Summary

**Status**: ✅ COMPLETE  
**Document**: [research.md](research.md)

**Key Decisions**:
1. **Tag Storage**: Comma-separated strings with SQLite LIKE + index (avoid junction table complexity)
2. **Compiled Queries**: Use EF Core compiled queries for tag filtering (40% faster for hot path)
3. **Batch Loading**: Single query for example sentence counts to prevent N+1 anti-pattern
4. **Encoding Strength**: Derived calculation in-memory (not persisted) for simplicity and correctness
5. **Index Strategy**: Focused indexes on Tags, Lemma, VocabularyWordId, IsCore for mobile optimization
6. **Pagination**: Skip/take with CollectionView virtualization for 5000+ word support

**Performance Targets Established**:
- Tag filtering: <50ms for 5000 words with index
- Example sentence counts: <100ms for 50 words (batch query)
- Encoding strength: <30ms for 100 words (in-memory calculation)

## Phase 1: Design Summary

**Status**: ✅ COMPLETE  
**Documents**: [data-model.md](data-model.md), [contracts/](contracts/), [quickstart.md](quickstart.md)

**Database Changes**:
- **2 Migrations**: AddVocabularyEncodingFields, CreateExampleSentenceTable
- **New Entity**: ExampleSentence (target/native sentences, audio, core flag)
- **Extended Entity**: VocabularyWord (lemma, tags, mnemonic text/image, audio)
- **6 Indexes**: Tags, Lemma, VocabularyWordId, IsCore, composite (VocabId+IsCore), LearningResourceId

**Service Interfaces**:
- `IVocabularyEncodingRepository`: Tag filtering, lemma search, encoding metadata CRUD
- `IExampleSentenceRepository`: Example sentence CRUD, batch count loading
- `IEncodingStrengthCalculator`: Derived score calculation (0-1.0 → Basic/Good/Strong)

**Performance Optimizations**:
- Compiled queries for repeated tag filtering
- Batch loading for example sentence counts (prevents N+1)
- Focused SQLite indexes for mobile I/O efficiency
- Pagination for unbounded vocabulary lists

### Constitution Re-Check (Post-Design)

*GATE: Re-validate constitution alignment after Phase 1 design*

- [x] **User-Centric AI-Powered Learning**: ✅ Encoding features support proven memory techniques without AI dependency
- [x] **Cross-Platform Native**: ✅ SQLite schema and compiled queries work identically on all platforms
- [x] **MauiReactor MVU**: ✅ UI components designed with semantic methods and fluent chains
- [x] **Theme-First UI**: ✅ All UI elements use `.ThemeKey()` or MyTheme constants (see quickstart.md examples)
- [x] **Localization by Default**: ✅ All user-facing strings use `$"{_localize["Key"]}"` pattern
- [x] **Observability**: ✅ `ILogger<T>` used for repository operations and performance tracking
- [x] **Documentation in docs/**: ✅ All artifacts in `specs/001-vocab-encoding/`

**Performance Gate**: Constitution requires <100ms UI response. Design targets are:
- Tag filtering: 50ms (50% margin)
- Encoding calculation: 30ms (70% margin)
- Example counts: 100ms (at limit, requires testing)

**Action**: Benchmark example sentence count loading on mid-range Android device. If >100ms, add caching layer.

## Phase 2: Implementation Tasks

**Status**: ⏸️ PENDING  
**Next Command**: `/speckit.tasks` to generate task breakdown

**Estimated Task Count**: ~35-40 tasks across 4 user stories (P1-P3)

**Implementation Order**:
1. **Phase 1** (Setup): Migrations, model updates, index creation
2. **Phase 2** (Foundational): Repositories, compiled queries, encoding calculator
3. **Phase 3** (US1 - Memory Aids): Edit UI for tags/mnemonics/images, encoding indicator
4. **Phase 4** (US2 - Example Sentences): Example sentence editor, audio generation integration
5. **Phase 5** (US3 - Filtering): Tag filter UI, encoding strength sort
6. **Phase 6** (US4 - Lemma Storage): Lemma field in edit UI, search by lemma
7. **Phase 7** (Polish): Performance testing, localization, cross-platform verification

## Risk Assessment

| Risk | Likelihood | Impact | Mitigation |
|------|------------|--------|------------|
| Tag filtering >50ms on low-end devices | Medium | High | Index on Tags column; compiled query; pagination |
| N+1 queries for example counts | High | High | Batch loading pattern documented in quickstart.md |
| Users add >10 tags per word | Low | Medium | UI validation limits to 10 tags; database constraint at 500 chars |
| Encoding strength calculation overhead | Low | Low | Pure function ~1μs per call; batch operation for lists |
| CoreSync conflicts with new tables | Medium | Medium | Test sync after migration; follow CoreSync singular table naming |

**Critical Path**: Tag filtering performance depends on SQLite index creation. Must verify index exists in migration before testing.

## Success Metrics (Aligned with Spec)

- **SC-001**: Add memory aids in under 2 minutes ✅ (UI design supports this)
- **SC-002**: 3x increase in "Strong" encoding words ⏸️ (measure after 30-day deployment)
- **SC-003**: Tag filtering results under 1 second ✅ (target <50ms with index)
- **SC-004**: 80% of words with sentences have "Core" examples ⏸️ (monitor user behavior)
- **SC-005**: Add 2-3 example sentences in under 3 minutes ✅ (UI supports inline editing)
- **SC-006**: Real-time encoding strength indicator updates ✅ (derived calculation ensures this)
- **SC-007**: 50% reduction in related word discovery time ⏸️ (requires user timing study)
- **SC-008**: 95% audio generation success rate ✅ (existing audio infrastructure proven)

## Next Steps

1. **Run**: `/speckit.tasks` to generate detailed task breakdown
2. **Create Feature Branch**: `001-vocab-encoding` (already created)
3. **Run Migrations**: `dotnet ef migrations add ...` in SentenceStudio.Shared
4. **Implement Repositories**: Start with VocabularyEncodingRepository (compiled query)
5. **Add UI Components**: VocabularyEditPage extensions for encoding metadata
6. **Performance Test**: Benchmark tag filtering with 5000 words on Android device
7. **Cross-Platform Test**: Verify on iOS, Android, macOS, Windows

**Branch**: `001-vocab-encoding`  
**Spec**: [spec.md](spec.md)  
**Artifacts**: research.md, data-model.md, contracts/, quickstart.md (all complete)
