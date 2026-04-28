# Scenario B: Phrases Import — Captain's Korean Example

**Status:** AUTHORED, NOT YET RUN  
**Priority:** P0 — core v1.1 feature  
**Feature branch:** `feature/import-content-mvp`  
**Decision refs:** `squad-captain-harvest-model.md`, `squad-captain-checkboxes.md`

## Preconditions

- Aspire stack running
- Signed in as David (Korean)
- Note VocabularyWord baseline count

## Pre-test DB Baseline

```sql
SELECT COUNT(*) AS baseline_count FROM VocabularyWords
WHERE UserProfileId = 'f452438c-b0ac-4770-afea-0803e2670df5';
```

## Input Text (Captain's Margo Example)

```
마고는 눈하고 귀가 안 좋아요. 잘 못 보고, 잘 못 들어요.
Margo's eyes and ears are not good. (She) can't see well and can't hear well.
```

## Steps

### Step 1: Navigate to Import Content

1. Playwright: navigate to `https://localhost:7071/import-content`
2. **Expected:** Import wizard loads

### Step 2: Select Content Type "Phrases"

1. Select "Phrases" from the content type selector
2. **Expected:** Phrases option selected

### Step 3: Verify Default Checkbox State

Per Captain's checkbox decision (`squad-captain-checkboxes.md`):
- When "Phrases" is selected/auto-detected, defaults are:
  - [ ] Transcript — UNCHECKED
  - [x] Phrases — CHECKED
  - [x] Words — CHECKED

1. **Expected:** Harvest checkboxes visible with correct defaults

### Step 4: Paste Input Text

1. Paste the Captain's Korean example text into the input area
2. **Expected:** Text area shows the Korean + English text

### Step 5: Preview

1. Click "Preview" / "Next"
2. **Expected:** Preview shows extracted items:
   - At least one entry with `LexicalUnitType=2` (Phrase) — e.g., realistic Korean phrases like "눈하고 귀가 안 좋다", "잘 못 보다", "잘 못 듣다"
   - At least one entry with `LexicalUnitType=1` (Word) — e.g., 눈, 귀, 보다, 듣다
3. **Expected:** Both phrase and word sections visible in preview

### Step 6: Commit Import

1. Click "Commit"
2. Wait for completion
3. **Expected:** Results summary with non-zero Created count for both types

### Step 7: Verify DB — Phrase entries

```sql
SELECT Id, TargetLanguageTerm, NativeLanguageTerm, LexicalUnitType
FROM VocabularyWords
WHERE UserProfileId = 'f452438c-b0ac-4770-afea-0803e2670df5'
  AND LexicalUnitType = 2
  AND CreatedAt > datetime('now', '-5 minutes')
ORDER BY CreatedAt DESC;
```

**Expected:**
- At least 1 row with `LexicalUnitType = 2` (Phrase)
- Phrase entries should be realistic Korean phrases (multi-word units):
  - Examples: "눈하고 귀가 안 좋다", "잘 못 보다", "잘 못 듣다"
  - NOT full sentences verbatim (those would be `LexicalUnitType=3`)
- Each phrase should have a corresponding English translation in `NativeLanguageTerm`

### Step 8: Verify DB — Word entries

```sql
SELECT Id, TargetLanguageTerm, NativeLanguageTerm, LexicalUnitType
FROM VocabularyWords
WHERE UserProfileId = 'f452438c-b0ac-4770-afea-0803e2670df5'
  AND LexicalUnitType = 1
  AND CreatedAt > datetime('now', '-5 minutes')
ORDER BY CreatedAt DESC;
```

**Expected:**
- At least 1 row with `LexicalUnitType = 1` (Word)
- Word entries should be individual vocabulary: 눈, 귀, 보다, 듣다, 좋다, etc.
- Each word should have an English translation

### Step 9: Verify no Unknown types created

```sql
SELECT COUNT(*) AS unknown_count FROM VocabularyWords
WHERE UserProfileId = 'f452438c-b0ac-4770-afea-0803e2670df5'
  AND LexicalUnitType = 0
  AND CreatedAt > datetime('now', '-5 minutes');
```

**Expected:** `unknown_count = 0` — all new entries must have explicit classification

### Step 10: Verify LearningResource

```sql
SELECT Id, Title, MediaType, Transcript
FROM LearningResources
WHERE UserProfileId = 'f452438c-b0ac-4770-afea-0803e2670df5'
ORDER BY CreatedAt DESC LIMIT 1;
```

**Expected:**
- New resource created
- `Transcript` is NULL (Transcript checkbox was unchecked)

### Step 11: Check Aspire logs

- No errors during AI extraction
- No 503/timeout on the extraction call
- Log should show the extraction request completing

## Pass Criteria

- [ ] At least 1 Phrase (`LexicalUnitType=2`) row created with realistic Korean phrases
- [ ] At least 1 Word (`LexicalUnitType=1`) row created with individual Korean words
- [ ] Zero `LexicalUnitType=0` (Unknown) entries from this import
- [ ] Checkbox defaults correct (Transcript unchecked, Phrases + Words checked)
- [ ] No errors in Aspire logs
