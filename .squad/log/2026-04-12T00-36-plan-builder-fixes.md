# Session: Plan Builder Bug Fixes — 2026-04-12T00:36

**Agent:** Wash (Backend Dev)

## Summary

Three confirmed bugs in DeterministicPlanBuilder fixed:
1. Replaced Guid-based tiebreaker with deterministic hash (Id.GetHashCode ^ today.GetHashCode)
2. Truncated DueWords list to match reviewCount cap
3. Expanded resource recency lookback from 14 to 30 days

**Result:** 43/43 plan generation tests passing; 262/262 full suite passing.

## Files Modified

- `src/SentenceStudio.Shared/Services/PlanGeneration/DeterministicPlanBuilder.cs`

## Test Status

- Plan generation: ✅ 43/43
- Full suite: ✅ 262/262

---

**Logged:** 2026-04-12T00:36Z
