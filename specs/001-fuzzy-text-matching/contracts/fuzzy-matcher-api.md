# API Contract: FuzzyMatcher Service

**Phase 1 Output** | **Date**: 2025-12-14 | **Branch**: `001-fuzzy-text-matching`

## Service Contract

### Namespace
`SentenceStudio.Services`

### Class: FuzzyMatcher (Static)

**Purpose**: Provides deterministic fuzzy text matching for vocabulary quiz answer evaluation.

**Access Modifier**: `public static`

**Dependencies**: None (pure logic utility)

---

## Public API

### Method: Evaluate

```csharp
public static FuzzyMatchResult Evaluate(string userInput, string expectedAnswer)
```

**Description**: Evaluates whether user input matches expected answer using normalization and fuzzy matching rules.

**Parameters**:

| Name | Type | Description | Constraints |
|------|------|-------------|-------------|
| `userInput` | `string` | Text entered by user during quiz | Can be null, empty, or whitespace-only |
| `expectedAnswer` | `string` | Expected vocabulary term (may include annotations) | Can be null, empty, or whitespace-only |

**Returns**: `FuzzyMatchResult`

**Behavior**:
- Normalizes both inputs using internal `NormalizeText()` logic
- Compares normalized forms case-insensitively
- Determines match type (Exact vs Fuzzy) by comparing original trimmed inputs
- Returns result with `IsCorrect`, `MatchType`, and optional `CompleteForm`

**Performance Guarantee**: <10ms per call (typically <1ms)

**Thread Safety**: Yes (stateless, no shared mutable state)

**Exceptions**: None (defensive null handling, returns non-match for invalid inputs)

**Example Usage**:

```csharp
// Exact match
var result = FuzzyMatcher.Evaluate("take", "take");
// result.IsCorrect = true
// result.MatchType = "Exact"
// result.CompleteForm = null

// Fuzzy match (annotation removed)
var result = FuzzyMatcher.Evaluate("take", "take (a photo)");
// result.IsCorrect = true
// result.MatchType = "Fuzzy"
// result.CompleteForm = "take (a photo)"

// Fuzzy match (bidirectional infinitive)
var result = FuzzyMatcher.Evaluate("choose", "to choose");
// result.IsCorrect = true
// result.MatchType = "Fuzzy"
// result.CompleteForm = "to choose"

// Non-match
var result = FuzzyMatcher.Evaluate("wrong", "take");
// result.IsCorrect = false
// result.MatchType = null (or empty)
// result.CompleteForm = null
```

---

## Data Transfer Object: FuzzyMatchResult

### Class Definition

```csharp
public class FuzzyMatchResult
{
    /// <summary>
    /// Indicates whether the user's answer is correct (exact or fuzzy match).
    /// </summary>
    public bool IsCorrect { get; set; }

    /// <summary>
    /// Type of match: "Exact" for character-for-character match,
    /// "Fuzzy" for normalized/core word match.
    /// Null or empty when IsCorrect is false.
    /// </summary>
    public string? MatchType { get; set; }

    /// <summary>
    /// The complete expected answer when a fuzzy match is detected.
    /// Null for exact matches or incorrect answers.
    /// Used to display educational feedback to users.
    /// </summary>
    public string? CompleteForm { get; set; }
}
```

### Property Contracts

| Property | Type | Nullability | Valid Values | Description |
|----------|------|-------------|--------------|-------------|
| `IsCorrect` | `bool` | Non-nullable | `true`, `false` | Whether answer is correct |
| `MatchType` | `string?` | Nullable | `"Exact"`, `"Fuzzy"`, `null` | Type of match when correct |
| `CompleteForm` | `string?` | Nullable | Any string or `null` | Full expected answer for fuzzy matches |

### State Invariants

- If `IsCorrect == true`, then `MatchType` must be either `"Exact"` or `"Fuzzy"`
- If `IsCorrect == false`, then `MatchType` should be `null` or empty
- If `MatchType == "Exact"`, then `CompleteForm` should be `null`
- If `MatchType == "Fuzzy"`, then `CompleteForm` should contain the full expected answer
- `CompleteForm` is never empty string (either null or non-empty)

