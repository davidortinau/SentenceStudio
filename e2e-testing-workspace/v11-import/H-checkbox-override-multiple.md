# Scenario H: Checkbox Override — Multiple Checked

**Status:** AUTHORED, NOT YET RUN  
**Priority:** P1 — v1.1 checkbox flexibility  
**Feature branch:** `feature/import-content-mvp`  
**Decision refs:** `squad-captain-checkboxes.md` — "User can override any combination before commit"

## Preconditions

- Aspire stack running
- Signed in as David (Korean)
- Note baseline counts

## Pre-test DB Baseline

```sql
SELECT COUNT(*) AS baseline_words FROM VocabularyWords
WHERE UserProfileId = 'f452438c-b0ac-4770-afea-0803e2670df5';

SELECT COUNT(*) AS baseline_resources FROM LearningResources
WHERE UserProfileId = 'f452438c-b0ac-4770-afea-0803e2670df5'
  AND MediaType = 'Transcript';
```

## Input Text

Use Captain's Korean example from `fixtures/phrase-list-korean.txt`.

## Steps

### Step 1: Navigate and Select "Phrases"

1. Navigate to `https://localhost:7071/import-content`
2. Select "Phrases" content type
3. **Expected:** Default checkboxes for Phrases:
   - [ ] Transcript — UNCHECKED
   - [x] Phrases — CHECKED
   - [x] Words — CHECKED

### Step 2: Paste Content

1. Paste Korean phrase-list input
2. **Expected:** Text area populated

### Step 3: Override Checkboxes — Also Check Transcript

1. Additionally check the "Transcript" checkbox
2. **Expected:** All three checkboxes now checked:
   - [x] Transcript
   - [x] Phrases
   - [x] Words

### Step 4: Preview

1. Click "Preview"
2. **Expected:** Preview shows:
   - Full text transcript preview
   - Extracted phrases
   - Extracted words

### Step 5: Commit

1. Click "Commit"
2. Wait for completion
3. **Expected:** Results summary shows created items

### Step 6: Verify DB — LearningResource has Transcript

```sql
SELECT Id, Title, MediaType, Transcript
FROM LearningResources
WHERE UserProfileId = 'f452438c-b0ac-4770-afea-0803e2670df5'
ORDER BY CreatedAt DESC LIMIT 1;
```

**Expected:**
- `MediaType = "Transcript"` (Transcript checkbox was checked)
- `Transcript` field is NOT NULL and contains the full input text

### Step 7: Verify DB — Both Word AND Phrase rows

```sql
SELECT LexicalUnitType, COUNT(*) as cnt
FROM VocabularyWords
WHERE UserProfileId = 'f452438c-b0ac-4770-afea-0803e2670df5'
  AND CreatedAt > datetime('now', '-5 minutes')
GROUP BY LexicalUnitType;
```

**Expected:**
- At least 1 row with `LexicalUnitType = 1` (Word) — from Words checkbox
- At least 1 row with `LexicalUnitType = 2` (Phrase) — from Phrases checkbox
- Zero `LexicalUnitType = 0` (Unknown)

### Step 8: Verify ResourceVocabularyMapping

```sql
SELECT COUNT(*) as mapping_count
FROM ResourceVocabularyMapping rvm
JOIN LearningResources lr ON lr.Id = rvm.LearningResourceId
WHERE lr.UserProfileId = 'f452438c-b0ac-4770-afea-0803e2670df5'
  AND lr.CreatedAt > datetime('now', '-5 minutes');
```

**Expected:** Mapping count equals total VocabularyWord rows created

## Pass Criteria

- [ ] Full text stored on `LearningResource.Transcript` (Transcript checkbox honored)
- [ ] `MediaType = "Transcript"` set on resource
- [ ] Both Word AND Phrase VocabularyWord rows created
- [ ] Zero Unknown-type entries
- [ ] All rows linked via ResourceVocabularyMapping
- [ ] No errors in logs
