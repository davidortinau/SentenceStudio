# Vocabulary Quiz — Learning Journey Specification

> **Source of truth** for every quiz interaction. If the code doesn't match this spec, the code is wrong.
>
> **Last updated:** 2025-07-25
> **Revised:** 2025-07-24 — Captain's corrections on mode selection, demotion, and rotation timing
> **Revised:** 2025-07-25 — Captain's R2 feedback: terminology, QuizRecognitionStreak analysis, deferred persistence, demotion correction, sentence difficulty weight
> **Revised:** 2025-07-25 — Captain's R3 approvals: tiered rotation model, DifficultyWeight 2.5f, temporal weighting for mastery scoring
> **Revised:** 2025-07-25 — R4 structural fixes: unified mode-selection algorithm, session counter model, cross-activity behavior, cross-reference cleanup (Zoe, from Jayne's skeptic review)
> **Revised:** 2025-07-25 — R5 Captain's design decisions (Q1–Q6): DifficultyWeight accelerates mastery, tier 1 rotation requires text+recognition, no repeat within round, recovery-aware mastery formula, DueOnly once at session start, IsKnown re-qualification 14-day interval
> **Author:** Zoe (Lead) — derived from codebase audit + Captain's stated expectations

---

## 0. Terminology

These terms are used precisely throughout this spec. If usage is ambiguous, this section is the authority.

| Term | Definition |
|---|---|
| **Session** | The entire unbroken duration of the user in a quiz activity — from page load to page exit. A session contains one or more rounds. All session-local state (streaks, counts, flags) resets when the user leaves and re-enters the quiz. |
| **Round** | A cycle of up to 10 words (or fewer if fewer remain) within a session, ending in a summary view. Each round draws from the batch pool. After the summary, the user can start another round or exit. |
| **Turn** | A single word presentation + user answer within a round. One turn = one word shown, one response given (MC selection, text entry, or sentence shortcut). The turn ends when the user advances to the next word. |
| **Batch Pool** | The full set of up to 20 words (`BatchSize`) loaded at session start. Words are drawn from this pool each round. Words rotate out of the pool when mastered. When the pool is empty, the session ends. |
| **Active Words** | The subset of the batch pool selected for the current round (up to `ActiveWordCount` = 10). These are shuffled into `roundWordOrder`. |
| **Mode** | The input mode for a given turn: **MultipleChoice** (MC) — user selects from options; **Text** — user types the answer. Mode is determined per-turn based on the word's global progress. |
| **Rotation Out** | Removing a word from the batch pool because the user has demonstrated sufficient mastery of it this session. Rotation happens immediately mid-round (not deferred to round boundaries). |
| **Global Progress** | The persisted `VocabularyProgress` record for a word — lifetime stats including `CurrentStreak`, `MasteryScore`, `ProductionInStreak`, `TotalAttempts`. Survives across sessions. |
| **Session-Local State** | In-memory fields on `VocabQuizItem` that reset each session: `SessionCorrectCount`, `SessionMCCorrect`, `SessionTextCorrect`, `WasCorrectThisSession`, `PendingRecognitionCheck`. Counters are cumulative (never reset on wrong answers). `PendingRecognitionCheck` controls mode selection (overrides global progress); all other session fields are used only for rotation-out and UI display. See section 1.2.3 for the full model definition. |

---

## 1. Word Lifecycle in a Quiz Session

### 1.1 Word Selection

| Parameter | Value | Source |
|---|---|---|
| `BatchSize` | 20 | Constant in VocabQuiz.razor |
| `ActiveWordCount` | 10 | Constant in VocabQuiz.razor |
| `TurnsPerRound` | 10 | Constant in VocabQuiz.razor |

**Selection pipeline:**

1. Load all `VocabularyWord` records from the specified `resourceIds` (or all user vocabulary if none specified).
2. Deduplicate by `Word.Id`; discard any with blank `NativeLanguageTerm` or `TargetLanguageTerm`.
3. Fetch `VocabularyProgress` for every word via `GetProgressForWordsAsync`. Words without a DB record get a stub with `MasteryScore = 0`, `TotalAttempts = 0`.
4. **Filter out Known words:** Remove any word where `Progress.IsKnown == true`.
5. **Filter out Familiar-in-grace-period:** Remove any word where `Progress.IsInGracePeriod == true` (user-declared familiar, within 14-day grace window).
6. **DueOnly filter** (when `DueOnly=true` query param is set):
   - **Include** words with `TotalAttempts == 0` (truly unseen).
   - **Include** words where `NextReviewDate <= DateTime.Now` (due for SRS review).
   - **Exclude** everything else (not yet due).
7. Sort by `MasteryScore` ascending (weakest first), with random tiebreaker.
8. Take top `BatchSize` (20) into `batchPool`.
9. Mark each item with `IsDueOnlySession = DueOnly`.

**CURRENT vs EXPECTED:**

| Aspect | CURRENT | EXPECTED | Status |
|---|---|---|---|
| IsKnown filter | Uses computed `IsKnown` (not stale `IsCompleted` bool) | Same | OK |
| DueOnly filter | Applied once at load time | **EXPECTED (R5): Applied ONCE at session start only.** The DueOnly date filter applies during initial word selection (this section). It is NOT re-applied between rounds. Words that become not-due during a session (because their NextReviewDate was pushed into the future by correct answers) remain in the batch pool for the duration of the session. Rotation out of the session is controlled exclusively by the mastery/tiered rotation logic (section 1.3), never by scheduling. | **RESOLVED (R5)** |
| Grace period filter | Filters out `IsInGracePeriod` | Same | OK |
| Batch pool ordering | Weakest mastery first | Same | OK |

### 1.2 Recognition to Production Progression

Each word starts where its **lifetime learning status** indicates it should be in the journey. Mode selection is based on GLOBAL progress (`VocabularyProgress`), not session-local state.

**Unified mode-selection algorithm** (evaluated per-turn in `LoadCurrentItem`):

> **This is the single authoritative mode-selection algorithm. Implementers: use this — do not cross-reference other sections.**

```
// Priority 1: Gentle demotion override (see 1.2.1 for flag lifecycle)
IF PendingRecognitionCheck == true
    → mode = "MultipleChoice"    // Overrides everything — must prove recognition first

// Priority 2: Lifetime progress indicates production readiness
ELSE IF currentItem.Progress.CurrentStreak >= 3      // LIFETIME streak, NOT session-local
        OR currentItem.Progress.MasteryScore >= 0.50
    → mode = "Text"

// Priority 3: Default — still learning
ELSE
    → mode = "MultipleChoice"
```

**How `PendingRecognitionCheck` interacts with lifetime mode** (merged from 1.2.1):
- A wrong Text answer sets `PendingRecognitionCheck = true` on the session-local item.
- Even if `CurrentStreak >= 3` or `MasteryScore >= 0.50` (which would normally select Text mode), `PendingRecognitionCheck` **overrides** and forces MC.
- The flag clears ONLY on a **correct** MC answer. Wrong MC keeps the flag set.
- **Principle: Incorrect responses never result in promotion.**
- This matters most for established words where temporal weighting preserves the streak above 3 — without the override, those words would skip the recognition check entirely.

**EXPECTED behavior (Captain's correction):**
- A word with zero global progress starts in MultipleChoice.
- After 3 consecutive correct recognition answers **across any number of sessions (lifetime)**, it starts in Text mode in ALL future appearances.
- A word with >= 50% global MasteryScore starts directly in Text mode, regardless of session history.
- Only if the user **continually fails production** (text entry) does the scoring demote the word back to recognition (MC) — the `PendingRecognitionCheck` mechanism handles this.
- Session-local counters (`SessionCorrectCount`, `SessionMCCorrect`, `SessionTextCorrect`) are used ONLY for tiered rotation-out logic (section 1.2.2/1.3), NOT for mode selection. Mode is driven by global persisted progress.

**CURRENT vs EXPECTED:**

| Aspect | CURRENT CODE | EXPECTED (Captain's correction) | Status |
|---|---|---|---|
| Mode trigger | `QuizRecognitionStreak >= 3` (session-local) OR `MasteryScore >= 0.50` | `Progress.CurrentStreak >= 3` (lifetime/global) OR `MasteryScore >= 0.50` | **DISCREPANCY** — code uses session-local streak instead of global |
| Scope of "3 consecutive" | Resets every session — a word must prove itself fresh each time | Lifetime — once recognized 3x correctly across ANY sessions, stays in Text mode | **DISCREPANCY** — architectural change needed |
| Data source | `QuizRecognitionStreak` (in-memory, per `VocabQuizItem`) | `VocabularyProgress.CurrentStreak` (persisted, lifetime) | **DISCREPANCY** — different field entirely |

**Implementation implications:**
- `LoadCurrentItem` must read `currentItem.Progress.CurrentStreak` (from DB) instead of `currentItem.QuizRecognitionStreak` (session memory) for mode selection.
- The old session-local `QuizRecognitionStreak`/`QuizProductionStreak` are replaced by `SessionCorrectCount`/`SessionMCCorrect`/`SessionTextCorrect` for tiered rotation-out (section 1.2.2).
- This is an architectural change: mode is no longer determined by what happens in THIS session but by the word's full learning history.

#### 1.2.1 Demotion: Text to MultipleChoice (Gentle Check)

When a user gets a **Text entry wrong**, the word should NOT fully reset back to MultipleChoice requiring 3 more MC correct. Instead:

**EXPECTED behavior (Captain's R2 correction):**
1. A wrong Text answer causes the word to appear as **ONE MultipleChoice turn** (a "recognition check" — can you still recognize this?).
2. If the user gets the MC check **CORRECT**: the `PendingRecognitionCheck` flag clears and the word returns to Text mode. They proved they still recognize it.
3. If the user gets the MC check **WRONG**: the flag does **NOT** clear. The word **stays in MC mode**. It will continue appearing as MC until the user answers correctly.
4. **Principle: Incorrect responses should never result in a promotion. It should be status quo or demotion.**

**CURRENT vs EXPECTED:**

| Aspect | CURRENT CODE | EXPECTED (Captain's R2 correction) | Status |
|---|---|---|---|
| Wrong text answer effect | Resets `QuizRecognitionStreak` to 0, demoting fully to MC; requires 3 more MC correct to re-promote | ONE MC check turn; returns to Text only on correct MC answer | **DISCREPANCY** — current is too aggressive |
| MC check — correct | N/A (uses 3-correct gate) | Clear `PendingRecognitionCheck`, return to Text mode | **NEW MECHANISM NEEDED** |
| MC check — wrong | N/A (stays in MC indefinitely until 3 correct) | Keep `PendingRecognitionCheck = true`, word stays in MC. No auto-promotion back to Text. | **NEW MECHANISM NEEDED** |
| Demotion duration | Potentially many turns (until 3 consecutive correct) | 1+ MC turns: minimum 1, but stays in MC until user proves competence | **DISCREPANCY** |

**Implementation implications:**
- Need a new per-item flag (`PendingRecognitionCheck`) on `VocabQuizItem`.
- When a Text answer is wrong: set `PendingRecognitionCheck = true`.
- In `LoadCurrentItem`: if `PendingRecognitionCheck == true`, force MC mode regardless of global progress.
- After MC turn completes:
  - **Correct**: Clear `PendingRecognitionCheck`. Next appearance returns to Text.
  - **Wrong**: Keep `PendingRecognitionCheck = true`. Word stays in MC until user answers correctly.
- The global `CurrentStreak` reset from the wrong text answer still happens (affecting mastery scoring), but it does NOT auto-promote the word back to Text. Only a correct answer can promote.

#### 1.2.2 Tiered Rotation Model (Replaces QuizRecognitionStreak/QuizProductionStreak)

> **Status: APPROVED by Captain (R3).** This replaces the old uniform 3+3 session-local streak requirement.

> **Background (R2):** Captain asked: "What's the purpose of QuizRecognitionStreak then if Progress.CurrentStreak is driving the mode change? Is it just confusing and should we remove it?"

**What QuizRecognitionStreak/QuizProductionStreak were:**
- Session-local counters on `VocabQuizItem` tracking consecutive correct MC/Text answers *within this session*.
- Originally used for both mode selection (MC→Text) and rotation-out.
- After R1, decoupled from mode selection (which now uses global `Progress.CurrentStreak`).
- Their remaining purpose was rotation-out: `QuizRecognitionComplete = (QuizRecognitionStreak >= 3)` gated `ReadyToRotateOut`.

**Why they are being removed:**
- Two different "streak" concepts (session-local vs global) are confusing.
- They force every word to demonstrate 3 MC + 3 Text correct *this session* to rotate out — even words the user clearly already knows (e.g., 84% mastery, 13-streak globally). This was flagged as discrepancy D7.
- With mode selection on global progress, a high-mastery word starts directly in Text mode and may never see an MC turn this session — making `QuizRecognitionStreak` unreachable and the word unable to rotate out.

**APPROVED replacement: Tiered rotation based on global progress.**

Instead of uniform 3+3 session-local streaks, the word's global progress determines how much in-session evidence is needed to rotate out:

| Global Progress Tier | Rotation-Out Requirement | Rationale |
|---|---|---|
| `MasteryScore >= 0.80` OR `CurrentStreak >= 8` | 1 correct text answer AND `PendingRecognitionCheck == false` | User clearly knows this word; one text production confirms it. If a recognition check is pending (wrong answer triggered it), word cannot rotate until both recognition AND production are re-demonstrated. |
| `MasteryScore >= 0.50` OR `CurrentStreak >= 3` | 2 correct answers (at least 1 Text) | User is progressing; moderate evidence needed |
| Below both thresholds | 3 correct MC + 3 correct Text (current behavior) | User is still learning; full session demonstration needed |

**Implementation:**
- `QuizRecognitionStreak` and `QuizProductionStreak` are **replaced** by a single `SessionCorrectCount` per item (incremented on any correct answer, reset on session start).
- For the lowest tier, track `SessionMCCorrect` and `SessionTextCorrect` separately to maintain the 3+3 gate.
- Eliminates the confusing session-local vs global streak distinction.
- Solves D7 (high-mastery treadmill) automatically.
- The `PendingRecognitionCheck` mechanism (section 1.2.1) remains independent — it controls mode, not rotation.

#### 1.2.3 Session Counter Model (on `VocabQuizItem`)

> **Explicit model definition** — these are the session-local tracking fields that replace the old `QuizRecognitionStreak`/`QuizProductionStreak`.

```csharp
// New session-local tracking fields (replace QuizRecognitionStreak/QuizProductionStreak)
int SessionCorrectCount;      // Total correct answers this session (any mode). Initial: 0
int SessionMCCorrect;          // MC correct answers this session. Initial: 0
int SessionTextCorrect;        // Text correct answers this session. Initial: 0
bool PendingRecognitionCheck;  // Gentle demotion flag (see 1.2.1). Initial: false
bool LostKnownThisSession;    // True if word was IsKnown at session start but lost it due to wrong answer (R5). Initial: false. Used to apply 14-day review interval on re-qualification instead of 60-day.
```

**Initial values:** All `0` / `false` at session start. Exception: `LostKnownThisSession` is initialized to `false` and set to `true` when a wrong answer causes `IsKnown` to become `false` for a word that had `MasteredAt != null`.

**Reset behavior:** These fields reset when the user leaves and re-enters the quiz (new session). They do NOT reset between rounds within the same session.

> **IMPORTANT — Cumulative, NOT consecutive:** Session counters (`SessionMCCorrect`, `SessionTextCorrect`, `SessionCorrectCount`) are **CUMULATIVE**. They increment on correct answers and **NEVER reset on wrong answers**. This is a deliberate change from the old consecutive-streak counters (`QuizRecognitionStreak`/`QuizProductionStreak`) which reset to 0 on any wrong answer. The cumulative semantics ensure that a wrong answer mid-session doesn't erase prior correct demonstrations — it only fails to add new evidence.

### 1.3 Rotation Out of Session

A word is eligible for removal from the `batchPool` when it meets the **tiered rotation-out** criteria (APPROVED R3 — see section 1.2.2).

**EXPECTED logic (tiered rotation model):**

```csharp
// Tier 1 — High mastery: 1 correct text answer + recognition cleared
if (MasteryScore >= 0.80 || CurrentStreak >= 8)
    ReadyToRotateOut = SessionTextCorrect >= 1 && PendingRecognitionCheck == false

// Tier 2 — Mid mastery: 2 correct (at least 1 text)
else if (MasteryScore >= 0.50 || CurrentStreak >= 3)
    ReadyToRotateOut = SessionCorrectCount >= 2 && SessionTextCorrect >= 1

// Tier 3 — Low mastery: full 3+3 demonstration
else
    ReadyToRotateOut = SessionMCCorrect >= 3 && SessionTextCorrect >= 3

// DueOnly bonus (unchanged):
ReadyToRotateOut = ReadyToRotateOut || Progress.IsKnown
```

**A word is NEVER presented twice in a round.** If only 2 words remain in the round, the round is 2 turns. Rounds naturally shrink as words rotate out. There is no minimum round size.

**CURRENT vs EXPECTED:**

| Aspect | CURRENT CODE | EXPECTED (APPROVED R3) | Status |
|---|---|---|---|
| Standard session rotation | Requires `QuizRecognitionStreak >= 3` + `QuizProductionStreak >= 3` (uniform 3+3) | Tiered: high-mastery = 1 correct, mid = 2 correct, low = 3+3 | **DISCREPANCY** — needs tiered implementation |
| Session-local counters | `QuizRecognitionStreak`, `QuizProductionStreak` (consecutive correct per mode) | `SessionCorrectCount`, `SessionMCCorrect`, `SessionTextCorrect` (cumulative correct per mode) | **DISCREPANCY** — replace streak counters with cumulative counters |
| DueOnly session rotation | Also rotates if `IsKnown` globally | Same — unchanged | OK |
| High-mastery rotation | 84% mastery + 13-streak still requires 3+3 | 1 correct answer is enough | **DISCREPANCY** (D7 resolved by tiered model) |
| **Rotation timing** | Rotation happens at round boundaries | **EXPECTED: Immediate removal mid-round.** As soon as a word meets criteria, remove it from `roundWordOrder` and `batchPool` before the next turn loads. | **DISCREPANCY** — Captain explicitly wants immediate removal |
| Mid-session mastery attainment | Word becomes `IsKnown` mid-session but `ReadyToRotateOut` only checks at round boundaries | **EXPECTED: Remove immediately on the very next turn** | **DISCREPANCY** |

**Implementation implications:**
- After each answer is recorded (in `RecordPendingAttemptAsync` or equivalent), check whether the word now meets `ReadyToRotateOut` or `IsKnown`.
- If yes: immediately remove it from `roundWordOrder` and `batchPool`, increment `wordsMastered`.
- The existing `SetupNewRound()` cleanup can remain as a safety net, but the primary rotation should happen mid-round.
- Need to handle edge case: if the removed word was the last in `roundWordOrder`, trigger round summary immediately.

### 1.4 Between Rounds (`SetupNewRound`)

1. Hide session summary.
2. Remove all `ReadyToRotateOut` words from `batchPool`.
3. If `batchPool` is empty, show "All words mastered!" and navigate back.
4. Randomly select up to `ActiveWordCount` (10) from `batchPool` for this round.
5. Shuffle into `roundWordOrder`.
6. Reset `currentTurnInRound = 0`.
7. Load first item.

> **DueOnly filter — session-start only (R5):** The DueOnly date filter applies ONCE during initial word selection (section 1.1). It is NOT re-applied between rounds. Words that become not-due during a session (because their NextReviewDate was pushed into the future by correct answers) remain in the batch pool for the duration of the session. Rotation out of the session is controlled exclusively by the mastery/tiered rotation logic (section 1.3), never by scheduling.

---

## 2. Per-Answer Expected Behavior

### 2.1 Multiple Choice — Correct

**Immediate state changes:**
| Field | Change |
|---|---|
| `SessionCorrectCount` | +1 |
| `SessionMCCorrect` | +1 |
| `WasCorrectThisSession` | Set to `true` |
| `totalTurns` | +1 |
| `correctCount` | +1 |
| `PendingRecognitionCheck` | Cleared (correct MC answer proves recognition — see 1.2.1) |

**Deferred persistence** (on `NextItem` or auto-advance):
| Field | Change |
|---|---|
| `Progress.TotalAttempts` | +1 |
| `Progress.CorrectAttempts` | +1 |
| `Progress.CurrentStreak` | +1 (MC DifficultyWeight = 1.0, so streak increment = 1.0) |
| `Progress.ProductionInStreak` | Unchanged (MC is not production) |
| `Progress.MasteryScore` | Recalculated: `min(EffectiveStreak / 7.0, 1.0)` where `EffectiveStreak = CurrentStreak + (ProductionInStreak * 0.5)` — with recovery boost if in recovery period (see section 5.6) |
| `Progress.NextReviewDate` | Updated via SM-2 (interval grows) |
| `Progress.ReviewInterval` | SM-2: first correct = 6 days, then *= EaseFactor |
| `Progress.LastPracticedAt` | Now |

> **DifficultyWeight (R5):** The streak increment is weighted by DifficultyWeight. For MC (`DifficultyWeight = 1.0`), this is +1.0 — identical to old behavior. For Text and Sentence modes the increment is larger (see sections 2.3, 2.5). The weight is applied to `EffectiveStreak` calculation: `EffectiveStreak = Sum(DifficultyWeight per correct answer) + (ProductionInStreak * 0.5)`. See section 5.6 for the full formula.

**Learning Details panel:** EXPECTED to reflect the updated `Progress` immediately after persistence. **CURRENT:** The `currentItem.Progress` reference IS updated (returned from `RecordAttemptAsync`), but **the panel reads from `currentItem.Progress` which is NOT updated until `RecordPendingAttemptAsync` fires on advance.** During the answer-shown state, the panel shows **stale pre-answer data**.

**DISCREPANCY:** Learning Details panel shows stale data while the answer is displayed. The Captain's expectation is: *"My activity immediately is reflected in the learning details for that word."*

**Mode change in subsequent turns:** If `Progress.CurrentStreak >= 3` (lifetime), next time this word appears it will be in Text mode. Note: session-local counters do NOT drive mode — only the persisted global streak does.

### 2.2 Multiple Choice — Wrong

**Immediate state changes:**
| Field | Change |
|---|---|
| `SessionCorrectCount` | Unchanged |
| `WasCorrectThisSession` | Unchanged (stays whatever it was) |
| `totalTurns` | +1 |
| `PendingRecognitionCheck` | Unchanged — stays `true` if set (wrong MC does NOT clear the flag) |

**Deferred persistence:**
| Field | Change |
|---|---|
| `Progress.TotalAttempts` | +1 |
| `Progress.CurrentStreak` | Partially preserved based on track record (see section 5.6 — temporal weighting) |
| `Progress.ProductionInStreak` | Partially preserved based on track record (see section 5.6) |
| `Progress.MasteryScore` | Scaled penalty based on track record (see section 5.6 — ranges from 0.6 to 0.92) |
| `Progress.NextReviewDate` | Reset to tomorrow (interval = 1 day) |
| `Progress.EaseFactor` | Decreased by 0.2 (min 1.3) |

**Mode change:** If word was in Text mode (promoted via lifetime streak), it triggers a **gentle demotion** — set `PendingRecognitionCheck = true`. The word will appear as MC for one or more turns until the user answers correctly. Only a **correct** MC answer clears the flag and returns the word to Text mode. A wrong MC answer keeps the flag set — the word stays in MC. **Principle: incorrect responses never result in promotion.** See section 1.2.1.

**CURRENT vs EXPECTED:**

| Aspect | CURRENT CODE | EXPECTED (Captain's R3 corrections) | Status |
|---|---|---|---|
| Wrong MC penalty | Flat `*= 0.6` + full streak reset to 0 | Scaled penalty + partial streak preservation based on track record (section 5.6) | **DISCREPANCY** — needs temporal weighting |
| Wrong MC demotion | Resets `QuizRecognitionStreak` to 0; word stays in MC until 3 more correct | Mode driven by lifetime `CurrentStreak` + `PendingRecognitionCheck`; tiered rotation (section 1.2.2) | **DISCREPANCY** — needs new counter model |
| Stale Learning Details | Panel shows pre-answer data | Same issue as 2.1 | DISCREPANCY |

### 2.3 Text Entry — Correct

**Immediate state changes:**
| Field | Change |
|---|---|
| `SessionCorrectCount` | +1 |
| `SessionTextCorrect` | +1 |
| `WasCorrectThisSession` | Set to `true` |
| `totalTurns` | +1 |
| `correctCount` | +1 |

**Deferred persistence:**
| Field | Change |
|---|---|
| `Progress.TotalAttempts` | +1 |
| `Progress.CorrectAttempts` | +1 |
| `Progress.CurrentStreak` | +1.5 (Text DifficultyWeight = 1.5, applied to EffectiveStreak) |
| `Progress.ProductionInStreak` | +1 (text IS production) |
| `Progress.MasteryScore` | Recalculated: recovery-aware formula (see section 5.6, Component 3). `EffectiveStreak = CurrentStreak + (ProductionInStreak * 0.5)` — Text entry adds 1.5 to CurrentStreak + 0.5 production bonus = 2.0 effective per correct answer |
| SRS fields | Updated via SM-2 |

**Text matching:** Uses `FuzzyMatcher.Evaluate` — allows minor typos/form variants. If correct but not exact, shows "Full form: {completeForm}".

**Mastery check:** If after this attempt `MasteryScore >= 0.85 AND ProductionInStreak >= 2`, the word is marked `IsKnown`, `MasteredAt` is set, and `ReviewInterval` jumps to 60 days. **Exception (R5):** If the word was previously `IsKnown` (has `MasteredAt` set) and lost that status this session due to a wrong answer, re-qualification sets `ReviewInterval = 14 days` (not 60) to verify the recovery sticks. See section 4.3 for the full re-qualification logic.

### 2.4 Text Entry — Wrong

**Immediate state changes:**
| Field | Change |
|---|---|
| `SessionCorrectCount` | Unchanged |
| `requireCorrectTyping` | Set to `true` — user must retype the correct answer before advancing |
| `totalTurns` | +1 |

**Deferred persistence:** Same as MC wrong — scaled penalty + partial streak preservation based on track record (section 5.6). SRS reset to 1 day.

**Gentle demotion (Captain's R2 correction):** A wrong text answer triggers the **recognition check** mechanism (section 1.2.1). Set `PendingRecognitionCheck = true`. The very next time this word appears, it shows as MC. The flag only clears when the user answers the MC check **correctly** — proving they still recognize the word. If they get the MC check wrong, the word **stays in MC** until they demonstrate competence. Incorrect responses never result in promotion back to Text.

**CURRENT vs EXPECTED:**

| Aspect | CURRENT CODE | EXPECTED (Captain's R2 correction) | Status |
|---|---|---|---|
| Wrong text demotion | Resets `QuizRecognitionStreak` to 0, fully demoting to MC; needs 3 more MC correct to re-promote | MC check: returns to Text only on correct MC answer; stays in MC on wrong | **DISCREPANCY** — current is too aggressive/severe |
| Flag clearing | N/A | `PendingRecognitionCheck` clears ONLY on correct MC answer, NOT on any MC answer | **NEW MECHANISM** |
| Global streak/mastery impact | `CurrentStreak` resets to 0, `MasteryScore *= 0.6` | Scaled penalty + partial streak preservation based on track record (section 5.6, APPROVED R3). Scoring does NOT control mode — mode is controlled by the unified algorithm (section 1.2). | **DISCREPANCY** — needs temporal weighting |

**Retype requirement:** The input field clears and the placeholder changes to "Type the correct answer to continue." The user must type the correct answer to proceed. An "I was correct" override button is also shown.

### 2.5 Sentence Shortcut

The user can open a sentence-writing mode from the page header menu at any time during a turn.

**Flow:**
1. User writes one or more sentences using the current target word.
2. AI grades each sentence for `UsageCorrect` (did they use the target word meaningfully?).
3. Only sentences where `UsageCorrect == true` count toward mastery.

**For each credited sentence:**
| Field | Change |
|---|---|
| `Progress.TotalAttempts` | +1 |
| `Progress.CorrectAttempts` | +1 |
| `Progress.CurrentStreak` | +2.5 (Sentence DifficultyWeight = 2.5, applied to EffectiveStreak) |
| `Progress.ProductionInStreak` | +1 (recorded as `InputMode.Text`) |
| `Progress.MasteryScore` | Recalculated via recovery-aware formula (section 5.6, Component 3). `EffectiveStreak = CurrentStreak + (ProductionInStreak * 0.5)` — Sentence adds 2.5 to CurrentStreak + 0.5 production bonus = 3.0 effective per credited sentence |
| `SessionCorrectCount` | +1 |
| `SessionTextCorrect` | +1 |
| `WasCorrectThisSession` | Set to `true` (if any sentence passed) |
| `totalTurns` | += total sentence count |
| `correctCount` | += credited sentence count |

**DifficultyWeight:** Sentence shortcut uses `2.5f` (higher than text entry's `1.5f`), reflecting the significantly greater production effort. Writing a sentence requires vocabulary recall, grammar knowledge, contextual understanding, and composition skill — substantially harder than single-word text recall. **This weight directly accelerates mastery** — each credited sentence adds 2.5 to the effective streak (plus the 0.5 production bonus), meaning a single credited sentence builds mastery ~3x faster than a single MC correct.

**Difficulty weight scale (functional — directly affects mastery via EffectiveStreak):**

| Input Mode | DifficultyWeight | Streak Increment | + Production Bonus | = Effective per Correct |
|---|---|---|---|---|
| Multiple Choice | `1.0f` (default) | +1.0 | +0.0 (not production) | **1.0** |
| Text Entry | `1.5f` | +1.5 | +0.5 | **2.0** |
| Sentence Production | `2.5f` | +2.5 | +0.5 | **3.0** |

**CURRENT vs EXPECTED:**

| Aspect | CURRENT | EXPECTED | Status |
|---|---|---|---|
| `WasCorrectThisSession` set | Yes (Fix 1 applied) | Same | OK |
| Per-sentence recording | Each credited sentence is a separate `RecordAttemptAsync` call | Same — allows streak to build correctly | OK |
| Non-credited sentences | No attempt recorded | **EXPECTED: Should record as wrong attempt to maintain accurate stats** | DISCREPANCY — inflates accuracy |
| Sentence shortcut does not record wrong attempts | Only correct sentences generate `RecordAttemptAsync` calls | EXPECTED: All graded sentences should be recorded (correct or not) | DISCREPANCY |

> **Known quirk — turn count inflation:** Sentence shortcut adds `totalTurns += totalSentenceCount`, which inflates session stats. A user who writes 5 sentences in one shortcut interaction sees 5 turns counted, even though only 1 word card was presented. **Recommended approach:** Count sentence shortcut as 1 turn with multiple credited attempts, not multiple turns. The session summary should display sentence attempts separately or annotate the inflated count. This is a UX polish issue, not a correctness bug — mastery scoring is unaffected.

### 2.6 Override as Correct

When the user gets a text answer wrong and is in retype mode, they can click "I was correct."

**Behavior:**
1. `isCorrect` set to `true`.
2. `requireCorrectTyping` set to `false`.
3. Appropriate quiz streak incremented (+1).
4. `WasCorrectThisSession` set to `true`.
5. `pendingAttempt.WasCorrect` flipped to `true`.
6. `RecordPendingAttemptAsync` called immediately (not deferred).
7. `correctCount` +1.
8. Auto-advance timer starts.

**CURRENT vs EXPECTED:**

| Aspect | CURRENT | EXPECTED | Status |
|---|---|---|---|
| Override persists as correct | Yes | Same | OK |
| Override restores streak | Increments quiz streak by 1 | **Minor concern: The global `CurrentStreak` was already reset to 0 by the initial wrong answer recording... but wait — the pending attempt hasn't been persisted yet.** The override changes the pending attempt to correct *before* persisting. This is correct. | OK |
| Learning Details update | Panel shows updated progress after override persists | Panel reads `currentItem.Progress` which IS updated by `RecordPendingAttemptAsync` returning new progress — **but `currentItem.Progress` is not reassigned in the override path** | DISCREPANCY — Progress reference not updated after override |

---

## 3. Session Summary Expected Behavior

### 3.1 When Summary Appears

The session summary appears when:
- `currentTurnInRound >= roundWordOrder.Count` (all words in round shown), OR
- `currentTurnInRound >= TurnsPerRound` (10 turns completed)

### 3.2 Summary Data

| Metric | Calculation | Display |
|---|---|---|
| **Correct** | `correctCount` — running total across all rounds | Green number |
| **Total** | `totalTurns` — running total across all rounds | Plain number |
| **Mastered** | `wordsMastered` — words removed via `ReadyToRotateOut` | Green number |
| **Rounds** | `roundsCompleted` — incremented each time a round ends | Plain number |

### 3.3 Per-Word Summary

Each word in `sessionItems` (= `vocabItems` at round end) shows:
- Green filled check (`bi-check-circle-fill`) if `WasCorrectThisSession && ReadyToRotateOut`
- Green outline check (`bi-check-circle`) if `WasCorrectThisSession && !ReadyToRotateOut`
- Red X (`bi-x-circle`) if `!WasCorrectThisSession`

**CURRENT vs EXPECTED:**

| Aspect | CURRENT | EXPECTED | Status |
|---|---|---|---|
| Correct icon logic | Based on `WasCorrectThisSession` | Should reflect whether the user answered correctly *in this round*, not just *ever this session* | MINOR DISCREPANCY — a word answered wrong in Round 1 but correct in Round 2 shows as correct in both summaries |
| Mastered count | Based on `ReadyToRotateOut` | Correct — only counts words that proved mastery | OK |
| Session totals persist across rounds | `correctCount` and `totalTurns` accumulate | Correct behavior — shows cumulative session progress | OK |

### 3.4 Continue vs Done

- **Continue** button calls `SetupNewRound()` — starts a new round with remaining `batchPool` words.
- **Done** button calls `GoBack()` — navigates to dashboard.
- Both buttons are always shown in the summary.

**EXPECTED:** When `batchPool` is empty after removing mastered words, `SetupNewRound` shows "All words mastered!" toast and auto-navigates back. The Continue button should either be hidden when no words remain, or handle the empty case gracefully. **CURRENT:** The empty check happens inside `SetupNewRound`, so tapping Continue with no words left still works (auto-navigates). Acceptable but could be more explicit in UI.

---

## 4. Cross-Session Behavior

### 4.1 Progress Persistence

All progress is persisted via `VocabularyProgressService.RecordAttemptAsync` which:
1. Gets or creates a `VocabularyProgress` record (one per word per user).
2. Updates streak, mastery, and SRS fields.
3. Saves to SQLite via `VocabularyProgressRepository`.
4. Records a `VocabularyLearningContext` entry (detailed attempt log).

**Persistence timing:**
- Standard MC/Text answers: Deferred until `NextItem()` or `OverrideAsCorrect()`.
- Sentence shortcut: Persisted immediately per credited sentence.
- On page dispose: `RecordPendingAttemptAsync` is called to catch any un-persisted answer.

### 4.2 SRS Scheduling

**SM-2 algorithm implementation:**

| Event | ReviewInterval | EaseFactor |
|---|---|---|
| First correct answer | 6 days | Unchanged |
| Subsequent correct | `interval *= EaseFactor` (capped at 365 days) | +0.1 (max 2.5) |
| Wrong answer | Reset to 1 day | -0.2 (min 1.3) |
| Mastery reached (`IsKnown`) — first time | **Override to 60 days** | Unchanged |
| Mastery RE-qualified (`IsKnown` lost and regained — R5) | **Override to 14 days** (shorter to verify recovery sticks) | Unchanged |

**NextReviewDate = Now + ReviewInterval**

**CURRENT vs EXPECTED:**

| Aspect | CURRENT | EXPECTED | Status |
|---|---|---|---|
| SM-2 implementation | Basic but functional | Same | OK |
| Mastery override | Known words get 60-day interval regardless of SM-2 | Good — prevents over-reviewing mastered words | OK |
| SRS respected in DueOnly mode | Filter checks `NextReviewDate <= now` | Same | OK |
| SRS respected in standard mode | **No** — standard mode loads all non-Known words regardless of SRS | **EXPECTED: Standard mode intentionally ignores SRS (user chose to practice this resource).** This is correct by design. | OK |

### 4.3 When a Word Stops Appearing

A word permanently exits quizzes when `IsKnown == true`:

**Primary path:**
```
MasteryScore >= 0.85 AND ProductionInStreak >= 2
```

**High-confidence bypass:**
```
MasteryScore >= 0.75
AND ProductionInStreak >= 4
AND TotalAttempts >= 8
```

#### 4.3.1 IsKnown Re-qualification — Shorter Review Interval (R5)

When a word loses `IsKnown` status due to a wrong answer and then re-qualifies, it receives a shorter review interval to verify the recovery sticks:

```
IF word was previously IsKnown (MasteredAt != null)
   AND lost IsKnown this session (wrong answer dropped MasteryScore or ProductionInStreak below threshold)
   AND now re-qualifies as IsKnown (correct answers restored it)
THEN ReviewInterval = 14 days (not 60)
```

**Detection mechanism:** Track whether a word lost IsKnown status this session via a `LostKnownThisSession` flag on `VocabQuizItem`. Alternatively, detect it at recording time by checking `MasteredAt != null && !IsKnown` before the correct answer is recorded — if both are true, the word previously had IsKnown and lost it. After re-qualification, set `ReviewInterval = 14` instead of the standard 60.

**Rationale:** 14 days is long enough to verify the recovery wasn't a fluke, but short enough that the user doesn't have to wait 60 days to confirm. A word that was mastered, forgotten, and re-mastered deserves closer follow-up than a word mastered for the first time.

**What this means in practice:**
- Minimum path to IsKnown: ~7 consecutive correct answers (including 2+ text/production), no prior wrong answers.
- With some wrong answer history: The 40% mastery penalty on wrong answers means recovery requires more consecutive correct answers.
- The high-confidence bypass allows words with lower mastery but extensive correct production history to qualify.

**CURRENT vs EXPECTED:**

| Aspect | CURRENT | EXPECTED | Status |
|---|---|---|---|
| IsKnown threshold | 85% + 2 production | Captain expects ~3 correct sentence productions should "test out" | DISCREPANCY — 3 sentence productions would give `ProductionInStreak = 3` and boost mastery significantly, but may not reach 85% if there's wrong-answer history |
| High-confidence bypass | 75% + 4 production + 8 attempts | Reasonable safety valve | OK |
| Known words in future sessions | Filtered out in `LoadVocabulary` | Same | OK |
| User-declared familiar | Grace period (14 days) then verification probes | Same | OK |

### 4.4 Cross-Activity Behavior

This spec is VocabQuiz-specific, but VocabQuiz shares `VocabularyProgress` with all other activities (Cloze, Writing, Translation, VocabMatching). This section clarifies the boundary.

**Global progress fields — shared across ALL activities:**
- `CurrentStreak`, `MasteryScore`, `ProductionInStreak`, `TotalAttempts`, `CorrectAttempts`, `EaseFactor`, `ReviewInterval`, `NextReviewDate`, `IsKnown`, `MasteredAt`, `LastPracticedAt`
- A correct answer in **any** activity increments `CurrentStreak` and may raise `MasteryScore`. This means a user who gets 2 correct MC in VocabQuiz and then 1 correct in VocabMatching has `CurrentStreak = 3`, which triggers Text mode in the next VocabQuiz session.

**Session-local fields — scoped to a single VocabQuiz session:**
- `PendingRecognitionCheck`, `SessionCorrectCount`, `SessionMCCorrect`, `SessionTextCorrect`, `WasCorrectThisSession`
- These exist only in memory on `VocabQuizItem` and are destroyed when the user leaves the quiz page.

**Activity-switching resets session-local state — by design:**
- If a user gets a word wrong in VocabQuiz (setting `PendingRecognitionCheck = true`), then switches to Writing activity and answers correctly, then returns to VocabQuiz — the `PendingRecognitionCheck` flag is gone (new session). The word's mode is determined by the updated global `CurrentStreak`/`MasteryScore`.
- This is intentional: the user continued practicing the word in a different context and demonstrated competence there. Forcing a recognition check after successful production in another activity would be punitive.

---

## 5. Captain's Stated Expectations

### 5.1 "My activity immediately is reflected in the learning details for that word"

**CURRENT:** Progress is persisted via a **deferred recording** pattern. The `pendingAttempt` is created when the answer is checked, but only persisted when the user advances to the next item (or the auto-advance timer fires). The Learning Details panel reads from `currentItem.Progress`, which holds the **pre-answer** state until `RecordPendingAttemptAsync` runs and returns an updated `VocabularyProgress` object.

**However**, `RecordPendingAttemptAsync` updates `currentItem.Progress` only indirectly — the returned progress is NOT assigned back to `currentItem.Progress` in the standard flow. Looking at the code:

```csharp
private async Task RecordPendingAttemptAsync()
{
    var updatedProgress = await ProgressService.RecordAttemptAsync(attempt);
    // updatedProgress is NOT assigned back to currentItem.Progress
}
```

**VERDICT: BROKEN.** The Learning Details panel never reflects the current answer's impact. It shows stale data from before the answer was submitted.

**EXPECTED FIX:** After `RecordAttemptAsync` returns, assign the result back:
```csharp
var updatedProgress = await ProgressService.RecordAttemptAsync(attempt);
// Find the item and update its Progress reference
var item = vocabItems.FirstOrDefault(i => i.Word.Id == attempt.VocabularyWordId)
        ?? batchPool.FirstOrDefault(i => i.Word.Id == attempt.VocabularyWordId);
if (item != null) item.Progress = updatedProgress;
```

### 5.2 "Immediately impacts how that word shows up in the current activity"

**CURRENT:** Mode selection (`LoadCurrentItem`) evaluates `currentItem.Progress.MasteryScore` each turn, so if progress were updated in real-time, this would work. But because of the deferred recording pattern, the mastery score used for mode selection may be stale.

**Under the revised model (Captain's corrections):** Mode selection now depends on **lifetime** `Progress.CurrentStreak` and `Progress.MasteryScore`. Session-local streaks no longer drive mode. This makes stale progress data MORE impactful — if the previous answer's progress hasn't been persisted yet, the mode decision for the next turn uses outdated global state.

**VERDICT: MORE IMPORTANT NOW.** The deferred recording pattern must be addressed before implementing the lifetime-based mode selection. Either persist immediately, or at minimum update `currentItem.Progress` in memory after each answer.

### 5.3 "Words I clearly know should stop appearing"

**CURRENT:** Words stop appearing when `IsKnown == true`, which requires `MasteryScore >= 0.85 AND ProductionInStreak >= 2`. The quiz-local rotation (`ReadyToRotateOut`) requires 3 consecutive MC correct + 3 consecutive Text correct *within this session*.

**VERDICT: PARTIALLY BROKEN for high-mastery words.** A word with 84% mastery and a 13-streak still appears every session because:
1. It's not `IsKnown` (84% < 85%).
2. It needs 3+3 in-session to rotate out.
3. In DueOnly mode, the `IsKnown` bypass helps — but only if the word has actually reached IsKnown.

**EXPECTED:** Words with sustained high streaks (e.g., CurrentStreak >= 8 AND ProductionInStreak >= 3) should either:
- Qualify for `IsKnown` via an additional bypass, OR
- Have a reduced in-session rotation requirement (e.g., 1 correct in text mode).

### 5.4 "3 correct sentence productions should test out of a word"

**CURRENT:** Three sentence shortcut productions where all pass `UsageCorrect`:
- `CurrentStreak` increases by 3
- `ProductionInStreak` increases by 3
- `EffectiveStreak` increases by 4.5 (3 + 3*0.5)
- If starting from 0: `MasteryScore = min(4.5/7.0, 1.0) = 0.643 (64%)`
- Not enough for `IsKnown` (needs 85%)
- Under old model: `QuizProductionStreak` = 3 but `QuizRecognitionStreak` = 0 → `ReadyToRotateOut = false`
- Under tiered model (R3): `SessionCorrectCount` = 3, `SessionTextCorrect` = 3 → depends on mastery tier

**VERDICT: PARTIALLY FIXED by tiered rotation (R3).** Under the new tiered model:
- If the word already has `MasteryScore >= 0.50` or `CurrentStreak >= 3`: Tier 2 applies → only needs `SessionCorrectCount >= 2 AND SessionTextCorrect >= 1` → 3 sentence productions DOES rotate out.
- If the word starts from zero: Tier 3 applies → needs `SessionMCCorrect >= 3` which sentence shortcut doesn't provide.

**REMAINING FIX NEEDED:**
- Sentence shortcut with 3+ credited sentences should count toward `SessionMCCorrect` as well (or bypass the MC requirement entirely) for Tier 3 words.
- OR: Add a "sentence mastery fast-track" — 3 correct sentence productions in a session = rotate out regardless of tier.

---

### 5.5 State Persistence Model: Immediate vs Deferred

> **Captain's question (R2):** "Why do we have immediate state vs deferred state persistence? Is this a good pattern?"

The quiz uses a **two-phase state update** pattern. This section explains why, whether it's sound, and what the expected timing is.

**Phase 1 — Immediate (in-memory, session-local):**
When the user submits an answer, these fields update instantly in memory:
- `SessionCorrectCount` / `SessionMCCorrect` / `SessionTextCorrect` — session-local counters for tiered rotation-out (section 1.2.2)
- `WasCorrectThisSession` — flag for summary display
- `correctCount`, `totalTurns` — session stats
- `isCorrect`, `requireCorrectTyping` — UI state for the current turn

**Phase 2 — Deferred (DB persistence via `RecordPendingAttemptAsync`):**
The actual DB write happens later — when the user advances to the next word (or the auto-advance timer fires):
- `Progress.CurrentStreak`, `Progress.MasteryScore`, `Progress.ProductionInStreak` — global/lifetime stats
- `Progress.NextReviewDate`, `Progress.ReviewInterval`, `Progress.EaseFactor` — SRS scheduling
- `VocabularyLearningContext` — detailed attempt log

**Why the deferred pattern exists:**
The quiz flow is: **answer → show result → (optional retype/override) → advance → next word**. Between answering and advancing, the user may:
1. View the result and Learning Details panel
2. Retype a wrong answer to reinforce memory
3. Use the "I was correct" override, which flips the `pendingAttempt` from wrong to correct

If we persisted immediately on answer, the override would require a separate DB update (or undo). The deferred pattern lets the `pendingAttempt` object be modified in memory before a single, final write.

**Is this pattern sound?** Yes — with one critical fix. The deferred pattern is architecturally reasonable for the answer → review → advance flow. The bug was that `RecordPendingAttemptAsync` calls `RecordAttemptAsync` which returns an updated `VocabularyProgress` object, but that returned object was **not written back** to `currentItem.Progress`. This one-line fix (assigning the returned progress back to the item) is the key to making the Learning Details panel reflect current state. See section 5.1 for the fix.

**Expected timing from the user's perspective:**
1. User sees word, submits answer
2. UI immediately shows correct/wrong feedback + updated session stats (Phase 1)
3. Learning Details panel should reflect updated global progress (requires the one-line fix above)
4. User reviews the result, optionally retypes or overrides
5. User advances (tap "Next" or auto-advance fires)
6. `RecordPendingAttemptAsync` persists to DB (Phase 2)
7. Next word loads with fresh mode selection based on now-persisted global progress

**Key invariant:** The user should never see stale Learning Details data. The in-memory update (writing returned progress back to `currentItem.Progress`) must happen at answer time, not at advance time, even though DB persistence is deferred.

### 5.6 Temporal Weighting for Wrong-Answer Impact (APPROVED R3)

> **Status: APPROVED by Captain.** Based on Wash's analysis (`.squad/decisions/inbox/wash-temporal-weighting.md`).

**The problem — the "double-whammy" bug:**

The current mastery scoring has a critical flaw: a correct answer after a wrong answer can make mastery **WORSE**.

**Captain's scenario:** User learns a word, gets it wrong once early, then gets it right 10 times in a row (including 4 production). MasteryScore = 1.00, IsKnown = true. Then ONE wrong answer:

| Step | CurrentStreak | ProdInStreak | MasteryScore | IsKnown |
|------|--------|-------------|-------------|---------|
| Before wrong | 10 | 4 | 1.00 | Yes |
| After wrong (current) | **0** | **0** | **0.60** | **No** |
| 1st correct after (current) | 1 | 0 | **0.14** | No |

The correct answer makes mastery DROP from 0.60 to 0.14 because the formula `MasteryScore = min(EffectiveStreak/7, 1.0)` **replaces** the penalized score entirely. With streak = 1, the new score is 1/7 = 0.14 — worse than the 0.60 penalty. It takes **7 correct answers** to recover.

**CURRENT vs EXPECTED:**

| Aspect | CURRENT CODE | EXPECTED (APPROVED R3) | Status |
|---|---|---|---|
| Wrong answer penalty | Flat `*= 0.6` regardless of track record | Scaled by track record: 0.6 (new learner) to 0.92 (50+ correct) | **DISCREPANCY** |
| Streak reset on wrong | Full reset to 0 | Partial preservation based on track record (0% for new, up to 50% for established) | **DISCREPANCY** |
| Correct answer mastery | `MasteryScore = min(streak/7, 1.0)` — replaces old score, can be LOWER | `MasteryScore = max(streakScore, currentMastery)` — can only RAISE | **DISCREPANCY** (double-whammy bug) |

**APPROVED 3-part fix:**

#### Component 1: Scaled Wrong-Answer Penalty

Instead of a flat `*= 0.6`, the penalty softens as the learner accumulates correct answers:

```csharp
float penaltyFactor = MathF.Max(
    WRONG_ANSWER_FLOOR,  // 0.6 — never softer than this
    1.0f - (MAX_WRONG_PENALTY / (1f + MathF.Log(1 + progress.CorrectAttempts)))
);
progress.MasteryScore *= penaltyFactor;
```

| CorrectAttempts | Penalty Factor | Mastery Drop (from 1.0) |
|-----------------|---------------|----------------------|
| 0 | 0.60 | 1.0 → 0.60 (40% drop) |
| 2 | 0.76 | 1.0 → 0.76 (24% drop) |
| 5 | 0.85 | 1.0 → 0.85 (15% drop) |
| 10 | 0.88 | 1.0 → 0.88 (12% drop) |
| 20 | 0.90 | 1.0 → 0.90 (10% drop) |
| 50 | 0.92 | 1.0 → 0.92 (8% drop) |

#### Component 2: Partial Streak Preservation

Instead of a full streak reset to 0, established words keep a portion:

```csharp
float preserveFraction = MathF.Min(
    MAX_STREAK_PRESERVE,  // 0.5 — never keep more than half
    MathF.Log(1 + progress.CorrectAttempts) / STREAK_PRESERVE_DIVISOR  // 8.0
);
progress.CurrentStreak = (int)(progress.CurrentStreak * preserveFraction);
progress.ProductionInStreak = (int)(progress.ProductionInStreak * preserveFraction);
```

| CorrectAttempts | Preserve % | Streak 10 → | Streak 5 → |
|-----------------|-----------|-------------|------------|
| 0 | 0% | 0 | 0 |
| 2 | 14% | 1 | 0 |
| 5 | 22% | 2 | 1 |
| 10 | 30% | 3 | 1 |
| 20 | 38% | 3 | 1 |
| 50 | 49% | 4 | 2 |

#### Component 3: Recovery-Aware Correct-Answer Mastery (fixes the double-whammy + plateau)

On correct answers, mastery should never DROP, **and** during recovery it should always show visible progress:

```csharp
// CURRENT (broken for recovery — creates flat plateau):
progress.MasteryScore = Math.Min(effectiveStreak / EFFECTIVE_STREAK_DIVISOR, 1.0f);

// R3 FIX (correct answer can only raise mastery — but still has plateau):
// float streakScore = Math.Min(effectiveStreak / EFFECTIVE_STREAK_DIVISOR, 1.0f);
// progress.MasteryScore = Math.Max(streakScore, progress.MasteryScore);

// R5 FIX (recovery-aware — every correct answer shows visible progress):
float streakScore = MathF.Min(effectiveStreak / EFFECTIVE_STREAK_DIVISOR, 1.0f);
float recoveryBoost = (progress.MasteryScore > streakScore) ? RECOVERY_BOOST_PER_CORRECT : 0f;
progress.MasteryScore = MathF.Max(streakScore, progress.MasteryScore) + recoveryBoost;
progress.MasteryScore = MathF.Min(progress.MasteryScore, 1.0f);
```

**How it works:**
- When `streakScore >= MasteryScore` (normal progression): mastery = streakScore, no boost needed. Streak has caught up to or passed current mastery.
- When `streakScore < MasteryScore` (recovery period — streak was partially preserved but mastery was only lightly penalized): each correct answer adds `+0.02` to mastery. This ensures visible progress on every correct answer during recovery.
- The boost stops automatically once the streak catches up (at which point `streakScore >= MasteryScore` and `recoveryBoost = 0`).

**Recovery scenario table (R5):**

| Step | CurrentStreak | EffectiveStreak | streakScore | MasteryScore | recoveryBoost | Final Mastery | Visible Change? |
|------|--------|-------------|-------------|-------------|-------------|-------------|-------------|
| Before wrong (established, 12 correct) | 10 | 12.0 | 1.00 | 1.00 | — | 1.00 | — |
| After wrong (penalty=0.88, preserve=30%) | 3 | 3.5 | 0.50 | 0.88 | — | 0.88 | Drop visible |
| 1st correct (MC, wt=1.0) | 4 | 4.5 | 0.64 | 0.88 | +0.02 | **0.90** | Yes (+0.02) |
| 2nd correct (Text, wt=1.5) | 5.5 | 6.5 | 0.93 | 0.90 | 0 | **0.93** | Yes (streak caught up) |
| 3rd correct (MC) | 6.5 | 7.5 | 1.00 | 0.93 | 0 | **1.00** | Yes |

Compare with R3 (Math.Max only — no recovery boost):

| Step | MasteryScore (R3) | Visible Change? |
|------|-------------|-------------|
| After wrong | 0.88 | Drop visible |
| 1st correct | **0.88** (flat) | **No — plateau** |
| 2nd correct | **0.88** (flat) | **No — plateau** |
| 3rd correct | **0.93** | Yes (streak finally caught up) |

The R5 formula eliminates the flat period entirely. Regaining mastery is EASIER than initially earning it — exactly as the Captain specified.

#### Combined Scenario — Captain's Example (with R5 fix: DifficultyWeight + recovery boost)

**User with 12 correct, streak of 10, ProdInStreak of 4, MasteryScore 1.0:**

| Step | Streak | ProdInStreak | EffStreak | streakScore | recoveryBoost | MasteryScore | IsKnown |
|------|--------|-------------|-----------|-------------|-------------|-------------|---------|
| Before wrong | 10 | 4 | 12.0 | 1.00 | — | 1.00 | Yes |
| After wrong (penalty=0.88, preserve=30%) | 3 | 1 | 3.5 | 0.50 | — | 0.88 | No* |
| 1st correct (prod, wt=1.5) | 4.5 | 2 | 5.5 | 0.79 | +0.02 | 0.90 | Yes |
| 2nd correct (prod, wt=1.5) | 6.0 | 3 | 7.5 | 1.00 | 0 | 1.00 | Yes |

*Temporarily loses Known because ProdInStreak drops to 1 < 2, but recovers after just 1 production correct.

**Compare: current system needs 7 correct to recover. With R5 fix: 1–2 correct answers.**

#### New learner scenario (2 correct, streak 2, mastery 0.29)

| Step | Streak | ProdInStreak | MasteryScore |
|------|--------|-------------|-------------|
| Before wrong | 2 | 0 | 0.29 |
| After wrong (penalty=0.76, preserve=14%) | 0 | 0 | 0.22 |
| 1st correct | 1 | 0 | 0.22 (floor) |
| 2nd correct | 2 | 0 | 0.29 (streak) |

New learners still get punished appropriately — exactly the intent.

#### Constants

```csharp
// Existing (unchanged)
private const float MASTERY_THRESHOLD = 0.85f;
private const int MIN_PRODUCTION_FOR_KNOWN = 2;
private const float EFFECTIVE_STREAK_DIVISOR = 7.0f;

// Modified
private const float WRONG_ANSWER_FLOOR = 0.6f;         // Minimum penalty factor
private const float MAX_WRONG_PENALTY = 0.4f;           // Maximum penalty magnitude

// New (R3)
private const float MAX_STREAK_PRESERVE = 0.5f;         // Never preserve more than 50% of streak
private const float STREAK_PRESERVE_DIVISOR = 8.0f;     // Controls how fast preservation grows

// New (R5)
private const float RECOVERY_BOOST_PER_CORRECT = 0.02f; // Mastery boost per correct during recovery period
private const int REQUALIFICATION_REVIEW_DAYS = 14;     // Shorter review interval for re-qualified IsKnown words
```

**DB migration required?** No. All three changes use existing fields: `CorrectAttempts`, `CurrentStreak`, `ProductionInStreak`, `MasteryScore`.

**SRS impact:** None. The `UpdateSpacedRepetitionSchedule` method is independent and still resets `ReviewInterval = 1` on wrong answers. SRS ensures the word comes back soon; temporal weighting ensures mastery reflects actual knowledge.

---

## 6. Discrepancy Summary

| # | Category | Severity | Description |
|---|---|---|---|
| D1 | Learning Details | **HIGH** | Panel shows stale pre-answer data. Progress not assigned back to `currentItem.Progress` after recording. Captain's #1 expectation violated. |
| D2 | Sentence Shortcut | **HIGH** | 3 correct sentence productions don't test out. MC recognition requirement blocks rotation. Captain's explicit expectation violated. |
| D3 | **Mode Selection** | **HIGH** | Code uses session-local `QuizRecognitionStreak` for MC→Text promotion. Captain expects LIFETIME `Progress.CurrentStreak` — a word recognized 3x correctly across any sessions should always start in Text mode. **Architectural change required.** |
| D4 | **Demotion Severity (R2)** | **HIGH** | Wrong text answer fully resets to MC requiring 3 more correct. Captain expects MC check that returns to Text ONLY on correct MC answer. Wrong MC answer = stay in MC. **Incorrect responses never result in promotion.** |
| D5 | **Rotation Timing** | **MEDIUM** | Mastered words rotate out at round boundaries. Captain expects immediate removal mid-round on the very next turn. |
| D6 | DueOnly Re-filter | ~~MEDIUM~~ **RESOLVED (R5)** | Resolved: DueOnly filter applies ONCE at session start. Not re-applied between rounds. Rotation is controlled by mastery/tiered logic only. |
| D7 | **High-Mastery Rotation (RESOLVED R3)** | ~~MEDIUM~~ **RESOLVED** | Resolved by tiered rotation model (section 1.2.2). High-mastery words now need only 1 correct answer to rotate out. |
| D8 | Sentence Wrong Recording | **LOW** | Failed sentence shortcut sentences are not recorded as wrong attempts. Inflates accuracy stats. |
| D9 | Override Progress Update | **LOW** | After "I was correct" override, `currentItem.Progress` is not updated with the persisted result. Panel still shows stale data. |
| D10 | Session Summary Scope | **LOW** | `WasCorrectThisSession` reflects entire session, not per-round. A word wrong in Round 1 but correct in Round 2 shows as correct in both summaries. |
| D11 | **Mastery Scoring (R3)** | **HIGH** | Double-whammy bug: correct answer after wrong makes mastery WORSE (0.60 → 0.14). Flat penalty + full streak reset + replacement formula compound destructively. **3-part fix approved (section 5.6).** |
| D12 | **Rotation Counters (R3)** | **MEDIUM** | Code uses `QuizRecognitionStreak`/`QuizProductionStreak` (session-local consecutive streaks). Spec now requires `SessionCorrectCount`/`SessionMCCorrect`/`SessionTextCorrect` (cumulative counters) for tiered rotation. |

---

## 7. Scoring Reference

### MasteryScore Calculation (REVISED R5 — DifficultyWeight + Recovery-Aware)

**On correct answer:**
```
StreakIncrement = DifficultyWeight    (MC=1.0, Text=1.5, Sentence=2.5)
CurrentStreak += StreakIncrement      (NOTE: CurrentStreak is float, not int — R5 design choice)
ProductionInStreak++ (if Text/Voice/Sentence input)
EffectiveStreak = CurrentStreak + (ProductionInStreak * 0.5)
streakScore = min(EffectiveStreak / 7.0, 1.0)

// Recovery-aware mastery (R5):
recoveryBoost = (MasteryScore > streakScore) ? 0.02 : 0.0
MasteryScore = max(streakScore, MasteryScore) + recoveryBoost
MasteryScore = min(MasteryScore, 1.0)
```

> **Design note (R5):** `CurrentStreak` changes from `int` to `float` to support fractional DifficultyWeight increments. This is the cleaner approach vs applying the weight only at EffectiveStreak calculation time, because it keeps the streak value itself meaningful — a streak of 4.5 after 3 text entries is more expressive than a streak of 3 with an external multiplier. Downstream consumers (mode selection thresholds, tier boundaries) should use `>=` comparisons which work identically with float values.

**Effective mastery per correct answer by mode:**

| Mode | DifficultyWeight | Streak + | ProdInStreak + | Effective + |
|------|---------|----------|--------|-------------|
| MC | 1.0 | 1.0 | 0 | 1.0 |
| Text | 1.5 | 1.5 | 1 (+0.5) | 2.0 |
| Sentence | 2.5 | 2.5 | 1 (+0.5) | 3.0 |

**On wrong answer (scaled by track record):**
```
penaltyFactor = max(0.6, 1.0 - (0.4 / (1 + log(1 + CorrectAttempts))))
preserveFraction = min(0.5, log(1 + CorrectAttempts) / 8.0)

CurrentStreak = CurrentStreak * preserveFraction    (float result preserved)
ProductionInStreak = int(ProductionInStreak * preserveFraction)
MasteryScore *= penaltyFactor
```

### IsKnown Thresholds
```
Primary:   MasteryScore >= 0.85 AND ProductionInStreak >= 2
Bypass:    MasteryScore >= 0.75 AND ProductionInStreak >= 4 AND TotalAttempts >= 8

Re-qualification (R5):
  IF MasteredAt != null AND word lost IsKnown this session AND re-qualifies
  THEN ReviewInterval = 14 days (not 60)
```

### Mode Selection — Unified Algorithm (APPROVED R3)
```
Priority 1: IF PendingRecognitionCheck == true → MC (overrides everything)
Priority 2: IF Progress.CurrentStreak >= 3 (LIFETIME) OR MasteryScore >= 0.50 → Text
Priority 3: ELSE → MC

Gentle demotion:  Wrong text answer → PendingRecognitionCheck = true
                  → MC until correct MC answer → then back to Text
                  (flag clears ONLY on correct MC, NOT on wrong MC)
                  (see section 1.2 for full algorithm)
```

### Tiered Rotation (REVISED R5)
```
Tier 1 (MasteryScore >= 0.80 OR CurrentStreak >= 8):
    ReadyToRotateOut = SessionTextCorrect >= 1 AND PendingRecognitionCheck == false

Tier 2 (MasteryScore >= 0.50 OR CurrentStreak >= 3):
    ReadyToRotateOut = SessionCorrectCount >= 2 AND SessionTextCorrect >= 1

Tier 3 (below both):
    ReadyToRotateOut = SessionMCCorrect >= 3 AND SessionTextCorrect >= 3

DueOnly bonus: OR IsKnown
Rotation timing: IMMEDIATE (mid-round), not deferred to round boundary
No-repeat rule: A word is NEVER presented twice in a round.
```

### Difficulty Weights (REVISED R5 — Functional, Not Decorative)
```
Multiple Choice:     1.0f (default — streak += 1.0)
Text Entry:          1.5f (active recall — streak += 1.5)
Sentence Production: 2.5f (full productive use — streak += 2.5)
Range: 0.0–3.0
```

### DueOnly Filter (R5)
```
Applied ONCE at session start (section 1.1).
NOT re-applied between rounds.
Rotation controlled by mastery/tiered logic only.
```
