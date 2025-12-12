# Quickstart: Vocabulary Search Syntax

**Feature**: 002-vocabulary-search-syntax  
**Target Audience**: Developers implementing this feature

## TL;DR

Replace VocabularyManagementPage filter UI (status dialog, resource dropdown) with unified search syntax: `tag:nature resource:general 단풍`. Parser extracts filters, repository builds optimized SQL, UXD Popups provides autocomplete, filter chips show active filters.

---

## Prerequisites

1. **Research completed**: Read `research.md` for architectural decisions
2. **Data model understood**: Review `data-model.md` for entity definitions
3. **Service contracts defined**: Check `contracts/service-contracts.md` for interfaces
4. **UXD.Popups installed**: Add NuGet package `UXDivers.Popups` to SentenceStudio project
5. **Existing code familiarity**: Review `VocabularyManagementPage.cs` current implementation

---

## Implementation Order (Follow spec-template Priority)

### Phase 1: Foundation (P1 - Must Have)

**US1: Basic Text Search**
1. Remove existing search timer logic (replaced by unified parser)
2. Add Entry with TextChanged binding to new `RawSearchQuery` state property
3. Implement `SearchQueryParser.Parse()` method (regex-based tokenization)
4. Update `ApplyFilters()` to use `ParsedQuery` instead of `SearchText`
5. Test: Type "단풍" → results show matching words

**US6: Filter Combination**
1. Extend `SearchQueryParser` to detect multiple filter types
2. Implement `FilterChipConverter.ToChips()` method
3. Add CollectionView above Entry to display FilterChips
4. Wire up chip removal (tap X → remove from ParsedQuery → re-apply)
5. Test: Type "tag:nature resource:general" → both chips appear, results filtered correctly

---

### Phase 2: Autocomplete (P2 - Should Have)

**US2: Tag-Based Filtering**
1. Install UXD.Popups NuGet package
2. Implement `VocabularySearchRepository.GetAutocompleteAsync("tag", partial)`
3. Add Popup control below Entry with CollectionView content
4. Detect `tag:` prefix in Entry.TextChanged → show autocomplete
5. Tap suggestion → insert tag value into Entry → hide popup
6. Test: Type "tag:nat" → autocomplete shows "nature" → tap → "tag:nature" inserted

**US3: Resource-Based Filtering**
1. Extend autocomplete to handle `resource:` prefix
2. Use existing `State.AvailableResources` for suggestions (no DB query)
3. Test: Type "resource:gen" → autocomplete shows "General Vocabulary"

---

### Phase 3: Advanced Filters (P3 - Nice to Have)

**US4: Lemma-Based Search**
1. Extend parser to handle `lemma:` prefix
2. Add lemma autocomplete (query distinct Lemma values)
3. Update repository to filter by `Lemma = '{value}'`
4. Test: Type "lemma:가다" → shows words with that lemma

**US5: Status-Based Filtering**
1. Extend parser to handle `status:` prefix
2. Add fixed autocomplete options: `known`, `learning`, `unknown`
3. Update repository to query VocabularyProgress for status filter
4. Test: Type "status:learning" → shows only learning-phase words

---

## Key Files to Modify

### New Files (Created)

```
src/SentenceStudio.Shared/Services/
└── SearchQueryParser.cs                    # Regex-based parser (testable in Shared project)

src/SentenceStudio.Shared/Models/
├── ParsedQuery.cs                          # Query DTO with Filters and FreeText
└── FilterToken.cs                          # Filter DTO (Type, Value, OriginalText)

src/SentenceStudio/Models/
└── AutocompleteSuggestion.cs               # Autocomplete DTO

tests/SentenceStudio.UnitTests/Services/
└── SearchQueryParserTests.cs               # 53 comprehensive unit tests
```

### Existing Files (Modified)

