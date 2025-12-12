# Implementation Tasks: Vocabulary Encoding Enhancements

**Feature**: 001-vocab-encoding  
**Branch**: `001-vocab-encoding`  
**Created**: 2025-12-11  
**Spec**: [spec.md](spec.md) | **Plan**: [plan.md](plan.md)

## Task Summary

- **Total Tasks**: 48
- **Setup Phase**: 3 tasks
- **Foundational Phase**: 10 tasks (MUST complete before user stories)
- **User Story 1 (P1)**: 12 tasks
- **User Story 2 (P2)**: 9 tasks
- **User Story 3 (P3)**: 8 tasks
- **User Story 4 (P3)**: 3 tasks
- **Polish Phase**: 3 tasks

## User Story Dependencies

```
Phase 2 (Foundational) → MUST complete before any user story
    ↓
User Story 1 (P1) → Independent (MVP)
    ↓ (optional dependency)
User Story 2 (P2) → Depends on US1 (uses encoding metadata)
    ↓ (optional dependency)
User Story 3 (P3) → Depends on US1, US2 (filters encoding data)
    ↓ (optional dependency)
User Story 4 (P3) → Independent of other user stories
```

**MVP Recommendation**: Implement User Story 1 only for initial release. It delivers core value (memory encoding) and is independently testable.

## Parallel Execution Opportunities

### Phase 2 (Foundational)
Tasks T006-T010 can run in parallel (different files, no dependencies):
- VocabularyEncodingRepository + ExampleSentenceRepository + EncodingStrengthCalculator
- Service registration can happen after all three complete

### User Story 1 (P1)
Tasks T016-T020 can run in parallel (different UI sections):
- Encoding strength indicator + Tag badge rendering + Mnemonic image preview

### User Story 2 (P2)
Tasks T024-T026 can run in parallel (different methods):
- Audio generation + Core toggle + Delete operations

### User Story 3 (P3)
Tasks T034-T036 can run in parallel (different UI components):
- Tag filter UI + Encoding sort UI + Inline indicators

---

## Phase 1: Setup (Project Initialization)

**Purpose**: Prepare development environment and branch structure

- [X] T001 Verify feature branch `001-vocab-encoding` is checked out
- [X] T002 Verify .NET 10.0 MAUI workloads are installed (`dotnet workload list`)
- [X] T003 Backup existing SQLite database (copy from FileSystem.AppDataDirectory to safe location)

**Checkpoint**: ✅ Development environment ready, database backed up

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Database schema and core services MUST be complete before ANY user story implementation

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

- [X] T004 [P] Create migration AddVocabularyEncodingFields in `src/SentenceStudio.Shared/Migrations/` to add Lemma, Tags, MnemonicText, MnemonicImageUri, AudioPronunciationUri columns to VocabularyWord table with indexes on Tags and Lemma
- [X] T005 [P] Create migration CreateExampleSentenceTable in `src/SentenceStudio.Shared/Migrations/` to create ExampleSentence table with foreign keys to VocabularyWord and LearningResource, indexes on VocabularyWordId, IsCore, and composite (VocabularyWordId, IsCore)
- [X] T006 [P] Update VocabularyWord model in `src/SentenceStudio.Shared/Models/VocabularyWord.cs` to add ObservableProperty fields: Lemma, Tags, MnemonicText, MnemonicImageUri, AudioPronunciationUri, ExampleSentences navigation property, and NotMapped EncodingStrength/EncodingStrengthLabel properties
- [X] T007 [P] Create ExampleSentence model in `src/SentenceStudio.Shared/Models/ExampleSentence.cs` with properties: Id, VocabularyWordId, LearningResourceId, TargetSentence, NativeSentence, AudioUri, IsCore, CreatedAt, UpdatedAt, and navigation properties to VocabularyWord and LearningResource
- [X] T008 [P] Update ApplicationDbContext in `src/SentenceStudio.Shared/Data/ApplicationDbContext.cs` to add ExampleSentences DbSet and configure relationships: VocabularyWord.ExampleSentences (one-to-many with cascade delete), ExampleSentence.LearningResource (many-to-one with set null on delete)
- [X] T009 [P] Implement VocabularyEncodingRepository in `src/SentenceStudio/Data/VocabularyEncodingRepository.cs` with compiled query for FilterByTagAsync using EF.Functions.Like, batch loading in GetWithEncodingStrengthAsync to avoid N+1, and SearchByLemmaAsync with index lookup
- [X] T010 [P] Implement ExampleSentenceRepository in `src/SentenceStudio/Data/ExampleSentenceRepository.cs` with methods: GetByVocabularyWordIdAsync, GetCoreExamplesAsync, GetCountsByVocabularyWordIdsAsync (batch query with GroupBy), CreateAsync, UpdateAsync, DeleteAsync, SetCoreAsync
- [X] T011 [P] Implement EncodingStrengthCalculator service in `src/SentenceStudio/Services/EncodingStrengthCalculator.cs` with Calculate(VocabularyWord, int exampleCount) method checking 6 factors, GetLabel(double score) returning Basic/Good/Strong, and CalculateBatch for bulk operations
- [X] T012 Register repositories and calculator in `src/SentenceStudio/MauiProgram.cs`: AddSingleton for EncodingStrengthCalculator, ExampleSentenceRepository, VocabularyEncodingRepository
- [X] T013 Run migrations on all platforms (iOS, Android, macOS, Windows) using `dotnet build -t:Run -f <TFM>` and verify database schema updated with new columns, table, and indexes using SQLite browser or query

