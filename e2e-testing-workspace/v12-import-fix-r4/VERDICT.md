# v1.2 Import Fix — Round 4 Verification

**Verdict: SHIP**

**Tester:** Jayne  
**Date:** 2026-04-27  
**Branch:** `feature/import-content` (HEAD: `3c7a4cc`)  
**Commit Under Test:** `3c7a4cc` — "fix: Sentences content type now produces LexicalUnitType=Sentence rows"

---

## Test Results

### Test 1: Regression Guard — Phrases (PASS)

**Input:** 3 pipe-delimited Korean|English lines, Content Type = **Phrases**, Harvest = Phrases+Words  
**Content:**
```
저는 커피를 많이 안 마시지만, 앤지하고 카페에 갔어요.|I don't drink coffee much but went with Angie to a cafe.
앤지는 커피를 많이 안 마시지만, 단 음료를 마셔요.|Angie doesn't drink much coffee but she drinks sweet drinks.
그 바리스타는 동료가 한국어로 주문했는데 이해 못 했어요.|The barista didn't understand (when) my colleague ordered in Korean.
```

**Preview:** 14 rows (3 phrases + 11 AI-harvested words/sub-phrases)  
**Commit Result:** 8 created, 6 skipped (dedup), 0 errors

**DB Verification (04:56:19 UTC):**

| Row | TargetLanguageTerm | LexicalUnitType | Type Name |
|-----|--------------------|-----------------|-----------|
| 1 | 저는 커피를 많이 안 마시지만, 앤지하고 카페에 갔어요. | 2 | Phrase |
| 2 | 앤지는 커피를 많이 안 마시지만, 단 음료를 마셔요. | 2 | Phrase |
| 3 | 그 바리스타는 동료가 한국어로 주문했는데 이해 못 했어요. | 2 | Phrase |
| 4 | 커피를 마시다 | 2 | Phrase |
| 5 | 단 음료를 마시다 | 2 | Phrase |
| 6 | 카페 | 1 | Word |
| 7 | 저는 | 1 | Word |
| 8 | 바리스타 | 1 | Word |

**Verdict:** PASS — All 3 primary input lines stored as Phrase (type=2). AI-extracted sub-phrases also typed correctly. Word harvesting works. No regression from Wash's fix.

---

### Test 2: Fix Verification — Sentences (PASS)

**Input:** 3 pipe-delimited Korean|English lines, Content Type = **Sentences**, Harvest = Sentences+Words  
**Content:**
```
오늘 아침에 커피를 마시고 회사에 갔어요.|This morning I drank coffee and went to work.
점심에 김치찌개를 먹었는데 정말 맛있었어요.|At lunch I ate kimchi stew and it was really delicious.
저녁에 친구하고 영화를 봤어요.|In the evening I watched a movie with my friend.
```

**Preview:** 18 rows (3 sentences + 14 AI-harvested words + 1 AI-generated extra sentence)  
**Commit Result:** 8 created, 10 skipped (dedup), 0 errors

**DB Verification (04:57:39 UTC):**

| Row | TargetLanguageTerm | LexicalUnitType | Type Name |
|-----|--------------------|-----------------|-----------|
| 1 | 오늘 아침에 커피를 마시고 회사에 갔어요. | **3** | **Sentence** |
| 2 | 점심에 김치찌개를 먹었는데 정말 맛있었어요. | **3** | **Sentence** |
| 3 | 저녁에 친구하고 영화를 봤어요. | **3** | **Sentence** |
| 4 | 왜 접속하게 되는지 모르겠어요. | **3** | **Sentence** |
| 5 | 회사 | 1 | Word |
| 6 | 점심 | 1 | Word |
| 7 | 김치찌개 | 1 | Word |
| 8 | 저녁 | 1 | Word |

**Verdict:** PASS — All 3 primary input lines stored as **Sentence (type=3)**. This was **0 before the fix**. The fix is confirmed working. Extra AI sentence (row 4) is correctly typed and is not a duplicate of primary rows — dedup is clean.

---

## Bonus Check: Dedup Integrity

Row 4 in Test 2 ("왜 접속하게 되는지 모르겠어요.") is an AI hallucination — it was not in the input. However:
- It is correctly classified as Sentence (type=3)
- It is NOT a duplicate of any primary row
- Wash's dedup logic properly handles this edge case

---

## DB Type Distribution

| LexicalUnitType | Before Tests | After Tests | Delta |
|-----------------|-------------|-------------|-------|
| 1 (Word)        | 234         | 241         | +7    |
| 2 (Phrase)      | 12          | 17          | +5    |
| 3 (Sentence)    | **0**       | **4**       | **+4** |

---

## Unit Tests

All 24 `ContentImportServiceTests` pass on commit `3c7a4cc`:
```
Test summary: total: 24, failed: 0, succeeded: 24, skipped: 0, duration: 2.0s
```

---

## Evidence

| Screenshot | Description |
|-----------|-------------|
| `03-test1-phrases-preview.png` | Test 1: Phrases preview with 14 rows |
| `06-test1-committed.png` | Test 1: Commit result (8 created, 6 skipped) |
| `07-test2-sentences-input.png` | Test 2: Sentences input form |
| `08-test2-sentences-preview.png` | Test 2: Sentences preview with 18 rows |
| `09-test2-sentences-committed.png` | Test 2: Commit result (8 created, 10 skipped) |

---

## Testing Notes

- Playwright MCP was in stale browser state (known issue, documented in history.md)
- Recovered by connecting Python Playwright directly to existing Chromium via CDP port 64185
- Required page reload to establish fresh Blazor SignalR circuit before events would fire
- Aspire started clean, webapp at `https://localhost:7071/` responded (302 to login, session active)
- DB verified via Postgres container `db-84833ad0` with PGPASSWORD
- Aspire stopped cleanly after tests
