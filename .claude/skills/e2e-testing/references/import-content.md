# Content Import E2E Test Script

**Feature**: Content Import (Wave 2)  
**Test Environment**: Mac Catalyst, Debug build  
**Prerequisites**: 
- Active skill profile with Korean→English
- At least one existing vocabulary resource (for "existing resource" tests)
- Clear database or known vocabulary baseline

---

## Test Scenario 1: Import CSV to New Resource (Happy Path)

**Goal**: Verify basic CSV import with format detection, dedup, and new resource creation.

### Steps:

1. **Navigate to Resources → Import Content**
   - ✅ Import page loads
   - ✅ Empty text input shown
   - ✅ "New Resource" mode selected by default

2. **Paste CSV content**:
   ```
   Learning,Native
   안녕하세요,Hello
   감사합니다,Thank you
   ```
   - ✅ Format detected as "CSV"
   - ✅ Preview shows 2 rows
   - ✅ Both rows marked "Ok" status

3. **Configure target**:
   - Title: "Test Vocabulary 001"
   - Description: "E2E test import"
   - Dedup mode: "Skip duplicates"

4. **Tap "Preview"** (if exists) or **"Import"**
   - ✅ Preview modal shows 2 rows
   - ✅ Row 1: 안녕하세요 → Hello
   - ✅ Row 2: 감사합니다 → Thank you
   - ✅ No warnings

5. **Tap "Confirm Import"**
   - ✅ Success toast: "Created 2 new words"
   - ✅ Navigates back to resources list or resource detail
   - ✅ New resource "Test Vocabulary 001" appears in list

6. **Open "Test Vocabulary 001"**
   - ✅ 2 vocabulary items present
   - ✅ Data matches imported content

**Expected Result**: CSV imported successfully, 2 new words created, resource created.

---

## Test Scenario 2: Import TSV to Existing Resource (Dedup Skip)

**Goal**: Verify tab-delimited import, existing resource append, duplicate detection.

### Steps:

1. **Navigate to Resources → Import Content**

2. **Paste TSV content**:
   ```
   Learning	Native
   안녕하세요	Hello
   새로운	New
   ```
   - ✅ Format detected as "TSV" or "Tab-delimited"
   - ✅ Preview shows 2 rows
   - ✅ Row 1 (안녕하세요) marked "Duplicate" (already exists from Scenario 1)
   - ✅ Row 2 (새로운) marked "Ok"

3. **Configure target**:
   - Mode: "Existing Resource"
   - Select: "Test Vocabulary 001" (from Scenario 1)
   - Dedup mode: "Skip duplicates"

4. **Tap "Import"**
   - ✅ Success toast: "Created 1 new word, Skipped 1 duplicate"
   - ✅ Returns to resource detail

5. **Verify resource contents**:
   - ✅ Total items: 3 (original 2 + 1 new)
   - ✅ New word "새로운 → New" present
   - ✅ Duplicate "안녕하세요" NOT duplicated

**Expected Result**: TSV imported, 1 new word added, 1 duplicate skipped.

---

## Test Scenario 3: Import with Dedup Mode "Update"

**Goal**: Verify Update mode overwrites existing vocabulary word.

### Steps:

1. **Navigate to Resources → Import Content**

2. **Paste CSV content with updated translation**:
   ```
   Learning,Native
   안녕하세요,Hello there!
   ```
   - ✅ Preview shows 1 row
   - ✅ Row marked "Duplicate" or "Warning"

3. **Configure target**:
   - Mode: "Existing Resource"
   - Select: "Test Vocabulary 001"
   - Dedup mode: "Update existing words"
   - ✅ **Warning displayed**: "⚠️ Updating will affect ALL resources using this word"

4. **Tap "Import"**
   - ✅ Success toast: "Updated 1 word"
   - ✅ No new words created

5. **Verify all resources using "안녕하세요"**:
   - ✅ Native term updated to "Hello there!" EVERYWHERE
   - ✅ Other resources referencing this word also show new translation

**Expected Result**: Update mode modifies shared word, affects all resources.

---

## Test Scenario 4: Import with Dedup Mode "Import All"

**Goal**: Verify ImportAll mode creates duplicate word with new GUID.

### Steps:

1. **Navigate to Resources → Import Content**

2. **Paste CSV content** (same learning term as existing):
   ```
   Learning,Native
   안녕하세요,Greetings
   ```
   - ✅ Preview shows 1 row
   - ✅ Row marked "Duplicate" or "Ok" (depending on dedup mode visibility)

3. **Configure target**:
   - Mode: "New Resource"
   - Title: "Duplicate Vocabulary Test"
   - Dedup mode: "Import all (allow duplicates)"

4. **Tap "Import"**
   - ✅ Success toast: "Created 1 new word"
   - ✅ New resource created

