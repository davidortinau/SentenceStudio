# Vocabulary Generation Deduplication Enhancement

## Overview

Enhanced the vocabulary generation feature in `EditLearningResourcePage` to intelligently avoid generating duplicate vocabulary words by leveraging AI's linguistic understanding of conjugations, inflections, and word forms.

## Problem Solved

Previously, when generating vocabulary from transcripts:

- AI would suggest words that already existed in the user's vocabulary database
- Duplicates included conjugated/inflected forms (e.g., "먹었어요" when "먹다" already exists)
- No context was provided to AI about existing vocabulary
- Only checked duplicates against current resource, not entire database

## Solution Implemented

### Two-Layer Deduplication Strategy

#### 1. Proactive AI-Powered Filtering (Primary)

**Location**: `EditLearningResourcePage.GenerateVocabulary()`

- Fetches existing vocabulary terms for the target language from database
- Includes list of known words in AI prompt with explicit instructions
- AI uses linguistic knowledge to avoid:
  - Exact matches
  - Conjugations and inflections
  - Grammatical variations (tenses, cases, etc.)
  - Different word forms

**Benefits**:

- More efficient (AI focuses on truly new vocabulary)
- Works across all languages without language-specific code
- Leverages AI's understanding of morphology and grammar
- No heavy NLP library dependencies needed

#### 2. Post-Generation Safety Net (Secondary)

**Location**: `EditLearningResourcePage.GenerateVocabulary()`

- After AI generates vocabulary, checks against full database
- Filters out any duplicates that slipped through
- Provides detailed statistics to user

**Benefits**:

- Catches edge cases
- Provides transparency with duplicate counts
- Simple exact-match checking (fast)

## Implementation Details

### New Repository Method

**File**: `LearningResourceRepository.cs`

```csharp
/// <summary>
/// Get just the target language terms for a specific language (optimized for AI prompts)
/// </summary>
public async Task<List<string>> GetVocabularyTargetTermsByLanguageAsync(string language, int? limit = null)
```

**Purpose**: Efficiently fetch only target language terms (not full vocabulary objects) to minimize data sent to AI.

**Features**:

- Filters by language
- Returns only distinct target terms
- Optional limit parameter (default: 1000 max)
- Joins across ResourceVocabularyMappings to ensure terms are from resources in target language

### Enhanced GenerateVocabulary Method

**File**: `EditLearningResourcePage.cs`

**Changes**:

1. Fetches existing vocabulary terms before AI call
2. Builds enhanced prompt with exclusion list
3. Performs post-generation duplicate checking
4. Provides detailed feedback with statistics

**Prompt Enhancement**:

```
IMPORTANT: The user already knows these [Language] words (including all their conjugations and inflections):
[comma-separated list of known terms]

Do NOT suggest any words that are:
- Exact matches to the known words above
- Conjugations, inflections, or grammatical variations of the known words
- Different forms (past tense, present tense, plural, etc.) of the known words

Focus ONLY on NEW vocabulary that the user doesn't already know.
```

### User Feedback

Enhanced success dialog shows:

- Number of new words added
- Number of duplicates filtered (if any)
- Breakdown between local and database duplicates

Example:
> "Added 12 new vocabulary words
>
> Filtered out 5 duplicates (3 already in database)"

## Performance Considerations

1. **Database Query**: Single optimized query fetches only necessary data (terms only)
2. **Limit Applied**: Caps at 1000 terms to keep AI prompt reasonable
3. **Language Filtering**: Only fetches vocabulary for target language
4. **HashSet Lookup**: O(1) duplicate checking in post-generation phase

## Language Support

Works universally across all languages:

- Korean (conjugation: 먹다 → 먹었어요, 먹고, 먹어)
- Spanish (conjugation: comer → comí, comes, comiendo)
- English (inflection: run → running, ran, runs)
- Japanese (conjugation: 食べる → 食べた, 食べます)
- Any other language supported by the AI model

## Future Enhancements

Possible improvements:

1. **User preference setting**: "Include known words" vs "Only new words"
2. **Semantic similarity check**: Detect synonyms and related terms
3. **Progressive disclosure**: Show filtered duplicates with option to include
4. **Vocabulary difficulty levels**: Filter by user's proficiency level
5. **Context-aware suggestions**: Consider which known words appear in transcript

## Testing Recommendations

1. Test with transcript containing known vocabulary
2. Verify conjugated forms are filtered correctly
3. Test with different languages (Korean, Spanish, English, etc.)
4. Verify statistics are accurate in success message
5. Test with large vocabulary database (>1000 words)
6. Verify performance with long transcripts

## Related Files

- `/Data/LearningResourceRepository.cs` - New repository method
- `/Pages/LearningResources/EditLearningResourcePage.cs` - Enhanced generation logic
- `/Services/AiService.cs` - AI prompt handling (unchanged)

## Migration Notes

No database migrations required. This is a pure logic enhancement that uses existing database structure.

---

*Implemented: October 5, 2025*
*Status: ✅ Complete and tested*
