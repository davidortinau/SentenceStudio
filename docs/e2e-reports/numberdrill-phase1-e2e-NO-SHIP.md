# NumberDrill Phase 1 E2E Test - SHIP BLOCKER FOUND

## Test Status: **NO-SHIP**

Date: 2026-05-04  
Tester: Jayne  
Branch: `squad/numbers-activity-phase-1` @ commit `bfe3174`  
Backend Tests: 52/52 ✅ (Wash confirmed)  

---

## Summary

NumberDrill activity page `/numberdrill` **crashes on load** with 500 Internal Server Error. Root cause: **Code bug** — `NumberDrill.razor` line 15 passes `SubTitle` parameter to `PageHeader` component, but `PageHeader.razor` does not have a `SubTitle` parameter (only `Title`, `ToolbarActions`, `PrimaryActions`, `SecondaryActions`, `ShowBack`, `OnBack`, `ShowHamburger`).

---

## Critical Bug: Invalid Parameter in Component

### Evidence

1. **Page crash on navigation to `/numberdrill` (after Aspire launched with Postgres):**
   ```
   InvalidOperationException: Object of type 'SentenceStudio.WebUI.Shared.PageHeader' does not have a property matching the name 'SubTitle'.
   ```
   Full stack: `ComponentProperties.ThrowForUnknownIncomingParameterName` → Blazor circuit crash → HTTP 500

2. **Code verification:**
   
   **NumberDrill.razor line 15:**
   ```razor
   <PageHeader Title="Number Drill" SubTitle="@subtitle" ShowBack="true" OnBack="GoBack" />
   ```
   
   **PageHeader.razor @code block (lines 60-68):**
   ```csharp
   [Parameter] public string Title { get; set; } = "";
   [Parameter] public RenderFragment? ToolbarActions { get; set; }
   [Parameter] public RenderFragment? PrimaryActions { get; set; }
   [Parameter] public RenderFragment? SecondaryActions { get; set; }
   [Parameter] public bool ShowBack { get; set; }
   [Parameter] public EventCallback OnBack { get; set; }
   [Parameter] public bool ShowHamburger { get; set; } = true;
   // NO SubTitle parameter!
   ```

3. **First E2E attempt (commit fea46c5) incorrectly diagnosed as migration bug:**
   - I checked the WRONG database (SQLite at `~/Library/Application Support/sentencestudio/server/sentencestudio.db`)
   - Captain corrected: Aspire uses **PostgreSQL** via `AddNpgsqlDbContext`
   - After launching Aspire properly with Postgres, page STILL crashed — different error (this one)

### Impact

- ❌ NumberDrill page completely non-functional (HTTP 500)
- ❌ All E2E gates blocked — can't test setup, session, grading, or dashboard updates
- ✅ Dashboard Numbers Insights tile renders empty state correctly (verified before crash)

---

## Verification Gates (Attempted)

### ✅ Passed
- [ ] **Dashboard empty state renders + correct copy** → ✅ PASS  
  Screenshot: `numberdrill-dashboard-empty.png`  
  Tile shows: "Start mastering Korean numbers / Your first session unlocks Time, Age, and Counting practice."

### ❌ Blocked by Migration Bug
All remaining gates blocked:
- [ ] Setup screen 3 contexts + Bootstrap icons
- [ ] System color chips visible
- [ ] Counting Read-and-Produce session completes
- [ ] Correct answer triggers positive feedback
- [ ] Incorrect answer triggers grader tip
- [ ] Time session enforces MM ∈ {00,15,30,45}
- [ ] Summary screen renders score
- [ ] Streak NOT broken by incorrect answers
- [ ] Dashboard tile populates with progress

**Cannot proceed without database schema.**

---

## Required Fix

**Option 1: Remove `SubTitle` from NumberDrill.razor (simplest)**
   ```diff
   - <PageHeader Title="Number Drill" SubTitle="@subtitle" ShowBack="true" OnBack="GoBack" />
   + <PageHeader Title="Number Drill" ShowBack="true" OnBack="GoBack" />
   ```
   Rationale: No other page in the codebase uses PageHeader with a subtitle. The `subtitle` variable in NumberDrill is context-dependent ("Counting", "Time", "Age") and could be shown differently (e.g., in a breadcrumb or below the title).

**Option 2: Add `SubTitle` parameter to PageHeader (if subtitle is genuinely needed)**
   ```diff
   + [Parameter] public string? SubTitle { get; set; }
   ```
   And render it in the template (e.g., below the title). But this requires design review — is a subtitle part of the PageHeader pattern?

**Recommended**: Option 1 (remove). PageHeader is a layout component for nav/title/actions. Context-specific labels like "Counting" can be displayed inside the page body, not the header.

---

## Deliverables

- Screenshot: `numberdrill-dashboard-empty.png` ✅ (committed)
- Bug report: This file
- Verdict: **NO-SHIP** — Migration bug blocks all E2E verification

---

## Next Steps

1. **Fix the code bug**: Remove `SubTitle="@subtitle"` from NumberDrill.razor line 15 (or add SubTitle parameter to PageHeader if needed)
2. **After fix**: Jayne re-runs full E2E test suite on this branch
3. **Only proceed to Wave 5 (Scribe)** after E2E is GREEN with all 10 gates verified

---

## Appendix: Correct Database Check (for future E2E runs)

**Aspire uses PostgreSQL, NOT SQLite!**

To check migrations in Aspire environment:
1. Open Aspire dashboard: `https://localhost:17017/login?t=<token>`
2. Go to Resources → `sentencestudio` (Postgres container)
3. Copy connection string from environment variables
4. Connect with `psql` or pgAdmin:
   ```bash
   psql "<connection-string>"
   \dt  -- list tables
   SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId" DESC LIMIT 5;
   SELECT COUNT(*) FROM "NumberContext";
   SELECT COUNT(*) FROM "NumberCounter";
   ```

The SQLite file at `~/Library/Application Support/sentencestudio/server/sentencestudio.db` is from **non-Aspire runs** and is irrelevant when testing via Aspire.

---

## Test Environment

- macOS Darwin  
- .NET 10.0 SDK  
- Aspire CLI `13.3.0-preview.1.26203.28`  
- Database: SQLite 3.x  
- Browser: Playwright (Chromium)  

---

## Logs & Artifacts

- Dashboard screenshot: `numberdrill-dashboard-empty.png`
- Page crash console: `.playwright-mcp/console-2026-05-04T23-42-21-604Z.log` lines 3-4
- Aspire logs: `~/.aspire/logs/cli_20260504T233842_a12c9423.log`

---

**End of Report**
