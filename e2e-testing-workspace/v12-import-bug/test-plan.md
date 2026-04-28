# v1.2 Import Bug — Test Plan

**Author:** Jayne (Tester)  
**Date:** 2026-04-26  
**Branch:** feature/import-content  
**Status:** Ready for Round 2 execution (after Wash/River/Kaylee fixes land)

---

## Test Fixture (Captain's Input)

All tests below use these 3 pipe-delimited lines unless stated otherwise:

```
저는 맥주를 많이 안 마시지만, 앤지하고 맥주집에 갔어요.|I don't drink beer much but went with Angie to a beer house (brewery).
앤지는 맥주를 많이 안 마시지만, 단 음료를 마셔요.|Angie doesn't drink much beer but she drinks sweet drinks.
그 웨이터는 동료가 한국어로 주문했는데 이해 못 했어요.|The waiter didn't understand (when) my colleague ordered in Korean.
```

**Settings:** Content Type as specified, Delimiter = Pipe, no header row.

---

## 1. Bug Regression: Phrases + Words (Captain's exact scenario)

**Setup:** Content Type = Phrases, Delimiter = Pipe, Harvest = Phrases + Words  
**Input:** Test fixture (3 lines)

| Check | Expected |
|-|-|
| Preview shows phrase entries | 3 rows with full Korean sentences as TargetLanguageTerm |
| Preview shows word entries | N word-level rows (individual vocabulary extracted from phrases) |
| Preview LexicalUnitType column | Phrase rows show "Phrase", Word rows show "Word" |
| After commit: DB phrase count | 3 entries with LexicalUnitType=2 |
| After commit: DB word count | N entries with LexicalUnitType=1 (맥주, 웨이터, 동료, etc.) |
| After commit: resource mapping | All entries mapped to the target resource |
| Vocabulary list shows phrases | Filter by Phrase shows 3 entries |
| Vocabulary list shows words | Filter by Word shows N entries |

**Pass criteria:** All 3 full phrases saved with LexicalUnitType=2, plus individual words with LexicalUnitType=1. Zero data loss.

---

## 2. Phrases Harvest Matrix

### 2a. Phrases-only (no Words)

**Setup:** Content Type = Phrases, Delimiter = Pipe, Harvest = Phrases ONLY  
**Input:** Test fixture

| Check | Expected |
|-|-|
| Preview rows | 3 phrase-level rows only |
| DB after commit | 3 entries with LexicalUnitType=2 |
| No word entries | 0 new Word entries from this import |

### 2b. Words-only (no Phrases)

**Setup:** Content Type = Phrases, Delimiter = Pipe, Harvest = Words ONLY  
**Input:** Test fixture

| Check | Expected |
|-|-|
| Preview rows | N word-level rows only (individual vocab from phrases) |
| DB after commit | N entries with LexicalUnitType=1 |
| No phrase entries | 0 new Phrase entries from this import |

### 2c. Both (Phrases + Words) — same as Test 1

Already covered in Test 1.

### 2d. Neither checked (validation)

**Setup:** Content Type = Phrases, uncheck BOTH Phrases and Words  
**Expected:** Validation error blocks commit ("At least one harvest option must be checked")

---

## 3. Sentences Content Type

### 3a. Sentences-only

**Setup:** Content Type = Sentences (NEW), Delimiter = Pipe, Harvest = Sentences ONLY  
**Input:** Test fixture

| Check | Expected |
|-|-|
| Sentences option visible in dropdown | "Sentences" appears as a content type option |
| Preview rows | 3 rows with LexicalUnitType=Sentence (3) |
| DB after commit | 3 entries with LexicalUnitType=3 |

### 3b. Sentences + Phrases

**Setup:** Content Type = Sentences, Harvest = Sentences + Phrases  
**Input:** Test fixture

| Check | Expected |
|-|-|
| Preview rows | 3 sentence rows + N phrase rows (sub-phrases extracted) |
| DB LexicalUnitType=3 count | 3 |
| DB LexicalUnitType=2 count | N (phrase fragments from sentences) |

### 3c. Sentences + Words

**Setup:** Content Type = Sentences, Harvest = Sentences + Words  
**Input:** Test fixture

| Check | Expected |
|-|-|
| Preview rows | 3 sentence rows + N word rows |
| DB LexicalUnitType=3 count | 3 |
| DB LexicalUnitType=1 count | N |

### 3d. All three (Sentences + Phrases + Words)

