# Squad History — Publish Log

## 398a7690 Review Remediation — Orchestration Complete

**Date:** 2026-05-08  
**Review Agent:** Wash  
**Remediation Squad:** Zoe, Kaylee (API + Flutter), Jayne  
**Status:** ✅ REMEDIATION COMPLETE (3 branches, not pushed; awaiting Captain merge decision)

### Branches Completed
1. **Zoe — `squad/wash-398a7690-fixes-maintenance`** (commit `35133e36`): Removed MigrateToStreakBasedScoringAsync (7 files, -240 lines). Pre-emptive deletion of one-shot historical migration endpoint per Captain directive.
2. **Kaylee — `squad/wash-398a7690-fixes-profile-speech`** (commits `4fe6e2ba` + `cefe6db6`): ProfileEndpoints + SpeechEndpoints full rewrite (IDOR fix, query scoping, validation, logging, CancellationToken, AuthClaimTypes constants). DTO renames (UserProfileDto→ProfileDto). Email validation tightened. Query counter portability fixed. 27/27 tests pass when stacked with Jayne.
3. **Kaylee — Flutter `fix/wash-398a7690-profile-api-contract`** (commit `f0e5e0a`): Fixed 3 pre-existing client breakages (wrong path, field names, required field crashes). New ProfileValidationException + RFC 7807 error handling.
4. **Jayne — `squad/wash-398a7690-tests-profile-speech`** (3 commits): 27 integration tests covering IDOR, 404/401, validation, BCP-47 language codes, pager regression. Initial run (unpatched 398a7690): 16/27 passed. After Kaylee's fixes: 27/27 passed.

### Key Decisions
- **AuthClaimTypes constants:** All JWT claim names live in `src/SentenceStudio.Api/AuthClaimTypes.cs` (merged to decisions.md)
- **Query scoping:** No more `ListAsync().FirstOrDefault()` in multi-user endpoints; use indexed GetByIdAsync(id, userId)
- **Email validation:** Strict RFC 5321 validator (EmailAddressAttribute was too lenient)

### Learnings Appended
- **Zoe:** Removal-vs-gate-feature decision criteria; git worktree pattern for multi-agent races
- **Kaylee:** ValidationProblem-details pattern; email-strictness rationale; query-counter portability concern
- **Jayne:** Pager hazard in shared test fixtures; commit-early discipline for cross-branch fixes

### Critical Follow-up: Verify Counter Portability
**Gap identified:** Jayne's UserProfilesQueryCounter bug fix lives on Kaylee's branch (cefe6db6). Jayne's standalone branch may not have it applied. **Action:** Rerun tests on `squad/wash-398a7690-tests-profile-speech` after Kaylee lands to confirm portability, or manually apply fix to Jayne's branch before merging.

### Orchestration Logs
- `.squad/orchestration-log/20260508T155724Z-zoe.md`
- `.squad/orchestration-log/20260508T155750Z-kaylee.md`
- `.squad/orchestration-log/20260508T155810Z-jayne.md`

### Session Log
- `.squad/log/2026-05-08-398a7690-remediation.md` (full arc: review → direction → fanout → feedback loop)

---

## Publish #8: NumberDrill Layout Parity (Footer Pin + Card Wrapper Removal)

**Date:** 2026-05-07  
**Ship Agent:** Wash (DevOps/Release Engineering)  
**Status:** ✅ SHIPPED

### Commits Shipped
- `577852ff` — fix(css): PageHeader `flex-shrink: 0` so footer pins to bottom edge
- `d09c233c` — fix(numberdrill): Remove card wrapper to match VocabQuiz flat layout
- `28aaca6e` — Kaylee bookkeeping (activity log + dashboard integration)
- `17209ec3` — Wash build fix (dangling `</div>` in NumberDrill.razor)

### Azure Deployment
- **Result:** ✅ SUCCESS (1m 57s)
- **API Revision:** api--0000094 (new)
- **WebApp Revision:** webapp--0000080 (new)
- **Post-Deploy Validation:** ✅ PASS (16/16 checks)

### iOS Deployment to DX24
- **Build:** ✅ SUCCESS (net10 SDK, ValidateXcodeVersion=false)
- **Install:** ✅ SUCCESS (2nd attempt after device wake)
- **Launch:** ✅ SUCCESS (bundle ID com.simplyprofound.sentencestudio)

### Friction Encountered
**HTML Build Issue (Unbalanced Tags):** Kaylee's card wrapper removal commit `d09c233c` removed opening `<div class="card card-ss p-4">` but left closing `</div>`. Razorblade caught unbalanced tags (33 close vs 32 open). Fixed in `17209ec3` before final deploy. **Lesson:** Post-structural removals in HTML, always verify balanced tag counts. Candidate for pre-commit Razor build check.

### Manual Validation (Captain)
- App running on DX24 post-launch
- Ready for NumberDrill footer pin + layout parity visual confirmation
