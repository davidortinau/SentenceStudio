# NumberDrill Manual Validation Checklist
**Date:** 2026-05-10
**Commit:** e8d0fbfe
**Platform:** Mac Catalyst Debug
**Tester:** Jayne

## NEGATIVE PICKER TESTS (Critical - Kaylee's fix)

### Test 1: Counting Context
- [ ] Select "Counting" context
- [ ] Screenshot: `jayne-negative-counting.png`
- [ ] Expected: 4 modes visible (Listen&Type, Read&Produce, TapTheCounter, Disambiguate)
- [ ] Expected: ListenAndPlace NOT visible
- [ ] Result: 

### Test 2: Time Context  
- [ ] Select "Time" context
- [ ] Screenshot: `jayne-negative-time.png`
- [ ] Expected: 4 modes visible (Listen&Type, Read&Produce, ListenAndPlace, Disambiguate)
- [ ] Expected: TapTheCounter NOT visible
- [ ] Result:

### Test 3: Any Context
- [ ] Select "Any" context
- [ ] Screenshot: `jayne-negative-any.png`
- [ ] Expected: 3 modes visible (Listen&Type, Read&Produce, Disambiguate)
- [ ] Expected: TapTheCounter AND ListenAndPlace both NOT visible
- [ ] Result:

### Test 4-7: Other Contexts (Age, Money, Date, Ordinal)
- [ ] Each should show 3 modes only
- [ ] Screenshot: `jayne-negative-others.png`
- [ ] Result:

## POSITIVE SMOKE TESTS (Verify working combos)

### Counting + ListenAndType (Audio + Placeholder Check)
- [ ] Start session
- [ ] Screenshot: `jayne-counting-listen-item.png`
- [ ] Verify NO "(TTS placeholder: ...)" text anywhere
- [ ] Tap play button
- [ ] Verify audio plays (check logs if needed)
- [ ] Result:

### Counting + TapTheCounter
- [ ] Start session
- [ ] Screenshot: `jayne-counting-tap-item.png`
- [ ] Complete 1 correct + 1 incorrect turn
- [ ] Result:

### Time + ListenAndPlace
- [ ] Start session
- [ ] Screenshot: `jayne-time-place-item.png`
- [ ] Complete 1 turn
- [ ] Result:

## FINAL VERDICT
- [ ] All negative tests PASS → APPROVE FOR DX24
- [ ] Any negative test FAIL → BLOCK
