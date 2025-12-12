# Research: Vocabulary Search Syntax

**Date**: 2025-12-12  
**Feature**: 002-vocabulary-search-syntax

## Phase 0: Research Findings

### Decision 1: Search Query Parser Architecture

**Decision**: Use a simple state machine parser that tokenizes input into filter tokens (`tag:value`) and free-text segments.

**Rationale**:
- GitHub-style syntax is well-understood by developers and power users
- Simple regex-based tokenization is sufficient for our limited filter types (tag, resource, lemma, status)
- No need for complex query language parser (e.g., ANTLR) given straightforward syntax
- Performance: String parsing is fast enough for real-time autocomplete (<100ms requirement)

**Alternatives Considered**:
- **Full text search engine (FTS5)**: Overkill for structured filtering; adds SQLite extension complexity
- **LINQ dynamic queries**: Poor performance on mobile; violates constitution's SQLite optimization principle
- **Lucene.NET**: Heavy dependency for simple key:value filtering

**Implementation Approach**:
```csharp
// Pseudo-code
public class SearchQueryParser
{
    public ParsedQuery Parse(string input)
    {
        var tokens = new List<FilterToken>();
        var freeText = new List<string>();
        
        // Regex: (tag|resource|lemma|status):(\S+)
        var filterPattern = @"(tag|resource|lemma|status):(\S+)";
        foreach (Match match in Regex.Matches(input, filterPattern))
        {
            tokens.Add(new FilterToken(match.Groups[1].Value, match.Groups[2].Value));
        }
        
        // Remove filter syntax from input to get free text
        var remaining = Regex.Replace(input, filterPattern, "");
        freeText = remaining.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        
        return new ParsedQuery { Filters = tokens, FreeTextTerms = freeText };
    }
}
```

---

### Decision 2: Autocomplete UI Component (UXD Popups)

**Decision**: Use UXD.Popups `Popup` control with custom content for filter autocomplete dropdown.

**Rationale**:
- UXD.Popups is native MAUI control - no wrapper needed for MauiReactor (constitution requirement)
- Provides platform-appropriate dropdown behavior (iOS modal sheet, Android dropdown, etc.)
- Lightweight: doesn't require Syncfusion dependency
- Reference implementation available in BaristaNotes project for guidance

**Alternatives Considered**:
- **Syncfusion SfAutoComplete**: Too heavyweight; requires license; overkill for simple key:value suggestions
- **Custom CollectionView overlay**: Reinventing the wheel; UXD.Popups solves platform consistency
- **MAUI Community Toolkit Popup**: Less actively maintained than UXD.Popups

**Implementation Approach**:
```csharp
// Pseudo-code (MauiReactor fluent syntax)
new Popup()
    .Content(
        CollectionView()
            .ItemsSource(autocompleteItems)
            .ItemTemplate(item => 
                Label(item.DisplayText)
                    .OnTapped(() => InsertFilter(item.Value))
            )
    )
    .IsOpen(showAutocomplete)
    .OnClosed(() => SetState(s => s.ShowAutocomplete = false))
```

---

### Decision 3: Filter Chip UI Pattern

**Decision**: Use horizontal scrollable CollectionView with Border-based chips that display filter type + value with X button.

**Rationale**:
- Follows Material Design chip pattern (familiar to users)
- CollectionView provides virtualization for many filters (though unlikely to exceed 10 filters in practice)
- MauiReactor Border + HStack composition is straightforward
- Meets constitution requirement: no unnecessary wrappers, theme-first styling

**Alternatives Considered**:
- **FlexLayout**: No virtualization; performance degrades with many chips
- **Syncfusion SfChip**: License requirement; overkill for simple remove-on-tap behavior
- **Custom control**: Violates simplicity principle

**Implementation Approach**:
```csharp
// Pseudo-code
CollectionView()
    .ItemsSource(activeFilters)
    .ItemsLayout(new LinearItemsLayout(ItemsLayoutOrientation.Horizontal) { ItemSpacing = 8 })
    .ItemTemplate(filter =>
        Border(
            HStack(
                Label($"{filter.Type}: {filter.Value}").ThemeKey(MyTheme.Caption1),
                ImageButton()
                    .Source(MyTheme.IconClose)
                    .OnClicked(() => RemoveFilter(filter))
            )
        ).ThemeKey(MyTheme.CardStyle)
    )
```

---

### Decision 4: SQLite Query Optimization Strategy

**Decision**: Generate parameterized SQL queries dynamically based on active filters; avoid LINQ Contains() for large collections.

**Rationale**:
- Constitution requires SQLite optimization for mobile performance
- Recent performance issue: 1700-parameter WHERE IN clause from LINQ `Contains()`
- Solution: Use JOINs and indexed columns instead of parameter explosion
- Real-time search requirement (<200ms) demands efficient queries

