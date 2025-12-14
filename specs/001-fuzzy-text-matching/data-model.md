# Data Model: Fuzzy Text Matching

**Phase 1 Output** | **Date**: 2025-12-14 | **Branch**: `001-fuzzy-text-matching`

## Entities

### FuzzyMatchResult

**Purpose**: Represents the result of evaluating user input against expected answer with fuzzy matching.

**Lifetime**: Transient (created per answer evaluation, not persisted)

**Properties**:

| Property | Type | Description | Validation |
|----------|------|-------------|------------|
| `IsCorrect` | `bool` | Whether the user's answer is correct (exact or fuzzy match) | Required |
| `MatchType` | `string` | Type of match: "Exact" or "Fuzzy" | Required; must be "Exact" or "Fuzzy" |
| `CompleteForm` | `string?` | The full expected answer when fuzzy match (null for exact) | Optional; set only when MatchType is "Fuzzy" |

**Usage**:
```csharp
var result = FuzzyMatcher.Evaluate(userInput, expectedAnswer);
if (result.IsCorrect)
{
    if (result.MatchType == "Fuzzy")
    {
        // Show enhanced feedback with result.CompleteForm
    }
    else
    {
        // Show standard "Correct!" feedback
    }
}
```

**State Transitions**: None (stateless evaluation)

---

## Services

### FuzzyMatcher (Static Utility Class)

**Purpose**: Provides fuzzy text matching logic for vocabulary quiz answer evaluation.

**Location**: `src/SentenceStudio/Services/FuzzyMatcher.cs`

**Dependencies**: None (pure logic, no DI needed)

**API Surface**:

#### `Evaluate(string userInput, string expectedAnswer): FuzzyMatchResult`

**Description**: Evaluates user input against expected answer using normalization and fuzzy matching rules.

**Parameters**:
- `userInput`: Text entered by user (can be empty/null)
- `expectedAnswer`: Expected vocabulary term (can include annotations)

**Returns**: `FuzzyMatchResult` indicating correctness, match type, and complete form if applicable

**Algorithm**:
1. Normalize both inputs using `NormalizeText()`
2. Compare normalized forms case-insensitively
3. If match, determine if exact or fuzzy by comparing original (trimmed) inputs
4. Return result with appropriate MatchType and CompleteForm

**Performance**: <1ms per call (compiled regex patterns)

**Thread Safety**: Yes (static methods, no shared mutable state)

**Example**:
```csharp
var result = FuzzyMatcher.Evaluate("take", "take (a photo)");
// result.IsCorrect = true
// result.MatchType = "Fuzzy"
// result.CompleteForm = "take (a photo)"

var result2 = FuzzyMatcher.Evaluate("take (a photo)", "take (a photo)");
// result2.IsCorrect = true
// result2.MatchType = "Exact"
// result2.CompleteForm = null
```

---

#### `NormalizeText(string text): string` (Private)

**Description**: Normalizes text by removing annotations, punctuation, and applying Unicode NFC.

**Normalization Steps**:
1. Unicode NFC normalization (Korean character consistency)
2. Remove parenthetical annotations: `\s*\([^)]*\)`
3. Remove tilde descriptors: `~.*$`
4. Trim whitespace
5. Remove punctuation: `[^\p{L}\p{N}\s]`
6. Remove "to " prefix if present (English infinitives)

**Parameters**:
- `text`: Raw text to normalize

**Returns**: Normalized core word

**Edge Cases**:
- `null` or empty input → returns empty string
- Multiple parentheses → removes all
- Korean text → preserves Hangul, normalizes encoding
- Mixed language → handles both English and Korean

**Example**:
```csharp
NormalizeText("take (a photo)") → "take"
NormalizeText("ding~ (a sound)") → "ding"
NormalizeText("to choose") → "choose"
NormalizeText("안녕하세요 (hello)") → "안녕하세요"
NormalizeText("don't") → "dont"
```

---

## Integration Points

### VocabularyQuizPage.cs

**Modification Location**: `CheckAnswer()` method (line 1395-1396)

**Change Summary**:
```csharp
// BEFORE:
var isCorrect = string.Equals(answer.Trim(), State.CurrentTargetLanguageTerm.Trim(),
    StringComparison.OrdinalIgnoreCase);

// AFTER:
var matchResult = FuzzyMatcher.Evaluate(answer, State.CurrentTargetLanguageTerm);
var isCorrect = matchResult.IsCorrect;
```

**Additional Logic**:
- Store `matchResult` in method scope
- Pass `matchResult.MatchType` and `matchResult.CompleteForm` to feedback UI update
- Log fuzzy match decisions using `ILogger<VocabularyQuizPage>`

**State Changes**: None (FuzzyMatchResult not stored in component state; used for immediate feedback only)

---

### Localization Resources

**Files**:
- `Resources/Strings/AppResources.resx` (English)
- `Resources/Strings/AppResources.ko-KR.resx` (Korean)

**New Keys**:

| Key | English Value | Korean Value |
|-----|---------------|--------------|
| `QuizFuzzyMatchCorrect` | `✓ Correct! Full form: {0}` | `✓ 정답! 전체 형태: {0}` |

