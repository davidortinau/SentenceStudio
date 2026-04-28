# Scenario D: Auto-Detect — High Confidence (>=0.85)

**Status:** AUTHORED, NOT YET RUN  
**Priority:** P1 — v1.1 auto-detect feature  
**Feature branch:** `feature/import-content-mvp`  
**Decision refs:** `squad-captain-harvest-model.md` (Decision #3 confidence gate)

## Preconditions

- Aspire stack running
- Signed in as David (Korean)

## Input Text

Use fixture `fixtures/vocab-csv.csv` — a clearly CSV-shaped vocabulary blob:
```
책,book
읽다,to read
학교,school
선생님,teacher
공부하다,to study
```

This is unambiguously CSV vocabulary — classifier should return >=0.85 confidence.

## Steps

### Step 1: Navigate and Select "Auto-detect"

1. Navigate to `https://localhost:7071/import-content`
2. Select "Auto-detect" from content type selector
3. **Expected:** Auto-detect option selected

### Step 2: Paste CSV Content

1. Paste the CSV fixture content
2. **Expected:** Text area populated

### Step 3: Trigger Detection

1. Click "Next" / "Preview" or whatever triggers detection
2. Wait for auto-detection to complete

### Step 4: Verify Banner — High Confidence

1. **Expected:** Banner displays: "Auto-detected: Vocabulary (XX% confidence)" where XX >= 85
2. **Expected:** Banner includes a [Change] button allowing override
3. **Expected:** Auto-detect proceeds WITHOUT forcing user confirmation (high confidence = auto-route)

### Step 5: Verify Checkbox Defaults for Vocabulary

Per Captain's checkbox decision, "Vocabulary" defaults:
- [ ] Transcript — UNCHECKED
- [ ] Phrases — UNCHECKED
- [x] Words — CHECKED (only Words for vocabulary)

1. **Expected:** Correct defaults applied

### Step 6: Preview

1. **Expected:** Preview table shows the 5 vocabulary rows
2. **Expected:** All items classified as Word

### Step 7: Commit and Verify

1. Commit the import
2. **Expected:** Results show 5 created
3. Verify DB: all `LexicalUnitType=1` (Word)

```sql
SELECT TargetLanguageTerm, LexicalUnitType FROM VocabularyWords
WHERE UserProfileId = 'f452438c-b0ac-4770-afea-0803e2670df5'
  AND CreatedAt > datetime('now', '-5 minutes');
```

## Pass Criteria

- [ ] Banner shows "Auto-detected: Vocabulary" with confidence >= 85%
- [ ] [Change] button visible on banner
- [ ] Harvest checkboxes default to Words only
- [ ] Import proceeds without mandatory user confirmation
- [ ] All rows created as `LexicalUnitType=1`
- [ ] No errors in logs
