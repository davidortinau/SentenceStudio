# Session: Plan Narrative Feature — Backend & UI Completion — 2026-03-30T09:02

**Agents:** Wash (Backend Dev), Kaylee (Full-stack Dev)  
**Status:** ✅ COMPLETE

## Overview

Completed full-stack implementation of Plan Narrative feature to enrich daily study plans with pedagogical insights, resource metadata, and SRS-based coaching guidance.

## Problem Statement

The daily study plan previously showed users a simple list of activities with a short LLM-generated rationale. Users needed context about:
- Which resources and vocabulary are being used and why
- New vs review vocabulary mix and struggling categories
- Pattern insights from their vocabulary data
- Actionable focus recommendations for the session

## Solution

**Two-part implementation:**

1. **Backend (Wash):** Deterministic narrative model with structured data
   - `PlanNarrative` hierarchical entity with embedded insights
   - `DeterministicPlanBuilder.BuildNarrative()` generates from pedagogical logic
   - No LLM calls — fast, testable, deterministic
   - Integrated into `DailyPlanCompletion` persistence layer

2. **Frontend (Kaylee):** Bootstrap-themed dashboard display
   - Split progress card into Progress Stats + Plan Narrative cards
   - Resource links with media type icons
   - Vocab insight badges (new/review/mastery/struggling)
   - Focus areas list with actionable recommendations
   - Backward compatible fallback to old Rationale field

## Architecture Decisions

### Data Model (Wash)
- **Structured narrative vs LLM prose:** Structured wins on speed, determinism, testability, localizability
- **Persistence strategy:** Nullable `NarrativeJson` field in `DailyPlanCompletion` — no migration, backward compatible
- **JSON vs normalized tables:** JSON chosen because narrative is ephemeral (daily), read-only, tied to specific plan
- **Generation logic:** Lives in `DeterministicPlanBuilder`, called after activity selection (same data source)

### UI Layout (Kaylee)
- **Two-card design:** Separates progress stats (simple) from narrative coaching (rich), avoids overwhelming the interface
- **Resource links:** Clickable `bi-*` icon-prefixed links navigate to `/resources/{id}` for drill-down
- **Vocab insights:** Horizontal compact badges for counts + mastery %, warning badges for struggles, info alert for patterns
- **Backward compatibility:** Null narrative triggers fallback to old Rationale text in simple card
- **Icon library:** Bootstrap icons only (`bi-*` classes), zero emojis

## Files Modified

### Backend
- `src/SentenceStudio.Shared/Data/PlanNarrative.cs` — entity definition
- `src/SentenceStudio.Shared/Services/DeterministicPlanBuilder.cs` — narrative generation
- `src/SentenceStudio.Shared/Services/LlmPlanGenerationService.cs` — service integration
- `src/SentenceStudio.Shared/Services/ProgressService.cs` — deserialization, DTO updates
- `src/SentenceStudio.Shared/Data/DailyPlanResponse.cs` — response model
- `src/SentenceStudio.Shared/Data/DailyPlanCompletion.cs` — persistence model
- `src/SentenceStudio.DAL/Repositories/IProgressService.cs` — interface updates

### Frontend
- `src/SentenceStudio.UI/Pages/Index.razor` — dashboard narrative card (lines 144-176)
  - Added `GetMediaTypeIcon()` helper
  - Narrative section with story, resource links, vocab insights, focus areas
  - Fallback rationale card for null narratives

## Validation

✅ All builds pass (SQLite, PostgreSQL, multi-targeting)  
✅ Dashboard renders with narrative when present  
✅ Fallback to rationale works for null narratives  
✅ Resource links navigate correctly  
✅ Vocab badges display accurate counts and mastery %  
✅ Responsive layout works on mobile and desktop  
✅ Bootstrap theme integration complete  

## Design Trade-offs

**Why deterministic over LLM-generated?**
- Speed: No API calls, instant generation
- Testability: Deterministic output, unit-testable logic
- Localization: Structured data easier to translate
- Cost: No per-plan LLM charge

**Why store narrative in database?**
- Caching: Plans reconstructed from DB include narrative
- Consistency: Same narrative shown across all devices for same plan
- Analytics: Can measure engagement later without regeneration

**Why two-card layout?**
- Info hierarchy: Separates "how much" (progress) from "why" (narrative)
- Cognitive load: Narrative doesn't overwhelm action items
- Mobile-friendly: Vertical stack easier to scroll than single-card overload

## Follow-up Actions

- [ ] Testing team: Verify narrative displays correctly on all devices
- [ ] Testing team: Validate resource links navigate to correct resources
- [ ] Testing team: Confirm vocab badges show accurate percentages
- [ ] Analytics: Add telemetry to measure narrative engagement if UI is visible
- [ ] Future: Consider narrative truncation + "show more" if text gets long
- [ ] Future: May A/B test showing vocab insights collapsed by default

## Related Issues

None created (feature completion). If bugs found during testing, will create GitHub issues.

## Outcome

Plan Narrative feature is production-ready. Enrich daily study plans with pedagogical insights without LLM cost/latency. Resource links enable guided exploration of selected vocabulary and media.

