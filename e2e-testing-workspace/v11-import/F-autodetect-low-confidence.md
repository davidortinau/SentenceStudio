# Scenario F: Auto-Detect — Low Confidence (<0.70)

**Status:** AUTHORED, NOT YET RUN  
**Priority:** P1 — v1.1 low-confidence fallback  
**Feature branch:** `feature/import-content-mvp`  
**Decision refs:** `squad-captain-harvest-model.md` (three-tier model)

## Preconditions

- Aspire stack running
- Signed in as David (Korean)

## Input Text

Use fixture `fixtures/low-confidence-noise.txt` — extremely short or noisy input that the classifier cannot confidently categorize.

## Steps

### Step 1: Navigate and Select "Auto-detect"

1. Navigate to `https://localhost:7071/import-content`
2. Select "Auto-detect"

### Step 2: Paste Noisy Input

1. Paste content from `fixtures/low-confidence-noise.txt`
2. **Expected:** Text area populated

### Step 3: Trigger Detection

1. Click "Next" / trigger detection
2. Wait for auto-detection

### Step 4: Verify "Couldn't Auto-Detect" Message

1. **Expected:** Friendly message displayed, e.g., "Couldn't auto-detect the content type"
2. **Expected:** NO auto-routing happens — the system does NOT guess
3. **Expected:** Manual type picker/selector is shown, allowing user to choose Vocabulary/Phrases/Transcript

### Step 5: Verify No Auto-Routing

1. **Expected:** The wizard does NOT advance past the type selection step
2. **Expected:** No banner showing a detected type with low confidence
3. **Expected:** User must manually select a content type to proceed

### Step 6: Manual Selection and Proceed

1. Manually select "Vocabulary" from the picker
2. **Expected:** Wizard advances with Vocabulary selected
3. Verify checkbox defaults match Vocabulary: [x] Words only

### Step 7: Verify No Premature DB Writes

```sql
SELECT COUNT(*) AS new_words FROM VocabularyWords
WHERE UserProfileId = 'f452438c-b0ac-4770-afea-0803e2670df5'
  AND CreatedAt > datetime('now', '-2 minutes');
```

**Expected:** 0 until user commits

## Pass Criteria

- [ ] Friendly "Couldn't auto-detect" message shown
- [ ] Manual type picker displayed
- [ ] NO auto-routing occurs
- [ ] User can select type manually and proceed
- [ ] No errors in logs