5. **Verify database**:
   - ✅ Two separate VocabularyWord records with LearningTerm="안녕하세요"
   - ✅ One has NativeTerm="Hello there!" (from Scenario 3)
   - ✅ One has NativeTerm="Greetings" (new)
   - ✅ Different GUIDs

**Expected Result**: ImportAll creates new word even if learning term exists.

---

## Test Scenario 5: Import Pipe-Delimited Content

**Goal**: Verify pipe (|) delimiter detection.

### Steps:

1. **Navigate to Resources → Import Content**

2. **Paste pipe-delimited content**:
   ```
   Learning|Native
   책|Book
   컴퓨터|Computer
   ```
   - ✅ Format detected as "Pipe-delimited"
   - ✅ Preview shows 2 rows
   - ✅ Both rows "Ok"

3. **Configure and import to new resource**:
   - Title: "Pipe Test"

4. **Verify**:
   - ✅ 2 words imported correctly

**Expected Result**: Pipe delimiter detected and parsed.

---

## Test Scenario 6: CSV with Quoted Fields (Commas in Content)

**Goal**: Verify CSV parser handles quoted fields correctly.

### Steps:

1. **Navigate to Resources → Import Content**

2. **Paste CSV with quotes**:
   ```
   Learning,Native
   "안녕, 여러분","Hello, everyone"
   "감사합니다!","Thank you!"
   ```
   - ✅ Format detected as "CSV"
   - ✅ Preview shows 2 rows
   - ✅ Row 1: `안녕, 여러분` → `Hello, everyone` (commas preserved)
   - ✅ Row 2: `감사합니다!` → `Thank you!`

3. **Import and verify**

**Expected Result**: Quoted fields with commas parsed correctly.

---

## Test Scenario 7: Whitespace Trimming

**Goal**: Verify whitespace trimmed before dedup comparison.

### Steps:

1. **Navigate to Resources → Import Content**

2. **Paste CSV with extra whitespace**:
   ```
   Learning,Native
     책  ,  Book  
   책,Book
   ```
   - ✅ Preview shows 2 rows
   - ✅ Row 2 marked "Duplicate" (after trim, both are "책" / "Book")

3. **Import with Skip mode**
   - ✅ Only 1 word created
   - ✅ Trimmed correctly: "책" (no leading/trailing spaces)

**Expected Result**: Whitespace trimmed, dedup case-sensitive but space-insensitive.

---

## Test Scenario 8: Empty Row Validation

**Goal**: Verify empty rows marked as errors.

### Steps:

1. **Navigate to Resources → Import Content**

2. **Paste CSV with empty row**:
   ```
   Learning,Native
   안녕하세요,Hello
   ,
   감사합니다,Thank you
   ```
   - ✅ Preview shows 3 rows
   - ✅ Row 2 marked "Error": "Both terms required"
   - ✅ Row 2 NOT selected for import (checkbox unchecked)

3. **Import**
   - ✅ Success: "Created 2 words, 1 error"
   - ✅ Empty row skipped

**Expected Result**: Empty rows detected, marked as errors, skipped during commit.

---

## Test Scenario 9: Single-Column AI Translation

**Goal**: Verify AI fills missing native term when only one column provided.

⚠️ **Prerequisite**: Requires Wave 2 AI wiring complete + valid Azure OpenAI API key.

### Steps:

1. **Navigate to Resources → Import Content**

2. **Paste single-column content**:
   ```
   Learning
   안녕하세요
   감사합니다
   ```
   - ✅ Format detected (CSV or single-column)
   - ⏳ AI translation triggered automatically
   - ✅ Preview shows 2 rows with AI-filled native terms:
     - Row 1: 안녕하세요 → Hello *(AI translated badge shown)*
     - Row 2: 감사합니다 → Thank you *(AI translated badge shown)*

3. **Verify AI badge**:
   - ✅ Rows show visual indicator (icon/badge) that translation is AI-generated

4. **Import**
   - ✅ Words imported
   - ✅ `VocabularyWord.IsAiTranslated = true` in database

**Expected Result**: AI fills missing native terms, marks as AI-translated.

---

## Test Scenario 10: Free-Text Extraction

**Goal**: Verify AI extracts vocabulary pairs from unstructured text.

⚠️ **Prerequisite**: Requires Wave 2 AI wiring complete + valid Azure OpenAI API key.

### Steps:

1. **Navigate to Resources → Import Content**

2. **Paste free-form text**:
   ```
   In Korean, hello is 안녕하세요. Thank you is 감사합니다. Book is 책.
   ```
   - ✅ Format detected as "Free-text" or "Unstructured"
   - ⏳ AI extraction triggered
   - ✅ Preview shows extracted pairs:
     - 안녕하세요 → hello
     - 감사합니다 → thank you
     - 책 → book