**Query Pattern**:
```sql
-- Tag filter example
SELECT v.* FROM VocabularyWord v
WHERE v.Tags LIKE '%nature%'  -- Assumes comma-separated tags column
  AND (v.TargetLanguageTerm LIKE '%가을%' OR v.NativeLanguageTerm LIKE '%가을%')

-- Resource filter with JOIN (avoid WHERE IN)
SELECT DISTINCT v.* FROM VocabularyWord v
INNER JOIN ResourceVocabularyMapping rvm ON v.Id = rvm.VocabularyWordId
INNER JOIN LearningResource lr ON rvm.LearningResourceId = lr.Id
WHERE lr.Title = 'General'

-- Multiple filters combined with AND logic
SELECT v.* FROM VocabularyWord v
INNER JOIN ResourceVocabularyMapping rvm ON v.Id = rvm.VocabularyWordId
INNER JOIN LearningResource lr ON rvm.LearningResourceId = lr.Id
WHERE v.Tags LIKE '%nature%'
  AND lr.Title = 'General'
  AND (v.TargetLanguageTerm LIKE '%가을%' OR v.NativeLanguageTerm LIKE '%가을%')
```

**Index Strategy**:
```sql
-- Create indexes for filter columns
CREATE INDEX IF NOT EXISTS idx_vocabulary_tags ON VocabularyWord(Tags);
CREATE INDEX IF NOT EXISTS idx_vocabulary_lemma ON VocabularyWord(Lemma);
CREATE INDEX IF NOT EXISTS idx_vocabulary_target ON VocabularyWord(TargetLanguageTerm);
CREATE INDEX IF NOT EXISTS idx_vocabulary_native ON VocabularyWord(NativeLanguageTerm);
```

**Alternatives Considered**:
- **LINQ Where() chains**: Clean syntax but poor performance; translates to inefficient SQL
- **Raw SQL with Dapper**: Adds dependency; EF Core parameterization is sufficient
- **Full-text search (FTS5)**: Overkill for exact-match filtering

---

### Decision 5: Real-Time Search Debouncing

**Decision**: Use 300ms debounce timer for text input changes; apply filters immediately for chip removal and autocomplete selection.

**Rationale**:
- Existing code already implements search timer pattern (`OnSearchTextChanged` method)
- 300ms balances responsiveness with query frequency (prevents query spam)
- User expectations: typing feels instant, but backend doesn't thrash database
- Autocomplete selections and chip removals are explicit actions - no debounce needed

**Implementation**: Reuse existing `_searchTimer` pattern, extend for filter application.

---

### Decision 6: Autocomplete Trigger Detection

**Decision**: Detect filter prefix (`tag:`, `resource:`, `lemma:`, `status:`) in Entry's TextChanged event using cursor position; show popup when prefix detected.

**Rationale**:
- Users type naturally: "tag:nat" should show autocomplete at "nat"
- Cursor position matters: editing middle of query shouldn't trigger autocomplete unless at prefix
- Regex pattern: `(tag|resource|lemma|status):(\w*)$` (end-of-string check for active typing)

**Implementation Approach**:
```csharp
void OnSearchTextChanged(string text)
{
    SetState(s => s.SearchText = text);
    
    // Detect active filter prefix at cursor position
    var cursorIndex = GetCursorPosition(); // Entry.CursorPosition
    var textBeforeCursor = text.Substring(0, cursorIndex);
    var match = Regex.Match(textBeforeCursor, @"(tag|resource|lemma|status):(\w*)$");
    
    if (match.Success)
    {
        var filterType = match.Groups[1].Value;
        var partialValue = match.Groups[2].Value;
        ShowAutocomplete(filterType, partialValue);
    }
    else
    {
        HideAutocomplete();
    }
    
    // Existing debounced filter application
    _searchTimer?.Dispose();
    _searchTimer = new Timer(_ => ApplyFilters(), null, 300, Timeout.Infinite);
}
```

---

### Decision 7: Filter Combination Logic

**Decision**: Use AND logic between different filter types; OR logic within same filter type.

**Rationale**:
- Matches user expectations from GitHub search: `is:open is:pr` = AND, `author:user1 author:user2` = OR
- Spec requirement FR-004 and FR-005 explicitly define this behavior
- SQL translation: Different types JOIN together, same types use IN clause

**Examples**:
- `tag:nature resource:general` → Words that have nature tag AND are in general resource
- `resource:general resource:textbook` → Words in EITHER general OR textbook resource
- `tag:nature tag:season` → Words with BOTH nature AND season tags (AND logic for tags makes sense)

**Implementation**: ParsedQuery object groups filters by type, repository builds SQL accordingly.

---

## Summary: Key Technical Decisions

| Decision | Choice | Primary Rationale |
|----------|--------|------------------|
| **Parser** | Regex-based state machine | Simple, fast, sufficient for key:value syntax |
| **Autocomplete UI** | UXD.Popups Popup | Native MAUI, platform-appropriate behavior |
| **Filter Chips** | CollectionView + Border | Virtualized, theme-first, familiar pattern |
| **SQLite Queries** | Parameterized SQL with JOINs | Avoids parameter explosion, indexed lookups |
| **Debouncing** | 300ms timer for text input | Balances responsiveness with query frequency |
| **Autocomplete Trigger** | Cursor position + regex | Detects active typing context accurately |
| **Filter Logic** | AND between types, OR within | Matches GitHub pattern, spec requirements |

---

## Open Questions

None - all NEEDS CLARIFICATION items resolved.

---

## References

- UXD.Popups documentation: https://github.com/UXDivers/uxd-popups/blob/main/docs/Popup-Controls.md
- BaristaNotes reference implementation: https://github.com/davidortinau/BaristaNotes
- GitHub search syntax documentation (inspiration): https://docs.github.com/en/search-github/getting-started-with-searching-on-github
- Material Design Chips: https://m3.material.io/components/chips/overview
