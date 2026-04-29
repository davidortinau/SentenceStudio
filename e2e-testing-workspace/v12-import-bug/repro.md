# v1.2 Import Bug — Reproduction Report

**Tester:** Jayne  
**Date:** 2026-04-26  
**Commit:** 3b6c01b (HEAD of feature/import-content)  
**Severity:** Critical — data loss (phrases silently dropped)

---

## What Captain Reported

Imported 3 pipe-delimited Korean|English phrase lines with:
- Content Type: **Phrases**
- Delimiter: **Pipe**
- Harvest: **Phrases + Words** both checked

Input:
```
저는 맥주를 많이 안 마시지만, 앤지하고 맥주집에 갔어요.|I don't drink beer much but went with Angie to a beer house (brewery).
앤지는 맥주를 많이 안 마시지만, 단 음료를 마셔요.|Angie doesn't drink much beer but she drinks sweet drinks.
그 웨이터는 동료가 한국어로 주문했는데 이해 못 했어요.|The waiter didn't understand (when) my colleague ordered in Korean.
```

**Expected:** 3 phrase entries (LexicalUnitType=2) + N word entries (LexicalUnitType=1).  
**Actual:** Only individual words landed. Zero phrases saved.

---

## Reproduction Steps

1. Started Aspire stack: `cd src/SentenceStudio.AppHost && aspire run`
2. Confirmed webapp at https://localhost:7071/ (HTTP 302 → login)
3. Playwright MCP was unresponsive (browser previously closed) — fell back to DB-level verification
4. Queried Postgres (container `db-84833ad0`, port 60488) for Captain's import data

---

## DB Evidence (Postgres — server DB)

### Captain's import batch (2026-04-27 02:54:20 UTC)

All entries from this timestamp are individual words, LexicalUnitType=1 (Word):

| TargetLanguageTerm | NativeLanguageTerm | LexicalUnitType |
|-|-|-|
| 맥주 | beer | 1 (Word) |
| 웨이터 | waiter | 1 (Word) |
| 동료 | colleague | 1 (Word) |
| 한국어 | Korean language | 1 (Word) |
| 주문하다 | to order | 1 (Word) |
| 이해하다 | to understand | 1 (Word) |
| 안 | not (negation marker) | 1 (Word) |
| 많이 | much | 1 (Word) |

**8 word entries. 0 phrase entries. 0 sentence entries.**

The 3 full phrases from Captain's input were never saved.

### Global LexicalUnitType distribution

| LexicalUnitType | Count |
|-|-|
| 1 (Word) | 221 |
| 2 (Phrase) | 6 |

Only 6 phrases in the entire DB — none from this import.

---

## Root Cause Analysis (Code-Level)

### Bug 1: Phrases branch bypasses delimiter-aware parsing

**File:** `src/SentenceStudio.Shared/Services/ContentImportService.cs`  
**Lines:** 176–196

When `ContentType == Phrases`, the code **always** calls `ParseFreeTextContentAsync()` (line 192), which sends the raw text to AI for free-text extraction. It **completely ignores** the user's delimiter selection.

Compare with the Vocabulary branch (line 215+), which correctly calls `DetectFormat()` and routes to `ParseDelimitedContent()` for pipe/CSV/TSV data.

**What happens:**
1. User pastes pipe-delimited Korean|English phrases
2. Phrases branch sends raw text (including pipe chars) to AI via `FreeTextToVocab.scriban-txt`
3. AI extracts individual vocabulary words, not phrases
4. Each word gets `LexicalUnitType.Word` from AI
5. `ResolveLexicalUnitType()` doesn't promote single Korean words (no spaces) to Phrase
6. All entries land as Word — the full phrases are lost

**What SHOULD happen:**
1. Detect delimiter (pipe), parse structured data via `ParseDelimitedContent()`
2. Each parsed row IS a phrase (the user told us ContentType=Phrases)
3. Set `LexicalUnitType = Phrase` on each row
4. If HarvestWords is also checked, ADDITIONALLY extract individual words from each phrase via AI

### Bug 2: ParseDelimitedContent hardcodes LexicalUnitType.Word

**File:** `src/SentenceStudio.Shared/Services/ContentImportService.cs`  
**Line:** 485

```csharp
LexicalUnitType = ResolveLexicalUnitType(LexicalUnitType.Word, targetTerm)
```

Even if the Phrases branch were to use `ParseDelimitedContent`, it hardcodes `LexicalUnitType.Word` as the AI classification input. The heuristic might catch multi-word Korean phrases (via space check), but it's still wrong — the user explicitly said these are phrases.

### Bug 3 (latent): No Sentence content type in UI

The UI only offers Vocabulary, Phrases, Transcript, and Auto. There's no "Sentences" option. Captain wants Sentence as a third content type (alongside Word and Phrase) but it doesn't exist yet.

---

## Enum Mapping Confirmed

**File:** `src/SentenceStudio.Shared/Models/LexicalUnitType.cs`

| Enum Value | Integer | Description |
|-|-|-|
| Unknown | 0 | Not determined |
| Word | 1 | Single word/morpheme |
| Phrase | 2 | Multi-word phrase/idiom |
| Sentence | 3 | Full sentence |

---

## SQLite (Mobile) DB — Missing Migration

The server Postgres DB has `LexicalUnitType` column (migration applied).  
The mobile SQLite DB at `~/Library/Application Support/sentencestudio/server/sentencestudio.db` does **NOT** — migration `20260423213242_AddLexicalUnitTypeAndConstituents` was never applied. This is a separate issue for mobile but worth noting.

---

## Verdict

**BUG CONFIRMED.** The Phrases import path is fundamentally broken. It ignores structured delimiters and sends everything to AI free-text extraction, which decomposes phrases into individual words. No phrase-typed entries are saved.