**Checkpoint**: ✅ Foundation ready - database schema updated, repositories implemented with performance optimizations (compiled queries, batch loading, indexes), user story implementation can now begin

---

## Phase 3: User Story 1 - Add Memory Aids to Vocabulary Words (Priority: P1)

**Goal**: Enable users to add mnemonics, images, and tags to vocabulary words with encoding strength indicator

**Independent Test**: Edit an existing vocabulary word, add mnemonic "단풍 sounds like 'don't pong' - leaves that don't smell bad in fall", add image URL, add tags "nature,season,visual". Verify fields save correctly, display on detail page, and encoding strength indicator shows "Strong" (67-100% complete).

### Implementation for User Story 1

- [X] T014 [P] [US1] Add encoding metadata fields to EditVocabularyWordPageState in `src/SentenceStudio/Pages/VocabularyManagement/EditVocabularyWordPage.cs`: Lemma, Tags, MnemonicText, MnemonicImageUri (string properties with empty string defaults)
- [X] T015 [US1] Extend RenderWordForm() in `src/SentenceStudio/Pages/VocabularyManagement/EditVocabularyWordPage.cs` to add Lemma field (Entry with VocabLemmaLabel and placeholder), Tags field (Entry with VocabTagsLabel and comma-separated placeholder), MnemonicText field (Editor with 100px height), MnemonicImageUri field (Entry with Keyboard.Url)
- [X] T016 [P] [US1] Add RenderEncodingStrength() method in `src/SentenceStudio/Pages/VocabularyManagement/EditVocabularyWordPage.cs` to calculate score using _encodingCalculator.Calculate(), display colored badge (Warning for Basic, Gray for Good, Success for Strong) with localized label
- [X] T017 [P] [US1] Add RenderTagBadges() method in `src/SentenceStudio/Pages/VocabularyManagement/EditVocabularyWordPage.cs` to split comma-separated tags, render as individual Border badges with MyTheme.Gray200 background, rounded corners
- [X] T018 [P] [US1] Add conditional mnemonic image preview in RenderWordForm() in `src/SentenceStudio/Pages/VocabularyManagement/EditVocabularyWordPage.cs`: Image control with Source bound to MnemonicImageUri, HeightRequest 200, Aspect.AspectFit, only displayed if URI is not empty
- [X] T019 [US1] Update SaveVocabularyWord() in `src/SentenceStudio/Pages/VocabularyManagement/EditVocabularyWordPage.cs` to persist Lemma, Tags, MnemonicText, MnemonicImageUri to State.Word before calling repository SaveAsync, log success with ILogger
- [X] T020 [US1] Update LoadData() in `src/SentenceStudio/Pages/VocabularyManagement/EditVocabularyWordPage.cs` to load encoding metadata from State.Word into form fields (Lemma, Tags, MnemonicText, MnemonicImageUri) when editing existing word
- [X] T021 [US1] Add localization keys to `src/SentenceStudio/Resources/Strings/AppResources.resx` (English): VocabLemmaLabel, VocabLemmaPlaceholder, VocabTagsLabel, VocabTagsPlaceholder, VocabMnemonicLabel, VocabMnemonicPlaceholder, VocabImageUrlLabel, VocabImageUrlPlaceholder, EncodingStrength, EncodingStrengthBasic, EncodingStrengthGood, EncodingStrengthStrong
- [X] T022 [US1] Add localization keys to `src/SentenceStudio/Resources/Strings/AppResources.ko-KR.resx` (Korean): Translate all encoding-related keys added in T021
- [X] T023 [US1] Add tag input validation in EditVocabularyWordPage.cs to limit to 10 tags max (split by comma, count, display error message if exceeded) before saving
- [X] T024 [US1] Inject EncodingStrengthCalculator into EditVocabularyWordPage.cs and verify RenderEncodingStrength() displays correct indicator based on filled fields
- [X] T025 [US1] Test on all platforms (iOS, Android, macOS, Windows): Add mnemonic, tags, image to word, verify save, reload page, verify data persists, verify encoding indicator updates correctly

