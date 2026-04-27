# Edge Cases: Import Content v1.1

**Status:** AUTHORED, NOT YET RUN  
**Priority:** P2 — edge case coverage  
**Feature branch:** `feature/import-content-mvp`

---

## Edge 1: Empty Input

### Steps
1. Navigate to import page, select any content type
2. Leave the text area empty (or whitespace only)
3. Click "Next" / "Preview"

### Expected
- Graceful inline error: "Please enter some content to import" (or similar)
- Wizard does NOT advance
- No spinner, no loading, no crash
- No DB writes

### DB Check
```sql
SELECT COUNT(*) FROM VocabularyWords
WHERE CreatedAt > datetime('now', '-2 minutes');
```
Expected: 0

---

## Edge 2: Massive Input >30KB Transcript

### Steps
1. Navigate to import page, select "Transcript"
2. Paste a text blob exceeding 30KB (generate: ~30,000+ characters of Korean prose)
3. Click "Preview"

### Expected
- Per Wash's chunking decision: error message shown (e.g., "Content too large — maximum 30KB")
- Wizard does NOT advance past the input step
- No partial processing
- No DB writes
- **Note:** If Wash implements chunking instead of rejection, update this to verify chunked processing

### DB Check
```sql
SELECT COUNT(*) FROM LearningResources
WHERE CreatedAt > datetime('now', '-2 minutes');
```
Expected: 0

---

## Edge 3: Korean-Only Input (No English)

### Steps
1. Navigate to import page, select "Phrases"
2. Paste Korean-only text (no English translations):
   ```
   오늘 날씨가 정말 좋아요.
   내일은 비가 올 거예요.
   주말에 뭐 할 거예요?
   ```
3. Click "Preview"

### Expected
- AI extraction still runs (generates translations from Korean)
- Preview shows extracted items WITH AI-generated English translations
- Import succeeds — translations are generated, not required in input
- `NativeLanguageTerm` fields populated by AI

---

## Edge 4: Korean + English Mixed Input

### Steps
1. Navigate to import page, select "Phrases"
2. Paste mixed content (Captain's Margo example has this pattern):
   ```
   마고는 눈하고 귀가 안 좋아요. 잘 못 보고, 잘 못 들어요.
   Margo's eyes and ears are not good. (She) can't see well and can't hear well.
   ```
3. Click "Preview"

### Expected
- AI correctly identifies Korean as target language, English as native
- Extracted items have Korean in `TargetLanguageTerm` and English in `NativeLanguageTerm`
- No confusion or mixed-language entries

---

## Edge 5: Zero-Vocab Extraction Result

### Steps
1. Navigate to import page, select "Vocabulary"
2. Paste content that produces zero extractable vocabulary (e.g., numbers only: "1234567890")
3. Click "Preview"

### Expected (per Wash's decision file — verify which option was implemented):
- **Option A:** Warning shown: "No vocabulary could be extracted from this content"
  - Resource NOT created (no empty resource pollution)
  - User returned to input step to try different content
- **Option B:** Resource created but with warning about empty extraction
  - LearningResource exists, but zero VocabularyWord rows
  - Warning surfaced to user

### DB Check (whichever option):
```sql
-- If Option A:
SELECT COUNT(*) FROM LearningResources WHERE CreatedAt > datetime('now', '-2 minutes');
-- Expected: 0

-- If Option B:
SELECT lr.Id, lr.MediaType, COUNT(rvm.VocabularyWordId) as vocab_count
FROM LearningResources lr
LEFT JOIN ResourceVocabularyMapping rvm ON rvm.LearningResourceId = lr.Id
WHERE lr.CreatedAt > datetime('now', '-2 minutes')
GROUP BY lr.Id;
-- Expected: 1 resource, 0 vocab
```

**FLAG:** Which option did Wash implement? Document in test report.

---

## Edge 6: Duplicate Import (Same Content Twice)

### Steps
1. Complete a full Vocabulary CSV import (Scenario A)
2. Immediately repeat with the exact same CSV content
3. Click Preview, then Commit

### Expected
- Dedup logic kicks in
- Results show Skipped count = 5 (all terms already exist)
- Created count = 0 (or items are updated, not duplicated)
- No duplicate VocabularyWord rows in DB

### DB Check
```sql
SELECT TargetLanguageTerm, COUNT(*) as cnt
FROM VocabularyWords
WHERE UserProfileId = 'f452438c-b0ac-4770-afea-0803e2670df5'
  AND TargetLanguageTerm IN ('책', '읽다', '학교', '선생님', '공부하다')
GROUP BY TargetLanguageTerm
HAVING cnt > 1;
```
Expected: 0 rows (no duplicates)

---

## Edge 7: Special Characters in Input

### Steps
1. Navigate to import page, select "Vocabulary"
2. Paste CSV with special characters:
   ```
   "안녕하세요!","Hello!"
   "감사합니다.","Thank you."
   "뭐?!","What?!"
   ```
3. Preview and commit

### Expected
- Special characters (quotes, punctuation, exclamation) handled correctly
- Preview shows clean text without escaped characters
- DB rows have clean `TargetLanguageTerm` values without surrounding quotes

---

## Pass Criteria Summary

- [ ] Edge 1: Empty input blocked with error
- [ ] Edge 2: >30KB rejected (or chunked) gracefully
- [ ] Edge 3: Korean-only input produces AI-generated translations
- [ ] Edge 4: Mixed input correctly separates target/native languages
- [ ] Edge 5: Zero extraction handled (document which option)
- [ ] Edge 6: Duplicate import correctly deduped
- [ ] Edge 7: Special characters handled cleanly
