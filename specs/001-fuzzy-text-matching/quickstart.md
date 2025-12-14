# Quickstart: Fuzzy Text Matching

**Phase 1 Output** | **Date**: 2025-12-14 | **Branch**: `001-fuzzy-text-matching`

Welcome! This guide helps you understand and implement fuzzy text matching for vocabulary quiz answers in SentenceStudio.

---

## What is Fuzzy Text Matching?

Users practicing vocabulary often know the correct word but fail quiz questions because they forget annotation formatting. For example:

- Expected: `"take (a photo)"` → User types: `"take"` → **Should be CORRECT** ✅
- Expected: `"ding~ (a sound)"` → User types: `"ding"` → **Should be CORRECT** ✅
- Expected: `"to choose"` → User types: `"choose"` → **Should be CORRECT** ✅

Fuzzy matching accepts these answers by comparing the **core word** after removing annotations, punctuation, and whitespace differences.

---

## Quick Implementation

### 1. Add FuzzyMatcher Service

Create `/src/SentenceStudio/Services/FuzzyMatcher.cs`:

```csharp
using System.Text;
using System.Text.RegularExpressions;

namespace SentenceStudio.Services;

public class FuzzyMatchResult
{
    public bool IsCorrect { get; set; }
    public string? MatchType { get; set; }
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

**Key Points**:
- **Static class**: No DI needed, pure logic
- **Compiled regex**: Performance optimization (<1ms evaluation)
- **Unicode NFC**: Handles Korean character encoding consistency
- **Bidirectional infinitive**: "to choose" ↔ "choose"

---

### 2. Update VocabularyQuizPage

Modify `/src/SentenceStudio/Pages/VocabularyQuiz/VocabularyQuizPage.cs`:

**Find** (around line 1395):
```csharp
var isCorrect = string.Equals(answer.Trim(), State.CurrentTargetLanguageTerm.Trim(),
    StringComparison.OrdinalIgnoreCase);
```

**Replace with**:
```csharp
var matchResult = FuzzyMatcher.Evaluate(answer, State.CurrentTargetLanguageTerm);
var isCorrect = matchResult.IsCorrect;

_logger.LogDebug("🔍 CheckAnswer: answer='{Answer}', expected='{Expected}', matchType='{MatchType}'", 
    answer, State.CurrentTargetLanguageTerm, matchResult.MatchType);
```

**Then find** (UI feedback update section, around line 1432):
```csharp
// Enhanced feedback: Update UI based on enhanced progress
_logger.LogDebug("🎨 CheckAnswer: Updating UI feedback...");
await UpdateUIBasedOnEnhancedProgress(currentItem, isCorrect);
_logger.LogDebug("✅ CheckAnswer: UI feedback updated");
```

**Add after**:
```csharp
// NEW: Show fuzzy match feedback
if (isCorrect && matchResult.MatchType == "Fuzzy")
{
    _logger.LogInformation("✨ Fuzzy match accepted: user='{User}', complete='{Complete}'", 
        answer, matchResult.CompleteForm);
    
    // Update state with fuzzy match message
    SetState(s =>
    {
        s.FeedbackMessage = string.Format($"{_localize["QuizFuzzyMatchCorrect"]}", 
            matchResult.CompleteForm);
    });
}
```

**Note**: You may need to add `FeedbackMessage` to page state if it doesn't exist, or use existing feedback mechanism.

---

### 3. Add Localization Keys

**English** (`/src/SentenceStudio/Resources/Strings/AppResources.resx`):
```xml
<data name="QuizFuzzyMatchCorrect" xml:space="preserve">
  <value>✓ Correct! Full form: {0}</value>
</data>
```

**Korean** (`/src/SentenceStudio/Resources/Strings/AppResources.ko-KR.resx`):
```xml
<data name="QuizFuzzyMatchCorrect" xml:space="preserve">
  <value>✓ 정답! 전체 형태: {0}</value>
