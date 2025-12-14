# Research: Fuzzy Text Matching for Vocabulary Quiz

**Phase 0 Output** | **Date**: 2025-12-14 | **Branch**: `001-fuzzy-text-matching`

## Research Tasks Completed

### 1. Core Word Extraction Patterns

**Task**: Research annotation patterns in vocabulary entries and determine extraction rules.

**Findings**:

Based on examples from spec:
- `"take (a photo)"` → Extract `"take"`
- `"ding~ (a sound)"` → Extract `"ding"`
- `"to choose"` → Extract `"choose"` (remove infinitive marker)
- `"안녕하세요 (hello)"` → Extract `"안녕하세요"`

**Pattern Analysis**:
1. **Parenthetical annotations**: Text within `()` provides context but isn't part of core word
2. **Tilde descriptors**: `~` followed by parenthetical sound descriptor
3. **Infinitive markers**: English "to" prefix for verbs
4. **Korean annotations**: Same parenthetical pattern applies

**Extraction Algorithm**:
```
1. Remove parenthetical content: regex `\s*\([^)]*\)`
2. Remove tilde and trailing content: regex `~.*$`
3. Trim whitespace
4. For English verbs, remove "to " prefix if present (bidirectional matching)
5. Result: core word
```

**Edge Cases Identified**:
- Multiple parentheses: `"word (context) (more context)"` → Extract `"word"`
- Nested parentheses: Rare in vocabulary, but regex handles outer pair
- Korean particles: Not typically in parentheses, so preserved (e.g., `"가다"` stays `"가다"`)
- Apostrophes in core word: `"don't"` → Normalize by removing apostrophes during comparison

**Decision**: Use regex-based extraction with pattern priority: parentheses first, then tildes, then whitespace normalization.

**Rationale**: Regex provides fast, maintainable pattern matching. Order matters: must remove annotations before normalizing whitespace.

**Alternatives Considered**:
- Manual string parsing (loop through characters): More complex, error-prone, slower
- AI-based parsing: Overkill for deterministic patterns, requires API call (violates offline requirement)
- Dictionary lookup: Not feasible without comprehensive word database

---

### 2. Bidirectional Matching Strategy

**Task**: Determine how to handle "choose" ↔ "to choose" bidirectional matching.

**Findings**:

**Scenario**: User input could be either form, expected answer could be either form.

**Cases**:
- Expected: `"to choose"`, User: `"choose"` → MATCH
- Expected: `"choose"`, User: `"to choose"` → MATCH
- Expected: `"take (a photo)"`, User: `"take"` → MATCH (already covered by annotation removal)

**Matching Logic**:
```
1. Normalize both strings (extract core word)
2. If normalized strings match exactly → CORRECT
3. If one has "to " prefix and other doesn't:
   a. Remove "to " from prefixed version
   b. Compare again
   c. If match → CORRECT (fuzzy match)
```

**Decision**: Normalize both expected and user input identically, including optional "to " removal for English infinitives.

