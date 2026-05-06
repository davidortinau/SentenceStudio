# Grader Override Pattern

**When to use:** Any automated-grading activity where edge cases are inevitable (Cloze, Translation, Writing, NumberDrill, etc.)

**Problem:** Perfect graders are impossible. Korean number counters have sound changes, spacing variants, IME quirks, copy-paste artifacts. You can add rules forever and still miss edge cases. Each fix adds complexity and risk of regressions.

**Solution:** Human-in-the-loop + telemetry. Let the user override when the grader is wrong, and emit structured telemetry to mine for patterns. **Learn** the right rules from real user data instead of guessing upfront.

## Pattern

### 1. UI: Override Button (mirrored from VocabQuiz)

```razor
@* Show override button only when grader says incorrect *@
@if (!lastGrade.IsCorrect)
{
    <div class="text-center mt-3">
        <button class="btn btn-outline-success btn-sm" @onclick="OverrideAsCorrect">
            <i class="bi bi-check2 me-1"></i>I was correct
        </button>
    </div>
}
```

**Placement:** After error feedback, before Next button (same as VocabQuiz)  
**Label:** "I was correct" (plain text, no localization needed for Phase 1)  
**Icon:** `bi-check2` (Bootstrap checkmark icon)

### 2. Override Effect: Flip + Count + Telemetry + Auto-advance

```csharp
private async Task OverrideAsCorrect()
{
    if (lastGrade == null || lastAttemptResult == null || currentItem == null)
        return;

    // 1. Flip result (immutable update)
    lastGrade = lastGrade with 
    { 
        IsCorrect = true,
        Verdict = "정확해요! (Overridden)"  // Or localized equivalent
    };
    
    // 2. Count toward streak (affects session summary)
    correctCount++;
    
    // 3. Emit telemetry event for grader miss analysis
    Logger.LogInformation(
        "⚠️ [ActivityName] grader override: user={UserId} [activity-specific-context] " +
        "canonicalAnswer={Canonical} userAnswer={UserAnswer} [more context fields]",
        AppState?.CurrentUserProfile?.Id ?? "unknown",
        // ... activity-specific fields that help reverse-engineer missing rules
    );
    
    StateHasChanged();
    
    // 4. Auto-advance after brief pause (1.5s standard)
    await Task.Delay(1500);
    await NextItemAsync();
}
```

### 3. Telemetry Shape (activity-specific)

**Minimum fields for ALL activities:**
- `user` — who overrode
- `canonicalAnswer` — what the system expected
- `userAnswer` — what they actually typed

**Activity-specific fields** (capture whatever helps reverse-engineer the rule):

**NumberDrill:**
- `system` (Sino/Native) — pedagogical context
- `counter` (잔/개/명 etc.) — counter-specific edge cases
- `digitValue` — target number (allows rule reconstruction)
- `errorClass` — what grader thought was wrong (may be misdiagnosed)

**Cloze/Translation:**
- `promptText` — source sentence
- `targetLanguage` — ko/en/etc.
- `errorClass` — grammar/spelling/etc.

**Writing:**
- `rubric` — grading criteria used
- `aiScore` — what AI grader assigned (0.0-1.0)
- `humanOverrideScore` — what user thinks it should be

**Pattern:** Capture enough to **reproduce** the grading decision offline. If you can't reconstruct the rule from the telemetry, you're missing fields.

### 4. Narrow Normalizer Rules (BEFORE permissive grading)

**Two-phase normalization:**

1. **Narrow pre-processing** (typing artifacts, not pedagogy):
   - Strip trailing punctuation (`.`, `,`, `?`, `!`, `。`, `？`, `！`) — copy-paste or autocorrect artifacts
   - Normalize fullwidth digits/chars (`０-９` → `0-9`) — IME quirks
   - Collapse whitespace — `\s+` → single space
   
2. **Permissive grading** (linguistic equivalence):
   - System-aware forms (NumberDrill: bare digits + system-correct Korean)
   - Whitespace variants (e.g., `5시` vs `5 시`)
   - Sound-change alternates (e.g., `한 개` vs `하나`)

**NOTHING fuzzier than narrow rules** — no Levenshtein, no typo tolerance, no autocorrect. If it's not a mechanical typing artifact, the user should type it correctly or use the override.

### 5. Override Does NOT Change Past Data

The override is **UI-only**. The grader still persists the attempt as incorrect to the database. The user simply overrides the UI feedback and streak counter. We do NOT retroactively change stored attempts or mastery progress — the override is purely for:
1. Unblocking the user (lets them proceed without frustration)
2. Capturing telemetry (for future rule mining)

**Future workflow:** When telemetry identifies a confident new rule (e.g., 10+ overrides for the same pattern), ADD that rule to the grader. The override doesn't replace the grader — it TEACHES the grader.

## When NOT to Use This Pattern

- **Objective-correctness activities** (e.g., tap-the-counter multiple choice) — no ambiguity, no need for override
- **Freeform creative writing** — no single correct answer, grading is holistic (use AI rubric + human review instead)
- **Audio playback bugs** — if audio doesn't play, that's a tooling issue, not a grading issue (fix the audio system, don't override)

## Reusable Across Activities

This pattern **generalizes** to any activity where:
1. Automated grading is imperfect (edge cases exist)
2. User knows they're correct (they typed a valid alternate form)
3. Telemetry can identify patterns (enough context to reconstruct the rule)

**Candidates:** Cloze (grammar edge cases), Translation (phrasing variants), Writing (AI misgrading), Conversation (speech recognition errors).

## Testing Checklist

When implementing override for a new activity:

- [ ] Override button appears only when `!lastGrade.IsCorrect`
- [ ] Override flips feedback to green "Correct (Overridden)" equivalent
- [ ] Override increments `correctCount` (session summary shows higher score)
- [ ] Override emits telemetry event with ALL required fields populated
- [ ] Override auto-advances after 1.5s (no manual Next button tap)
- [ ] Override does NOT change stored `Attempt` record (DB remains unchanged)
- [ ] Narrow normalizer rules applied BEFORE grading (test trailing punctuation, fullwidth chars)
- [ ] Telemetry event includes enough context to reverse-engineer missing rules offline

## Example Commit Structure

```
feat(NumberDrill): Add grader override + narrow normalizer

- Override button (mirrored from VocabQuiz): "I was correct"
- Override flips result, updates streak, emits telemetry
- Telemetry shape: canonicalAnswer, userAnswer, system, counter, digitValue, errorClass
- Narrow normalizer: strip trailing punctuation, normalize fullwidth digits
- Auto-advance after 1.5s (matches VocabQuiz flow)
- Decision doc: .squad/decisions/inbox/[agent]-grader-override.md
```