```
src/SentenceStudio/Pages/VocabularyManagement/
└── VocabularyManagementPage.cs
    - Added: RenderSearchBar() with Entry and filter chip display
    - Added: RenderFilterChips() for visual filter management
    - Added: ShowTagAutocompletePopup(), ShowResourceAutocompletePopup(), etc.
    - Added: OnSearchQueryChanged() with 300ms debounce
    - Added: OnTagSelected(), OnResourceSelected(), OnLemmaSelected(), OnStatusSelected()
    - Added: RemoveFilter(), ClearAllFilters()
    - Retained: Legacy filter methods for backward compatibility

src/SentenceStudio/Data/
└── VocabularyEncodingRepository.cs
    - Added: GetAllTagsAsync() - distinct tags query
    - Added: GetAllLemmasAsync() - distinct lemmas query  
    - Added: GetLemmasByPartialAsync() - lemma autocomplete
    - Added: FilterByTagAsync() - tag-based filtering
    - Added: FilterByResourcesAsync() - resource-based filtering
    - Added: FilterByResourceNamesAsync() - resource name filtering
    - Added: SearchByLemmaAsync() - lemma exact match

src/SentenceStudio.Shared/Migrations/
└── [YYYYMMDD]_AddVocabularySearchIndexes.cs
    - Created indexes: Tags, Lemma, TargetLanguageTerm, NativeLanguageTerm
```

---

## Component Architecture

```
VocabularyManagementPage (MauiReactor Component)
├── Entry (RawSearchQuery binding)
│   └── OnTextChanged → DetectFilterPrefix → ShowAutocomplete
│
├── Popup (UXD.Popups)
│   └── CollectionView (AutocompleteSuggestions)
│       └── OnTapped → InsertFilter → HidePopup
│
├── CollectionView (FilterChips)
│   └── Border + ImageButton (X) per chip
│       └── OnTapped → RemoveFilter → RefreshResults
│
└── CollectionView (FilteredVocabularyItems)
    └── Results from VocabularySearchRepository.SearchAsync()
```

---

## Service Interaction Flow

```
User Types "tag:nat"
    ↓
Entry.TextChanged
    ↓
OnSearchQueryChanged(string text)
    ↓
DetectFilterPrefix("tag:nat") → prefix="tag:", partial="nat"
    ↓
VocabularySearchRepository.GetAutocompleteAsync("tag", "nat")
    ↓
Query: SELECT DISTINCT Tags FROM VocabularyWord WHERE Tags LIKE '%nat%'
    ↓
Return: [{ Value: "nature", DisplayText: "nature (12 words)", Count: 12 }]
    ↓
SetState(s => s.AutocompleteSuggestions = results)
    ↓
Popup renders suggestions
    ↓
User taps "nature" suggestion
    ↓
InsertFilter("tag", "nature") → RawSearchQuery = "tag:nature"
    ↓
(Debounce 300ms)
    ↓
SearchQueryParser.Parse("tag:nature")
    ↓
Return: ParsedQuery { Filters: [{ Type: "tag", Value: "nature" }], FreeTextTerms: [] }
    ↓
FilterChipConverter.ToChips(parsedQuery)
    ↓
Return: [{ DisplayText: "Tag: nature" }]
    ↓
VocabularySearchRepository.SearchAsync(parsedQuery)
    ↓
Query: SELECT * FROM VocabularyWord WHERE Tags LIKE '%nature%'
    ↓
SetState(s => s.FilteredVocabularyItems = results)
    ↓
CollectionView renders filtered results
```

---

## Database Query Patterns

### Single Tag Filter
```sql
SELECT * FROM VocabularyWord
WHERE Tags LIKE '%nature%'
ORDER BY TargetLanguageTerm;
```

### Multiple Resource Filters (OR logic)
```sql
SELECT DISTINCT v.* FROM VocabularyWord v
INNER JOIN ResourceVocabularyMapping rvm ON v.Id = rvm.VocabularyWordId
INNER JOIN LearningResource lr ON rvm.LearningResourceId = lr.Id
WHERE lr.Title IN ('General', 'Textbook')
ORDER BY v.TargetLanguageTerm;
```