**Checkpoint**: ✅ User Story 1 complete and independently testable - users can add memory aids and see encoding strength

---

## Phase 4: User Story 2 - Add Example Sentences with Context (Priority: P2)

**Goal**: Enable users to add example sentences with translations and audio to vocabulary words

**Independent Test**: Select a vocabulary word, add 2-3 example sentences in target language with native translations, mark one as "core" example, generate audio for one sentence. Verify sentences display in list with audio playback buttons and core indicator.

**Dependencies**: Requires User Story 1 (encoding metadata) for encoding strength calculation to include example sentence counts

### Implementation for User Story 2

- [X] T026 [P] [US2] Add example sentence state to EditVocabularyWordPageState in `src/SentenceStudio/Pages/VocabularyManagement/EditVocabularyWordPage.cs`: ExampleSentences list, IsEditingSentence bool, EditingSentence object
- [X] T027 [US2] Add RenderExampleSentences() method in `src/SentenceStudio/Pages/VocabularyManagement/EditVocabularyWordPage.cs` to render list of existing sentences with target/native display, audio play button, core indicator badge (⭐), edit/delete buttons, each wrapped in ThemeKey CardStyle Border
- [X] T028 [US2] Add inline example sentence editor in RenderExampleSentences() in `src/SentenceStudio/Pages/VocabularyManagement/EditVocabularyWordPage.cs`: Entry for target sentence, Entry for native translation, CheckBox for IsCore, Save/Cancel buttons, conditional rendering based on IsEditingSentence flag
- [X] T029 [US2] Implement SaveExampleSentenceAsync() in `src/SentenceStudio/Pages/VocabularyManagement/EditVocabularyWordPage.cs` to validate target sentence not empty, set VocabularyWordId and timestamps, call _exampleSentenceRepo.CreateAsync or UpdateAsync, update State.ExampleSentences list, log with ILogger
- [X] T030 [P] [US2] Implement GenerateSentenceAudioAsync() in `src/SentenceStudio/Pages/VocabularyManagement/EditVocabularyWordPage.cs` to call _speechService.TextToSpeechAsync with sentence.TargetSentence, save audio stream to AudioCache directory, update sentence.AudioUri, call repository UpdateAsync
- [X] T031 [P] [US2] Implement PlaySentenceAudioAsync() in `src/SentenceStudio/Pages/VocabularyManagement/EditVocabularyWordPage.cs` to check AudioUri exists and file exists, create AudioPlayer from file stream, call Play()
- [X] T032 [P] [US2] Implement ToggleCoreSentenceAsync() in `src/SentenceStudio/Pages/VocabularyManagement/EditVocabularyWordPage.cs` to call _exampleSentenceRepo.SetCoreAsync with negated IsCore value, update State.ExampleSentences list with returned sentence
- [X] T033 [US2] Implement DeleteSentenceAsync() and EditSentence() in `src/SentenceStudio/Pages/VocabularyManagement/EditVocabularyWordPage.cs`: Delete removes from repo and state list, Edit loads sentence into EditingSentence and sets IsEditingSentence true
- [X] T034 [US2] Update LoadData() in `src/SentenceStudio/Pages/VocabularyManagement/EditVocabularyWordPage.cs` to load example sentences via _exampleSentenceRepo.GetByVocabularyWordIdAsync and populate State.ExampleSentences
- [X] T035 [US2] Inject IExampleSentenceRepository into EditVocabularyWordPage.cs and verify RenderExampleSentences() displays correctly
- [X] T036 [US2] Add localization keys to Resources.resx and Resources.ko.resx: ExampleSentences, NoExampleSentences, AddExampleSentence, TargetSentencePlaceholder, NativeSentencePlaceholder, MarkAsCoreExample, GenerateAudio, MarkAsCore, ErrorSavingSentence, TargetSentenceRequired
- [X] T037 [US2] Update RenderEncodingStrength() in EditVocabularyWordPage.cs to pass State.ExampleSentences.Count to _encodingCalculator.Calculate() so example sentences affect encoding strength score
- [X] T038 [US2] Test on all platforms: Add 2-3 example sentences to word, mark one as core, generate audio, play audio, edit sentence, delete sentence, verify encoding strength increases when sentences added