**Setup:** Content Type = Sentences, Harvest = ALL three  
**Input:** Test fixture

| Check | Expected |
|-|-|
| Preview rows | 3 sentences + N phrases + M words |
| DB counts | 3 Sentence + N Phrase + M Word |

---

## 4. Heuristic Enforcement (ResolveLexicalUnitType)

Tests that the defensive heuristic corrects AI misclassification.

### 4a. AI returns Word for multi-word Korean phrase

**Scenario:** A structured phrase like "맥주를 마시다" comes back from AI with LexicalUnitType=Word.  
**Expected:** `ResolveLexicalUnitType` detects spaces, promotes to Phrase (LexicalUnitType=2).

### 4b. AI returns Word for terminal-punctuation sentence

**Scenario:** "저는 맥주를 마셔요." comes back as Word.  
**Expected:** Heuristic detects terminal punctuation (. ? ! 요 etc.), promotes to Sentence (LexicalUnitType=3).  
**Note:** This requires Wash to implement the terminal-punctuation check in `ResolveLexicalUnitType`. Current code only checks for spaces.

### 4c. Single token stays Word

**Scenario:** "맥주" comes back as Word.  
**Expected:** No promotion — stays Word (LexicalUnitType=1). No spaces, no terminal punctuation.

### 4d. AI returns Phrase — no override

**Scenario:** AI correctly returns Phrase for "맥주를 마시다".  
**Expected:** `ResolveLexicalUnitType` passes through Phrase (line 899: `return aiClassification`).

### 4e. AI returns Sentence — no override

**Scenario:** AI correctly returns Sentence for a full sentence.  
**Expected:** `ResolveLexicalUnitType` passes through Sentence.

---

## 5. Vocabulary List Filter by Type (Kaylee's work)

### 5a. Filter by Word

**Setup:** Navigate to Vocabulary list, select filter "Word"  
**Expected:** Only LexicalUnitType=1 entries shown. Count matches DB query: `SELECT COUNT(*) FROM "VocabularyWord" WHERE "LexicalUnitType"=1`

### 5b. Filter by Phrase

**Setup:** Navigate to Vocabulary list, select filter "Phrase"  
**Expected:** Only LexicalUnitType=2 entries shown. Count matches DB.

### 5c. Filter by Sentence

**Setup:** Navigate to Vocabulary list, select filter "Sentence"  
**Expected:** Only LexicalUnitType=3 entries shown. Count matches DB.

### 5d. No filter (all)

**Setup:** Navigate to Vocabulary list, no type filter  
**Expected:** All entries shown regardless of LexicalUnitType.

### 5e. Filter + search combo

**Setup:** Filter by Phrase, then search "맥주"  
**Expected:** Only phrase entries containing "맥주" shown.

---

## 6. "Add" Button Rename

### 6a. Vocabulary list "Add" button

**Setup:** Navigate to Vocabulary list  
**Expected:** Button reads "Add" (not "Add Word"). LexicalUnitType-aware — if adding from Phrases tab, default type should be Phrase.

### 6b. Add button in filtered context

**Setup:** Filter by Phrase, click Add  
**Expected:** New entry form defaults LexicalUnitType to Phrase.

---

## 7. Auto-Detect Classifier

### 7a. High confidence — Captain's input

**Setup:** Content Type = Auto, paste test fixture  
**Expected:** Classifier returns Sentence with >= 0.85 confidence. Each line is a complete sentence with terminal verb form.

### 7b. Auto-detect pipe-delimited CSV

**Setup:** Content Type = Auto, paste simple word pairs: `사과|apple\n바나나|banana`  
**Expected:** Classifier returns Vocabulary (not Phrases/Sentences). These are single words.

### 7c. Auto-detect phrases vs sentences

**Setup:** Content Type = Auto, paste: `맥주를 마시다|to drink beer\n한국어로 주문하다|to order in Korean`  
**Expected:** Classifier returns Phrases. These are verb phrases, not complete sentences (no terminal conjugation, no sentence-ending particles).

---

## Execution Notes

- **Round 1 (this document):** Test plan authored. Bug reproduction confirmed via DB queries.
- **Round 2 (after fixes land):** Execute all tests via Playwright + DB verification. Screenshot every step. Report pass/fail with evidence.
- **Three-level verification required for each test:** UI state (Playwright snapshot) + DB query + Aspire structured logs (no errors).
- **Clean DB state:** Before executing, note current vocab counts per LexicalUnitType as baseline. After each test, verify delta matches expected.
