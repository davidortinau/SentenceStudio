# Scenario E: Auto-Detect — Medium Confidence (0.70-0.84)

**Status:** AUTHORED, NOT YET RUN  
**Priority:** P1 — v1.1 confidence gate enforcement  
**Feature branch:** `feature/import-content-mvp`  
**Decision refs:** `squad-captain-harvest-model.md` — Captain: "have the user confirm before the import potentially pollutes the database"

## Preconditions

- Aspire stack running
- Signed in as David (Korean)

## Input Text

Use fixture `fixtures/ambiguous-blob.txt` — content that mixes phrase-list patterns and prose-like sentences, designed to produce medium confidence from the classifier.

## Steps

### Step 1: Navigate and Select "Auto-detect"

1. Navigate to `https://localhost:7071/import-content`
2. Select "Auto-detect"

### Step 2: Paste Ambiguous Content

1. Paste content from `fixtures/ambiguous-blob.txt`
2. **Expected:** Text area populated

### Step 3: Trigger Detection

1. Click "Next" / trigger detection
2. Wait for auto-detection

### Step 4: Verify Banner + Mandatory Chooser

1. **Expected:** Banner appears showing detected type WITH confidence between 70-84%
2. **Expected:** A chooser/selector appears requiring the user to explicitly confirm or change the content type
3. **Expected:** User MUST explicitly confirm before the wizard advances
4. **CRITICAL:** The wizard must NOT auto-advance past this gate

### Step 5: Verify No Premature DB Writes

Before confirming, check that no database pollution occurred:

```sql
SELECT COUNT(*) AS new_words FROM VocabularyWords
WHERE UserProfileId = 'f452438c-b0ac-4770-afea-0803e2670df5'
  AND CreatedAt > datetime('now', '-2 minutes');

SELECT COUNT(*) AS new_resources FROM LearningResources
WHERE UserProfileId = 'f452438c-b0ac-4770-afea-0803e2670df5'
  AND CreatedAt > datetime('now', '-2 minutes');
```

**Expected:** Both counts = 0 (no DB writes until user confirms)

### Step 6: Confirm and Proceed

1. Select a content type from the chooser (e.g., confirm "Phrases")
2. Click confirm
3. **Expected:** Wizard advances to preview with the confirmed content type

### Step 7: Complete Import

1. Preview should show extracted items per the confirmed type
2. Commit the import
3. **Expected:** Import succeeds with correct LexicalUnitType based on confirmed type

## Pass Criteria

- [ ] Banner displays medium confidence (70-84%)
- [ ] Chooser/confirmation dialog appears — user cannot skip
- [ ] ZERO DB writes occur before user confirms (Captain's confidence gate)
- [ ] After confirmation, import proceeds correctly
- [ ] No errors in logs