</data>
```

**Rebuild** after adding resource keys:
```bash
cd src
dotnet build SentenceStudio/SentenceStudio.csproj -f net10.0-maccatalyst
```

---

## Testing Your Implementation

### Manual Test Cases

Run vocabulary quiz with these test words:

| Expected Answer | User Input | Expected Result |
|----------------|------------|-----------------|
| `take (a photo)` | `take` | ✓ Correct! Full form: take (a photo) |
| `ding~ (a sound)` | `ding` | ✓ Correct! Full form: ding~ (a sound) |
| `to choose` | `choose` | ✓ Correct! Full form: to choose |
| `choose` | `to choose` | ✓ Correct! Full form: choose |
| `don't` | `dont` | ✓ Correct! Full form: don't |
| `Take` | `take` | ✓ Correct! (exact match, case-insensitive) |
| `wrong` | `take` | ✗ Incorrect |

### Quick Debugging

Add this temporary debug method to VocabularyQuizPage:

```csharp
private void TestFuzzyMatcher()
{
    var tests = new[]
    {
        ("take", "take (a photo)"),
        ("ding", "ding~ (a sound)"),
        ("choose", "to choose"),
        ("to choose", "choose"),
        ("dont", "don't"),
        ("wrong", "take")
    };

    foreach (var (user, expected) in tests)
    {
        var result = FuzzyMatcher.Evaluate(user, expected);
        _logger.LogInformation("Test: '{User}' vs '{Expected}' → IsCorrect={IsCorrect}, MatchType={MatchType}", 
            user, expected, result.IsCorrect, result.MatchType);
    }
}
```

Call `TestFuzzyMatcher()` in `OnMounted()` to see results in console logs.

---

## Understanding the Normalization Process

### Example: `"take (a photo)"` vs `"take"`