---

## Internal Logic (Not Exposed as Public API)

### Method: NormalizeText (Private)

```csharp
private static string NormalizeText(string text)
```

**Purpose**: Normalizes text by removing annotations, punctuation, and applying Unicode NFC.

**Normalization Steps**:
1. Unicode NFC normalization (Korean character consistency)
2. Remove parenthetical annotations: regex `\s*\([^)]*\)`
3. Remove tilde descriptors: regex `~.*$`
4. Trim whitespace
5. Remove punctuation: regex `[^\p{L}\p{N}\s]`
6. Remove "to " prefix if present (English infinitives)

**Parameters**:
- `text`: Raw text to normalize

**Returns**: Normalized core word (empty string if input is null/empty)

**Not Part of Public Contract**: This method is implementation detail and may change without affecting public API consumers.

---

## Integration Contract

### VocabularyQuizPage Integration

**Location**: `src/SentenceStudio/Pages/VocabularyQuiz/VocabularyQuizPage.cs`

**Method**: `CheckAnswer()` (line ~1395)

**Usage Pattern**:

```csharp
// In CheckAnswer() method:
var answer = State.UserMode == InputMode.MultipleChoice.ToString() ?
    State.UserGuess : State.UserInput;

if (string.IsNullOrWhiteSpace(answer))
{
    _logger.LogDebug("❌ CheckAnswer: Answer is empty");
    return;
}

// FUZZY MATCHING INTEGRATION POINT
var matchResult = FuzzyMatcher.Evaluate(answer, State.CurrentTargetLanguageTerm);
var isCorrect = matchResult.IsCorrect;

_logger.LogDebug("🔍 CheckAnswer: answer='{Answer}', expected='{Expected}', matchType='{MatchType}'", 
    answer, State.CurrentTargetLanguageTerm, matchResult.MatchType);

// Rest of CheckAnswer() logic uses isCorrect as before...

// NEW: Enhanced feedback based on match type
if (isCorrect && matchResult.MatchType == "Fuzzy")
{
    var feedbackMessage = string.Format($"{_localize["QuizFuzzyMatchCorrect"]}", 
        matchResult.CompleteForm);
    _logger.LogInformation("✨ Fuzzy match accepted: user='{User}', complete='{Complete}'", 
        answer, matchResult.CompleteForm);
    // Update UI with feedbackMessage
}
else if (isCorrect)
{
    var feedbackMessage = $"{_localize["QuizCorrect"]}";
    // Update UI with standard message
}
```

**Contract Requirements**:
- Must pass both `answer` and `State.CurrentTargetLanguageTerm` to `Evaluate()`
- Must check `matchResult.IsCorrect` for correctness (replaces current exact match logic)
- Should log `matchResult.MatchType` for debugging and analytics
- Should display `matchResult.CompleteForm` in feedback when `MatchType == "Fuzzy"`

---

## Localization Contract

**Required Keys**:

| Key | Purpose | Format |
|-----|---------|--------|
| `QuizFuzzyMatchCorrect` | Feedback message for fuzzy matches | String with single `{0}` placeholder |

**English Resource**:
```xml
<data name="QuizFuzzyMatchCorrect" xml:space="preserve">
  <value>✓ Correct! Full form: {0}</value>
</data>
```

**Korean Resource**:
```xml
<data name="QuizFuzzyMatchCorrect" xml:space="preserve">
  <value>✓ 정답! 전체 형태: {0}</value>
</data>
```

**Usage Contract**:
```csharp
var message = string.Format($"{_localize["QuizFuzzyMatchCorrect"]}", 
    matchResult.CompleteForm);
```

**Contract Requirement**: `{0}` placeholder MUST be replaced with complete vocabulary term (not normalized form).

---

## Behavioral Contracts

### Normalization Rules (Public Behavior)

These rules define the observable behavior of fuzzy matching:

1. **Parenthetical Annotations**: Text within `()` is removed
   - `"take (a photo)"` → matches `"take"`
   - `"안녕하세요 (hello)"` → matches `"안녕하세요"`

