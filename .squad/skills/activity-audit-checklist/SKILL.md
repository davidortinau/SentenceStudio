---
confidence: high
last_validated: 2026-05-10
status: production-validated
pattern: Code-driven activity mode audit
---

# Activity Mode Audit Checklist

## When to Use

Any activity with multiple sub-modes (ListenAndType, TapTheCounter, Disambiguate, etc.) and multiple contexts (Counting, Time, Age, etc.) where combinations may be partially implemented.

**Trigger:** User reports "X mode doesn't work" or "clicking Y mode shows broken UI."

## The Problem

Activities with M modes × N contexts = M×N combinations. Developers implement a subset, but the picker exposes ALL combos. Result: users click broken combinations and hit stubs, crashes, or blank UI.

**NumberDrill case study:** 6 contexts × 5 modes = 30 combos, but only 12 worked. 14 were broken backend logic (would crash or render blank), 1 had a UI leak, 3 were semantically invalid (N/A).

## Audit Approach

**DO NOT** run all combos blindly first. Code review tells you 90% of what's broken.

### Step 1: Code Review — Backend Generator (30 min)

Find the item generator (e.g., `KoreanNumberItemGenerator.cs`, `VocabQuizItemGenerator.cs`).

Look for:
1. **Router method** — `GenerateItem()` switch on context/mode. Does it route ALL combos or only some?
2. **Context-specific methods** — Do they handle ALL modes or only ListenAndType/ReadAndProduce?
3. **Mode-specific methods** — Are any modes hardcoded to ONE context?
4. **Switch statements with missing cases** — `ArgumentException` throws signal unimplemented combos
5. **Stub markers** — `TODO`, `NotImplementedException`, `throw new`, placeholder strings

**NumberDrill evidence:**
- Line 34-42: Disambiguate = cross-context (works everywhere), ListenAndPlace = Time-only (line 40 explicit check)
- Line 73-160: TapTheCounter ONLY in `GenerateCountingItem()`, completely missing from Time/Age/Money/Date/Ordinal
- Lines 163-566: Context methods (Time/Age/Money/Date/Ordinal) only handle ListenAndType/ReadAndProduce

**Verdict from code alone:** TapTheCounter works for Counting, broken for 5 other contexts. ListenAndPlace works for Time, broken for 5 other contexts.

### Step 2: Code Review — Frontend UI (15 min)

Find the activity page (e.g., `NumberDrill.razor`, `VocabQuiz.razor`).

Look for:
1. **Mode picker** — does it filter combos by context, or show all modes always?
2. **Conditional rendering** — `@if (selectedMode == "X")` blocks. Do all modes have a render branch?
3. **Placeholder leaks** — debug strings like "(TTS placeholder: ...)" that shipped
4. **Submit handlers** — `TapCounter()`, `SubmitAnswerAsync()`. Do all modes have handlers?

**NumberDrill evidence:**
- Line 36-66: Picker shows ALL modes for ALL contexts (no filtering)
- Lines 137-327: UI render branches exist for all 5 modes (ListenAndType, ReadAndProduce, TapTheCounter, ListenAndPlace, Disambiguate)
- Line 149: UI leak `<em>(TTS placeholder: "@currentItem.AudioCue")</em>` — debug string that shipped
- Line 347-366: Text input + submit only shown for ListenAndType/ReadAndProduce (correct)

**Verdict from code alone:** UI can RENDER all combos, but doesn't FILTER broken backend combos. Picker needs `IsValidCombo()` check.

### Step 3: Build Audit Matrix (15 min)

Create a table:

| Context | Sub-Mode | Picker | Start | Success Turn | Failure Turn | Audio | Stub Markers | Verdict | Owner for Fix |
|---------|----------|--------|-------|--------------|--------------|-------|--------------|---------|---------------|

**Fill columns based on code review:**
- **Picker:** Does picker show this combo? (always YES unless UI filters)
- **Start:** Can backend `GenerateItem()` produce an item? (NO if switch has no case)
- **Success Turn:** Does generator populate `CanonicalAnswer` + grading fields? (code inspection)
- **Failure Turn:** Are `AcceptableAlternates` / `ErrorClassHints` populated? (code inspection)
- **Audio:** For Listen* modes, does generator set `AudioCue`? (code inspection)
- **Stub Markers:** List any TODO/placeholder strings/missing logic

**Verdict rules:**
- **SHIP:** Code complete, no stubs, both success/failure turns work
- **FIX:** Partial implementation (e.g., UI leak but backend works). Note effort (S/M/L) + owner.
- **HIDE:** Backend broken or missing. Picker should filter this combo until implemented.
- **N/A:** Combo doesn't make semantic sense (e.g., Date + TapTheCounter — dates don't use counters)

### Step 4: Targeted Runtime Verification (30 min, optional)

Run the app and test ONLY the combos flagged as SHIP or FIX (not broken ones — waste of time).

For each SHIP combo:
1. Select context + mode from picker
2. Start session
3. Submit correct answer → green feedback, progress increments
4. Submit wrong answer → red feedback, hint shown, progress NOT incremented
5. (If audio mode) Tap Play → audio plays, no console errors

**Evidence format:** Screenshots named `{context}-{mode}-{state}.png` (e.g., `counting-tapcounter-feedback-correct.png`).

### Step 5: Write Recommendation (10 min)

Summarize in audit matrix:
- **Total combos:** M × N
- **SHIP count, FIX count, HIDE count, N/A count**
- **Picker visibility recommendation:** UI filter (Option A, fast) vs backend schema (Option B, cleaner)
- **Fix ownership:** Which agent owns each FIX/HIDE item, effort estimate (S/M/L), priority (P0/P1/P2)

