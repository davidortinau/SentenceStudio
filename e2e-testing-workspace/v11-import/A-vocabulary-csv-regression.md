# Scenario A: Vocabulary CSV Import (v1.0 Regression)

**Status:** AUTHORED, NOT YET RUN  
**Priority:** P0 — existing functionality must not regress  
**Feature branch:** `feature/import-content-mvp`

## Preconditions

- Aspire stack running (`cd src/SentenceStudio.AppHost && aspire run`)
- Webapp accessible at `https://localhost:7071/`
- Signed in as test user David (Korean, `f452438c-b0ac-4770-afea-0803e2670df5`)
- Note current VocabularyWord row count before test

## Pre-test DB Baseline

```sql
-- Record starting count
SELECT COUNT(*) AS baseline_count FROM VocabularyWords
WHERE UserProfileId = 'f452438c-b0ac-4770-afea-0803e2670df5';
```

## Steps

### Step 1: Navigate to Import Content

1. Playwright: navigate to `https://localhost:7071/import-content`
2. **Expected:** Import wizard loads with Step 1 (Source selection) visible

### Step 2: Select Content Type "Vocabulary"

1. Find the content type selector/radio buttons
2. Select "Vocabulary"
3. **Expected:** Vocabulary option is selected; UI shows text input area

### Step 3: Paste CSV Content

1. Paste the fixture content from `fixtures/vocab-csv.csv`:
   ```
   책,book
   읽다,to read
   학교,school
   선생님,teacher
   공부하다,to study
   ```
2. **Expected:** Text area populated with CSV content

### Step 4: Preview

1. Click "Preview" / "Next" button
2. **Expected:** Preview table shows 5 rows with:
   - Target Language Term column: 책, 읽다, 학교, 선생님, 공부하다
   - Native Language Term column: book, to read, school, teacher, to study
3. **Expected:** No validation errors or warnings

### Step 5: Commit Import

1. Click "Commit" / "Import" button in header
2. Wait for import to complete (spinner then results)
3. **Expected:** Results summary shows:
   - Created: 5 (or fewer if dedup kicks in for pre-existing terms)
   - Failed: 0
   - No error toasts

### Step 6: Verify DB — VocabularyWord rows

```sql
SELECT Id, TargetLanguageTerm, NativeLanguageTerm, LexicalUnitType, UserProfileId
FROM VocabularyWords
WHERE UserProfileId = 'f452438c-b0ac-4770-afea-0803e2670df5'
  AND TargetLanguageTerm IN ('책', '읽다', '학교', '선생님', '공부하다')
ORDER BY CreatedAt DESC;
```

**Expected:**
- 5 rows returned (unless deduped against existing)
- All rows have `LexicalUnitType = 1` (Word)
- `UserProfileId` matches test user
- `NativeLanguageTerm` values match CSV input

### Step 7: Verify DB — LearningResource created

```sql
SELECT Id, Title, MediaType, UserProfileId, CreatedAt
FROM LearningResources
WHERE UserProfileId = 'f452438c-b0ac-4770-afea-0803e2670df5'
ORDER BY CreatedAt DESC
LIMIT 1;
```

**Expected:**
- New LearningResource row exists
- `MediaType` = "Vocabulary List" (or equivalent v1.0 value)

### Step 8: Verify DB — ResourceVocabularyMapping

```sql
SELECT rvm.LearningResourceId, rvm.VocabularyWordId
FROM ResourceVocabularyMapping rvm
JOIN LearningResources lr ON lr.Id = rvm.LearningResourceId
WHERE lr.UserProfileId = 'f452438c-b0ac-4770-afea-0803e2670df5'
  AND lr.CreatedAt > datetime('now', '-5 minutes');
```

**Expected:**
- 5 mapping rows linking the new resource to the 5 vocab words

### Step 9: Verify Aspire logs

Check Aspire structured logs for the API service:
- No 500/503 errors during import
- No NullReferenceException
- No unhandled exceptions

## Pass Criteria

- [ ] All 5 CSV rows imported as VocabularyWord with `LexicalUnitType=1` (Word)
- [ ] LearningResource created
- [ ] ResourceVocabularyMapping rows link resource to words
- [ ] No errors in logs
- [ ] Results summary shows correct counts