3. **Verify confidence**:
   - ✅ High-confidence rows: "Ok"
   - ✅ Medium-confidence rows: "Warning" (yellow, can still import)
   - ✅ Low-confidence rows: "Error" (red, cannot import)

4. **Import**
   - ✅ Words imported (excluding error rows)

**Expected Result**: AI extracts vocabulary from free-text, confidence mapped to row status.

---

## Test Scenario 11: Free-Text Size Limit (50 KB)

**Goal**: Verify free-text capped at 50 KB.

### Steps:

1. **Navigate to Resources → Import Content**

2. **Paste large unstructured text** (>50 KB):
   - Generate by repeating lorem ipsum or random text until >51,200 bytes

3. **Verify error**:
   - ✅ Error message: "Free-text content limited to 50 KB"
   - ✅ No AI call made
   - ✅ No preview shown

**Expected Result**: Large free-text rejected before AI call.

---

## Test Scenario 12: JSON Array-of-Objects Format

**Goal**: Verify JSON object array import.

### Steps:

1. **Navigate to Resources → Import Content**

2. **Paste JSON**:
   ```json
   [
     {"learning": "안녕하세요", "native": "Hello"},
     {"learning": "감사합니다", "native": "Thank you"}
   ]
   ```
   - ✅ Format detected as "JSON"
   - ✅ Preview shows 2 rows

3. **Import**
   - ✅ 2 words created

**Expected Result**: JSON array-of-objects parsed correctly.

---

## Test Scenario 13: JSON Array-of-Arrays Format

**Goal**: Verify JSON 2D array import.

### Steps:

1. **Navigate to Resources → Import Content**

2. **Paste JSON**:
   ```json
   [
     ["안녕하세요", "Hello"],
     ["감사합니다", "Thank you"]
   ]
   ```
   - ✅ Format detected as "JSON"
   - ✅ Preview shows 2 rows

3. **Import**
   - ✅ 2 words created

**Expected Result**: JSON array-of-arrays parsed correctly.

---

## Test Scenario 14: Cancel/Back Navigation

**Goal**: Verify no data loss on cancel.

### Steps:

1. **Navigate to Resources → Import Content**

2. **Paste content and configure**

3. **Tap "Back" or "Cancel" BEFORE committing**
   - ✅ Returns to resources list
   - ✅ No database changes
   - ✅ No new resources created

4. **Re-enter Import page**
   - ✅ Input cleared (no state leak)

**Expected Result**: Cancel does not persist anything.

---

## Test Scenario 15: Transaction Rollback on Error

**Goal**: Verify atomic commit - if one row fails, entire import aborted.

⚠️ **Note**: This is an internal safety mechanism. Hard to test via UI without triggering DB constraint violation.

**Manual verification**:
- Review `CommitImportAsync` code for transaction scope
- Verify all DB writes wrapped in `try/catch` with rollback

**Code inspection checklist**:
- ✅ Uses `using var transaction = db.Database.BeginTransaction()`
- ✅ Calls `transaction.Commit()` only after all rows processed
- ✅ Catches exceptions and logs errors
- ✅ Transaction auto-rolls back on exception (via `using` disposal)

---

## Post-Test Cleanup

After all scenarios:
1. Delete all "Test Vocabulary" resources created during E2E run
2. Restore database to baseline (or note new vocabulary count for next run)
3. Verify no orphaned VocabularyWords (all should have at least one ResourcePhrase)

---

## Coverage Summary

| Feature | Scenario |  Status |
|---------|----------|---------|
| CSV detection | Scenario 1 | ✅ |
| TSV detection | Scenario 2 | ✅ |
| Pipe detection | Scenario 5 | ✅ |
| JSON object array | Scenario 12 | ✅ |
| JSON 2D array | Scenario 13 | ✅ |
| Quoted CSV fields | Scenario 6 | ✅ |
| Whitespace trim | Scenario 7 | ✅ |
| Empty row validation | Scenario 8 | ✅ |
| Dedup Skip | Scenario 1,2 | ✅ |
| Dedup Update | Scenario 3 | ✅ |
| Dedup ImportAll | Scenario 4 | ✅ |
| New resource creation | Scenario 1,4,5 | ✅ |
| Existing resource append | Scenario 2,3 | ✅ |
| Single-column AI | Scenario 9 | ⏳ (AI required) |
| Free-text extraction | Scenario 10 | ⏳ (AI required) |
| Free-text 50KB cap | Scenario 11 | ⏳ (AI required) |
| Cancel/back | Scenario 14 | ✅ |

**Total**: 15 scenarios, 13 non-AI + 2 AI-dependent
