# Data Model: Vocabulary Search Syntax

**Date**: 2025-12-12  
**Feature**: 002-vocabulary-search-syntax

## Entity Definitions

### SearchQuery (New - Business Logic Entity)

Represents a parsed user search query containing filter tokens and free-text components.

**Fields:**
- `Filters`: `List<FilterToken>` - Structured filter specifications (tag, resource, lemma, status)
- `FreeTextTerms`: `List<string>` - Unstructured search terms (words without prefix syntax)

**Relationships:**
- None (ephemeral parse result, not persisted)

**Validation Rules:**
- Maximum 10 filter tokens (prevents query complexity explosion)
- Maximum 50 characters per free-text term
- Filter types must be one of: `tag`, `resource`, `lemma`, `status`
- Filter values cannot be empty strings

**State Transitions:**
- Created: When user types in search box
- Parsed: After regex tokenization extracts filters
- Applied: When converted to SQLite query
- Discarded: When user clears search or navigates away

---

### FilterToken (New - Business Logic Entity)

Individual structured filter extracted from search query (e.g., `tag:nature`).

**Fields:**
- `Type`: `string` - Filter category (`tag`, `resource`, `lemma`, `status`)
- `Value`: `string` - Filter value (e.g., `nature`, `general`, `가다`, `learning`)

**Relationships:**
- Parent: `SearchQuery.Filters` collection

**Validation Rules:**
- `Type` must be one of: `tag`, `resource`, `lemma`, `status` (case-insensitive)
- `Value` cannot be null or whitespace
- `Value` maximum length: 100 characters

**Equality:**
- Two FilterTokens are equal if Type and Value match (case-insensitive comparison)

---

### AutocompleteSuggestion (New - Business Logic Entity)

Available filter value displayed to user during autocomplete.

**Fields:**
- `Type`: `string` - Filter category this suggestion belongs to
- `Value`: `string` - Actual filter value (e.g., "nature", "General Vocabulary")
- `DisplayText`: `string` - User-friendly display (may include count: "nature (12 words)")
- `Count`: `int?` - Optional: number of items matching this filter

**Relationships:**
- None (ephemeral UI data, not persisted)

**Validation Rules:**
- `Value` and `DisplayText` cannot be null or empty
- `Count` must be non-negative if present

**Data Sources:**
- **Tags**: `VocabularyWord.Tags` column (comma-separated, parsed)
- **Resources**: `LearningResource.Title` column
- **Lemmas**: `VocabularyWord.Lemma` column (distinct values)
- **Status**: Fixed enum (`known`, `learning`, `unknown`)

---

### FilterChip (New - UI Entity)

Visual representation of active filter that can be removed by user.

**Fields:**
- `Type`: `string` - Filter category
- `Value`: `string` - Filter value
- `DisplayText`: `string` - UI label (e.g., "Tag: nature")

**Relationships:**
- Derived from `SearchQuery.Filters` for display

**Validation Rules:**
- Same as `FilterToken`

**UI Behavior:**
- Tap/Click: Removes filter from SearchQuery and re-applies filters
- Long-press (optional future enhancement): Edit filter value inline

---

## Existing Entity Extensions

### VocabularyWord (Existing - Database Entity)

**New Index Requirements:**
```sql
CREATE INDEX IF NOT EXISTS idx_vocabulary_tags ON VocabularyWord(Tags);
CREATE INDEX IF NOT EXISTS idx_vocabulary_lemma ON VocabularyWord(Lemma);
CREATE INDEX IF NOT EXISTS idx_vocabulary_target ON VocabularyWord(TargetLanguageTerm);
CREATE INDEX IF NOT EXISTS idx_vocabulary_native ON VocabularyWord(NativeLanguageTerm);
```

