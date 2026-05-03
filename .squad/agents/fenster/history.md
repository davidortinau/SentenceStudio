# Fenster's Project Knowledge

## Learnings

### 2026-05-03: Issue #200 - Cloze ResourceId Decoupling

**Files touched:**
- `src/SentenceStudio.Shared/Services/PlanGeneration/DeterministicPlanBuilder.cs` (Layer 1)
- `src/SentenceStudio.Shared/Services/PlanGeneration/PlanConverter.cs` (Layer 2)
- `src/SentenceStudio.UI/Pages/Index.razor` (Layer 3)
- `src/SentenceStudio.UI/Pages/Cloze.razor` (Layer 4a)
- `src/SentenceStudio.AppLib/Services/ClozureService.cs` (Layer 4b)

**What was tricky:**
- ClozureService refactoring required extracting common AI generation logic into `GenerateSentencesFromWords()` helper to avoid code duplication between resource-driven and vocabulary-driven paths
- Had to mirror VocabQuiz's SRS filtering logic exactly (grace period exclusion, NextReviewDate checks, unseen word handling)
- The 40-word cap for AI prompts is critical for both paths — dynamic resources can return thousands of eligible words

**What surprised me:**
- Persisted plan items bypass PlanConverter entirely — Index.razor reads `item.ResourceId` directly, which is why Layer 3 guard is mandatory even after Layer 1 fix ships
- Layer 4 defense-in-depth (DueOnly check in Cloze.razor) works correctly even when URL has resourceId leaked from persisted plan
- The ClozureService method signature needed a default parameter (`bool dueOnly = false`) to avoid breaking existing callers

**Key pattern:**
When adding vocabulary-driven mode to an activity:
1. Layer 1 prevents NEW plans from carrying ResourceId
2. Layer 2 sets DueOnly route param for plan-initiated launches
3. Layer 3 guards against ResourceId leak from OLD persisted plans
4. Layer 4 implements dual-mode loading (DueOnly=true → global vocab, DueOnly=false → resource-filtered)

**Reusable for:**
Any activity that should load from full user vocab pool when launched from Today's Plan but remain resource-filtered when launched directly from a resource detail page.
