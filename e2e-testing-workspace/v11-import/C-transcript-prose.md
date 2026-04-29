# Scenario C: Transcript Import — Multi-Paragraph Prose

**Status:** AUTHORED, NOT YET RUN  
**Priority:** P0 — core v1.1 feature  
**Feature branch:** `feature/import-content-mvp`  
**Decision refs:** `squad-captain-harvest-model.md` (transcripts harvest words primarily, NOT phrases), `squad-captain-confirms-d2.md`

## Preconditions

- Aspire stack running
- Signed in as David (Korean)
- Note VocabularyWord baseline count

## Pre-test DB Baseline

```sql
SELECT COUNT(*) AS baseline_count FROM VocabularyWords
WHERE UserProfileId = 'f452438c-b0ac-4770-afea-0803e2670df5';

SELECT COUNT(*) AS baseline_resources FROM LearningResources
WHERE UserProfileId = 'f452438c-b0ac-4770-afea-0803e2670df5';
```

## Input Text

Use fixture `fixtures/transcript-korean.txt` — a 2-3 paragraph Korean prose passage with sentence-to-sentence continuity. The key classifier signal: continuity between sentences (unlike phrases which are standalone).

## Steps

### Step 1: Navigate and Select "Transcript"

1. Navigate to `https://localhost:7071/import-content`
2. Select "Transcript" content type

### Step 2: Verify Default Checkbox State

Per Captain's checkbox decision:
- When "Transcript" is selected:
  - [x] Transcript — CHECKED
  - [ ] Phrases — UNCHECKED
  - [x] Words — CHECKED

1. **Expected:** Correct checkbox defaults

### Step 3: Paste Transcript Content

1. Paste content from `fixtures/transcript-korean.txt`
2. **Expected:** Text area populated

### Step 4: Preview

1. Click "Preview"
2. **Expected:** Preview shows:
   - Full transcript text displayed (not truncated)
   - Extracted vocabulary items listed
   - Items should be predominantly Words, not Phrases (Captain's directive)

### Step 5: Commit

1. Click "Commit"
2. Wait for completion
3. **Expected:** Results summary with Created count > 0

### Step 6: Verify DB — LearningResource with Transcript

```sql
SELECT Id, Title, MediaType, Transcript, Language, UserProfileId
FROM LearningResources
WHERE UserProfileId = 'f452438c-b0ac-4770-afea-0803e2670df5'
ORDER BY CreatedAt DESC LIMIT 1;
```

**Expected:**
- `MediaType = "Transcript"`
- `Transcript` field is NOT NULL and contains the full input text
- `Language` = "Korean" (or equivalent)

### Step 7: Verify DB — VocabularyWord distribution

```sql
SELECT LexicalUnitType, COUNT(*) as cnt
FROM VocabularyWords
WHERE UserProfileId = 'f452438c-b0ac-4770-afea-0803e2670df5'
  AND CreatedAt > datetime('now', '-5 minutes')
GROUP BY LexicalUnitType;
```

**Expected:**
- Predominantly `LexicalUnitType = 1` (Word) entries
- Few or zero `LexicalUnitType = 2` (Phrase) entries
- Zero `LexicalUnitType = 0` (Unknown) entries
- **Document the exact distribution** (e.g., "8 words, 0 phrases") for the test report

### Step 8: Verify ResourceVocabularyMapping

```sql
SELECT COUNT(*) as mapping_count
FROM ResourceVocabularyMapping rvm
JOIN LearningResources lr ON lr.Id = rvm.LearningResourceId
WHERE lr.UserProfileId = 'f452438c-b0ac-4770-afea-0803e2670df5'
  AND lr.MediaType = 'Transcript'
  AND lr.CreatedAt > datetime('now', '-5 minutes');
```

**Expected:**
- Mapping count matches the number of VocabularyWord rows created
- All extracted words are linked to the transcript resource

### Step 9: Aspire Logs

- No errors
- Check that `ExtractVocabularyFromTranscript` (or equivalent extraction prompt) was invoked
- No timeout on AI call (transcripts may take longer due to text length)

## Pass Criteria

- [ ] LearningResource created with `MediaType="Transcript"` and `Transcript` field populated
- [ ] VocabularyWord rows created predominantly as `LexicalUnitType=1` (Word)
- [ ] Documented count distribution (words vs phrases)
- [ ] Checkbox defaults correct (Transcript + Words checked, Phrases unchecked)
- [ ] Full transcript text stored on LearningResource.Transcript
- [ ] No errors in logs