**Captain decision gate:** "Do we hide broken combos and ship what works, OR do we implement all combos before ship?"
Usually the answer is: hide + ship fast, implement later.

## Implementation Pattern (Picker Filter)

**Option A: UI-level filter (fastest, ship in 1 PR)**

In activity page picker loop, add conditional:

```razor
@foreach (var mode in availableSubModes.Where(m => IsValidCombo(selectedContext, m.Code)))
{
    <button ... >@mode.DisplayName</button>
}

@code {
    private bool IsValidCombo(string? context, string modeCode)
    {
        // TapTheCounter only works for Counting context
        if (modeCode == "TapTheCounter" && context != "Counting")
            return false;
        
        // ListenAndPlace only works for Time context
        if (modeCode == "ListenAndPlace" && context != "Time")
            return false;
        
        return true;
    }
}
```

**Option B: Backend schema (cleaner, Phase 2)**

1. Add `SupportedContexts` column to `SubMode` table (JSON array or comma-delimited)
2. Populate in seeder: `TapTheCounter.SupportedContexts = ["Counting"]`
3. Filter in `LoadSetupDataAsync()`: `availableSubModes = modes.Where(m => m.SupportedContexts.Contains(selectedContext))`

## Key Principles

1. **Code review first, runtime second.** Don't waste time clicking through 30 combos when the code tells you 14 are broken.
2. **Picker visibility = quality gate.** If a combo is in the picker, it MUST work end-to-end. Never ship broken combos.
3. **HIDE > CRASH.** Better to hide a combo and ship fewer features than expose it and erode user trust.
4. **Evidence format:** Audit matrix markdown table + screenshots for SHIP combos only.
5. **Fix triage:** S (< 1 hour) ships in same PR, M (1-4 hours) ships in follow-up, L (> 4 hours) is Phase N.

## Common Patterns

### Pattern 1: Mode only works for one context

**Example:** ListenAndPlace hardcoded Time-only (NumberDrill line 40-42)

**Fix:** Either (a) implement for all contexts (L effort) OR (b) hide for non-Time contexts (S effort)

**Verdict:** HIDE (fast ship) unless mode is core feature (then implement)

### Pattern 2: Context methods don't handle all modes

**Example:** GenerateTimeItem() handles ListenAndType/ReadAndProduce, ignores TapTheCounter

**Fix:** Add TapTheCounter branch to every context method (M effort per context × N contexts = L total)

**Verdict:** HIDE TapTheCounter for non-Counting contexts OR implement (Phase 2)

### Pattern 3: UI leak (placeholder text shipped)

**Example:** `<em>(TTS placeholder: "@currentItem.AudioCue")</em>` on line 149

**Fix:** Delete the debug div (S effort, 1-line change)

**Verdict:** FIX (ship in same PR, P0 priority)

## NumberDrill Case Study Summary

**Audit duration:** 90 minutes (60 min code review, 0 min runtime, 30 min matrix + write-up)

**Result:** 12 SHIP, 1 FIX (S), 14 HIDE, 3 N/A

**Action:** Kaylee fixes picker filter (S, <30 min) + UI leak (S, <5 min), ship immediately. Wash implements missing combos in Phase 3 (M-L, 2-8 hours total).

**Captain directive honored:** "Test them all and either implement them or hide them." ✅

## Validation Script Pattern (Phase 1 Ship)

After audit matrix identifies SHIP combos, draft a comprehensive E2E validation script covering:

**Three-platform pass order:**
1. **Mac Catalyst (full pass):** All SHIP combos × 2 turns (success + failure) + DB verification (SM-2 DueDate sanity, progress records)
2. **Webapp (quick pass + audio focus):** All SHIP combos × 1 turn (success) + explicit audio verification for Listen* modes
3. **iOS Sim (smoke test):** 1 combo per context (N combos total) + negative picker tests

**Negative picker tests:**
- For each broken combo flagged as HIDE, verify it does NOT appear in picker for that context
- Example: Time context → TapTheCounter button must NOT render in sub-mode picker

**Regression checks:**
- NO placeholder text (e.g., `(TTS placeholder: ...)`) in any Listen* combo
- Audio actually plays (manual "does sound come out?" check, not just "no console error")
- UI components render correctly (chips for TapTheCounter, cards for ListenAndPlace, etc.)
- Picker filters work (broken combos hidden)

**DB verification queries:**
```sql
-- Verify attempts recorded
SELECT ContextCode, SubModeCode, Bucket, IsCorrect, AnsweredAt
FROM NumberAttempt
WHERE UserId = '<testUserId>' AND SubModeCode = '<mode>'
ORDER BY AnsweredAt DESC LIMIT 5;

-- Verify SM-2 scheduling
SELECT ContextCode, SubModeCode, Bucket, MasteryLevel, DueDate
FROM NumberMasteryProgress
WHERE UserId = '<testUserId>' AND SubModeCode = '<mode>'
LIMIT 5;

-- Pass criteria: DueDate >= tomorrow for correct, ~1 day for incorrect
```

**Acceptance verdict template:**
- Pass/Fail per combo (checkbox table)
- Pass/Fail per negative picker test
- Sign-off: "All SHIP + negative tests pass → APPROVE for DX24. Any failure → BLOCK."

**Script format:** Markdown checklist suitable for both manual execution (tester follows steps) AND scripted execution (Playwright/maui-devflow-debug automation).

**Reference:** `.squad/decisions/inbox/jayne-numberdrill-validation-script.md`

## Related Skills

- **e2e-testing**: Runtime verification workflow (Aspire + Playwright)
- **activity-audio-playback**: TTS cache-first pattern for Listen* modes
- **project-conventions**: Activity page structure (picker + session + summary states)