**Checkpoint**: User Story 2 complete and independently testable - users can add context sentences with audio

---

## Phase 5: User Story 3 - Browse and Filter Words by Encoding Metadata (Priority: P3)

**Goal**: Enable users to filter vocabulary by tags and sort by encoding strength

**Independent Test**: Navigate to vocabulary list, filter by tag "nature", verify only tagged words display. Sort by encoding strength, verify weakest words (Basic) appear first.

**Dependencies**: Requires User Story 1 (tags, encoding) and User Story 2 (example sentences affect encoding)

### Implementation for User Story 3

- [X] T039 [P] [US3] Create VocabularyFilterService in `src/SentenceStudio/Services/VocabularyFilterService.cs` with compiled query for FilterByTagAsync using EF.CompileAsyncQuery with EF.Functions.Like pattern, OrderBy TargetLanguageTerm
- [X] T040 [P] [US3] Add GetAllTagsAsync() method to VocabularyEncodingRepository in `src/SentenceStudio/Data/VocabularyEncodingRepository.cs` to load all Tags columns, split by comma in memory, return distinct list of tags for UI
- [X] T041 [US3] Add filtering state to VocabularyListPage (or equivalent) in `src/SentenceStudio/Pages/VocabularyManagement/`: SelectedTag, SortByEncoding bool, AvailableTags list
- [X] T042 [US3] Add LoadWordsWithEncodingAsync() to VocabularyListPage in `src/SentenceStudio/Pages/VocabularyManagement/` to call _encodingRepository.GetWithEncodingStrengthAsync with tagFilter and sortByEncodingStrength parameters, update word list state
- [X] T043 [P] [US3] Add RenderFilterBar() to VocabularyListPage in `src/SentenceStudio/Pages/VocabularyManagement/` with Picker for tag selection (bound to AvailableTags), Button for sort by encoding toggle with ThemeKey Secondary
- [X] T044 [P] [US3] Add inline encoding strength indicators to vocabulary list items in VocabularyListPage in `src/SentenceStudio/Pages/VocabularyManagement/`: Small colored badge (Basic/Good/Strong) displayed next to each word using MyTheme colors
- [X] T045 [P] [US3] Add OnTagClicked() handler in VocabularyListPage in `src/SentenceStudio/Pages/VocabularyManagement/` to set SelectedTag filter and reload word list when tag badge clicked (from RenderTagBadges in edit page)
- [X] T046 [US3] Update LoadData() in VocabularyListPage in `src/SentenceStudio/Pages/VocabularyManagement/` to load available tags via _encodingRepository.GetAllTagsAsync and populate AvailableTags for filter picker
- [X] T047 [US3] Inject IVocabularyEncodingRepository into VocabularyListPage and verify filtering and sorting work correctly
- [X] T048 [US3] Register VocabularyFilterService in MauiProgram.cs as singleton
- [X] T049 [US3] Add localization keys to Resources.resx and Resources.ko.resx: SortByEncoding, FilterByTag, AllTags (picker default)
- [ ] T050 [US3] Test on all platforms: Filter by tag "nature", verify only matching words, sort by encoding, verify Basic words first, click tag badge in detail page, verify list filters

**Checkpoint**: User Story 3 complete and independently testable - users can discover words by tags and encoding strength

---

## Phase 6: User Story 4 - Store Lemma Forms for Dictionary Lookup (Priority: P3)

**Goal**: Store dictionary form (lemma) for inflected words and enable search

**Independent Test**: Edit vocabulary word "가면" (if you go), add lemma "가다" (to go), save. Search for "가다", verify related conjugated forms appear in results.

**Dependencies**: None (independent of other user stories, though UI already added in User Story 1)

### Implementation for User Story 4