2. **Tilde Descriptors**: Text after `~` is removed
   - `"ding~ (a sound)"` → matches `"ding"`

3. **Whitespace Tolerance**: Leading/trailing spaces ignored
   - `" take "` → matches `"take"`

4. **Case Insensitivity**: Comparison ignores case
   - `"Take"` → matches `"take"`

5. **Punctuation Tolerance**: Punctuation removed during comparison
   - `"don't"` → matches `"dont"`

6. **Bidirectional Infinitive**: English "to" prefix is optional
   - `"choose"` → matches `"to choose"`
   - `"to choose"` → matches `"choose"`

7. **Unicode Normalization**: Korean characters normalized to NFC
   - Decomposed Hangul → matches precomposed Hangul

### Non-Negotiable Constraints (Safety)

1. **No False Positives**: Incorrect answers MUST NOT be accepted
   - `"wrong"` → does NOT match `"take"`
   - `"택시"` → does NOT match `"안녕하세요"`

2. **Exact Match Priority**: Exact matches MUST be detected as "Exact"
   - `("take", "take")` → `MatchType = "Exact"` (not "Fuzzy")

3. **Deterministic**: Same inputs MUST produce same output every time
   - No randomness, no external dependencies

4. **Performance**: Evaluation MUST complete in <10ms
   - Compiled regex patterns, no API calls, no disk I/O

---

## Versioning & Compatibility

**Version**: 1.0.0 (initial implementation)

**Stability**: Stable (public API will not change without major version bump)

**Breaking Changes**: None planned

**Future Additions** (backward-compatible):
- Additional `Evaluate()` overloads for language-specific rules
- Optional `FuzzyMatchOptions` parameter for customization
- Performance metrics in `FuzzyMatchResult` (e.g., `EvaluationTimeMs`)

**Deprecation Policy**: If normalization rules change, old behavior will be preserved under feature flag for one major version cycle.

---

## Testing Contract

### Required Unit Tests

1. **Exact Matches**: Verify all exact matches return `MatchType = "Exact"`
2. **Annotation Removal**: Verify parentheses and tildes are handled correctly
3. **Bidirectional Infinitive**: Verify "to" prefix works both ways
4. **Whitespace/Punctuation**: Verify normalization tolerates formatting differences
5. **Korean Unicode**: Verify NFC normalization with Hangul characters
6. **Edge Cases**: Verify null/empty inputs, multiple annotations, only-annotation inputs
7. **False Positives**: Verify incorrect answers are rejected

### Required Integration Tests

1. **VocabularyQuizPage**: Verify `CheckAnswer()` uses `FuzzyMatcher.Evaluate()`
2. **Feedback Display**: Verify correct messages for exact vs fuzzy vs incorrect
3. **Progress Tracking**: Verify both match types record as correct
4. **Logging**: Verify debug/info logs include match type and forms

### Performance Tests

1. **Benchmark**: Verify <10ms per call (measure on all platforms)
2. **Stress Test**: Verify 1000 consecutive evaluations don't degrade performance
3. **Memory**: Verify no memory leaks or excessive allocations

---

## Documentation Contract

### XML Documentation

All public members MUST have XML doc comments:
- `<summary>` for class/method purpose
- `<param>` for each parameter
- `<returns>` for return values
- `<example>` for common usage patterns
- `<remarks>` for behavioral contracts and edge cases

### Code Comments

Internal logic SHOULD have inline comments for:
- Regex patterns (explain what each pattern matches)
- Normalization steps (explain why each step is necessary)
- Edge case handling (explain rationale for decisions)

### External Documentation

- Feature spec: `/specs/001-fuzzy-text-matching/spec.md`
- Implementation plan: `/specs/001-fuzzy-text-matching/plan.md`
- Data model: `/specs/001-fuzzy-text-matching/data-model.md`
- This contract: `/specs/001-fuzzy-text-matching/contracts/fuzzy-matcher-api.md`
- Quickstart guide: `/specs/001-fuzzy-text-matching/quickstart.md`

---

## Change Log

| Version | Date | Changes |
|---------|------|---------|
| 1.0.0 | 2025-12-14 | Initial contract definition |
