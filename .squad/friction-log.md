# Friction Log — Developer Experience Issues

## 2026-05-07: Unbalanced HTML Tags in Structural Removals (Publish #8)

**Date:** 2026-05-07  
**Severity:** Medium (caught at build, not production)  
**Component:** NumberDrill.razor  
**Root Cause:** Kaylee removed opening `<div class="card card-ss p-4">` but left closing `</div>` at line 416.

**What Happened:**
1. Commit `d09c233c` removed card wrapper to achieve flat layout parity with VocabQuiz
2. Kaylee deleted lines 116 (opening div) and 417 (content end), but line 416 (closing div) remained
3. Razorblade HTML parser caught unbalanced tags: 33 closing divs vs 32 opening divs
4. Build failed with RZ9981 + RZ1026 errors
5. Wash spotted issue, fixed in commit `17209ec3` by removing extra closing div

**Friction:**
- Manual tag-counting required during code review
- No pre-commit Razor build check caught this
- Forced hotfix + re-deploy cycle

**Candidate Fix:**
Add pre-commit Razor build check to `scripts/` folder that validates HTML tag balance in Blazor/Razor files before committing. Similar to ESLint + Prettier pattern for web.

**Prevention Pattern:**
When performing structural removals in Razor/Blazor (opening + closing tags), always:
1. Delete both opening AND closing in one edit block
2. Run `dotnet build` locally before committing
3. Verify div/form/section counts match: `grep -o '<div' file.razor | wc -l` == `grep -o '</div>' file.razor | wc -l`

**Recurring Risk:**
Kaylee + Wash both ship frequently; this pattern will recur without automation.

**Decision Trail:**
- Documented in Wash's decision file (wash-publish-8.md): Build issue section
- Friction log entry added to squad bookkeeping
- Candidate for `.squad/skills/razor-build-validation/` SKILL creation