**Query Patterns:**
- **Tag filter**: `WHERE Tags LIKE '%{tag}%'` (assumes comma-separated format)
- **Lemma filter**: `WHERE Lemma = '{lemma}'` (exact match)
- **Free text**: `WHERE TargetLanguageTerm LIKE '%{text}%' OR NativeLanguageTerm LIKE '%{text}%' OR Lemma LIKE '%{text}%'`

---

### VocabularyProgress (Existing - Database Entity)

**Status Filter Query Pattern:**
```sql
-- Known words
WHERE EXISTS (
    SELECT 1 FROM VocabularyProgress vp
    WHERE vp.VocabularyWordId = VocabularyWord.Id
      AND vp.MasteryScore >= 80
)

-- Learning words
WHERE EXISTS (
    SELECT 1 FROM VocabularyProgress vp
    WHERE vp.VocabularyWordId = VocabularyWord.Id
      AND vp.MasteryScore < 80
      AND vp.MasteryScore > 0
)

-- Unknown words
WHERE NOT EXISTS (
    SELECT 1 FROM VocabularyProgress vp
    WHERE vp.VocabularyWordId = VocabularyWord.Id
)
```

---

### LearningResource (Existing - Database Entity)

**Resource Filter Query Pattern:**
```sql
-- Single resource
SELECT DISTINCT v.* FROM VocabularyWord v
INNER JOIN ResourceVocabularyMapping rvm ON v.Id = rvm.VocabularyWordId
INNER JOIN LearningResource lr ON rvm.LearningResourceId = lr.Id
WHERE lr.Title = '{resourceName}'

-- Multiple resources (OR logic)
WHERE lr.Title IN ('{resource1}', '{resource2}', ...)
```

---

## State Management

### VocabularyManagementPageState (Existing - Extended)

**New Fields:**
```csharp
public string RawSearchQuery { get; set; } = string.Empty;  // User's typed text
public ParsedQuery? ParsedQuery { get; set; }               // Parsed filter structure
public List<FilterChip> ActiveFilterChips { get; set; } = new();
public bool ShowAutocomplete { get; set; } = false;
public string AutocompleteFilterType { get; set; } = string.Empty;
public string AutocompletePartialValue { get; set; } = string.Empty;
public List<AutocompleteSuggestion> AutocompleteSuggestions { get; set; } = new();
```

**Removed Fields:**
```csharp
// OLD: Replaced by unified search syntax
// public string SearchText { get; set; }
// public VocabularyFilter SelectedFilter { get; set; }
// public LearningResource? SelectedResource { get; set; }
// public string? SelectedTag { get; set; }
```

**State Flow:**
1. User types → `RawSearchQuery` updates
2. Parser extracts → `ParsedQuery` populated
3. Chips derived → `ActiveFilterChips` generated
4. Query applied → `FilteredVocabularyItems` updated

---

## Performance Considerations

### Query Complexity

**Best Case** (Free text only):
```sql
SELECT * FROM VocabularyWord
WHERE TargetLanguageTerm LIKE '%단풍%' OR NativeLanguageTerm LIKE '%단풍%'
```
**Estimated**: <50ms with indexes

**Worst Case** (All filter types):
```sql
SELECT DISTINCT v.* FROM VocabularyWord v
INNER JOIN ResourceVocabularyMapping rvm ON v.Id = rvm.VocabularyWordId
INNER JOIN LearningResource lr ON rvm.LearningResourceId = lr.Id
LEFT JOIN VocabularyProgress vp ON v.Id = vp.VocabularyWordId
WHERE v.Tags LIKE '%nature%'
  AND v.Lemma = '가다'
  AND lr.Title IN ('General', 'Textbook')
  AND vp.MasteryScore < 80
  AND (v.TargetLanguageTerm LIKE '%가을%' OR v.NativeLanguageTerm LIKE '%가을%')
```
**Estimated**: <200ms with indexes (meets requirement SC-002)

### Autocomplete Performance

