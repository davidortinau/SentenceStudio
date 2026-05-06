# Keaton's History

## Learnings

### Issue #200 - Cloze Activity ResourceId Decoupling (2026-05-03)

**Root Cause:** Cloze activity is missing the 4-layer ResourceId Decoupling Pattern that was applied to VocabQuiz (commits 88a0272, c081a63) and VocabMatching (commit 0c8e197).

**Investigation Files:**
- `src/SentenceStudio.Shared/Services/PlanGeneration/DeterministicPlanBuilder.cs:690,755` — Cloze is categorized as an output activity but NOT marked as vocabulary-driven
- `src/SentenceStudio.Shared/Services/PlanGeneration/DeterministicPlanBuilder.cs:502-510` — Cloze activities ARE created with `ResourceId = resource.Id` (resource-driven)
- `src/SentenceStudio.Shared/Services/PlanGeneration/PlanConverter.cs:81-89` — Cloze enum case exists, but NO special handling (unlike VocabReview/VocabGame)
- `src/SentenceStudio.Shared/Services/PlanGeneration/PlanConverter.cs:121-159` — `BuildRouteParameters` has NO Cloze branch; falls through to resource-based default
- `src/SentenceStudio.UI/Pages/Index.razor:974-1012` — `LaunchPlanItem` guard at lines 984-986 ONLY excludes VocabReview + VocabGame; Cloze leaks ResourceId
- `src/SentenceStudio.UI/Pages/Cloze.razor:146-227` — NO `DueOnly` parameter; `LoadSentences` calls `ClozureSvc.GetSentences(resourceId, 8, skillId)` directly
- `src/SentenceStudio.AppLib/Services/ClozureService.cs:43-69` — Hard requires non-empty resourceID + skillID; returns empty list otherwise (line 50-59)

**Pattern Comparison:**

| Layer | VocabQuiz (✅ Fixed) | VocabMatching (✅ Fixed) | Cloze (❌ Missing) |
|-------|---------------------|-------------------------|-------------------|
| **1. Plan Builder** | ResourceId = null (line 460) | ResourceId = null (line 520) | ResourceId = resource.Id (line 505) |
| **2. PlanConverter** | DueOnly = true (line 128) | DueOnly = true (line 134) | No special handling; uses default resource-based params |
| **3. Index.razor guard** | Excluded at line 984 | Excluded at line 985 | NOT excluded; ResourceId leaks to URL |
| **4. Page defense** | DueOnly check line 643 | DueOnly check at VocabMatching.razor | NO DueOnly param; no fallback to full vocab pool |

**Mechanism:**
1. Plan builder stamps `ResourceId = resource.Id` for Cloze (line 505)
2. PlanConverter has no Cloze branch, so `BuildRouteParameters` falls through to default (lines 141-148), passes ResourceId unchanged
3. Index.razor `LaunchPlanItem` has no guard for Cloze, so it appends `resourceId={item.ResourceId}` to URL
4. Cloze.razor reads `ResourceIdParam` and passes it to `ClozureSvc.GetSentences(resourceId, 8, skillId)` (line 203)
5. ClozureService HARD REQUIRES resourceId (line 50-54) and returns empty list if missing or if that resource has no vocab
6. If the resource from the plan has no remaining vocab (all mastered, grace period, or empty), Cloze shows "no sentences available" even when hundreds of due words exist globally

**Discrepancy vs. Hypothesis:** Cloze is NOT vocabulary-driven in the current implementation — it's strictly resource-driven. This is unlike VocabQuiz/VocabMatching which were ALWAYS vocabulary-driven but incorrectly filtered by resource. Cloze genuinely uses the resource's transcript/content to generate contextual sentences via AI. The bug is that TODAY'S PLAN shouldn't restrict Cloze to a single resource when the user has broader vocab needs.

**Files:** DeterministicPlanBuilder.cs, PlanConverter.cs, Index.razor, Cloze.razor, ClozureService.cs

---

## 2026-05-05: Dogfooding Directive Established

**Status:** DIRECTIVE (Cross-team impact)

Per Captain's directive captured in AGENTS.md under "Project Purpose: Dogfooding .NET MAUI", **tooling friction investigations are HIGHER priority than app features**. When .NET MAUI SDK/DX friction surfaces during normal work:

1. Root-cause systematically (layer elimination, reproduction steps)
2. File upstream with high-quality issue body
3. Document workarounds for team reuse
4. Continue app validation using workaround

This directive was established during NumberDrill Phase 1 work when DevFlow CLI bugs blocked automation. Investigation yielded dotnet/maui-labs#232 — a real upstream issue capturing tooling friction that benefits the .NET MAUI team.

**Impact on your work:** Prioritize tooling investigations over feature delays. Document findings as skills for team reuse.

---

