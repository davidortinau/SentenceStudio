# Scenario I: Confidence Gate — DB Pollution Check

**Status:** AUTHORED, NOT YET RUN  
**Priority:** P0 — data integrity  
**Feature branch:** `feature/import-content-mvp`  
**Decision refs:** `squad-captain-harvest-model.md` — "have the user confirm before the import potentially pollutes the database"

## Preconditions

- Aspire stack running
- Signed in as David (Korean)

## Pre-test DB Snapshot

```sql
SELECT COUNT(*) AS vocab_before FROM VocabularyWords
WHERE UserProfileId = 'f452438c-b0ac-4770-afea-0803e2670df5';

SELECT COUNT(*) AS resources_before FROM LearningResources
WHERE UserProfileId = 'f452438c-b0ac-4770-afea-0803e2670df5';

SELECT COUNT(*) AS mappings_before FROM ResourceVocabularyMapping rvm
JOIN LearningResources lr ON lr.Id = rvm.LearningResourceId
WHERE lr.UserProfileId = 'f452438c-b0ac-4770-afea-0803e2670df5';
```

Record these three counts.

## Steps

### Step 1: Navigate and Select "Auto-detect"

1. Navigate to `https://localhost:7071/import-content`
2. Select "Auto-detect"

### Step 2: Paste Medium-Confidence Blob

1. Paste content from `fixtures/ambiguous-blob.txt`
2. **Expected:** Text area populated

### Step 3: Trigger Detection

1. Click "Next" / trigger detection
2. **Expected:** Medium confidence result — chooser/confirmation dialog appears

### Step 4: Wait at the Chooser Step

1. Do NOT confirm — just wait
2. Let the page sit for 5-10 seconds
3. **Expected:** No background processing, no spinner, no async DB writes

### Step 5: CANCEL the Wizard

1. Click "Cancel" / "Back" / navigate away from the import page
2. Navigate to `https://localhost:7071/` (dashboard)
3. **Expected:** Wizard closes/resets cleanly

### Step 6: Verify ZERO New DB Rows

```sql
SELECT COUNT(*) AS vocab_after FROM VocabularyWords
WHERE UserProfileId = 'f452438c-b0ac-4770-afea-0803e2670df5';

SELECT COUNT(*) AS resources_after FROM LearningResources
WHERE UserProfileId = 'f452438c-b0ac-4770-afea-0803e2670df5';

SELECT COUNT(*) AS mappings_after FROM ResourceVocabularyMapping rvm
JOIN LearningResources lr ON lr.Id = rvm.LearningResourceId
WHERE lr.UserProfileId = 'f452438c-b0ac-4770-afea-0803e2670df5';
```

**Expected:**
- `vocab_after == vocab_before` (zero new VocabularyWord rows)
- `resources_after == resources_before` (zero new LearningResource rows)
- `mappings_after == mappings_before` (zero new mapping rows)

### Step 7: Double-Check with Timestamp

```sql
SELECT * FROM VocabularyWords
WHERE UserProfileId = 'f452438c-b0ac-4770-afea-0803e2670df5'
  AND CreatedAt > datetime('now', '-5 minutes');

SELECT * FROM LearningResources
WHERE UserProfileId = 'f452438c-b0ac-4770-afea-0803e2670df5'
  AND CreatedAt > datetime('now', '-5 minutes');
```

**Expected:** Both queries return 0 rows

## Pass Criteria

- [ ] ZERO new VocabularyWord rows created during cancelled session
- [ ] ZERO new LearningResource rows created during cancelled session
- [ ] ZERO new ResourceVocabularyMapping rows created
- [ ] Wizard closes/resets cleanly on cancel
- [ ] No orphaned/partial records in DB
- [ ] No errors in logs
