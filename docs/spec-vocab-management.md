# Vocabulary Management Spec

## Current State

Vocabulary is currently managed as lists (VocabularyList), each typically associated with a lesson, chapter, or other content. Each list contains VocabularyWord objects, which may be either words or phrases (no restriction). Words are often duplicated across lists and resources. Associations are managed via many-to-many mapping tables. Data is stored locally in SQLite, with VocabularyService and LearningResourceRepository handling CRUD and association logic. There is support for ingesting transcripts and generating vocabulary via AI.

## Problems

- **Duplication:** Words/phrases are duplicated across lists and resources, leading to inefficiency and inconsistency.
- **Inefficient Data Transfer:** Sending large transcripts or vocab lists to the cloud is inefficient.
- **Tracking:** There is no unified way to track user comprehension/fluency per word across all content.

## Goals

1. **Centralized Vocabulary Store:**
   - Store each unique word/phrase only once, with global IDs.
   - Avoid duplication by referencing words from lists/resources.

2. **Flexible Associations:**
   - Allow words/phrases to be associated with multiple lists, lessons, or resources (many-to-many).
   - Support both vocabulary lists and direct association with LearningResource objects.

3. **Comprehension Tracking:**
   - Track user comprehension/fluency per word/phrase, similar to Kimchi Reader.
   - Use this data to score new content and personalize study recommendations.

