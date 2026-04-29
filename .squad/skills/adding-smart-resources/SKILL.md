# Skill: Adding Smart Resources

**Author**: Wash (Backend Dev)  
**Created**: 2026-01-29  
**Context**: Extracted from Sentences smart resource implementation

## When to Use This Skill

Use this skill when adding a new **smart resource type** to `SmartResourceService`. Smart resources are system-generated, dynamic vocabulary lists that auto-populate based on SRS (Spaced Repetition System) rules or other programmatic criteria.

Examples of smart resources:
- **DailyReview**: Words due for SRS review today
- **NewWords**: Words never practiced
- **Struggling**: Low mastery despite multiple attempts
- **Phrases**: All phrase vocabulary
- **Sentences**: All sentence vocabulary

## Pre-Requisites

Before adding a new smart resource, ensure:
1. The filtering criteria can be expressed via existing `VocabularyWord` or `VocabularyProgress` fields
2. The resource type is semantically distinct from existing smart resources (no overlap)
3. You understand the user-scoping pattern (see below)

## Implementation Pattern

### Step 1: Add Constant

In `SmartResourceService.cs`, add a public constant near the top (after existing type constants):

```csharp
public const string SmartResourceType_YourType = "YourType";
```

### Step 2: Add Resource Definition

In `InitializeSmartResourcesAsync()`, add a new definition to the `definitions` array (preserving order):

```csharp
new LearningResource
{
    Title = "Your Resource Title",
    Description = "Clear description of what this resource contains",
    MediaType = "Smart Vocabulary List",
    Language = targetLanguage,
    Tags = "system-generated,dynamic,your-tag",
    IsSmartResource = true,
    SmartResourceType = SmartResourceType_YourType,
    CreatedAt = DateTime.Now,
    UpdatedAt = DateTime.Now
}
```

**Icon/Emoji Convention**: Choose a semantically distinct icon that doesn't conflict with existing resources:
- DailyReview: 📅
- NewWords: ✨
- Struggling: 💪
- Phrases: 📝
- Sentences: 📖

### Step 3: Add Dispatch Case

In `GetSmartResourceVocabularyIdsAsync()`, add a new switch case:

```csharp
SmartResourceType_YourType => await GetYourTypeVocabularyIdsAsync(userId),
```

### Step 4: Implement Selection Method

Add a private method following the naming pattern `Get{Type}VocabularyIdsAsync`:

```csharp
/// <summary>
/// Get vocabulary IDs for YourType: [clear selection criteria].
/// Selection: [exact filter logic], scoped by user via VocabularyProgress.
/// </summary>
private async Task<List<string>> GetYourTypeVocabularyIdsAsync(string userId = "")
{
    // User-scoping pattern (see below)
    var allProgress = await _progressRepo.ListAsync();
    var userWordIds = allProgress
        .Where(vp => vp.UserId == userId)
        .Select(vp => vp.VocabularyWordId)
        .Distinct()
        .ToList();

    if (userWordIds.Count == 0)
    {
        _logger.LogDebug("🔍 YourType found 0 entries (no user progress)");
        return new List<string>();
    }

    using var scope = _serviceProvider.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

    var wordIds = await db.VocabularyWords
        .Where(w => userWordIds.Contains(w.Id))
        .Where(w => /* your filtering criteria */)
        .Select(w => w.Id)
        .ToListAsync();

    _logger.LogDebug("🔍 YourType found {Count} entries", wordIds.Count);
    return wordIds;
}
```

### User-Scoping Pattern (Critical)

**IMPORTANT**: `VocabularyWord` has **NO UserProfileId** field. User ownership is tracked through the `VocabularyProgress` table via `VocabularyProgress.UserId`.

**Required pattern**:
1. Query all `VocabularyProgress` records
2. Filter by `UserId`
3. Extract distinct `VocabularyWordId` list
4. Join to `VocabularyWords` table with `WHERE userWordIds.Contains(w.Id)`
5. Apply additional filtering criteria (LexicalUnitType, mastery thresholds, etc.)

**DO NOT**:
- Query `VocabularyWords` directly without user scoping
- Assume `VocabularyWord.UserProfileId` exists (it doesn't)
- Use `LearningResource.UserProfileId` for filtering (smart resources may be user-specific or global)

## Testing Pattern

Create a parallel test file in `tests/SentenceStudio.UnitTests/Services/` following this structure:

### File Name
`SmartResource{Type}Tests.cs`

### Test Scenarios (Minimum)

1. **Mixed vocabulary**: Verify correct type filtering (includes target type, excludes others)
2. **Empty state**: Zero matching vocabulary → empty mapping (resource still exists)
3. **Unknown lexical type**: Explicitly exclude `LexicalUnitType.Unknown` if applicable
4. **Multi-user scoping**: User A's vocabulary not included in User B's resource
5. **Idempotency**: Refresh twice → same mapping both times

## Auto-Refresh Behavior

**Good news**: The service handles upgrades automatically!

**Per-type idempotency** (lines 58-63 in `InitializeSmartResourcesAsync`):
- Checks `existingTypes` HashSet by `SmartResourceType`
- Only creates missing types
- Immediately refreshes newly created resources

**Upgrade path**:
1. **First launch after upgrade**: New resource type is created and populated
2. **Next refresh cycle** (app restart or explicit refresh): All smart resources refreshed via `RefreshAllSmartResourcesAsync`
3. **No explicit migration needed**: The refresh logic naturally updates all mappings

## Build Verification

```bash
# Build Shared project
dotnet build src/SentenceStudio.Shared/SentenceStudio.Shared.csproj -f net10.0

# Run tests
dotnet test tests/SentenceStudio.UnitTests/SentenceStudio.UnitTests.csproj \
  --filter "FullyQualifiedName~SmartResource{Type}Tests"
```

## Example: Sentences Smart Resource

See full implementation in:
- **Service**: `src/SentenceStudio.Shared/Services/SmartResourceService.cs` (lines 25, 115-124, 237, 334-360)
- **Tests**: `tests/SentenceStudio.UnitTests/Services/SmartResourceSentencesTests.cs`
- **Decision**: `.squad/decisions/inbox/wash-sentences-smart-resource.md`

Key points from Sentences implementation:
- Split from Phrases resource (narrowed Phrases to `LexicalUnitType.Phrase` only)
- Used 📖 icon (open book) to distinguish from Phrases' 📝 (memo)
- User-scoping via `VocabularyProgress.UserId` join (indirect pattern)
- 6 tests covering mixed vocab, empty state, user scoping, idempotency
- Zero schema changes (SmartResourceType + LexicalUnitType.Sentence already existed)

## Common Pitfalls

1. **Forgetting user scoping**: Always filter via `VocabularyProgress.UserId` join
2. **Direct VocabularyWord query**: VocabularyWord has no UserProfileId — use progress join
3. **Not updating init tests**: Remember to update count assertions in initialization tests
4. **Emoji overload**: Choose semantically distinct icons, don't reuse
5. **Unclear selection criteria**: Document EXACTLY what qualifies vocabulary for this resource
6. **No empty state handling**: Always return empty list if no progress exists (don't throw)

---

**Status**: ✅ Pattern validated via Sentences implementation (2026-01-29)
