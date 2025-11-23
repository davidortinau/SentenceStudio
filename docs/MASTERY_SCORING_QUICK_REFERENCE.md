# Mastery Scoring Quick Reference

## What Changed

### Before (Broken)
```
날씨: 15/15 correct (100%) → 0.749 mastery (stuck, never "known")
Required: 3+ recognition AND 3+ production for 0.80+ mastery
Problem: Arbitrary thresholds ignored that production is harder than recognition
```

### After (Fixed)
```
날씨: 15/15 correct (100%) → 0.85+ mastery (marked as "known")
Required: 3+ recognition OR 2+ production for mastery
Logic: Production implies recognition (can't produce what you don't recognize)
```

---

## New Mastery Tiers

| Score Range | Status | Meaning | Example |
|-------------|--------|---------|---------|
| **0.85-1.0** | 🎯 **Known** (Productive) | Can recognize AND produce | Typed "날씨" correctly 2+ times |
| **0.70-0.85** | 👁️ **Known** (Receptive) | Can recognize reliably | Identified "날씨" correctly 3+ times |
| **0.50-0.70** | 📚 **Learning** | Building competency | Mixed success, needs more practice |
| **0.00-0.50** | 🆕 **New/Struggling** | Early attempts or difficult | Just started or having trouble |

---

## Evidence Thresholds

### Recognition (Easier Task)
- **Minimum:** 3 correct attempts needed
- **Why:** Recognition is passive, needs more evidence to confirm stable knowledge
- **Result:** Can reach 0.70-0.85 mastery with recognition alone

### Production (Harder Task)
- **Minimum:** 2 correct attempts needed  
- **Why:** Production is active recall + spelling, stronger signal per attempt
- **Result:** Automatically implies recognition, reaches 0.85+ mastery

---

## Expected Impact

### Words That Will Jump to "Known" (0.85+)

Run this query to see which words will benefit:

```sql
SELECT 
    TargetLanguageTerm,
    NativeLanguageTerm,
    TotalAttempts,
    CorrectAttempts,
    RecognitionCorrect,
    ProductionCorrect,
    MasteryScore as OldScore
FROM VocabularyWord vw
JOIN VocabularyProgress vp ON vw.Id = vp.VocabularyWordId
WHERE vp.MasteryScore < 0.80 
  AND vp.TotalAttempts >= 5
  AND CAST(vp.CorrectAttempts AS FLOAT) / vp.TotalAttempts >= 0.90
ORDER BY vp.TotalAttempts DESC;
```

**Estimated:** 50-100 words currently stuck at 0.74-0.79 will jump to 0.85+ on next practice.

---

## Testing Checklist

After launching the app with the fix:

1. ✅ **Do a vocabulary quiz** that includes 날씨 or similar "stuck" words
2. ✅ **Check mastery score** increases to 0.85+ after getting it correct
3. ✅ **Verify dashboard** shows more words in "Known" category
4. ✅ **Monitor debug output** for scoring calculations:
   ```
   🏴‍☠️ Word 230: Base=0.85, Rolling=0.90, Penalized=0.88, Final=0.88
   ```

---

## Files Modified

- ✅ `src/SentenceStudio/Services/VocabularyProgressService.cs`

**Changes:**
1. Updated constants (MIN_CORRECT_RECOGNITION=3, MIN_CORRECT_PRODUCTION=2)
2. Rewrote `CalculatePhaseSpecificScore()` with SLA-aligned logic
3. Added `CalculateRecognitionScore()` and `CalculateProductionScore()` helpers
4. Removed broken `ApplyMixedModeRequirement()` method
5. Updated `UpdateLearningPhaseRigorous()` to use new thresholds

---

## Learning Science Rationale

**Key Principle:** *Production implies recognition, but recognition doesn't imply production.*

**Why This Matters:**
- If you can TYPE "날씨" correctly → you obviously RECOGNIZE it
- But if you can RECOGNIZE "날씨" → you might not recall how to spell it
- Therefore: 2 production attempts = stronger evidence than 3 recognition attempts

**Aligns with:**
- Laufer & Nation (1999) on receptive vs. productive vocabulary
- Nation (2001) on depth of word knowledge
- CEFR can-do descriptors (reception precedes production)

---

## Troubleshooting

### "Why is word X still at 0.70 mastery?"

**Check:**
- Does it have 3+ recognition attempts? ✅ → Receptive mastery (0.70-0.85) is correct
- Does it have 2+ production attempts? ❌ → Needs production practice to reach 0.85+

**This is working as designed:** The word is "known receptively" but not yet "mastered productively."

### "Why did word Y jump from 0.74 to 0.92?"

**Explanation:**
- Old system: Capped at 0.75 due to missing production threshold
- New system: 2 production correct + high recognition score → 0.85-1.0 range
- The jump reflects the word's TRUE mastery level finally being recognized

---

## Next Steps

### Immediate (Done ✅)
- Fixed scoring algorithm
- Removed arbitrary caps
- Applied SLA-aligned thresholds

### Short-Term (Optional)
- Add UI badges to distinguish receptive vs. productive mastery
- Show "Production practice recommended" for 0.70-0.85 words
- Dashboard breakdown: "450 known (350 receptive, 100 productive)"

### Long-Term (Future Enhancement)
- Placement test to establish baseline (see `docs/PLACEMENT_TEST_DESIGN.md`)
- Response time tracking for additional mastery signal
- Forgetting curves to model retention over time

---

**Captain, the scoring is now shipshape and aligned with learning science! ⚓**

Run the app and watch those "stuck" words finally get the credit they deserve! 🎯
