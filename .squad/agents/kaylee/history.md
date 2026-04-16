# Kaylee's History

## Learnings

### Activity Log UI Implementation (2025-05-XX)

Built the complete Activity Log feature (Strava-inspired Practice Calendar) for SentenceStudio.UI:

**Navigation Setup:**
- Added "Activity" as the second nav item in NavMenu.razor (after Dashboard, before Resources)
- Registered `/activity-log` route in NavigationMemoryService.cs at index 1

**Components Created:**
- **ActivityLog.razor**: Main page with filtering (All/Input/Output), weekly cards, expandable day details, and "Load More" pagination (starts with 8 weeks, loads 4 more at a time)
- **ActivityDot.razor**: Visual indicator with size based on minutes (sm <10, md 10-25, lg 25+) and color based on activity type (blue=Input, orange=Output, gradient=Both, green border=Complete)
- **PlanSummaryCard.razor**: Expandable detail showing original plan items with completion status, minutes spent, and activity breakdown

**CSS Additions:**
- Added activity-dot styles to app.css: size variants (sm/md/lg), color classes (input/output/split), hover effects, and completion indicator

**Layout Patterns:**
- Used responsive Bootstrap utilities for mobile/desktop differences (single-char day names on mobile, full names on desktop)
- PageHeader component with ToolbarActions (refresh) and SecondaryActions (filter dropdown)
- Card-based layout following existing patterns (card-ss, ss-body1, ss-caption1 typography classes)
- Week header shows date range and total minutes; 7-day grid shows dots or "Rest"; expandable detail shows full plan breakdown

**Key Decisions:**
- No year sidebar (keeps mobile-first, week cards are self-explanatory with date ranges)
- Filter re-queries rather than client-side filtering (keeps service layer responsible for data)
- Toggle day detail on tap (simpler than modal, keeps context visible)
- Used hardcoded English strings with TODO comments for future localization (prioritized getting UI working)

### Activity Log UI Implementation (2026-04-16)

Built complete Activity Log feature UI for SentenceStudio.UI (Strava-inspired Practice Calendar):

**Components Created:**
- **ActivityLog.razor**: Main page with All/Input/Output filtering, weekly card layout, expandable day details, "Load More" pagination (8 weeks initial, 4 at a time)
- **ActivityDot.razor**: Visual indicator—size by minutes (sm <10, md 10-25, lg 25+), color by type (blue=Input, orange=Output, gradient=Both), green border for completion
- **PlanSummaryCard.razor**: Expandable detail showing all day activities with resource title, minutes, completion status

**Navigation & Routing:**
- Added "Activity" as second nav item in NavMenu.razor (after Dashboard)
- Registered `/activity-log` route in NavigationMemoryService at index 1

**Styling (app.css):**
- `.activity-dot` with size/color variants, hover effects, responsive spacing
- Completion indicator styling (green border)

**Layout Patterns:**
- Responsive typography: single-char day names (mobile), full names (desktop)
- PageHeader with ToolbarActions (refresh) + SecondaryActions (filter dropdown)
- Card-based layout consistent with existing SentenceStudio.UI patterns (card-ss, ss-body1, ss-caption1)
- Week header shows date range and total minutes; 7-day grid shows dots or "Rest"

**Key Decisions:**
- No year sidebar (mobile-first, week cards self-explanatory)
- Service-owned filtering (no client-side filtering)
- Toggle day detail on tap (simpler than modal)
- Size-based dots show time investment at a glance

**Integration with Wash's DTOs:**
- Consumes ActivityLogWeek, ActivityLogDay, ActivityLogEntry from ProgressService
- Uses ActivityCategory enum for color/sizing logic
- 4-week pagination batches per UI spec

**Build Fixes (Coordinator):**
- Fixed Razor switch expression HTML parsing bug (`< 10` → `&lt; 10`)
- Fixed duplicate key in ToDictionary for resource/skill grouping