**Query Pattern:**
```sql
-- Get distinct tags (cached after first query)
SELECT DISTINCT Tags FROM VocabularyWord WHERE Tags IS NOT NULL;

-- Get resources (already in memory: State.AvailableResources)
-- No additional query needed

-- Get distinct lemmas starting with partial value
SELECT DISTINCT Lemma FROM VocabularyWord
WHERE Lemma LIKE '{partial}%'
LIMIT 10;
```
**Estimated**: <100ms (meets requirement SC-005)

---

## Migration Strategy

**No schema changes required** - all new entities are business logic only (not persisted).

**Index creation migration:**
```csharp
// New EF Core migration: AddVocabularySearchIndexes
protected override void Up(MigrationBuilder migrationBuilder)
{
    migrationBuilder.Sql(
        "CREATE INDEX IF NOT EXISTS idx_vocabulary_tags ON VocabularyWord(Tags);");
    migrationBuilder.Sql(
        "CREATE INDEX IF NOT EXISTS idx_vocabulary_lemma ON VocabularyWord(Lemma);");
    migrationBuilder.Sql(
        "CREATE INDEX IF NOT EXISTS idx_vocabulary_target ON VocabularyWord(TargetLanguageTerm);");
    migrationBuilder.Sql(
        "CREATE INDEX IF NOT EXISTS idx_vocabulary_native ON VocabularyWord(NativeLanguageTerm);");
}

protected override void Down(MigrationBuilder migrationBuilder)
{
    migrationBuilder.Sql("DROP INDEX IF EXISTS idx_vocabulary_tags;");
    migrationBuilder.Sql("DROP INDEX IF EXISTS idx_vocabulary_lemma;");
    migrationBuilder.Sql("DROP INDEX IF EXISTS idx_vocabulary_target;");
    migrationBuilder.Sql("DROP INDEX IF EXISTS idx_vocabulary_native;");
}
```

---

## Entity Diagram

```
┌────────────────────────────┐
│      SearchQuery           │
│  (Ephemeral - Memory)      │
├────────────────────────────┤
│ + Filters: List<FilterToken>│
│ + FreeTextTerms: List<string>│
└────────────┬───────────────┘
             │ contains
             ▼
┌────────────────────────────┐
│      FilterToken           │
│  (Ephemeral - Memory)      │
├────────────────────────────┤
│ + Type: string             │
│ + Value: string            │
└────────────────────────────┘

           ║ drives queries against
           ▼
┌────────────────────────────┐      ┌────────────────────────────┐
│   VocabularyWord           │◄────►│  LearningResource          │
│   (Database - Indexed)     │  M:N │  (Database - Indexed)      │
├────────────────────────────┤      ├────────────────────────────┤
│ + Tags: string             │      │ + Title: string            │
│ + Lemma: string            │      └────────────────────────────┘
│ + TargetLanguageTerm: string│
│ + NativeLanguageTerm: string│
└────────────┬───────────────┘
             │ 1:1
             ▼
┌────────────────────────────┐
│   VocabularyProgress       │
│   (Database)               │
├────────────────────────────┤
│ + MasteryScore: double     │
│ + IsKnown: computed        │
│ + IsLearning: computed     │
└────────────────────────────┘

           ║ displayed as
           ▼
┌────────────────────────────┐
│      FilterChip            │
│  (UI Entity - Memory)      │
├────────────────────────────┤
│ + Type: string             │
│ + Value: string            │
│ + DisplayText: string      │
└────────────────────────────┘
```

---

## Future Enhancements (Out of Scope)

- **Saved Searches**: Persist frequently used queries for quick access
- **Search History**: Recent searches dropdown for quick re-application
- **Advanced Operators**: Support NOT, OR operators (`-tag:nature`, `tag:nature OR tag:season`)
- **Date Filters**: `created:>2025-01-01`, `reviewed:<7d` (last reviewed within 7 days)
- **Numeric Filters**: `mastery:>80`, `attempts:<5`