4. **Efficient Ingestion:**
   - When ingesting new content (e.g., a transcript), automatically:
     - Identify new vocabulary (words/phrases not in the user's known set).
     - Identify grammar skills.
     - Score expected comprehension.
     - Produce targeted vocab and skills filters for study.

5. **Cloud Architecture:**
   - Minimize data transfer by syncing only changes (new/updated words, associations, and user stats).
   - Consider using a vector database for semantic search, similarity, and fast lookup of related words/phrases.
   - Support offline-first operation with local SQLite, syncing to the cloud as needed.

## Proposed Architecture

### Data Model

- **VocabularyWord**
  - Unique global ID
  - TargetLanguageTerm (e.g., Korean)
  - NativeLanguageTerm (e.g., English)
  - [Optional] Part of speech, tags, frequency, etc.
  - [Optional] Vector embedding for semantic search

- **VocabularyList**
  - ID, Name, CreatedAt, UpdatedAt
  - Many-to-many with VocabularyWord

- **LearningResource**
  - ID, Title, Description, Language, etc.
  - Many-to-many with VocabularyWord

- **UserVocabularyStats**
  - UserID, VocabularyWordID, Fluency/Comprehension score, LastSeen, etc.

### Workflow

#### Ingesting New Content
1. Extract transcript/content.
2. Use AI/NLP to extract candidate vocabulary and grammar points.
3. For each candidate word/phrase:
   - Check if it exists in the global VocabularyWord table (by normalized form).
   - If not, add it.
   - Associate with the relevant list/resource.
4. Update user stats as they interact with the content.

### Tracking and Scoring

#### UserVocabularyStats Model

```csharp
public class UserVocabularyStats
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int VocabularyWordId { get; set; }
    
    // Comprehension score (0-100)
    public double ComprehensionScore { get; set; } = 0;
    
    // Number of times seen/encountered
    public int TimesEncountered { get; set; } = 0;
    
    // Number of correct responses
    public int CorrectResponses { get; set; } = 0;
    
    // Number of incorrect responses
    public int IncorrectResponses { get; set; } = 0;
    
    // Last time the word was seen
    public DateTime LastSeen { get; set; }
    
    // Last time the score was updated
    public DateTime LastUpdated { get; set; }
    
    // Context where word was last used (e.g., "clozure", "translation", "writing")
    public string LastContext { get; set; }
    
    // Spaced repetition interval (days until next review)
    public int NextReviewInterval { get; set; } = 1;
    
    // Navigation properties
    public User User { get; set; }
    public VocabularyWord VocabularyWord { get; set; }
}
```

#### Scoring Algorithm

The comprehension score is calculated based on:
- **Accuracy rate**: (CorrectResponses / (CorrectResponses + IncorrectResponses))
- **Recency**: Recent correct responses have more weight
- **Context difficulty**: Different activities have different weights

```csharp
public double CalculateComprehensionScore(UserVocabularyStats stats)
{
    if (stats.TimesEncountered == 0) return 0;
    
    // Base accuracy (0-70 points)
    var totalResponses = stats.CorrectResponses + stats.IncorrectResponses;
    var accuracy = totalResponses > 0 
        ? (double)stats.CorrectResponses / totalResponses 
        : 0;
    var accuracyScore = accuracy * 70;
    
    // Recency bonus (0-20 points)
    var daysSinceLastSeen = (DateTime.Now - stats.LastSeen).TotalDays;
    var recencyScore = Math.Max(0, 20 - (daysSinceLastSeen * 2));
    
    // Exposure bonus (0-10 points)
    var exposureScore = Math.Min(10, stats.TimesEncountered);
    
    return Math.Min(100, accuracyScore + recencyScore + exposureScore);
}
```

#### Example 1: Clozure Activity

**Scenario**: User completes a clozure exercise with the sentence "나는 커피를 _____ 싶어요" (I want to drink coffee), where the answer is "마시고" (drink-and).

**Success Case**:
```csharp
// User correctly fills in "마시고"
public async Task UpdateVocabularyStatsForClozure(int userId, int vocabWordId, bool isCorrect)
{
    var stats = await GetOrCreateUserVocabularyStats(userId, vocabWordId);
    
    stats.TimesEncountered++;
    stats.LastSeen = DateTime.Now;
    stats.LastContext = "clozure";
    
    if (isCorrect)
    {
        stats.CorrectResponses++;
        
        // Increase comprehension score
        var previousScore = stats.ComprehensionScore;
        stats.ComprehensionScore = CalculateComprehensionScore(stats);
        
        // Adjust spaced repetition interval
        if (stats.ComprehensionScore > 80)
        {
            stats.NextReviewInterval = Math.Min(30, stats.NextReviewInterval * 2);
        }
        else if (stats.ComprehensionScore > 60)
        {
            stats.NextReviewInterval = Math.Min(14, stats.NextReviewInterval + 2);
        }
    }
    else
    {
        stats.IncorrectResponses++;
        
        // Decrease comprehension score
        stats.ComprehensionScore = CalculateComprehensionScore(stats);
        
        // Reset review interval for more practice
        stats.NextReviewInterval = 1;
    }
    
    stats.LastUpdated = DateTime.Now;
    await SaveStats(stats);
}
```

**Failure Case**:
- User types "먹고" (eat-and) instead of "마시고" (drink-and)
- The app tracks both words:
  - "마시고": Marked as incorrect (missed opportunity)
  - "먹고": Marked as attempted but in wrong context

#### Example 2: Translation Activity

**Scenario**: User translates "I like to read books" and produces "나는 책을 읽는 것을 좋아해요"

**Processing**:
```csharp
public async Task UpdateVocabularyStatsForTranslation(
    int userId, 
    List<int> targetVocabIds, 
    List<int> producedVocabIds,
    double translationQuality) // 0-1 score from AI evaluation
{
    // Update stats for correctly used vocabulary
    var correctlyUsed = targetVocabIds.Intersect(producedVocabIds);
    foreach (var vocabId in correctlyUsed)
    {
        var stats = await GetOrCreateUserVocabularyStats(userId, vocabId);
        stats.TimesEncountered++;
        stats.CorrectResponses++;
        stats.LastSeen = DateTime.Now;
        stats.LastContext = "translation";
        
        // Weight by translation quality
        var scoreIncrease = 5 * translationQuality;
        stats.ComprehensionScore = Math.Min(100, 
            stats.ComprehensionScore + scoreIncrease);
        
        await SaveStats(stats);
    }
    
    // Update stats for missed vocabulary
    var missedVocab = targetVocabIds.Except(producedVocabIds);
    foreach (var vocabId in missedVocab)
    {
        var stats = await GetOrCreateUserVocabularyStats(userId, vocabId);
        stats.TimesEncountered++;
        stats.IncorrectResponses++;
        stats.LastContext = "translation_missed";
        
        // Small penalty for not using expected vocabulary
        stats.ComprehensionScore = Math.Max(0, 
            stats.ComprehensionScore - 2);
        
        await SaveStats(stats);
    }
}
```

#### Content Scoring Based on User Stats

When presenting new content, calculate expected comprehension:

```csharp
public async Task<ContentComprehensionScore> ScoreContent(
    int userId, 
    List<int> contentVocabularyIds)
{
    var userStats = await GetUserVocabularyStats(userId, contentVocabularyIds);
    
    // Calculate coverage (what % of words does user know)
    var knownWords = userStats.Count(s => s.ComprehensionScore > 60);
    var coverage = (double)knownWords / contentVocabularyIds.Count;
    
    // Calculate average comprehension
    var avgComprehension = userStats.Any() 
        ? userStats.Average(s => s.ComprehensionScore) 
        : 0;
    
    // Identify challenging vocabulary
    var challengingWords = userStats
        .Where(s => s.ComprehensionScore < 40)
        .Select(s => s.VocabularyWordId)
        .ToList();
    
    return new ContentComprehensionScore
    {
        Coverage = coverage,
        AverageComprehension = avgComprehension,
        ExpectedDifficulty = CalculateDifficulty(coverage, avgComprehension),
        ChallengingVocabularyIds = challengingWords,
        RecommendedForStudy = coverage > 0.7 && coverage < 0.95
    };
}
```

#### Adaptive Content Selection

Use vocabulary stats to recommend appropriate content:

```csharp
public async Task<List<LearningResource>> RecommendContent(
    int userId,
    int targetDifficulty = 75) // 75% comprehension is optimal for learning
{
    var allResources = await GetAvailableResources();
    var scoredResources = new List<(LearningResource resource, double score)>();
    
    foreach (var resource in allResources)
    {
        var vocabIds = await GetResourceVocabularyIds(resource.Id);
        var comprehension = await ScoreContent(userId, vocabIds);
        
        // Find resources close to target difficulty
        var difficultyDiff = Math.Abs(comprehension.AverageComprehension - targetDifficulty);
        scoredResources.Add((resource, difficultyDiff));
    }
    
    // Return resources ordered by how close they are to target difficulty
    return scoredResources
        .OrderBy(r => r.score)
        .Take(10)
        .Select(r => r.resource)
        .ToList();
}
```

### Cloud Sync
- Sync only deltas (new/updated words, associations, user stats).
- Optionally, use a vector DB (e.g., Pinecone, Weaviate) for semantic search and similarity queries.
- Store embeddings for words/phrases to enable fast lookup of related vocabulary.

## Open Questions

- Should all vocabulary be globally unique, or allow for context-specific variants (e.g., idioms, multi-word expressions)?
- What is the best way to handle multi-word phrases and their semantic relationships?
- What is the minimum viable cloud architecture (relational DB, vector DB, hybrid)?
- How to handle user privacy and data ownership in cloud sync?

## Next Steps

1. Refactor local data model to ensure global uniqueness of VocabularyWord.
2. Implement association tables for lists/resources <-> words.
3. Add UserVocabularyStats for tracking comprehension/fluency.
4. Prototype cloud sync (start with relational DB, add vector DB for search if needed).
5. Design ingestion workflow for new content.
6. Define API for client-cloud sync and semantic search.