### Combined Filters (AND logic between types)
```sql
SELECT DISTINCT v.* FROM VocabularyWord v
INNER JOIN ResourceVocabularyMapping rvm ON v.Id = rvm.VocabularyWordId
INNER JOIN LearningResource lr ON rvm.LearningResourceId = lr.Id
WHERE v.Tags LIKE '%nature%'
  AND lr.Title = 'General'
  AND (v.TargetLanguageTerm LIKE '%가을%' OR v.NativeLanguageTerm LIKE '%가을%')
ORDER BY v.TargetLanguageTerm;
```

---

## Testing Checklist

### Manual Testing

- [ ] Type free text → results filter in real-time
- [ ] Type `tag:` → autocomplete popup appears
- [ ] Select autocomplete suggestion → filter inserted correctly
- [ ] Multiple filters display as chips
- [ ] Remove chip → filter removed, results update
- [ ] Clear entry → all filters removed
- [ ] Combined filters apply AND logic
- [ ] Same-type filters apply OR logic
- [ ] Case-insensitive matching works
- [ ] Malformed syntax treated as free text

### Performance Testing

- [ ] Search completes in <200ms with full dataset (1700 words)
- [ ] Autocomplete appears in <100ms
- [ ] Filter chips render in <50ms
- [ ] No query timeout errors with complex filters
- [ ] No UI lag when typing rapidly

### Cross-Platform Testing

- [ ] iOS: Popup positioning correct, keyboard doesn't overlap
- [ ] Android: Popup dismisses on back button
- [ ] macOS: Escape key dismisses popup
- [ ] Windows: Search works identically to macOS

---

## Validation Scenarios

**Instructions**: Run each scenario and verify the expected behavior. All scenarios should work identically across iOS, Android, macOS, and Windows.

### Scenario 1: Basic Free-Text Search
```
Action: Type "단풍" into the search box
Expected: 
  - Results update within 300ms (debounced)
  - Only vocabulary words containing "단풍" in TargetLanguageTerm, NativeLanguageTerm, or Lemma appear
  - No filter chips appear (free text is not shown as a chip)
```

### Scenario 2: Single Tag Filter
```
Action: Type "tag:nature" into the search box
Expected:
  - Results show only words with "nature" tag
  - A filter chip appears: "tag: nature" with X button
  - Tap X on chip → filter is removed, all results return
```

### Scenario 3: Tag Autocomplete
```
Action: Type "tag:" then wait 100ms
Expected:
  - ListActionPopup appears with available tags
  - Tags are sorted alphabetically
  - Tap a tag → filter is inserted as "tag:tagname"
  - Continue typing (e.g., "tag:nat") → popup shows matching tags
```

### Scenario 4: Multiple Tag Filters (AND Logic)
```
Action: Type "tag:nature tag:season"
Expected:
  - Results show only words with BOTH "nature" AND "season" tags
  - Two filter chips appear
  - Remove one chip → results update with remaining filter only
```

### Scenario 5: Resource Filter
```
Action: Type "resource:general" into the search box
Expected:
  - Results show only words associated with "General Vocabulary" resource
  - A filter chip appears: "resource: general"
  - Resource autocomplete works when typing "resource:"
```

### Scenario 6: Multiple Resource Filters (OR Logic)
```
Action: Type "resource:general resource:textbook"
Expected:
  - Results show words from EITHER "general" OR "textbook" resources
  - Two filter chips appear for resources
```

### Scenario 7: Status Filter
```
Action: Type "status:learning" into the search box
Expected:
  - Results show only words in "learning" status (based on VocabularyProgress)
  - Status autocomplete shows three options: known, learning, unknown
  - Status values have appropriate icons (✓, ⏳, ?)
```

