# Training Log: single-flight-async

## Session: 2026-05-03 — Initial Assessment + lockAcquired Guard

**Trainer:** Skill Trainer (post-ship review)
**Trigger:** Skill created during auth-persistence fix cycle. First independent validation.

### Assessment

Skill is well-structured: clear when-to-use / when-not-to-use, fast-path-outside-lock pattern,
real-world example linking to `IdentityAuthService.cs`. Confidence: **medium → high** after
this cycle (production ship + xUnit regression test green + Zoe code review).

Gap discovered during review: the canonical `try/finally { Release(); }` pattern is unsafe
if `WaitAsync` itself throws (cancellation, OOM, etc.). Zoe flagged this as High in code
review of the actual `IdentityAuthService.GetAccessTokenAsync` implementation.

### Changes Made

Added a new top entry under "Common Mistakes" covering the `lockAcquired` guard pattern:
- BAD example showing `Release()` after a thrown `WaitAsync`
- GOOD example with `bool lockAcquired = false` + `if (lockAcquired) _lock.Release()`
- Note that this exact pattern is now applied across `IdentityAuthService`

### Evidence

- Zoe code review High finding (session 8c66d948, checkpoint 054)
- Actual fix landed in `src/SentenceStudio.AppLib/Services/IdentityAuthService.cs` lines
  ~290-330 (GetAccessTokenAsync), ~100-140 (silent restore), ~340-381 (RefreshTokenAsync)
- Commit `0014a84` on origin/main

### Suggested Eval Scenarios

1. **"Implement single-flight refresh in service X"** — give a model the skill + a stub
   service. Check whether the produced code includes the `lockAcquired` guard. Score 5/5
   if guard is present and `_inflightOperation = null` is in finally; 3/5 if guard absent.
2. **"Review this single-flight code for bugs"** — give a model the skill + intentionally
   buggy code (Release without acquired-guard, missing inflight-null, cache check outside
   lock). Score on number of correctly identified issues / 3.

### Verdict

**MINOR-UPDATE applied.** Skill is now production-validated and reflects the lockAcquired
guard. Confidence: **high**.
