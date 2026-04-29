# Scenario J: LexicalUnitType Backfill Migration Verification

**Status:** AUTHORED, NOT YET RUN  
**Priority:** P0 — migration correctness  
**Feature branch:** `feature/import-content-mvp`  
**Decision refs:** `squad-captain-confirms-d1.md` — heuristic backfill: space = Phrase, no space = Word

## Preconditions

- v1.1 migration `SetDefaultLexicalUnitType` has been generated and applied
- App has been built and run at least once (so migration runs)
- Database contains pre-existing VocabularyWord rows from before v1.1

## Steps

### Step 1: Run Migration Validation Script

```bash
bash scripts/validate-mobile-migrations.sh
```

**Expected:** Green output:
```
Migration validated on net10.0-maccatalyst -- no errors found
```

If the script fails, STOP — report the failure and do not proceed.

### Step 2: Verify Zero Unknown Types Remain

```sql
SELECT COUNT(*) AS unknown_count FROM VocabularyWords
WHERE LexicalUnitType = 0;
```

**Expected:** `unknown_count = 0` — the backfill migration should have classified every existing row

### Step 3: Verify Multi-Word Terms Are Phrases

```sql
SELECT Id, TargetLanguageTerm, LexicalUnitType
FROM VocabularyWords
WHERE INSTR(TRIM(TargetLanguageTerm), ' ') > 0
LIMIT 10;
```

**Expected:**
- All returned rows have `LexicalUnitType = 2` (Phrase)
- Terms should be multi-word Korean phrases (e.g., "잘 못 들어요", "어떻게 지내세요")

### Step 4: Verify Single-Token Terms Are Words

```sql
SELECT Id, TargetLanguageTerm, LexicalUnitType
FROM VocabularyWords
WHERE INSTR(TRIM(TargetLanguageTerm), ' ') = 0
  AND LENGTH(TRIM(TargetLanguageTerm)) > 0
LIMIT 10;
```

**Expected:**
- All returned rows have `LexicalUnitType = 1` (Word)
- Terms should be single Korean words (e.g., "책", "읽다", "학교")

### Step 5: Check Edge Cases — Leading/Trailing Whitespace

```sql
SELECT Id, TargetLanguageTerm, LexicalUnitType, LENGTH(TargetLanguageTerm) as raw_len, LENGTH(TRIM(TargetLanguageTerm)) as trimmed_len
FROM VocabularyWords
WHERE LENGTH(TargetLanguageTerm) != LENGTH(TRIM(TargetLanguageTerm))
LIMIT 10;
```

**Expected:**
- If any rows have leading/trailing whitespace, verify they were classified correctly based on TRIMMED content
- Captain's decision: migration should TRIM before checking for space

### Step 6: Distribution Report

```sql
SELECT 
  LexicalUnitType,
  CASE LexicalUnitType 
    WHEN 0 THEN 'Unknown'
    WHEN 1 THEN 'Word'
    WHEN 2 THEN 'Phrase'
    WHEN 3 THEN 'Sentence'
  END as type_name,
  COUNT(*) as cnt
FROM VocabularyWords
GROUP BY LexicalUnitType
ORDER BY LexicalUnitType;
```

**Document the distribution** in the test report. Example: "Word: 245, Phrase: 38, Unknown: 0"

### Step 7: Spot-Check Korean Phrases

```sql
SELECT TargetLanguageTerm, NativeLanguageTerm, LexicalUnitType
FROM VocabularyWords
WHERE LexicalUnitType = 2
ORDER BY RANDOM()
LIMIT 5;
```

**Expected:**
- Visually confirm these are genuine multi-word phrases, not single words with accidental spaces
- Each should have a matching English translation

## Pass Criteria

- [ ] `scripts/validate-mobile-migrations.sh` passes (green)
- [ ] Zero rows with `LexicalUnitType = 0` (Unknown) after backfill
- [ ] Multi-word terms correctly classified as `LexicalUnitType = 2` (Phrase)
- [ ] Single-token terms correctly classified as `LexicalUnitType = 1` (Word)
- [ ] Leading/trailing whitespace handled (TRIM before space check)
- [ ] Distribution documented