- [ ] T051 [P] [US4] Add SearchByLemma functionality to VocabularyListPage in `src/SentenceStudio/Pages/VocabularyManagement/` with search Entry field that calls _encodingRepository.SearchByLemmaAsync when text changes (debounced)
- [ ] T052 [US4] Add localization keys to Resources.resx and Resources.ko.resx: SearchByLemma, LemmaSearchPlaceholder, DictionaryForm
- [ ] T053 [US4] Test on all platforms: Add lemma "가다" to word "가면", search by "가다", verify word appears in results, verify lemma displays as "Dictionary Form" label on detail page

**Checkpoint**: User Story 4 complete and independently testable - users can search vocabulary by dictionary forms

---

## Phase 7: Polish & Cross-Cutting Concerns

**Purpose**: Improvements that affect multiple user stories

- [ ] T054 [P] Create performance benchmark tests in `tests/SentenceStudio.Tests/Performance/EncodingPerformanceTests.cs`: Tag filtering 5000 words <50ms, Example sentence counts 50 words <100ms, Encoding calculation 100 words <30ms
- [ ] T055 Run performance benchmarks on mid-range Android device (Snapdragon 660 or equivalent) and verify all targets met, log results with ILogger
- [ ] T056 Final cross-platform testing checklist: iOS (add/edit/filter), Android (add/edit/filter), macOS (add/edit/filter), Windows (add/edit/filter), verify offline functionality (all metadata accessible without connectivity), verify audio generation requires connectivity and handles offline gracefully

**Checkpoint**: All user stories tested, performance verified, cross-platform validated

---

## Implementation Strategy

### MVP First (Minimum Viable Product)
**Recommendation**: Ship User Story 1 only as MVP
- **Delivers**: Core memory encoding with mnemonics, tags, images, encoding strength indicator
- **Testable**: Fully independent, can be validated without other stories
- **User Value**: Immediate retention improvement through proven encoding techniques
- **Effort**: ~12 tasks after foundational phase

### Incremental Delivery Order
1. **Phase 2** (Foundational) - MUST complete first (~10 tasks)
2. **User Story 1** (P1) - MVP candidate (~12 tasks)
3. **User Story 2** (P2) - Adds context sentences (~9 tasks)
4. **User Story 3** (P3) - Adds discovery tools (~8 tasks)
5. **User Story 4** (P3) - Adds linguistic accuracy (~3 tasks)
6. **Phase 7** (Polish) - Final testing (~3 tasks)

### Parallel Work Opportunities
- **Foundational Phase**: 5 tasks can run in parallel (T006-T010)
- **User Story 1**: 3 tasks can run in parallel (T016-T018)
- **User Story 2**: 3 tasks can run in parallel (T030-T032)
- **User Story 3**: 3 tasks can run in parallel (T043-T045)

---

## Validation Checklist

Before marking feature complete, verify:

- [ ] All 4 user stories have independent test criteria defined
- [ ] Each user story is independently testable (can verify without other stories)
- [ ] Foundational phase blocks all user story work (correct ordering)
- [ ] User Story 1 can serve as MVP (delivers core value alone)
- [ ] Performance targets documented and achievable (<50ms tag filter, <100ms batch load, <30ms encoding calc)
- [ ] All platforms tested (iOS, Android, macOS, Windows)
- [ ] Offline functionality verified (all metadata accessible without connectivity)
- [ ] Localization complete (English + Korean)
- [ ] Constitution compliance verified (MauiReactor patterns, theme-first styling, ILogger usage)

---

## Notes

**Performance Critical**: Tag filtering and encoding strength calculations MUST use optimized patterns:
- ✅ Compiled queries for repeated tag filtering (40% faster)
- ✅ Batch loading for example sentence counts (prevents N+1)
- ✅ EF.Functions.Like with indexed Tags column
- ✅ Pagination for large vocabulary lists (5000+ words)

**SQLite Indexes Required**: Verify migrations created indexes on:
- VocabularyWord.Tags (tag filtering)
- VocabularyWord.Lemma (lemma search)
- ExampleSentence.VocabularyWordId (foreign key joins)
- ExampleSentence.IsCore (core example filtering)
- ExampleSentence (VocabularyWordId, IsCore) composite

**Audio Generation Pattern**: Example sentence audio follows existing EditVocabularyWordPage pattern:
- Use _speechService.TextToSpeechAsync
- Save to AudioCache directory in FileSystem.AppDataDirectory
- Store file path in AudioUri column
- Use AudioManager.Current.CreatePlayer for playback

**Localization Pattern**: All user-facing strings MUST use `$"{_localize["Key"]}"` pattern with keys in Resources.resx (English) and Resources.ko.resx (Korean)