### Scenario 8: Lemma Filter
```
Action: Type "lemma:가다" into the search box
Expected:
  - Results show words with lemma "가다" (including conjugated forms)
  - Lemma autocomplete shows available lemmas when typing "lemma:"
  - Korean text input works correctly in autocomplete
```

### Scenario 9: Combined Filters (Cross-Type AND)
```
Action: Type "tag:nature status:learning resource:general 단풍"
Expected:
  - Results match ALL of:
    - Has "nature" tag AND
    - Status is "learning" AND
    - Associated with "general" resource AND
    - Contains "단풍" in any searchable field
  - Three filter chips appear (tag, status, resource)
  - Free text "단풍" does not create a chip
```

### Scenario 10: Quoted Values
```
Action: Type 'tag:"multi word tag"' into the search box
Expected:
  - Properly handles tags with spaces
  - Filter chip shows: "tag: multi word tag"
```

### Scenario 11: Clear All Filters
```
Action: Add multiple filters, then tap "Clear all" button
Expected:
  - All filter chips removed
  - Search box cleared
  - All vocabulary results displayed
```

### Scenario 12: Filter Chip Removal
```
Action: Type "tag:nature tag:season", then tap X on "tag:nature" chip
Expected:
  - "tag:nature" chip removed
  - Search box updated to "tag:season"
  - Results update to show words with only "season" tag
```

### Scenario 13: Malformed Syntax
```
Action: Type "tag:" (with no value) or "tag: " (with space but no value)
Expected:
  - Treated as incomplete filter, autocomplete appears
  - No error messages displayed
  - User can continue typing to complete filter
```

### Scenario 14: Performance Test
```
Action: Type rapidly in the search box
Expected:
  - Debounce prevents excessive queries (300ms delay)
  - No UI lag or freezing
  - Results update smoothly after typing stops
```

### Scenario 15: Cursor Position Autocomplete
```
Action: Position cursor after "tag:" in "tag: resource:general"
Expected:
  - Tag autocomplete appears (detects filter at cursor position)
  - Not resource autocomplete
```

---

## Troubleshooting

### Popup Not Appearing

**Symptom**: Type `tag:` but autocomplete doesn't show

**Causes**:
- `ShowAutocomplete` state not updating
- Popup `IsOpen` binding not wired correctly
- Entry `CursorPosition` detection failing

**Fix**: Add debug logging in `DetectFilterPrefix()` method

---

### Slow Search Performance

**Symptom**: Results take >500ms to appear

**Causes**:
- Missing indexes on Tags, Lemma, TargetLanguageTerm, NativeLanguageTerm
- Using LINQ `Contains()` instead of SQL JOINs
- Not using parameterized queries

**Fix**: Run migration to create indexes, verify SQL in logs

---

### Filter Chips Not Removing

**Symptom**: Tap X on chip, but filter remains active

**Causes**:
- `OnChipRemoved()` not updating `RawSearchQuery`
- Parser not re-running after query update
- State mutation instead of immutable update

**Fix**: Ensure `SetState()` used for all state changes

---

## Next Steps After Implementation

1. **Update agent context**: Run `.specify/scripts/bash/update-agent-context.sh copilot`
2. **Add to changelog**: Document feature in CHANGELOG.md
3. **Update user documentation**: Add search syntax guide to docs/user-guide.md
4. **Measure performance**: Record benchmarks in contracts/service-contracts.md
5. **Create demo video**: Show search syntax in action for release notes

---

## Reference Links

- **UXD.Popups Documentation**: https://github.com/UXDivers/uxd-popups/blob/main/docs/Popup-Controls.md
- **BaristaNotes Example**: https://github.com/davidortinau/BaristaNotes (popup usage reference)
- **GitHub Search Syntax**: https://docs.github.com/en/search-github (inspiration)
- **Material Design Chips**: https://m3.material.io/components/chips/overview (UI pattern)
- **SQLite LIKE Performance**: https://www.sqlite.org/optoverview.html#like_opt (indexing strategy)