**User Input**: `"take"`
1. Unicode NFC: `"take"` (no change for ASCII)
2. Remove parentheses: `"take"` (no parentheses)
3. Remove tildes: `"take"` (no tildes)
4. Trim: `"take"`
5. Remove punctuation: `"take"` (no punctuation)
6. Remove "to ": `"take"` (doesn't start with "to ")
7. **Result**: `"take"`

**Expected Answer**: `"take (a photo)"`
1. Unicode NFC: `"take (a photo)"`
2. Remove parentheses: `"take "` (removes `" (a photo)"`)
3. Remove tildes: `"take "`
4. Trim: `"take"`
5. Remove punctuation: `"take"`
6. Remove "to ": `"take"`
7. **Result**: `"take"`

**Comparison**: `"take"` == `"take"` → **MATCH** ✅

**Match Type**: Original inputs differ (`"take"` vs `"take (a photo)"`) → **Fuzzy**

**CompleteForm**: `"take (a photo)"` (shown to user for learning)

---

## Performance Optimization

### Why Compiled Regex?

```csharp
// ❌ SLOW (recompiles on every call)
var result = Regex.Replace(text, @"\s*\([^)]*\)", "");

// ✅ FAST (compiled once, reused)
private static readonly Regex ParenthesesPattern = 
    new Regex(@"\s*\([^)]*\)", RegexOptions.Compiled);

var result = ParenthesesPattern.Replace(text, "");
```

**Performance**: Compiled regex is ~5-10x faster for repeated patterns.

### Expected Performance

- **Normalization**: ~0.3ms per string
- **Comparison**: ~0.01ms
- **Total**: ~0.6ms per evaluation (16x faster than 10ms requirement)

---

## Common Pitfalls

### 1. Forgetting String Interpolation for Localization

❌ **WRONG**:
```csharp
var message = _localize["QuizFuzzyMatchCorrect"]; // Returns object, not string!
```

✅ **CORRECT**:
```csharp
var message = $"{_localize["QuizFuzzyMatchCorrect"]}"; // Returns string
```

### 2. Showing CompleteForm for Exact Matches

❌ **WRONG**:
```csharp
if (isCorrect)
{
    var message = string.Format($"{_localize["QuizFuzzyMatchCorrect"]}", expectedAnswer);
}
```

✅ **CORRECT**:
```csharp
if (isCorrect && matchResult.MatchType == "Fuzzy")
{
    var message = string.Format($"{_localize["QuizFuzzyMatchCorrect"]}", matchResult.CompleteForm);
}
else if (isCorrect)
{
    var message = $"{_localize["QuizCorrect"]}"; // Standard message for exact matches
}
```

### 3. Over-Engineering Normalization

❌ **WRONG** (accepting misspellings):
```csharp
// Using Levenshtein distance to accept 1-2 char differences
if (LevenshteinDistance(user, expected) <= 2) return true; // TOO PERMISSIVE!
```

✅ **CORRECT** (only annotations/formatting):
```csharp
// Normalize core word extraction only
return NormalizeText(user) == NormalizeText(expected);
```

**Rationale**: Accepting misspellings creates false positives (violates success criteria).

---

## Cross-Platform Considerations

### Korean Input Methods (IME)

**macOS/iOS**: Input is evaluated AFTER user submits (presses Enter/Done). IME composition doesn't affect fuzzy matching.

**Android**: Same behavior - fuzzy matching happens on final submitted text.

**No Special Handling Needed**: FuzzyMatcher operates on submitted strings, not live IME composition.

### Unicode Normalization

**Why NFC?**
- Korean Hangul can be stored as:
  - **NFC (Precomposed)**: `"가"` (single character)
  - **NFD (Decomposed)**: `"ᄀ" + "ᅡ"` (consonant + vowel)

**Without Normalization**:
```csharp
"가".Equals("가") // FALSE (different representations)
```

**With NFC Normalization**:
```csharp
"가".Normalize(NormalizationForm.FormC).Equals("가".Normalize(NormalizationForm.FormC)) // TRUE
```

**Result**: Consistent matching regardless of input method or database encoding.

---

## Debugging Tips

### Enable Detailed Logging

In VocabularyQuizPage.cs, add these logs to CheckAnswer():

```csharp
_logger.LogDebug("🔍 FuzzyMatch Debug:");
_logger.LogDebug("  User input (raw): '{Raw}'", answer);
_logger.LogDebug("  User input (normalized): '{Normalized}'", 
    FuzzyMatcher.NormalizeText(answer)); // Make NormalizeText internal if needed
_logger.LogDebug("  Expected (raw): '{Raw}'", State.CurrentTargetLanguageTerm);
_logger.LogDebug("  Expected (normalized): '{Normalized}'", 
    FuzzyMatcher.NormalizeText(State.CurrentTargetLanguageTerm));
_logger.LogDebug("  Match result: IsCorrect={IsCorrect}, MatchType={MatchType}", 
    matchResult.IsCorrect, matchResult.MatchType);
```

### Console Output Location by Platform

- **macOS**: `Console.app` or `log show --predicate 'process == "SentenceStudio"'`
- **iOS Simulator**: Terminal `xcrun simctl spawn booted log stream`
- **Android**: `adb logcat | grep SentenceStudio`
- **Windows**: VS Code Debug Console

---

## Next Steps

1. **Build & Test**: Run vocabulary quiz on your platform
2. **Unit Tests**: Add tests to `tests/SentenceStudio.Tests/Services/FuzzyMatcherTests.cs`
3. **Integration Test**: Verify feedback messages display correctly
4. **Performance Benchmark**: Measure evaluation time (should be <1ms)
5. **User Testing**: Gather feedback on fuzzy match acceptance

---

## FAQ

### Q: Will this accept misspellings like "tak" for "take"?

**A**: No. Fuzzy matching only removes formatting (annotations, punctuation, whitespace). Core word spelling must be correct.

### Q: What if I want to allow 1-2 character typos?

**A**: That's out of scope for this feature (violates zero false positives requirement). Consider adding as a separate feature with user preference toggle.

### Q: Does this work for multiple-choice questions?

**A**: Yes, the same evaluation logic applies. However, multiple-choice options should already be normalized, so fuzzy matching is most beneficial for text entry mode.

### Q: Can I customize normalization rules per language?

**A**: Current implementation applies same rules to all languages. For language-specific rules, consider adding an optional `LanguageCode` parameter to `Evaluate()` in future version.

### Q: What if vocabulary has multiple parenthetical annotations?

**A**: All parentheses are removed: `"word (note1) (note2)"` → `"word"`. This matches user expectation of core word.

---

## Support

- **Feature Spec**: [spec.md](./spec.md)
- **Implementation Plan**: [plan.md](./plan.md)
- **Data Model**: [data-model.md](./data-model.md)
- **API Contract**: [contracts/fuzzy-matcher-api.md](./contracts/fuzzy-matcher-api.md)
- **Issues**: Create GitHub issue with label `fuzzy-matching`

---

**Last Updated**: 2025-12-14  
**Author**: /speckit.plan workflow  
**Branch**: `001-fuzzy-text-matching`
