# Phrase Constituent Backfill Service

**Decision**: Extend `VocabularyClassificationBackfillService` with `BackfillPhraseConstituentsAsync()` instead of creating a separate service.

**Date**: 2025-01-21  
**Owner**: Wash (Backend)  
**Status**: Implemented

## Problem

Existing `Phrase` and `Sentence` VocabularyWords have no `PhraseConstituent` rows linking them to their constituent words. This prevents:
- Understanding which words a user already knows within a phrase
- Showing constituent-level progress in the UI
- Calculating phrase complexity based on known constituents

## Solution

Added `BackfillPhraseConstituentsAsync()` to the existing `VocabularyClassificationBackfillService` to populate `PhraseConstituent` rows for all existing phrases/sentences by tokenizing and matching against each user's Word vocabulary.

### Service Placement

**Why same service?**
- Classification must run BEFORE constituent backfill (need LexicalUnitType to be set)
- Fewer DI registrations
- Clear startup sequence: classify → constituents
- Both are one-time idempotent startup operations
- Keeps related backfill logic together

### Algorithm

For each user (via VocabularyProgress.UserId):

1. **Pre-build lemma dictionary** — Query user's VocabularyProgress with `.Include(vp => vp.VocabularyWord)` once, extract words where `LexicalUnitType == Word`, group by normalized lemma/term to create lookup dictionary. Avoids N+1 queries.

2. **For each Phrase/Sentence** in the user's vocabulary:
   - **Idempotency guard**: Skip if any `PhraseConstituent` rows already exist for this phrase
   - **Tokenize** the `TargetLanguageTerm` using `TokenizePhrase(term, languageCode)`:
     - Split on whitespace (including CJK U+3000)
     - Strip terminal punctuation: `. ? ! 。 ？ ！ , 、`
     - Korean-specific: strip trailing particles from each token
   - **Match tokens** against lemma dictionary:
     - Try exact lemma lookup first (normalized to lowercase)
     - Fallback: substring match for tokens 2+ chars (catches conjugations)
   - **Dedupe**: Don't create multiple PhraseConstituent rows for the same ConstituentWordId within a phrase
   - **Insert** matched constituents with Guid IDs and CreatedAt timestamp

3. **Save** per-user to keep transaction size reasonable

4. **Log** per-phrase counts + total phrases processed + total constituents inserted + elapsed time

### Korean Particle Stripping

Best-effort tokenization for backfill. Strips these particles from token endings before lookup:

```csharp
"이", "가", "을", "를", "은", "는", "에", "의", "로", "으로", 
"와", "과", "에서", "에게", "도", "만", "부터", "까지"
```

**Source**: Common Korean subject/object/topic markers. This is brittle for production parsing but acceptable for backfill where 80% match rate is sufficient.

### Tokenization Method

```csharp
public static IReadOnlyList<string> TokenizePhrase(string term, string languageCode)
```

- **Public static** for unit testing without DB dependency
- Takes language code to enable/disable particle stripping (`ko` → strip, others → don't)
- Returns `IReadOnlyList<string>` of normalized tokens

### Startup Wiring

Called AFTER `BackfillLexicalUnitTypesAsync()` in:
- `SentenceStudio.Api/Program.cs` (line ~273)
- `SentenceStudio.WebApp/Program.cs` (line ~174)
- `SentenceStudio.Shared/Services/SyncService.cs` (line ~212, MAUI startup path)

```csharp
var backfillService = scope.ServiceProvider.GetRequiredService<VocabularyClassificationBackfillService>();
await backfillService.BackfillLexicalUnitTypesAsync();
await backfillService.BackfillPhraseConstituentsAsync(); // NEW
```

### Edge Cases

- **Phrase with zero matched tokens**: Insert no rows. Phrase still has `LexicalUnitType == Phrase`, just no constituents yet. Fine.
- **Phrase shorter than 2 chars**: Skip (garbage data)
- **ConstituentWordId FK is nullable**: Backfill always writes non-null. Nullable supports SetNull cascade when a constituent word is deleted later.
- **Duplicate tokens in phrase**: Dedupe in-memory. The unique composite index `(PhraseWordId, ConstituentWordId)` would reject duplicates anyway, but we avoid churn.

### Performance Characteristics

- **No N+1 queries**: Lemma dictionary built once per user upfront
- **No raw SQL**: EF fluent queries + `SaveChangesAsync`
- **Batched saves**: Per-user, not per-phrase
- **Idempotent**: Second run over same data inserts zero new rows

### Limits Found

- **UserProfileId does not exist on VocabularyWord**: Vocabulary is shared across users. User-specific tracking is via `VocabularyProgress.UserId`. Must query through progress to get user's vocabulary.
- **Substring fallback is O(n)**: For each unmatched token, we scan all userWords in-memory. Acceptable for backfill (one-time) but would need optimization for real-time.
- **Particle stripping is brittle**: Only handles exact suffix matches. Won't catch irregular conjugations or compounded particles. Good enough for backfill.

### Testing Hooks

- `TokenizePhrase(string, string)` is public static → unit testable without DB
- Jayne can test tokenization logic independently:
  - Korean particle stripping ("학교에서" → "학교")
  - Punctuation stripping ("안녕?" → "안녕")
  - CJK space handling
  - Multi-token phrases

### Future Work

- **Better Korean tokenization**: Integrate proper morphological analyzer (Mecab, KoNLPy) for production phrase parsing
- **Real-time constituent detection**: When user adds a new phrase, immediately populate constituents (not just backfill)
- **Constituent quality score**: Weight constituents by match type (exact lemma > substring > none)

## Related

- `.squad/decisions/inbox/wash-backfill-classification.md` — Prior art for classification backfill
- `PhraseConstituent` model documentation (if exists)
- Progress cascade service (parallel work, different concern)