**Rationale**: Symmetric normalization ensures bidirectional matching works naturally. "to" prefix removal only applies to English (Korean doesn't use this pattern).

**Alternatives Considered**:
- Only normalize expected answer: Would miss cases where user adds "to" unnecessarily
- Language-specific rules: Adds complexity; English "to" removal is safe and doesn't affect Korean
- Generate all permutations: Overkill for just "to" prefix

---

### 3. Normalization Best Practices

**Task**: Research text normalization patterns for cross-language comparison.

**Findings**:

**Standard Normalization Steps**:
1. **Trim whitespace**: `" take "` → `"take"`
2. **Case normalization**: `"Take"` → `"take"` (case-insensitive comparison)
3. **Punctuation removal**: `"don't"` → `"dont"` (for comparison only, not storage)
4. **Unicode normalization**: Ensure Korean characters use consistent encoding (NFC recommended)

**.NET Unicode Normalization**:
- Use `string.Normalize(NormalizationForm.FormC)` for consistent Korean character representation
- Hangul can be stored as precomposed (e.g., `"가"`) or decomposed (e.g., `"ᄀ" + "ᅡ"`)
- NFC (Canonical Composition) ensures precomposed form, standard for Korean text

**Comparison Pattern**:
```csharp
var normalizedUser = NormalizeText(userInput);
var normalizedExpected = NormalizeText(expectedAnswer);
bool isMatch = string.Equals(normalizedUser, normalizedExpected, StringComparison.OrdinalIgnoreCase);
```

**NormalizeText() Implementation**:
```csharp
private string NormalizeText(string text)
{
    if (string.IsNullOrWhiteSpace(text)) return string.Empty;
    
    // 1. Unicode normalization (NFC for Korean)
    text = text.Normalize(NormalizationForm.FormC);
    
    // 2. Extract core word (remove annotations)
    text = RemoveParentheses(text);
    text = RemoveTildeDescriptor(text);
    
    // 3. Trim whitespace
    text = text.Trim();
    
    // 4. Remove punctuation (for comparison only)
    text = Regex.Replace(text, @"[^\p{L}\p{N}\s]", "");
    
    // 5. Remove "to " prefix if present (English infinitives)
    if (text.StartsWith("to ", StringComparison.OrdinalIgnoreCase))
        text = text.Substring(3).Trim();
    
    return text;
}
```

**Decision**: Use comprehensive normalization pipeline with Unicode NFC, punctuation removal, and annotation extraction.

**Rationale**: Comprehensive approach handles cross-language requirements. NFC normalization prevents false negatives from Korean encoding differences. Punctuation removal tolerates typing variations.

**Alternatives Considered**:
- Levenshtein distance (fuzzy string matching): Too permissive, could accept misspellings (violates SC-005: zero false positives)
- Phonetic matching: Not applicable across English/Korean
- Stemming/lemmatization: Complex, requires NLP libraries, overkill for annotation removal

---

### 4. Performance Optimization

**Task**: Ensure fuzzy matching completes in <10ms per answer (SC-004).

**Findings**:

**Benchmark Requirements**:
- Target: <10ms evaluation time
- Platforms: iOS, Android, macOS, Windows (must test all)
- Context: Synchronous evaluation during quiz interaction

**Performance Considerations**:
1. **Regex compilation**: Use `RegexOptions.Compiled` for frequently used patterns
2. **String allocation**: Minimize intermediate string allocations
3. **Unicode normalization**: `.Normalize()` is fast (~0.01ms for typical vocab term)
4. **Regex execution**: Pattern matching on 10-50 character strings is sub-millisecond

**Optimized Implementation Pattern**:
```csharp
private static readonly Regex ParenthesesPattern = 
    new Regex(@"\s*\([^)]*\)", RegexOptions.Compiled);
private static readonly Regex TildePattern = 
    new Regex(@"~.*$", RegexOptions.Compiled);
private static readonly Regex PunctuationPattern = 
    new Regex(@"[^\p{L}\p{N}\s]", RegexOptions.Compiled);

private string NormalizeText(string text)
{
    // Use compiled regex for performance
    text = ParenthesesPattern.Replace(text, "");
    text = TildePattern.Replace(text, "");
    text = PunctuationPattern.Replace(text, "");
    return text.Trim();
}
```

**Expected Performance**:
- Regex operations: ~0.5ms total (compiled patterns)
- String operations (Trim, Normalize, ToLower): ~0.1ms
- Comparison: ~0.01ms
- **Total: <1ms** (well within 10ms target)

**Decision**: Use compiled regex patterns with minimal string allocations. No caching needed (evaluation is fast enough).

**Rationale**: Compiled regex provides best balance of performance and maintainability. Overhead is negligible for vocabulary term lengths.

**Alternatives Considered**:
- Precompute normalized forms: Adds database complexity, not needed given <1ms evaluation time
- Cache normalization results: Memory overhead not justified for sub-millisecond operations
- Manual string parsing: More complex, similar performance, harder to maintain

---

### 5. Feedback Message Strategy

**Task**: Determine how to show "fuzzy match accepted" feedback (User Story 3).

**Findings**:

**Feedback Scenarios**:
1. **Exact match**: User enters exact expected answer → Standard "✓ Correct!" message
2. **Fuzzy match**: User enters core word without annotations → "✓ Correct! Full form: {complete_term}"
3. **Incorrect**: User enters wrong word → Standard incorrect feedback (unchanged)

**Detection Logic**:
```csharp
var normalizedMatch = NormalizeText(userInput) == NormalizeText(expectedAnswer);
var exactMatch = userInput.Trim() == expectedAnswer.Trim();

if (normalizedMatch)
{
    if (exactMatch)
        return new FuzzyMatchResult { IsCorrect = true, MatchType = "Exact" };
    else
        return new FuzzyMatchResult 
        { 
            IsCorrect = true, 
            MatchType = "Fuzzy", 
            CompleteForm = expectedAnswer 
        };
}
else
{
    return new FuzzyMatchResult { IsCorrect = false };
}
```

**UI Implementation**:
- Exact match: Use existing `$"{_localize["QuizCorrect"]}"` → "✓ Correct!"
- Fuzzy match: Use new key `$"{_localize["QuizFuzzyMatchCorrect"]}"` → "✓ Correct! Full form: {0}"
  - String.Format with CompleteForm parameter

**Localization Keys**:
```xml
<!-- Resources.resx -->
<data name="QuizFuzzyMatchCorrect" xml:space="preserve">
  <value>✓ Correct! Full form: {0}</value>
</data>

<!-- Resources.ko-KR.resx -->
<data name="QuizFuzzyMatchCorrect" xml:space="preserve">
  <value>✓ 정답! 전체 형태: {0}</value>
</data>
```

**Decision**: Detect match type during evaluation, show enhanced feedback only for fuzzy matches with complete form.

**Rationale**: Provides educational reinforcement without cluttering exact match feedback. Users learn full forms through repeated exposure.

**Alternatives Considered**:
- Always show complete form: Redundant for exact matches, clutters UI
- Highlight differences: Complex to implement, not essential for learning
- Show annotation explanation: Too verbose, breaks quiz flow

---

## Summary of Decisions

### Core Algorithm

**FuzzyMatcher.cs** (new service class):
```csharp
public class FuzzyMatchResult
{
    public bool IsCorrect { get; set; }
    public string MatchType { get; set; } // "Exact" or "Fuzzy"
    public string? CompleteForm { get; set; }
}

public static class FuzzyMatcher
{
    private static readonly Regex ParenthesesPattern = 
        new Regex(@"\s*\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex TildePattern = 
        new Regex(@"~.*$", RegexOptions.Compiled);
    private static readonly Regex PunctuationPattern = 
        new Regex(@"[^\p{L}\p{N}\s]", RegexOptions.Compiled);

    public static FuzzyMatchResult Evaluate(string userInput, string expectedAnswer)
    {
        var normalizedUser = NormalizeText(userInput);
        var normalizedExpected = NormalizeText(expectedAnswer);
        
        bool normalizedMatch = string.Equals(normalizedUser, normalizedExpected, 
            StringComparison.OrdinalIgnoreCase);
        
        if (!normalizedMatch)
            return new FuzzyMatchResult { IsCorrect = false };
        
        bool exactMatch = string.Equals(userInput.Trim(), expectedAnswer.Trim(), 
            StringComparison.OrdinalIgnoreCase);
        
        return new FuzzyMatchResult
        {
            IsCorrect = true,
            MatchType = exactMatch ? "Exact" : "Fuzzy",
            CompleteForm = exactMatch ? null : expectedAnswer
        };
    }

    private static string NormalizeText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        
        text = text.Normalize(NormalizationForm.FormC);
        text = ParenthesesPattern.Replace(text, "");
        text = TildePattern.Replace(text, "");
        text = text.Trim();
        text = PunctuationPattern.Replace(text, "");
        
        if (text.StartsWith("to ", StringComparison.OrdinalIgnoreCase))
            text = text.Substring(3).Trim();
        
        return text;
    }
}
```

### Integration Points

1. **VocabularyQuizPage.cs**: Replace line 1395-1396 (current exact match logic):
   ```csharp
   // OLD:
   var isCorrect = string.Equals(answer.Trim(), State.CurrentTargetLanguageTerm.Trim(),
       StringComparison.OrdinalIgnoreCase);
   
   // NEW:
   var matchResult = FuzzyMatcher.Evaluate(answer, State.CurrentTargetLanguageTerm);
   var isCorrect = matchResult.IsCorrect;
   
   // Update feedback based on match type
   if (matchResult.MatchType == "Fuzzy")
   {
       // Show enhanced feedback with CompleteForm
   }
   ```

2. **Localization**: Add `QuizFuzzyMatchCorrect` key to both resource files

3. **Logging**: Add `ILogger` calls in FuzzyMatcher for debugging match decisions

### Testing Strategy

1. **Unit tests** (FuzzyMatcher.cs):
   - Test all scenarios from spec (take/take (a photo), ding/ding~, choose/to choose)
   - Test Korean annotations
   - Test edge cases (empty strings, only parentheses, multiple annotations)

2. **Integration tests** (VocabularyQuizPage):
   - Verify feedback messages for exact vs fuzzy matches
   - Verify logging output
   - Verify progress tracking (correct answers recorded regardless of match type)

3. **Manual testing** (all platforms):
   - Run quiz with annotated vocabulary
   - Verify <10ms evaluation time (log timestamps)
   - Test Korean input methods (IME)

### Performance Target

- **Expected**: <1ms per evaluation (100x faster than requirement)
- **Verification**: Add timestamp logging around FuzzyMatcher.Evaluate() calls

### Risks & Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| Over-accepting incorrect answers | High (violates SC-005) | Thorough unit tests; manual QA with diverse vocabulary |
| Korean encoding issues | Medium | Use NFC normalization; test with actual Korean IME input |
| Performance on older devices | Low | Compiled regex patterns; benchmark on low-end Android |
| Regex pattern bugs | Medium | Comprehensive unit tests; edge case coverage |

---

## Next Steps (Phase 1)

1. Create data-model.md with FuzzyMatchResult structure
2. Generate contracts/ with FuzzyMatcher API surface
3. Update agent context with fuzzy matching patterns
4. Create quickstart.md for developers
