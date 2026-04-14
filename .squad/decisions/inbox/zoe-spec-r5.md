### R5 Spec Revision — Captain's 6 Design Decisions Integrated

**Date:** 2025-07-25  
**Author:** Zoe (Lead)  
**Spec:** `docs/specs/quiz-learning-journey.md`  
**Source:** Captain's answers to Jayne's skeptic review (Q1–Q6)

---

#### Decision 1: DifficultyWeight accelerates mastery (no longer decorative)

**Change:** Streak increment is now multiplied by DifficultyWeight. MC adds 1.0, Text adds 1.5, Sentence adds 2.5 to CurrentStreak.

**Implementation note:** `CurrentStreak` changes from `int` to `float` to support fractional increments. This is cleaner than applying weights only at EffectiveStreak calculation time because it makes the streak value itself expressive.

**Sections updated:** 2.1, 2.3, 2.5, 5.6, 7. DifficultyWeight comment range updated from 0.0–2.0 to 0.0–3.0 in `VocabularyAttempt.cs`. All "decorative"/"log-only" references removed.

#### Decision 2: Tier 1 rotation requires text + cleared recognition

**Change:** Tier 1 (high mastery >= 0.80) now requires `SessionTextCorrect >= 1 AND PendingRecognitionCheck == false` instead of just `SessionCorrectCount >= 1`. A mastered word returning after months that gets a wrong answer must re-demonstrate both recognition (correct MC) AND production (correct text) before rotating out.

**Sections updated:** 1.2.2 (tier table), 1.3 (rotation code), 7 (tiered rotation reference).

#### Decision 3: No repeat within a round (existing rule, now explicit)

**Change:** Added bold rule in section 1.3: "A word is NEVER presented twice in a round." Clarified that rounds naturally shrink as words rotate out with no minimum round size. Degenerate round concern (from Jayne's review) is a non-issue.

**Sections updated:** 1.3, 7.

#### Decision 4: Recovery-aware mastery formula (no plateau)

**Change:** Replaced simple `Math.Max(streakScore, MasteryScore)` with a recovery-aware formula that adds `+0.02` per correct answer during recovery (when streak hasn't caught up to mastery). This eliminates the flat period where correct answers show no visible mastery progress.

**Key formula:**
```
recoveryBoost = (MasteryScore > streakScore) ? 0.02 : 0.0
MasteryScore = max(streakScore, MasteryScore) + recoveryBoost
```

Added recovery scenario table showing the improvement vs R3.

**Sections updated:** 5.6 Component 3, 5.6 Constants, 5.6 combined scenario, 7.

#### Decision 5: DueOnly filter applies at session start ONLY

**Change:** Removed "re-apply DueOnly filter between rounds" from section 1.4. Replaced with explicit rule: DueOnly applies once at initial word selection. Words that become not-due mid-session remain in the batch pool. Rotation is controlled exclusively by mastery/tiered logic.

**Sections updated:** 1.1 (discrepancy table — marked RESOLVED), 1.4 (removed expected step, added DueOnly note), 6 (D6 resolved), 7 (DueOnly reference added).

#### Decision 6: IsKnown re-qualification gets 14-day review interval

**Change:** When a word loses IsKnown status (wrong answer) and re-qualifies, ReviewInterval = 14 days (not 60). Added `LostKnownThisSession` flag to session counter model for detection. Added section 4.3.1 with full re-qualification logic.

**Sections updated:** 1.2.3 (session counter model), 2.3 (mastery check note), 4.2 (SRS table), 4.3 (new 4.3.1 subsection), 5.6 Constants, 7.

---

**Impact on implementation:** These are all spec-only changes. The code changes needed are tracked via the discrepancy table in section 6. Key new work items:
- Change `CurrentStreak` from `int` to `float` (or add `EffectiveStreakAccumulator` field)
- Implement recovery boost in mastery calculation
- Update tier 1 rotation logic
- Add `LostKnownThisSession` tracking
- Add 14-day re-qualification path in `RecordAttemptAsync`