**Usage in VocabularyQuizPage**:
```csharp
if (matchResult.MatchType == "Fuzzy")
{
    var message = string.Format($"{_localize["QuizFuzzyMatchCorrect"]}", 
        matchResult.CompleteForm);
    // Display message in feedback UI
}
else
{
    var message = $"{_localize["QuizCorrect"]}"; // Existing key
}
```

---

## Data Flow

```
User Input (Text Entry)
    ↓
VocabularyQuizPage.CheckAnswer()
    ↓
FuzzyMatcher.Evaluate(userInput, expectedAnswer)
    ↓
    ├─→ NormalizeText(userInput) → normalizedUser
    ├─→ NormalizeText(expectedAnswer) → normalizedExpected
    ├─→ Compare normalized forms (case-insensitive)
    └─→ Determine MatchType (Exact vs Fuzzy)
    ↓
FuzzyMatchResult
    ↓
VocabularyQuizPage UI Update
    ├─→ Show feedback message (exact or fuzzy)
    ├─→ Record answer correctness (progress tracking)
    └─→ Advance to next item
```

---

## No Database Changes

**Important**: This feature requires NO database schema changes. All logic is evaluation-only and operates on existing `VocabularyWord.NativeLanguageTerm` and `VocabularyWord.TargetLanguageTerm` fields.

**Rationale**: Fuzzy matching is a presentation/validation concern, not a data storage concern. Original vocabulary terms remain unchanged in the database.

---

## Validation Rules

### User Input Validation (Unchanged)

- Empty input: Not allowed (existing validation remains)
- Whitespace-only input: Treated as empty (existing validation remains)

### Match Evaluation Validation

- `userInput` and `expectedAnswer` must not be null (defensive check in `NormalizeText`)
- Normalized text can be empty (results in non-match)
- `MatchType` must be "Exact" or "Fuzzy" (enforced by evaluation logic)

---

## Performance Characteristics

### Time Complexity

- **Normalization**: O(n) where n = text length (typically 10-50 characters)
- **Regex operations**: O(n) with compiled patterns
- **String comparison**: O(n)
- **Total**: O(n), n < 50 → ~0.5-1ms

### Space Complexity

- **Temporary strings**: O(n) for normalized versions
- **Regex patterns**: Static (compiled once, reused)
- **FuzzyMatchResult**: Fixed size (3 properties)
- **Total**: O(n), n < 50 → minimal heap allocation

### Scalability

- **Per-answer evaluation**: Independent (no shared state)
- **Concurrent usage**: Thread-safe (static methods, compiled regex)
- **Memory footprint**: Negligible (no caching, stateless)

---

## Testing Considerations

### Unit Test Cases (FuzzyMatcher)

**Exact Matches**:
- `("take", "take")` → `IsCorrect=true, MatchType=Exact`
- `("안녕하세요", "안녕하세요")` → `IsCorrect=true, MatchType=Exact`

**Fuzzy Matches (Annotations)**:
- `("take", "take (a photo)")` → `IsCorrect=true, MatchType=Fuzzy, CompleteForm="take (a photo)"`
- `("ding", "ding~ (a sound)")` → `IsCorrect=true, MatchType=Fuzzy, CompleteForm="ding~ (a sound)"`

**Fuzzy Matches (Bidirectional)**:
- `("choose", "to choose")` → `IsCorrect=true, MatchType=Fuzzy, CompleteForm="to choose"`
- `("to choose", "choose")` → `IsCorrect=true, MatchType=Fuzzy, CompleteForm="choose"`

**Fuzzy Matches (Whitespace/Punctuation)**:
- `(" take ", "take")` → `IsCorrect=true, MatchType=Fuzzy, CompleteForm="take"`
- `("dont", "don't")` → `IsCorrect=true, MatchType=Fuzzy, CompleteForm="don't"`

**Incorrect Answers**:
- `("wrong", "take")` → `IsCorrect=false`
- `("택시", "안녕하세요")` → `IsCorrect=false`

**Edge Cases**:
- `("", "take")` → `IsCorrect=false`
- `("take", "")` → `IsCorrect=false`
- `("", "")` → `IsCorrect=false` (or true, depending on business rule)
- `("(annotation)", "word")` → `IsCorrect=false`

### Integration Test Cases (VocabularyQuizPage)

**Feedback Display**:
- Exact match shows standard "Correct!" message
- Fuzzy match shows "Correct! Full form: {complete}" message
- Incorrect answer shows existing error feedback

**Progress Tracking**:
- Both exact and fuzzy matches record as correct in progress system
- Enhanced tracking logs include MatchType for analytics

**Logging**:
- Debug logs show normalized forms for both user and expected
- Info logs indicate fuzzy match acceptance with CompleteForm

---

## Future Enhancements (Out of Scope)

- **Levenshtein distance**: Allow 1-2 character typos (requires careful tuning to avoid false positives)
- **Synonym matching**: Accept alternative translations (requires synonym database)
- **Multi-word phrase matching**: Handle word order variations (e.g., "a photo to take" vs "to take a photo")
- **Language-specific rules**: Korean particle flexibility, Japanese counter variations
- **Learning from corrections**: Track commonly confused terms for personalized hints
